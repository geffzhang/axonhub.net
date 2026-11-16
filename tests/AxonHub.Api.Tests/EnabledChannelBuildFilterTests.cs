using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the build half of the reference's enabled-channel refresh
/// (channel.go:253-278): every enabled row is built with
/// <c>buildChannelWithOutbounds</c>, and a failure logs a warning and drops the
/// row from the cache. The port runs that check in
/// <see cref="BuildFilteringEnabledChannelSource"/>, ahead of the snapshot swap
/// (§213, C178).
/// </summary>
public sealed class EnabledChannelBuildFilterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private const string ValidKeyCredentials = """{"apiKey":"sk-test"}""";

    private static ChannelDescriptor Channel(
        long id,
        string type = "openai",
        string credentials = ValidKeyCredentials,
        string? baseUrl = null,
        string? endpoints = null)
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials,
            SupportedModels = """["gpt-4o"]""",
            DefaultTestModel = "gpt-4o",
            Endpoints = endpoints,
        };

    private static async Task<ChannelDescriptor[]> ReloadAsync(
        params ChannelDescriptor[] rows)
        => await new BuildFilteringEnabledChannelSource(
                new FakeEnabledChannelSource(rows),
                new TestTimeProvider(Now))
            .ListEnabledAsync();

    [Fact]
    public async Task GetLatestUpdatedAtDelegatesToTheInnerSource()
    {
        var latest = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var inner = new FakeEnabledChannelSource([]) { LatestUpdatedAt = latest };
        var source = new BuildFilteringEnabledChannelSource(
            inner,
            new TestTimeProvider(Now));

        var fingerprint = await source.GetLatestUpdatedAtAsync();

        Assert.Equal(latest, fingerprint);
        Assert.Equal(0, inner.ListCalls);
    }

    [Fact]
    public async Task ChannelsThatBuildKeepTheirOrderAndSkippedRowsDisappear()
    {
        var rows = await ReloadAsync(
            Channel(1, baseUrl: "https://api.example.com/v1"),
            Channel(2, credentials: "{}"),
            Channel(3, baseUrl: "https://api.example.com/v1"));

        Assert.Equal([1L, 3L], rows.Select(static row => row.Id));
    }

    [Fact]
    public async Task MissingApiKeySkipsTheChannel()
    {
        var rows = await ReloadAsync(
            Channel(1, credentials: "{}"),
            Channel(2, credentials: """{"apiKey":"  "}"""));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task OAuthOnlyChannelTypesRequireAnOAuthDocument()
    {
        // channel_llm.go:670-676: github_copilot and xai_subscription are
        // OAuth-only; a plain key fails the build.
        var keyOnly = await ReloadAsync(
            Channel(1, "xai_subscription", credentials: ValidKeyCredentials));
        var oauth = await ReloadAsync(
            Channel(2, "xai_subscription", credentials: """{"oauth":{"access_token":"tok"}}"""));

        Assert.Empty(keyOnly);
        Assert.Single(oauth);
    }

    [Fact]
    public async Task CodexChannelsAcceptEitherAnApiKeyOrOAuth()
    {
        // channel_llm.go:660-668: codex/claudecode need "oauth or api key".
        var rows = await ReloadAsync(
            Channel(1, "codex"),
            Channel(2, "codex", credentials: """{"oauth":{"access_token":"tok"}}"""),
            Channel(3, "codex", credentials: "{}"));

        Assert.Equal([1L, 2L], rows.Select(static row => row.Id));
    }

    [Fact]
    public async Task UnknownChannelTypeSkipsTheChannel()
    {
        var rows = await ReloadAsync(
            Channel(1, "no_such_channel_type", baseUrl: "https://api.example.com/v1"),
            Channel(2, baseUrl: "https://api.example.com/v1"));

        Assert.Equal([2L], rows.Select(static row => row.Id));
    }

    [Fact]
    public async Task CommandCodeRequiresAnHttpsBaseUrl()
    {
        // channel_llm.go:640-647: both Command Code types reject non-HTTPS.
        var http = await ReloadAsync(
            Channel(1, "commandcode", baseUrl: "http://api.example.com"));
        var https = await ReloadAsync(
            Channel(2, "commandcode", baseUrl: "https://api.example.com"));

        Assert.Empty(http);
        Assert.Single(https);
    }

    [Fact]
    public async Task UserEndpointsAreBuiltAndAWebSocketChatEndpointSkipsTheChannel()
    {
        // channel_llm.go:240-252 builds every user endpoint; the port's
        // buildNonDefaultEndpointOutbound equivalent only allows WebSocket for
        // the Responses family (channel_llm.go:450-452).
        var valid = await ReloadAsync(Channel(
            1,
            endpoints:
            """[{"api_format":"openai/chat_completions","base_url":"https://api.example.com/v1","path":"/custom"}]"""));
        var invalidWebSocket = await ReloadAsync(Channel(
            2,
            endpoints:
            """[{"api_format":"openai/chat_completions","transport":"websocket"}]"""));

        Assert.Single(valid);
        Assert.Empty(invalidWebSocket);
    }

    [Fact]
    public async Task AnUnbuildableSecondaryEndpointSkipsTheWholeChannel()
    {
        // One good endpoint does not rescue the channel: the reference returns
        // on the first failing endpoint (channel_llm.go:244-247).
        var rows = await ReloadAsync(Channel(
            1,
            endpoints: """
                [
                  {"api_format":"openai/chat_completions","base_url":"https://api.example.com/v1"},
                  {"api_format":"anthropic/messages","transport":"websocket"}
                ]
                """));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task BuildFailureIsLoggedWithTheChannelAndType()
    {
        var logger = new RecordingLogger<BuildFilteringEnabledChannelSource>();
        var source = new BuildFilteringEnabledChannelSource(
            new FakeEnabledChannelSource([Channel(7, "no_such_channel_type")]),
            new TestTimeProvider(Now),
            logger);

        var rows = await source.ListEnabledAsync();

        Assert.Empty(rows);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("channel-7", entry.Message, StringComparison.Ordinal);
        Assert.Contains("no_such_channel_type", entry.Message, StringComparison.Ordinal);
        Assert.NotNull(entry.Exception);
    }

    private sealed class FakeEnabledChannelSource(ChannelDescriptor[] channels)
        : IEnabledChannelSource
    {
        public DateTime? LatestUpdatedAt { get; init; }

        public int ListCalls { get; private set; }

        public Task<DateTime?> GetLatestUpdatedAtAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromResult(LatestUpdatedAt);
        }

        public Task<ChannelDescriptor[]> ListEnabledAsync(
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            ListCalls++;
            return Task.FromResult(channels);
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
