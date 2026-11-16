using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks APIKeyProfileTemplateService's observable behavior
/// (internal/server/biz/api_key_profile_template.go): create-time profile
/// rewriting and routing-policy validation, the duplicate-name error, update
/// publishing to linked and unchanged legacy profiles while preserving the
/// per-key alias, delete-time detach, load-time conflict suffixes and the
/// personal-key guard.
/// </summary>
public sealed class ApiKeyProfileTemplateServiceTests
{
    private static readonly DateTime FixedTime = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task CreateStoresTheInputNameInTheProfileAndDefaultsRoutingPolicy()
    {
        var fixture = new Fixture(isOwner: true);

        var created = await fixture.Service.CreateAsync(
            new CreateApiKeyProfileTemplateCommand
            {
                Name = "my-template",
                Description = "A test template",
                ProjectId = 12,
            },
            new ApiKeyProfileModel
            {
                Name = "test-profile",
                TemplateId = 7,
                TemplateName = "stale",
                ModelMappings =
                [
                    new ApiKeyModelMapping { From = "gpt-4", To = "gpt-4-turbo" },
                ],
            });

        Assert.Equal("my-template", created.Name);
        Assert.Equal("A test template", created.Description);
        Assert.Equal(12L, created.ProjectId);

        var stored = Assert.Single(fixture.Templates.Rows);
        Assert.NotNull(stored.Profile);
        var profile = ApiKeyProfilesCodec.DeserializeProfile(stored.Profile);
        Assert.NotNull(profile);
        // The template name wins and the template link fields are cleared.
        Assert.Equal("my-template", profile.Name);
        Assert.Null(profile.TemplateId);
        Assert.Equal(string.Empty, profile.TemplateName);
        Assert.Equal(
            "default",
            profile.LoadBalanceStrategy);
        Assert.Equal("default", profile.TraceStickyMode);
        Assert.Equal("gpt-4", Assert.Single(profile.ModelMappings!).From);
    }

    [Fact]
    public async Task CreateWithoutProfileStoresSqlNullAndDefaultsDescription()
    {
        var fixture = new Fixture(isOwner: true);

        var created = await fixture.Service.CreateAsync(
            new CreateApiKeyProfileTemplateCommand
            {
                Name = "no-profile",
                ProjectId = 12,
            },
            profile: null);

        Assert.Equal(string.Empty, created.Description);
        Assert.Null(created.Profile);
        Assert.Null(Assert.Single(fixture.Templates.Rows).Profile);
    }

    [Fact]
    public async Task CreateInvalidRoutingPolicyReportsTheInputName()
    {
        var fixture = new Fixture(isOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(
                new CreateApiKeyProfileTemplateCommand
                {
                    Name = "input-name",
                    ProjectId = 12,
                },
                new ApiKeyProfileModel
                {
                    Name = "profile-name",
                    LoadBalanceStrategy = "round_robin",
                }));

        Assert.Equal("profile 'input-name' loadBalanceStrategy is invalid", error.Message);
        Assert.Empty(fixture.Templates.Rows);
    }

    [Fact]
    public async Task CreateDuplicateNameThrowsTheCodedTemplateNameError()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Templates.Add(template =>
        {
            template.Name = "shared";
            template.ProjectId = 12;
        });

        var error = await Assert.ThrowsAsync<CodedException>(() =>
            fixture.Service.CreateAsync(
                new CreateApiKeyProfileTemplateCommand
                {
                    Name = "shared",
                    ProjectId = 12,
                },
                profile: null));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("Template name 'shared' already exists", error.Message);
        Assert.Equal("Template", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("shared", error.Extensions["value"]);
        Assert.Single(fixture.Templates.Rows);
    }

    [Fact]
    public async Task CreateRequiresTheMutationProjectToMatchTheRequestProject()
    {
        var fixture = new Fixture(
            isOwner: false,
            scopes: [ApiKeyProfileTemplatePrivacy.ReadApiKeys,
                ApiKeyProfileTemplatePrivacy.WriteApiKeys]);
        fixture.Identity.ProjectId = 12;

        // A project-scoped caller may create inside the request's project...
        var created = await fixture.Service.CreateAsync(
            new CreateApiKeyProfileTemplateCommand
            {
                Name = "mine",
                ProjectId = 12,
            },
            profile: null);
        Assert.Equal(12L, created.ProjectId);

        // ...but a project mismatch skips the project rule and the owner rule
        // denies, exactly like projectMemberMutationRule.
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(
                new CreateApiKeyProfileTemplateCommand
                {
                    Name = "other",
                    ProjectId = 99,
                },
                profile: null));

        Assert.Equal("failed to create template: ent/privacy: deny rule", denied.Message);
        Assert.IsType<PrivacyDenyException>(denied.InnerException);
        Assert.Single(fixture.Templates.Rows);
    }

    [Fact]
    public async Task GetWrapsNotFoundWhileNodeAndForReadReturnRawEntErrors()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Templates.Add(template =>
        {
            template.Name = "existing";
            template.ProjectId = 12;
        });

        var wrapped = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetAsync(999));
        Assert.Equal(
            "failed to get template: ent: api_key_profile_template not found",
            wrapped.Message);

        var node = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetNodeAsync(999));
        Assert.Equal("ent: api_key_profile_template not found", node.Message);

        var both = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetForReadAsync(id: null, name: null));
        Assert.Equal(
            "exactly one of template id or name must be provided",
            both.Message);

        var byName = await fixture.Service.GetForReadAsync(id: null, name: "existing");
        Assert.Equal("existing", byName.Name);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetForReadAsync(id: null, name: "missing"));
        Assert.Equal("ent: api_key_profile_template not found", missing.Message);
    }

    [Fact]
    public async Task UpdatePublishesToLinkedAndUnchangedLegacyProfiles()
    {
        var fixture = new Fixture(isOwner: true);

        var template = fixture.Templates.Add(item =>
        {
            item.Name = "Production";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "Production",
                ModelMappings = [new ApiKeyModelMapping { From = "claude", To = "old-model" }],
            });
        });
        var templateId = template.Id;

        var linked = fixture.Keys.Add(key =>
        {
            key.ProjectId = 12;
            key.Profiles = Profiles("Local alias", templateId, "old-model");
        });
        var legacy = fixture.Keys.Add(key =>
        {
            key.ProjectId = 12;
            key.Profiles = Profiles("Production", null, "old-model");
        });
        var detached = fixture.Keys.Add(key =>
        {
            key.ProjectId = 12;
            key.Profiles = Profiles("Production", null, "custom-model");
        });

        var count = await fixture.Service.CountLinkedProfilesAsync(template);
        Assert.Equal(2, count);

        var updated = await fixture.Service.UpdateAsync(
            template.Id,
            new UpdateApiKeyProfileTemplateCommand(),
            new ApiKeyProfileModel
            {
                Name = "Production",
                ModelMappings = [new ApiKeyModelMapping { From = "claude", To = "new-model" }],
            });
        Assert.Equal("Production", updated.Name);

        foreach (var key in new[] { linked, legacy })
        {
            var profile = Assert.Single(
                ApiKeyProfilesCodec.Deserialize(key.Profiles).Profiles!);
            Assert.Equal("new-model", Assert.Single(profile.ModelMappings!).To);
            Assert.Equal<long?>(templateId, profile.TemplateId);
        }

        // The per-key alias survives publishing; a customized profile is not
        // adopted.
        Assert.Equal(
            "Local alias",
            Assert.Single(ApiKeyProfilesCodec.Deserialize(linked.Profiles).Profiles!).Name);
        var detachedProfile = Assert.Single(
            ApiKeyProfilesCodec.Deserialize(detached.Profiles).Profiles!);
        Assert.Equal("custom-model", Assert.Single(detachedProfile.ModelMappings!).To);
        Assert.Null(detachedProfile.TemplateId);

        var deleted = await fixture.Service.DeleteAsync(template.Id);
        Assert.Equal("Production", deleted.Name);

        foreach (var key in new[] { linked, legacy })
        {
            var profile = Assert.Single(
                ApiKeyProfilesCodec.Deserialize(key.Profiles).Profiles!);
            Assert.Null(profile.TemplateId);
            Assert.Equal(string.Empty, profile.TemplateName);
            Assert.Equal("new-model", Assert.Single(profile.ModelMappings!).To);
        }

        // The template is soft-deleted, so reads no longer find it.
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetNodeAsync(template.Id));
        Assert.Equal("ent: api_key_profile_template not found", missing.Message);
    }

    [Fact]
    public async Task UpdateWithOnlyNameRepublishesTheStoredProfile()
    {
        var fixture = new Fixture(isOwner: true);
        var template = fixture.Templates.Add(item =>
        {
            item.Name = "Original";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "Original",
                ModelMappings = [new ApiKeyModelMapping { From = "a", To = "b" }],
            });
        });

        var updated = await fixture.Service.UpdateAsync(
            template.Id,
            new UpdateApiKeyProfileTemplateCommand { Name = "Renamed", Description = "desc" },
            profile: null);

        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("desc", updated.Description);
        var profile = ApiKeyProfilesCodec.DeserializeProfile(updated.Profile);
        Assert.NotNull(profile);
        Assert.Equal("Renamed", profile.Name);
        Assert.Equal("b", Assert.Single(profile.ModelMappings!).To);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(
                999,
                new UpdateApiKeyProfileTemplateCommand { Name = "x" },
                profile: null));
        Assert.Equal(
            "failed to get template: ent: api_key_profile_template not found",
            missing.Message);
    }

    [Fact]
    public async Task LoadAppendsTheProfileAndResolvesNameConflicts()
    {
        var fixture = new Fixture(isOwner: true);

        var key = fixture.Keys.Add(item =>
        {
            item.ProjectId = 12;
            item.Profiles =
                """{"activeProfile":"Production","profiles":[{"name":"Production"},{"name":"Production (1)"}]}""";
        });
        var template = fixture.Templates.Add(item =>
        {
            item.Name = "prod-template";
            item.Description = "Production template";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "Production",
                ModelMappings = [new ApiKeyModelMapping { From = "claude-3", To = "claude-3-opus" }],
            });
        });

        var updated = await fixture.Service.LoadAsync(template.Id, key.Id);

        var profiles = ApiKeyProfilesCodec.Deserialize(updated.Profiles);
        Assert.Equal("Production", profiles.ActiveProfile);
        Assert.Equal(3, profiles.Profiles!.Count);
        Assert.Equal("Production", profiles.Profiles[0].Name);
        Assert.Equal("Production (1)", profiles.Profiles[1].Name);
        Assert.Equal("Production (2)", profiles.Profiles[2].Name);
        Assert.Equal<long?>(template.Id, profiles.Profiles[2].TemplateId);
        Assert.Equal("prod-template", profiles.Profiles[2].TemplateName);
        Assert.Equal("claude-3", Assert.Single(profiles.Profiles[2].ModelMappings!).From);
    }

    [Fact]
    public async Task LoadFallsBackToTheTemplateNameForAnUnnamedProfile()
    {
        var fixture = new Fixture(isOwner: true);

        var key = fixture.Keys.Add(item =>
        {
            item.ProjectId = 12;
            item.Profiles = """{"activeProfile":"Default","profiles":[{"name":"Default"}]}""";
        });
        var template = fixture.Templates.Add(item =>
        {
            item.Name = "load-template";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                ModelMappings = [new ApiKeyModelMapping { From = "gpt-4", To = "gpt-4-turbo" }],
            });
        });

        var updated = await fixture.Service.LoadAsync(template.Id, key.Id);

        var profiles = ApiKeyProfilesCodec.Deserialize(updated.Profiles);
        Assert.Equal(2, profiles.Profiles!.Count);
        Assert.Equal("Default", profiles.Profiles[0].Name);
        Assert.Equal("load-template", profiles.Profiles[1].Name);
    }

    [Fact]
    public async Task LoadReportsMissingTemplateKeyProjectMismatchAndEmptyProfile()
    {
        var fixture = new Fixture(isOwner: true);
        var key = fixture.Keys.Add(item => item.ProjectId = 12);

        var missingTemplate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.LoadAsync(999, key.Id));
        Assert.Equal(
            "failed to get template: ent: api_key_profile_template not found",
            missingTemplate.Message);

        var template = fixture.Templates.Add(item =>
        {
            item.Name = "template";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "Production",
            });
        });

        var missingKey = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.LoadAsync(template.Id, 999));
        Assert.Equal("failed to get API key: ent: api_key not found", missingKey.Message);

        var otherProjectKey = fixture.Keys.Add(item => item.ProjectId = 99);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.LoadAsync(template.Id, otherProjectKey.Id));
        Assert.Equal(
            "template and API key must belong to the same project",
            mismatch.Message);

        var emptyTemplate = fixture.Templates.Add(item =>
        {
            item.Name = "empty";
            item.ProjectId = 12;
            item.Profile = null;
        });
        var noProfile = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.LoadAsync(emptyTemplate.Id, key.Id));
        Assert.Equal("template has no profile", noProfile.Message);
    }

    [Fact]
    public async Task LoadGuardsPersonalKeysToTheirCreatorOrASystemOwner()
    {
        // One shared data set, three request identities, like the reference
        // TestLoadTemplate_PersonalKeyGuard.
        var users = new FakeUserAdminStore { RolesSource = new FakeRoleStore() };
        var projects = new FakeProjectStore();
        projects.Add(project =>
        {
            project.Id = 12;
            project.Name = "project";
            project.Status = "active";
        });

        var creatorId = await users.CreateAsync(
            new UserRecord { Email = "creator@example.com", Status = "activated" },
            roleIds: null);
        users.AssignProject(
            creatorId,
            12,
            scopes: [ApiKeyProfileTemplatePrivacy.ReadApiKeys,
                ApiKeyProfileTemplatePrivacy.WriteApiKeys]);

        var projectOwnerId = await users.CreateAsync(
            new UserRecord { Email = "project-owner@example.com", Status = "activated" },
            roleIds: null);
        users.AssignProject(projectOwnerId, 12, isOwner: true);

        var systemOwnerId = await users.CreateAsync(
            new UserRecord
            {
                Email = "system-owner@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);

        var keys = new FakeApiKeyStore();
        var templates = new FakeApiKeyProfileTemplateStore();
        var personalKey = keys.Add(key =>
        {
            key.Name = "personal-api-key";
            key.Type = "personal";
            key.ProjectId = 12;
            key.UserId = creatorId;
            key.Profiles = null;
        });
        var template = templates.Add(item =>
        {
            item.Name = "prod-template";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "Production",
            });
        });

        ApiKeyProfileTemplateService ServiceFor(long userId, bool isOwner)
            => BuildService(
                keys,
                templates,
                projects,
                users,
                new FakeManagementIdentity(new ManagementUser(userId, isOwner))
                {
                    ProjectId = 12,
                });

        var projectOwner = ServiceFor(projectOwnerId, isOwner: false);
        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            projectOwner.LoadAsync(template.Id, personalKey.Id));
        Assert.Equal(
            "personal API key can only be modified by its creator or a system owner",
            denied.Message);
        Assert.Null(personalKey.Profiles);

        var creator = ServiceFor(creatorId, isOwner: false);
        var updated = await creator.LoadAsync(template.Id, personalKey.Id);
        Assert.Single(ApiKeyProfilesCodec.Deserialize(updated.Profiles).Profiles!);

        var systemOwner = ServiceFor(systemOwnerId, isOwner: true);
        updated = await systemOwner.LoadAsync(template.Id, personalKey.Id);
        Assert.Equal(2, ApiKeyProfilesCodec.Deserialize(updated.Profiles).Profiles!.Count);
    }

    [Fact]
    public async Task ListFiltersByProjectEdgeAndTheQueryPolicy()
    {
        var fixture = new Fixture(isOwner: true);
        for (var i = 0; i < 3; i++)
        {
            var index = i;
            fixture.Templates.Add(template =>
            {
                template.Name = $"template-{index}";
                template.ProjectId = 12;
            });
        }

        fixture.Templates.Add(template =>
        {
            template.Name = "other-project";
            template.ProjectId = 99;
        });

        // The reference resolver's where clause from
        // TestApiKeyProfileTemplate_QueryTemplates.
        var page = await fixture.Service.ListAsync(new ApiKeyProfileTemplateListQuery
        {
            Where = new ApiKeyProfileTemplateListFilter
            {
                HasProject = true,
                HasProjectWith =
                [
                    new ApiKeyProfileTemplateProjectFilter { Id = 12 },
                ],
            },
            First = 10,
        });
        Assert.Equal(3, page.TotalCount);

        // A project-scoped caller gets the privacy predicate even without a
        // where clause.
        var member = new Fixture(
            isOwner: false,
            scopes: [ApiKeyProfileTemplatePrivacy.ReadApiKeys]);
        member.Identity.ProjectId = 12;
        member.Templates.Add(template =>
        {
            template.Name = "mine";
            template.ProjectId = 12;
        });
        member.Templates.Add(template =>
        {
            template.Name = "elsewhere";
            template.ProjectId = 99;
        });

        var scoped = await member.Service.ListAsync(
            new ApiKeyProfileTemplateListQuery { First = 10 });
        Assert.Equal("mine", Assert.Single(scoped.Nodes).Name);
    }

    [Fact]
    public async Task DeleteReturnsThePreDeleteRowAndRespectsTheMutationProject()
    {
        var fixture = new Fixture(
            isOwner: false,
            scopes: [ApiKeyProfileTemplatePrivacy.ReadApiKeys,
                ApiKeyProfileTemplatePrivacy.WriteApiKeys]);
        fixture.Identity.ProjectId = 12;
        var template = fixture.Templates.Add(item =>
        {
            item.Name = "to-delete";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "delete-profile",
            });
        });

        var deleted = await fixture.Service.DeleteAsync(template.Id);

        Assert.Equal("to-delete", deleted.Name);
        Assert.Equal("delete-profile",
            ApiKeyProfilesCodec.DeserializeProfile(deleted.Profile)?.Name);
        Assert.Null(await fixture.Templates.GetAsync(template.Id, access: null));
    }

    private static ApiKeyProfileTemplateService BuildService(
        FakeApiKeyStore keys,
        FakeApiKeyProfileTemplateStore templates,
        FakeProjectStore projects,
        FakeUserAdminStore users,
        FakeManagementIdentity identity)
        => new(
            templates,
            keys,
            new ApiKeyProfileTemplatePrivacy(identity, users),
            new ApiKeyPrivacy(identity, users, projects),
            identity);

    private static string Profiles(
        string profileName,
        long? templateId,
        string toModel)
    {
        var profile = new ApiKeyProfileModel
        {
            Name = profileName,
            TemplateId = templateId,
            TemplateName = templateId is null ? string.Empty : "Production",
            ModelMappings = [new ApiKeyModelMapping { From = "claude", To = toModel }],
        };

        return ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
        {
            ActiveProfile = profileName,
            Profiles = [profile],
        });
    }

    private sealed class Fixture
    {
        public FakeApiKeyProfileTemplateStore Templates { get; } = new();

        public FakeApiKeyStore Keys { get; } = new();

        public FakeProjectStore Projects { get; } = new();

        public FakeUserAdminStore Users { get; } = new();

        public FakeRoleStore Roles { get; } = new();

        public FakeManagementIdentity Identity { get; }

        public ApiKeyProfileTemplateService Service { get; }

        public long UserId { get; }

        public Fixture(bool isOwner, IReadOnlyList<string>? scopes = null)
        {
            Users.RolesSource = Roles;
            UserId = Users.CreateAsync(
                new UserRecord
                {
                    Email = "template-admin@example.com",
                    Status = "activated",
                    IsOwner = isOwner,
                },
                roleIds: null).GetAwaiter().GetResult();
            if (scopes is not null)
            {
                Users.AssignProject(UserId, 12, scopes: scopes);
            }

            Projects.Add(project =>
            {
                project.Id = 12;
                project.Name = "project";
                project.Status = "active";
            });

            Identity = new FakeManagementIdentity(new ManagementUser(UserId, isOwner));
            Service = BuildService(Keys, Templates, Projects, Users, Identity);
        }
    }
}

/// <summary>
/// In-memory APIKeyProfileTemplateStore mirroring the Dapper store's visible
/// predicates: live rows only, the access project predicate, the
/// (project_id, name, deleted_at) uniqueness rule, and soft deletes.
/// </summary>
internal sealed class FakeApiKeyProfileTemplateStore : IApiKeyProfileTemplateStore
{
    private static readonly DateTime FixedTime =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private long _nextId = 1;

    public List<ApiKeyProfileTemplateRecord> Rows { get; } = [];

    public Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
        => RunInTransactionCoreAsync(action, cancellationToken);

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => RunInTransactionCoreAsync(
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private async Task<T> RunInTransactionCoreAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        var snapshot = Rows.Select(Clone).ToList();
        var nextId = _nextId;
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Rows.Clear();
            Rows.AddRange(snapshot);
            _nextId = nextId;
            throw;
        }
    }

    public Task<ApiKeyProfileTemplateRecord?> GetAsync(
        long id,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        // The Dapper store materializes a detached record; mutations performed
        // by UpdateAsync must not reach the record a caller read earlier (the
        // update path compares against the pre-update template).
        var row = Rows.SingleOrDefault(item =>
            item.Id == id && item.DeletedAt == 0 && MatchesAccess(access, item));
        return Task.FromResult(row is null ? null : Clone(row));
    }

    public Task<ApiKeyProfileTemplateRecord?> GetByNameAsync(
        string name,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rows = Rows.Where(row => row.Name == name
            && row.DeletedAt == 0
            && MatchesAccess(access, row)).Take(2).ToList();
        if (rows.Count > 1)
        {
            throw new StoreFailureException(
                "ent: api_key_profile_template not singular",
                new InvalidOperationException("ent: api_key_profile_template not singular"));
        }

        return Task.FromResult<ApiKeyProfileTemplateRecord?>(
            rows.Count == 0 ? null : rows[0]);
    }

    public Task<IReadOnlyList<ApiKeyProfileTemplateRecord>> ListForProjectAsync(
        long projectId,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        IReadOnlyList<ApiKeyProfileTemplateRecord> rows = Rows
            .Where(row => row.DeletedAt == 0
                && row.ProjectId == projectId
                && MatchesAccess(access, row))
            .OrderBy(static row => row.Id)
            .Select(Clone)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<ListPage<ApiKeyProfileTemplateRecord>> ListAsync(
        ApiKeyProfileTemplateListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rows = Rows
            .Where(row => row.DeletedAt == 0
                && Matches(query.Where, row)
                && MatchesAccess(query.Access, row))
            .ToList();

        var page = FakePagination.Paginate(
            rows,
            query.SortField != ApiKeyProfileTemplateSortField.Id,
            static row => row.Id,
            row => query.SortField == ApiKeyProfileTemplateSortField.CreatedAt
                ? row.CreatedAt
                : row.UpdatedAt,
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<ApiKeyProfileTemplateRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<ApiKeyProfileTemplateRecord> CreateAsync(
        ApiKeyProfileTemplateRecord template,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (template.CreatedAt == default)
        {
            template.CreatedAt = DateTime.UtcNow;
        }

        if (template.UpdatedAt == default)
        {
            template.UpdatedAt = template.CreatedAt;
        }

        if (Rows.Any(row => row.DeletedAt == 0
            && row.ProjectId == template.ProjectId
            && row.Name == template.Name))
        {
            throw new ApiKeyProfileTemplateNameConflictException(
                new InvalidOperationException("duplicate key value"));
        }

        template.Id = _nextId++;
        Rows.Add(template);
        return Task.FromResult(template);
    }

    public Task<ApiKeyProfileTemplateRecord?> UpdateAsync(
        ApiKeyProfileTemplateUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var row = Rows.SingleOrDefault(item => item.Id == update.Id
            && item.DeletedAt == 0
            && (update.ProjectWritePredicate is null
                || item.ProjectId == update.ProjectWritePredicate.Value));
        if (row is null)
        {
            return Task.FromResult<ApiKeyProfileTemplateRecord?>(null);
        }

        if (update.Name is not null
            && update.Name != row.Name
            && Rows.Any(other => other.Id != row.Id
                && other.DeletedAt == 0
                && other.ProjectId == row.ProjectId
                && other.Name == update.Name))
        {
            throw new ApiKeyProfileTemplateNameConflictException(
                new InvalidOperationException("duplicate key value"));
        }

        if (update.Name is not null)
        {
            row.Name = update.Name;
        }

        if (update.Description is not null)
        {
            row.Description = update.Description;
        }

        if (update.Profile is not null)
        {
            row.Profile = update.Profile;
        }

        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<ApiKeyProfileTemplateRecord?>(row);
    }

    public Task<bool> DeleteAsync(
        long id,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var row = Rows.SingleOrDefault(item => item.Id == id
            && item.DeletedAt == 0
            && MatchesAccess(access, item));
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public ApiKeyProfileTemplateRecord Add(
        Action<ApiKeyProfileTemplateRecord>? configure = null)
    {
        var row = new ApiKeyProfileTemplateRecord
        {
            CreatedAt = FixedTime,
            UpdatedAt = FixedTime,
            Name = "template",
            Description = string.Empty,
            ProjectId = 12,
        };
        configure?.Invoke(row);
        row.Id = row.Id > 0 ? row.Id : _nextId++;
        if (row.Id >= _nextId)
        {
            _nextId = row.Id + 1;
        }

        Rows.Add(row);
        return row;
    }

    private static bool MatchesAccess(
        ApiKeyProfileTemplateAccessFilter? access,
        ApiKeyProfileTemplateRecord row) =>
        access?.ProjectId is null || row.ProjectId == access.ProjectId.Value;

    private static bool Matches(
        ApiKeyProfileTemplateListFilter? filter,
        ApiKeyProfileTemplateRecord row)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id
            || filter.IdNEQ is not null && row.Id == filter.IdNEQ
            || filter.IdIn is not null && !filter.IdIn.Contains(row.Id)
            || filter.IdNotIn is not null && filter.IdNotIn.Contains(row.Id)
            || filter.Name is not null && row.Name != filter.Name
            || filter.NameContainsFold is not null
                && !row.Name.Contains(filter.NameContainsFold, StringComparison.OrdinalIgnoreCase)
            || filter.ProjectId is not null && row.ProjectId != filter.ProjectId
            || filter.ProjectIdNEQ is not null && row.ProjectId == filter.ProjectIdNEQ
            || filter.ProjectIdIn is not null && !filter.ProjectIdIn.Contains(row.ProjectId)
            || filter.ProjectIdNotIn is not null
                && filter.ProjectIdNotIn.Contains(row.ProjectId)
            || filter.HasProject == false)
        {
            return false;
        }

        if (filter.HasProjectWith is { Count: > 0 } projects
            && !projects.Any(project => MatchesProject(project, row.ProjectId)))
        {
            return false;
        }

        if (filter.Not is not null
            && HasPredicates(filter.Not)
            && Matches(filter.Not, row))
        {
            return false;
        }

        if (filter.And is not null)
        {
            var nested = filter.And.Where(HasPredicates).ToList();
            if (nested.Count > 0 && !nested.All(inner => Matches(inner, row)))
            {
                return false;
            }
        }

        if (filter.Or is not null)
        {
            var nested = filter.Or.Where(HasPredicates).ToList();
            if (nested.Count == 0 || !nested.Any(inner => Matches(inner, row)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesProject(
        ApiKeyProfileTemplateProjectFilter filter,
        long projectId)
    {
        if (filter.Name is not null
            || filter.NameContainsFold is not null
            || filter.Status is not null
            || filter.StatusIn is not null)
        {
            throw new NotSupportedException(
                "the template fake has no project rows for name/status predicates");
        }

        if (filter.Id is not null && projectId != filter.Id
            || filter.IdNEQ is not null && projectId == filter.IdNEQ
            || filter.IdIn is not null && !filter.IdIn.Contains(projectId)
            || filter.IdNotIn is not null && filter.IdNotIn.Contains(projectId))
        {
            return false;
        }

        if (filter.Not is not null && HasProjectPredicates(filter.Not)
            && MatchesProject(filter.Not, projectId))
        {
            return false;
        }

        if (filter.And is not null)
        {
            var nested = filter.And.Where(HasProjectPredicates).ToList();
            if (nested.Count > 0 && !nested.All(inner => MatchesProject(inner, projectId)))
            {
                return false;
            }
        }

        if (filter.Or is not null)
        {
            var nested = filter.Or.Where(HasProjectPredicates).ToList();
            if (nested.Count == 0 || !nested.Any(inner => MatchesProject(inner, projectId)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasPredicates(ApiKeyProfileTemplateListFilter filter)
        => filter.Id is not null
            || filter.IdNEQ is not null
            || filter.IdIn is not null
            || filter.IdNotIn is not null
            || filter.Name is not null
            || filter.NameContainsFold is not null
            || filter.ProjectId is not null
            || filter.ProjectIdNEQ is not null
            || filter.ProjectIdIn is not null
            || filter.ProjectIdNotIn is not null
            || filter.HasProject is not null
            || filter.HasProjectWith is not null
            || (filter.Not is not null && HasPredicates(filter.Not))
            || (filter.And is not null && filter.And.Any(HasPredicates))
            || filter.Or is not null;

    private static bool HasProjectPredicates(ApiKeyProfileTemplateProjectFilter filter)
        => filter.Id is not null
            || filter.IdNEQ is not null
            || filter.IdIn is not null
            || filter.IdNotIn is not null
            || filter.Name is not null
            || filter.NameContainsFold is not null
            || filter.Status is not null
            || filter.StatusIn is not null
            || filter.Not is not null
            || (filter.And is not null && filter.And.Any(HasProjectPredicates))
            || filter.Or is not null;

    private static ApiKeyProfileTemplateRecord Clone(ApiKeyProfileTemplateRecord row) => new()
    {
        Id = row.Id,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        Name = row.Name,
        Description = row.Description,
        Profile = row.Profile,
        ProjectId = row.ProjectId,
    };
}
