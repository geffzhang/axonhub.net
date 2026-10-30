using System.Text.Json;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Orchestration;

/// <summary>Port of orchestrator.streamTerminalState.</summary>
public enum StreamTerminalState
{
    None = 0,
    Completed,
    Failed,
    Incomplete,
    Canceled,
}

/// <summary>
/// Port of orchestrator.streamOutcome (internal/server/orchestrator/
/// stream_outcome.go): the single monotonic view of a stream's terminal facts.
/// Provider terminal outcomes outrank transport and context errors observed
/// after them; a transport error can only be replaced by a completed outcome
/// when aggregation provides explicit completion evidence.
/// </summary>
public sealed class StreamOutcome
{
    /// <summary>
    /// Port of orchestrator.ErrStreamIncomplete: reports an upstream stream that
    /// ended without a terminal event and without an aggregated complete
    /// response. The same value is persisted on the request and delivered to the
    /// client, so both sides agree on the reason.
    /// </summary>
    public static readonly Exception ErrStreamIncomplete =
        new StreamIncompleteException("stream ended without terminal event or completed response");

    private bool _providerTerminal;
    private bool _aggregatedCompleted;
    private bool _validatedCompleted;
    private Exception? _transportErr;
    private Exception? _contextErr;
    private StreamTerminalState _contextState = StreamTerminalState.None;

    /// <summary>The provider terminal state; <see cref="StreamTerminalState.None"/> until observed.</summary>
    public StreamTerminalState TerminalState { get; private set; } = StreamTerminalState.None;

    public bool HasFinalEvidence => _providerTerminal || _aggregatedCompleted || _validatedCompleted;

    public void ObserveTerminal(StreamTerminalState state)
    {
        if (_providerTerminal || state == StreamTerminalState.None)
        {
            return;
        }

        _providerTerminal = true;
        TerminalState = state;
    }

    public void ObserveAggregatedCompletion(bool completed)
    {
        if (!_providerTerminal && completed)
        {
            _aggregatedCompleted = true;
        }
    }

    public void ObserveValidatedCompletion()
    {
        if (!_providerTerminal)
        {
            _validatedCompleted = true;
        }
    }

    /// <summary>
    /// Go classifies a cancellation surfaced as a stream error through the
    /// context branch: <c>errors.Is(err, context.Canceled)</c>.
    /// </summary>
    public void ObserveTransportError(Exception? error)
    {
        if (error is null)
        {
            return;
        }

        if (IsContextCancellation(error))
        {
            ObserveContextError(error);
            return;
        }

        _transportErr = error;
    }

    public void ObserveContextError(Exception? error)
    {
        if (error is null)
        {
            return;
        }

        if (HasTimeoutCause(error))
        {
            _contextState = StreamTerminalState.Failed;
            _contextErr = error;
            return;
        }

        if (_contextState == StreamTerminalState.None)
        {
            _contextState = StreamTerminalState.Canceled;
            _contextErr = error;
        }
    }

    public StreamOutcomeDecision FinalDecision()
    {
        if (_providerTerminal)
        {
            return new StreamOutcomeDecision(TerminalState, null);
        }

        if (_validatedCompleted)
        {
            return new StreamOutcomeDecision(StreamTerminalState.Completed, null);
        }

        if (_contextState == StreamTerminalState.Failed)
        {
            return new StreamOutcomeDecision(StreamTerminalState.Failed, _contextErr);
        }

        if (_transportErr is not null)
        {
            return new StreamOutcomeDecision(StreamTerminalState.Failed, _transportErr);
        }

        if (_contextState == StreamTerminalState.Canceled)
        {
            return new StreamOutcomeDecision(StreamTerminalState.Canceled, _contextErr);
        }

        if (_aggregatedCompleted)
        {
            return new StreamOutcomeDecision(StreamTerminalState.Completed, null);
        }

        return new StreamOutcomeDecision(StreamTerminalState.Incomplete, ErrStreamIncomplete);
    }

    public StreamTerminalState FinalState() => FinalDecision().State;

    internal static bool IsContextCancellation(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Go: <c>errors.Is(err, context.DeadlineExceeded)</c>. A cancelled token and
    /// an expired deadline are the same exception in .NET, so the timeout cause
    /// (HttpClient attaches a <see cref="TimeoutException"/>) is the signal.
    /// </summary>
    internal static bool HasTimeoutCause(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Port of orchestrator.streamOutcomeDecision.</summary>
public readonly record struct StreamOutcomeDecision(StreamTerminalState State, Exception? Cause);

/// <summary>
/// Port of the terminal-event classifier shared by the inbound and outbound
/// persistent streams (internal/server/orchestrator/inbound.go).
/// </summary>
public static class StreamTerminalClassifier
{
    /// <summary>Port of classifyAcceptedTerminalEvent.</summary>
    public static StreamTerminalState ClassifyAcceptedTerminalEvent(StreamEvent? streamEvent)
    {
        const string responseStatusCanceledBritish = "cancelled"; // OpenAI protocol spelling.

        if (streamEvent is null)
        {
            return StreamTerminalState.None;
        }

        var eventType = streamEvent.Type ?? string.Empty;
        var (jsonEventType, responseStatus) = ReadTypeAndStatus(streamEvent.Data);

        // Standalone errors can arrive before response.created and have no response status.
        if (eventType == "error" || jsonEventType == "error")
        {
            return StreamTerminalState.Failed;
        }

        if (eventType == "response.cancelled" || jsonEventType == "response.cancelled"
            || responseStatus == responseStatusCanceledBritish || responseStatus == "canceled")
        {
            return StreamTerminalState.Canceled;
        }

        if (eventType == "response.failed" || jsonEventType == "response.failed" || responseStatus == "failed")
        {
            return StreamTerminalState.Failed;
        }

        if (eventType == "response.incomplete" || jsonEventType == "response.incomplete"
            || responseStatus == "incomplete")
        {
            return StreamTerminalState.Incomplete;
        }

        return StreamTerminalState.Completed;
    }

    /// <summary>Port of streamTerminalErrorMessage, called for a non-successful terminal event.</summary>
    public static string TerminalErrorMessage(StreamEvent streamEvent, StreamTerminalState state)
    {
        ArgumentNullException.ThrowIfNull(streamEvent);

        foreach (var path in new[]
                 {
                     new[] { "response", "error", "message" },
                     new[] { "error", "message" },
                     new[] { "response", "incomplete_details", "reason" },
                 })
        {
            if (ReadNestedString(streamEvent.Data, path) is { Length: > 0 } message)
            {
                return message;
            }
        }

        // Compatible providers may use response.completed for abnormal outcomes.
        // Report the classified outcome rather than a misleading event type.
        return StateName(state);
    }

    /// <summary>Port of streamTerminalState.requestStatus.</summary>
    public static string ToRequestStatus(this StreamTerminalState state) => state switch
    {
        StreamTerminalState.Failed or StreamTerminalState.Incomplete => "failed",
        StreamTerminalState.Canceled => "canceled",
        _ => "completed",
    };

    /// <summary>Port of streamTerminalState.executionStatus.</summary>
    public static string ToExecutionStatus(this StreamTerminalState state) => state switch
    {
        StreamTerminalState.Failed or StreamTerminalState.Incomplete => "failed",
        StreamTerminalState.Canceled => "canceled",
        _ => "completed",
    };

    private static string StateName(StreamTerminalState state) => state switch
    {
        StreamTerminalState.Completed => "completed",
        StreamTerminalState.Failed => "failed",
        StreamTerminalState.Incomplete => "incomplete",
        StreamTerminalState.Canceled => "canceled",
        _ => string.Empty,
    };

    private static (string JsonType, string ResponseStatus) ReadTypeAndStatus(byte[] data)
    {
        using var document = TryParse(data);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (string.Empty, string.Empty);
        }

        var root = document.RootElement;
        var jsonType = root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;
        var responseStatus = root.TryGetProperty("response", out var response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            ? status.GetString() ?? string.Empty
            : string.Empty;

        return (jsonType, responseStatus);
    }

    private static string? ReadNestedString(byte[] data, string[] path)
    {
        using var document = TryParse(data);
        if (document is null)
        {
            return null;
        }

        var current = document.RootElement;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static JsonDocument? TryParse(byte[] data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
