using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>Go: <c>webSocketReconnectRequiredError</c>.</summary>
internal sealed class WebSocketReconnectRequiredException : InvalidOperationException
{
    public WebSocketReconnectRequiredException()
        : base("websocket session context changed; reconnect and send full context")
    {
    }
}

/// <summary>
/// Port of responses/websocket_executor.go: the Responses WebSocket transport.
/// It turns a Responses HTTP request into a <c>response.create</c> WebSocket
/// event and exposes the upstream events as a stream. Sessions that carry a
/// session id and a session scope share a pooled connection, so the upstream can
/// keep its connection-local conversation state and prompt cache.
/// </summary>
/// <remarks>
/// Reference differences (see porting/differences.jsonl C070/C071): the dialer cannot
/// inherit a proxy or TLS configuration from the port's HTTP executor, because
/// <see cref="HttpExecutor"/> exposes no such seam; the session id and scope are
/// read from the wire request instead of Go's request context (C062); and the
/// payload is carried as parsed <see cref="JsonElement"/> values, so nested
/// objects keep their original key order and number lexemes instead of Go's
/// recursive map re-marshal.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Close() is the reference's teardown (Go's OutboundTransformer.Stop calls it) and disposes the "
        + "cleanup timer. Pooled connections own a semaphore standing in for Go's in-flight channel, which has no "
        + "Dispose either; it holds no native wait handle.")]
internal sealed class WebSocketExecutor : IExecutor
{
    /// <summary>Go: <c>WebSocketBetaHeaderValue</c>.</summary>
    public const string BetaHeaderValue = "responses_websockets=2026-02-06";

    internal const string SessionHeader = "Session_id";

    internal const string AccountIdHeader = "Chatgpt-Account-Id";

    internal const string OriginatorHeader = "Originator";

    internal const string UserAgentHeader = "User-Agent";

    internal const string OrgHeader = "OpenAI-Organization";

    internal const string ProjectHeader = "OpenAI-Project";

    internal static readonly TimeSpan DefaultIdleTtl = TimeSpan.FromMinutes(50);

    internal static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromMinutes(50);

    internal const int DefaultMaxPoolSize = 128;

    internal const int DefaultMaxRetainedInput = 16 << 20;

    /// <summary>Go: the process-wide <c>NewResponsesBudget(64 &lt;&lt; 20)</c>.</summary>
    internal const int DefaultRetainedBudget = 64 << 20;

    internal static readonly TimeSpan MinCleanupDelay = TimeSpan.FromMilliseconds(10);

    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.Ordinal)
    {
        "Content-Length",
        "Content-Type",
        "Host",
    };

    /// <summary>
    /// Headers that never shard the pool: dedicated key fields, protocol shape,
    /// and per-request observability headers (websocket_executor.go:553-592).
    /// </summary>
    private static readonly HashSet<string> ExcludedPoolIdentityHeaders = CanonicalHeaderSet(
        // Already represented as dedicated WebSocketPoolKey fields.
        "Authorization",
        SessionHeader,
        AccountIdHeader,
        OriginatorHeader,
        UserAgentHeader,
        OrgHeader,
        ProjectHeader,
        "OpenAI-Beta",

        // Protocol/request-shape headers do not affect the WebSocket session identity.
        "Accept",
        "Cache-Control",
        "Connection",

        // Per-request observability headers must not shard the session pool.
        "Baggage",
        "Traceparent",
        "Tracestate",
        "Sentry-Trace",
        "Uber-Trace-Id",
        "X-Amzn-Trace-Id",
        "X-B3-Flags",
        "X-B3-ParentSpanId",
        "X-B3-Sampled",
        "X-B3-SpanId",
        "X-B3-TraceId",
        "X-Cloud-Trace-Context",
        "X-Client-Request-Id",
        "X-Codex-Turn-Metadata",
        "X-Correlation-Id",
        "X-Datadog-Parent-Id",
        "X-Datadog-Sampling-Priority",
        "X-Datadog-Trace-Id",
        "X-Request-Id",
        "X-Request-Start",
        "X-Span-Id",
        "X-Trace-Id");

    private readonly IExecutor? _inner;
    private readonly object _mu = new();
    private readonly Dictionary<WebSocketPoolKey, PooledWebSocketConnection> _pool = [];
    private readonly ResponsesRetainedBudget _retainedBudget = new(DefaultRetainedBudget);
    private Timer? _cleanupTimer;
    private bool _cleanupScheduled;
    private bool _closed;

    /// <summary>Go: <c>newWebSocketExecutor</c>; <paramref name="inner"/> may be null in tests.</summary>
    public WebSocketExecutor(IExecutor? inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Go: the <c>ChannelID</c> half of <c>webSocketPoolKey</c>
    /// (websocket_executor.go:314, 566-569). The reference's executor lives in
    /// the per-channel outbound cache, so its pool is channel-scoped; the port
    /// caches executors process-wide (<see cref="WebSocketExecutorCache"/>) and
    /// keeps the channel in the key to preserve that separation.
    /// </summary>
    internal long ChannelId { get; init; }

    internal TimeSpan IdleTtl { get; set; } = DefaultIdleTtl;

    internal TimeSpan MaxLifetime { get; set; } = DefaultMaxLifetime;

    internal int MaxPoolSize { get; set; } = DefaultMaxPoolSize;

    internal int MaxRetainedInput { get; set; } = DefaultMaxRetainedInput;

    /// <summary>Pooled sessions, exposed for the reference tests.</summary>
    internal IReadOnlyDictionary<WebSocketPoolKey, PooledWebSocketConnection> Pool => _pool;

    internal bool CleanupScheduled => _cleanupScheduled;

    /// <summary>Go: <c>Inner()</c>.</summary>
    internal IExecutor? Inner() => _inner;

    /// <summary>Port of WebSocketExecutor.Do: aggregate the events into a 200 JSON response.</summary>
    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        var stream = DoStream(cancellationToken, request);
        try
        {
            var chunks = new List<StreamEvent>(16);
            while (stream.Next())
            {
                if (stream.Current is { } ev)
                {
                    chunks.Add(new StreamEvent
                    {
                        Type = ev.Type,
                        LastEventId = ev.LastEventId,
                        Data = [.. ev.Data],
                    });
                }
            }

            if (stream.Err is { } readError)
            {
                throw readError;
            }

            if (TopLevelWebSocketError(chunks) is { } topLevel)
            {
                throw topLevel;
            }

            // Response-level terminal events (response.failed,
            // response.cancelled, response.incomplete) are valid Responses API
            // payloads, not transport failures. Keep them as HTTP 200 response
            // objects so callers can inspect body.status/error/
            // incomplete_details instead of retrying or disabling the channel
            // as if the WebSocket transport failed.
            return new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = ResponsesAggregate.AggregateStreamChunks(chunks, out _),
                Request = request,
            };
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>
    /// Go: <c>TopLevelWebSocketError</c> — converts a top-level <c>error</c>
    /// event into the transport error the HTTP path would have produced.
    /// </summary>
    internal static Exception? TopLevelWebSocketError(IReadOnlyList<StreamEvent?> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.Type != "error")
            {
                continue;
            }

            WebSocketErrorEvent? evt;
            try
            {
                evt = LlmJson.Deserialize<WebSocketErrorEvent>(chunk.Data);
            }
            catch (JsonException)
            {
                return new InvalidOperationException("websocket error event");
            }

            if (evt is null)
            {
                return new InvalidOperationException("websocket error event");
            }

            var status = evt.Status ?? 0;
            var code = evt.Code ?? string.Empty;
            var message = evt.Message ?? string.Empty;
            var param = evt.Param ?? string.Empty;

            if (status is >= 400 and <= 599)
            {
                var detail = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
                if (message.Length > 0)
                {
                    detail["message"] = JsonSerializer.SerializeToElement(message);
                }

                if (code.Length > 0)
                {
                    detail["type"] = JsonSerializer.SerializeToElement(code);
                    detail["code"] = JsonSerializer.SerializeToElement(code);
                }

                if (param.Length > 0)
                {
                    detail["param"] = JsonSerializer.SerializeToElement(param);
                }

                if (evt.Error is { } nestedElement && nestedElement.ValueKind != JsonValueKind.Null)
                {
                    Dictionary<string, JsonElement?>? nested;
                    try
                    {
                        nested = LlmJson.Deserialize<Dictionary<string, JsonElement?>>(
                            nestedElement.GetRawText());
                    }
                    catch (JsonException ex)
                    {
                        return new InvalidOperationException(
                            "failed to decode websocket error detail: "
                            + OpenAiInboundTransformer.JsonErrorText(ex),
                            ex);
                    }

                    if (nested is not null)
                    {
                        foreach (var (key, value) in nested)
                        {
                            if (value is null)
                            {
                                continue;
                            }

                            if (value.Value.ValueKind == JsonValueKind.String
                                && value.Value.GetString()!.Length == 0)
                            {
                                continue;
                            }

                            detail[key] = value;
                        }
                    }
                }

                return new HttpClientError(
                    string.Empty,
                    string.Empty,
                    status,
                    GoHttpStatus.Text(status),
                    GoJson.Marshal(new WebSocketErrorEnvelope { Error = detail }));
            }

            if (code.Length > 0 && message.Length > 0)
            {
                return new InvalidOperationException("websocket error event: " + code + ": " + message);
            }

            if (message.Length > 0)
            {
                return new InvalidOperationException("websocket error event: " + message);
            }

            if (code.Length > 0)
            {
                return new InvalidOperationException("websocket error event: " + code);
            }

            return new InvalidOperationException("websocket error event");
        }

        return null;
    }

    /// <summary>Port of WebSocketExecutor.DoStream.</summary>
    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        var wsUrl = ToWebSocketUrl(request.Url);
        var payload = BuildWebSocketCreatePayload(request.Body);

        var headers = CloneHeaders(request.Headers);
        foreach (var managed in ManagedHeaders)
        {
            headers.Remove(managed);
        }

        if (request.Auth is not null)
        {
            ApplyWebSocketAuth(headers, request.Auth);
        }

        HttpClientUtils.HeaderSet(headers, "OpenAI-Beta", BetaHeaderValue);

        var originalPayload = ClonePayloadMap(payload);
        var lease = AcquirePreparedLease(cancellationToken, request, wsUrl, headers, payload, originalPayload);

        var connection = lease.Conn
            ?? throw new InvalidOperationException("websocket connection is nil");

        try
        {
            // gorilla's WriteJSON uses json.Encoder, which terminates the frame
            // with a newline.
            var message = GoJson.Marshal(payload);
            var frame = new byte[message.Length + 1];
            message.CopyTo(frame, 0);
            frame[^1] = (byte)'\n';

            connection.SendAsync(
                new ArraySegment<byte>(frame),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException ex)
        {
            lease.Release(evict: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(ex.Message, ex, cancellationToken);
            }

            throw new InvalidOperationException(
                "failed to write response.create websocket event: " + ex.Message, ex);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException)
        {
            lease.Release(evict: true);
            throw new InvalidOperationException(
                "failed to write response.create websocket event: " + ex.Message, ex);
        }

        return new WebSocketStream(cancellationToken, lease);
    }

    /// <summary>
    /// Port of WebSocketExecutor.Close: closes idle pooled sessions. Sessions
    /// still leased by a stream are removed when that stream releases them.
    /// </summary>
    public void Close()
    {
        var idle = new List<PooledWebSocketConnection>();
        lock (_mu)
        {
            _closed = true;
            if (_cleanupTimer is not null)
            {
                _cleanupTimer.Dispose();
                _cleanupTimer = null;
            }

            _cleanupScheduled = false;

            foreach (var key in new List<WebSocketPoolKey>(_pool.Keys))
            {
                var pc = _pool[key];
                if (pc.InFlightCount > 0)
                {
                    continue;
                }

                _pool.Remove(key);
                idle.Add(pc);
            }
        }

        foreach (var pc in idle)
        {
            pc.Close();
        }
    }

    private WebSocketLease AcquirePreparedLease(
        CancellationToken cancellationToken,
        HttpRequestData request,
        string wsUrl,
        Dictionary<string, List<string>> headers,
        Dictionary<string, JsonElement?> payload,
        Dictionary<string, JsonElement?> originalPayload)
    {
        var lease = Acquire(cancellationToken, request, wsUrl, headers);
        try
        {
            PrepareWebSocketPayloadForLease(payload, lease);
            return lease;
        }
        catch (WebSocketReconnectRequiredException)
        {
            lease.Release(evict: true);
        }
        catch
        {
            lease.Release(evict: true);
            throw;
        }

        RestorePayloadMap(payload, originalPayload);
        lease = Acquire(cancellationToken, request, wsUrl, headers);
        try
        {
            PrepareWebSocketPayloadForLease(payload, lease);
            return lease;
        }
        catch
        {
            lease.Release(evict: true);
            throw;
        }
    }

    private WebSocketLease Acquire(
        CancellationToken cancellationToken,
        HttpRequestData request,
        string wsUrl,
        Dictionary<string, List<string>> headers)
    {
        var (key, poolable) = PoolKey(request, wsUrl, headers);
        if (!poolable)
        {
            return new WebSocketLease(
                Dial(cancellationToken, request, wsUrl, headers),
                pooled: null,
                owner: null,
                reused: false);
        }

        while (true)
        {
            var pc = GetOrDialPooled(cancellationToken, request, key, wsUrl, headers);

            pc.InFlight.Wait(cancellationToken);
            var (closed, expired, used) = pc.LeaseState(DateTime.UtcNow, MaxLifetime);
            if (closed || expired)
            {
                pc.ReleaseInFlight();
                Evict(pc);
                continue;
            }

            return new WebSocketLease(pc.Conn, pc, this, used);
        }
    }

    private PooledWebSocketConnection GetOrDialPooled(
        CancellationToken cancellationToken,
        HttpRequestData request,
        WebSocketPoolKey key,
        string wsUrl,
        Dictionary<string, List<string>> headers)
    {
        var now = DateTime.UtcNow;
        lock (_mu)
        {
            if (_closed)
            {
                throw new InvalidOperationException("websocket executor is closed");
            }

            CleanupExpiredLocked(now);

            if (_pool.TryGetValue(key, out var existing) && !existing.IsClosed())
            {
                return existing;
            }
        }

        var conn = Dial(cancellationToken, request, wsUrl, headers);
        var pc = new PooledWebSocketConnection(key, conn, now);

        lock (_mu)
        {
            if (_closed)
            {
                conn.Dispose();
                throw new InvalidOperationException("websocket executor is closed");
            }

            if (_pool.TryGetValue(key, out var existing) && !existing.IsClosed())
            {
                conn.Dispose();
                return existing;
            }

            // maxPoolSize is a hard cap on tracked pooled sessions (idle +
            // in-flight). If every tracked session is currently in-flight,
            // report capacity backpressure instead of opening unbounded
            // overflow WebSockets. Same-session concurrency does not reach this
            // branch; it waits on that session's in-flight slot.
            if (MaxPoolSize > 0
                && _pool.Count >= MaxPoolSize
                && !EvictOldestIdleLocked())
            {
                conn.Dispose();
                throw new InvalidOperationException("websocket session pool is full");
            }

            // Go: pc.budget = e.retainedBudget, assigned under the executor lock
            // before the connection is visible in the pool.
            pc.Budget = _retainedBudget;
            _pool[key] = pc;
            ScheduleCleanupLocked(now);
        }

        return pc;
    }

    private static WebSocket Dial(
        CancellationToken cancellationToken,
        HttpRequestData request,
        string wsUrl,
        Dictionary<string, List<string>> headers)
        => ResponsesWebSocketClient.Connect(cancellationToken, wsUrl, headers, request);

    /// <summary>Port of poolKey; a session id and scope are both required.</summary>
    private (WebSocketPoolKey Key, bool Poolable) PoolKey(
        HttpRequestData request,
        string wsUrl,
        Dictionary<string, List<string>> headers)
    {
        var sessionId = HttpClientUtils.HeaderGet(headers, SessionHeader).Trim();
        if (sessionId.Length == 0 && request.SessionId is { } fromRequest)
        {
            sessionId = fromRequest.Trim();
        }

        if (sessionId.Length == 0)
        {
            return (default, false);
        }

        var scope = request.SessionScope?.Trim() ?? string.Empty;
        if (scope.Length == 0)
        {
            return (default, false);
        }

        return (new WebSocketPoolKey
        {
            ChannelId = ChannelId,
            Url = wsUrl,
            SessionId = sessionId,
            Scope = scope,
            Auth = AuthPoolIdentity(request.Auth, headers),
            AccountId = HttpClientUtils.HeaderGet(headers, AccountIdHeader).Trim(),
            Originator = HttpClientUtils.HeaderGet(headers, OriginatorHeader).Trim(),
            UserAgent = HttpClientUtils.HeaderGet(headers, UserAgentHeader).Trim(),
            Org = HttpClientUtils.HeaderGet(headers, OrgHeader).Trim(),
            Project = HttpClientUtils.HeaderGet(headers, ProjectHeader).Trim(),
            BetaHeader = HttpClientUtils.HeaderGet(headers, "OpenAI-Beta").Trim(),
            Headers = HeaderPoolIdentity(headers),
        }, true);
    }

    /// <summary>Go: <c>headerPoolIdentity</c>.</summary>
    internal static string HeaderPoolIdentity(Dictionary<string, List<string>>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return string.Empty;
        }

        var keys = new List<string>(headers.Count);
        foreach (var key in headers.Keys)
        {
            var canonical = HttpClientUtils.CanonicalHeaderKey(key);
            if (!ExcludedPoolIdentityHeaders.Contains(canonical))
            {
                keys.Add(canonical);
            }
        }

        if (keys.Count == 0)
        {
            return string.Empty;
        }

        keys.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var key in keys)
        {
            if (!headers.TryGetValue(key, out var values))
            {
                continue;
            }

            var sorted = new List<string>(values);
            sorted.Sort(StringComparer.Ordinal);

            builder.Append(key).Append(':');
            foreach (var value in sorted)
            {
                builder.Append(value).Append('\0');
            }

            builder.Append('\n');
        }

        return HashSecret(builder.ToString());
    }

    private static string AuthPoolIdentity(AuthConfig? auth, Dictionary<string, List<string>> headers)
    {
        if (auth is not null)
        {
            return auth.Type + ":" + (auth.HeaderKey ?? string.Empty) + ":" + HashSecret(auth.ApiKey);
        }

        return HashSecret(HttpClientUtils.HeaderGet(headers, "Authorization"));
    }

    private static string HashSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private bool EvictOldestIdleLocked()
    {
        WebSocketPoolKey? oldestKey = null;
        PooledWebSocketConnection? oldest = null;
        var now = DateTime.UtcNow;

        foreach (var (key, pc) in new List<KeyValuePair<WebSocketPoolKey, PooledWebSocketConnection>>(_pool))
        {
            var (closed, lastUsedAt, _) = pc.PoolState(now, MaxLifetime);
            if (closed)
            {
                _pool.Remove(key);
                continue;
            }

            if (pc.InFlightCount > 0)
            {
                continue;
            }

            // A dictionary key may be a struct, so track it separately rather
            // than re-deriving it from the connection.
            if (oldest is null || lastUsedAt < oldest.PoolLastUsedAt())
            {
                oldestKey = key;
                oldest = pc;
            }
        }

        if (oldest is null || oldestKey is null)
        {
            return false;
        }

        _pool.Remove(oldestKey.Value);
        oldest.Close();
        return true;
    }

    private void CleanupExpiredLocked(DateTime now)
    {
        foreach (var (key, pc) in new List<KeyValuePair<WebSocketPoolKey, PooledWebSocketConnection>>(_pool))
        {
            var (closed, lastUsedAt, expired) = pc.PoolState(now, MaxLifetime);
            if (closed)
            {
                _pool.Remove(key);
                continue;
            }

            if (pc.InFlightCount > 0)
            {
                continue;
            }

            if (expired || (IdleTtl > TimeSpan.Zero && now - lastUsedAt > IdleTtl))
            {
                _pool.Remove(key);
                pc.Close();
            }
        }
    }

    private void ScheduleCleanupLocked(DateTime now)
    {
        if (_closed
            || _cleanupScheduled
            || _pool.Count == 0
            || (IdleTtl <= TimeSpan.Zero && MaxLifetime <= TimeSpan.Zero))
        {
            return;
        }

        var delay = NextCleanupDelayLocked(now);
        _cleanupScheduled = true;
        _cleanupTimer = new Timer(
            static state => ((WebSocketExecutor)state!).RunScheduledCleanup(),
            this,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    private void RunScheduledCleanup()
    {
        var now = DateTime.UtcNow;
        Timer? timer;
        lock (_mu)
        {
            _cleanupScheduled = false;
            timer = _cleanupTimer;
            _cleanupTimer = null;

            if (_closed)
            {
                timer?.Dispose();
                return;
            }

            CleanupExpiredLocked(now);
            ScheduleCleanupLocked(now);
        }

        timer?.Dispose();
    }

    private TimeSpan NextCleanupDelayLocked(DateTime now)
    {
        TimeSpan? delay = null;
        foreach (var pc in _pool.Values)
        {
            if (pc.InFlightCount > 0)
            {
                continue;
            }

            var (closed, lastUsedAt, expired) = pc.PoolState(now, MaxLifetime);
            if (closed || expired)
            {
                return MinCleanupDelay;
            }

            if (IdleTtl > TimeSpan.Zero)
            {
                var remaining = IdleTtl - (now - lastUsedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    return MinCleanupDelay;
                }

                if (delay is null || remaining < delay.Value)
                {
                    delay = remaining;
                }
            }

            if (MaxLifetime > TimeSpan.Zero)
            {
                var remaining = MaxLifetime - (now - pc.CreatedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    return MinCleanupDelay;
                }

                if (delay is null || remaining < delay.Value)
                {
                    delay = remaining;
                }
            }
        }

        if (delay is null || delay.Value <= TimeSpan.Zero)
        {
            if (IdleTtl > TimeSpan.Zero)
            {
                return IdleTtl;
            }

            if (MaxLifetime > TimeSpan.Zero)
            {
                return MaxLifetime;
            }

            return MinCleanupDelay;
        }

        return delay.Value < MinCleanupDelay ? MinCleanupDelay : delay.Value;
    }

    private void Evict(PooledWebSocketConnection pc)
    {
        lock (_mu)
        {
            if (_pool.TryGetValue(pc.Key, out var tracked) && ReferenceEquals(tracked, pc))
            {
                _pool.Remove(pc.Key);
            }
        }

        pc.Close();
    }

    internal void ReleaseLeaseToPool(PooledWebSocketConnection pc, bool evict)
    {
        bool closed;
        lock (_mu)
        {
            closed = _closed;
            if ((evict || closed)
                && _pool.TryGetValue(pc.Key, out var tracked)
                && ReferenceEquals(tracked, pc))
            {
                _pool.Remove(pc.Key);
            }
        }

        if (evict || closed)
        {
            pc.Close();
            return;
        }

        pc.MarkUsed();
        lock (_mu)
        {
            ScheduleCleanupLocked(DateTime.UtcNow);
        }
    }

    /// <summary>Go: <c>toWebSocketURL</c>.</summary>
    internal static string ToWebSocketUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            var separator = rawUrl.IndexOf("://", StringComparison.Ordinal);
            if (separator > 0)
            {
                var malformedScheme = rawUrl[..separator].ToLowerInvariant();
                if (malformedScheme is not ("http" or "https" or "ws" or "wss"))
                {
                    throw new InvalidOperationException(
                        "unsupported websocket request scheme \"" + malformedScheme + "\"");
                }
            }

            throw new InvalidOperationException(
                "invalid websocket request url: parse \"" + rawUrl + "\": invalid URI");
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        switch (scheme)
        {
            case "https":
                scheme = "wss";
                break;
            case "http":
                scheme = "ws";
                break;
            case "wss":
            case "ws":
                break;
            default:
                throw new InvalidOperationException(
                    "unsupported websocket request scheme \"" + scheme + "\"");
        }

        return scheme + "://" + uri.Authority + uri.PathAndQuery;
    }

    /// <summary>Go: <c>prepareWebSocketPayloadForLease</c>.</summary>
    internal static void PrepareWebSocketPayloadForLease(
        Dictionary<string, JsonElement?> payload,
        WebSocketLease? lease)
    {
        if (lease?.Pooled is null)
        {
            return;
        }

        var (fullInput, hasInput) = PayloadInputItems(payload);
        if (!hasInput)
        {
            return;
        }

        // Go keeps the full input for the next turn only when it fits the
        // retained-input cap; an oversized conversation is never cached, so the
        // next turn sends the full context instead of evicting the session.
        lease.NextFullInput = lease.Owner is null
            || RawMessagesSize(fullInput) <= lease.Owner.MaxRetainedInput
                ? CloneItems(fullInput)
                : [];

        var (previousInput, previousResponseId) = lease.Pooled.PreviousTurn();
        if (!lease.Reused || previousResponseId.Length == 0)
        {
            return;
        }

        var explicitId = ExplicitPreviousResponseId(payload);
        if (explicitId.Length > 0)
        {
            if (!string.Equals(explicitId, previousResponseId, StringComparison.Ordinal))
            {
                throw new WebSocketReconnectRequiredException();
            }

            // Native WebSocket clients send only the new input together with
            // the response id returned on this connection. Preserve that delta
            // as-is.
            return;
        }

        if (previousInput.Count == 0)
        {
            return;
        }

        var (suffix, appendOnly) = InputSuffix(previousInput, fullInput);
        if (!appendOnly || suffix.Count == 0 || SuffixStartsWithResponseOutput(suffix))
        {
            throw new WebSocketReconnectRequiredException();
        }

        payload["input"] = JsonSerializer.SerializeToElement(suffix, LlmJson.Options);
        payload["previous_response_id"] = JsonSerializer.SerializeToElement(previousResponseId);
    }

    /// <summary>Go: <c>clonePayloadMap</c>.</summary>
    internal static Dictionary<string, JsonElement?> ClonePayloadMap(Dictionary<string, JsonElement?>? payload)
    {
        var clone = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (payload is null)
        {
            return clone;
        }

        foreach (var (key, value) in payload)
        {
            clone[key] = value;
        }

        return clone;
    }

    /// <summary>Go: <c>restorePayloadMap</c>.</summary>
    internal static void RestorePayloadMap(
        Dictionary<string, JsonElement?> payload,
        Dictionary<string, JsonElement?> original)
    {
        payload.Clear();
        foreach (var (key, value) in original)
        {
            payload[key] = value;
        }
    }

    /// <summary>Go: <c>explicitPreviousResponseID</c>.</summary>
    internal static string ExplicitPreviousResponseId(Dictionary<string, JsonElement?> payload)
    {
        if (!payload.TryGetValue("previous_response_id", out var value)
            || value is not { ValueKind: JsonValueKind.String })
        {
            return string.Empty;
        }

        return value.Value.GetString()!.Trim();
    }

    /// <summary>Go: <c>suffixStartsWithResponseOutput</c>.</summary>
    internal static bool SuffixStartsWithResponseOutput(List<JsonElement> suffix)
    {
        if (suffix.Count == 0)
        {
            return false;
        }

        var first = suffix[0];
        if (first.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (first.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (first.TryGetProperty("role", out var roleElement)
            && roleElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = first.TryGetProperty("type", out typeElement) ? typeElement.GetString()! : string.Empty;
        switch (type)
        {
            case "reasoning":
            case "function_call":
            case "custom_tool_call":
                return true;
        }

        var role = first.TryGetProperty("role", out roleElement) ? roleElement.GetString()! : string.Empty;
        return role == "assistant";
    }

    /// <summary>Go: <c>payloadInputItems</c>; only an array yields items.</summary>
    internal static (List<JsonElement> Items, bool Ok) PayloadInputItems(
        Dictionary<string, JsonElement?> payload)
    {
        if (!payload.TryGetValue("input", out var inputValue)
            || inputValue is not { } element
            || element.ValueKind != JsonValueKind.Array)
        {
            return ([], false);
        }

        var items = new List<JsonElement>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            items.Add(item.Clone());
        }

        return (items, true);
    }

    /// <summary>Go: <c>inputSuffix</c>.</summary>
    internal static (List<JsonElement> Suffix, bool Ok) InputSuffix(
        List<JsonElement> previous,
        List<JsonElement> current)
    {
        if (previous.Count >= current.Count)
        {
            return ([], false);
        }

        for (var i = 0; i < previous.Count; i++)
        {
            if (!ResponsesOutboundStream.EqualJsonValues(
                previous[i].GetRawText(),
                current[i].GetRawText()))
            {
                return ([], false);
            }
        }

        return (CloneItems(current.GetRange(previous.Count, current.Count - previous.Count)), true);
    }

    internal static List<JsonElement> CloneItems(List<JsonElement> source)
    {
        var clone = new List<JsonElement>(source.Count);
        foreach (var item in source)
        {
            clone.Add(item.Clone());
        }

        return clone;
    }

    /// <summary>Go: <c>rawMessagesSize</c>.</summary>
    internal static int RawMessagesSize(List<JsonElement> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += Encoding.UTF8.GetByteCount(message.GetRawText());
        }

        return total;
    }

    /// <summary>Go: <c>buildWebSocketCreatePayload</c>.</summary>
    internal static Dictionary<string, JsonElement?> BuildWebSocketCreatePayload(byte[]? body)
    {
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        if (body is { Length: > 0 })
        {
            try
            {
                payload = LlmJson.Deserialize<Dictionary<string, JsonElement?>>(body)
                    ?? new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to decode responses request body: "
                    + OpenAiInboundTransformer.JsonErrorText(ex),
                    ex);
            }
        }

        payload["type"] = JsonSerializer.SerializeToElement("response.create");
        payload.Remove("stream");
        payload.Remove("background");

        return payload;
    }

    /// <summary>Go: <c>applyWebSocketAuth</c>.</summary>
    internal static void ApplyWebSocketAuth(
        Dictionary<string, List<string>> headers,
        AuthConfig auth)
    {
        switch (auth.Type)
        {
            case AuthTypes.Bearer:
                HttpClientUtils.HeaderSet(headers, "Authorization", "Bearer " + auth.ApiKey);
                break;
            case AuthTypes.ApiKey:
                if (string.IsNullOrEmpty(auth.HeaderKey))
                {
                    throw new InvalidOperationException("api key header is required");
                }

                HttpClientUtils.HeaderSet(
                    headers,
                    auth.HeaderKey,
                    auth.ApiKey ?? string.Empty);
                break;
            default:
                throw new InvalidOperationException(
                    "unsupported auth type \"" + auth.Type + "\"");
        }
    }

    /// <summary>Go: <c>normalizeWebSocketEvent</c>.</summary>
    internal static byte[] NormalizeWebSocketEvent(byte[] raw)
    {
        Dictionary<string, JsonElement?>? payload;
        try
        {
            payload = LlmJson.Deserialize<Dictionary<string, JsonElement?>>(raw);
        }
        catch (JsonException)
        {
            return [.. raw];
        }

        if (payload is null)
        {
            return [.. raw];
        }

        if (!payload.TryGetValue("type", out var typeValue)
            || typeValue is not { ValueKind: JsonValueKind.String }
            || typeValue.Value.GetString() != "error")
        {
            return [.. raw];
        }

        if (!payload.TryGetValue("error", out var errorValue)
            || errorValue is not { ValueKind: JsonValueKind.Object })
        {
            return [.. raw];
        }

        var errorObject = errorValue.Value;
        if (JsonValueIsEmpty(payload, "code"))
        {
            if (errorObject.TryGetProperty("code", out var nestedCode))
            {
                payload["code"] = nestedCode.Clone();
            }
            else if (errorObject.TryGetProperty("type", out var nestedType))
            {
                payload["code"] = nestedType.Clone();
            }
        }

        foreach (var key in new[] { "message", "param" })
        {
            if (JsonValueIsEmpty(payload, key)
                && errorObject.TryGetProperty(key, out var nested))
            {
                payload[key] = nested.Clone();
            }
        }

        return GoJson.Marshal(payload);
    }

    private static bool JsonValueIsEmpty(Dictionary<string, JsonElement?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return true;
        }

        return value.Value.ValueKind == JsonValueKind.String
            && value.Value.GetString()!.Length == 0;
    }

    internal static string StreamEventType(byte[] raw)
    {
        try
        {
            return LlmJson.Deserialize<WebSocketEventType>(raw)?.Type ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    internal static string ResponseIdFromWebSocketEvent(byte[] raw)
    {
        try
        {
            return LlmJson.Deserialize<WebSocketResponseIdEvent>(raw)?.Response?.Id ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    internal static List<JsonElement> ResponseOutputFromWebSocketEvent(byte[] raw)
    {
        try
        {
            var evt = LlmJson.Deserialize<WebSocketResponseOutputEvent>(raw);
            var output = evt?.Response?.Output;
            return output is null ? [] : CloneItems(output);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static bool IsTerminalWebSocketEvent(string eventType)
        => eventType is "response.completed"
            or "response.failed"
            or "response.cancelled"
            or "response.incomplete"
            or "error";

    internal static bool TerminalWebSocketEventEvicts(string eventType)
        => eventType is "response.failed"
            or "response.cancelled"
            or "response.incomplete"
            or "error";

    private static Dictionary<string, List<string>> CloneHeaders(
        Dictionary<string, List<string>>? headers)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (headers is null)
        {
            return clone;
        }

        foreach (var (key, values) in headers)
        {
            clone[key] = [.. values];
        }

        return clone;
    }

    private static HashSet<string> CanonicalHeaderSet(params string[] keys)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            set.Add(HttpClientUtils.CanonicalHeaderKey(key));
        }

        return set;
    }

    private sealed class WebSocketErrorEvent
    {
        [JsonPropertyName("status")]
        public int? Status { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("param")]
        public string? Param { get; set; }

        [JsonPropertyName("error")]
        public JsonElement? Error { get; set; }
    }

    private sealed class WebSocketErrorEnvelope
    {
        [JsonPropertyName("error")]
        public Dictionary<string, JsonElement?>? Error { get; set; }
    }

    private sealed class WebSocketEventType
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }
    }

    private sealed class WebSocketResponseIdEvent
    {
        [JsonPropertyName("response")]
        public WebSocketResponseId? Response { get; set; }
    }

    private sealed class WebSocketResponseId
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class WebSocketResponseOutputEvent
    {
        [JsonPropertyName("response")]
        public WebSocketResponseOutput? Response { get; set; }
    }

    private sealed class WebSocketResponseOutput
    {
        [JsonPropertyName("output")]
        public List<JsonElement>? Output { get; set; }
    }
}

/// <summary>Go: <c>webSocketPoolKey</c>.</summary>
internal readonly record struct WebSocketPoolKey
{
    /// <summary>
    /// Explicit parameterless constructor: field initializers require one
    /// (CS8983), and the defaults keep every key field non-null like Go's
    /// zero-valued string fields.
    /// </summary>
    public WebSocketPoolKey()
    {
    }

    /// <summary>
    /// Go: the <c>ChannelID</c> key field (websocket_executor.go:314, 568).
    /// Zero when the executor was built without a channel (tests, wrappers that
    /// do not carry one).
    /// </summary>
    public long ChannelId { get; init; }

    public string Url { get; init; } = string.Empty;

    public string SessionId { get; init; } = string.Empty;

    public string Scope { get; init; } = string.Empty;

    public string Auth { get; init; } = string.Empty;

    public string AccountId { get; init; } = string.Empty;

    public string Originator { get; init; } = string.Empty;

    public string UserAgent { get; init; } = string.Empty;

    public string Org { get; init; } = string.Empty;

    public string Project { get; init; } = string.Empty;

    public string BetaHeader { get; init; } = string.Empty;

    public string Headers { get; init; } = string.Empty;
}

/// <summary>Go: <c>pooledWebSocketConn</c>.</summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The semaphore models Go's buffered in-flight channel, which has no Dispose; "
        + "Close() is the reference teardown and releases the connection.")]
internal sealed class PooledWebSocketConnection
{
    private readonly object _mu = new();
    private bool _closed;
    private List<JsonElement> _lastInput = [];
    private string _lastResponseId = string.Empty;
    private int _retainedBytes;

    public PooledWebSocketConnection(WebSocketPoolKey key, WebSocket? conn, DateTime now)
    {
        Key = key;
        Conn = conn;
        CreatedAt = now;
        LastUsedAt = now;
    }

    public WebSocketPoolKey Key { get; }

    /// <summary>
    /// Go: <c>pooledWebSocketConn.budget</c> — the executor's shared 64 MiB
    /// retained-input budget, attached when the connection enters the pool.
    /// </summary>
    public ResponsesRetainedBudget? Budget { get; set; }

    public WebSocket? Conn { get; }

    public SemaphoreSlim InFlight { get; } = new(1, 1);

    public DateTime CreatedAt { get; }

    public DateTime LastUsedAt { get; private set; }

    public bool Used { get; private set; }

    /// <summary>Go: <c>len(pc.inFlight)</c>.</summary>
    public int InFlightCount => InFlight.CurrentCount == 0 ? 1 : 0;

    public (bool Closed, bool Expired, bool Used) LeaseState(DateTime now, TimeSpan maxLifetime)
    {
        lock (_mu)
        {
            return (
                _closed,
                maxLifetime > TimeSpan.Zero && now - CreatedAt > maxLifetime,
                Used);
        }
    }

    public (bool Closed, DateTime LastUsedAt, bool Expired) PoolState(
        DateTime now,
        TimeSpan maxLifetime)
    {
        lock (_mu)
        {
            return (
                _closed,
                LastUsedAt,
                maxLifetime > TimeSpan.Zero && now - CreatedAt > maxLifetime);
        }
    }

    public DateTime PoolLastUsedAt()
    {
        lock (_mu)
        {
            return LastUsedAt;
        }
    }

    public bool IsClosed()
    {
        lock (_mu)
        {
            return _closed;
        }
    }

    public void MarkUsed()
    {
        lock (_mu)
        {
            LastUsedAt = DateTime.UtcNow;
            Used = true;
        }
    }

    public void Close()
    {
        lock (_mu)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Budget?.Replace(_retainedBytes, 0);
            _retainedBytes = 0;
            _lastInput = [];
            _lastResponseId = string.Empty;
        }

        Conn?.Dispose();
    }

    public (List<JsonElement> Input, string ResponseId) PreviousTurn()
    {
        lock (_mu)
        {
            return (WebSocketExecutor.CloneItems(_lastInput), _lastResponseId);
        }
    }

    /// <summary>
    /// Go: <c>pooledWebSocketConn.rememberTurn</c>
    /// (websocket_retention.go:8-29). The response id is remembered even when
    /// the input is dropped for exceeding the per-connection or shared budget;
    /// the next turn then sends full context but still agrees on the last id.
    /// </summary>
    public bool RememberTurn(List<JsonElement> input, string responseId, int maxInputBytes)
    {
        if (responseId.Trim().Length == 0)
        {
            return false;
        }

        lock (_mu)
        {
            if (_closed)
            {
                return false;
            }

            _lastResponseId = responseId.Trim();

            var size = WebSocketExecutor.RawMessagesSize(input);
            if ((maxInputBytes > 0 && size > maxInputBytes)
                || (Budget is not null && !Budget.Replace(_retainedBytes, size)))
            {
                Budget?.Replace(_retainedBytes, 0);
                _lastInput = [];
                _retainedBytes = 0;
                return false;
            }

            _lastInput = WebSocketExecutor.CloneItems(input);
            _retainedBytes = size;
            return true;
        }
    }

    /// <summary>
    /// Drains the in-flight slot if this connection holds it (Go:
    /// <c>select { case &lt;-pc.inFlight: default: }</c>). A held token is a
    /// semaphore count of zero; releasing an already-free slot would exceed the
    /// semaphore's max count, which is why <see cref="SemaphoreFullException"/>
    /// is the port's <c>default</c> branch.
    /// </summary>
    public void ReleaseInFlight()
    {
        try
        {
            InFlight.Release();
        }
        catch (SemaphoreFullException)
        {
            // The slot was not held; Go's non-blocking receive took the
            // default branch here too.
        }
    }

    /// <summary>
    /// Test seam: the reference tests build the struct literal with a cached
    /// response id directly instead of replaying a turn.
    /// </summary>
    internal void SeedPreviousTurn(List<JsonElement> input, string responseId)
    {
        lock (_mu)
        {
            _lastInput = WebSocketExecutor.CloneItems(input);
            _lastResponseId = responseId;
        }
    }
}

/// <summary>Go: <c>webSocketLease</c>.</summary>
internal sealed class WebSocketLease(
    WebSocket? conn,
    PooledWebSocketConnection? pooled,
    WebSocketExecutor? owner,
    bool reused)
{
    private int _released;

    public WebSocket? Conn { get; } = conn;

    public PooledWebSocketConnection? Pooled { get; } = pooled;

    public WebSocketExecutor? Owner { get; } = owner;

    public bool Reused { get; } = reused;

    public List<JsonElement> NextFullInput { get; set; } = [];

    /// <summary>Go: <c>webSocketLease.rememberTurn</c> (websocket_retention.go:31-47).</summary>
    public bool RememberTurn(string responseId, List<JsonElement> output)
    {
        if (Pooled is null)
        {
            return false;
        }

        var maxInputBytes = Owner?.MaxRetainedInput ?? 0;

        // Without a cached full input only the response id is remembered; the
        // next turn then sends full context while still agreeing on the last
        // response id.
        if (NextFullInput.Count == 0)
        {
            return Pooled.RememberTurn([], responseId, maxInputBytes);
        }

        if (maxInputBytes > 0
            && WebSocketExecutor.RawMessagesSize(NextFullInput)
                + WebSocketExecutor.RawMessagesSize(output) > maxInputBytes)
        {
            return Pooled.RememberTurn([], responseId, maxInputBytes);
        }

        var input = new List<JsonElement>(NextFullInput.Count + output.Count);
        input.AddRange(NextFullInput);
        input.AddRange(output);
        return Pooled.RememberTurn(input, responseId, maxInputBytes);
    }

    public void Release(bool evict)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        if (Pooled is null)
        {
            Conn?.Dispose();
            return;
        }

        if (Owner is not null)
        {
            Owner.ReleaseLeaseToPool(Pooled, evict);
        }
        else if (evict)
        {
            Pooled.Close();
        }
        else
        {
            Pooled.MarkUsed();
        }

        Pooled.ReleaseInFlight();
    }
}

/// <summary>Go: <c>webSocketStream</c>.</summary>
internal sealed class WebSocketStream : IStream<StreamEvent>
{
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly CancellationToken _ct;
    private readonly WebSocketLease? _lease;
    private readonly CancellationTokenRegistration? _registration;
    private readonly byte[] _buffer = new byte[ReceiveBufferSize];
    private readonly object _mu = new();
    private StreamEvent? _current;
    private Exception? _err;
    private bool _closed;
    private bool _sawEvent;
    private bool _terminal;
    private int _finished;

    public WebSocketStream(CancellationToken cancellationToken, WebSocketLease? lease)
    {
        _ct = cancellationToken;
        _lease = lease;

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(
                static state => ((WebSocketStream)state!).Finish(evict: true),
                this);
            _registration = registration;

            // Register invokes the callback synchronously when the token is
            // already cancelled; drop the registration once the stream is done.
            if (cancellationToken.IsCancellationRequested)
            {
                registration.Unregister();
            }
        }
    }

    public StreamEvent? Current
    {
        get
        {
            lock (_mu)
            {
                return _current;
            }
        }
    }

    public Exception? Err
    {
        get
        {
            lock (_mu)
            {
                return _err;
            }
        }
    }

    /// <summary>Port of webSocketStream.Next.</summary>
    public bool Next()
    {
        if (ContextCancelled())
        {
            return false;
        }

        if (IsClosed())
        {
            return false;
        }

        if (_lease?.Conn is not { } connection)
        {
            SetErr(new InvalidOperationException("websocket connection is nil"));
            Finish(evict: true);
            return false;
        }

        Message message;
        try
        {
            message = ReceiveMessage(connection);
        }
        catch (Exception ex) when (ex is WebSocketException
            or IOException
            or OperationCanceledException
            or ObjectDisposedException)
        {
            HandleReadError(ex);
            return false;
        }

        if (message.IsClose)
        {
            HandleCloseFrame(message.CloseStatus, message.Description);
            return false;
        }

        var eventType = WebSocketExecutor.StreamEventType(message.Data);
        SetCurrent(new StreamEvent
        {
            Type = eventType,
            Data = WebSocketExecutor.NormalizeWebSocketEvent(message.Data),
        });

        if (WebSocketExecutor.IsTerminalWebSocketEvent(eventType))
        {
            MarkTerminal();

            // Only top-level `error` events are transport failures.
            // Response-level terminal events are yielded and then close the
            // stream normally so the non-streaming Do path can aggregate their
            // response object.
            var evict = WebSocketExecutor.TerminalWebSocketEventEvicts(eventType);
            if (eventType == "response.completed")
            {
                var responseId = WebSocketExecutor.ResponseIdFromWebSocketEvent(message.Data);
                if (responseId.Length == 0)
                {
                    evict = true;
                }
                else if (_lease?.Owner is { } owner && message.Data.Length > owner.MaxRetainedInput)
                {
                    // The completed event itself is larger than the retained
                    // input cap, so the conversation is not cached; the healthy
                    // connection is kept and the next turn sends full context
                    // (websocket_executor.go:1207-1213).
                    _lease.NextFullInput = [];
                    _lease.RememberTurn(responseId, []);
                }
                else
                {
                    // Retaining the full input is a local cache optimization
                    // for future incremental sends. If the input exceeds the
                    // memory budget, keep the healthy WebSocket connection and
                    // let the next turn send full context instead of evicting
                    // the session.
                    _lease?.RememberTurn(
                        responseId,
                        WebSocketExecutor.ResponseOutputFromWebSocketEvent(message.Data));
                }
            }

            Finish(evict);
        }

        return true;
    }

    public void Dispose() => Finish(evict: true);

    internal void Finish(bool evict)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
        {
            return;
        }

        lock (_mu)
        {
            _closed = true;
        }

        _lease?.Release(evict);
        _registration?.Unregister();
    }

    /// <summary>Test seam for the terminal-event cancellation case.</summary>
    internal void MarkTerminal()
    {
        lock (_mu)
        {
            _terminal = true;
        }
    }

    private bool ContextCancelled()
    {
        if (!_ct.IsCancellationRequested)
        {
            return false;
        }

        SetContextError();
        Finish(evict: true);
        return true;
    }

    private void SetContextError()
    {
        lock (_mu)
        {
            if (_err is null && !_terminal)
            {
                _err = new OperationCanceledException(_ct);
            }
        }
    }

    private bool IsClosed()
    {
        lock (_mu)
        {
            return _err is not null || _closed;
        }
    }

    private void SetCurrent(StreamEvent evt)
    {
        lock (_mu)
        {
            _current = evt;
            _sawEvent = true;
        }
    }

    private bool HasSeenEvent()
    {
        lock (_mu)
        {
            return _sawEvent;
        }
    }

    private void SetErr(Exception error)
    {
        lock (_mu)
        {
            _err = error;
        }
    }

    private void HandleReadError(Exception error)
    {
        // Go checks for gorilla's normal-closure error and for the
        // "use of closed network connection" text; .NET surfaces a local
        // teardown as an exception too, so the finished flag (or a requested
        // cancellation) identifies the same case.
        if (IsFinished() || _ct.IsCancellationRequested)
        {
            HandleNormalClose();
            return;
        }

        SetErr(error);
        Finish(evict: true);
    }

    private bool IsFinished() => Volatile.Read(ref _finished) != 0;

    private void HandleCloseFrame(WebSocketCloseStatus? status, string? description)
    {
        if (status == WebSocketCloseStatus.NormalClosure)
        {
            HandleNormalClose();
            return;
        }

        SetErr(new WebSocketException(CloseErrorText(status, description)));
        Finish(evict: true);
    }

    private void HandleNormalClose()
    {
        if (_ct.IsCancellationRequested)
        {
            SetErr(new OperationCanceledException(_ct));
        }
        else if (!HasSeenEvent())
        {
            SetErr(new InvalidOperationException("websocket closed before response event"));
        }

        Finish(evict: true);
    }

    private static string CloseErrorText(WebSocketCloseStatus? status, string? description)
    {
        var code = status is null ? 1005 : (int)status.Value;
        var name = status switch
        {
            WebSocketCloseStatus.EndpointUnavailable => " (going away)",
            WebSocketCloseStatus.ProtocolError => " (protocol error)",
            WebSocketCloseStatus.InvalidMessageType => " (unsupported data)",
            WebSocketCloseStatus.InvalidPayloadData => " (invalid payload data)",
            WebSocketCloseStatus.PolicyViolation => " (policy violation)",
            WebSocketCloseStatus.MessageTooBig => " (message too big)",
            WebSocketCloseStatus.MandatoryExtension => " (mandatory extension)",
            WebSocketCloseStatus.InternalServerError => " (internal server error)",
            _ => string.Empty,
        };

        var text = "websocket: close " + code.ToString(CultureInfo.InvariantCulture) + name;
        if (!string.IsNullOrEmpty(description))
        {
            text += ": " + description;
        }

        return text;
    }

    private Message ReceiveMessage(WebSocket connection)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = connection.ReceiveAsync(
                new ArraySegment<byte>(_buffer),
                _ct).GetAwaiter().GetResult();

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new Message(
                    IsClose: true,
                    result.CloseStatus,
                    result.CloseStatusDescription,
                    []);
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return new Message(
                    IsClose: false,
                    CloseStatus: null,
                    Description: null,
                    message.ToArray());
            }
        }
    }

    private sealed record Message(
        bool IsClose,
        WebSocketCloseStatus? CloseStatus,
        string? Description,
        byte[] Data);
}

/// <summary>Port of <c>shared.ResponsesBudget</c> (responses_lane.go:8-24).</summary>
internal sealed class ResponsesRetainedBudget(long limit)
{
    private readonly object _gate = new();
    private long _used;

    /// <summary>
    /// Reserves <paramref name="next"/> bytes in place of
    /// <paramref name="previous"/>; false when the replacement would exceed the
    /// limit, leaving the reservation untouched.
    /// </summary>
    public bool Replace(int previous, int next)
    {
        lock (_gate)
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

/// <summary>
/// Process-wide cache of Responses WebSocket executors, keyed by the inner
/// executor and the channel.
/// </summary>
/// <remarks>
/// The reference keeps one outbound transformer — and with it one
/// <c>WebSocketExecutor</c> and its connection pool — per enabled channel in its
/// channel cache (internal/server/biz/channel.go), so a later turn in the same
/// session reuses the upstream WebSocket and its connection-local conversation
/// state. The port builds one transformer per attempt
/// (<c>ChannelOutboundFactory</c>), and the orchestrator stops that transformer
/// when the attempt ends; caching the executors here gives the pool the
/// reference's lifetime instead of the attempt's (C072). The reference closes
/// executors when the channel cache swaps; the port has no swap event for
/// per-attempt outbounds, so pooled connections are retired by the executor's
/// idle TTL / max lifetime and its background cleanup instead.
/// </remarks>
internal static class WebSocketExecutorCache
{
    private static readonly object Gate = new();

    private static readonly Dictionary<(IExecutor Inner, long ChannelId), WebSocketExecutor>
        Executors = [];

    public static WebSocketExecutor GetOrCreate(IExecutor inner, long channelId)
    {
        lock (Gate)
        {
            var key = (inner, channelId);
            if (Executors.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var executor = new WebSocketExecutor(inner) { ChannelId = channelId };
            Executors[key] = executor;
            return executor;
        }
    }
}
