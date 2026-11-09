namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.PerformanceRecord (internal/server/biz/channel_metrics.go) reduced
/// to the fields the channel grain's counters depend on.
/// </summary>
/// <remarks>
/// The reference creates one record per outbound attempt and lets three places
/// mark it: the performance middleware (non-stream success and raw errors), the
/// outbound persistent stream (terminal-event classification) and the
/// aggregated-completion branch. Marking is not guarded here — the reference's
/// Mark* methods overwrite — except for <see cref="MarkStreamTerminal"/>, which
/// is the port of markPerformanceTerminal's <c>RequestCompleted</c> guard.
/// </remarks>
public sealed class ChannelPerformanceRecord
{
    public required long ChannelId { get; init; }

    public required DateTimeOffset StartTime { get; init; }

    /// <summary>
    /// Port of PerformanceRecord.APIKey: the key this attempt authenticated
    /// with (the <c>contexts.GetChannelAPIKey</c> value in the reference), or
    /// <see cref="AxonHub.Abstractions.ChannelCredentials.OAuthCredentialRef"/>
    /// for an OAuth channel, so the round_robin_success report can match the key
    /// the cursor is using.
    /// </summary>
    public string ApiKey { get; init; } = string.Empty;

    public bool Stream { get; init; }

    public bool Success { get; private set; }

    public bool Canceled { get; private set; }

    public bool RequestCompleted { get; private set; }

    public int ResponseStatusCode { get; private set; }

    public string ErrorMessage { get; private set; } = string.Empty;

    /// <summary>Port of PerformanceRecord.EndTime: when the outcome was marked.</summary>
    public DateTimeOffset EndTime { get; private set; }

    /// <summary>Port of PerformanceRecord.IsValid.</summary>
    public bool IsValid => ChannelId > 0 && RequestCompleted;

    /// <summary>Port of PerformanceRecord.MarkSuccess.</summary>
    public void MarkSuccess(DateTimeOffset now)
    {
        Success = true;
        RequestCompleted = true;
        EndTime = now;
    }

    /// <summary>Port of PerformanceRecord.MarkFailedWithMessage.</summary>
    public void MarkFailed(int errorCode, string errorMessage, DateTimeOffset now)
    {
        Success = false;
        ResponseStatusCode = errorCode;
        ErrorMessage = errorMessage;
        RequestCompleted = true;
        EndTime = now;
    }

    /// <summary>Port of PerformanceRecord.MarkCanceled.</summary>
    public void MarkCanceled(DateTimeOffset now)
    {
        Success = false;
        Canceled = true;
        RequestCompleted = true;
        EndTime = now;
    }

    /// <summary>
    /// Port of OutboundPersistentStream.markPerformanceTerminal: a stream's
    /// classified terminal state decides success, cancellation or failure.
    /// Token limits and content filtering (incomplete) end a response without a
    /// channel fault, so they count as success; a terminal-less stream decides
    /// nothing here.
    /// </summary>
    public void MarkStreamTerminal(StreamTerminalState state, string message, DateTimeOffset now)
    {
        if (RequestCompleted)
        {
            return;
        }

        switch (state)
        {
            case StreamTerminalState.Completed:
            case StreamTerminalState.Incomplete:
                MarkSuccess(now);
                break;
            case StreamTerminalState.Canceled:
                MarkCanceled(now);
                break;
            case StreamTerminalState.Failed:
                MarkFailed(500, message, now);
                break;
            case StreamTerminalState.None:
                break;
        }
    }
}

/// <summary>
/// The performance record currently owned by one request attempt. The reference
/// keeps it on PersistenceState.Perf and every middleware shares the pointer.
/// </summary>
public sealed class ChannelPerformanceState
{
    public ChannelPerformanceRecord? Current { get; set; }
}
