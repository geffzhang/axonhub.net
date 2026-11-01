using AxonHub.Abstractions;
using AxonHub.Api.Models;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.ModelService.ListEnabledModels (internal/server/biz/model.go:646)
/// for the channel-derived half the port implements: every enabled channel
/// contributes its GetModelEntries keys, deduplicated across channels, filtered
/// by ModelBlacklistRegex, in channel order.
/// </summary>
/// <remarks>
/// The reference also prepends configured Model entities; the port has no
/// <c>models</c> table (C088), so only channel-derived facades exist.
/// </remarks>
public sealed class ModelServiceTests
{
    private static readonly DateTime CreatedAt =
        new(2024, 4, 5, 19, 34, 58, DateTimeKind.Utc);

    private static readonly string[] PrefixAndMappingIds = ["fast", "gpt-4o", "openai/gpt-4o"];

    private static readonly string[] NullModelIds = [string.Empty, "gpt-4o"];

    private static async Task<IReadOnlyList<ModelFacade>> ListAsync(
        ModelSettings? settings,
        params ChannelDescriptor[] channels)
    {
        var provider = new FakeEnabledChannelProvider();
        provider.Set(channels);

        return await new ModelService(
                provider,
                new StaticModelSettingsProvider(settings ?? ModelSettings.Default))
            .ListEnabledModelsAsync();
    }

    [Fact]
    public async Task ChannelModelsBecomeFacades()
    {
        var models = await ListAsync(
            null,
            TestChannels.OpenAi(1, "gpt-4o") with
            {
                CreatedAt = CreatedAt,
                SupportedModels = """["gpt-4o","gpt-4o-mini"]""",
            });

        Assert.Collection(
            models,
            model =>
            {
                Assert.Equal("gpt-4o", model.Id);
                Assert.Equal("gpt-4o", model.DisplayName);
                Assert.Equal(CreatedAt, model.CreatedAt);
                Assert.Equal(1712345698L, model.Created);
                Assert.Equal("openai", model.OwnedBy);
            },
            model => Assert.Equal("gpt-4o-mini", model.Id));
    }

    [Fact]
    public async Task PrefixAndMappingAliasesAreListed()
    {
        // GetModelEntries: prefix aliases plus mapping aliases whose target is
        // supported. The list is sorted per channel (the reference's Go map
        // order is unspecified; C088).
        var models = await ListAsync(
            null,
            TestChannels.OpenAi(1, "gpt-4o") with
            {
                Settings =
                    """{"extraModelPrefix":"openai","modelMappings":[{"from":"fast","to":"gpt-4o"}]}""",
            });

        Assert.Equal(
            PrefixAndMappingIds,
            models.Select(static model => model.Id));
    }

    [Fact]
    public async Task DuplicateModelUsesTheFirstChannel()
    {
        var models = await ListAsync(
            null,
            TestChannels.OpenAi(1, "gpt-4o") with { CreatedAt = CreatedAt },
            TestChannels.Jina(2, "gpt-4o") with { CreatedAt = CreatedAt.AddDays(1) });

        var model = Assert.Single(models);
        Assert.Equal("gpt-4o", model.Id);
        Assert.Equal("openai", model.OwnedBy);
        Assert.Equal(CreatedAt, model.CreatedAt);
    }

    [Fact]
    public async Task BlacklistRegexFiltersChannelModels()
    {
        var models = await ListAsync(
            ModelSettings.Default with { ModelBlacklistRegex = "gpt-.*" },
            TestChannels.OpenAi(1, "gpt-4o") with
            {
                SupportedModels = """["gpt-4o","claude-3"]""",
            });

        Assert.Equal("claude-3", Assert.Single(models).Id);
    }

    [Fact]
    public async Task BlacklistWithoutMetacharactersIsAnExactMatch()
    {
        var models = await ListAsync(
            ModelSettings.Default with { ModelBlacklistRegex = "gpt-4o" },
            TestChannels.OpenAi(1, "gpt-4o") with
            {
                SupportedModels = """["gpt-4o","gpt-4o-mini"]""",
            });

        Assert.Equal("gpt-4o-mini", Assert.Single(models).Id);
    }

    [Fact]
    public async Task QueryAllChannelModelsFalseListsNoModels()
    {
        // The reference lists configured Model entities in this mode; the port
        // has none (C088).
        var models = await ListAsync(
            ModelSettings.Default with { QueryAllChannelModels = false },
            TestChannels.OpenAi(1, "gpt-4o"));

        Assert.Empty(models);
    }

    [Fact]
    public async Task NullSupportedModelBecomesAnEmptyIdLikeGo()
    {
        // Go unmarshals a null element of []string into "", so the reference
        // lists a facade whose id is empty.
        var models = await ListAsync(
            null,
            TestChannels.OpenAi(1, "gpt-4o") with
            {
                SupportedModels = """["gpt-4o",null]""",
            });

        Assert.Equal(
            NullModelIds,
            models.Select(static model => model.Id));
    }
}
