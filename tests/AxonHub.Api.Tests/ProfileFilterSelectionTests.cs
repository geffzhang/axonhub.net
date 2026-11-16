using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the profile half of the selectCandidates middleware
/// (select_candidates.go:27-51) against candidates_basic_test.go's
/// SelectedChannelsSelector cases, candidates_tags_test.go's tag matrix and the
/// decorator-intersection tests: project profile first, then the API key's
/// active profile, both narrowing the base selector's candidates.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ProfileFilterSelectionTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IModelCandidateSelector selector,
        RetryPolicy policy)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(policy),
            selector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static RetryPolicy Policy(int maxChannelRetries = 2)
        => new()
        {
            Enabled = maxChannelRetries > 0,
            MaxChannelRetries = maxChannelRetries,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
        };

    /// <summary>
    /// Builds the real DefaultSelector over an in-memory channel store, with one
    /// channel-model association per requested (channel, priority, tags) tuple.
    /// </summary>
    private ModelCandidateSelector Selector(
        string model,
        params (long ChannelId, int Priority, string? Tags)[] associations)
    {
        var models = new FakeModelStore();
        models.Seed(row =>
        {
            row.ModelId = model;
            row.Status = "enabled";
            row.Settings = ModelSettingsCodec.SerializeEntitySettings(new ModelEntitySettings
            {
                Associations = [.. associations.Select(association => new ModelAssociation
                {
                    Type = ModelAssociation.TypeChannelModel,
                    Priority = association.Priority,
                    ChannelModel = new ChannelModelAssociation(
                        (int)association.ChannelId,
                        model),
                })],
            });
        });

        var channels = Channels(
            associations.Select(static a => (a.ChannelId, a.Tags)),
            model);

        return new ModelCandidateSelector(
            models,
            new StaticModelSettingsProvider(ModelSettings.Default),
            channels,
            fixture.Channels,
            TimeProvider.System,
            bypassModelPrivacy: true);
    }

    /// <summary>
    /// Builds a selector whose model row is absent, so the legacy
    /// channel-only fallback produces the candidates (FallbackToChannelsOnModelNotFound
    /// defaults to true).
    /// </summary>
    private static ModelCandidateSelector FallbackSelector(
        string model,
        params (long ChannelId, string? Tags)[] channels)
    {
        // The legacy path reads the enabled-channel snapshot, like the
        // reference's GetEnabledChannels, not the association store.
        var enabled = new FakeEnabledChannelProvider();
        enabled.Set([.. channels.Select(channel => new ChannelDescriptor
        {
            Id = channel.ChannelId,
            Name = "channel-" + channel.ChannelId,
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://channel-" + channel.ChannelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channel.ChannelId + "\"}",
            SupportedModels = "[\"" + model + "\"]",
            Tags = channel.Tags,
        })]);

        return new ModelCandidateSelector(
            new FakeModelStore(),
            new StaticModelSettingsProvider(ModelSettings.Default),
            new FakeChannelStore(),
            enabled,
            TimeProvider.System,
            bypassModelPrivacy: true);
    }

    private static FakeChannelStore Channels(
        IEnumerable<(long ChannelId, string? Tags)> channels,
        string model)
    {
        var store = new FakeChannelStore();
        foreach (var (channelId, tags) in channels)
        {
            store.Add(new ChannelRecord
            {
                Id = channelId,
                Name = "channel-" + channelId,
                Type = "openai",
                Status = "enabled",
                BaseUrl = "http://channel-" + channelId + ".test",
                Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
                SupportedModels = "[\"" + model + "\"]",
                Tags = tags,
            });
        }

        return store;
    }

    private static ScriptedExecutor FailingExecutor()
        => new() { OnDo = (_, _, _) => throw TestChannels.HttpClient(500) };

    private static RequestIdentity Identity(
        ApiKeyProfileModel? keyProfile = null,
        ProjectRecord? project = null)
    {
        var apiKey = keyProfile is null
            ? null
            : new ApiKeyRecord
            {
                Id = 2,
                ProjectId = 1,
                Profiles = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
                {
                    ActiveProfile = "p",
                    Profiles = [keyProfile],
                }),
            };

        return new RequestIdentity(1, apiKey?.Id)
        {
            ApiKey = apiKey,
            Project = project,
        };
    }

    private static ApiKeyProfileModel KeyProfile(
        IReadOnlyList<long>? channelIds = null,
        IReadOnlyList<string>? tags = null,
        string matchMode = "")
        => new()
        {
            Name = "p",
            ChannelIDs = channelIds,
            ChannelTags = tags,
            ChannelTagsMatchMode = matchMode,
        };

    private static ProjectRecord Project(
        IReadOnlyList<int>? channelIds = null,
        IReadOnlyList<string>? tags = null,
        string matchMode = "")
        => new()
        {
            Id = 1,
            Profiles = ProjectProfilesCodec.Serialize(new ProjectProfilesModel
            {
                ActiveProfile = "p",
                Profiles =
                [
                    new ProjectProfileModel
                    {
                        Name = "p",
                        ChannelIDs = channelIds,
                        ChannelTags = tags,
                        ChannelTagsMatchMode = matchMode,
                    },
                ],
            }),
        };

    [Fact]
    public async Task KeyProfileChannelIdsFilterTheCandidates()
    {
        const string model = "profile-ids-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5601, 0, null),
            (5602, 1, null),
            (5603, 2, null));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy());

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(channelIds: [5601, 5603])),
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5601.test/v1/chat/completions",
                "http://channel-5603.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task EmptyProfileFiltersReturnEveryCandidate()
    {
        const string model = "profile-empty-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5604, 0, null),
            (5605, 1, null));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 1));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile()),
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5604.test/v1/chat/completions",
                "http://channel-5605.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task ProjectAndKeyProfilesIntersect()
    {
        // select_candidates.go applies the project profile first and the key
        // profile second, so a channel must satisfy both.
        const string model = "profile-intersect-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5611, 0, null),
            (5612, 1, null),
            (5613, 2, null));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy());

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(
                KeyProfile(channelIds: [5612, 5613]),
                Project(channelIds: [5611, 5612])),
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5612.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task ProjectProfileIsIgnoredWithoutAnApiKey()
    {
        // The reference only reads the project edge from the API key, so a
        // project in context without a key must not narrow the candidates.
        const string model = "profile-no-key-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5621, 0, null),
            (5622, 1, null));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 1));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(keyProfile: null, Project(channelIds: [5622])),
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5621.test/v1/chat/completions",
                "http://channel-5622.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task AnyTagModeMatchesOverlappingTags()
    {
        const string model = "profile-any-tags-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5631, 0, """["a","b"]"""),
            (5632, 1, """["b"]"""),
            (5633, 2, """["c"]"""));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 2));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(tags: ["a", "b"])),
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5631.test/v1/chat/completions",
                "http://channel-5632.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task UnknownTagMatchModeBehavesLikeAny()
    {
        const string model = "profile-unknown-mode-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5636, 0, """["a"]"""),
            (5637, 1, """["c"]"""));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 1));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(tags: ["a"], matchMode: "bogus")),
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5636.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task AllTagModeRequiresEveryAllowedTag()
    {
        const string model = "profile-all-tags-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5631, 0, """["a","b"]"""),
            (5632, 1, """["b"]"""),
            (5633, 2, """["c"]"""));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 2));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(tags: ["a", "b"], matchMode: "all")),
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5631.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task NoneTagModeExcludesChannelsWithAnyAllowedTag()
    {
        const string model = "profile-none-tags-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5631, 0, """["a","b"]"""),
            (5632, 1, """["b"]"""),
            (5633, 2, """["c"]"""));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 2));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(tags: ["a", "b"], matchMode: "none")),
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5633.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task KeyProfileTagsAndChannelIdsIntersectWithinOneProfile()
    {
        const string model = "profile-ids-tags-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5651, 0, """["b"]"""),
            (5652, 1, """["b"]"""),
            (5653, 2, """["c"]"""));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy());

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(channelIds: [5651, 5653], tags: ["b"])),
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5651.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task ChannelsWithoutTagsNeverMatchAnAllowList()
    {
        const string model = "profile-no-tags-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5641, 0, null),
            (5642, 1, "null"));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy());

        var exception = await Assert.ThrowsAsync<TransformerException>(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(tags: ["a"])),
            onRequestCreated: null));

        Assert.Equal("model not found: " + model, exception.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task ProfileFiltersApplyToTheChannelFallback()
    {
        // The reference decorators wrap whatever base selector produced the
        // candidates, including the legacy channel fallback.
        const string model = "profile-fallback-model";
        fixture.Channels.Set();
        var selector = FallbackSelector(
            model,
            (5661, """["a"]"""),
            (5662, """["b"]"""));
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(executor, selector, Policy(maxChannelRetries: 1));

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            Identity(KeyProfile(tags: ["a"])),
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5661.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task SpecifiedCandidateIsFilteredByTheApiKeyProfile()
    {
        // The tester's processor runs the same selectCandidates middleware; a
        // profile that excludes the specified channel yields the invalid-model
        // error before any upstream call.
        const string model = "profile-specified-model";
        fixture.Channels.Set();
        var orchestrator = NewOrchestrator(
            FailingExecutor(),
            Selector(model),
            Policy());
        var candidate = new ChannelCandidate
        {
            ChannelId = 5671,
            ChannelName = "channel-5671",
            ActualModel = model,
            Type = "openai",
            BaseUrl = "http://channel-5671.test",
            Credentials = "{\"apiKey\":\"sk-5671\"}",
            Tags = """["a"]""",
        };

        var exception = await Assert.ThrowsAsync<TransformerException>(() =>
            orchestrator.ProcessSpecifiedAsync(
                new OpenAiInboundTransformer(),
                TestChannels.ChatRequest(model),
                (_, _) => Task.FromResult<ChannelCandidate?>(candidate),
                Identity(KeyProfile(tags: ["b"])), CancellationToken.None));

        Assert.Equal("model not found: " + model, exception.Message);
    }
}
