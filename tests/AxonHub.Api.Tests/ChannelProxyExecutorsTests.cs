using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the channel-level proxy seam (internal/server/biz/channel_llm.go:113-130,
/// 700-774 and internal/server/orchestrator/outbound.go:1164-1196): a channel
/// with <c>settings.proxy</c> gets its own cached client, the tester's
/// per-request proxy wins over it, and a channel without a proxy keeps the
/// shared executor.
/// </summary>
public sealed class ChannelProxyExecutorsTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AChannelWithoutProxyKeepsTheSharedExecutor()
    {
        var shared = new ScriptedExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var service = new ChannelProxyExecutors(shared, factory);

        var resolved = service.Resolve(Candidate(701, settings: null));

        Assert.Same(shared, resolved);
        Assert.Empty(factory.Requests);
    }

    [Fact]
    public void AChannelProxyBuildsADedicatedExecutor()
    {
        var shared = new ScriptedExecutor();
        var proxyExecutor = new ScriptedExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxyExecutor };
        var service = new ChannelProxyExecutors(shared, factory);
        var candidate = Candidate(
            702,
            """
            {"proxy":{"type":"url","url":"http://proxy.test:8080","username":"u","password":"p","disableConnectionReuse":true}}
            """);

        var resolved = service.Resolve(candidate);

        Assert.Same(proxyExecutor, resolved);

        var config = Assert.Single(factory.Requests);
        Assert.Equal(ProxyType.Url, config.Type);
        Assert.Equal("http://proxy.test:8080", config.Url);
        Assert.Equal("u", config.Username);
        Assert.Equal("p", config.Password);
        Assert.True(config.DisableConnectionReuse);
    }

    [Fact]
    public void TheSameProxyValueIsBuiltOnceAndReused()
    {
        var factory = new ScriptedProxyExecutorFactory { Executor = new ScriptedExecutor() };
        var service = new ChannelProxyExecutors(new ScriptedExecutor(), factory);
        var candidate = Candidate(
            703,
            """{"proxy":{"type":"url","url":"http://proxy.test:8080"}}""");

        var first = service.Resolve(candidate);
        var second = service.Resolve(candidate);

        Assert.Same(first, second);
        Assert.Single(factory.Requests);
    }

    [Fact]
    public void AChangedProxyValueReplacesTheCachedChannelExecutor()
    {
        var factory = new ScriptedProxyExecutorFactory();
        var service = new ChannelProxyExecutors(new ScriptedExecutor(), factory);
        var candidate = Candidate(
            704,
            """{"proxy":{"type":"url","url":"http://first.test:8080"}}""");

        factory.Executor = new ScriptedExecutor();
        var first = service.Resolve(candidate);

        // The reference rebuilds the channel client whenever the enabled-channel
        // cache entry is rebuilt; the port sees the new snapshot in the candidate.
        candidate.Settings = """{"proxy":{"type":"url","url":"http://second.test:8080"}}""";
        factory.Executor = new ScriptedExecutor();
        var second = service.Resolve(candidate);

        Assert.NotSame(first, second);
        Assert.Equal(2, factory.Requests.Count);
        Assert.Equal("http://second.test:8080", factory.Requests[1].Url);
    }

    [Fact]
    public void AProxyTypeDisabledStillGetsADedicatedExecutor()
    {
        // getHttpClient only returns the shared client when settings or proxy is
        // nil. A disabled proxy is a real configuration: it must suppress the
        // environment proxy rather than fall back to the shared client.
        var shared = new ScriptedExecutor();
        var proxyExecutor = new ScriptedExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxyExecutor };
        var service = new ChannelProxyExecutors(shared, factory);
        var candidate = Candidate(705, """{"proxy":{"type":"disabled"}}""");

        var resolved = service.Resolve(candidate);

        Assert.Same(proxyExecutor, resolved);
        Assert.Equal(ProxyType.Disabled, Assert.Single(factory.Requests).Type);
    }

    [Fact]
    public void OverridesAlwaysBuildAFreshClient()
    {
        var factory = new ScriptedProxyExecutorFactory { Executor = new ScriptedExecutor() };
        var service = new ChannelProxyExecutors(new ScriptedExecutor(), factory);
        var proxy = new ProxyConfig
        {
            Type = ProxyType.Url,
            Url = "http://override.test:8080",
        };

        service.CreateOverride(proxy);
        service.CreateOverride(proxy);

        // channel.HTTPClient.WithProxy(state.Proxy) constructs a new client for
        // every call instead of reusing a cached one.
        Assert.Equal(2, factory.Requests.Count);
        Assert.All(factory.Requests, config => Assert.Equal("http://override.test:8080", config.Url));
    }

    [Fact]
    public void TheTransformerUsesTheChannelSettingsProxy()
    {
        var shared = new ScriptedExecutor();
        var proxyExecutor = new ScriptedExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxyExecutor };
        var service = new ChannelProxyExecutors(shared, factory);
        var transformer = NewTransformer(
            Candidate(706, """{"proxy":{"type":"url","url":"http://channel.proxy:8080"}}"""),
            service);

        transformer.TransformRequest(Request());
        var customized = transformer.CustomizeExecutor(shared);

        Assert.Same(proxyExecutor, customized);
        Assert.Equal("http://channel.proxy:8080", Assert.Single(factory.Requests).Url);
    }

    [Fact]
    public void TheTesterProxyOverrideWinsOverTheChannelSetting()
    {
        var shared = new ScriptedExecutor();
        var proxyExecutor = new ScriptedExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxyExecutor };
        var service = new ChannelProxyExecutors(shared, factory);
        var transformer = NewTransformer(
            Candidate(707, """{"proxy":{"type":"url","url":"http://channel.proxy:8080"}}"""),
            service,
            proxyOverride: new ProxyConfig
            {
                Type = ProxyType.Url,
                Url = "http://test.proxy:8080",
            });

        transformer.TransformRequest(Request());
        var customized = transformer.CustomizeExecutor(shared);

        Assert.Same(proxyExecutor, customized);

        // The channel's cached client is never built when the override wins.
        var config = Assert.Single(factory.Requests);
        Assert.Equal("http://test.proxy:8080", config.Url);
    }

    // -- Helpers ------------------------------------------------------------

    private static LlmRequest Request() => new()
    {
        Model = "test-model",
        RequestType = "chat",
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
    };

    private static PersistentOutboundTransformer NewTransformer(
        ChannelCandidate candidate,
        ChannelProxyExecutors proxyExecutors,
        ProxyConfig? proxyOverride = null)
        => new(
            [candidate],
            new TestTimeProvider(Now),
            new RecordingMetricsRecorder(),
            proxyExecutors: proxyExecutors,
            proxyOverride: proxyOverride);

    private static ChannelCandidate Candidate(long channelId, string? settings)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = "test-model",
            Type = "openai",
            BaseUrl = "http://upstream.test/v1",
            Credentials = """{"apiKey":"test-key"}""",
            ApiFormat = "openai/chat_completions",
            Settings = settings,
        };
        candidate.Models.Add(new ChannelCandidateModel
        {
            RequestModel = "test-model",
            ActualModel = "test-model",
        });

        return candidate;
    }
}
