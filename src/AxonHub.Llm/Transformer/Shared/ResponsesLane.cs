using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.Shared;

/// <summary>
/// Port of shared.ResponsesBudget (llm/transformer/shared/responses_lane.go:8-24):
/// a byte budget shared by a WebSocket lane and every continuation attempt on
/// it. <see cref="Replace"/> atomically swaps a previous reservation for a new
/// one and refuses when the result would exceed the limit.
/// </summary>
public sealed class ResponsesBudget(long limit)
{
    private readonly object _mu = new();
    private long _used;

    public bool Replace(long previous, long next)
    {
        lock (_mu)
        {
            if (next > limit - (_used - previous))
            {
                return false;
            }

            _used += next - previous;
            return true;
        }
    }
}

/// <summary>Port of shared.ResponsesSnapshot: input and output items kept for the next turn.</summary>
public sealed class ResponsesSnapshot
{
    public List<byte[]> Input { get; init; } = [];

    public List<byte[]> Output { get; init; } = [];
}

/// <summary>
/// Port of shared.ResponsesOrigin: identifies the upstream endpoint a
/// continuation record belongs to. Value equality matches Go's comparable
/// struct; <see cref="Generation"/> is excluded by <see cref="SameEndpoint"/>.
/// </summary>
public readonly record struct ResponsesOrigin(
    int ChannelId,
    string Endpoint,
    string Format,
    string Model,
    string Auth,
    ulong Generation)
{
    /// <summary>Port of <c>SameEndpoint</c>: everything but the lease generation.</summary>
    public bool SameEndpoint(ResponsesOrigin other)
        => ChannelId == other.ChannelId
           && string.Equals(Endpoint, other.Endpoint, StringComparison.Ordinal)
           && string.Equals(Format, other.Format, StringComparison.Ordinal)
           && string.Equals(Model, other.Model, StringComparison.Ordinal)
           && string.Equals(Auth, other.Auth, StringComparison.Ordinal);
}

/// <summary>
/// Port of shared.ResponsesRecord: the id, origin and optional full snapshot a
/// lane or the session store keeps for one completed response.
/// </summary>
public sealed record ResponsesRecord
{
    public static ResponsesRecord Empty { get; } = new();

    public string Id { get; init; } = string.Empty;

    public ResponsesOrigin Origin { get; init; }

    public ResponsesSnapshot? Snapshot { get; init; }
}

/// <summary>
/// Port of shared.ResponsesLane (responses_lane.go:37-117): the per-WebSocket-
/// connection continuation lane. It holds the latest completed record, accounts
/// its snapshot bytes against the lane budget, and owns the leases that must be
/// retired when the lane closes.
/// </summary>
public sealed class ResponsesLane
{
    private readonly object _mu = new();
    private readonly Dictionary<ResponsesOrigin, Action> _retire = [];
    private ResponsesRecord _record = new();
    private long _size;
    private bool _closed;

    public ResponsesLane(string id, ResponsesBudget budget)
    {
        Id = id;
        Budget = budget;
    }

    public string Id { get; }

    public ResponsesBudget Budget { get; }

    /// <summary>Port of <c>Record</c>: returns a copy of the current record.</summary>
    public ResponsesRecord Record()
    {
        lock (_mu)
        {
            return _record with { };
        }
    }

    /// <summary>Port of <c>Remember</c>.</summary>
    public void Remember(ResponsesRecord record)
    {
        lock (_mu)
        {
            if (_closed)
            {
                return;
            }

            long size = 0;
            if (record.Snapshot is not null)
            {
                foreach (var item in record.Snapshot.Input)
                {
                    size += item.Length;
                }

                foreach (var item in record.Snapshot.Output)
                {
                    size += item.Length;
                }
            }

            if (!Budget.Replace(_size, size))
            {
                record = record with { Snapshot = null };
                size = 0;
                Budget.Replace(_size, 0);
            }

            _record = record;
            _size = size;
        }
    }

    /// <summary>Port of <c>RegisterLease</c>.</summary>
    public bool RegisterLease(ResponsesOrigin origin, Action retire)
    {
        lock (_mu)
        {
            if (_closed)
            {
                return false;
            }

            _retire[origin] = retire;
            return true;
        }
    }

    /// <summary>Port of <c>ForgetLease</c>.</summary>
    public void ForgetLease(ResponsesOrigin origin)
    {
        lock (_mu)
        {
            _retire.Remove(origin);
        }
    }

    /// <summary>Port of <c>RetireLeases</c>: takes the map, then closes every lease outside the lock.</summary>
    public void RetireLeases()
    {
        List<Action> retire;
        lock (_mu)
        {
            retire = [.. _retire.Values];
            _retire.Clear();
        }

        foreach (var closeLease in retire)
        {
            closeLease();
        }
    }

    /// <summary>Port of <c>Close</c>.</summary>
    public void Close()
    {
        lock (_mu)
        {
            _closed = true;
            Budget.Replace(_size, 0);
            _size = 0;
            _record = new ResponsesRecord();
        }

        RetireLeases();
    }
}

/// <summary>
/// Port of shared.ResponsesAttempt (responses_continuation.go:58-73): the origin
/// of the attempt currently on the wire, shared by the outbound transformer and
/// the capture middleware.
/// </summary>
public sealed class ResponsesAttempt
{
    private readonly object _mu = new();
    private ResponsesOrigin _origin;

    public ResponsesOrigin Origin
    {
        get
        {
            lock (_mu)
            {
                return _origin;
            }
        }
    }

    public void SetOrigin(ResponsesOrigin origin)
    {
        lock (_mu)
        {
            _origin = origin;
        }
    }
}

/// <summary>
/// Port of shared.ErrResponsesContinuationUnavailable and the error helpers in
/// responses_continuation.go:14-42. The reference models
/// <c>errors.Join(ErrResponsesContinuationUnavailable, &amp;httpclient.Error{...})</c>;
/// the port uses an exception whose inner chain is the sentinel followed by the
/// wire error so <see cref="GoErrors.Is"/> and <see cref="GoErrors.As{T}"/> see
/// both, exactly like Go's join.
/// </summary>
public static class ResponsesRecovery
{
    private const string WireBody =
        "{\"error\":{\"type\":\"invalid_request_error\",\"code\":\"previous_response_not_found\",\"message\":\"Continuation context is unavailable; resend the full input without previous_response_id.\"}}";

    /// <summary>The wire error carrying status 400 and the recovery envelope.</summary>
    public static HttpClientError WireError { get; } = new(
        string.Empty,
        string.Empty,
        400,
        "Bad Request",
        Encoding.UTF8.GetBytes(WireBody));

    /// <summary>Port of <c>ErrResponsesContinuationUnavailable</c>: the sentinel matched by <c>errors.Is</c>.</summary>
    public static Exception Sentinel { get; } = new ResponsesContinuationUnavailableSentinel(
        "Responses continuation unavailable",
        WireError);

    /// <summary>Port of <c>ResponsesRecoveryError()</c>.</summary>
    public static Exception Error() => new ResponsesContinuationUnavailableException(
        "Responses continuation unavailable\n -  with status Bad Request",
        Sentinel);

    /// <summary>Port of <c>IsResponsesRecoveryError</c>.</summary>
    public static bool IsRecoveryError(Exception? err)
    {
        if (GoErrors.Is(err, Sentinel))
        {
            return true;
        }

        if (GoErrors.As<ResponseError>(err) is { } response)
        {
            return string.Equals(response.Detail.Code, "previous_response_not_found", StringComparison.Ordinal);
        }

        if (GoErrors.As<HttpClientError>(err) is { } wire)
        {
            try
            {
                using var document = JsonDocument.Parse(wire.Body);
                var root = document.RootElement;
                return root.ValueKind == JsonValueKind.Object
                       && root.TryGetProperty("error", out var error)
                       && error.ValueKind == JsonValueKind.Object
                       && error.TryGetProperty("code", out var code)
                       && code.ValueKind == JsonValueKind.String
                       && string.Equals(code.GetString(), "previous_response_not_found", StringComparison.Ordinal);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>
/// The sentinel exception type behind <see cref="ResponsesRecovery.Sentinel"/>.
/// Go's sentinel is a plain <c>errors.New</c>; the dedicated type keeps the
/// reference-identity match analyzable as an exception flow.
/// </summary>
public sealed class ResponsesContinuationUnavailableSentinel(string message, Exception inner)
    : Exception(message, inner);

/// <summary>
/// The concrete recovery error produced by <see cref="ResponsesRecovery.Error"/>.
/// Its inner chain is the sentinel, whose inner is the wire error, so both
/// <c>errors.Is</c> and <c>errors.As(*httpclient.Error)</c> match.
/// </summary>
public sealed class ResponsesContinuationUnavailableException(string message, Exception inner)
    : Exception(message, inner);
