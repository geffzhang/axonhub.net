using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks APIKeyService's observable behavior (internal/server/biz/api_key.go):
/// generated keys, schema defaults, the project-admin gate, personal-key
/// ownership, profile validation/template detach and the bulk status flow with
/// the reference error strings.
/// </summary>
public sealed class ApiKeyManagementServiceTests
{
    [Fact]
    public async Task CreateDefaultsUserKeyAndAssociatesCurrentUser()
    {
        var fixture = new Fixture(isOwner: true);

        var created = await fixture.Service.CreateAsync(
            new CreateApiKeyCommand { Name = "Primary", ProjectId = 12 });

        Assert.Equal("user", created.Type);
        Assert.Equal("enabled", created.Status);
        Assert.Equal(fixture.UserId, created.UserId);
        Assert.Equal(12, created.ProjectId);
        // User type keeps the schema default scopes.
        Assert.Equal(
            ["read_channels", "write_requests"],
            JsonSerializer.Deserialize<string[]>(created.Scopes!)!);
        Assert.Equal("""{"activeProfile":"","profiles":null}""", created.Profiles);
        Assert.Equal("[]", created.AllowedIps);

        // GenerateAPIKey: "<prefix>-" + 32 random bytes as lowercase hex.
        Assert.StartsWith("ah-", created.Key, StringComparison.Ordinal);
        Assert.Equal(3 + 64, created.Key.Length);
        Assert.Matches("^[0-9a-f]{64}$", created.Key[3..]);

        Assert.Same(created, Assert.Single(fixture.Keys.Rows));
    }

    [Fact]
    public async Task CreateRejectsNoAuthKeys()
    {
        var fixture = new Fixture(isOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(new CreateApiKeyCommand
            {
                Name = "No auth",
                ProjectId = 12,
                Type = "noauth",
            }));

        Assert.Equal("noauth type API key is reserved", error.Message);
        Assert.Empty(fixture.Keys.Rows);
    }

    [Fact]
    public async Task CreateUserKeysRequiresProjectAdminButServiceAccountsDoNot()
    {
        // Project member with write_api_keys (privacy allows writes) but without
        // write_users/write_roles, so not a project admin.
        var fixture = new Fixture(isOwner: false, scopes: ["read_api_keys", "write_api_keys"]);
        fixture.Identity.ProjectId = 12;

        var denied = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(new CreateApiKeyCommand { Name = "Mine", ProjectId = 12 }));
        Assert.Equal(
            "permission denied: project API keys require project admin permissions",
            denied.Message);
        Assert.Empty(fixture.Keys.Rows);

        var serviceAccount = await fixture.Service.CreateAsync(new CreateApiKeyCommand
        {
            Name = "CI",
            ProjectId = 12,
            Type = "service_account",
            Scopes = ["read_channels"],
        });

        Assert.Equal("service_account", serviceAccount.Type);
        Assert.Equal("""["read_channels"]""", serviceAccount.Scopes);
        Assert.Equal(fixture.UserId, serviceAccount.UserId);
    }

    [Fact]
    public async Task CreateServiceAccountUsesAnEmptyScopeArrayWhenOmitted()
    {
        var fixture = new Fixture(isOwner: true);

        var created = await fixture.Service.CreateAsync(new CreateApiKeyCommand
        {
            Name = "CI",
            ProjectId = 12,
            Type = "service_account",
        });

        Assert.Equal("[]", created.Scopes);
    }

    [Fact]
    public async Task CreateValidatesAllowedIpsWithTheReferenceMessage()
    {
        var fixture = new Fixture(isOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(new CreateApiKeyCommand
            {
                Name = "Guarded",
                ProjectId = 12,
                AllowedIps = ["10.0.0.0/33"],
            }));

        Assert.StartsWith("invalid CIDR \"10.0.0.0/33\":", error.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Keys.Rows);
    }

    [Fact]
    public async Task CreateDuplicateNameThrowsCodedErrorAndRollsTheInsertBack()
    {
        var fixture = new Fixture(isOwner: true);
        var existing = fixture.Keys.Add(key =>
        {
            key.Name = "shared";
            key.ProjectId = 12;
            key.UserId = fixture.UserId;
        });

        var error = await Assert.ThrowsAsync<CodedException>(() =>
            fixture.Service.CreateAsync(new CreateApiKeyCommand { Name = "shared", ProjectId = 12 }));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("API Key name 'shared' already exists", error.Message);
        Assert.Equal("API Key", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("shared", error.Extensions["value"]);

        // The uniqueness query runs after the insert; the surrounding
        // transaction rolls the new row back.
        var retained = Assert.Single(fixture.Keys.Rows);
        Assert.Equal(existing.Id, retained.Id);
        Assert.Equal("shared", retained.Name);
    }

    [Fact]
    public async Task UpdateRejectsScopeChangesOnUserAndPersonalKeys()
    {
        var fixture = new Fixture(isOwner: true);
        var userKey = fixture.Keys.Add(key => key.ProjectId = 12);
        var personalKey = fixture.Keys.Add(key =>
        {
            key.Name = "personal key";
            key.Type = "personal";
            key.ProjectId = 12;
            key.UserId = fixture.UserId;
        });

        var userError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(new UpdateApiKeyCommand
            {
                Id = userKey.Id,
                AppendScopes = ["read_users"],
            }));
        Assert.Equal("user type API key cannot update scopes", userError.Message);

        var personalError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(new UpdateApiKeyCommand
            {
                Id = personalKey.Id,
                ClearScopes = true,
            }));
        Assert.Equal("personal type API key cannot update scopes", personalError.Message);
    }

    [Fact]
    public async Task UpdateRejectsNoAuthKeys()
    {
        var fixture = new Fixture(isOwner: true);
        var noauth = fixture.Keys.Add(key =>
        {
            key.Name = "no auth";
            key.Type = "noauth";
            key.ProjectId = 12;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(new UpdateApiKeyCommand { Id = noauth.Id, Name = "renamed" }));

        Assert.Equal("noauth type API key cannot be updated", error.Message);
        Assert.Equal("no auth", noauth.Name);
    }

    [Fact]
    public async Task UpdateAppliesScopesAndAllowedIpsForServiceAccounts()
    {
        var fixture = new Fixture(isOwner: true);
        var key = fixture.Keys.Add(item =>
        {
            item.Name = "CI";
            item.Type = "service_account";
            item.ProjectId = 12;
            item.Scopes = """["read_channels"]""";
            item.AllowedIps = """["127.0.0.1"]""";
        });

        var updated = await fixture.Service.UpdateAsync(new UpdateApiKeyCommand
        {
            Id = key.Id,
            Name = "CI 2",
            Scopes = ["write_requests"],
            AppendScopes = ["read_users"],
            AllowedIps = ["10.0.0.0/8"],
            AppendAllowedIps = ["192.168.0.1"],
            ClearAllowedIps = false,
        });

        Assert.Equal("CI 2", updated.Name);
        // ent's json Append overwrites the plain Set on the same column, so the
        // result is the pre-update value plus the appended elements.
        Assert.Equal("""["read_channels","read_users"]""", updated.Scopes);
        Assert.Equal("""["127.0.0.1","192.168.0.1"]""", updated.AllowedIps);
    }

    [Fact]
    public async Task UpdateStatusWrapsPrivacyDenialsLikeTheReference()
    {
        // read_api_keys only: the read succeeds, the mutation policy denies.
        var fixture = new Fixture(isOwner: false, scopes: ["read_api_keys"]);
        fixture.Identity.ProjectId = 12;
        var key = fixture.Keys.Add(item => item.ProjectId = 12);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateStatusAsync(key.Id, "disabled"));

        Assert.Equal("failed to update API key status: ent/privacy: deny rule", error.Message);
        Assert.Equal("enabled", key.Status);
    }

    [Fact]
    public async Task UpdateStatusEnforcesPersonalKeyOwnership()
    {
        // Project owner (can see every key in the project) who is not the key
        // creator and not a system owner.
        var fixture = new Fixture(isOwner: false);
        fixture.Identity.ProjectId = 12;
        fixture.Users.AssignProject(fixture.UserId, 12, isOwner: true);
        var personal = fixture.Keys.Add(item =>
        {
            item.Name = "someone else's";
            item.Type = "personal";
            item.ProjectId = 12;
            item.UserId = 999;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateStatusAsync(personal.Id, "disabled"));

        Assert.Equal(
            "personal API key can only be modified by its creator or a system owner",
            error.Message);
        Assert.Equal("enabled", personal.Status);
    }

    [Fact]
    public async Task UpdateProfilesKeepsUnchangedTemplateLinksAndDetachesEditedOnes()
    {
        var fixture = new Fixture(isOwner: true);
        var linkedJson =
            """{"activeProfile":"a","profiles":[{"name":"a","templateID":7,"templateName":"Tpl","modelMappings":null,"loadBalanceStrategy":"default","traceStickyMode":"default","channelIDs":[1],"channelTags":["x"],"channelTagsMatchMode":"any","modelIDs":["m1"]}]}""";
        var key = fixture.Keys.Add(item =>
        {
            item.Name = "Profiled";
            item.ProjectId = 12;
            item.Profiles = linkedJson;
        });

        var unchanged = await fixture.Service.UpdateProfilesAsync(
            key.Id,
            new ApiKeyProfilesModel
            {
                ActiveProfile = "a",
                Profiles =
                [
                    new ApiKeyProfileModel
                    {
                        Name = "a",
                        TemplateId = 7,
                        TemplateName = "ignored by detach",
                        ChannelIDs = [1],
                        ChannelTags = ["x"],
                        ChannelTagsMatchMode = "any",
                        ModelIDs = ["m1"],
                    },
                ],
            });

        // The template link survives and the stored template name wins.
        Assert.Equal(
            """{"activeProfile":"a","profiles":[{"name":"a","templateID":7,"templateName":"Tpl","modelMappings":null,"loadBalanceStrategy":"default","traceStickyMode":"default","channelIDs":[1],"channelTags":["x"],"channelTagsMatchMode":"any","modelIDs":["m1"]}]}""",
            unchanged.Profiles);

        var edited = await fixture.Service.UpdateProfilesAsync(
            key.Id,
            new ApiKeyProfilesModel
            {
                ActiveProfile = "a",
                Profiles =
                [
                    new ApiKeyProfileModel
                    {
                        Name = "a",
                        TemplateId = 7,
                        TemplateName = "Tpl",
                        ChannelIDs = [2],
                        ChannelTags = ["x"],
                        ChannelTagsMatchMode = "any",
                        ModelIDs = ["m1"],
                    },
                ],
            });

        // A one-off edit detaches only that profile from its template.
        Assert.Equal(
            """{"activeProfile":"a","profiles":[{"name":"a","modelMappings":null,"loadBalanceStrategy":"default","traceStickyMode":"default","channelIDs":[2],"channelTags":["x"],"channelTagsMatchMode":"any","modelIDs":["m1"]}]}""",
            edited.Profiles);
    }

    [Fact]
    public async Task UpdateProfilesValidationRunsBeforeTheStoreWrite()
    {
        var fixture = new Fixture(isOwner: true);
        var key = fixture.Keys.Add(item => item.ProjectId = 12);
        var before = key.Profiles;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateProfilesAsync(
                key.Id,
                new ApiKeyProfilesModel
                {
                    ActiveProfile = "",
                    Profiles =
                    [
                        new ApiKeyProfileModel { Name = "a" },
                        new ApiKeyProfileModel { Name = "A" },
                    ],
                }));

        Assert.Equal("duplicate profile name: A", error.Message);
        Assert.Equal(before, key.Profiles);
    }

    [Fact]
    public async Task UpdateProfilesRejectsNoAuthKeysBeforeValidating()
    {
        var fixture = new Fixture(isOwner: true);
        var noauth = fixture.Keys.Add(item =>
        {
            item.Name = "no auth";
            item.Type = "noauth";
            item.ProjectId = 12;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateProfilesAsync(noauth.Id, new ApiKeyProfilesModel()));

        Assert.Equal("noauth type API key profiles cannot be updated", error.Message);
    }

    [Fact]
    public async Task RotateGeneratesANewKeyAndEnforcesPersonalOwnership()
    {
        var fixture = new Fixture(isOwner: true);
        var key = fixture.Keys.Add(item =>
        {
            item.Key = "ah-old";
            item.ProjectId = 12;
            item.UserId = fixture.UserId;
        });

        var rotated = await fixture.Service.RotateAsync(key.Id);

        Assert.StartsWith("ah-", rotated.Key, StringComparison.Ordinal);
        Assert.NotEqual("ah-old", rotated.Key);
        Assert.Matches("^[0-9a-f]{64}$", rotated.Key[3..]);
        Assert.Equal(key.Id, rotated.Id);

        var other = new Fixture(isOwner: false);
        other.Identity.ProjectId = 12;
        other.Users.AssignProject(other.UserId, 12, isOwner: true);
        var personal = other.Keys.Add(item =>
        {
            item.Name = "someone else's";
            item.Type = "personal";
            item.ProjectId = 12;
            item.UserId = 999;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            other.Service.RotateAsync(personal.Id));
        Assert.Equal(
            "personal API key can only be rotated by its creator or a system owner",
            error.Message);
    }

    [Fact]
    public async Task BulkUpdatesValidateCountsNoauthAndPersonalOwnership()
    {
        var fixture = new Fixture(isOwner: true);
        var first = fixture.Keys.Add(item => item.ProjectId = 12);
        var second = fixture.Keys.Add(item => item.ProjectId = 12);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkDisableAsync([first.Id, 999]));
        Assert.Equal("expected to find 2 API keys, but found 1", missing.Message);

        var noauth = fixture.Keys.Add(item =>
        {
            item.Name = "no auth";
            item.Type = "noauth";
            item.ProjectId = 12;
        });
        var noauthError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkDisableAsync([first.Id, noauth.Id]));
        Assert.Equal("noauth type API key cannot be bulk disabled", noauthError.Message);

        await fixture.Service.BulkArchiveAsync([second.Id]);
        var archived = await fixture.Keys.GetByIdAsync(second.Id);
        Assert.NotNull(archived);
        Assert.Equal("archived", archived.Status);
        Assert.Equal(0, archived.DeletedAt);
    }

    [Fact]
    public async Task BulkUpdateQuotesThePersonalKeyNameInTheOwnershipError()
    {
        var fixture = new Fixture(isOwner: false);
        fixture.Identity.ProjectId = 12;
        fixture.Users.AssignProject(fixture.UserId, 12, isOwner: true);
        var personal = fixture.Keys.Add(item =>
        {
            item.Name = "someone else's";
            item.Type = "personal";
            item.ProjectId = 12;
            item.UserId = 999;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkDisableAsync([personal.Id]));

        Assert.Equal(
            "personal API key \"someone else's\" can only be disabled by its creator or a system owner",
            error.Message);
        Assert.Equal("enabled", personal.Status);
    }

    [Fact]
    public async Task BulkUpdateWithNoIdsIsANoOp()
    {
        var fixture = new Fixture(isOwner: false);

        await fixture.Service.BulkArchiveAsync([]);

        Assert.Empty(fixture.Keys.Rows);
    }

    [Fact]
    public async Task ListAndNodeRequireReadableApiKeys()
    {
        var owner = new Fixture(isOwner: true);
        owner.Keys.Add(item => item.ProjectId = 12);
        owner.Keys.Add(item => item.ProjectId = 12);

        var page = await owner.Service.ListAsync(new ApiKeyListQuery { First = 10 });
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Nodes.Count);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owner.Service.GetNodeAsync(999));
        Assert.Equal("ent: api_key not found", missing.Message);

        // A user without read_api_keys and no project context is denied by the
        // Query policy before the store is touched.
        var outsider = new Fixture(isOwner: false);
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(() =>
            outsider.Service.ListAsync(new ApiKeyListQuery { First = 10 }));
        Assert.Equal("ent/privacy: deny rule", denied.Message);
    }

    private sealed class Fixture
    {
        public FakeApiKeyStore Keys { get; } = new();

        public FakeProjectStore Projects { get; } = new();

        public FakeUserAdminStore Users { get; } = new();

        public FakeRoleStore Roles { get; } = new();

        public FakeManagementIdentity Identity { get; }

        public ApiKeyManagementService Service { get; }

        public long UserId { get; }

        public Fixture(bool isOwner, IReadOnlyList<string>? scopes = null)
        {
            Users.RolesSource = Roles;
            UserId = Users.CreateAsync(
                new UserRecord
                {
                    Email = "key-admin@example.com",
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
            var validator = new PermissionValidator(Identity, Users, Roles);
            Service = new ApiKeyManagementService(
                Keys,
                Identity,
                validator,
                new ApiKeyPrivacy(Identity, Users, Projects),
                new ApiKeyAuthOptions(false, [], "ah"));
        }
    }
}
