using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks orchestrator.PersistentOutboundTransformer.ResponseTimeouts
/// (outbound.go:913-940): only a custom channel responseTimeout overrides the
/// pipeline defaults, each field independently; inherit, absent and malformed
/// settings return null so the global retry policy values remain in effect.
/// </summary>
public sealed class ResponseTimeoutProviderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"responseTimeout":null}""")]
    [InlineData("""{"responseTimeout":{"mode":"inherit","streamFirstEventTimeoutSeconds":15,"nonStreamResponseTimeoutSeconds":25}}""")]
    [InlineData("""{"responseTimeout":{"streamFirstEventTimeoutSeconds":15,"nonStreamResponseTimeoutSeconds":25}}""")]
    public void InheritedOrAbsentSettingsReturnNoOverrides(string? settings)
    {
        var (stream, nonStream) = Transformer(settings).ResponseTimeouts();

        Assert.Null(stream);
        Assert.Null(nonStream);
    }

    [Fact]
    public void MalformedSettingsReturnNoOverrides()
    {
        var (stream, nonStream) = Transformer("{not json").ResponseTimeouts();

        Assert.Null(stream);
        Assert.Null(nonStream);
    }

    [Theory]
    [InlineData(
        """{"responseTimeout":{"mode":"custom","streamFirstEventTimeoutSeconds":15,"nonStreamResponseTimeoutSeconds":25}}""",
        15,
        25)]
    [InlineData(
        """{"responseTimeout":{"mode":"custom","streamFirstEventTimeoutSeconds":15}}""",
        15,
        -1)]
    [InlineData(
        """{"responseTimeout":{"mode":"custom","nonStreamResponseTimeoutSeconds":25}}""",
        -1,
        25)]
    [InlineData(
        """{"responseTimeout":{"mode":"custom","streamFirstEventTimeoutSeconds":0,"nonStreamResponseTimeoutSeconds":0}}""",
        0,
        0)]
    public void CustomSettingsReturnTheConfiguredSeconds(
        string settings,
        int streamSeconds,
        int nonStreamSeconds)
    {
        var (stream, nonStream) = Transformer(settings).ResponseTimeouts();

        Assert.Equal(ToDuration(streamSeconds), stream);
        Assert.Equal(ToDuration(nonStreamSeconds), nonStream);
    }

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("{}", 0, false)]
    [InlineData("""{"maxSingleChannelRetries":5}""", 5, true)]
    [InlineData("""{"maxSingleChannelRetries":0}""", 0, true)]
    public void SameChannelRetryLimitReadsTheChannelOverride(
        string? settings,
        int expectedLimit,
        bool expectedOverride)
    {
        var transformer = new PersistentOutboundTransformer(
            [new ChannelCandidate { Settings = settings }],
            new TestTimeProvider(DateTimeOffset.UtcNow),
            new RecordingMetricsRecorder());

        var (limit, hasOverride) = transformer.SameChannelRetryLimit();

        Assert.Equal(expectedLimit, limit);
        Assert.Equal(expectedOverride, hasOverride);
    }

    [Fact]
    public void NoCandidatesReturnNoOverrides()
    {
        var transformer = new PersistentOutboundTransformer(
            [],
            new TestTimeProvider(DateTimeOffset.UtcNow),
            new RecordingMetricsRecorder());

        var (stream, nonStream) = transformer.ResponseTimeouts();

        Assert.Null(stream);
        Assert.Null(nonStream);
    }

    private static PersistentOutboundTransformer Transformer(string? settings)
        => new(
            [new ChannelCandidate { Settings = settings }],
            new TestTimeProvider(DateTimeOffset.UtcNow),
            new RecordingMetricsRecorder());

    private static TimeSpan? ToDuration(int seconds)
        => seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
}
