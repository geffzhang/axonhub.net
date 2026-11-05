using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.ChannelOverrideTemplateService and the resolvers' user handling
/// (internal/server/biz/channel_override_template.go:40-311,
/// internal/server/gql/axonhub.resolvers.go:644-708): validation prefixes,
/// duplicate-name errors, privacy denials, the merge/replace settings writes,
/// and the rollback-before-reload contract.
/// </summary>
public sealed class ChannelOverrideTemplateServiceTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly FakeChannelOverrideTemplateStore _store = new();
    private readonly FakeManagementIdentity _identity = new(new ManagementUser(9, IsOwner: false));
    private readonly RecordingModelSyncReloadHook _reload = new();

    private ChannelOverrideTemplateService Service => new(_store, _identity, _reload);

    // ---------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CreateValidatesHeaderOperationsBeforeBodyOnes()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.CreateAsync(new CreateChannelOverrideTemplateCommand
            {
                Name = "Template",
                HeaderOverrideOperations = [new OverrideOperationDraft { Op = "set" }],
                BodyOverrideOperations = [new OverrideOperationDraft { Op = "mystery" }],
            }));

        Assert.Equal(
            "invalid header override operations: "
            + "header operation at index 0 (set) has an empty path",
            error.Message);
        Assert.Empty(_store.Templates);
    }

    [Fact]
    public async Task CreateValidatesBodyOperations()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.CreateAsync(new CreateChannelOverrideTemplateCommand
            {
                Name = "Template",
                BodyOverrideOperations = [new OverrideOperationDraft { Op = "mystery" }],
            }));

        Assert.Equal(
            "invalid body override operations: "
            + "body operation at index 0 has unknown op \"mystery\"",
            error.Message);
    }

    [Fact]
    public async Task CreateWithoutAUserFailsLikeTheResolver()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.CreateAsync(new CreateChannelOverrideTemplateCommand { Name = "Template" }));

        Assert.Equal("user not found in context", error.Message);
        Assert.Empty(_store.Templates);
    }

    [Fact]
    public async Task CreateStoresJsonNullForAbsentOperationLists()
    {
        var created = await Service.CreateAsync(new CreateChannelOverrideTemplateCommand
        {
            Name = "Headers",
        });

        // Ent's builder always sets both operation columns, so the nil slices
        // override the schema defaults with jsonb 'null'.
        Assert.Equal("null", created.HeaderOverrideOperations);
        Assert.Equal("null", created.BodyOverrideOperations);
        Assert.Equal("{}", created.OverrideParameters);
        Assert.Equal("[]", created.OverrideHeaders);
        Assert.Equal(9, created.UserId);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.True(created.Id > 0);
    }

    [Fact]
    public async Task CreateMarshalsOperationListsInTheReferenceShape()
    {
        var created = await Service.CreateAsync(new CreateChannelOverrideTemplateCommand
        {
            Name = "Operations",
            HeaderOverrideOperations =
            [
                new OverrideOperationDraft { Op = "set", Path = "x-key", Value = "v" },
            ],
            BodyOverrideOperations =
            [
                new OverrideOperationDraft { Op = "delete", Path = "top_p" },
                new OverrideOperationDraft
                {
                    Op = "rename",
                    From = "a",
                    To = "b",
                    Condition = "{{if .Model}}true{{end}}",
                },
            ],
        });

        Assert.Equal(
            """[{"op":"set","path":"x-key","value":"v"}]""",
            created.HeaderOverrideOperations);
        Assert.Equal(
            """[{"op":"delete","path":"top_p"},{"op":"rename","from":"a","to":"b","condition":"{{if .Model}}true{{end}}"}]""",
            created.BodyOverrideOperations);
    }

    [Fact]
    public async Task CreateSurfacesAUniqueIndexAsDuplicateName()
    {
        _store.Templates.Add(Template(id: 1, userId: 9, name: "Template"));

        var error = await Assert.ThrowsAsync<CodedException>(() =>
            Service.CreateAsync(new CreateChannelOverrideTemplateCommand { Name = "Template" }));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("Template name 'Template' already exists", error.Message);
        Assert.Equal("Template", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("Template", error.Extensions["value"]);
    }

    [Fact]
    public async Task CreatePrefixesStoreFailures()
    {
        _store.CreateFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.CreateAsync(new CreateChannelOverrideTemplateCommand { Name = "Template" }));

        Assert.Equal("failed to create channel override template: boom", error.Message);
    }

    // ---------------------------------------------------------------------
    // Update
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateUsesADifferentPrefixForAppendedOperationLists()
    {
        var header = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
            {
                Id = 1,
                AppendHeaderOverrideOperations = [new OverrideOperationDraft { Op = "set" }],
            }));
        Assert.Equal(
            "invalid header override operations to append: "
            + "header operation at index 0 (set) has an empty path",
            header.Message);

        var body = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
            {
                Id = 1,
                AppendBodyOverrideOperations = [new OverrideOperationDraft { Op = "mystery" }],
            }));
        Assert.Equal(
            "invalid body override operations to append: "
            + "body operation at index 0 has unknown op \"mystery\"",
            body.Message);

        var setBody = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
            {
                Id = 1,
                BodyOverrideOperations = [new OverrideOperationDraft { Op = "set" }],
            }));
        Assert.Equal(
            "invalid body override operations: "
            + "body operation at index 0 (set) has an empty path",
            setBody.Message);
    }

    [Fact]
    public async Task UpdateWithoutAUserIsDeniedByThePrivacyRule()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand { Id = 1, Name = "x" }));

        Assert.Equal(
            "failed to update channel override template: "
            + "no user in context: ent/privacy: deny rule",
            error.Message);
        Assert.Null(_store.LastUpdate);
    }

    [Fact]
    public async Task UpdateOfAnInvisibleRowIsNotFound()
    {
        // UserOwnedMutationRule scopes the update to user_id = current.
        _store.Templates.Add(Template(id: 1, userId: 8));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand { Id = 1, Name = "x" }));

        Assert.Equal(
            "failed to update channel override template: "
            + "ent: channel_override_template not found",
            error.Message);
    }

    [Fact]
    public async Task UpdatePassesSetAppendAndClearThroughToTheStore()
    {
        _store.Templates.Add(Template(id: 1, userId: 9));

        var updated = await Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
        {
            Id = 1,
            Name = "Renamed",
            Description = "desc",
            HeaderOverrideOperations =
            [
                new OverrideOperationDraft { Op = "set", Path = "a", Value = "1" },
            ],
            // An empty append never reaches the store: ent's Append is a no-op
            // when ok is false.
            AppendHeaderOverrideOperations = [],
            BodyOverrideOperations =
            [
                new OverrideOperationDraft { Op = "set", Path = "b", Value = "2" },
            ],
            AppendBodyOverrideOperations = [],
        });

        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("desc", updated.Description);
        Assert.Equal("""[{"op":"set","path":"a","value":"1"}]""", updated.HeaderOverrideOperations);
        Assert.Equal("""[{"op":"set","path":"b","value":"2"}]""", updated.BodyOverrideOperations);

        Assert.NotNull(_store.LastUpdate);
        Assert.Null(_store.LastUpdate.AppendHeaderOverrideOperations);
        Assert.Null(_store.LastUpdate.AppendBodyOverrideOperations);
        Assert.Equal(9, _store.LastUpdate.ScopeUserId);
    }

    [Fact]
    public async Task UpdateClearDescriptionWinsOverASimultaneousDescription()
    {
        _store.Templates.Add(Template(id: 1, userId: 9));
        _store.Templates[0].Description = "old";

        var updated = await Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
        {
            Id = 1,
            ClearDescription = true,
            Description = "ignored",
        });

        Assert.Null(updated.Description);
    }

    [Fact]
    public async Task UpdateAppendsToTheStoredOperations()
    {
        _store.Templates.Add(Template(id: 1, userId: 9));
        _store.Templates[0].HeaderOverrideOperations = """[{"op":"set","path":"a","value":"1"}]""";

        var updated = await Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
        {
            Id = 1,
            AppendHeaderOverrideOperations =
            [
                new OverrideOperationDraft { Op = "delete", Path = "b" },
            ],
        });

        Assert.Equal(
            """[{"op":"set","path":"a","value":"1"},{"op":"delete","path":"b"}]""",
            updated.HeaderOverrideOperations);
    }

    [Fact]
    public async Task UpdateByTheOwnerIsUnscoped()
    {
        _identity.User = new ManagementUser(1, IsOwner: true);
        _store.Templates.Add(Template(id: 1, userId: 8));

        var updated = await Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
        {
            Id = 1,
            Name = "Renamed",
        });

        Assert.Equal("Renamed", updated.Name);
        Assert.Null(_store.LastUpdate!.ScopeUserId);
    }

    [Fact]
    public async Task ARenameOntoATakenNameIsDuplicateName()
    {
        _store.Templates.Add(Template(id: 1, userId: 9, name: "First"));
        _store.Templates.Add(Template(id: 2, userId: 9, name: "Second"));

        var error = await Assert.ThrowsAsync<CodedException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand
            {
                Id = 1,
                Name = "Second",
            }));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("Template name 'Second' already exists", error.Message);
    }

    [Fact]
    public async Task UpdatePrefixesStoreFailures()
    {
        _store.Templates.Add(Template(id: 1, userId: 9));
        _store.UpdateFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.UpdateAsync(new UpdateChannelOverrideTemplateCommand { Id = 1, Name = "x" }));

        Assert.Equal("failed to update channel override template: boom", error.Message);
    }

    // ---------------------------------------------------------------------
    // Delete / Get
    // ---------------------------------------------------------------------

    [Fact]
    public async Task DeleteWithoutAUserIsDenied()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.DeleteAsync(1));

        Assert.Equal(
            "failed to delete channel override template: "
            + "no user in context: ent/privacy: deny rule",
            error.Message);
    }

    [Fact]
    public async Task DeleteOfAnInvisibleRowIsNotFound()
    {
        _store.Templates.Add(Template(id: 1, userId: 8));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.DeleteAsync(1));

        Assert.Equal(
            "failed to delete channel override template: "
            + "ent: channel_override_template not found",
            error.Message);
    }

    [Fact]
    public async Task DeleteSoftDeletesForTheOwningUser()
    {
        _store.Templates.Add(Template(id: 1, userId: 9));

        await Service.DeleteAsync(1);

        Assert.NotEqual(0, _store.Templates[0].DeletedAt);
        Assert.Null(await _store.GetAsync(1, scopeUserId: null));
    }

    [Fact]
    public async Task DeletePrefixesStoreFailures()
    {
        _store.Templates.Add(Template(id: 1, userId: 9));
        _store.DeleteFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.DeleteAsync(1));

        Assert.Equal("failed to delete channel override template: boom", error.Message);
    }

    [Fact]
    public async Task GetWithoutAUserIsDenied()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.GetAsync(1));

        Assert.Equal(
            "failed to get channel override template: "
            + "no user in context: ent/privacy: deny rule",
            error.Message);
    }

    [Fact]
    public async Task GetOfAnInvisibleRowIsNotFound()
    {
        _store.Templates.Add(Template(id: 1, userId: 8));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.GetAsync(1));

        Assert.Equal(
            "failed to get channel override template: "
            + "ent: channel_override_template not found",
            error.Message);
    }

    [Fact]
    public async Task GetPrefixesStoreFailures()
    {
        _store.GetFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.GetAsync(1));

        Assert.Equal("failed to get channel override template: boom", error.Message);
    }

    // ---------------------------------------------------------------------
    // List
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ListWithoutAUserIsDenied()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ListAsync(new ChannelOverrideTemplateListQuery()));

        Assert.Equal("no user in context: ent/privacy: deny rule", error.Message);
        Assert.Empty(_store.Queries);
    }

    [Fact]
    public async Task ListScopesANonOwnerToTheirOwnRows()
    {
        await Service.ListAsync(new ChannelOverrideTemplateListQuery { First = 5 });

        var query = Assert.Single(_store.Queries);
        Assert.Equal(9, query.ScopeUserId);
        Assert.Equal(5, query.First);
    }

    [Fact]
    public async Task ListLeavesTheOwnerUnscoped()
    {
        _identity.User = new ManagementUser(1, IsOwner: true);

        await Service.ListAsync(new ChannelOverrideTemplateListQuery());

        Assert.Null(Assert.Single(_store.Queries).ScopeUserId);
    }

    // ---------------------------------------------------------------------
    // ApplyTemplate
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ApplyReplaceUsesTemplateOperationsAndClearsLegacyFields()
    {
        _store.Templates.Add(Template(
            id: 1,
            headerOverrideOperations: """[{"op":"set","path":"x","value":"t"}]""",
            bodyOverrideOperations: """[{"op":"set","path":"b","value":"2"}]"""));
        _store.Channels.Add(Channel(10, "Alpha",
            """
            {
              "modelMappings": [{"from": "a", "to": "b"}],
              "hideOriginalModels": true,
              "overrideParameters": "{\"a\":\"1\"}",
              "overrideHeaders": [{"key": "x", "value": "legacy"}]
            }
            """));

        var result = await Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
        {
            TemplateId = 1,
            ChannelIds = [10],
            Mode = ChannelOverrideApplyMode.Replace,
        });

        Assert.True(result.Success);
        Assert.Equal(1, result.Updated);
        Assert.Single(result.Channels);
        Assert.Equal(1, _reload.Count);

        var settings = ParseJson(Assert.Single(_store.Writes).SettingsJson);
        Assert.Equal(
            """[{"op":"set","path":"x","value":"t"}]""",
            settings.GetProperty("headerOverrideOperations").GetRawText());
        Assert.Equal(
            """[{"op":"set","path":"b","value":"2"}]""",
            settings.GetProperty("bodyOverrideOperations").GetRawText());
        Assert.Equal("", settings.GetProperty("overrideParameters").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("overrideHeaders").ValueKind);

        // Settings outside the override fields survive the rewrite.
        Assert.Equal(1, settings.GetProperty("modelMappings").GetArrayLength());
        Assert.True(settings.GetProperty("hideOriginalModels").GetBoolean());
    }

    [Fact]
    public async Task ApplyMergeCombinesLegacyFieldsWithTemplateOperations()
    {
        _store.Templates.Add(Template(
            id: 1,
            headerOverrideOperations: """[{"op":"set","path":"x-key","value":"new"}]""",
            bodyOverrideOperations: """[{"op":"set","path":"temperature","value":"0.7"}]"""));
        _store.Channels.Add(Channel(10, "Alpha",
            """
            {
              "overrideHeaders": [
                {"key": "X-Key", "value": "old"},
                {"key": "other", "value": "keep"}
              ],
              "overrideParameters": "{\"temperature\":\"0.5\",\"remove\":\"__AXONHUB_CLEAR__\"}"
            }
            """));

        await Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
        {
            TemplateId = 1,
            ChannelIds = [10],
        });

        var settings = ParseJson(Assert.Single(_store.Writes).SettingsJson);

        // Headers compare case-insensitively and the template operation
        // replaces the first matching set/delete in place.
        Assert.Equal(
            """[{"op":"set","path":"x-key","value":"new"},{"op":"set","path":"other","value":"keep"}]""",
            settings.GetProperty("headerOverrideOperations").GetRawText());

        // Body operations replace every operation at the template's path and
        // the replacement is emitted at the first existing position.
        Assert.Equal(
            """[{"op":"set","path":"temperature","value":"0.7"},{"op":"delete","path":"remove"}]""",
            settings.GetProperty("bodyOverrideOperations").GetRawText());
    }

    [Fact]
    public async Task ApplyMergePrefersTheNewFieldsEvenWhenTheyAreEmpty()
    {
        _store.Templates.Add(Template(id: 1));
        _store.Channels.Add(Channel(10, "Alpha",
            """
            {
              "headerOverrideOperations": [],
              "bodyOverrideOperations": [],
              "overrideHeaders": [{"key": "legacy", "value": "1"}],
              "overrideParameters": "{\"legacy\":\"1\"}"
            }
            """));

        await Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
        {
            TemplateId = 1,
            ChannelIds = [10],
        });

        var settings = ParseJson(Assert.Single(_store.Writes).SettingsJson);

        // An empty operation list is dropped by omitempty, and the legacy
        // fields are cleared unconditionally.
        Assert.False(
            settings.TryGetProperty("headerOverrideOperations", out _),
            settings.GetRawText());
        Assert.False(
            settings.TryGetProperty("bodyOverrideOperations", out _),
            settings.GetRawText());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("overrideHeaders").ValueKind);
        Assert.Equal("", settings.GetProperty("overrideParameters").GetString());
    }

    [Fact]
    public async Task ApplyWithoutAUserIsAFetchFailure()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10],
            }));

        Assert.Equal(
            "failed to fetch template: no user in context: ent/privacy: deny rule",
            error.Message);
    }

    [Fact]
    public async Task ApplyOfAnInvisibleTemplateIsNotFound()
    {
        _store.Templates.Add(Template(id: 1, userId: 8));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10],
            }));

        Assert.Equal(
            "failed to fetch template: ent: channel_override_template not found",
            error.Message);
    }

    [Fact]
    public async Task ApplyParsesTemplateOperationsWhileFetching()
    {
        _store.Templates.Add(Template(id: 1, headerOverrideOperations: "{bad"));
        _store.Channels.Add(Channel(10, "Alpha"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10],
            }));

        Assert.StartsWith("failed to fetch template: invalid override parameters: ", error.Message);
        Assert.Empty(_store.ChannelQueries);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task ApplyRejectsAMissingChannel()
    {
        _store.Templates.Add(Template(id: 1));
        _store.Channels.Add(Channel(10, "Alpha"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10, 11],
            }));

        Assert.Equal("some channels not found for provided IDs", error.Message);
        Assert.Empty(_store.Writes);
        Assert.Equal(0, _reload.Count);
    }

    [Fact]
    public async Task ApplyRejectsDuplicateChannelIdsThroughTheCountCheck()
    {
        // Ent's IDIn collapses duplicates, so two identical ids fetch one row
        // and the count check fails.
        _store.Templates.Add(Template(id: 1));
        _store.Channels.Add(Channel(10, "Alpha"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10, 10],
            }));

        Assert.Equal("some channels not found for provided IDs", error.Message);
    }

    [Fact]
    public async Task ApplyWithNoChannelsStillCommitsAndReloads()
    {
        _store.Templates.Add(Template(id: 1));

        var result = await Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
        {
            TemplateId = 1,
            ChannelIds = [],
        });

        Assert.True(result.Success);
        Assert.Equal(0, result.Updated);
        Assert.Empty(_store.Writes);
        Assert.Equal(1, _reload.Count);
    }

    [Fact]
    public async Task ApplySurfacesMalformedChannelSettingsAsAQueryFailure()
    {
        _store.Templates.Add(Template(id: 1));
        _store.Channels.Add(Channel(10, "Alpha", settings: "{"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10],
            }));

        Assert.StartsWith("failed to query channels: ", error.Message);
        Assert.Empty(_store.Writes);
        Assert.Equal(0, _reload.Count);
    }

    [Fact]
    public async Task ApplyPropagatesTheTransactionFailureWithoutReloading()
    {
        _store.Templates.Add(Template(id: 1));
        _store.Channels.Add(Channel(10, "Alpha"));
        _store.UpdateChannelSettingsFailure = new StoreFailureException(
            "failed to update channel Ghost: ent: channel not found",
            new InvalidOperationException("inner"));

        var error = await Assert.ThrowsAsync<StoreFailureException>(() =>
            Service.ApplyAsync(new ApplyChannelOverrideTemplateCommand
            {
                TemplateId = 1,
                ChannelIds = [10],
            }));

        Assert.Equal(
            "failed to update channel Ghost: ent: channel not found",
            error.Message);
        Assert.Equal(0, _reload.Count);
    }

    // ---------------------------------------------------------------------
    // ClearTemplates
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ClearNormalizesEveryOverrideField()
    {
        _store.Channels.Add(Channel(10, "Alpha",
            """
            {
              "modelMappings": [{"from": "a", "to": "b"}],
              "headerOverrideOperations": [{"op":"set","path":"x","value":"1"}],
              "bodyOverrideOperations": [{"op":"set","path":"y","value":"2"}],
              "overrideHeaders": [{"key": "x", "value": "1"}],
              "overrideParameters": "{\"y\":\"2\"}"
            }
            """));

        var result = await Service.ClearAsync([10]);

        Assert.True(result.Success);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, _reload.Count);

        var settings = ParseJson(Assert.Single(_store.Writes).SettingsJson);
        Assert.False(settings.TryGetProperty("headerOverrideOperations", out _));
        Assert.False(settings.TryGetProperty("bodyOverrideOperations", out _));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("overrideHeaders").ValueKind);
        Assert.Equal("", settings.GetProperty("overrideParameters").GetString());
        Assert.Equal(1, settings.GetProperty("modelMappings").GetArrayLength());
    }

    [Fact]
    public async Task ClearRejectsAMissingChannel()
    {
        _store.Channels.Add(Channel(10, "Alpha"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ClearAsync([10, 11]));

        Assert.Equal("some channels not found for provided IDs", error.Message);
        Assert.Empty(_store.Writes);
        Assert.Equal(0, _reload.Count);
    }

    [Fact]
    public async Task ClearWithNoChannelsStillCommitsAndReloads()
    {
        var result = await Service.ClearAsync([]);

        Assert.True(result.Success);
        Assert.Equal(0, result.Updated);
        Assert.Empty(_store.Writes);
        Assert.Equal(1, _reload.Count);
    }

    [Fact]
    public async Task ClearSurfacesMalformedChannelSettingsAsAQueryFailure()
    {
        _store.Channels.Add(Channel(10, "Alpha", settings: "{"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service.ClearAsync([10]));

        Assert.StartsWith("failed to query channels: ", error.Message);
        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task ClearPropagatesTheTransactionFailureWithoutReloading()
    {
        _store.Channels.Add(Channel(10, "Alpha"));
        _store.UpdateChannelSettingsFailure = new StoreFailureException(
            "failed to update channel Alpha: ent: channel not found",
            new InvalidOperationException("inner"));

        var error = await Assert.ThrowsAsync<StoreFailureException>(() =>
            Service.ClearAsync([10]));

        Assert.Equal(
            "failed to update channel Alpha: ent: channel not found",
            error.Message);
        Assert.Equal(0, _reload.Count);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static JsonElement ParseJson(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static ChannelOverrideTemplateRecord Template(
        long id,
        long? userId = 9,
        string name = "Template",
        string? headerOverrideOperations = "null",
        string? bodyOverrideOperations = "null") => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt.AddMinutes(1),
        DeletedAt = 0,
        Name = name,
        Description = null,
        OverrideParameters = "{}",
        OverrideHeaders = "[]",
        HeaderOverrideOperations = headerOverrideOperations,
        BodyOverrideOperations = bodyOverrideOperations,
        UserId = userId,
    };

    private static ChannelRecord Channel(
        long id,
        string name,
        string? settings = """{"modelMappings":[]}""") => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        DeletedAt = 0,
        Type = "openai",
        Name = name,
        Status = "enabled",
        Credentials = "{}",
        DisabledApiKeys = "[]",
        SupportedModels = "null",
        Tags = "[]",
        Policies = """{"stream":"unlimited"}""",
        Settings = settings,
        Endpoints = "[]",
    };
}

/// <summary>In-memory store with the ent predicates the service relies on.</summary>
internal sealed class FakeChannelOverrideTemplateStore : IChannelOverrideTemplateStore
{
    private long _nextId = 100;

    public List<ChannelOverrideTemplateRecord> Templates { get; } = [];

    public List<ChannelRecord> Channels { get; } = [];

    public List<ChannelOverrideTemplateListQuery> Queries { get; } = [];

    public List<IReadOnlyList<long>> ChannelQueries { get; } = [];

    public List<ChannelSettingsWrite> Writes { get; } = [];

    public ChannelOverrideTemplateUpdate? LastUpdate { get; private set; }

    public Exception? GetFailure { get; set; }

    public Exception? CreateFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? UpdateChannelSettingsFailure { get; set; }

    public Task<ChannelOverrideTemplateRecord?> GetAsync(
        long id,
        long? scopeUserId,
        CancellationToken cancellationToken = default)
    {
        if (GetFailure is not null)
        {
            throw GetFailure;
        }

        return Task.FromResult(Live(id, scopeUserId));
    }

    public Task<ListPage<ChannelOverrideTemplateRecord>> ListAsync(
        ChannelOverrideTemplateListQuery query,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);

        // Mirrors the store: soft-delete/privacy/where predicates, entgql's
        // Last-direction flip, tuple cursors, then LIMIT first+1 / last+1 for
        // the page flags. totalCount ignores cursors, as COUNT(*) does.
        var rows = Templates
            .Where(row => row.DeletedAt == 0)
            .Where(row => query.ScopeUserId is null || row.UserId == query.ScopeUserId)
            .Where(row => Matches(row, query.Where))
            .ToList();

        var totalCount = rows.Count;
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        if (query.After is not null)
        {
            rows = rows
                .Where(row => Beyond(row, query.After, query.SortField, direction))
                .ToList();
        }

        if (query.Before is not null)
        {
            var opposite = direction == SortDirection.Asc
                ? SortDirection.Desc
                : SortDirection.Asc;
            rows = rows
                .Where(row => Beyond(row, query.Before, query.SortField, opposite))
                .ToList();
        }

        rows.Sort((left, right) =>
        {
            var compare = Compare(left, right, query.SortField);
            return direction == SortDirection.Asc ? compare : -compare;
        });

        var hasNextPage = query.Before is not null;
        var hasPreviousPage = query.After is not null;

        var limit = query.First is not null
            ? query.First.Value + 1
            : query.Last is not null ? query.Last.Value + 1 : (int?)null;
        if (limit is not null && rows.Count > limit)
        {
            rows.RemoveRange(limit.Value, rows.Count - limit.Value);
        }

        if (query.First is not null && rows.Count == query.First + 1)
        {
            hasNextPage = true;
            rows.RemoveAt(rows.Count - 1);
        }
        else if (query.Last is not null && rows.Count == query.Last + 1)
        {
            hasPreviousPage = true;
            rows.RemoveAt(rows.Count - 1);
        }

        if (query.Last is not null)
        {
            rows.Reverse();
        }

        return Task.FromResult(new ListPage<ChannelOverrideTemplateRecord>(
            rows,
            hasNextPage,
            hasPreviousPage,
            totalCount));
    }

    public Task<ChannelOverrideTemplateRecord> CreateAsync(
        ChannelOverrideTemplateRecord template,
        CancellationToken cancellationToken = default)
    {
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        if (Templates.Any(row =>
            row.DeletedAt == 0
            && row.UserId == template.UserId
            && row.Name == template.Name))
        {
            throw new ChannelOverrideTemplateNameConflictException(
                new InvalidOperationException("duplicate key"));
        }

        template.Id = _nextId++;
        Templates.Add(template);
        return Task.FromResult(template);
    }

    public Task<ChannelOverrideTemplateRecord?> UpdateAsync(
        ChannelOverrideTemplateUpdate update,
        CancellationToken cancellationToken = default)
    {
        LastUpdate = update;

        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        var row = Live(update.Id, update.ScopeUserId);
        if (row is null)
        {
            return Task.FromResult<ChannelOverrideTemplateRecord?>(null);
        }

        if (update.Name is not null
            && Templates.Any(other =>
                other.Id != row.Id
                && other.DeletedAt == 0
                && other.UserId == row.UserId
                && other.Name == update.Name))
        {
            throw new ChannelOverrideTemplateNameConflictException(
                new InvalidOperationException("duplicate key"));
        }

        if (update.Name is not null)
        {
            row.Name = update.Name;
        }

        if (update.ClearDescription)
        {
            row.Description = null;
        }
        else if (update.Description is not null)
        {
            row.Description = update.Description;
        }

        if (update.HeaderOverrideOperations is not null)
        {
            row.HeaderOverrideOperations = update.HeaderOverrideOperations;
        }

        row.HeaderOverrideOperations = Append(
            row.HeaderOverrideOperations,
            update.AppendHeaderOverrideOperations);

        if (update.BodyOverrideOperations is not null)
        {
            row.BodyOverrideOperations = update.BodyOverrideOperations;
        }

        row.BodyOverrideOperations = Append(
            row.BodyOverrideOperations,
            update.AppendBodyOverrideOperations);

        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<ChannelOverrideTemplateRecord?>(row);
    }

    public Task<bool> DeleteAsync(
        long id,
        long? scopeUserId,
        CancellationToken cancellationToken = default)
    {
        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        var row = Live(id, scopeUserId);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<ChannelRecord>> ListChannelsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ChannelQueries.Add(ids);

        var rows = Channels
            .Where(channel => channel.DeletedAt == 0 && ids.Contains(channel.Id))
            .OrderBy(channel => channel.Id)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChannelRecord>>(rows);
    }

    public Task<IReadOnlyList<ChannelRecord>> UpdateChannelSettingsAsync(
        IReadOnlyList<ChannelSettingsWrite> updates,
        CancellationToken cancellationToken = default)
    {
        if (UpdateChannelSettingsFailure is not null)
        {
            throw UpdateChannelSettingsFailure;
        }

        var updated = new List<ChannelRecord>(updates.Count);
        foreach (var write in updates)
        {
            var channel = Channels.FirstOrDefault(row =>
                row.Id == write.Id && row.DeletedAt == 0);
            if (channel is null)
            {
                throw new StoreFailureException(
                    $"failed to update channel {write.Name}: ent: channel not found",
                    new InvalidOperationException("missing"));
            }

            Writes.Add(write);
            channel.Settings = write.SettingsJson;
            channel.UpdatedAt = DateTime.UtcNow;
            updated.Add(channel);
        }

        return Task.FromResult<IReadOnlyList<ChannelRecord>>(updated);
    }

    private ChannelOverrideTemplateRecord? Live(long id, long? scopeUserId)
        => Templates.FirstOrDefault(row =>
            row.Id == id
            && row.DeletedAt == 0
            && (scopeUserId is null || row.UserId == scopeUserId));

    /// <summary>Ent predicate subset the management frontend sends.</summary>
    private static bool Matches(
        ChannelOverrideTemplateRecord row,
        ChannelOverrideTemplateListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id)
        {
            return false;
        }

        if (filter.IdIn is not null && !filter.IdIn.Contains(row.Id))
        {
            return false;
        }

        if (filter.Name is not null
            && !string.Equals(row.Name, filter.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.NameIn is not null && !filter.NameIn.Contains(row.Name, StringComparer.Ordinal))
        {
            return false;
        }

        if (filter.NameContains is not null
            && !row.Name.Contains(filter.NameContains, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.NameContainsFold is not null
            && !row.Name.Contains(filter.NameContainsFold, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Not is not null && Matches(row, filter.Not))
        {
            return false;
        }

        if (filter.And is not null && filter.And.Any(nested => !Matches(row, nested)))
        {
            return false;
        }

        // A present but empty OR folds to SQL FALSE.
        if (filter.Or is not null && !filter.Or.Any(nested => Matches(row, nested)))
        {
            return false;
        }

        return true;
    }

    /// <summary>ENTGQL rule: value column first, then id as the tie-breaker.</summary>
    private static int Compare(
        ChannelOverrideTemplateRecord left,
        ChannelOverrideTemplateRecord right,
        ChannelOverrideTemplateSortField sortField)
    {
        if (sortField == ChannelOverrideTemplateSortField.UpdatedAt)
        {
            var compare = left.UpdatedAt.CompareTo(right.UpdatedAt);
            if (compare != 0)
            {
                return compare;
            }
        }

        return left.Id.CompareTo(right.Id);
    }

    private static int CompareCursor(
        ChannelOverrideTemplateRecord row,
        ListCursor cursor,
        ChannelOverrideTemplateSortField sortField)
    {
        if (sortField == ChannelOverrideTemplateSortField.UpdatedAt
            && cursor.Value is DateTime time)
        {
            var compare = row.UpdatedAt.CompareTo(time);
            if (compare != 0)
            {
                return compare;
            }
        }

        return row.Id.CompareTo(cursor.Id);
    }

    private static bool Beyond(
        ChannelOverrideTemplateRecord row,
        ListCursor cursor,
        ChannelOverrideTemplateSortField sortField,
        SortDirection direction)
    {
        var compare = CompareCursor(row, cursor, sortField);
        return direction == SortDirection.Asc ? compare > 0 : compare < 0;
    }

    /// <summary>
    /// Ent's sqljson modifier: an empty append is a no-op, JSON null is
    /// replaced, and a JSON array is concatenated with the appended array.
    /// </summary>
    private static string? Append(string? current, string? appended)
    {
        if (appended is null)
        {
            return current;
        }

        if (current is null || current == "null")
        {
            return appended;
        }

        return current.TrimEnd(']') + "," + appended.TrimStart('[');
    }
}

/// <summary>Swappable current management user.</summary>
internal sealed class FakeManagementIdentity(ManagementUser? user) : IManagementIdentity
{
    public ManagementUser? User { get; set; } = user;

    /// <summary>X-Project-ID context; null mirrors a request without the header.</summary>
    public long? ProjectId { get; set; }

    /// <summary>API key principal; null when the caller is a user or anonymous.</summary>
    public AxonHub.Persistence.ApiKeyRecord? ApiKey { get; set; }

    /// <summary>The key's project edge (apiKey.Edges.Project).</summary>
    public AxonHub.Persistence.ProjectRecord? Project { get; set; }

    public Task<ManagementUser?> GetCurrentUserAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(User);

    public AxonHub.Persistence.ApiKeyRecord? GetCurrentApiKey() => ApiKey;

    public long? GetCurrentProjectId() => ProjectId;

    public AxonHub.Persistence.ProjectRecord? GetCurrentProject() => Project;
}
