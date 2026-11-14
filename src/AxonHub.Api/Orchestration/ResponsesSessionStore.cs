using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using Microsoft.Extensions.Logging;
using LlmGoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of <c>responsesSessionLoadFunc</c> (responses_session.go:34): reloads the
/// original request and final response of a completed Responses request from
/// persistence. The reference reads the API key and project from the context;
/// the port passes the authenticated identity explicitly.
/// </summary>
public delegate Task<ResponsesSessionLoaded> ResponsesSessionLoadFunc(
    string responseId,
    RequestIdentity identity,
    CancellationToken cancellationToken);

/// <summary>Port of the loader's <c>(requestBody, responseBody, found)</c> tuple.</summary>
public sealed record ResponsesSessionLoaded(byte[]? RequestBody, byte[]? ResponseBody, bool Found)
{
    public static ResponsesSessionLoaded NotFound { get; } = new(null, null, false);
}

/// <summary>Port of <c>responsesSessionKey</c> (responses_session.go:36-39).</summary>
public readonly record struct ResponsesSessionKey(string Scope, string ResponseId);

/// <summary>Port of <c>responsesSessionRecord</c> (responses_session.go:41-47).</summary>
internal sealed class ResponsesSessionRecord
{
    public IReadOnlyList<byte[]> Input { get; init; } = [];

    public IReadOnlyList<byte[]> Output { get; init; } = [];

    public string SessionId { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public int Size { get; init; }
}

/// <summary>Result of <see cref="ResponsesSessionStore.PrepareAsync"/>.</summary>
public readonly record struct ResponsesSessionPrepared(byte[]? Body, string SessionId);

/// <summary>
/// Port of <c>orchestrator.responsesSessionStore</c>
/// (internal/server/orchestrator/responses_session.go): keeps recently completed
/// Responses turns in memory so a client that sends
/// <c>previous_response_id</c> does not have to resend the whole conversation.
/// The store is process-wide, keyed by the authenticated session scope and the
/// provider response id, expires entries after two hours, and caps both the
/// record count and the buffered bytes.
/// </summary>
/// <remarks>
/// The reference talks to <c>shared.GetSessionID</c> / <c>GetSessionScope</c>
/// through the context. The port threads both values explicitly (C062); the
/// JSON handling reproduces Go's <c>encoding/json</c> bytes: sorted object keys,
/// compacted raw messages and HTML escaping.
/// </remarks>
public sealed class ResponsesSessionStore
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromHours(2);
    internal const int MaxRecords = 2048;
    internal const int MaxBytes = 64 << 20;
    internal const int MaxResponse = 1 << 20;

    // Go's decoder accepts nesting up to 10000 levels; System.Text.Json defaults
    // to 64, so the higher limit keeps the same accept/reject boundary.
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 10_000 };

    private static readonly Action<ILogger, Exception?> LogRestoreFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "RestoreResponsesSessionFailed"),
            "Failed to restore persisted Responses session");

    private readonly object _mutex = new();
    private readonly TimeProvider _timeProvider;
    private readonly ResponsesSessionLoadFunc? _load;
    private readonly ILogger<ResponsesSessionStore>? _logger;
    private readonly ResponsesInboundTransformer _aggregate = new();
    private readonly Dictionary<ResponsesSessionKey, ResponsesSessionRecord> _byResponse = [];
    private int _totalBytes;

    public ResponsesSessionStore(
        TimeProvider? timeProvider = null,
        ResponsesSessionLoadFunc? load = null,
        ILogger<ResponsesSessionStore>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _load = load;
        _logger = logger;
    }

    /// <summary>The live record table; exposed for the port's store tests.</summary>
    internal Dictionary<ResponsesSessionKey, ResponsesSessionRecord> ByResponse => _byResponse;

    /// <summary>The live byte accounting; exposed for the port's store tests.</summary>
    internal int TotalBytes
    {
        get => _totalBytes;
        set => _totalBytes = value;
    }

    /// <summary>
    /// Port of <c>responsesSessionStore.prepare</c> (responses_session.go:58-107):
    /// normalize the client-provided history, and when <c>previous_response_id</c>
    /// resolves to a known turn, prepend that turn's input and output to the
    /// request before dropping the continuation marker.
    /// </summary>
    public async Task<ResponsesSessionPrepared> PrepareAsync(
        byte[]? body,
        string? sessionId,
        string? scope,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var current = TryParseObject(body);
        if (current is null)
        {
            return new ResponsesSessionPrepared(body, ResponseSessionId(sessionId));
        }

        var normalizedInput = NormalizeResponseSessionInput(
            current.GetValueOrDefault("input"),
            out var inputChanged);
        if (inputChanged)
        {
            current["input"] = normalizedInput!;
        }

        var previousId = ResponseSessionString(current.GetValueOrDefault("previous_response_id"));
        if (previousId.Length == 0)
        {
            return new ResponsesSessionPrepared(
                EncodePreparedResponsesBody(body, current, inputChanged),
                ResponseSessionId(sessionId));
        }

        var record = await LookupOrLoadAsync(
                previousId,
                scope,
                identity,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (record is null)
        {
            return new ResponsesSessionPrepared(
                EncodePreparedResponsesBody(body, current, inputChanged),
                ResponseSessionId(sessionId));
        }

        if (record.SessionId.Length > 0)
        {
            sessionId = record.SessionId;
        }
        sessionId = ResponseSessionId(sessionId);

        var merged = new Dictionary<string, byte[]>(current, StringComparer.Ordinal);

        var baseItems = NormalizeResponseSessionItems(record.Input, out _);
        var normalizedOutput = NormalizeResponseSessionItems(record.Output, out _);
        baseItems.AddRange(normalizedOutput);
        baseItems.AddRange(ResponseSessionInputItems(current.GetValueOrDefault("input")));
        if (baseItems.Count > 0)
        {
            merged["input"] = MarshalRawArray(baseItems);
        }
        else
        {
            merged.Remove("input");
        }

        merged.Remove("previous_response_id");

        return new ResponsesSessionPrepared(MarshalRawObject(merged), sessionId);
    }

    /// <summary>
    /// Port of <c>responsesSessionStore.lookupOrLoad</c>
    /// (responses_session.go:109-128): an in-memory hit wins; a miss asks the
    /// loader once and caches the restored turn.
    /// </summary>
    private async Task<ResponsesSessionRecord?> LookupOrLoadAsync(
        string responseId,
        string? scope,
        RequestIdentity identity,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var record = Lookup(responseId, scope);
        if (record is not null || _load is null)
        {
            return record;
        }

        ResponsesSessionLoaded loaded;
        try
        {
            loaded = await _load(responseId, identity, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // responses_session.go:119-121: a loader failure is logged and the
            // continuation falls back to the client-supplied history.
            if (_logger is not null)
            {
                LogRestoreFailed(_logger, ex);
            }

            return null;
        }

        if (!loaded.Found)
        {
            return null;
        }

        Record(loaded.RequestBody, loaded.ResponseBody, sessionId, scope);
        return Lookup(responseId, scope);
    }

    /// <summary>
    /// Port of <c>responsesSessionStore.record</c> (responses_session.go:142-201):
    /// remember a completed turn's input and final output, keyed by scope and the
    /// provider response id.
    /// </summary>
    public void Record(
        byte[]? requestBody,
        byte[]? responseBody,
        string? sessionId,
        string? scope)
    {
        if (responseBody is null || responseBody.Length == 0 || responseBody.Length > MaxResponse)
        {
            return;
        }

        var requestPayload = TryParseObject(requestBody);
        if (requestPayload is null)
        {
            return;
        }

        var response = TryParseResponseMeta(responseBody);
        if (response is null || response.Id.Length == 0)
        {
            return;
        }

        // Both spellings have appeared in Responses-compatible implementations.
        if (response.Status is "failed" or "cancelled" or "canceled")
        {
            return;
        }

        var input = ResponseSessionInputItems(requestPayload.GetValueOrDefault("input"));
        var output = CloneValues(response.Output);
        var size = RawSize(input) + RawSize(output);
        if (size > MaxResponse)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return;
        }

        var key = new ResponsesSessionKey(scope, response.Id);

        lock (_mutex)
        {
            EvictExpiredLocked(now);
            if (_byResponse.TryGetValue(key, out var previous))
            {
                _totalBytes -= previous.Size;
            }

            _byResponse[key] = new ResponsesSessionRecord
            {
                Input = CloneValues(input),
                Output = output,
                SessionId = sessionId ?? string.Empty,
                UpdatedAt = now,
                Size = size,
            };
            _totalBytes += size;

            while (_byResponse.Count > MaxRecords || _totalBytes > MaxBytes)
            {
                if (!EvictOldestLocked())
                {
                    _totalBytes = 0;
                    break;
                }
            }
        }
    }

    /// <summary>Port of <c>responsesSessionStore.lookup</c> (responses_session.go:203-226).</summary>
    internal ResponsesSessionRecord? Lookup(string responseId, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var key = new ResponsesSessionKey(scope, responseId);

        lock (_mutex)
        {
            EvictExpiredLocked(now);
            if (!_byResponse.TryGetValue(key, out var record))
            {
                return null;
            }

            return new ResponsesSessionRecord
            {
                Input = CloneValues(record.Input),
                Output = CloneValues(record.Output),
                SessionId = record.SessionId,
                UpdatedAt = record.UpdatedAt,
                Size = record.Size,
            };
        }
    }

    /// <summary>
    /// Port of <c>responsesSessionStore.wrapStream</c>
    /// (responses_session.go:265-278): buffer the event stream until its terminal
    /// event, then aggregate and record the completed response. Response headers
    /// attached by the pipeline survive the extra layer.
    /// </summary>
    public IStream<StreamEvent> WrapStream(
        IStream<StreamEvent> stream,
        byte[] requestBody,
        string? sessionId,
        string? scope)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var responseHeaders = ResponseHeaders.GetResponseHeaders(stream);
        var wrapped = new ResponsesSessionStream(
            this,
            [.. requestBody],
            sessionId,
            scope,
            stream);

        return ResponseHeaders.WithResponseHeaders(wrapped, responseHeaders);
    }

    internal byte[] AggregateChunks(IReadOnlyList<StreamEvent> chunks)
        => _aggregate.AggregateStreamChunks(chunks, out _);

    private void EvictExpiredLocked(DateTimeOffset now)
    {
        foreach (var key in _byResponse.Keys.ToList())
        {
            var record = _byResponse[key];
            if (now - record.UpdatedAt > Ttl)
            {
                _totalBytes -= record.Size;
                _byResponse.Remove(key);
            }
        }
    }

    private bool EvictOldestLocked()
    {
        ResponsesSessionKey? oldestKey = null;
        var oldest = DateTimeOffset.MinValue;
        foreach (var (key, record) in _byResponse)
        {
            if (oldestKey is null || record.UpdatedAt < oldest)
            {
                oldestKey = key;
                oldest = record.UpdatedAt;
            }
        }

        if (oldestKey is not { } victim)
        {
            return false;
        }

        _totalBytes -= _byResponse[victim].Size;
        _byResponse.Remove(victim);
        return true;
    }

    private static byte[]? EncodePreparedResponsesBody(
        byte[]? original,
        Dictionary<string, byte[]> payload,
        bool changed)
        => changed ? MarshalRawObject(payload) : original;

    private static Dictionary<string, byte[]>? TryParseObject(byte[]? raw)
    {
        if (raw is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw, ParseOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryParseArrayItems(byte[]? raw, out List<byte[]> items)
    {
        items = [];
        if (raw is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(raw, ParseOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                items.Add(Encoding.UTF8.GetBytes(item.GetRawText()));
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryParseString(byte[]? raw)
    {
        if (raw is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw, ParseOptions);
            if (document.RootElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return document.RootElement.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Port of the response struct decode in <c>record</c> (responses_session.go:152-157):
    /// Go rejects the whole document on any field type mismatch, so the port
    /// does the same rather than reading fields leniently.
    /// </summary>
    private static ResponseMeta? TryParseResponseMeta(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, ParseOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var id = string.Empty;
            if (root.TryGetProperty("id", out var idElement))
            {
                if (idElement.ValueKind == JsonValueKind.String)
                {
                    id = idElement.GetString() ?? string.Empty;
                }
                else if (idElement.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }
            }

            var status = string.Empty;
            if (root.TryGetProperty("status", out var statusElement))
            {
                if (statusElement.ValueKind == JsonValueKind.String)
                {
                    status = statusElement.GetString() ?? string.Empty;
                }
                else if (statusElement.ValueKind != JsonValueKind.Null)
                {
                    return null;
                }
            }

            var output = new List<byte[]>();
            if (root.TryGetProperty("output", out var outputElement)
                && outputElement.ValueKind != JsonValueKind.Null)
            {
                if (outputElement.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var item in outputElement.EnumerateArray())
                {
                    output.Add(Encoding.UTF8.GetBytes(item.GetRawText()));
                }
            }

            return new ResponseMeta(id, status, output);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[]? NormalizeResponseSessionInput(byte[]? value, out bool changed)
    {
        changed = false;
        if (value is null || !TryParseArrayItems(value, out var items))
        {
            return value;
        }

        var normalized = NormalizeResponseSessionItems(items, out changed);
        return changed ? MarshalRawArray(normalized) : value;
    }

    private static List<byte[]> NormalizeResponseSessionItems(
        IReadOnlyList<byte[]> items,
        out bool changed)
    {
        var normalized = new List<byte[]>(items.Count);
        changed = false;
        foreach (var item in items)
        {
            var itemObject = TryParseObject(item);
            if (itemObject is null || !itemObject.ContainsKey("status"))
            {
                normalized.Add([.. item]);
                continue;
            }

            if (ResponseSessionString(itemObject.GetValueOrDefault("type")) == "web_search_call")
            {
                normalized.Add([.. item]);
                continue;
            }

            itemObject.Remove("status");
            normalized.Add(MarshalRawObject(itemObject));
            changed = true;
        }

        return normalized;
    }

    /// <summary>
    /// Port of <c>responseSessionInputItems</c> (responses_session.go:389-412):
    /// a string input becomes one user message, like OpenAI's shorthand.
    /// </summary>
    private static List<byte[]> ResponseSessionInputItems(byte[]? value)
    {
        if (value is null || IsNullLiteral(value))
        {
            return [];
        }

        if (TryParseArrayItems(value, out var items))
        {
            return items;
        }

        var text = TryParseString(value);
        if (text is null)
        {
            return [];
        }

        var item = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["content"] = EncodeGoString(text),
            ["role"] = EncodeGoString("user"),
            ["type"] = EncodeGoString("message"),
        };

        return [MarshalRawObject(item)];
    }

    private static bool IsNullLiteral(byte[] value)
        => string.Equals(
            Encoding.UTF8.GetString(value).Trim(),
            "null",
            StringComparison.Ordinal);

    private static string ResponseSessionString(byte[]? value)
        => TryParseString(value) ?? string.Empty;

    private static string ResponseSessionId(string? sessionId)
        => string.IsNullOrEmpty(sessionId)
            ? "responses-" + Guid.NewGuid().ToString()
            : sessionId;

    private static byte[] MarshalRawArray(IReadOnlyList<byte[]> items)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)'[');
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                buffer.WriteByte((byte)',');
            }

            buffer.Write(LlmGoJson.Compact(items[i]));
        }

        buffer.WriteByte((byte)']');
        return buffer.ToArray();
    }

    /// <summary>
    /// <c>json.Marshal(map[string]json.RawMessage)</c>: keys sort by their UTF-8
    /// bytes (Go map ordering), every raw value is compacted and HTML-escaped.
    /// </summary>
    private static byte[] MarshalRawObject(IReadOnlyDictionary<string, byte[]> payload)
    {
        var entries = new List<(byte[] SortKey, byte[] Key, byte[] Value)>(payload.Count);
        foreach (var (key, value) in payload)
        {
            entries.Add((Encoding.UTF8.GetBytes(key), EncodeGoString(key), LlmGoJson.Compact(value)));
        }

        entries.Sort(static (left, right) =>
            left.SortKey.AsSpan().SequenceCompareTo(right.SortKey));

        using var buffer = new MemoryStream();
        buffer.WriteByte((byte)'{');
        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                buffer.WriteByte((byte)',');
            }

            buffer.Write(entries[i].Key);
            buffer.WriteByte((byte)':');
            buffer.Write(entries[i].Value);
        }

        buffer.WriteByte((byte)'}');
        return buffer.ToArray();
    }

    /// <summary>Go's <c>encodeState.string</c> with <c>escapeHTML=true</c>.</summary>
    private static byte[] EncodeGoString(string value)
    {
        using var buffer = new MemoryStream(value.Length + 2);
        buffer.WriteByte((byte)'"');
        Span<byte> encoded = stackalloc byte[4];
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    WriteAscii(buffer, "\\\"");
                    continue;
                case '\\':
                    WriteAscii(buffer, "\\\\");
                    continue;
                case '\n':
                    WriteAscii(buffer, "\\n");
                    continue;
                case '\r':
                    WriteAscii(buffer, "\\r");
                    continue;
                case '\t':
                    WriteAscii(buffer, "\\t");
                    continue;
                case '<':
                    WriteAscii(buffer, "\\u003c");
                    continue;
                case '>':
                    WriteAscii(buffer, "\\u003e");
                    continue;
                case '&':
                    WriteAscii(buffer, "\\u0026");
                    continue;
                case '\u2028':
                    WriteAscii(buffer, "\\u2028");
                    continue;
                case '\u2029':
                    WriteAscii(buffer, "\\u2029");
                    continue;
            }

            if (rune.Value < 0x20)
            {
                WriteAscii(buffer, "\\u00");
                buffer.WriteByte(HexDigit(rune.Value >> 4));
                buffer.WriteByte(HexDigit(rune.Value & 0xF));
                continue;
            }

            var count = rune.EncodeToUtf8(encoded);
            buffer.Write(encoded[..count]);
        }

        buffer.WriteByte((byte)'"');
        return buffer.ToArray();
    }

    private static void WriteAscii(Stream stream, string text)
    {
        foreach (var c in text)
        {
            stream.WriteByte((byte)c);
        }
    }

    private static byte HexDigit(int value)
        => (byte)(value < 10 ? '0' + value : 'a' + value - 10);

    private static List<byte[]> CloneValues(IEnumerable<byte[]> values)
        => [.. values.Select(static value => value.ToArray())];

    private static int RawSize(IEnumerable<byte[]> values)
    {
        var total = 0;
        foreach (var value in values)
        {
            total += value.Length;
        }

        return total;
    }

    private sealed record ResponseMeta(string Id, string Status, List<byte[]> Output);
}

/// <summary>
/// Port of <c>responsesSessionStream</c> (responses_session.go:280-346): passes
/// every event through unchanged while buffering a bounded copy and records the
/// aggregated response as soon as the terminal event appears (or on dispose for
/// readers that stop right after it).
/// </summary>
internal sealed class ResponsesSessionStream : IStream<StreamEvent>
{
    private readonly ResponsesSessionStore _store;
    private readonly byte[] _requestBody;
    private readonly string? _sessionId;
    private readonly string? _scope;
    private readonly IStream<StreamEvent> _inner;
    private List<StreamEvent> _chunks = [];
    private int _chunksBytes;
    private bool _terminal;
    private bool _recorded;

    internal ResponsesSessionStream(
        ResponsesSessionStore store,
        byte[] requestBody,
        string? sessionId,
        string? scope,
        IStream<StreamEvent> inner)
    {
        _store = store;
        _requestBody = requestBody;
        _sessionId = sessionId;
        _scope = scope;
        _inner = inner;
    }

    /// <summary>The overflow flag, exposed for the port's store tests.</summary>
    internal bool Overflow { get; private set; }

    public StreamEvent? Current { get; private set; }

    public Exception? Err => _inner.Err;

    public bool Next()
    {
        if (!_inner.Next())
        {
            Current = null;
            return false;
        }

        Current = _inner.Current;
        if (Current is { } current)
        {
            if (!Overflow)
            {
                _chunksBytes += Encoding.UTF8.GetByteCount(current.Type)
                    + Encoding.UTF8.GetByteCount(current.LastEventId ?? string.Empty)
                    + current.Data.Length;
                if (_chunksBytes > ResponsesSessionStore.MaxResponse)
                {
                    _chunks = [];
                    Overflow = true;
                }
                else
                {
                    _chunks.Add(new StreamEvent
                    {
                        Type = current.Type,
                        LastEventId = current.LastEventId,
                        Data = [.. current.Data],
                        Size = current.Size,
                    });
                }
            }

            if (new StreamTerminalTracker(1).Observe(current))
            {
                _terminal = true;
                RecordCompleted();
            }
        }

        return true;
    }

    public void Dispose()
    {
        RecordCompleted();
        _inner.Dispose();
    }

    private void RecordCompleted()
    {
        if (_recorded || Overflow || !_terminal)
        {
            return;
        }

        byte[] body;
        try
        {
            body = _store.AggregateChunks(_chunks);
        }
        catch (Exception)
        {
            return;
        }

        _store.Record(_requestBody, body, _sessionId, _scope);
        _recorded = true;
    }
}
