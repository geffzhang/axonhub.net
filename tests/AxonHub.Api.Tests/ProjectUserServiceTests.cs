using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports of TestAddUserToProject_*, TestRemoveUserFromProject_* and
/// TestUpdateProjectUser_* (internal/server/biz/user_test.go:431-1576). The
/// three *_CacheInvalidation tests collapse into C173: the port's management
/// read path has no user cache to invalidate.
/// </summary>
public sealed class ProjectUserServiceTests
{
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();

    public ProjectUserServiceTests()
    {
        _users.RolesSource = _roles;
    }

    /// <summary>
    /// Service with a test principal (authz.WithTestBypass): AddUserToProject
    /// skips every permission check like the reference (user.go:396-397).
    /// </summary>
    private UserManagementService TestBypassService()
        => NewService(new ManagementUser(1, IsOwner: false, IsTest: true));

    /// <summary>
    /// Service with the owner principal the reference UpdateProjectUser tests
    /// install via contexts.WithUser(ctx, createOwnerUser(...)).
    /// </summary>
    private UserManagementService OwnerService()
        => NewService(new ManagementUser(1, IsOwner: true));

    private UserManagementService NewService(ManagementUser current)
    {
        var identity = new FakeManagementIdentity(current);
        return new(
            _users,
            new PasswordHasher(),
            new PermissionValidator(identity, _users, _roles),
            identity,
            _roles,
            new UserPrivacy(identity, _users),
            new UserProjectPrivacy(identity, _users),
            new RolePrivacy(identity, _users, _roles));
    }

    private async Task<UserRecord> CreateUserAsync(string email = "user@example.com")
    {
        var user = new UserRecord
        {
            Email = email,
            Status = "activated",
        };

        await _users.CreateAsync(user, null);
        return user;
    }

    private async Task<RoleRecord> CreateRoleAsync(
        string name,
        long projectId,
        string level,
        IReadOnlyList<string>? scopes = null)
    {
        var role = new RoleRecord
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Name = name,
            Level = level,
            ProjectId = projectId,
            Scopes = scopes is null ? null : JsonSerializer.Serialize(scopes),
        };

        await _roles.CreateAsync(role);
        return role;
    }

    [Fact]
    public async Task AddUserToProjectSuccess()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();

        var userProject = await service.AddUserToProjectAsync(new AddProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            IsOwner = false,
            Scopes = ["read_project", "write_project"],
        });

        Assert.Equal(user.Id, userProject.UserId);
        Assert.Equal(42, userProject.ProjectId);
        Assert.False(userProject.IsOwner);
        Assert.Equal(
            ["read_project", "write_project"],
            JsonSerializer.Deserialize<List<string>>(userProject.Scopes!));
    }

    [Fact]
    public async Task AddUserToProjectWithRoles()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();
        var role1 = await CreateRoleAsync("Project Admin", 42, RoleManagementService.LevelProject, ["manage_project"]);
        var role2 = await CreateRoleAsync("Project Member", 42, RoleManagementService.LevelProject, ["read_project"]);

        var userProject = await service.AddUserToProjectAsync(new AddProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            IsOwner = true,
            Scopes = ["custom_scope"],
            RoleIds = [role1.Id, role2.Id],
        });

        Assert.True(userProject.IsOwner);
        Assert.Equal(["custom_scope"], JsonSerializer.Deserialize<List<string>>(userProject.Scopes!));
        Assert.Equal([role1.Id, role2.Id], _roles.UserRoleIds(user.Id));
    }

    [Fact]
    public async Task AddUserToProjectWithNilOwner()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();

        // isOwner null uses ent's create default, false (user.go:426-428).
        var userProject = await service.AddUserToProjectAsync(new AddProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
        });

        Assert.False(userProject.IsOwner);
        Assert.Null(userProject.Scopes);
    }

    [Fact]
    public async Task AddUserToProjectDuplicateRelationship()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();
        var command = new AddProjectUserCommand { UserId = user.Id, ProjectId = 42 };

        await service.AddUserToProjectAsync(command);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddUserToProjectAsync(command));

        // The reference asserts only that the second add fails; the port locks
        // the SQLite constraint wording the reference test database produces
        // (C153).
        Assert.Contains("failed to add user to project:", error.Message);
    }

    [Fact]
    public async Task RemoveUserFromProjectSuccess()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42 });

        await service.RemoveUserFromProjectAsync(user.Id, 42);

        Assert.Empty(await _users.ListProjectUsersForUserAsync(user.Id));
    }

    [Fact]
    public async Task RemoveUserFromProjectNotFound()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();

        // Removing a relationship that doesn't exist succeeds (user.go:476-478).
        await service.RemoveUserFromProjectAsync(user.Id, 42);
    }

    [Fact]
    public async Task RemoveUserFromProjectWithRoles()
    {
        var service = TestBypassService();
        var user = await CreateUserAsync();
        var projectRole1 = await CreateRoleAsync("Project 1 Admin", 1, RoleManagementService.LevelProject, ["manage_project_1"]);
        var projectRole2 = await CreateRoleAsync("Project 2 Admin", 2, RoleManagementService.LevelProject, ["manage_project_2"]);
        var globalRole = await CreateRoleAsync("Global Viewer", 0, RoleManagementService.LevelSystem, ["view_all"]);

        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 1, RoleIds = [projectRole1.Id] });
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 2, RoleIds = [projectRole2.Id] });
        _roles.Assign(user.Id, globalRole.Id);

        Assert.Equal(3, _roles.UserRoleIds(user.Id).Count);

        await service.RemoveUserFromProjectAsync(user.Id, 1);

        // Project 1 relationship gone, project 2 relationship remains.
        Assert.Empty(await _users.ListProjectUsersAsync(1));
        Assert.Single(await _users.ListProjectUsersAsync(2));

        // projectRole1 gone; projectRole2 and globalRole remain.
        var remaining = _roles.UserRoleIds(user.Id);
        Assert.Equal(2, remaining.Count);
        Assert.Contains(projectRole2.Id, remaining);
        Assert.Contains(globalRole.Id, remaining);
        Assert.DoesNotContain(projectRole1.Id, remaining);
    }

    [Fact]
    public async Task UpdateProjectUserUpdateScopes()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42, Scopes = ["read_project"] });

        var userProject = await service.UpdateProjectUserAsync(new UpdateProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            Scopes = ["read_project", "write_project", "delete_project"],
        });

        Assert.Equal(
            ["read_project", "write_project", "delete_project"],
            JsonSerializer.Deserialize<List<string>>(userProject.Scopes!));
    }

    [Fact]
    public async Task UpdateProjectUserAddRoles()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();
        var role1 = await CreateRoleAsync("Project Admin", 42, RoleManagementService.LevelProject, ["manage_project"]);
        var role2 = await CreateRoleAsync("Project Member", 42, RoleManagementService.LevelProject, ["read_project"]);
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42 });

        await service.UpdateProjectUserAsync(new UpdateProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            AddRoleIds = [role1.Id, role2.Id],
        });

        Assert.Equal([role1.Id, role2.Id], _roles.UserRoleIds(user.Id));
    }

    [Fact]
    public async Task UpdateProjectUserRemoveRoles()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();
        var role1 = await CreateRoleAsync("Project Admin", 42, RoleManagementService.LevelProject, ["manage_project"]);
        var role2 = await CreateRoleAsync("Project Member", 42, RoleManagementService.LevelProject, ["read_project"]);
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42, RoleIds = [role1.Id, role2.Id] });

        await service.UpdateProjectUserAsync(new UpdateProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            RemoveRoleIds = [role1.Id],
        });

        Assert.Equal([role2.Id], _roles.UserRoleIds(user.Id));
    }

    [Fact]
    public async Task UpdateProjectUserAddAndRemoveRoles()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();
        var role1 = await CreateRoleAsync("Project Admin", 42, RoleManagementService.LevelProject, ["manage_project"]);
        var role2 = await CreateRoleAsync("Project Member", 42, RoleManagementService.LevelProject, ["read_project"]);
        var role3 = await CreateRoleAsync("Project Viewer", 42, RoleManagementService.LevelProject, ["view_project"]);
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42, RoleIds = [role1.Id] });

        await service.UpdateProjectUserAsync(new UpdateProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            AddRoleIds = [role2.Id, role3.Id],
            RemoveRoleIds = [role1.Id],
        });

        Assert.Equal([role2.Id, role3.Id], _roles.UserRoleIds(user.Id));
    }

    [Fact]
    public async Task UpdateProjectUserNotFound()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateProjectUserAsync(new UpdateProjectUserCommand
            {
                UserId = user.Id,
                ProjectId = 42,
                Scopes = ["read_project"],
            }));

        Assert.Equal(
            "failed to find user project relationship: ent: user_project not found",
            error.Message);
    }

    [Fact]
    public async Task UpdateProjectUserUpdateScopesAndRoles()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();
        var role = await CreateRoleAsync("Project Admin", 42, RoleManagementService.LevelProject, ["manage_project"]);
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42, Scopes = ["read_project"] });

        var userProject = await service.UpdateProjectUserAsync(new UpdateProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            Scopes = ["read_project", "write_project"],
            AddRoleIds = [role.Id],
        });

        Assert.Equal(
            ["read_project", "write_project"],
            JsonSerializer.Deserialize<List<string>>(userProject.Scopes!));
        Assert.Equal([role.Id], _roles.UserRoleIds(user.Id));
    }

    [Fact]
    public async Task UpdateProjectUserUpdateIsOwnerSuccess()
    {
        var service = OwnerService();
        var user = await CreateUserAsync();
        await service.AddUserToProjectAsync(
            new AddProjectUserCommand { UserId = user.Id, ProjectId = 42, Scopes = ["read_project"] });

        var updated = await service.UpdateProjectUserAsync(new UpdateProjectUserCommand
        {
            UserId = user.Id,
            ProjectId = 42,
            IsOwner = true,
        });

        Assert.True(updated.IsOwner);
        Assert.True((await _users.ListProjectUsersForUserAsync(user.Id))[0].IsOwner);
    }

    [Fact]
    public async Task UpdateProjectUserUpdateIsOwnerPermissionDenied()
    {
        // The reference test installs the regular user with no edges loaded
        // (contexts.WithUser(ctx, regularUser)), which production never does:
        // WithJWTAuth loads the user via GetUserByID with Roles/ProjectUsers
        // edges, so an equal-scope edit is allowed there. The port's on-demand
        // loading implements the production behavior (C161), so the denial is
        // reproduced with a target that has a scope the caller lacks.
        var setup = TestBypassService();
        var regular = await CreateUserAsync("regular@example.com");
        var target = await CreateUserAsync("target@example.com");

        await setup.AddUserToProjectAsync(new AddProjectUserCommand
        {
            UserId = regular.Id,
            ProjectId = 42,
            Scopes = ["read_project"],
        });
        await setup.AddUserToProjectAsync(new AddProjectUserCommand
        {
            UserId = target.Id,
            ProjectId = 42,
            Scopes = ["read_project", "write_project"],
        });

        var identity = new FakeManagementIdentity(new ManagementUser(regular.Id, IsOwner: false));
        var service = new UserManagementService(
            _users,
            new PasswordHasher(),
            new PermissionValidator(identity, _users, _roles),
            identity,
            _roles,
            new UserPrivacy(identity, _users),
            new UserProjectPrivacy(identity, _users),
            new RolePrivacy(identity, _users, _roles));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateProjectUserAsync(new UpdateProjectUserCommand
            {
                UserId = target.Id,
                ProjectId = 42,
                IsOwner = true,
            }));

        Assert.Equal(
            "permission denied: insufficient permissions: target user has scope "
                + "'write_project' that you don't possess",
            error.Message);

        // The isOwner flag was not updated.
        Assert.False((await _users.ListProjectUsersAsync(42)).Single(up => up.UserId == target.Id).IsOwner);
    }
}
