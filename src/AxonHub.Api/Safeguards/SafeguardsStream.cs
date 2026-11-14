using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Safeguards;

/// <summary>
/// Port of safeguards.Stream (internal/server/safeguards/response.go:83-224):
/// per-message reviewer state. It buffers only bounded tool inputs and passes
/// text and thinking blocks through; the terminal message_delta triggers one
/// review.
/// </summary>
public sealed class SafeguardsReviewer
{
    private const int MaxToolBytes = 64000;

    private readonly SafeguardsEvaluator? _evaluator;
    private readonly Dictionary<long, StreamedTool> _tools = [];
    private long _lastIndex = -1;
    private SafeguardsResult[]? _results;
    private int _bytes;

    public SafeguardsReviewer(SafeguardsEvaluator? evaluator)
    {
        _evaluator = evaluator;
    }

    /// <summary>
    /// Port of <c>Stream.Observe</c> (response.go:104-224). Consumes one
    /// Anthropic event, removing upstream tool-use verdicts and collecting
    /// bounded tool arguments. Throws when required event fields cannot be
    /// decoded; the caller must validate stream completion before forwarding the
    /// reviewed delta.
    /// </summary>
    public byte[] Observe(CancellationToken cancellationToken, byte[]? body)
    {
        body ??= [];
        if (_evaluator is null)
        {
            return body;
        }

        var kind = SafeguardsJson.ReadRoot(body, StringComparer.Ordinal, out var fields);
        if (kind == SafeguardsRootKind.Invalid)
        {
            throw new SafeguardsDecodeException(
                "decode safeguard stream event: invalid JSON document");
        }

        if (kind == SafeguardsRootKind.Null)
        {
            throw new SafeguardsDecodeException("expected safeguard event object");
        }

        if (kind != SafeguardsRootKind.Object || fields is null)
        {
            throw new SafeguardsDecodeException(
                "decode safeguard stream event: json: cannot unmarshal value into Go value of type map[string]json.RawMessage");
        }

        // The configured reviewer owns dangerous_tool_use results. Remove
        // upstream verdicts even from early message_start/nonterminal delta
        // events so clients cannot act on an approval before the local review
        // has completed.
        var changed = SafeguardsJson.StripUpstreamResults(fields);
        foreach (var key in NestedResultKeys)
        {
            if (!fields.TryGetValue(key, out var nestedRaw))
            {
                continue;
            }

            var nestedKind = SafeguardsJson.ReadRoot(nestedRaw, StringComparer.Ordinal, out var nested);
            if (nestedKind == SafeguardsRootKind.Object
                && nested is not null
                && SafeguardsJson.StripUpstreamResults(nested))
            {
                fields[key] = SafeguardsJson.MarshalRawMap(nested);
                changed = true;
            }
        }

        if (changed)
        {
            body = SafeguardsJson.MarshalRawMap(fields);
        }

        var eventKind = string.Empty;
        if (fields.TryGetValue("type", out var typeRaw))
        {
            SafeguardsJson.TryString(typeRaw, out eventKind);
        }

        switch (eventKind)
        {
            case "content_block_start":
                var startIndex = DecodeIndex(fields);
                _lastIndex = startIndex;
                if (SafeguardsJson.TryDecodeTool(
                        fields.TryGetValue("content_block", out var blockRaw) ? blockRaw : null,
                        out var tool)
                    && tool is not null)
                {
                    if (_tools.TryGetValue(startIndex, out var existingTool))
                    {
                        existingTool.Tool.Truncated = true;
                        return body;
                    }

                    _bytes += tool.Input?.Length ?? 0;
                    if (_bytes > MaxToolBytes)
                    {
                        tool.Truncated = true;
                        tool.Input = null;
                    }

                    _tools[startIndex] = new StreamedTool(tool);
                }

                break;

            case "content_block_delta":
                var deltaIndex = DecodeIndex(fields);
                if (!_tools.TryGetValue(deltaIndex, out var current))
                {
                    return body;
                }

                DecodeInputDelta(
                    fields.TryGetValue("delta", out var inputDeltaRaw) ? inputDeltaRaw : null,
                    out var deltaType,
                    out var partial);
                if (!string.Equals(deltaType, "input_json_delta", StringComparison.Ordinal))
                {
                    return body;
                }

                if (current.Stopped)
                {
                    current.Tool.Truncated = true;
                }

                if (current.Tool.Truncated)
                {
                    return body;
                }

                _bytes += Encoding.UTF8.GetByteCount(partial);
                if (_bytes > MaxToolBytes)
                {
                    current.Tool.Truncated = true;
                    current.Partial = null;
                    return body;
                }

                current.Partial = current.Partial is null
                    ? Encoding.UTF8.GetBytes(partial)
                    : [.. current.Partial, .. Encoding.UTF8.GetBytes(partial)];
                break;

            case "content_block_stop":
                if (TryDecodeIndex(
                        fields.TryGetValue("index", out var stopIndexRaw) ? stopIndexRaw : null,
                        out var stopIndex)
                    && _tools.TryGetValue(stopIndex, out var stopped))
                {
                    stopped.Stopped = true;
                }

                break;

            case "message_delta":
                var deltaKind = SafeguardsJson.ReadRoot(
                    fields.TryGetValue("delta", out var deltaRaw) ? deltaRaw : null,
                    StringComparer.Ordinal,
                    out var deltaFields);
                if (deltaKind == SafeguardsRootKind.Invalid)
                {
                    throw new SafeguardsDecodeException(
                        "decode safeguard message delta: invalid JSON document");
                }

                if (deltaKind == SafeguardsRootKind.Null)
                {
                    return body;
                }

                if (deltaKind != SafeguardsRootKind.Object || deltaFields is null)
                {
                    throw new SafeguardsDecodeException(
                        "decode safeguard message delta: json: cannot unmarshal value into Go value of type map[string]json.RawMessage");
                }

                var stop = string.Empty;
                if (deltaFields.TryGetValue("stop_reason", out var stopRaw))
                {
                    SafeguardsJson.TryString(stopRaw, out stop);
                }

                if (stop.Length == 0)
                {
                    return body;
                }

                if (_results is null)
                {
                    var tools = new List<SafeguardsToolUse>(_tools.Count);
                    foreach (var index in _tools.Keys.Order())
                    {
                        var currentTool = _tools[index];
                        var copy = new SafeguardsToolUse
                        {
                            Id = currentTool.Tool.Id,
                            Name = currentTool.Tool.Name,
                            Input = currentTool.Tool.Input,
                            Truncated = currentTool.Tool.Truncated,
                        };

                        copy.Truncated = copy.Truncated
                            || !currentTool.Stopped
                            || (string.Equals(stop, "max_tokens", StringComparison.Ordinal)
                                && index == _lastIndex);

                        if (currentTool.Partial is { Length: > 0 })
                        {
                            copy.Input = currentTool.Partial;
                            if (!SafeguardsJson.IsValid(copy.Input))
                            {
                                copy.Truncated = true;
                            }
                        }

                        tools.Add(copy);
                    }

                    _results = _evaluator.Evaluate(cancellationToken, tools);
                }

                deltaFields["safeguard_results"] = SafeguardsJson.MergeResults(
                    deltaFields.TryGetValue("safeguard_results", out var existing) ? existing : null,
                    _results);
                fields["delta"] = SafeguardsJson.MarshalRawMap(deltaFields);
                return SafeguardsJson.MarshalRawMap(fields);
        }

        return body;
    }

    private static readonly string[] NestedResultKeys = ["message", "delta"];

    private static long DecodeIndex(Dictionary<string, byte[]> fields)
    {
        var raw = fields.TryGetValue("index", out var value) ? value : null;
        if (!TryDecodeIndex(raw, out var index))
        {
            throw new SafeguardsDecodeException(
                raw is null || raw.Length == 0
                    ? "decode safeguard content block index: unexpected end of JSON input"
                    : "decode safeguard content block index: json: cannot unmarshal value into Go value of type int");
        }

        return index;
    }

    private static bool TryDecodeIndex(byte[]? raw, out long index)
        => SafeguardsJson.TryInt64(raw, out index);

    /// <summary>
    /// Go <c>json.Unmarshal(event["delta"], &amp;struct{Type,Partial string})</c>:
    /// a null value succeeds as the zero struct; any field-type error rejects
    /// the whole delta.
    /// </summary>
    private static void DecodeInputDelta(byte[]? raw, out string type, out string partial)
    {
        type = string.Empty;
        partial = string.Empty;

        var kind = SafeguardsJson.ReadRoot(raw, StringComparer.OrdinalIgnoreCase, out var fields);
        if (kind == SafeguardsRootKind.Null)
        {
            return;
        }

        if (kind != SafeguardsRootKind.Object
            || fields is null
            || !SafeguardsJson.TryFieldString(fields, "type", out type)
            || !SafeguardsJson.TryFieldString(fields, "partial_json", out partial))
        {
            throw new SafeguardsDecodeException(
                "decode safeguard content block delta: json: cannot unmarshal value into Go value of type struct { Type string; PartialJson string }");
        }
    }

    private sealed class StreamedTool(SafeguardsToolUse tool)
    {
        public SafeguardsToolUse Tool { get; } = tool;

        public byte[]? Partial { get; set; }

        public bool Stopped { get; set; }
    }
}

/// <summary>
/// Port of api.safeguardsStream (internal/server/api/safeguards.go:54-184):
/// withholds the terminal delta until the upstream has completed and released
/// its capacity, then reviews it before replaying the buffered terminal events.
/// A nil evaluator leaves the source unchanged.
/// </summary>
public sealed class SafeguardsStream : IStream<StreamEvent>
{
    private readonly IStream<StreamEvent> _source;
    private readonly SafeguardsReviewer _reviewer;
    private readonly CancellationToken _context;
    private readonly Action? _abortUpstream;
    private readonly List<StreamEvent> _pending = [];
    private StreamEvent? _current;
    private Exception? _error;
    private bool _exhausted;

    private SafeguardsStream(
        IStream<StreamEvent> source,
        SafeguardsReviewer reviewer,
        CancellationToken context,
        Action? abortUpstream)
    {
        _source = source;
        _reviewer = reviewer;
        _context = context;
        _abortUpstream = abortUpstream;
    }

    /// <summary>Port of api.wrapSafeguardsStream.</summary>
    public static IStream<StreamEvent> Wrap(
        IStream<StreamEvent> source,
        SafeguardsEvaluator? evaluator,
        CancellationToken cancellationToken,
        Action? abortUpstream)
        => evaluator is null
            ? source
            : DeliveryCapabilities.Preserve(
                source,
                new SafeguardsStream(source, new SafeguardsReviewer(evaluator), cancellationToken, abortUpstream));

    public StreamEvent? Current => _current;

    /// <summary>
    /// Port of <c>safeguardsStream.Next</c>: tail validation or observation
    /// errors stop delivery.
    /// </summary>
    public bool Next()
    {
        if (_error is not null)
        {
            return false;
        }

        StreamEvent? streamEvent;
        if (_pending.Count > 0)
        {
            streamEvent = _pending[0];
            _pending.RemoveAt(0);
        }
        else
        {
            if (_exhausted || !_source.Next())
            {
                return false;
            }

            streamEvent = _source.Current;
        }

        if (streamEvent is null || streamEvent.Data.Length == 0)
        {
            _current = streamEvent;
            return true;
        }

        if (TryDecodeEnvelope(streamEvent.Data, out var type, out var stopReason)
            && string.Equals(type, "message_delta", StringComparison.Ordinal)
            && stopReason.Length > 0
            && !_exhausted)
        {
            // Finish consuming the upstream before invoking the reviewer, so a
            // reviewer sharing a channel with concurrency=1 does not wait on its
            // own main request's slot. Deep-copy the event first because the
            // provider may reuse its buffers while the tail is drained.
            streamEvent = CloneEvent(streamEvent);
            var drainError = DrainTerminalTail();
            if (drainError is not null)
            {
                _error = drainError;
                return false;
            }
        }

        byte[] data;
        try
        {
            data = _reviewer.Observe(_context, streamEvent.Data);
        }
        catch (Exception ex)
        {
            _error = ex;
            return false;
        }

        var reviewed = CloneEvent(streamEvent);
        reviewed.Data = data;
        reviewed.Size = data.Length;
        _current = reviewed;
        return true;
    }

    /// <summary>Local review or tail failures win over the source error.</summary>
    public Exception? Err => _error ?? _source.Err;

    public void Dispose() => _source.Dispose();

    /// <summary>
    /// Port of <c>drainTerminalTail</c> (safeguards.go:129-173): buffer a
    /// bounded tail and require message_stop before a clean upstream EOF. The
    /// optional abort callback bounds a stalled tail to one second.
    /// </summary>
    private Exception? DrainTerminalTail()
    {
        Timer? timer = null;
        if (_abortUpstream is not null)
        {
            timer = new Timer(
                static state =>
                {
                    try
                    {
                        ((Action)state!).Invoke();
                    }
                    catch (Exception)
                    {
                        // A failed abort cannot make the tail any more complete.
                    }
                },
                _abortUpstream,
                TimeSpan.FromSeconds(1),
                Timeout.InfiniteTimeSpan);
        }

        try
        {
            var bytes = 0;
            var stopped = false;
            while (_source.Next())
            {
                var tailEvent = _source.Current;
                if (tailEvent is null)
                {
                    continue;
                }

                bytes += tailEvent.Data.Length;
                if (_pending.Count >= 256 || bytes > 1 << 20)
                {
                    return new InvalidOperationException(
                        "safeguards terminal stream tail exceeds limit");
                }

                if (tailEvent.Data.Length != 0)
                {
                    if (!TryDecodeEventType(tailEvent.Data, out var tailType))
                    {
                        return new SafeguardsDecodeException(
                            "decode safeguard terminal stream event: invalid JSON document");
                    }

                    switch (tailType)
                    {
                        case "message_stop":
                            stopped = true;
                            break;
                        case "ping":
                            break;
                        default:
                            return new InvalidOperationException(
                                "unexpected safeguards terminal stream event "
                                + SafeguardsJson.GoQuote(tailType));
                    }
                }

                _pending.Add(CloneEvent(tailEvent));
            }

            _exhausted = true;
            if (_source.Err is { } sourceError)
            {
                return sourceError;
            }

            if (!stopped)
            {
                return new InvalidOperationException(
                    "safeguards stream ended without message_stop");
            }

            return null;
        }
        finally
        {
            timer?.Dispose();
        }
    }

    /// <summary>
    /// Go <c>json.Unmarshal(data, &amp;struct{Type string; Delta struct{StopReason string}})</c>.
    /// A non-object root or a wrongly typed field fails the decode; unknown
    /// fields are ignored and null values are no-ops.
    /// </summary>
    private static bool TryDecodeEnvelope(byte[] data, out string type, out string stopReason)
    {
        type = string.Empty;
        stopReason = string.Empty;

        var kind = SafeguardsJson.ReadRoot(data, StringComparer.OrdinalIgnoreCase, out var fields);
        if (kind == SafeguardsRootKind.Null)
        {
            return true;
        }

        if (kind != SafeguardsRootKind.Object || fields is null)
        {
            return false;
        }

        if (!SafeguardsJson.TryFieldString(fields, "type", out type))
        {
            return false;
        }

        if (!fields.TryGetValue("delta", out var deltaRaw))
        {
            return true;
        }

        var deltaKind = SafeguardsJson.ReadRoot(deltaRaw, StringComparer.OrdinalIgnoreCase, out var deltaFields);
        if (deltaKind == SafeguardsRootKind.Null)
        {
            return true;
        }

        if (deltaKind != SafeguardsRootKind.Object || deltaFields is null)
        {
            return false;
        }

        return SafeguardsJson.TryFieldString(deltaFields, "stop_reason", out stopReason);
    }

    /// <summary>Go <c>json.Unmarshal(data, &amp;struct{Type string})</c>.</summary>
    private static bool TryDecodeEventType(byte[] data, out string type)
    {
        type = string.Empty;

        var kind = SafeguardsJson.ReadRoot(data, StringComparer.OrdinalIgnoreCase, out var fields);
        if (kind == SafeguardsRootKind.Null)
        {
            return true;
        }

        if (kind != SafeguardsRootKind.Object || fields is null)
        {
            return false;
        }

        return SafeguardsJson.TryFieldString(fields, "type", out type);
    }

    /// <summary>Port of <c>copy := *event; copy.Data = append([]byte(nil), ...)</c>.</summary>
    private static StreamEvent CloneEvent(StreamEvent source) => new()
    {
        LastEventId = source.LastEventId,
        Type = source.Type,
        Data = (byte[])source.Data.Clone(),
        CleanEofCompletionEvidence = source.CleanEofCompletionEvidence,
        Size = source.Size,
        Headers = source.Headers,
    };
}
