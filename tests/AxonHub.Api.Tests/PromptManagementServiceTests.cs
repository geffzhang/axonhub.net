using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Prompts;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.PromptService's management surface
/// (internal/server/biz/prompt.go): the project requirement, settings
/// validation order, duplicate-name coded error, the privacy decisions of the
/// Prompt schema (no API-key rule), the soft-delete behaviour of the ent
/// SoftDeleteMixin and the exact reference error strings.
/// </summary>
public sealed class PromptManagementServiceTests
{
    /// <summary>Matches PromptRecord's schema default project id.</summary>
    private const long ProjectId = 1;

    [Fact]
    public async Task CreateAppliesSchemaDefaultsAndSerializesSettings()
    {
        var fixture = new Fixture(isOwner: true);

        var created = await fixture.Service.CreateAsync(new CreatePromptCommand
        {
            Name = "greeting",
            Role = "system",
            Content = "Hello",
            Settings = new PromptSettings
            {
                Action = new PromptAction { Type = PromptActionTypes.Append },
                Conditions =
                [
                    new PromptActivationConditionComposite
                    {
                        Conditions =
                        [
                            new PromptActivationCondition
                            {
                                Type = PromptActivationConditionTypes.ModelId,
                                ModelId = "gpt-4o",
                            },
                        ],
                    },
                ],
            },
        });

        Assert.Equal(ProjectId, created.ProjectId);
        Assert.Equal("", created.Description);
        Assert.Equal("disabled", created.Status);
        Assert.Equal(0, created.Order);
        Assert.Equal(
            """{"action":{"type":"append"},"conditions":[{"conditions":[{"type":"model_id","model_id":"gpt-4o"}]}]}""",
            created.Settings);
        Assert.Same(created, Assert.Single(fixture.Prompts.Rows));
    }

    [Fact]
    public async Task CreateRequiresAProjectInContext()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Identity.ProjectId = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(MinimalCommand("greeting")));

        Assert.Equal("project id not found in context", error.Message);
    }

    [Fact]
    public async Task CreateValidatesSettingsBeforeTheDuplicateCheck()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Prompts.Seed(record => record.Name = "greeting");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(MinimalCommand("greeting") with
            {
                Settings = new PromptSettings
                {
                    Action = new PromptAction { Type = PromptActionTypes.Append },
                    Conditions =
                    [
                        new PromptActivationConditionComposite
                        {
                            Conditions =
                            [
                                new PromptActivationCondition
                                {
                                    Type = PromptActivationConditionTypes.ModelPattern,
                                    ModelPattern = "[",
                                },
                            ],
                        },
                    ],
                },
            }));

        Assert.Equal(
            "invalid regex pattern in model_pattern: invalid regex pattern: [",
            error.Message);
        Assert.Empty(fixture.Prompts.Queries);
    }

    [Fact]
    public async Task CreateDuplicateNameThrowsTheCodedError()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Prompts.Seed(record => record.Name = "greeting");

        var error = await Assert.ThrowsAsync<CodedException>(() =>
            fixture.Service.CreateAsync(MinimalCommand("greeting")));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("prompt name 'greeting' already exists", error.Message);
        Assert.Equal("prompt", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("greeting", error.Extensions["value"]);
        Assert.Single(fixture.Prompts.Rows);
    }

    [Fact]
    public async Task CreateWrapsTheDuplicateCheckStoreFailure()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Prompts.ExistsFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(MinimalCommand("greeting")));

        Assert.Equal(
            "failed to check prompt name uniqueness: boom",
            error.Message);
    }

    [Fact]
    public async Task ReadOnlyUserCannotCreateAndTheDenyIsWrapped()
    {
        // read_prompts passes the query policy, write_prompts is missing.
        var fixture = new Fixture(isOwner: false, scopes: ["read_prompts"]);
        fixture.Identity.ProjectId = ProjectId;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateAsync(MinimalCommand("greeting")));

        Assert.Equal(
            "failed to create prompt: default deny: ent/privacy: deny rule",
            error.Message);
        Assert.Empty(fixture.Prompts.Rows);
    }

    [Fact]
    public async Task ProjectScopedReaderListsWithinTheContextProject()
    {
        var fixture = new Fixture(isOwner: false, scopes: ["read_prompts"]);
        fixture.Identity.ProjectId = ProjectId;
        fixture.Prompts.Seed(record =>
        {
            record.ProjectId = ProjectId;
            record.Name = "inside";
        });
        fixture.Prompts.Seed(record =>
        {
            record.ProjectId = 99;
            record.Name = "outside";
        });

        var page = await fixture.Service.ListAsync(new PromptListQuery { First = 10 });

        Assert.Equal(ProjectId, page.Nodes.Single().ProjectId);
        Assert.Equal(ProjectId, Assert.Single(fixture.Prompts.Queries).ProjectScope);
    }

    [Fact]
    public async Task UpdateReQueriesThroughTheQueryPolicy()
    {
        var fixture = new Fixture(isOwner: true);
        var prompt = fixture.Prompts.Seed(record => record.Name = "before");

        var updated = await fixture.Service.UpdateAsync(new UpdatePromptCommand
        {
            Id = prompt.Id,
            Name = "after",
        });

        Assert.Equal("after", updated.Name);
        Assert.Equal("after", prompt.Name);
    }

    [Fact]
    public async Task WriteOnlyProjectUserUpdatesButTheReQueryIsDenied()
    {
        // write_prompts passes the mutation policy; read_prompts is missing,
        // so the post-update re-query ends at the query policy's default deny.
        // The update omits Name so it does not reach the duplicate check's
        // query first.
        var fixture = new Fixture(isOwner: false, scopes: ["write_prompts"]);
        fixture.Identity.ProjectId = ProjectId;
        var prompt = fixture.Prompts.Seed(record =>
        {
            record.Name = "before";
            record.Content = "before-content";
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(new UpdatePromptCommand
            {
                Id = prompt.Id,
                Content = "after-content",
            }));

        Assert.Equal(
            "failed to query updated prompt: default deny: ent/privacy: deny rule",
            error.Message);
        // The write already committed, like the reference.
        Assert.Equal("after-content", prompt.Content);
    }

    [Fact]
    public async Task RenamingAsAWriteOnlyUserIsDeniedAtTheDuplicateCheck()
    {
        // The reference checks name uniqueness through the privacy query
        // before Save, so a write-only user never reaches the update.
        var fixture = new Fixture(isOwner: false, scopes: ["write_prompts"]);
        fixture.Identity.ProjectId = ProjectId;
        var prompt = fixture.Prompts.Seed(record => record.Name = "before");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(new UpdatePromptCommand
            {
                Id = prompt.Id,
                Name = "after",
            }));

        Assert.Equal(
            "failed to check prompt name uniqueness: default deny: ent/privacy: deny rule",
            error.Message);
        Assert.Equal("before", prompt.Name);
    }

    [Fact]
    public async Task UpdateOfAMissingPromptReportsNotInProject()
    {
        var fixture = new Fixture(isOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateAsync(new UpdatePromptCommand
            {
                Id = 404,
                Name = "after",
            }));

        Assert.Equal("prompt not found or not in project", error.Message);
    }

    [Fact]
    public async Task StatusUpdateOfASoftDeletedRowHitsItThenFailsTheReQuery()
    {
        // The aggregate status update has no deleted_at predicate: the row is
        // written, then the live-only re-query reports ent's not-found.
        var fixture = new Fixture(isOwner: true);
        var prompt = fixture.Prompts.Seed(record =>
        {
            record.Name = "gone";
            record.DeletedAt = 1700000000;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateStatusAsync(prompt.Id, PromptStatuses.Enabled));

        Assert.Equal(
            "failed to query updated prompt: ent: prompt not found",
            error.Message);
        Assert.Equal(PromptStatuses.Enabled, prompt.Status);
    }

    [Fact]
    public async Task StatusUpdateOfAMissingPromptReportsNotInProject()
    {
        var fixture = new Fixture(isOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateStatusAsync(404, PromptStatuses.Enabled));

        Assert.Equal("prompt not found or not in project", error.Message);
    }

    [Fact]
    public async Task DeleteSoftDeletesAndReportsAlreadyDeletedRows()
    {
        var fixture = new Fixture(isOwner: true);
        var prompt = fixture.Prompts.Seed(record => record.Name = "gone");

        await fixture.Service.DeleteAsync(prompt.Id);
        Assert.NotEqual(0, prompt.DeletedAt);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.DeleteAsync(prompt.Id));
        Assert.Equal("prompt not found or not in project", error.Message);
    }

    [Fact]
    public async Task BulkMutationsEvaluatePrivacyEvenWithEmptyIds()
    {
        var fixture = new Fixture(isOwner: false);
        fixture.Identity.ProjectId = ProjectId;

        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkDeleteAsync([]));
        Assert.Equal(
            "failed to bulk delete prompts: default deny: ent/privacy: deny rule",
            delete.Message);

        var enable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkEnableAsync([]));
        Assert.Equal(
            "failed to bulk enable prompts: default deny: ent/privacy: deny rule",
            enable.Message);

        var disable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkDisableAsync([]));
        Assert.Equal(
            "failed to bulk disable prompts: default deny: ent/privacy: deny rule",
            disable.Message);
    }

    [Fact]
    public async Task BulkMutationsApplyToEveryLiveRowOfTheProject()
    {
        var fixture = new Fixture(isOwner: true);
        var first = fixture.Prompts.Seed(record => record.Name = "first");
        var second = fixture.Prompts.Seed(record => record.Name = "second");
        var foreign = fixture.Prompts.Seed(record =>
        {
            record.Name = "foreign";
            record.ProjectId = 99;
        });

        await fixture.Service.BulkEnableAsync([first.Id, second.Id, foreign.Id]);
        Assert.Equal(PromptStatuses.Enabled, first.Status);
        Assert.Equal(PromptStatuses.Enabled, second.Status);
        Assert.Equal(PromptStatuses.Disabled, foreign.Status);

        await fixture.Service.BulkDisableAsync([first.Id]);
        Assert.Equal(PromptStatuses.Disabled, first.Status);

        await fixture.Service.BulkDeleteAsync([first.Id, second.Id]);
        Assert.NotEqual(0, first.DeletedAt);
        Assert.NotEqual(0, second.DeletedAt);
        Assert.Equal(0, foreign.DeletedAt);
    }

    [Fact]
    public async Task BulkFailureKeepsTheReferencePrefixes()
    {
        var fixture = new Fixture(isOwner: true);
        fixture.Prompts.BulkFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.BulkEnableAsync([1]));

        Assert.Equal("failed to bulk enable prompts: boom", error.Message);
    }

    [Fact]
    public async Task ApiKeyPrincipalIsDeniedByTheQueryPolicy()
    {
        // Prompt has no API-key rule: a service-account principal has no user
        // in context and every rule denies.
        var fixture = new Fixture(isOwner: true);
        fixture.Identity.User = null;

        var error = await Assert.ThrowsAsync<PrivacyDenyException>(() =>
            fixture.Service.GetNodeAsync(1));

        Assert.Equal(
            "no user in context: ent/privacy: deny rule",
            error.Message);
    }

    [Fact]
    public async Task NodeLookupAppliesTheProjectScope()
    {
        var fixture = new Fixture(isOwner: false, scopes: ["read_prompts"]);
        fixture.Identity.ProjectId = ProjectId;
        var prompt = fixture.Prompts.Seed(record => record.ProjectId = ProjectId);

        Assert.Equal(prompt.Id, (await fixture.Service.GetNodeAsync(prompt.Id)).Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.GetNodeAsync(404));
        Assert.Equal("ent: prompt not found", error.Message);
    }

    [Fact]
    public async Task TestPrincipalBypassesEveryRule()
    {
        var fixture = new Fixture(isOwner: false);
        fixture.Identity.User = new ManagementUser(7, IsOwner: false, IsTest: true);

        var created = await fixture.Service.CreateAsync(MinimalCommand("greeting"));

        Assert.Equal(ProjectId, created.ProjectId);
    }

    [Theory]
    [InlineData("model_pattern", null, null, null,
        "model_pattern is required when type is model_pattern")]
    [InlineData("model_id", null, null, null,
        "model_id is required when type is model_id")]
    [InlineData("api_key", null, null, null,
        "api_key_id is required when type is api_key")]
    [InlineData("api_key", null, null, 0L,
        "api_key_id must be greater than 0")]
    [InlineData("api_key", null, null, -3L,
        "api_key_id must be greater than 0")]
    public async Task ValidateSettingsUsesTheReferenceMessages(
        string type,
        string? modelId,
        string? modelPattern,
        long? apiKeyId,
        string expected)
    {
        var settings = new PromptSettings
        {
            Action = new PromptAction { Type = PromptActionTypes.Append },
            Conditions =
            [
                new PromptActivationConditionComposite
                {
                    Conditions =
                    [
                        new PromptActivationCondition
                        {
                            Type = type,
                            ModelId = modelId,
                            ModelPattern = modelPattern,
                            ApiKeyId = apiKeyId,
                        },
                    ],
                },
            ],
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new Fixture(isOwner: true).Service.CreateAsync(MinimalCommand("greeting") with
            {
                Settings = settings,
            }));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void ValidateSettingsIgnoresEmptyConditionLists()
    {
        PromptManagementService.ValidateSettings(new PromptSettings());
        PromptManagementService.ValidateSettings(new PromptSettings
        {
            Conditions = [],
        });
        PromptManagementService.ValidateSettings(new PromptSettings
        {
            Conditions = [new PromptActivationConditionComposite()],
        });
        PromptManagementService.ValidateSettings(new PromptSettings
        {
            Conditions = [new PromptActivationConditionComposite { Conditions = [] }],
        });
    }

    [Fact]
    public void SerializeKeepsOmitEmptySemantics()
    {
        Assert.Equal(
            """{"action":{"type":""}}""",
            PromptSettingsCodec.Serialize(new PromptSettings()));

        Assert.Equal(
            """{"action":{"type":"prepend"},"conditions":[{}]}""",
            PromptSettingsCodec.Serialize(new PromptSettings
            {
                Action = new PromptAction { Type = PromptActionTypes.Prepend },
                Conditions = [new PromptActivationConditionComposite()],
            }));

        // A pointer to an empty string is kept (omitempty does not drop it).
        Assert.Equal(
            """{"action":{"type":"append"},"conditions":[{"conditions":[{"type":"model_id","model_id":""}]}]}""",
            PromptSettingsCodec.Serialize(new PromptSettings
            {
                Action = new PromptAction { Type = PromptActionTypes.Append },
                Conditions =
                [
                    new PromptActivationConditionComposite
                    {
                        Conditions =
                        [
                            new PromptActivationCondition
                            {
                                Type = PromptActivationConditionTypes.ModelId,
                                ModelId = "",
                            },
                        ],
                    },
                ],
            }));
    }

    private static CreatePromptCommand MinimalCommand(string name) => new()
    {
        Name = name,
        Role = "system",
        Content = "content",
        Settings = new PromptSettings
        {
            Action = new PromptAction { Type = PromptActionTypes.Append },
        },
    };

    private sealed class Fixture
    {
        public FakePromptStore Prompts { get; } = new();

        public FakeUserAdminStore Users { get; } = new();

        public FakeRoleStore Roles { get; } = new();

        public FakeManagementIdentity Identity { get; }

        public PromptManagementService Service { get; }

        public Fixture(bool isOwner, IReadOnlyList<string>? scopes = null)
        {
            Users.RolesSource = Roles;
            var userId = Users.CreateAsync(
                new UserRecord
                {
                    Email = "prompt-admin@example.com",
                    Status = "activated",
                    IsOwner = isOwner,
                },
                roleIds: null).GetAwaiter().GetResult();
            if (scopes is not null)
            {
                Users.AssignProject(userId, ProjectId, scopes: scopes);
            }

            Identity = new FakeManagementIdentity(new ManagementUser(userId, isOwner))
            {
                ProjectId = ProjectId,
            };
            Service = new PromptManagementService(
                Prompts,
                new PromptPrivacy(Identity, Users),
                Identity);
        }
    }
}
