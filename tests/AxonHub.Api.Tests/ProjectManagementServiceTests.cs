using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.ProjectService's create/update/delete semantics and the
/// profile JSON codec against internal/server/biz/project.go and
/// internal/objects/project.go.
/// </summary>
public sealed class ProjectManagementServiceTests
{
    private readonly FakeProjectStore _projects = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();

    public ProjectManagementServiceTests()
    {
        _users.RolesSource = _roles;
    }

    private ProjectManagementService NewService()
        => NewService(new FakeManagementIdentity(new ManagementUser(9, IsOwner: true)));

    private ProjectManagementService NewService(ManagementUser user)
        => NewService(new FakeManagementIdentity(user));

    private ProjectManagementService NewService(IManagementIdentity identity)
        => new(
            _projects,
            _roles,
            identity,
            new PermissionValidator(identity, _users, _roles),
            new ProjectPrivacy(identity, _users, _projects),
            new RolePrivacy(identity, _users, _roles),
            new UserProjectPrivacy(identity, _users));

    [Fact]
    public async Task CreateWritesTheProjectRolesAndOwnerRowInReferenceOrder()
    {
        var service = NewService();

        var project = await service.CreateAsync(new CreateProjectCommand
        {
            Name = "Alpha",
            Description = "First",
        });

        Assert.True(project.Id > 0);
        Assert.Equal("Alpha", project.Name);
        Assert.Equal("First", project.Description);
        Assert.Equal("active", project.Status);
        Assert.Equal(
            """{"activeProfile":"","profiles":null}""",
            project.Profiles);

        Assert.Equal(["Admin", "Developer", "Viewer"], _roles.Rows.Select(static row => row.Name));
        Assert.All(
            _roles.Rows,
            row =>
            {
                Assert.Equal(RoleManagementService.LevelProject, row.Level);
                Assert.Equal(project.Id, row.ProjectId);
            });
        Assert.Equal(
            """["read_users","write_users","read_roles","write_roles","read_api_keys","write_api_keys","read_requests","write_requests","read_prompts","write_prompts"]""",
            _roles.Rows[0].Scopes);
        Assert.Equal(
            """["read_api_keys","write_api_keys","read_requests","write_requests"]""",
            _roles.Rows[1].Scopes);
        Assert.Equal(
            """["read_prompts","read_requests"]""",
            _roles.Rows[2].Scopes);

        var owner = Assert.Single(_projects.ProjectUsers);
        Assert.Equal(9, owner.UserId);
        Assert.Equal(project.Id, owner.ProjectId);
        Assert.True(owner.IsOwner);
        Assert.Equal("[]", owner.Scopes);
    }

    [Fact]
    public async Task CreateWithDuplicateNameReturnsTheCodedError()
    {
        _projects.Add(project => project.Name = "Alpha");
        var service = NewService();

        var error = await Assert.ThrowsAsync<CodedException>(
            () => service.CreateAsync(new CreateProjectCommand { Name = "Alpha" }));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("project name 'Alpha' already exists", error.Message);
        Assert.Equal("project", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("Alpha", error.Extensions["value"]);
        Assert.Single(_projects.Rows);
    }

    [Fact]
    public async Task CreateWithoutContextUserFailsBeforeTheStore()
    {
        var service = NewService(new FakeManagementIdentity(null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(new CreateProjectCommand { Name = "Alpha" }));

        Assert.Equal("user not found in context", error.Message);
        Assert.Empty(_projects.Rows);
    }

    [Fact]
    public async Task CreateRoleFailureKeepsTheReferencePrefixAndLeavesTheProjectBehind()
    {
        // CreateProject runs without a transaction in the reference: the
        // project row survives a failed preset role insert.
        _roles.CreateFailure = new StoreFailureException(
            "boom",
            new InvalidOperationException("boom"));
        var service = NewService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(new CreateProjectCommand { Name = "Alpha" }));

        Assert.Equal("failed to create admin role: boom", error.Message);
        Assert.Single(_projects.Rows);
        Assert.Empty(_projects.ProjectUsers);
    }

    [Fact]
    public async Task UpdateAppliesNameAndReplacesEdges()
    {
        var project = _projects.Add(row => row.Name = "Alpha");
        _projects.ProjectUsers.Add(NewProjectUser(project.Id, 2));
        var before = project.UpdatedAt;

        var service = NewService();
        var updated = await service.UpdateAsync(new UpdateProjectCommand
        {
            Id = project.Id,
            Name = "Beta",
            RemoveUserIds = [2],
            AddUserIds = [3],
        });

        Assert.Equal("Beta", updated.Name);
        Assert.Equal("Default project", updated.Description);
        Assert.Equal("active", updated.Status);
        Assert.True(updated.UpdatedAt > before);
        Assert.Equal([3], _projects.ProjectUsers.Select(static row => row.UserId));

        var added = Assert.Single(_projects.ProjectUsers);
        Assert.False(added.IsOwner);
        Assert.Equal("[]", added.Scopes);
    }

    [Fact]
    public async Task UpdateDuplicateUserAddFailsWithTheEdgePrefix()
    {
        var project = _projects.Add(row => row.Name = "Alpha");
        _projects.ProjectUsers.Add(NewProjectUser(project.Id, 3));

        var service = NewService();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(new UpdateProjectCommand
            {
                Id = project.Id,
                Name = "Beta",
                AddUserIds = [3],
            }));

        Assert.Equal(
            "failed to update project: add m2m edge for table user_projects: "
            + "duplicate key value violates unique constraint "
            + "\"user_projects_by_user_id_project_id\"",
            error.Message);
        // The whole update rolls back, name included.
        Assert.Equal("Alpha", project.Name);
    }

    [Fact]
    public async Task UpdateMissingProjectReturnsTheEntNotFoundError()
    {
        var service = NewService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(new UpdateProjectCommand { Id = 42, Name = "Beta" }));

        Assert.Equal("failed to update project: ent: project not found", error.Message);
    }

    [Fact]
    public async Task UpdateStatusPersistsAndMissingReturnsTheEntNotFoundError()
    {
        var project = _projects.Add();
        var service = NewService();

        var updated = await service.UpdateStatusAsync(project.Id, "archived");

        Assert.Equal("archived", updated.Status);
        Assert.True(updated.UpdatedAt > project.CreatedAt);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateStatusAsync(42, "archived"));
        Assert.Equal("failed to update project status: ent: project not found", error.Message);
    }

    [Fact]
    public async Task UpdateProfilesPersistsTheGoJsonShape()
    {
        var project = _projects.Add();
        var service = NewService();

        var updated = await service.UpdateProfilesAsync(new UpdateProjectProfilesCommand
        {
            Id = project.Id,
            Profiles = new ProjectProfilesModel
            {
                ActiveProfile = "a",
                Profiles =
                [
                    new ProjectProfileModel
                    {
                        Name = "a",
                        ChannelIDs = [1, 2],
                        ChannelTags = ["t1"],
                        ChannelTagsMatchMode = "any",
                    },
                    new ProjectProfileModel { Name = "b" },
                ],
            },
        });

        Assert.Equal(
            """{"activeProfile":"a","profiles":[{"name":"a","channelIDs":[1,2],"channelTags":["t1"],"channelTagsMatchMode":"any"},{"name":"b"}]}""",
            updated.Profiles);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateProfilesAsync(new UpdateProjectProfilesCommand
            {
                Id = 42,
                Profiles = new ProjectProfilesModel(),
            }));
        Assert.Equal(
            "failed to update project profiles: ent: project not found",
            error.Message);
    }

    [Fact]
    public async Task UpdateProfilesValidatesBeforeTouchingTheStore()
    {
        var project = _projects.Add(row => row.Profiles = "unchanged");
        var service = NewService();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateProfilesAsync(new UpdateProjectProfilesCommand
            {
                Id = project.Id,
                Profiles = new ProjectProfilesModel
                {
                    Profiles =
                    [
                        new ProjectProfileModel { Name = "a" },
                        new ProjectProfileModel { Name = "A" },
                    ],
                },
            }));

        // Validation errors are returned without the store prefix.
        Assert.Equal("duplicate profile name: A", error.Message);
        Assert.Equal("unchanged", project.Profiles);
    }

    [Fact]
    public async Task DeleteRequiresTheSystemOwner()
    {
        var project = _projects.Add();
        var service = NewService(new ManagementUser(9, IsOwner: false));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(project.Id));

        Assert.Equal(
            "permission denied: insufficient permissions: only system owners can delete projects",
            error.Message);
        Assert.NotNull(await _projects.GetByIdAsync(project.Id));
    }

    [Fact]
    public async Task DeleteWithoutContextUserIsWrappedInThePermissionPrefix()
    {
        var project = _projects.Add();
        var service = NewService(new FakeManagementIdentity(null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(project.Id));

        Assert.Equal("permission denied: user not found in context", error.Message);
    }

    [Fact]
    public async Task DeleteSoftDeletesTheProjectAndItsJoinRows()
    {
        var project = _projects.Add();
        _projects.ProjectUsers.Add(NewProjectUser(project.Id, 2));
        var service = NewService();

        await service.DeleteAsync(project.Id);

        Assert.Null(await _projects.GetByIdAsync(project.Id));
        Assert.Empty(_projects.ProjectUsers);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(project.Id));
        Assert.Equal("failed to get project: ent: project not found", error.Message);
    }

    [Theory]
    [InlineData(" ", "profile name cannot be empty")]
    [InlineData("a,a", "duplicate profile name: A")]
    [InlineData("bad", "profile 'a' channelTagsMatchMode is invalid")]
    public void ValidateRejectsInvalidProfiles(string scenario, string expected)
    {
        var profiles = scenario switch
        {
            " " => new ProjectProfilesModel
            {
                Profiles = [new ProjectProfileModel { Name = "  " }],
            },
            "a,a" => new ProjectProfilesModel
            {
                Profiles =
                [
                    new ProjectProfileModel { Name = "a" },
                    new ProjectProfileModel { Name = "A" },
                ],
            },
            _ => new ProjectProfilesModel
            {
                Profiles =
                [
                    new ProjectProfileModel
                    {
                        Name = "a",
                        ChannelTagsMatchMode = "sometimes",
                    },
                ],
            },
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => ProjectProfilesCodec.Validate(profiles));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void ValidateRequiresTheActiveProfileToMatchCaseSensitively()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ProjectProfilesCodec.Validate(new ProjectProfilesModel
            {
                ActiveProfile = "alpha",
                Profiles = [new ProjectProfileModel { Name = "Alpha" }],
            }));

        Assert.Equal(
            "active profile 'alpha' does not exist in the profiles list",
            error.Message);

        ProjectProfilesCodec.Validate(new ProjectProfilesModel
        {
            ActiveProfile = "Alpha",
            Profiles = [new ProjectProfileModel { Name = "Alpha", ChannelTagsMatchMode = "none" }],
        });
    }

    [Fact]
    public void SerializeMatchesGoEncodingJson()
    {
        Assert.Equal(
            """{"activeProfile":"","profiles":null}""",
            ProjectProfilesCodec.Serialize(new ProjectProfilesModel()));

        Assert.Equal(
            """{"activeProfile":"","profiles":[]}""",
            ProjectProfilesCodec.Serialize(new ProjectProfilesModel { Profiles = [] }));

        // omitempty drops nil AND empty slices and the empty match mode; the
        // string escapes are Go's encoding/json HTML escaping.
        var json = ProjectProfilesCodec.Serialize(new ProjectProfilesModel
        {
            ActiveProfile = "a",
            Profiles =
            [
                new ProjectProfileModel
                {
                    Name = "a<b",
                    ChannelIDs = [],
                    ChannelTags = ["<x>&"],
                    ChannelTagsMatchMode = "all",
                },
            ],
        });

        Assert.Equal(
            """{"activeProfile":"a","profiles":[{"name":"a<b","channelTags":["<x>&"],"channelTagsMatchMode":"all"}]}""",
            json.Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\u0026", "&"));
    }

    [Fact]
    public void DeserializeReadsTheStoredShape()
    {
        var model = ProjectProfilesCodec.Deserialize(
            """{"activeProfile":"a","profiles":[{"name":"x","channelIDs":[1,2],"channelTags":["t"],"channelTagsMatchMode":"all"},{"name":"y"}]}""");

        Assert.Equal("a", model.ActiveProfile);
        Assert.NotNull(model.Profiles);
        Assert.Equal(2, model.Profiles!.Count);
        Assert.Equal([1, 2], model.Profiles[0].ChannelIDs);
        Assert.Equal(["t"], model.Profiles[0].ChannelTags);
        Assert.Equal("all", model.Profiles[0].ChannelTagsMatchMode);
        Assert.Null(model.Profiles[1].ChannelIDs);
        Assert.Null(model.Profiles[1].ChannelTags);
        Assert.Equal(string.Empty, model.Profiles[1].ChannelTagsMatchMode);

        var empty = ProjectProfilesCodec.Deserialize(null);
        Assert.Equal(string.Empty, empty.ActiveProfile);
        Assert.Null(empty.Profiles);

        var missing = ProjectProfilesCodec.Deserialize("{}");
        Assert.Equal(string.Empty, missing.ActiveProfile);
        Assert.Null(missing.Profiles);
    }

    private static UserProjectRecord NewProjectUser(long projectId, long userId) => new()
    {
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        IsOwner = false,
        Scopes = "[]",
        UserId = userId,
        ProjectId = projectId,
    };
}
