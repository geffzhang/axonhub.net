using System.Text.Json.Nodes;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks orchestrator.DefaultSelector (internal/server/orchestrator/candidates.go
/// 110-615) against the reference tests in candidates_basic_test.go,
/// candidates_condition_test.go and candidates_dedup_test.go: the missing-model
/// fallback, the ent privacy deny texts, every association kind, the when
/// conditions, the aggregation rules, API-format population, model-card output
/// limits and the routing-policy normalization.
/// </summary>
public sealed class ModelCandidateSelectorTests
{
    private const string Model = "m";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly ModelSettings FallbackEnabled = new()
    {
        FallbackToChannelsOnModelNotFound = true,
    };

    // -- Fallback to the legacy channel pool ---------------------------------

    [Fact]
    public async Task MissingModelFallsBackToChannelCandidates()
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(11, """["m"]"""));

        var selection = await CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(11, candidate.ChannelId);
        Assert.Equal(0, candidate.Priority);
        Assert.Null(candidate.ModelApiFormats);
        Assert.Equal(0, candidate.DefaultMaxTokens);
        Assert.Null(selection.RoutingPolicy);

        var model = Assert.Single(candidate.Models);
        Assert.Equal("m", model.ActualModel);
        Assert.Equal("openai/chat_completions", candidate.ApiFormat);
    }

    [Fact]
    public async Task MissingModelWithoutFallbackRaisesInvalidModel()
    {
        var ex = await Assert.ThrowsAsync<TransformerException>(
            () => CreateSelector(
                    new FakeModelStore(),
                    new ModelSettings { FallbackToChannelsOnModelNotFound = false })
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Equal(TransformerErrorKind.InvalidModel, ex.Kind);
        Assert.Equal("model not found: \"m\"", ex.Message);
    }

    [Fact]
    public async Task DisabledModelRowFallsBackLikeAMissingRow()
    {
        var models = new FakeModelStore();
        SeedModel(models, status: "disabled");

        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(12, """["m"]"""));

        var selection = await CreateSelector(models, FallbackEnabled, enabled: enabled)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        // GetModelByModelID filters StatusEQ(enabled), so a disabled row is
        // ent.IsNotFound and takes the fallback path.
        Assert.Single(selection.Candidates);
        Assert.Null(selection.RoutingPolicy);
    }

    [Fact]
    public async Task FallbackSelectsEveryEnabledChannelSupportingTheModel()
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(
            FallbackChannel(13, """["m"]"""),
            FallbackChannel(14, """["other"]"""),
            FallbackChannel(15, """["m","other"]"""));

        var selection = await CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(
            [13L, 15L],
            selection.Candidates.Select(static candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task ConfiguredModelWithoutAssociationsDoesNotFallBackToChannels()
    {
        var models = new FakeModelStore();
        SeedModel(models);

        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(16, """["m"]"""));

        var selection = await CreateSelector(models, FallbackEnabled, enabled: enabled)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        // The fallback only covers a missing row; an existing model with no
        // effective association returns an empty list.
        Assert.Empty(selection.Candidates);
        Assert.Null(selection.RoutingPolicy);
    }

    [Fact]
    public async Task EmptyRequestModelFallsBackToAnEmptyCandidateList()
    {
        var selection = await CreateSelector(new FakeModelStore(), FallbackEnabled)
            .SelectAsync(Request(string.Empty), RequestIdentity.Default);

        Assert.Empty(selection.Candidates);
        Assert.Null(selection.RoutingPolicy);
    }

    [Fact]
    public async Task FallbackSkipsExplicitEndpointRequestsTheChannelCannotServe()
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(17, """["m"]"""));

        var selection = await CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled)
            .SelectAsync(
                Request("m", requestType: "systemone"),
                RequestIdentity.Default);

        // An openai channel exposes no systemone endpoint, and the request type
        // requires an explicit match.
        Assert.Empty(selection.Candidates);
    }

    // -- Model resolution through the fallback path --------------------------

    [Fact]
    public async Task PrefixAutoTrimAndMappingResolveLikeTheReference()
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(
            FallbackChannel(
                801,
                """["prefix-upstream"]""",
                """{"extraModelPrefix":"azure"}"""),
            FallbackChannel(
                802,
                """["vendor/trim-upstream"]""",
                """{"autoTrimedModelPrefixes":["vendor"]}"""),
            FallbackChannel(
                803,
                """["mapped-upstream"]""",
                """{"modelMappings":[{"from":"mapped-alias","to":"mapped-upstream"}]}"""));

        var selector = CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled);

        Assert.Equal("prefix-upstream", await ActualModel(selector, "azure/prefix-upstream"));
        Assert.Equal("vendor/trim-upstream", await ActualModel(selector, "trim-upstream"));
        Assert.Equal("mapped-upstream", await ActualModel(selector, "mapped-alias"));
    }

    [Fact]
    public async Task HideOriginalAndMappedModelsRemovesDirectAliases()
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(
            901,
            """["hidden-target"]""",
            """
            {"modelMappings":[{"from":"hidden-alias","to":"hidden-target"}],
             "hideOriginalModels":true,"hideMappedModels":true}
            """));

        var selector = CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled);

        Assert.Empty((await selector
            .SelectAsync(Request("hidden-target"), RequestIdentity.Default)).Candidates);
        Assert.Equal("hidden-target", await ActualModel(selector, "hidden-alias"));
    }

    [Fact]
    public async Task LowercaseModelIdMatchesCaseInsensitively()
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(1001, """["LOWER-MODEL"]""", """{"lowercaseModelId":true}"""));

        var selector = CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled);

        Assert.Equal("LOWER-MODEL", await ActualModel(selector, "lower-model"));
    }

    [Theory]
    [InlineData(
        """["gpt-6-sol","select-a"]""",
        """{"modelMappings":[{"from":"main","to":"gpt-6-sol"}]}""",
        " main ",
        "select-a",
        "gpt-6-sol")]
    [InlineData(
        """["gpt-6-sol","select-b"]""",
        """{"modelMappings":[{"from":"main","to":"gpt-6-sol"}],"lowercaseModelId":true}""",
        "MAIN",
        "select-b",
        "gpt-6-sol")]
    [InlineData("""["gpt-6-sol"]""", null, "  gpt-6-sol  ", "gpt-6-sol", "gpt-6-sol")]
    [InlineData(
        """["gpt-6-sol","select-d"]""",
        null,
        "unknown-model",
        "select-d",
        "unknown-model")]
    public async Task DefaultTestModelIsTrimmedAndResolvedThroughModelEntries(
        string supportedModels,
        string? settings,
        string defaultTestModel,
        string requestModel,
        string expected)
    {
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(
            2601,
            supportedModels,
            settings,
            type: "codex",
            defaultTestModel: defaultTestModel));

        var selection = await CreateSelector(new FakeModelStore(), FallbackEnabled, enabled: enabled)
            .SelectAsync(Request(requestModel), RequestIdentity.Default);

        Assert.Equal(expected, Assert.Single(selection.Candidates).DefaultTestModel);
    }

    // -- Ent privacy ---------------------------------------------------------

    [Fact]
    public async Task PrivacyDeniesAnApiKeyWithoutReadChannels()
    {
        var identity = new RequestIdentity(1, 2)
        {
            ApiKey = new ApiKeyRecord { Scopes = """["write_requests"]""" },
        };

        var ex = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => CreateSelector(new FakeModelStore(), FallbackEnabled, bypassPrivacy: false)
                .SelectAsync(Request("m"), identity));

        Assert.Equal(
            "failed to query AxonHub Model: API key does not have required scope: "
            + "read_channels: ent/privacy: deny rule: \"m\"",
            ex.Message);
    }

    [Fact]
    public async Task PrivacyDeniesAContextWithoutAUser()
    {
        var ex = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => CreateSelector(new FakeModelStore(), FallbackEnabled, bypassPrivacy: false)
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Equal(
            "failed to query AxonHub Model: no user in context: ent/privacy: deny rule: \"m\"",
            ex.Message);
    }

    [Fact]
    public async Task AnApiKeyWithReadChannelsMayQueryTheModel()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(21, "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 21);

        var identity = new RequestIdentity(1, 2)
        {
            ApiKey = new ApiKeyRecord { Scopes = """["read_channels"]""" },
        };

        var selection = await CreateSelector(models, FallbackEnabled, channels, bypassPrivacy: false)
            .SelectAsync(Request("m"), identity);

        Assert.Single(selection.Candidates);
    }

    // -- Association kinds ---------------------------------------------------

    [Fact]
    public async Task ChannelModelAssociationSelectsTheNamedChannelAndModel()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(31, "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 31);
        SeedChannel(channels, 32);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(31, candidate.ChannelId);
        Assert.Equal(0, candidate.Priority);
        Assert.Equal("m", Assert.Single(candidate.Models).ActualModel);
    }

    [Fact]
    public async Task ChannelRegexAssociationSelectsTheNamedChannelModels()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelRegexAssoc(41, "alpha")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 41, """["alpha","beta"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal("alpha", Assert.Single(candidate.Models).ActualModel);
    }

    [Fact]
    public async Task RegexAssociationScansEveryChannel()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(RegexAssoc("m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 51);
        SeedChannel(channels, 52);
        SeedChannel(channels, 53, """["other"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(
            [51L, 52L],
            selection.Candidates.Select(static candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task ExcludeRulesDropChannelsByIdNameAndTag()
    {
        var models = new FakeModelStore();
        // xregexp anchors the pattern as ^(?:skip-.*)$, so a bare "skip-" would
        // only match a channel named exactly that.
        SeedModel(models, settings: EntitySettings(RegexAssoc(
            "m",
            new ExcludeAssociation("skip-.*", [52], ["blocked"]))));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 51);
        SeedChannel(channels, 52);
        SeedChannel(channels, 53, name: "skip-five");
        SeedChannel(channels, 54, tags: """["blocked"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(51L, Assert.Single(selection.Candidates).ChannelId);
    }

    [Fact]
    public async Task ModelAssociationMatchesEveryChannelExposingTheModel()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ModelAssoc("m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 61);
        SeedChannel(channels, 62);
        SeedChannel(channels, 63, """["other"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(
            [61L, 62L],
            selection.Candidates.Select(static candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task ChannelTagsModelAssociationRequiresAMatchingTag()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelTagsModelAssoc(["premium"], "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 71, tags: """["premium"]""");
        SeedChannel(channels, 72, tags: """["basic"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(71L, Assert.Single(selection.Candidates).ChannelId);
    }

    [Fact]
    public async Task ChannelTagsRegexAssociationRequiresAMatchingTag()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelTagsRegexAssoc(["premium"], "^m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 81, """["m","other"]""", tags: """["premium"]""");
        SeedChannel(channels, 82, tags: """["basic"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(81L, Assert.Single(selection.Candidates).ChannelId);
    }

    [Fact]
    public async Task DisabledAssociationsAreIgnoredWithoutFallingBack()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(
            ChannelModelAssoc(91, "m") with { Disabled = true }));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 91);

        var enabled = new FakeEnabledChannelProvider();
        enabled.Set(FallbackChannel(92, """["m"]"""));

        var selection = await CreateSelector(models, FallbackEnabled, channels, enabled)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Empty(selection.Candidates);
    }

    [Fact]
    public async Task DeveloperAssociationsAreInheritedByTheModel()
    {
        var systemSettings = new ModelSettings
        {
            FallbackToChannelsOnModelNotFound = true,
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    "acme",
                    [
                        new ModelAssociation
                        {
                            Type = ModelAssociation.TypeChannelModel,
                            ChannelModel = new ChannelModelAssociation(101, string.Empty),
                        },
                    ]),
            ],
        };

        var models = new FakeModelStore();
        SeedModel(models, developer: "acme", settings: "{}");

        var channels = new FakeChannelStore();
        SeedChannel(channels, 101);

        var selection = await CreateSelector(models, systemSettings, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(101L, Assert.Single(selection.Candidates).ChannelId);
    }

    [Fact]
    public async Task DisableDeveloperSettingsInheritanceDropsTheRules()
    {
        var systemSettings = new ModelSettings
        {
            FallbackToChannelsOnModelNotFound = true,
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    "acme",
                    [
                        new ModelAssociation
                        {
                            Type = ModelAssociation.TypeChannelModel,
                            ChannelModel = new ChannelModelAssociation(102, string.Empty),
                        },
                    ]),
            ],
        };

        var models = new FakeModelStore();
        SeedModel(
            models,
            developer: "acme",
            settings: """{"disableDeveloperSettingsInheritance":true}""");

        var channels = new FakeChannelStore();
        SeedChannel(channels, 102);

        var selection = await CreateSelector(models, systemSettings, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Empty(selection.Candidates);
    }

    // -- When conditions -----------------------------------------------------

    [Fact]
    public async Task RequestFormatConditionFiltersTheAssociation()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(ChannelModelAssoc(
                111,
                "m",
                when: When("request_format", "eq", JsonValue.Create("anthropic/messages")))));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 111);

        var selector = CreateSelector(models, FallbackEnabled, channels);

        Assert.Single((await selector.SelectAsync(
            Request("m", apiFormat: "anthropic/messages"),
            RequestIdentity.Default)).Candidates);
        Assert.Empty((await selector.SelectAsync(
            Request("m", apiFormat: "openai/chat_completions"),
            RequestIdentity.Default)).Candidates);
    }

    [Fact]
    public async Task ReasoningEffortConditionMatchesTheRequestEffort()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(ChannelModelAssoc(
                112,
                "m",
                when: When("reasoning_effort", "eq", JsonValue.Create("high")))));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 112);

        var selector = CreateSelector(models, FallbackEnabled, channels);

        var matching = Request("m");
        matching.ReasoningEffort = "high";
        Assert.Single((await selector.SelectAsync(matching, RequestIdentity.Default)).Candidates);

        var other = Request("m");
        other.ReasoningEffort = "low";
        Assert.Empty((await selector.SelectAsync(other, RequestIdentity.Default)).Candidates);
    }

    [Fact]
    public async Task ContentFeatureConditionMatchesStructuredParts()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(ChannelModelAssoc(
                113,
                "m",
                when: When("has_image", "eq", JsonValue.Create(true)))));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 113);

        var selector = CreateSelector(models, FallbackEnabled, channels);

        var withImage = Request("m");
        withImage.Messages.Add(new Message
        {
            Role = "user",
            Content = MessageContent.FromParts(
            [
                new MessageContentPart
                {
                    Type = "image_url",
                    ImageUrl = new ImageUrl { Url = "https://example.test/a.png" },
                },
            ]),
        });

        Assert.Single((await selector.SelectAsync(withImage, RequestIdentity.Default)).Candidates);
        Assert.Empty((await selector.SelectAsync(Request("m"), RequestIdentity.Default)).Candidates);
    }

    [Fact]
    public async Task DisabledWhenConditionsAreUnconditional()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(ChannelModelAssoc(
                114,
                "m",
                when: new ModelAssociationWhen(
                    Enabled: false,
                    Condition: When("request_format", "eq", JsonValue.Create("never"))
                        .Condition))));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 114);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Single(selection.Candidates);
    }

    [Fact]
    public async Task ConditionalAssociationsFilterBeforeAggregation()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(
                ChannelModelAssoc(
                    115,
                    "m",
                    priority: 0,
                    when: When("request_format", "eq", JsonValue.Create("anthropic/messages"))),
                ChannelModelAssoc(115, "m", priority: 1)));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 115);

        var selector = CreateSelector(models, FallbackEnabled, channels);

        // Both associations match for an Anthropic request; the global
        // (channel, model) dedupe keeps the first (priority 0) candidate.
        var anthropic = await selector.SelectAsync(
            Request("m", apiFormat: "anthropic/messages"),
            RequestIdentity.Default);
        Assert.Equal(0, Assert.Single(anthropic.Candidates).Priority);

        // The conditional rule is filtered out for OpenAI, leaving the
        // unconditional priority-1 fallback.
        var openAi = await selector.SelectAsync(
            Request("m", apiFormat: "openai/chat_completions"),
            RequestIdentity.Default);
        Assert.Equal(1, Assert.Single(openAi.Candidates).Priority);
    }

    // -- Aggregation ---------------------------------------------------------

    [Fact]
    public async Task DuplicateActualModelsAreDroppedWithinACandidate()
    {
        var models = new FakeModelStore();
        // xregexp anchors "gpt" as ^(?:gpt)$, so the wildcard is required to match
        // the "gpt-4" and "gpt4" entries (the reference test uses "gpt.*").
        SeedModel(models, settings: EntitySettings(RegexAssoc("gpt.*")));

        var channels = new FakeChannelStore();
        // "gpt4" is a mapping alias for "gpt-4", so the regex matches two
        // entries of the same channel that resolve to one upstream model.
        SeedChannel(
            channels,
            121,
            """["gpt-4"]""",
            """{"modelMappings":[{"from":"gpt4","to":"gpt-4"}]}""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        var entry = Assert.Single(candidate.Models);
        Assert.Equal("gpt-4", entry.ActualModel);
    }

    [Fact]
    public async Task ConnectionsOnTheSameChannelAndPriorityMergeIntoOneCandidate()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(
                ChannelModelAssoc(131, "m"),
                ChannelModelAssoc(131, "m2")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 131, """["m","m2"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(
            ["m", "m2"],
            candidate.Models.Select(static entry => entry.ActualModel).ToArray());
    }

    [Fact]
    public async Task SameChannelDifferentPrioritiesStaySeparateCandidates()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(
                ChannelModelAssoc(141, "m", priority: 0),
                ChannelModelAssoc(141, "m2", priority: 1)));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 141, """["m","m2"]""");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(
            [0, 1],
            selection.Candidates.Select(static candidate => candidate.Priority).ToArray());
    }

    [Fact]
    public async Task ARepeatedActualModelNeverReentersALaterCandidate()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(
                ChannelModelAssoc(151, "m", priority: 0),
                ChannelModelAssoc(151, "m", priority: 1)));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 151);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(0, candidate.Priority);
    }

    // -- API format population -----------------------------------------------

    [Fact]
    public async Task PopulateApiFormatPrefersTheInboundFormat()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(161, "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 161);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(
                Request("m", apiFormat: "openai/chat_completions"),
                RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal("openai/chat_completions", candidate.ApiFormat);
        Assert.Equal(
            ["openai/chat_completions"],
            candidate.ModelApiFormats!);
    }

    [Fact]
    public async Task PerModelProtocolOverridesProduceAModelFormatTable()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(RegexAssoc(".*")));

        var channels = new FakeChannelStore();
        SeedChannel(
            channels,
            171,
            """["alpha","beta"]""",
            """
            {"modelProtocols":[
              {"model":"alpha","apiFormats":["openai/chat_completions"]},
              {"model":"beta","apiFormats":["anthropic/messages"]}]}
            """,
            type: "custom-endpoints",
            endpoints: """
            [{"api_format":"openai/chat_completions"},
             {"api_format":"anthropic/messages"}]
            """);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal(
            ["openai/chat_completions", "anthropic/messages"],
            candidate.ModelApiFormats!);
        Assert.Equal("openai/chat_completions", candidate.ApiFormat);
    }

    [Fact]
    public async Task ExplicitEndpointRequestsDropCandidatesWithoutAMatchingFormat()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(181, "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 181);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m", requestType: "systemone"), RequestIdentity.Default);

        Assert.Empty(selection.Candidates);
    }

    [Fact]
    public async Task ExplicitEndpointRequestsKeepChannelsThatExposeTheFormat()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(182, "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 182, type: "typesafe");

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m", requestType: "systemone"), RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal("typesafe/systemone", candidate.ApiFormat);
        Assert.Equal(["typesafe/systemone"], candidate.ModelApiFormats!);
    }

    // -- Model card and routing policy ---------------------------------------

    [Fact]
    public async Task TheModelCardOutputLimitBecomesDefaultMaxTokens()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: EntitySettings(ChannelModelAssoc(191, "m")),
            card: """{"limit":{"context":200000,"output":8192}}""");

        var channels = new FakeChannelStore();
        SeedChannel(channels, 191);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(8192, Assert.Single(selection.Candidates).DefaultMaxTokens);
    }

    [Fact]
    public async Task AZeroOutputLimitLeavesDefaultMaxTokensUnset()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(192, "m")));

        var channels = new FakeChannelStore();
        SeedChannel(channels, 192);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(0, Assert.Single(selection.Candidates).DefaultMaxTokens);
    }

    [Fact]
    public async Task RoutingPolicyIsReadFromTheModelSettings()
    {
        var models = new FakeModelStore();
        SeedModel(
            models,
            settings: """
            {"associations":[{"type":"channel_model","priority":0,
             "channelModel":{"channelId":201,"modelId":"m"}}],
             "loadBalancerStrategy":"round-robin",
             "traceStickyMode":"system_default"}
            """);

        var channels = new FakeChannelStore();
        SeedChannel(channels, 201);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(
            new ModelRoutingPolicy("round-robin", "default"),
            selection.RoutingPolicy);
    }

    [Theory]
    [InlineData(
        """{"associations":[{"type":"channel_model","priority":0,"channelModel":{"channelId":202,"modelId":"m"}}]}""")]
    [InlineData(
        """{"associations":[{"type":"channel_model","priority":0,"channelModel":{"channelId":202,"modelId":"m"}}],"loadBalancerStrategy":"system_default","traceStickyMode":"system_default"}""")]
    public async Task MissingPolicyValuesDefaultToDefault(string modelSettings)
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: modelSettings);

        var channels = new FakeChannelStore();
        SeedChannel(channels, 202);

        var selection = await CreateSelector(models, FallbackEnabled, channels)
            .SelectAsync(Request("m"), RequestIdentity.Default);

        Assert.Equal(
            new ModelRoutingPolicy("default", "default"),
            selection.RoutingPolicy);
    }

    // -- Error wrapping ------------------------------------------------------

    [Fact]
    public async Task ModelQueryFailuresAreWrappedWithTheModel()
    {
        var models = new FakeModelStore
        {
            GetFirstByModelIdAndStatusFailure = new InvalidOperationException("boom"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSelector(models, FallbackEnabled)
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Equal("failed to query AxonHub Model: boom: \"m\"", ex.Message);
    }

    [Fact]
    public async Task AssociationResolutionFailuresAreWrappedWithTheModel()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: EntitySettings(ChannelModelAssoc(211, "m")));

        var channels = new FakeChannelStore
        {
            ListEnabledFailure = new InvalidOperationException("boom"),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSelector(models, FallbackEnabled, channels)
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Equal("failed to resolve associations: boom: \"m\"", ex.Message);
    }

    [Fact]
    public async Task ModelCardDecodeFailuresAreWrapped()
    {
        var models = new FakeModelStore();
        SeedModel(models, card: "[1]");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSelector(models, FallbackEnabled)
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Equal(
            "failed to query AxonHub Model: unmarshal field model_card: "
            + "json: cannot unmarshal array into Go value of type objects.ModelCard: \"m\"",
            ex.Message);
    }

    [Fact]
    public async Task ModelSettingsDecodeFailuresAreWrapped()
    {
        var models = new FakeModelStore();
        SeedModel(models, settings: "[1]");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSelector(models, FallbackEnabled)
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Equal(
            "failed to query AxonHub Model: unmarshal field settings: "
            + "failed to unmarshal model settings: json: cannot unmarshal array into "
            + "Go value of type objects.modelSettingsAlias: \"m\"",
            ex.Message);
    }

    [Fact]
    public async Task ACorruptModelCardWinsOverCorruptSettings()
    {
        var models = new FakeModelStore();
        SeedModel(models, card: "[1]", settings: "[1]");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSelector(models, FallbackEnabled)
                .SelectAsync(Request("m"), RequestIdentity.Default));

        Assert.Contains("unmarshal field model_card", ex.Message);
    }

    // -- Helpers -------------------------------------------------------------

    private static ModelCandidateSelector CreateSelector(
        FakeModelStore models,
        ModelSettings settings,
        FakeChannelStore? channels = null,
        FakeEnabledChannelProvider? enabled = null,
        bool bypassPrivacy = true)
        => new(
            models,
            new StaticModelSettingsProvider(settings),
            channels ?? new FakeChannelStore(),
            enabled ?? new FakeEnabledChannelProvider(),
            new TestTimeProvider(Now),
            bypassPrivacy);

    private static LlmRequest Request(
        string model,
        string? apiFormat = null,
        string? requestType = "chat")
        => new()
        {
            Model = model,
            RequestType = requestType,
            ApiFormat = apiFormat,
        };

    private static async Task<string> ActualModel(
        ModelCandidateSelector selector,
        string requestModel)
    {
        var selection = await selector.SelectAsync(
            Request(requestModel),
            RequestIdentity.Default);
        var candidate = Assert.Single(selection.Candidates);
        return Assert.Single(candidate.Models).ActualModel;
    }

    private static ModelRecord SeedModel(
        FakeModelStore models,
        string modelId = Model,
        string developer = "",
        string settings = "{}",
        string card = "{}",
        string status = "enabled")
        => models.Seed(row =>
        {
            row.ModelId = modelId;
            row.Developer = developer;
            row.Settings = settings;
            row.ModelCard = card;
            row.Status = status;
        });

    private static ChannelRecord SeedChannel(
        FakeChannelStore channels,
        long id,
        string supportedModels = """["m"]""",
        string? settings = null,
        string type = "openai",
        string? name = null,
        string? tags = null,
        string? endpoints = null)
        => channels.Add(new ChannelRecord
        {
            Id = id,
            Name = name ?? "channel-" + id,
            Type = type,
            Status = "enabled",
            SupportedModels = supportedModels,
            Settings = settings,
            Tags = tags,
            Endpoints = endpoints,
        });

    private static ChannelDescriptor FallbackChannel(
        long id,
        string supportedModels,
        string? settings = null,
        string type = "openai",
        string defaultTestModel = "")
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = type,
            BaseUrl = "http://upstream.test",
            Credentials = """{"apiKey":"sk-test"}""",
            SupportedModels = supportedModels,
            Settings = settings,
            DefaultTestModel = defaultTestModel,
        };

    private static string EntitySettings(params ModelAssociation[] associations)
        => ModelSettingsCodec.SerializeEntitySettings(new ModelEntitySettings
        {
            Associations = associations,
        });

    private static ModelAssociation ChannelModelAssoc(
        long channelId,
        string modelId,
        int priority = 0,
        ModelAssociationWhen? when = null)
        => new()
        {
            Type = ModelAssociation.TypeChannelModel,
            Priority = priority,
            When = when,
            ChannelModel = new ChannelModelAssociation((int)channelId, modelId),
        };

    private static ModelAssociation ChannelRegexAssoc(
        long channelId,
        string pattern,
        int priority = 0)
        => new()
        {
            Type = ModelAssociation.TypeChannelRegex,
            Priority = priority,
            ChannelRegex = new ChannelRegexAssociation((int)channelId, pattern),
        };

    private static ModelAssociation RegexAssoc(
        string pattern,
        ExcludeAssociation? exclude = null,
        int priority = 0)
        => new()
        {
            Type = ModelAssociation.TypeRegex,
            Priority = priority,
            Regex = new RegexAssociation(pattern, exclude is null ? null : [exclude]),
        };

    private static ModelAssociation ModelAssoc(
        string modelId,
        ExcludeAssociation? exclude = null,
        int priority = 0)
        => new()
        {
            Type = ModelAssociation.TypeModel,
            Priority = priority,
            ModelId = new ModelIdAssociation(modelId, exclude is null ? null : [exclude]),
        };

    private static ModelAssociation ChannelTagsModelAssoc(
        IReadOnlyList<string> tags,
        string modelId,
        int priority = 0)
        => new()
        {
            Type = ModelAssociation.TypeChannelTagsModel,
            Priority = priority,
            ChannelTagsModel = new ChannelTagsModelAssociation(tags, modelId),
        };

    private static ModelAssociation ChannelTagsRegexAssoc(
        IReadOnlyList<string> tags,
        string pattern,
        int priority = 0)
        => new()
        {
            Type = ModelAssociation.TypeChannelTagsRegex,
            Priority = priority,
            ChannelTagsRegex = new ChannelTagsRegexAssociation(tags, pattern),
        };

    private static ModelAssociationWhen When(string field, string op, JsonNode? value)
        => new(
            Enabled: true,
            Condition: new FilterCondition
            {
                Type = FilterCondition.TypeCondition,
                Field = field,
                Operator = op,
                Value = value,
            });
}
