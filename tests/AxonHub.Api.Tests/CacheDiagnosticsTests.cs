using System.Text.Json;
using AxonHub.Api.Diagnostics;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the getCacheDiagnostics export
/// (internal/server/gql/channel_model_cache_diagnostics.go) against the port:
/// struct field order, Go's nil-versus-empty slice rules, the settings
/// omitempty set, RFC 3339 timestamps, candidate previews, the Go encoding of
/// condition <c>any</c> values and the resolver's error chains.
/// </summary>
public sealed class CacheDiagnosticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static readonly DateTime ChannelUpdatedAt =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly FakeChannelAdminStore _channels = new();
    private readonly FakeModelStore _models = new();
    private readonly FakeEnabledChannelCacheSnapshotProvider _enabledCache = new();
    private readonly FakeDiagnosticsCandidateSelector _selector = new();
    private readonly TestTimeProvider _time = new(Now);

    private CacheDiagnosticsService Service =>
        new(_channels, _models, _enabledCache, _selector, _time);

    // -- whole-document shape --------------------------------------------

    [Fact]
    public async Task EmptyExportMatchesGoMarshalIndent()
    {
        var payload = await Service.GetCacheDiagnosticsAsync(null);

        Assert.Equal(
            "axonhub-channel-model-cache-diagnostics-20260203T040506Z.json",
            payload.FileName);
        Assert.Equal(new[] { DiagnosticsTargetValue.ChannelCache }, payload.Targets);
        Assert.Equal(EmptyExport(), payload.Content);
    }

    [Fact]
    public async Task TargetsNormalizeToTheDefaultAndDropDuplicates()
    {
        Assert.Equal(
            new[] { DiagnosticsTargetValue.ChannelCache },
            DiagnosticsTargets.Normalize(null));
        Assert.Equal(
            new[] { DiagnosticsTargetValue.ChannelCache },
            DiagnosticsTargets.Normalize([]));
        Assert.Equal(
            new[] { DiagnosticsTargetValue.ChannelCache },
            DiagnosticsTargets.Normalize(
                [DiagnosticsTargetValue.ChannelCache, DiagnosticsTargetValue.ChannelCache]));

        var payload = await Service.GetCacheDiagnosticsAsync(
            [DiagnosticsTargetValue.ChannelCache, DiagnosticsTargetValue.ChannelCache]);
        Assert.Single(payload.Targets);
    }

    // -- database.channels -----------------------------------------------

    [Fact]
    public async Task DatabaseChannelsKeepTheEntOrderingAndShape()
    {
        SeedChannel("Beta", status: "disabled", orderingWeight: 9, baseUrl: "http://beta.test");
        SeedChannel(
            "Alpha",
            orderingWeight: 5,
            tags: """["tag-a","tag-b"]""",
            supportedModels: """["m2","m1"]""",
            defaultTestModel: "m1",
            autoSyncSupportedModels: true,
            settings:
                """{"extraModelPrefix":"azure","autoTrimedModelPrefixes":["vendor"],"hideMappedModels":true,"hideOriginalModels":true,"lowercaseModelId":true,"modelMappings":[{"from":"alias","to":"m1"}]}""");
        var deleted = SeedChannel("Deleted", orderingWeight: 99);
        deleted.DeletedAt = 1;

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var channels = document.RootElement.GetProperty("database").GetProperty("channels");
        Assert.Equal(2, channels.GetArrayLength());

        // ORDER BY ordering_weight DESC, id ASC.
        Assert.Equal("Beta", channels[0].GetProperty("name").GetString());
        Assert.Equal("Alpha", channels[1].GetProperty("name").GetString());

        var beta = channels[0];
        Assert.Equal(
            new[]
            {
                "id", "name", "type", "status", "updatedAt", "baseUrl", "tags",
                "supportedModels", "defaultTestModel", "autoSyncSupportedModels", "settings",
            },
            Names(beta));
        Assert.Equal("disabled", beta.GetProperty("status").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", beta.GetProperty("updatedAt").GetString());
        Assert.Equal("http://beta.test", beta.GetProperty("baseUrl").GetString());
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("supportedModels").ValueKind);
        Assert.False(beta.GetProperty("autoSyncSupportedModels").GetBoolean());

        // A nil *objects.ChannelSettings marshals as the zero struct: only the
        // three non-omitempty booleans survive.
        Assert.Equal(
            new[] { "hideMappedModels", "hideOriginalModels", "lowercaseModelId" },
            Names(beta.GetProperty("settings")));

        var alpha = channels[1];
        Assert.Equal("", alpha.GetProperty("baseUrl").GetString());
        Assert.Equal(
            new[] { "tag-a", "tag-b" },
            Strings(alpha.GetProperty("tags")));
        Assert.Equal(
            new[] { "m2", "m1" },
            Strings(alpha.GetProperty("supportedModels")));
        Assert.Equal("m1", alpha.GetProperty("defaultTestModel").GetString());
        Assert.True(alpha.GetProperty("autoSyncSupportedModels").GetBoolean());

        var settings = alpha.GetProperty("settings");
        Assert.Equal(
            new[]
            {
                "extraModelPrefix", "autoTrimedModelPrefixes", "hideMappedModels",
                "hideOriginalModels", "lowercaseModelId", "modelMappings",
            },
            Names(settings));
        Assert.Equal("azure", settings.GetProperty("extraModelPrefix").GetString());
        Assert.Equal(new[] { "vendor" }, Strings(settings.GetProperty("autoTrimedModelPrefixes")));
        Assert.True(settings.GetProperty("hideMappedModels").GetBoolean());
        Assert.True(settings.GetProperty("hideOriginalModels").GetBoolean());
        Assert.True(settings.GetProperty("lowercaseModelId").GetBoolean());
        var mapping = settings.GetProperty("modelMappings")[0];
        Assert.Equal(new[] { "from", "to" }, Names(mapping));
        Assert.Equal("alias", mapping.GetProperty("from").GetString());
        Assert.Equal("m1", mapping.GetProperty("to").GetString());

        var query = Assert.Single(_channels.AllQueries);
        Assert.True(query.Ordered);
        Assert.Equal(ChannelSortField.OrderingWeight, query.SortField);
        Assert.Equal(SortDirection.Desc, query.Direction);
    }

    // -- database.models -------------------------------------------------

    [Fact]
    public async Task DatabaseModelsKeepTheModelViewShape()
    {
        // ListAll orders by id, not by model_id.
        SeedModel("z-model", status: "disabled", settings: "{}");
        SeedModel("a-model", status: "enabled", settings: "null");

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var models = document.RootElement.GetProperty("database").GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal("z-model", models[0].GetProperty("modelId").GetString());
        Assert.Equal("a-model", models[1].GetProperty("modelId").GetString());

        var model = models[0];
        Assert.Equal(
            new[]
            {
                "id", "modelId", "status", "updatedAt", "associationCount",
                "associations", "candidateSelectionPreview",
            },
            Names(model));
        Assert.Equal("disabled", model.GetProperty("status").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", model.GetProperty("updatedAt").GetString());
        Assert.Equal(0, model.GetProperty("associationCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("associations").ValueKind);
        Assert.Empty(model.GetProperty("candidateSelectionPreview").EnumerateArray());

        // A JSON null settings column is a nil *objects.ModelSettings.
        Assert.Equal(JsonValueKind.Null, models[1].GetProperty("associations").ValueKind);
        Assert.Equal(0, models[1].GetProperty("associationCount").GetInt32());
    }

    [Fact]
    public async Task ExplicitEmptyAssociationsMarshalAsAnEmptyArray()
    {
        SeedModel("m", status: "enabled", settings: """{"associations":[]}""");

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var model = document.RootElement.GetProperty("database").GetProperty("models")[0];
        Assert.Equal(0, model.GetProperty("associationCount").GetInt32());
        Assert.Equal(JsonValueKind.Array, model.GetProperty("associations").ValueKind);
        Assert.Empty(model.GetProperty("associations").EnumerateArray());
    }

    [Fact]
    public async Task ModelAssociationsKeepTheGoFieldOrder()
    {
        SeedModel("m", settings: RichSettings);

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var model = document.RootElement.GetProperty("database").GetProperty("models")[0];
        Assert.Equal(2, model.GetProperty("associationCount").GetInt32());
        var associations = model.GetProperty("associations");
        Assert.Equal(2, associations.GetArrayLength());

        var channelModel = associations[0];
        Assert.Equal(
            new[]
            {
                "type", "priority", "disabled", "when", "channelModel", "channelRegex",
                "regex", "modelId", "channelTagsModel", "channelTagsRegex",
            },
            Names(channelModel));
        Assert.Equal("channel_model", channelModel.GetProperty("type").GetString());
        Assert.Equal(3, channelModel.GetProperty("priority").GetInt32());
        Assert.True(channelModel.GetProperty("disabled").GetBoolean());

        var when = channelModel.GetProperty("when");
        Assert.Equal(new[] { "enabled", "condition" }, Names(when));
        Assert.True(when.GetProperty("enabled").GetBoolean());

        var group = when.GetProperty("condition");
        Assert.Equal(new[] { "type", "logic", "conditions" }, Names(group));
        Assert.Equal("group", group.GetProperty("type").GetString());
        Assert.Equal("and", group.GetProperty("logic").GetString());
        var children = group.GetProperty("conditions");
        Assert.Equal(2, children.GetArrayLength());

        var tokens = children[0];
        Assert.Equal(new[] { "type", "field", "operator", "value" }, Names(tokens));
        Assert.Equal("condition", tokens.GetProperty("type").GetString());
        Assert.Equal("prompt_tokens", tokens.GetProperty("field").GetString());
        Assert.Equal("gt", tokens.GetProperty("operator").GetString());
        Assert.Equal(1000L, tokens.GetProperty("value").GetInt64());

        var stream = children[1];
        Assert.Equal("stream", stream.GetProperty("field").GetString());
        Assert.Equal(JsonValueKind.True, stream.GetProperty("value").ValueKind);

        var channelModelRef = channelModel.GetProperty("channelModel");
        Assert.Equal(new[] { "channelId", "modelId" }, Names(channelModelRef));
        Assert.Equal(7, channelModelRef.GetProperty("channelId").GetInt32());
        Assert.Equal("upstream", channelModelRef.GetProperty("modelId").GetString());
        Assert.Equal(JsonValueKind.Null, channelModel.GetProperty("channelRegex").ValueKind);
        Assert.Equal(JsonValueKind.Null, channelModel.GetProperty("regex").ValueKind);
        Assert.Equal(JsonValueKind.Null, channelModel.GetProperty("modelId").ValueKind);
        Assert.Equal(JsonValueKind.Null, channelModel.GetProperty("channelTagsModel").ValueKind);
        Assert.Equal(JsonValueKind.Null, channelModel.GetProperty("channelTagsRegex").ValueKind);

        var regex = associations[1];
        // `when` has omitempty; the pointer fields do not.
        Assert.Equal(
            new[]
            {
                "type", "priority", "disabled", "channelModel", "channelRegex",
                "regex", "modelId", "channelTagsModel", "channelTagsRegex",
            },
            Names(regex));
        Assert.Equal(JsonValueKind.Null, regex.GetProperty("channelModel").ValueKind);

        var regexBody = regex.GetProperty("regex");
        Assert.Equal(new[] { "pattern", "exclude" }, Names(regexBody));
        Assert.Equal("^gpt-.*$", regexBody.GetProperty("pattern").GetString());
        var excludes = regexBody.GetProperty("exclude");
        Assert.Equal(2, excludes.GetArrayLength());
        Assert.Equal(
            new[] { "channelNamePattern", "channelIds", "channelTags" },
            Names(excludes[0]));
        Assert.Equal("no-.*", excludes[0].GetProperty("channelNamePattern").GetString());
        Assert.Equal(new[] { 1, 2 }, Ints(excludes[0].GetProperty("channelIds")));
        Assert.Equal(new[] { "t1" }, Strings(excludes[0].GetProperty("channelTags")));
        Assert.Equal(JsonValueKind.Null, excludes[1].ValueKind);

        // A decoded empty exclude slice stays non-nil and marshals as [].
        var modelIdBody = regex.GetProperty("modelId");
        Assert.Equal(new[] { "modelId", "exclude" }, Names(modelIdBody));
        Assert.Equal("m", modelIdBody.GetProperty("modelId").GetString());
        Assert.Empty(modelIdBody.GetProperty("exclude").EnumerateArray());

        var tagsModel = regex.GetProperty("channelTagsModel");
        Assert.Equal(new[] { "channelTags", "modelId" }, Names(tagsModel));
        Assert.Equal(new[] { "a", "b" }, Strings(tagsModel.GetProperty("channelTags")));
        Assert.Equal("m2", tagsModel.GetProperty("modelId").GetString());

        var tagsRegex = regex.GetProperty("channelTagsRegex");
        Assert.Equal(new[] { "channelTags", "pattern" }, Names(tagsRegex));
        Assert.Equal(JsonValueKind.Null, tagsRegex.GetProperty("channelTags").ValueKind);
        Assert.Equal("p", tagsRegex.GetProperty("pattern").GetString());
    }

    [Fact]
    public async Task ConditionValuesUseGoNumberAndSortedKeyEncoding()
    {
        SeedModel("m", settings: ConditionValueSettings);

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var value = document.RootElement
            .GetProperty("database")
            .GetProperty("models")[0]
            .GetProperty("associations")[0]
            .GetProperty("when")
            .GetProperty("condition")
            .GetProperty("value");

        // map[string]any keys marshal sorted.
        Assert.Equal(new[] { "a", "big", "frac", "list", "z" }, Names(value));

        var nested = value.GetProperty("a");
        Assert.Equal(new[] { "m", "n" }, Names(nested));
        Assert.Equal("1.5", nested.GetProperty("m").GetRawText());
        Assert.Equal("1000", nested.GetProperty("n").GetRawText());
        Assert.Equal("2", value.GetProperty("z").GetRawText());

        // json.Number.Int64 succeeded, so the value is an int64 in fixed notation.
        var list = value.GetProperty("list");
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal(JsonValueKind.True, list[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, list[1].ValueKind);
        Assert.Equal("s", list[2].GetString());

        // Float64 overflow falls back to the raw text as a string.
        Assert.Equal(JsonValueKind.String, value.GetProperty("big").ValueKind);
        Assert.Equal("1e400", value.GetProperty("big").GetString());

        // encoding/json switches to exponent notation below 1e-6.
        Assert.Equal("1e-7", value.GetProperty("frac").GetRawText());
    }

    // -- candidateSelectionPreview --------------------------------------

    [Fact]
    public async Task CandidatePreviewMirrorsTheSelectedModelEntries()
    {
        SeedModel("gpt-4", status: "enabled", settings: "{}");
        _selector.Results["gpt-4"] =
        [
            new ChannelCandidate
            {
                ChannelId = 3,
                ChannelName = "channel-c",
                Priority = 2,
                Models =
                [
                    new ChannelCandidateModel
                    {
                        RequestModel = "gpt-4",
                        ActualModel = "up-a",
                        Source = "direct",
                    },
                    new ChannelCandidateModel
                    {
                        RequestModel = "gpt-4",
                        ActualModel = "up-b",
                        Source = "mapping",
                    },
                ],
            },
            new ChannelCandidate
            {
                ChannelId = 4,
                ChannelName = "channel-d",
                Priority = 0,
            },
        ];

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var preview = document.RootElement
            .GetProperty("database")
            .GetProperty("models")[0]
            .GetProperty("candidateSelectionPreview");
        Assert.Equal(2, preview.GetArrayLength());
        Assert.Equal(new[] { "channelId", "channelName", "priority", "models" }, Names(preview[0]));
        Assert.Equal(3, preview[0].GetProperty("channelId").GetInt32());
        Assert.Equal("channel-c", preview[0].GetProperty("channelName").GetString());
        Assert.Equal(2, preview[0].GetProperty("priority").GetInt32());

        var models = preview[0].GetProperty("models");
        Assert.Equal(2, models.GetArrayLength());
        Assert.Equal(new[] { "requestModel", "actualModel", "source" }, Names(models[0]));
        Assert.Equal("gpt-4", models[0].GetProperty("requestModel").GetString());
        Assert.Equal("up-a", models[0].GetProperty("actualModel").GetString());
        Assert.Equal("direct", models[0].GetProperty("source").GetString());
        Assert.Equal("mapping", models[1].GetProperty("source").GetString());

        // lo.Map over an empty list is a non-nil empty slice.
        Assert.Empty(preview[1].GetProperty("models").EnumerateArray());
        Assert.Equal(new[] { "gpt-4" }, _selector.Calls);
    }

    [Fact]
    public async Task DisabledModelsAreNotSelected()
    {
        SeedModel("off", status: "disabled", settings: "{}");

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var model = document.RootElement.GetProperty("database").GetProperty("models")[0];
        Assert.Empty(model.GetProperty("candidateSelectionPreview").EnumerateArray());
        Assert.Empty(_selector.Calls);
    }

    [Fact]
    public async Task SelectorFailureLeavesAnEmptyPreview()
    {
        SeedModel("m", status: "enabled", settings: "{}");
        _selector.Failure = new InvalidOperationException("selector down");

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var model = document.RootElement.GetProperty("database").GetProperty("models")[0];
        Assert.Empty(model.GetProperty("candidateSelectionPreview").EnumerateArray());
        Assert.Equal(new[] { "m" }, _selector.Calls);
    }

    // -- cache.enabledChannelCache --------------------------------------

    [Fact]
    public async Task EnabledChannelCacheSnapshotComesFromTheGrainSnapshot()
    {
        _enabledCache.Snapshot = new EnabledChannelsSnapshot
        {
            Version = 42,
            Channels =
            [
                new EnabledChannelSnapshot
                {
                    Id = 7,
                    Name = "channel-g",
                    Type = "openai",
                    UpdatedAt = ChannelUpdatedAt,
                    Tags = """["x"]""",
                    SupportedModels = """["a","b"]""",
                    DefaultTestModel = "a",
                    Credentials = """{"apiKeys":["k1","k2","k3"]}""",
                    DisabledApiKeys = """[{"key":"k2","expiresAt":"2026-03-01T00:00:00Z"}]""",
                    Settings =
                        """{"extraModelPrefix":"azure","modelMappings":[{"from":"alias","to":"a"}]}""",
                },
            ],
        };

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var cache = document.RootElement.GetProperty("cache").GetProperty("enabledChannelCache");
        Assert.Equal(
            new[]
            {
                "channelCount", "cacheVersion", "channelIDs", "channelNames",
                "channels", "capturedAtUtc",
            },
            Names(cache));
        Assert.Equal(1, cache.GetProperty("channelCount").GetInt32());
        Assert.Equal(42, cache.GetProperty("cacheVersion").GetInt64());
        Assert.Equal(new[] { 7 }, Ints(cache.GetProperty("channelIDs")));
        Assert.Equal(new[] { "channel-g" }, Strings(cache.GetProperty("channelNames")));
        Assert.Equal("2026-02-03T04:05:06Z", cache.GetProperty("capturedAtUtc").GetString());

        var channel = cache.GetProperty("channels")[0];
        Assert.Equal(
            new[]
            {
                "id", "name", "type", "status", "updatedAt", "tags", "supportedModels",
                "defaultTestModel", "enabledApiKeyCount", "disabledApiKeyCount",
                "settings", "modelEntries",
            },
            Names(channel));
        Assert.Equal(7, channel.GetProperty("id").GetInt32());
        Assert.Equal("channel-g", channel.GetProperty("name").GetString());
        Assert.Equal("openai", channel.GetProperty("type").GetString());

        // reloadEnabledChannels only loads enabled rows, so the cached view is
        // always "enabled" even though the snapshot carries no status.
        Assert.Equal("enabled", channel.GetProperty("status").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", channel.GetProperty("updatedAt").GetString());
        Assert.Equal(new[] { "x" }, Strings(channel.GetProperty("tags")));
        Assert.Equal(new[] { "a", "b" }, Strings(channel.GetProperty("supportedModels")));
        Assert.Equal("a", channel.GetProperty("defaultTestModel").GetString());
        Assert.Equal(2, channel.GetProperty("enabledApiKeyCount").GetInt32());
        Assert.Equal(1, channel.GetProperty("disabledApiKeyCount").GetInt32());

        var settings = channel.GetProperty("settings");
        Assert.Equal(
            new[]
            {
                "extraModelPrefix", "hideMappedModels", "hideOriginalModels",
                "lowercaseModelId", "modelMappings",
            },
            Names(settings));

        // lo.Keys + sort.Strings over the built entry map.
        var entries = channel.GetProperty("modelEntries");
        Assert.Equal(5, entries.GetArrayLength());
        Assert.Equal(
            new[] { "a", "alias", "azure/a", "azure/b", "b" },
            entries.EnumerateArray().Select(static entry =>
                entry.GetProperty("requestModel").GetString()!)
                .ToArray());
        Assert.Equal(
            new[] { "direct", "mapping", "prefix", "prefix", "direct" },
            entries.EnumerateArray().Select(static entry =>
                entry.GetProperty("source").GetString()!)
                .ToArray());
        Assert.Equal(
            new[] { "a", "a", "a", "b", "b" },
            entries.EnumerateArray().Select(static entry =>
                entry.GetProperty("actualModel").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task ClockReadsFollowTheExportOrder()
    {
        // The snapshot read sits between exportedAt (before it) and the
        // enabled-key expiry check / file name (after it).
        _enabledCache.OnGet = () => _time.Advance(TimeSpan.FromMinutes(2));
        _enabledCache.Snapshot = new EnabledChannelsSnapshot
        {
            Channels =
            [
                new EnabledChannelSnapshot
                {
                    Id = 1,
                    Name = "expiring",
                    Type = "openai",
                    UpdatedAt = ChannelUpdatedAt,
                    Credentials = """{"apiKeys":["k1"]}""",
                    DisabledApiKeys = """[{"key":"k1","expiresAt":"2026-02-03T04:06:00Z"}]""",
                },
            ],
        };

        var payload = await Service.GetCacheDiagnosticsAsync(null);

        using var document = JsonDocument.Parse(payload.Content);
        var root = document.RootElement;
        Assert.Equal("2026-02-03T04:05:06Z", root.GetProperty("exportedAt").GetString());
        var cache = root.GetProperty("cache").GetProperty("enabledChannelCache");
        Assert.Equal("2026-02-03T04:05:06Z", cache.GetProperty("capturedAtUtc").GetString());

        // time.Now() is read again for Sprintf(..., "20060102T150405Z").
        Assert.Equal(
            "axonhub-channel-model-cache-diagnostics-20260203T040706Z.json",
            payload.FileName);

        // ch.GetEnabledAPIKeys() is evaluated against the export-time clock, so
        // a disable that elapsed between the two reads no longer hides the key.
        Assert.Equal(1, cache.GetProperty("channels")[0].GetProperty("enabledApiKeyCount").GetInt32());
        Assert.Equal(1, cache.GetProperty("channels")[0].GetProperty("disabledApiKeyCount").GetInt32());
    }

    // -- error chains ----------------------------------------------------

    [Fact]
    public async Task ChannelQueryFailureKeepsTheBuildPrefix()
    {
        _channels.ListAllFailure = new InvalidOperationException("db down");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetCacheDiagnosticsAsync(null));

        Assert.Equal(
            "failed to build cache diagnostics export: failed to query channels: db down",
            ex.Message);
        // The build wrapper wraps the store wrapper, which wraps the driver
        // error in turn (three nodes, root has no inner).
        var queryError = Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("failed to query channels: db down", queryError.Message);
        var root = Assert.IsType<InvalidOperationException>(queryError.InnerException);
        Assert.Equal("db down", root.Message);
        Assert.Null(root.InnerException);
    }

    [Fact]
    public async Task ModelQueryFailureKeepsTheBuildPrefix()
    {
        _models.ListAllFailure = new InvalidOperationException("db down");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetCacheDiagnosticsAsync(null));

        Assert.Equal(
            "failed to build cache diagnostics export: failed to query models: db down",
            ex.Message);
    }

    [Fact]
    public async Task MalformedChannelSettingsFailDuringTheChannelQuery()
    {
        SeedChannel("broken", settings: "{oops");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetCacheDiagnosticsAsync(null));

        Assert.StartsWith(
            "failed to build cache diagnostics export: failed to query channels: ",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SnapshotFailureIsWrappedByTheBuildPrefix()
    {
        _enabledCache.Failure = new InvalidOperationException("snapshot down");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetCacheDiagnosticsAsync(null));

        Assert.Equal("failed to build cache diagnostics export: snapshot down", ex.Message);
    }

    // -- helpers ---------------------------------------------------------

    private ChannelRecord SeedChannel(
        string name,
        string type = "openai",
        string status = "enabled",
        long orderingWeight = 0,
        string? baseUrl = null,
        string? tags = null,
        string supportedModels = "null",
        string defaultTestModel = "",
        bool autoSyncSupportedModels = false,
        string? settings = null,
        DateTime? updatedAt = null)
    {
        var record = new ChannelRecord
        {
            Name = name,
            Type = type,
            Status = status,
            OrderingWeight = orderingWeight,
            BaseUrl = baseUrl,
            Tags = tags,
            SupportedModels = supportedModels,
            DefaultTestModel = defaultTestModel,
            AutoSyncSupportedModels = autoSyncSupportedModels,
            Settings = settings,
            UpdatedAt = updatedAt ?? ChannelUpdatedAt,
        };

        _channels.Seed(record);
        return record;
    }

    private ModelRecord SeedModel(
        string modelId,
        string status = "enabled",
        string settings = "{}",
        DateTime? updatedAt = null)
        => _models.Seed(row =>
        {
            row.ModelId = modelId;
            row.Status = status;
            row.Settings = settings;
            row.UpdatedAt = updatedAt ?? ChannelUpdatedAt;
        });

    private static string[] Names(JsonElement element) =>
        [.. element.EnumerateObject().Select(static property => property.Name)];

    private static string[] Strings(JsonElement element) =>
        [.. element.EnumerateArray().Select(static item => item.GetString()!)];

    private static int[] Ints(JsonElement element) =>
        [.. element.EnumerateArray().Select(static item => item.GetInt32())];

    private static string EmptyExport() => """
        {
          "exportedAt": "2026-02-03T04:05:06Z",
          "targets": [
            "CHANNEL_CACHE"
          ],
          "database": {
            "channels": [],
            "models": []
          },
          "cache": {
            "enabledChannelCache": {
              "channelCount": 0,
              "cacheVersion": 0,
              "channelIDs": [],
              "channelNames": [],
              "channels": [],
              "capturedAtUtc": "2026-02-03T04:05:06Z"
            },
            "modelAssociationCache": []
          }
        }
        """.ReplaceLineEndings("\n");

    private const string RichSettings = """
        {
          "associations": [
            {
              "type": "channel_model",
              "priority": 3,
              "disabled": true,
              "when": {
                "enabled": true,
                "condition": {
                  "type": "group",
                  "logic": "and",
                  "conditions": [
                    {
                      "type": "condition",
                      "field": "prompt_tokens",
                      "operator": "gt",
                      "value": 1000
                    },
                    {
                      "type": "condition",
                      "field": "stream",
                      "operator": "eq",
                      "value": true
                    }
                  ]
                }
              },
              "channelModel": {"channelId": 7, "modelId": "upstream"}
            },
            {
              "type": "regex",
              "regex": {
                "pattern": "^gpt-.*$",
                "exclude": [
                  {
                    "channelNamePattern": "no-.*",
                    "channelIds": [1, 2],
                    "channelTags": ["t1"]
                  },
                  null
                ]
              },
              "modelId": {"modelId": "m", "exclude": []},
              "channelTagsModel": {"channelTags": ["a", "b"], "modelId": "m2"},
              "channelTagsRegex": {"channelTags": null, "pattern": "p"}
            }
          ]
        }
        """;

    private const string ConditionValueSettings = """
        {
          "associations": [
            {
              "type": "channel_model",
              "when": {
                "enabled": true,
                "condition": {
                  "type": "condition",
                  "field": "request_header.x",
                  "operator": "eq",
                  "value": {
                    "z": 2,
                    "a": {"n": 1000, "m": 1.5},
                    "list": [true, null, "s"],
                    "big": 1e400,
                    "frac": 0.0000001
                  }
                }
              },
              "channelModel": {"channelId": 1, "modelId": "m"}
            }
          ]
        }
        """;
}
