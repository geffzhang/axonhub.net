using AxonHub.Api.Models;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks SystemService.ModelSettingsOrDefault
/// (internal/server/biz/system.go:1237-1291) and
/// normalizeSystemModelSettings (model_settings_inheritance.go:12-32): a missing
/// row, a lookup failure or a malformed document falls back to
/// <see cref="ModelSettings.Default"/>, while a valid but empty document keeps
/// Go's zero values.
/// </summary>
public sealed class SystemModelSettingsServiceTests
{
    [Fact]
    public async Task MissingRowReturnsDefaults()
    {
        var settings = await ReadAsync(value: null);

        Assert.True(settings.FallbackToChannelsOnModelNotFound);
        Assert.True(settings.QueryAllChannelModels);
        Assert.False(settings.DefaultModelApiIncludeAll);
        Assert.False(settings.AutoReasoningEffort);
        Assert.Equal(string.Empty, settings.ModelBlacklistRegex);
        Assert.False(settings.HideUnroutableModelsInList);
        Assert.Empty(settings.DeveloperSettings);
    }

    [Fact]
    public async Task StoredValueIsParsed()
    {
        var settings = await ReadAsync(
            value: """
                {"fallback_to_channels_on_model_not_found":false,
                 "query_all_channel_models":false,
                 "default_model_api_include_all":true,
                 "auto_reasoning_effort":true,
                 "model_blacklist_regex":"gpt-.*",
                 "hide_unroutable_models_in_list":true}
                """);

        Assert.False(settings.FallbackToChannelsOnModelNotFound);
        Assert.False(settings.QueryAllChannelModels);
        Assert.True(settings.DefaultModelApiIncludeAll);
        Assert.True(settings.AutoReasoningEffort);
        Assert.Equal("gpt-.*", settings.ModelBlacklistRegex);
        Assert.True(settings.HideUnroutableModelsInList);
    }

    [Fact]
    public async Task MissingMembersKeepGoZeroValues()
    {
        // A stored document is never merged with the defaults: absent flags stay
        // false even though defaultModelSettings has them true.
        var settings = await ReadAsync(value: """{"model_blacklist_regex":"^gpt-"}""");

        Assert.False(settings.FallbackToChannelsOnModelNotFound);
        Assert.False(settings.QueryAllChannelModels);
        Assert.Equal("^gpt-", settings.ModelBlacklistRegex);
    }

    [Fact]
    public async Task LiteralNullDocumentKeepsGoZeroValues()
    {
        // json.Unmarshal("null", &struct) is a no-op, so the zero settings are
        // returned instead of the defaults.
        var settings = await ReadAsync(value: "null");

        Assert.False(settings.FallbackToChannelsOnModelNotFound);
        Assert.False(settings.QueryAllChannelModels);
        Assert.Equal(string.Empty, settings.ModelBlacklistRegex);
        Assert.Empty(settings.DeveloperSettings);
    }

    [Fact]
    public async Task EmptyObjectKeepsGoZeroValues()
    {
        var settings = await ReadAsync(value: "{}");

        Assert.False(settings.QueryAllChannelModels);
        Assert.Equal(string.Empty, settings.ModelBlacklistRegex);
        Assert.Empty(settings.DeveloperSettings);
    }

    [Fact]
    public async Task KeysMatchCaseInsensitivelyLikeGo()
    {
        var settings = await ReadAsync(
            value: """{"QUERY_ALL_CHANNEL_MODELS":false,"Model_Blacklist_Regex":"claude-.*"}""");

        Assert.False(settings.QueryAllChannelModels);
        Assert.Equal("claude-.*", settings.ModelBlacklistRegex);
    }

    [Fact]
    public async Task UnknownMembersAreIgnored()
    {
        var settings = await ReadAsync(
            value: """{"query_all_channel_models":false,"future_setting":123}""");

        Assert.False(settings.QueryAllChannelModels);
    }

    [Fact]
    public async Task DeveloperSettingsAreNormalized()
    {
        var settings = await ReadAsync(
            value: """
                {"developer_settings":[
                    {"developer":"  openai  ","associations":[{"type":"channel_model","channel_id":7}]},
                    {"developer":"anthropic"},
                    null
                ]}
                """);

        Assert.Collection(
            settings.DeveloperSettings,
            openai =>
            {
                Assert.Equal("openai", openai.Developer);
                var association = Assert.Single(openai.Associations);
                Assert.Equal("channel_model", association.GetProperty("type").GetString());
                Assert.Equal(7L, association.GetProperty("channel_id").GetInt64());
            },
            anthropic =>
            {
                Assert.Equal("anthropic", anthropic.Developer);
                Assert.Empty(anthropic.Associations);
            });
    }

    [Fact]
    public async Task NullDeveloperSettingsNormalizesToEmpty()
    {
        var settings = await ReadAsync(value: """{"developer_settings":null}""");

        Assert.Empty(settings.DeveloperSettings);
    }

    [Theory]
    [InlineData("{\"query_all_channel_models\":")]
    [InlineData("""{"query_all_channel_models":"true"}""")]
    [InlineData("""{"model_blacklist_regex":5}""")]
    [InlineData("""{"developer_settings":5}""")]
    public async Task MalformedValueReturnsDefaults(string value)
    {
        var settings = await ReadAsync(value);

        Assert.True(settings.QueryAllChannelModels);
        Assert.True(settings.FallbackToChannelsOnModelNotFound);
    }

    [Fact]
    public async Task StoreFailureReturnsDefaults()
    {
        var store = new FakeSystemStore { GetFailure = new InvalidOperationException("down") };

        var settings = await new SystemModelSettingsService(store).GetModelSettingsOrDefaultAsync();

        Assert.True(settings.QueryAllChannelModels);
    }

    [Fact]
    public async Task SoftDeletedRowIsTreatedAsMissing()
    {
        var settings = await ReadAsync(
            value: """{"query_all_channel_models":false}""",
            deletedAt: 1_700_000_000L);

        Assert.True(settings.QueryAllChannelModels);
    }

    private static async Task<ModelSettings> ReadAsync(
        string? value,
        long deletedAt = 0)
    {
        var store = new FakeSystemStore();
        if (value is not null)
        {
            store.Add(
                SystemModelSettingsService.ModelSettingsKey,
                value,
                row => row.DeletedAt = deletedAt);
        }

        return await new SystemModelSettingsService(store).GetModelSettingsOrDefaultAsync();
    }
}
