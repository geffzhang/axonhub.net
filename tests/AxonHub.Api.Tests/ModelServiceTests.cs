using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.ModelService.ListEnabledModels (internal/server/biz/model.go:646)
/// and queryConfiguredModelFacades against the reference's test matrix
/// (model_test.go, model_list_routability_test.go): the configured entities
/// merged in front of channel-derived facades, the project/key profile filters,
/// the blacklist and the HideUnroutableModelsInList suppression (C178).
/// </summary>
public sealed class ModelServiceTests
{
    private const string ProfileModelIds = """
        {"activeProfile":"production","profiles":[{"name":"production","modelIDs":["gpt-4","claude-3-opus-20240229"]}]}
        """;

    private static IReadOnlyList<ChannelRecord> DefaultChannels()
    {
        var disabled = ModelTestSupport.Channel(
            4,
            "openai",
            "gpt-4-disabled",
            supportedModels: """["gpt-4-disabled"]""");
        disabled.Status = "disabled";

        return
        [
            ModelTestSupport.Channel(
                1,
                "openai",
                "gpt-4",
                supportedModels: """["gpt-4","gpt-3.5-turbo"]"""),
            ModelTestSupport.Channel(
                2,
                "anthropic",
                "claude-3-opus-20240229",
                supportedModels: """["claude-3-opus-20240229"]""",
                settings: """
                    {"modelMappings":[{"from":"claude-3-opus","to":"claude-3-opus-20240229"},{"from":"claude-opus","to":"claude-3-opus-20240229"}]}
                    """),
            ModelTestSupport.Channel(
                3,
                "openai",
                "deepseek-chat",
                supportedModels: """["deepseek-chat","deepseek-reasoner"]""",
                settings: """{"extraModelPrefix":"deepseek"}"""),
            disabled,
        ];
    }

    private static async Task<IReadOnlyList<ModelFacade>> ListAsync(
        ModelService service) => await service.ListEnabledModelsAsync();

    private static HashSet<string> Ids(IReadOnlyList<ModelFacade> models)
        => [.. models.Select(static model => model.Id)];

    private static void AssertSameIds(
        IEnumerable<string> expected, IReadOnlyList<ModelFacade> actual)
        => Assert.Equal(
            expected.OrderBy(static id => id, StringComparer.Ordinal),
            actual.Select(static model => model.Id)
                .OrderBy(static id => id, StringComparer.Ordinal));

    [Fact]
    public async Task ListAllEnabledModelsFromChannels()
    {
        var models = await ListAsync(ModelTestSupport.Service(DefaultChannels()));

        AssertSameIds(
            [
                "gpt-4",
                "gpt-3.5-turbo",
                "claude-3-opus-20240229",
                "claude-3-opus",
                "claude-opus",
                "deepseek-chat",
                "deepseek-reasoner",
                "deepseek/deepseek-chat",
                "deepseek/deepseek-reasoner",
            ],
            models);
        Assert.Equal(9, models.Count);

        // The disabled channel's models never appear.
        Assert.DoesNotContain("gpt-4-disabled", Ids(models));
    }

    [Fact]
    public async Task FacadesExposeIdDisplayNameAndChannelType()
    {
        var models = await ListAsync(ModelTestSupport.Service(DefaultChannels()));

        Assert.All(models, model =>
        {
            Assert.NotEmpty(model.Id);
            Assert.NotEmpty(model.DisplayName);
            Assert.NotEmpty(model.OwnedBy);
            Assert.Equal(model.Id, model.DisplayName);
        });

        var byId = models.ToDictionary(static model => model.Id);
        Assert.Equal("openai", byId["gpt-4"].OwnedBy);
        Assert.Equal("openai", byId["deepseek/deepseek-chat"].OwnedBy);
        Assert.Equal("anthropic", byId["claude-3-opus"].OwnedBy);
        Assert.Equal("anthropic", byId["claude-opus"].OwnedBy);
        Assert.Equal(ModelTestSupport.CreatedAt, byId["gpt-4"].CreatedAt);
        Assert.Equal(1712345698L, byId["gpt-4"].Created);
    }

    [Fact]
    public async Task ApiKeyWithActiveProfileModelIdsReturnsOnlySpecifiedModels()
    {
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(ProfileModelIds));

        var models = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), identity: identity));

        AssertSameIds(["gpt-4", "claude-3-opus-20240229"], models);

        var byId = models.ToDictionary(static model => model.Id);
        Assert.Equal("openai", byId["gpt-4"].OwnedBy);
        Assert.Equal("anthropic", byId["claude-3-opus-20240229"].OwnedBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"activeProfile":"","profiles":[{"name":"production","modelIDs":["gpt-4"]}]}""")]
    [InlineData("""{"activeProfile":"non-existent","profiles":[{"name":"production","modelIDs":["gpt-4"]}]}""")]
    [InlineData("""{"activeProfile":"development","profiles":[{"name":"development","modelIDs":[],"modelMappings":[{"from":"gpt-4","to":"gpt-4"}]}]}""")]
    public async Task ProfilesWithoutAnActiveRestrictionReturnAllModels(string? profiles)
    {
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(profiles));

        var models = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), identity: identity));

        AssertSameIds(
            Ids(await ListAsync(ModelTestSupport.Service(DefaultChannels()))),
            models);
        Assert.Contains("gpt-4", Ids(models));
        Assert.Contains("gpt-3.5-turbo", Ids(models));
    }

    [Fact]
    public async Task NoApiKeyPrincipalWithOwnerIdentityReturnsAllModels()
    {
        // The reference fixture installs authz.WithTestBypass; the port's
        // equivalent is an owner/test management user (C178).
        var models = await ListAsync(ModelTestSupport.Service(DefaultChannels()));

        Assert.Contains("gpt-4", Ids(models));
        Assert.Contains("claude-3-opus-20240229", Ids(models));
    }

    [Fact]
    public async Task MappingToUnsupportedModelIsIgnored()
    {
        var channels = new List<ChannelRecord>(DefaultChannels())
        {
            ModelTestSupport.Channel(
                5,
                "openai",
                "gpt-4",
                supportedModels: """["gpt-4"]""",
                settings: """
                    {"modelMappings":[{"from":"gpt-4-latest","to":"gpt-4"},{"from":"invalid-mapping","to":"unsupported-model"}]}
                    """),
        };

        var ids = Ids(await ListAsync(ModelTestSupport.Service(channels)));

        Assert.Contains("gpt-4", ids);
        Assert.Contains("gpt-4-latest", ids);
        Assert.DoesNotContain("invalid-mapping", ids);
    }

    [Fact]
    public async Task AutoTrimmedModelsAreListed()
    {
        var channels = new List<ChannelRecord>
        {
            ModelTestSupport.Channel(
                6,
                "openai",
                "provider/gpt-4",
                supportedModels: """["provider/gpt-4","provider/gpt-3.5-turbo"]""",
                settings: """{"autoTrimedModelPrefixes":["provider"]}"""),
        };

        var ids = Ids(await ListAsync(ModelTestSupport.Service(channels)));

        Assert.Contains("gpt-4", ids);
        Assert.Contains("gpt-3.5-turbo", ids);
    }

    [Fact]
    public async Task DenyWithoutReadChannelsThrowsTheWrappedPrivacyDeny()
    {
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(scopes: "write_requests"));

        var service = ModelTestSupport.Service(DefaultChannels(), identity: identity);

        var error = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => service.ListEnabledModelsAsync());

        Assert.Equal(
            "failed to list configured models: ent/privacy: deny rule",
            error.Message);
        Assert.True(PrivacyDenyException.IsDeny(error));
    }

    [Fact]
    public async Task ListReadsTheEnabledChannelSnapshotNotTheStore()
    {
        // GetEnabledChannels reads the enabled-channel cache, not the table
        // (C178). The store's row must stay invisible and the snapshot's row
        // visible even though only the store was queried before §213.
        var storeChannels = new[]
        {
            ModelTestSupport.Channel(1, "openai", "from-store"),
        };

        var snapshot = new[]
        {
            new ChannelDescriptor
            {
                Id = 2,
                Name = "channel-2",
                OrderingWeight = 0,
                CreatedAt = ModelTestSupport.CreatedAt,
                UpdatedAt = ModelTestSupport.CreatedAt,
                Type = "openai",
                SupportedModels = """["from-snapshot"]""",
                DefaultTestModel = "from-snapshot",
            },
        };

        var provider = new CountingEnabledChannelProvider(snapshot);
        var (service, channels, _) = ModelTestSupport.ServiceWithStores(
            storeChannels,
            enabledChannels: provider);

        var models = await ListAsync(service);

        Assert.Equal(["from-snapshot"], models.Select(static model => model.Id));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, channels.ListEnabledCalls);
    }

    [Fact]
    public async Task StoreFailureIsWrappedLikeTheConfiguredQuery()
    {
        var (service, _, modelStore) = ModelTestSupport.ServiceWithStores(DefaultChannels());
        modelStore.ListAllFailure = new InvalidOperationException("db is down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListEnabledModelsAsync());

        Assert.Equal("failed to list configured models: db is down", error.Message);
    }

    [Fact]
    public async Task MalformedConfiguredSettingsFailTheWholeQuery()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel("broken", settings: "[1]"),
        };

        var service = ModelTestSupport.Service(DefaultChannels(), models);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListEnabledModelsAsync());

        Assert.Equal(
            "failed to list configured models: unmarshal field settings: "
            + "failed to unmarshal model settings: json: cannot unmarshal array "
            + "into Go value of type objects.modelSettingsAlias",
            error.Message);
    }

    [Fact]
    public async Task MalformedConfiguredModelCardFailsTheWholeQuery()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel("broken", modelCard: "[1]"),
        };

        var service = ModelTestSupport.Service(DefaultChannels(), models);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ListEnabledModelsAsync());

        Assert.Equal(
            "failed to list configured models: unmarshal field model_card: "
            + "json: cannot unmarshal array into Go value of type objects.ModelCard",
            error.Message);
    }

    [Fact]
    public async Task BlacklistRegexFiltersChannelDerivedModels()
    {
        var settings = ModelSettings.Default with { ModelBlacklistRegex = "deepseek.*" };

        var models = await ListAsync(ModelTestSupport.Service(DefaultChannels(), settings: settings));
        var ids = Ids(models);

        Assert.DoesNotContain("deepseek-chat", ids);
        Assert.DoesNotContain("deepseek-reasoner", ids);
        Assert.DoesNotContain("deepseek/deepseek-chat", ids);
        Assert.DoesNotContain("deepseek/deepseek-reasoner", ids);
        Assert.Contains("gpt-4", ids);
        Assert.Contains("claude-3-opus-20240229", ids);
    }

    [Fact]
    public async Task EmptyBlacklistPatternKeepsEveryModel()
    {
        var settings = ModelSettings.Default with { ModelBlacklistRegex = string.Empty };

        var ids = Ids(await ListAsync(ModelTestSupport.Service(DefaultChannels(), settings: settings)));

        Assert.Contains("gpt-4", ids);
        Assert.Contains("deepseek-chat", ids);
    }

    [Fact]
    public async Task BlacklistIsNotEffectiveWhenQueryAllChannelModelsIsFalse()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel(
                "gpt-4-blacklist-bypass",
                settings: ModelTestSupport.ModelIdSettings("gpt-4")),
        };
        var settings = ModelSettings.Default with
        {
            QueryAllChannelModels = false,
            ModelBlacklistRegex = ".*",
        };

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), models, settings)));

        Assert.Contains("gpt-4-blacklist-bypass", ids);
        Assert.DoesNotContain("deepseek-chat", ids);
    }

    [Fact]
    public async Task BlacklistDoesNotFilterConfiguredModels()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel(
                "gpt-4-cfg-protected",
                settings: ModelTestSupport.ModelIdSettings("gpt-4")),
        };
        var settings = ModelSettings.Default with
        {
            ModelBlacklistRegex = "gpt-4-cfg-protected",
        };

        var listed = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), models, settings));
        var found = Assert.Single(listed, model => model.Id == "gpt-4-cfg-protected");

        Assert.Equal("configured", found.OwnedBy);
    }

    [Fact]
    public async Task BlacklistExactStringPatternUsesTheExactMatchPath()
    {
        var settings = ModelSettings.Default with { ModelBlacklistRegex = "deepseek-chat" };

        var ids = Ids(await ListAsync(ModelTestSupport.Service(DefaultChannels(), settings: settings)));

        Assert.DoesNotContain("deepseek-chat", ids);
        Assert.Contains("deepseek-reasoner", ids);
        Assert.Contains("deepseek/deepseek-chat", ids);
    }

    [Fact]
    public async Task QueryAllChannelModelsFalseReturnsConfiguredModelsOnly()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel("gpt-4", settings: ModelTestSupport.ModelIdSettings("gpt-4")),
            ModelTestSupport.ConfiguredModel(
                "claude-3-opus",
                developer: "anthropic",
                settings: ModelTestSupport.ModelIdSettings("claude-3-opus-20240229")),
            ModelTestSupport.ConfiguredModel("gpt-3.5-turbo", settings: "{}"),
            ModelTestSupport.ConfiguredModel(
                "gpt-4-turbo",
                settings: """{"associations":[]}"""),
        };
        var settings = ModelSettings.Default with { QueryAllChannelModels = false };

        var result = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), models, settings));

        AssertSameIds(["gpt-4", "claude-3-opus"], result);
        Assert.Equal("configured", result[0].OwnedBy);
    }

    [Fact]
    public async Task QueryAllChannelModelsFalseWithProfileModelIds()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel(
                "gpt-4-configured",
                settings: ModelTestSupport.ChannelModelSettings(1, "gpt-4")),
            ModelTestSupport.ConfiguredModel(
                "gpt-3.5-turbo-configured",
                settings: ModelTestSupport.ChannelModelSettings(1, "gpt-3.5-turbo")),
        };
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","modelIDs":["gpt-4-configured"]}]}"""));
        var settings = ModelSettings.Default with { QueryAllChannelModels = false };

        var result = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), models, settings, identity));

        Assert.Equal("gpt-4-configured", Assert.Single(result).Id);
    }

    [Fact]
    public async Task ApiKeyChannelIdsFiltersChannels()
    {
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","channelIDs":[1]}]}"""));

        var models = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), identity: identity));

        Assert.NotEmpty(models);
        Assert.All(models, model => Assert.Equal("openai", model.OwnedBy));
        AssertSameIds(["gpt-4", "gpt-3.5-turbo"], models);
    }

    [Fact]
    public async Task ApiKeyChannelTagsFiltersChannels()
    {
        var channels = new List<ChannelRecord>(DefaultChannels())
        {
            ModelTestSupport.Channel(
                5,
                "openai",
                "tagged-model-1",
                supportedModels: """["tagged-model-1","tagged-model-2"]""",
                tags: """["production","team-a"]"""),
        };
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","channelTags":["production"]}]}"""));

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service(channels, identity: identity)));

        Assert.True(ids.SetEquals(["tagged-model-1", "tagged-model-2"]));
    }

    [Fact]
    public async Task ApiKeyChannelTagsAllMatchFiltersChannels()
    {
        var channels = new List<ChannelRecord>
        {
            ModelTestSupport.Channel(
                6,
                "openai",
                "all-tags-model",
                tags: """["production","team-a","official"]"""),
            ModelTestSupport.Channel(
                7,
                "openai",
                "partial-tags-model",
                tags: """["production","team-a"]"""),
        };
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","channelTags":["production","team-a","official"],"channelTagsMatchMode":"all"}]}"""));

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service(channels, identity: identity)));

        Assert.Contains("all-tags-model", ids);
        Assert.DoesNotContain("partial-tags-model", ids);
    }

    [Fact]
    public async Task ApiKeyChannelTagsNoneMatchExcludesTaggedChannels()
    {
        var channels = new List<ChannelRecord>
        {
            ModelTestSupport.Channel(
                8,
                "openai",
                "none-excluded-model",
                tags: """["cc","internal"]"""),
            ModelTestSupport.Channel(
                9,
                "openai",
                "none-allowed-model",
                tags: """["general"]"""),
        };
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","channelTags":["cc","internal"],"channelTagsMatchMode":"none"}]}"""));

        var models = await ListAsync(
            ModelTestSupport.Service(channels, identity: identity));
        var ids = Ids(models);

        Assert.NotEmpty(models);
        Assert.Contains("none-allowed-model", ids);
        Assert.DoesNotContain("none-excluded-model", ids);
    }

    [Fact]
    public async Task ApiKeyWithBothChannelIdsAndChannelTagsFiltersSequentially()
    {
        // ChannelIDs runs first, then ChannelTags narrows the same list: a
        // channel that matches the id but not the tag is removed (intersection).
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","channelIDs":[1],"channelTags":["non-existent-tag"]}]}"""));

        var models = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), identity: identity));

        Assert.Empty(models);
    }

    [Fact]
    public async Task ProjectProfileChannelIdsAndChannelTagsUseIntersection()
    {
        var channels = new List<ChannelRecord>
        {
            ModelTestSupport.Channel(10, "openai", "project-id-only-model"),
            ModelTestSupport.Channel(
                11,
                "openai",
                "project-matching-model",
                tags: """["project-allowed"]"""),
        };
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(),
            projectProfiles:
            """{"activeProfile":"project-production","profiles":[{"name":"project-production","channelIDs":[10,11],"channelTags":["project-allowed"]}]}""");

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service(channels, identity: identity)));

        Assert.Contains("project-matching-model", ids);
        Assert.DoesNotContain("project-id-only-model", ids);
    }

    [Fact]
    public async Task ProjectProfileAllMatchChannelTagsFiltersChannels()
    {
        var channels = new List<ChannelRecord>
        {
            ModelTestSupport.Channel(
                12,
                "openai",
                "project-all-tags-model",
                tags: """["project-a","project-b"]"""),
            ModelTestSupport.Channel(
                13,
                "openai",
                "project-partial-tags-model",
                tags: """["project-a"]"""),
        };
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(),
            projectProfiles:
            """{"activeProfile":"project-production","profiles":[{"name":"project-production","channelTags":["project-a","project-b"],"channelTagsMatchMode":"all"}]}""");

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service(channels, identity: identity)));

        Assert.Contains("project-all-tags-model", ids);
        Assert.DoesNotContain("project-partial-tags-model", ids);
    }

    [Fact]
    public async Task EmptyChannelsReturnEmptyModels()
    {
        var models = await ListAsync(ModelTestSupport.Service([]));

        Assert.Empty(models);
    }

    [Fact]
    public async Task QueryAllChannelModelsTrueWithProfileModelIdsFiltersModels()
    {
        var identity = ModelTestSupport.ApiKeyIdentity(ModelTestSupport.ApiKey(
            """{"activeProfile":"production","profiles":[{"name":"production","modelIDs":["gpt-4","claude-opus"]}]}"""));

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), identity: identity)));

        Assert.Contains("gpt-4", ids);
        Assert.Contains("claude-opus", ids);
        Assert.DoesNotContain("gpt-3.5-turbo", ids);
    }

    [Fact]
    public async Task QueryAllChannelModelsTrueIncludesConfiguredAndChannelModels()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel("gpt-4", settings: ModelTestSupport.ModelIdSettings("gpt-4")),
            ModelTestSupport.ConfiguredModel(
                "claude-3-opus",
                developer: "anthropic",
                settings: ModelTestSupport.ModelIdSettings("claude-3-opus-20240229")),
        };

        var result = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), models));
        var byId = result.ToDictionary(static model => model.Id);

        Assert.Equal("configured", byId["gpt-4"].OwnedBy);
        Assert.Equal("configured", byId["claude-3-opus"].OwnedBy);

        // Models not overridden by a configured entity still come from channels.
        Assert.True(byId.ContainsKey("gpt-3.5-turbo"));
        Assert.True(byId.ContainsKey("claude-3-opus-20240229"));
        Assert.True(byId.ContainsKey("deepseek-chat"));
    }

    [Fact]
    public async Task QueryAllChannelModelsTrueConfiguredModelsTakePriority()
    {
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel("gpt-4", settings: ModelTestSupport.ModelIdSettings("gpt-4")),
        };

        var result = await ListAsync(
            ModelTestSupport.Service(DefaultChannels(), models));

        var gpt4 = Assert.Single(result, model => model.Id == "gpt-4");
        Assert.Equal("configured", gpt4.OwnedBy);

        var seen = new HashSet<string>();
        Assert.All(result, model => Assert.True(seen.Add(model.Id), model.Id + " appears twice"));
    }

    [Fact]
    public async Task HideUnroutableModelsInListOffKeepsEveryConfiguredModel()
    {
        var (service, _, _) = RoutabilityFixture();

        var ids = Ids(await ListAsync(service));

        Assert.Contains("routable-chat", ids);
        Assert.Contains("unroutable-embedding", ids);
    }

    [Fact]
    public async Task HideUnroutableModelsInListHidesModelsWithoutACapableChannel()
    {
        var (service, _, _) = RoutabilityFixture(
            ModelSettings.Default with
            {
                QueryAllChannelModels = false,
                HideUnroutableModelsInList = true,
            });

        var ids = Ids(await ListAsync(service));

        Assert.Contains("routable-chat", ids);
        Assert.DoesNotContain("unroutable-embedding", ids);
    }

    [Fact]
    public async Task HiddenConfiguredModelDoesNotReappearAsAChannelDerivedFacade()
    {
        var (service, _, _) = RoutabilityFixture(
            ModelSettings.Default with
            {
                QueryAllChannelModels = true,
                HideUnroutableModelsInList = true,
            });

        var ids = Ids(await ListAsync(service));

        Assert.Contains("routable-chat", ids);
        Assert.DoesNotContain("unroutable-embedding", ids);
    }

    [Fact]
    public async Task HideUnroutableRespectsExcludedProjectTags()
    {
        var channel = ModelTestSupport.Channel(
            20,
            "openai",
            "tagged-only-model",
            tags: """["AAA"]""");
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel(
                "tagged-only-model",
                settings: ModelTestSupport.ChannelModelSettings(20, "tagged-only-model")),
        };
        var settings = ModelSettings.Default with
        {
            QueryAllChannelModels = false,
            HideUnroutableModelsInList = true,
        };
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(),
            projectProfiles:
            """{"activeProfile":"default","profiles":[{"name":"default","channelTags":["AAA"],"channelTagsMatchMode":"none"}]}""");

        var filtered = Ids(await ListAsync(
            ModelTestSupport.Service([channel], models, settings, identity)));
        var unfiltered = Ids(await ListAsync(
            ModelTestSupport.Service([channel], models, settings)));

        Assert.DoesNotContain("tagged-only-model", filtered);
        Assert.Contains("tagged-only-model", unfiltered);
    }

    [Fact]
    public async Task HideUnroutableKeepsChannelDerivedModels()
    {
        var channel = ModelTestSupport.Channel(
            21,
            "openai",
            "channel-only-model");
        var settings = ModelSettings.Default with
        {
            QueryAllChannelModels = true,
            HideUnroutableModelsInList = true,
        };

        var ids = Ids(await ListAsync(ModelTestSupport.Service([channel], settings: settings)));

        Assert.Contains("channel-only-model", ids);
    }

    [Fact]
    public async Task HideUnroutableDoesNotEvaluateWhenConditions()
    {
        // hasCapableEndpointForModel only looks at API formats, so a When
        // condition that could never match at request time does not hide the
        // model from the list.
        var channel = ModelTestSupport.Channel(22, "openai", "when-model");
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel(
                "when-model",
                settings: """
                    {"associations":[{"type":"channel_model","channelModel":{"channelId":22,"modelId":"when-model"},"when":{"enabled":true,"condition":{"type":"condition","field":"request_format","operator":"eq","value":"openai/moderation"}}}]}
                    """),
        };
        var settings = ModelSettings.Default with
        {
            QueryAllChannelModels = false,
            HideUnroutableModelsInList = true,
        };

        var ids = Ids(await ListAsync(
            ModelTestSupport.Service([channel], models, settings)));

        Assert.Contains("when-model", ids);
    }

    /// <summary>
    /// The reference routability fixture: an openai chat channel serving a chat
    /// model, and a responses-only channel serving an embedding model.
    /// </summary>
    private static (ModelService Service, FakeChannelStore Channels, FakeModelStore Models)
        RoutabilityFixture(ModelSettings? settings = null)
    {
        var channels = new List<ChannelRecord>
        {
            ModelTestSupport.Channel(30, "openai", "routable-chat"),
            ModelTestSupport.Channel(31, "openai_responses", "unroutable-embedding"),
        };
        var models = new[]
        {
            ModelTestSupport.ConfiguredModel(
                "routable-chat",
                settings: ModelTestSupport.ChannelModelSettings(30, "routable-chat")),
            ModelTestSupport.ConfiguredModel(
                "unroutable-embedding",
                type: "embedding",
                settings: ModelTestSupport.ChannelModelSettings(31, "unroutable-embedding")),
        };

        return ModelTestSupport.ServiceWithStores(channels, models, settings);
    }

    /// <summary>
    /// The enabled-channel cache: a fixed descriptor snapshot that never
    /// touches the store. Its call count proves the model list reads the
    /// cache, not the table (C178).
    /// </summary>
    private sealed class CountingEnabledChannelProvider(ChannelDescriptor[] snapshot)
        : IEnabledChannelProvider
    {
        public int Calls { get; private set; }

        public Task<ChannelDescriptor[]> ListEnabledAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Calls++;
            return Task.FromResult(snapshot);
        }
    }
}
