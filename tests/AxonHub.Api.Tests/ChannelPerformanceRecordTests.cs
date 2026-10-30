using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the port of markPerformanceTerminal (internal/server/orchestrator/
/// outbound.go), the switch that decides whether a classified stream terminal
/// touches a channel's health counters.
/// </summary>
public sealed class ChannelPerformanceRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static ChannelPerformanceRecord NewRecord() => new()
    {
        ChannelId = 7,
        StartTime = Now.AddSeconds(-1),
    };

    [Theory]
    [InlineData(StreamTerminalState.Completed, true, false)]
    [InlineData(StreamTerminalState.Incomplete, true, false)]
    [InlineData(StreamTerminalState.Canceled, false, true)]
    [InlineData(StreamTerminalState.Failed, false, false)]
    public void StreamTerminalClassificationMatchesReference(
        StreamTerminalState state,
        bool success,
        bool canceled)
    {
        var record = NewRecord();

        record.MarkStreamTerminal(state, "boom", Now);

        Assert.True(record.RequestCompleted);
        Assert.Equal(success, record.Success);
        Assert.Equal(canceled, record.Canceled);
        Assert.Equal(Now, record.EndTime);

        if (state == StreamTerminalState.Failed)
        {
            // The reference marks a failed terminal with a synthetic 500 and the
            // terminal event's message.
            Assert.Equal(500, record.ResponseStatusCode);
            Assert.Equal("boom", record.ErrorMessage);
        }
    }

    [Fact]
    public void TerminalWithoutEventMarksNothing()
    {
        var record = NewRecord();

        record.MarkStreamTerminal(StreamTerminalState.None, string.Empty, Now);

        Assert.False(record.RequestCompleted);
        Assert.False(record.IsValid);
    }

    [Fact]
    public void FirstTerminalOutcomeWinsLikeMarkPerformanceTerminal()
    {
        var record = NewRecord();

        record.MarkStreamTerminal(StreamTerminalState.Completed, string.Empty, Now);
        record.MarkStreamTerminal(StreamTerminalState.Failed, "late transport error", Now.AddSeconds(1));

        Assert.True(record.Success);
        Assert.False(record.Canceled);
        Assert.Equal(string.Empty, record.ErrorMessage);
        Assert.Equal(Now, record.EndTime);
    }

    [Fact]
    public void RecordWithoutChannelIsNotValid()
    {
        var record = new ChannelPerformanceRecord { ChannelId = 0, StartTime = Now };

        record.MarkSuccess(Now);

        // PerformanceRecord.IsValid requires a channel: nothing may be reported.
        Assert.False(record.IsValid);
    }
}
