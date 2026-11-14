using System.Text;
using System.Text.Json;
using AxonHub.Api.Http;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Orchestration;

/// <summary>The upstream attempt a continuation is about to be sent to.</summary>
internal readonly record struct ResponsesAttemptTarget(ResponsesOrigin Origin, bool ForwardsId);

/// <summary>
/// Port of <c>orchestrator.responsesCompletion</c> (responses_capture.go:16-56):
/// the completion observer shared by the raw-response capture middleware and the
/// provider stream wrapper. It keeps one final Responses body (or just its
/// id/status metadata) while accounting every byte against the lane budget.
/// </summary>
internal sealed class ResponsesCompletion
{
    internal readonly object Mu = new();

    internal byte[]? Body;

    internal int Bytes;

    internal bool Overflow;

    internal ResponsesBudget? Budget;

    internal int Retained;

    internal bool MetadataOnly;

    /// <summary>Port of <c>observe</c>.</summary>
    public void Observe(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            return;
        }

        lock (Mu)
        {
            Bytes += streamEvent.Data?.Length ?? 0;
            if (Bytes > ResponsesSessionStore.MaxResponse)
            {
                Overflow = true;
            }

            if (ResponsesJson.TopLevelString(streamEvent.Data, "type") != "response.completed")
            {
                return;
            }

            if (ResponsesJson.Lookup(streamEvent.Data, "response") is not { } body)
            {
                return;
            }

            var status = ResponsesJson.String(body, "status");
            var id = ResponsesJson.String(body, "id");
            if (status != "completed" || id.Length == 0)
            {
                return;
            }

            var raw = body.GetRawText();
            var rawSize = Encoding.UTF8.GetByteCount(raw);
            if (!Overflow && !MetadataOnly && Budget is not null)
            {
                if (Budget.Replace(Retained, rawSize))
                {
                    Retained = rawSize;
                }
                else
                {
                    Overflow = true;
                }
            }

            Body = Overflow || MetadataOnly
                ? Encoding.UTF8.GetBytes("{\"id\":" + GoJson.Quote(id) + ",\"status\":\"completed\"}")
                : Encoding.UTF8.GetBytes(raw);
        }
    }

    /// <summary>Port of <c>OnOutboundRawResponse</c>'s capture bookkeeping.</summary>
    public void CaptureResponse(byte[]? responseBody)
    {
        lock (Mu)
        {
            Body = responseBody;
            Overflow = responseBody is { Length: > 0 }
                && responseBody.Length > ResponsesSessionStore.MaxResponse;
            if (!Overflow && Budget is not null)
            {
                var length = responseBody?.Length ?? 0;
                if (Budget.Replace(0, length))
                {
                    Retained = length;
                }
                else
                {
                    Overflow = true;
                }
            }
        }
    }
}

/// <summary>
/// Port of <c>responsesProviderStream</c> (responses_capture.go:58-75): observes
/// every provider event while passing it through unchanged.
/// </summary>
internal sealed class ResponsesProviderStream(IStream<StreamEvent> inner, ResponsesCompletion completion)
    : IStream<StreamEvent>
{
    private StreamEvent? _current;

    public StreamEvent? Current => _current;

    public Exception? Err => inner.Err;

    public bool Next()
    {
        if (!inner.Next())
        {
            _current = null;
            return false;
        }

        _current = inner.Current;
        completion.Observe(_current);
        return true;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>
/// Port of <c>responsesAcceptedStream</c> (responses_capture.go:177-203):
/// reminds the continuation whenever a terminal event completes the turn, and
/// releases its reservations when the stream is closed.
/// </summary>
internal sealed class ResponsesAcceptedStream(IStream<StreamEvent> inner, ResponsesContinuation continuation)
    : IStream<StreamEvent>
{
    private StreamEvent? _current;

    public StreamEvent? Current => _current;

    public Exception? Err => inner.Err;

    public bool Next()
    {
        if (!inner.Next())
        {
            _current = null;
            return false;
        }

        _current = inner.Current;
        if (ClassifyStreamTerminalEvent(_current) == StreamTerminalState.Completed)
        {
            continuation.Accept();
        }

        return true;
    }

    public void Dispose()
    {
        try
        {
            inner.Dispose();
        }
        finally
        {
            // Deferred calls run LIFO: releaseCapture, then releaseRetention.
            continuation.ReleaseCapture();
            continuation.ReleaseRetention();
        }
    }

    /// <summary>Port of <c>classifyStreamTerminalEvent</c> (inbound.go:126-170).</summary>
    private static StreamTerminalState ClassifyStreamTerminalEvent(StreamEvent? streamEvent)
    {
        if (!new StreamTerminalTracker(1).Observe(streamEvent))
        {
            return StreamTerminalState.None;
        }

        return StreamTerminalClassifier.ClassifyAcceptedTerminalEvent(streamEvent);
    }
}

/// <summary>
/// Port of <c>orchestrator.responsesContinuation</c> (responses_attempt.go:19-37):
/// per-request state that lets a Responses turn be continued by the same
/// upstream endpoint, either from a WebSocket lane or from the session store.
/// </summary>
/// <remarks>
/// The reference reads the lane, the attempt, the session id and the session
/// scope from the request context. The port constructs this object in the
/// orchestrator with those values resolved from the inbound wire request
/// (difference C289), and pre-loads the store record because
/// <c>lookupOrLoad</c> is asynchronous while the transformer API is synchronous.
/// </remarks>
internal sealed class ResponsesContinuation
{
    /// <summary>Prepared payloads larger than this are not merged (responses_attempt.go:74).</summary>
    private const int MaxMergedInput = 128 << 20;

    /// <summary>Inputs larger than this are not retained for the next turn (responses_attempt.go:123).</summary>
    private const int MaxRetainedInput = 64 << 20;

    private readonly ResponsesSessionStore? _store;
    private readonly string? _scope;
    private readonly string? _sessionId;
    private readonly bool _webSocket;
    private readonly ResponsesSessionRecord? _preloadedRecord;
    private readonly bool _known;
    private readonly bool _knownFailed;
    private readonly ResponsesAttempt? _attempt;

    internal ResponsesContinuation(
        ResponsesSessionStore? store,
        byte[]? original,
        string? scope,
        string? sessionId,
        ResponsesLane? lane,
        ResponsesAttempt? attempt,
        bool webSocket,
        ResponsesSessionRecord? preloadedRecord = null,
        bool known = false,
        bool knownFailed = false)
    {
        _store = store;
        Original = original ?? [];
        Previous = ResponsesJson.TopLevelString(Original, "previous_response_id");
        _scope = scope;
        _sessionId = sessionId;
        Lane = lane;
        _attempt = attempt;
        _webSocket = webSocket;
        _preloadedRecord = preloadedRecord;
        _known = known;
        _knownFailed = knownFailed;
        Capture = new ResponsesCompletion();
    }

    /// <summary>The client's raw Responses body (responses_attempt.go:21).</summary>
    internal byte[] Original { get; }

    /// <summary><c>previous_response_id</c> read from <see cref="Original"/> at construction.</summary>
    internal string Previous { get; }

    /// <summary>The body sent upstream, after history expansion (responses_attempt.go:22).</summary>
    internal byte[]? Effective { get; private set; }

    /// <summary>The body recorded for the next turn (responses_attempt.go:23).</summary>
    internal byte[]? SnapshotBody { get; set; }

    internal List<byte[]>? Input { get; set; }

    internal bool Complete { get; private set; }

    internal bool Retain { get; private set; }

    internal int RetentionBytes { get; private set; }

    internal ResponsesLane? Lane { get; }

    internal ResponsesOrigin? FirstOrigin { get; set; }

    internal ResponsesRecord? Owner { get; set; }

    internal ResponsesCompletion Capture { get; private set; }

    internal bool IsResponsesWebSocket => _webSocket;

    /// <summary>
    /// Port of <c>loadResponsesContinuationOwner</c> (responses_attempt.go:145-165).
    /// The store branch returns the record pre-loaded by the orchestrator: the
    /// reference's <c>lookupOrLoad</c> is asynchronous and also refreshes its
    /// in-memory cache, which the port performs once before the pipeline runs.
    /// </summary>
    internal ResponsesRecord? LoadOwner()
    {
        if (Lane is not null)
        {
            var laneRecord = Lane.Record();
            return string.Equals(laneRecord.Id, Previous, StringComparison.Ordinal) ? laneRecord : null;
        }

        if (_store is null)
        {
            return null;
        }

        var record = _preloadedRecord;
        if (record is null)
        {
            return null;
        }

        var owner = new ResponsesRecord
        {
            Id = Previous,
            Origin = record.Origin,
        };
        if (record.Complete)
        {
            owner = owner with
            {
                Snapshot = new ResponsesSnapshot
                {
                    Input = [.. record.Input],
                    Output = [.. record.Output],
                },
            };
        }

        return owner;
    }

    /// <summary>
    /// Port of <c>prepareResponsesAttemptBodyWithRequest</c>
    /// (responses_attempt.go:48-143). Resets the per-attempt capture and
    /// retention state, resolves the continuation owner, expands a complete
    /// snapshot into <c>input</c>, and returns the effective body.
    /// </summary>
    internal byte[] PrepareAttemptBody(ResponsesAttemptTarget target)
    {
        ReleaseCapture();
        Capture = new ResponsesCompletion();
        ReleaseRetention();
        SnapshotBody = null;

        var body = Original;
        if (!ResponsesJson.TryUnmarshalRawObject(body, out var payload, out var parseError))
        {
            throw new ResponsesContinuationException(
                "parse Responses continuation: " + parseError);
        }

        Complete = Previous.Length == 0;
        if (Previous.Length != 0)
        {
            // shared.GetSessionScope(ctx) reports presence, not a non-empty value.
            if (_scope is null)
            {
                throw ResponsesRecovery.Error();
            }

            Owner = LoadOwner();

            if (Owner?.Snapshot is { } snapshot)
            {
                var inputSize = ResponsesSessionStore.RawSize(snapshot.Input)
                    + ResponsesSessionStore.RawSize(snapshot.Output)
                    + RawLength(payload, "input");
                if (inputSize > MaxMergedInput)
                {
                    throw ResponsesRecovery.Error();
                }

                var items = new List<byte[]>(
                    snapshot.Input.Count + snapshot.Output.Count + 1);
                items.AddRange(snapshot.Input);
                items.AddRange(snapshot.Output);
                items.AddRange(ResponsesSessionStore.ResponseSessionInputItems(
                    payload?.GetValueOrDefault("input")));

                var merged = payload!;
                merged["input"] = ResponsesSessionStore.MarshalRawArray(items);
                merged.Remove("previous_response_id");
                Complete = true;
                body = ResponsesSessionStore.MarshalRawObject(merged);
            }
            else
            {
                switch (Owner)
                {
                    case not null:
                        if (!OriginMatches(Owner.Origin, target.Origin) || !target.ForwardsId)
                        {
                            throw ResponsesRecovery.Error();
                        }

                        break;

                    default:
                        if (_webSocket
                            || !string.Equals(target.Origin.Format, ApiFormats.OpenAIResponse, StringComparison.Ordinal)
                            || !target.ForwardsId)
                        {
                            throw ResponsesRecovery.Error();
                        }

                        if (_store is not null && (_knownFailed || _known))
                        {
                            throw ResponsesRecovery.Error();
                        }

                        if (FirstOrigin is { } firstOrigin && !OriginMatches(firstOrigin, target.Origin))
                        {
                            throw ResponsesRecovery.Error();
                        }

                        break;
                }
            }
        }

        Effective = body;
        var native = _webSocket
            && string.Equals(target.Origin.Format, ApiFormats.OpenAIResponseWebSocket, StringComparison.Ordinal);
        if (Complete && !native)
        {
            SnapshotBody = MarshalPayload(payload);
        }

        Input = null;
        Retain = Complete && !native && RawLength(payload, "input") <= MaxRetainedInput;
        if (Retain && Lane is not null)
        {
            RetentionBytes = RawLength(payload, "input");
            if (!Lane.Budget.Replace(0, RetentionBytes))
            {
                RetentionBytes = 0;
                Retain = false;
            }
        }

        if (Retain)
        {
            Input = ResponsesSessionStore.ResponseSessionInputItems(payload?.GetValueOrDefault("input"));
        }

        if (Lane is not null)
        {
            Capture.MetadataOnly = native || !Retain;
            if (!Capture.MetadataOnly)
            {
                Capture.Budget = Lane.Budget;
            }
        }

        return body;
    }

    /// <summary>Port of <c>releaseRetention</c> (responses_attempt.go:204-209).</summary>
    internal void ReleaseRetention()
    {
        if (Lane is not null && RetentionBytes > 0)
        {
            Lane.Budget.Replace(RetentionBytes, 0);
        }

        RetentionBytes = 0;
    }

    /// <summary>Port of <c>releaseCapture</c> (responses_capture.go:167-175).</summary>
    internal void ReleaseCapture()
    {
        lock (Capture.Mu)
        {
            if (Capture.Budget is not null)
            {
                Capture.Budget.Replace(Capture.Retained, 0);
            }

            Capture.Retained = 0;
            Capture.Body = null;
        }
    }

    /// <summary>
    /// Port of <c>verifyResponsesAttempt</c> (responses_attempt.go:304-345): pins
    /// the attempt to its origin, refuses a previous id the upstream cannot
    /// forward, and snapshots the final provider body.
    /// </summary>
    internal void Verify(HttpRequestData request, int channelId, string modelId, bool forwardsId)
    {
        var body = request.Body ?? [];
        var origin = new ResponsesOrigin(
            channelId,
            request.Url ?? string.Empty,
            request.ApiFormat ?? string.Empty,
            modelId,
            ResponsesContinuationAuth.Identity(request),
            0);

        if (ResponsesJson.TopLevelString(body, "model") is { Length: > 0 } model)
        {
            origin = origin with { Model = model };
        }

        var previous = OverrideJson.Get(body, "previous_response_id");
        if (Previous.Length != 0 && !Complete)
        {
            if (!string.Equals(previous.StringValue, Previous, StringComparison.Ordinal)
                || !forwardsId
                || (Owner is not null && !OriginMatches(Owner.Origin, origin))
                || (FirstOrigin is { } first && !OriginMatches(first, origin)))
            {
                throw ResponsesRecovery.Error();
            }
        }

        FirstOrigin ??= origin;

        if (Complete
            && !previous.Exists
            && ChannelTransformOptions.IsResponsesFormat(request.ApiFormat))
        {
            SnapshotBody = [.. body];
            if (Retain)
            {
                var input = OverrideJson.Get(body, "input");
                var inputBytes = input.Exists
                    ? Encoding.UTF8.GetBytes(input.Node!.Raw)
                    : [];
                if (Lane is not null && !Lane.Budget.Replace(RetentionBytes, inputBytes.Length))
                {
                    ReleaseRetention();
                    Retain = false;
                    Input = null;
                }
                else
                {
                    if (Lane is not null)
                    {
                        RetentionBytes = inputBytes.Length;
                    }

                    Input = ResponsesSessionStore.ResponseSessionInputItems(inputBytes);
                }
            }
        }

        if (_attempt is not null)
        {
            var attemptOrigin = origin;
            if (Owner is not null
                && OriginMatches(Owner.Origin, origin)
                && previous.Exists)
            {
                attemptOrigin = attemptOrigin with { Generation = Owner.Origin.Generation };
            }

            _attempt.SetOrigin(attemptOrigin);
        }
    }

    /// <summary>Port of <c>accept</c> (responses_capture.go:113-165).</summary>
    internal void Accept()
    {
        lock (Capture.Mu)
        {
            var body = Capture.Body;
            if (body is null
                || body.Length == 0
                || !ResponsesSessionStore.IsValidJson(body)
                || ResponsesSessionStore.GoString(body, "status") != "completed")
            {
                return;
            }

            var id = ResponsesSessionStore.GoString(body, "id");
            if (id.Length == 0)
            {
                return;
            }

            var origin = _attempt?.Origin ?? default;
            if (Lane is { } lane)
            {
                var record = new ResponsesRecord
                {
                    Id = id,
                    Origin = origin,
                };

                if (!string.Equals(origin.Format, ApiFormats.OpenAIResponseWebSocket, StringComparison.Ordinal)
                    && Retain
                    && !Capture.Overflow)
                {
                    var retained = Capture.Retained == 0 || Capture.Retained == body.Length;
                    if (Capture.Retained == body.Length)
                    {
                        RetentionBytes += Capture.Retained;
                        Capture.Retained = 0;
                    }

                    var output = TryUnmarshalOutput(body);
                    if (retained && output is not null)
                    {
                        record = record with
                        {
                            Snapshot = new ResponsesSnapshot
                            {
                                Input = Input ?? [],
                                Output = output,
                            },
                        };
                    }
                }

                ReleaseRetention();
                if (Capture.Budget is not null)
                {
                    Capture.Budget.Replace(Capture.Retained, 0);
                    Capture.Retained = 0;
                }

                lane.Remember(record);
            }
            else if (_store is not null)
            {
                var requestBody = Effective;
                if (Complete)
                {
                    requestBody = SnapshotBody;
                }

                if (Capture.Overflow)
                {
                    requestBody = Encoding.UTF8.GetBytes("{\"previous_response_id\":\"unavailable\"}");
                }

                _store.Record(requestBody, body, _sessionId, _scope, _attempt);
                if (Capture.Budget is not null)
                {
                    Capture.Budget.Replace(Capture.Retained, 0);
                    Capture.Retained = 0;
                }
            }

            Capture.Body = null;
        }
    }

    /// <summary>Port of <c>originMatches</c> (responses_attempt.go:211-214).</summary>
    internal static bool OriginMatches(ResponsesOrigin owner, ResponsesOrigin target)
        => owner.ChannelId == target.ChannelId
           && string.Equals(owner.Format, target.Format, StringComparison.Ordinal)
           && string.Equals(owner.Model, target.Model, StringComparison.Ordinal)
           && (target.Endpoint.Length == 0
               || string.Equals(owner.Endpoint, target.Endpoint, StringComparison.Ordinal))
           && (target.Auth.Length == 0
               || string.Equals(owner.Auth, target.Auth, StringComparison.Ordinal));

    private static byte[] MarshalPayload(Dictionary<string, byte[]>? payload)
        => payload is null
            ? Encoding.UTF8.GetBytes("null")
            : ResponsesSessionStore.MarshalRawObject(payload);

    private static int RawLength(Dictionary<string, byte[]>? payload, string key)
        => payload is not null && payload.TryGetValue(key, out var raw) ? raw.Length : 0;

    /// <summary>
    /// <c>json.Unmarshal(body, &amp;struct{ Output []json.RawMessage })</c> in
    /// <c>accept</c>: a type mismatch discards the snapshot, <c>null</c> and a
    /// missing field decode as an empty slice.
    /// </summary>
    private static List<byte[]>? TryUnmarshalOutput(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, ResponsesSessionStore.ParseOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var output = new List<byte[]>();
            if (!root.TryGetProperty("output", out var outputElement)
                || outputElement.ValueKind == JsonValueKind.Null)
            {
                return output;
            }

            if (outputElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in outputElement.EnumerateArray())
            {
                output.Add(Encoding.UTF8.GetBytes(item.GetRawText()));
            }

            return output;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Raised when the client body cannot be decoded as a Responses object.</summary>
internal sealed class ResponsesContinuationException(string message) : Exception(message);

/// <summary>
/// gjson-compatible reads used by the continuation subsystem. The reference
/// reads stream events with gjson, which tolerates ill-formed UTF-8; large
/// documents go through <see cref="GoJsonUtf8"/> before System.Text.Json sees
/// them, exactly like the other orchestrator readers.
/// </summary>
internal static class ResponsesJson
{
    /// <summary>gjson.GetBytes(data, path) for a nested object path.</summary>
    public static JsonElement? Lookup(byte[]? data, params string[] path)
    {
        if (data is null || data.Length == 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                GoJsonUtf8.SanitizeStringLiterals(data) ?? data,
                ResponsesSessionStore.ParseOptions);
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

            return current.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>gjson.GetBytes(data, key).String() for a top-level key.</summary>
    public static string TopLevelString(byte[]? data, string key)
        => Lookup(data) is { } root ? String(root, key) : string.Empty;

    /// <summary>gjson.Result.Get(key).String() on an already-decoded object.</summary>
    public static string String(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(key, out var found))
        {
            return string.Empty;
        }

        return String(found);
    }

    /// <summary>gjson.Result.String() for a decoded element.</summary>
    public static string String(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => string.Empty,
    };

    /// <summary>
    /// <c>json.Unmarshal(body, &amp;map[string]json.RawMessage{})</c>: a null
    /// root yields a nil map without error, any other non-object root is the
    /// decoder's type error, and malformed input is the scanner's message.
    /// </summary>
    public static bool TryUnmarshalRawObject(
        byte[]? body,
        out Dictionary<string, byte[]>? payload,
        out string? error)
    {
        payload = null;
        error = null;

        var data = body ?? [];
        if (GoJsonSyntax.Validate(data) is { } syntax)
        {
            error = syntax;
            return false;
        }

        var kind = GoStructJson.RootKind(data);
        if (kind is null)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using var document = JsonDocument.Parse(data, ResponsesSessionStore.ParseOptions);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
            }

            payload = result;
            return true;
        }

        if (kind == "null")
        {
            return true;
        }

        error = GoStructJson.TypeMismatch(kind, "map[string]json.RawMessage");
        return false;
    }
}
