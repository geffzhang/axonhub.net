using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of <c>serveResponsesWebSocket</c>
/// (internal/server/api/responses_websocket.go): the downstream Responses
/// WebSocket mode mounted at <c>GET /v1/responses</c> and
/// <c>GET /codex/responses</c>. Each text frame is a <c>response.create</c>
/// event processed through the chat orchestrator; results stream back as
/// <c>response.*</c> events on the same socket.
/// </summary>
/// <remarks>
/// <para>
/// The reference serializes frames per <c>stream_id</c> lane, keeps at most 32
/// named lanes, 64 pending frames and 128 MiB of queued frames, and retains
/// warmup (<c>generate=false</c>) payloads against a connection-wide 64 MiB
/// budget. The port reproduces the same accounting with in-process counters.
/// </para>
/// <para>Known differences (registered with C244):</para>
/// <list type="bullet">
/// <item>The reference sends protocol-level pings every 2.5 minutes and
/// refreshes a 5 minute read deadline on pongs; <see cref="WebSocket"/> cannot
/// emit ping frames or observe pongs, so ASP.NET Core's keep-alive (pings on
/// <see cref="ResponsesWebSocketPingInterval"/>, abort after the keep-alive
/// timeout) stands in.</item>
/// <item>The reference applies <c>LLMRequestTimeout</c> per event and carries a
/// <c>shared.ResponsesLane</c> lease for Responses continuation; the port has
/// neither a request-timeout setting (C115) nor the continuation subsystem, so
/// events run until the connection closes and <c>previous_response_id</c>
/// warmups merge locally.</item>
/// <item><c>writeStreamDelivery</c>'s request-chain delivery boundary is not
/// ported (request-chain persistence records directly), matching the rest of
/// the port's stream paths.</item>
/// </list>
/// </remarks>
public static class ResponsesWebSocketEndpoint
{
    /// <summary>responseCreateWebSocketEventType.</summary>
    public const string ResponseCreateEventType = "response.create";

    /// <summary>responsesWebSocketMaxMessageSize: 128 MiB.</summary>
    public const int MaxMessageSize = 128 << 20;

    /// <summary>responsesWebSocketPingInterval: the reference's idle timeout / 2.</summary>
    public static readonly TimeSpan ResponsesWebSocketPingInterval = TimeSpan.FromMinutes(2.5);

    private const int MaxActive = 16;
    private const int MaxPending = 64;
    private const int MaxNamedStreams = 32;
    private const int QueueBudgetLimit = 128 << 20;
    private const int RetainedBudgetLimit = 64 << 20;
    private const int MaxContextBytes = 64 << 20;

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Port of <c>serveResponsesWebSocket</c>.</summary>
    public static Task HandleAsync(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        TimeSpan requestTimeout = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(orchestrator);

        return HandleAsync(
            context,
            (request, identity, cancellationToken) => orchestrator.ProcessAsync(
                request,
                identity,
                onRequestCreated: null,
                cancellationToken),
            orchestrator.TransformErrorAsync,
            requestTimeout);
    }

    /// <summary>
    /// The reference takes the processing and error-transform functions as
    /// parameters (<c>responsesWebSocketProcessFunc</c> /
    /// <c>responsesWebSocketErrorFunc</c>); the port keeps the same seam for
    /// wire-shape tests.
    /// </summary>
    internal static async Task HandleAsync(
        HttpContext context,
        Func<HttpRequestData, RequestIdentity, CancellationToken, Task<ChatCompletionResult>> process,
        Func<Exception, CancellationToken, Task<HttpResponseData>> transformError,
        TimeSpan requestTimeout = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(transformError);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "websocket upgrade required",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        // shared.WithSessionID: present when the trace middleware resolved one,
        // otherwise a fresh connection id.
        var sessionId = TraceContext.GetSessionId(context)
            ?? "responses-ws-" + Guid.NewGuid().ToString();

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted);
        var sessionToken = sessionCts.Token;

        WebSocket socket;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference logs the failed upgrade and stops; the connection
            // is unusable either way.
            return;
        }

        try
        {
            using var connection = new Connection(
                context,
                socket,
                process,
                transformError,
                sessionId,
                sessionCts,
                requestTimeout);

            await connection.RunAsync(sessionToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                socket.Dispose();
            }
            catch (Exception)
            {
                // Disposing a broken socket is best-effort.
            }
        }
    }

    /// <summary>Per-connection state: lanes, budgets and the dispatcher.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly WebSocket _socket;
        private readonly Func<HttpRequestData, RequestIdentity, CancellationToken, Task<ChatCompletionResult>> _process;
        private readonly Func<Exception, CancellationToken, Task<HttpResponseData>> _transformError;
        private readonly CancellationTokenSource _sessionCts;
        private readonly TimeSpan _requestTimeout;
        private readonly RequestInfo _request;
        private readonly RequestIdentity _identity;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly SemaphoreSlim _active = new(MaxActive, MaxActive);
        private readonly object _stateLock = new();
        private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
        private readonly List<Task> _tasks = [];
        private readonly Budget _queueBudget = new(QueueBudgetLimit);
        private readonly Budget _retainedBudget = new(RetainedBudgetLimit);

        private int _pending;
        private int _namedStreams;

        public Connection(
            HttpContext context,
            WebSocket socket,
            Func<HttpRequestData, RequestIdentity, CancellationToken, Task<ChatCompletionResult>> process,
            Func<Exception, CancellationToken, Task<HttpResponseData>> transformError,
            string sessionId,
            CancellationTokenSource sessionCts,
            TimeSpan requestTimeout)
        {
            _socket = socket;
            _process = process;
            _transformError = transformError;
            _sessionCts = sessionCts;
            _requestTimeout = requestTimeout;
            _identity = RequestIdentity.FromHttpContext(context);

            var headers = HttpRequestReader.CanonicalizeHeaders(context.Request.Headers);
            _request = new RequestInfo(
                context.Request.Scheme + "://"
                    + context.Request.Host.ToUriComponent()
                    + context.Request.PathBase
                    + context.Request.Path
                    + context.Request.QueryString,
                context.Request.PathBase + context.Request.Path,
                ToMultiMap(context.Request.Query),
                headers,
                ClientIp(headers, context.Connection.RemoteIpAddress?.ToString() ?? string.Empty),
                HttpClientUtils.HeaderGet(headers, "User-Agent"),
                sessionId,
                SessionScope(_identity));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];

            while (!cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result;
                var message = new MemoryStream();
                try
                {
                    do
                    {
                        result = await _socket
                            .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                            .ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await TryReplyCloseAsync(cancellationToken).ConfigureAwait(false);
                            await WaitAsync().ConfigureAwait(false);
                            return;
                        }

                        message.Write(buffer, 0, result.Count);
                        if (message.Length > MaxMessageSize)
                        {
                            // gorilla's SetReadLimit closes with CloseMessageTooBig.
                            await TryCloseAsync().ConfigureAwait(false);
                            await WaitAsync().ConfigureAwait(false);
                            return;
                        }
                    }
                    while (!result.EndOfMessage);
                }
                catch (Exception)
                {
                    await WaitAsync().ConfigureAwait(false);
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Text)
                {
                    if (!await WriteErrorAsync(
                            InvalidRequest(
                                "response.create must be sent as a text message",
                                string.Empty),
                            string.Empty)
                        .ConfigureAwait(false))
                    {
                        await WaitAsync().ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                var frame = message.ToArray();
                var (streamId, envelopeError) = EnvelopeStreamId(frame);
                if (envelopeError is not null)
                {
                    if (!await WriteErrorAsync(envelopeError, string.Empty).ConfigureAwait(false))
                    {
                        await WaitAsync().ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                var (lane, laneError) = GetLane(streamId);
                if (laneError is not null)
                {
                    if (!await WriteErrorAsync(laneError, streamId).ConfigureAwait(false))
                    {
                        await WaitAsync().ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                if (!Reserve(frame.Length))
                {
                    if (!await WriteErrorAsync(QueueLimitError(), streamId).ConfigureAwait(false))
                    {
                        await WaitAsync().ConfigureAwait(false);
                        return;
                    }

                    continue;
                }

                Dispatch(lane!, streamId, frame);
            }

            await WaitAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _writeLock.Dispose();
            _active.Dispose();
        }

        private async Task WaitAsync()
        {
            _sessionCts.Cancel();

            Task[] tasks;
            lock (_stateLock)
            {
                tasks = [.. _tasks];
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Every task handles its own failures; this only joins them.
            }

            // dispatcher.wait(): close every lane owner and release warmup.
            lock (_stateLock)
            {
                foreach (var lane in _lanes.Values)
                {
                    lane.Session.ReleaseWarmup();
                }
            }
        }

        private void Dispatch(Lane lane, string streamId, byte[] frame)
        {
            var previous = lane.Tail;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lane.Tail = done.Task;

            var task = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        await previous.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        await _active.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        if (!await ProcessMessageAsync(lane, streamId, frame).ConfigureAwait(false))
                        {
                            Abort();
                        }
                    }
                    finally
                    {
                        _active.Release();
                    }
                }
                catch (Exception)
                {
                    Abort();
                }
                finally
                {
                    // The reference releases both slots only when the goroutine
                    // finishes (defer), not when it starts processing.
                    lock (_stateLock)
                    {
                        _pending--;
                    }

                    _queueBudget.Replace(frame.Length, 0);
                    done.TrySetResult();
                }
            });

            lock (_stateLock)
            {
                _tasks.Add(task);
            }
        }

        /// <summary>Returns false when the connection must close.</summary>
        private async Task<bool> ProcessMessageAsync(Lane lane, string streamId, byte[] frame)
        {
            var prepared = lane.Session.PrepareRequest(_request, frame);

            // defer lane.session.releaseConsumedWarmup().
            try
            {
                if (prepared.Error is { } error)
                {
                    return await WriteErrorAsync(error, streamId).ConfigureAwait(false);
                }

                if (prepared.Warmup is { } warmup)
                {
                    return await WriteWarmupAsync(warmup, streamId).ConfigureAwait(false);
                }

                var request = prepared.Request!;

                // responses_websocket.go:267-283: the per-event deadline covers
                // both processing and writing the result, not the whole socket.
                using var requestCts = _requestTimeout > TimeSpan.Zero
                    ? CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token)
                    : null;
                if (requestCts is not null)
                {
                    requestCts.CancelAfter(_requestTimeout);
                }

                var requestToken = requestCts?.Token ?? _sessionCts.Token;

                ChatCompletionResult result;
                try
                {
                    result = await _process(request, _identity, requestToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return await WriteTransformedErrorAsync(ex, streamId, requestToken)
                        .ConfigureAwait(false);
                }

                return await WriteResultAsync(request, result, streamId, requestToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                lane.Session.ReleaseConsumedWarmup();
            }
        }

        private async Task<bool> WriteResultAsync(
            HttpRequestData request,
            ChatCompletionResult result,
            string streamId,
            CancellationToken requestToken)
        {
            if (result.Stream is { } stream)
            {
                using (stream)
                {
                    var terminalSeen = false;
                    var tracker = new StreamTerminalTracker(
                        ExpectedStreamChoices.FromRequestBody(request.Body));
                    while (stream.Next())
                    {
                        if (requestToken.IsCancellationRequested)
                        {
                            if (_sessionCts.IsCancellationRequested)
                            {
                                // The connection is gone; like the reference's
                                // ctx.Err() == context.Canceled branch there is
                                // nothing left to report.
                                return true;
                            }

                            // The per-event deadline fired. The reference lets
                            // the context-bound stream end and reports its
                            // error below.
                            break;
                        }

                        var streamEvent = stream.Current;
                        if (streamEvent is null || streamEvent.Data.Length == 0)
                        {
                            continue;
                        }

                        byte[] data;
                        try
                        {
                            data = WithStreamId(streamEvent.Data, streamId);
                        }
                        catch (Exception)
                        {
                            return false;
                        }

                        var eventType = DataString(streamEvent.Data, "type");
                        var status = DataString(streamEvent.Data, "response", "status");

                        if (!await WriteMessageAsync(data).ConfigureAwait(false))
                        {
                            return false;
                        }

                        var terminal = !terminalSeen && tracker.Observe(streamEvent);
                        if (terminal)
                        {
                            terminalSeen = true;
                        }

                        // writeStreamDelivery's stop signal: either type may
                        // carry the completion marker, but both must be empty
                        // or response.completed and the status must agree.
                        if (terminal
                            && (string.Equals(streamEvent.Type, "response.completed", StringComparison.Ordinal)
                                || string.Equals(eventType, "response.completed", StringComparison.Ordinal))
                            && (streamEvent.Type.Length == 0
                                || string.Equals(streamEvent.Type, "response.completed", StringComparison.Ordinal))
                            && (eventType.Length == 0
                                || string.Equals(eventType, "response.completed", StringComparison.Ordinal))
                            && (status.Length == 0
                                || string.Equals(status, "completed", StringComparison.Ordinal)))
                        {
                            break;
                        }
                    }

                    if (stream.Err is { } streamError && !terminalSeen)
                    {
                        return await WriteTransformedErrorAsync(streamError, streamId, requestToken)
                            .ConfigureAwait(false);
                    }

                    return true;
                }
            }

            if (result.Response is { } response)
            {
                var body = response.Body ?? [];
                if (!IsValidJson(body))
                {
                    return await WriteErrorAsync(
                            InvalidRequest("invalid Responses result", string.Empty),
                            streamId)
                        .ConfigureAwait(false);
                }

                var completed = JsonObject(
                    ("response", AxonHub.Llm.Json.GoJson.Compact(body)),
                    ("sequence_number", Number(0)),
                    ("type", String("response.completed")));
                return await WriteEventAsync(completed, streamId).ConfigureAwait(false);
            }

            return await WriteErrorAsync(
                    InvalidRequest("Responses request returned no result", string.Empty),
                    streamId)
                .ConfigureAwait(false);
        }

        private async Task<bool> WriteWarmupAsync(Warmup warmup, string streamId)
        {
            var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            (string Name, byte[] Raw)[] previous = warmup.PreviousResponseId.Length > 0
                ? [("previous_response_id", String(warmup.PreviousResponseId))]
                : [];

            var inProgress = JsonObject(
                [
                    ("created_at", Number(createdAt)),
                    ("id", String(warmup.Id)),
                    ("model", String(warmup.Model)),
                    ("object", String("response")),
                    ("output", EmptyArray()),
                    .. previous,
                    ("status", String("in_progress")),
                ]);
            var completed = JsonObject(
                [
                    ("created_at", Number(createdAt)),
                    ("id", String(warmup.Id)),
                    ("model", String(warmup.Model)),
                    ("object", String("response")),
                    ("output", EmptyArray()),
                    .. previous,
                    ("status", String("completed")),
                ]);

            var createdEvent = JsonObject(
                ("response", inProgress),
                ("sequence_number", Number(0)),
                ("type", String("response.created")));
            if (!await WriteEventAsync(createdEvent, streamId).ConfigureAwait(false))
            {
                return false;
            }

            var inProgressEvent = JsonObject(
                ("response", inProgress),
                ("sequence_number", Number(1)),
                ("type", String("response.in_progress")));
            if (!await WriteEventAsync(inProgressEvent, streamId).ConfigureAwait(false))
            {
                return false;
            }

            var completedEvent = JsonObject(
                ("response", completed),
                ("sequence_number", Number(2)),
                ("type", String("response.completed")));
            return await WriteEventAsync(completedEvent, streamId).ConfigureAwait(false);
        }

        private async Task<bool> WriteTransformedErrorAsync(
            Exception error,
            string streamId,
            CancellationToken? requestToken = null)
        {
            HttpResponseData? transformed = null;
            try
            {
                transformed = await _transformError(error, requestToken ?? _sessionCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // transformResponsesWebSocketError falls back to the fixed 500
                // envelope when the transformer produces nothing.
            }

            if (transformed is not null)
            {
                return await WriteErrorAsync(transformed, streamId).ConfigureAwait(false);
            }

            // transformResponsesWebSocketError's fallback body.
            return await WriteErrorEventAsync(
                    StatusCodes.Status500InternalServerError,
                    JsonObject(
                        ("message", String("internal server error")),
                        ("type", String("server_error"))),
                    streamId)
                .ConfigureAwait(false);
        }

        /// <summary>Port of writeResponsesWebSocketError for an httpclient.Error.</summary>
        private async Task<bool> WriteErrorAsync(HttpResponseData? httpError, string streamId)
        {
            var status = httpError is { StatusCode: not 0 } ? httpError.StatusCode : 500;

            var detail = JsonObject(
                ("message", String(ReasonPhrases.GetReasonPhrase(status))),
                ("type", String("server_error")));
            if (httpError is { Body.Length: > 0 } && TryExtractErrorDetail(httpError.Body!, out var extracted))
            {
                detail = extracted;
            }

            return await WriteErrorEventAsync(status, detail, streamId).ConfigureAwait(false);
        }

        /// <summary>Write a request-level error (invalidResponsesWebSocketRequest).</summary>
        private Task<bool> WriteErrorAsync(ResponseError httpError, string streamId)
            => WriteErrorEventAsync(httpError.StatusCode, httpError.Detail, streamId);

        private async Task<bool> WriteErrorEventAsync(int status, byte[] detail, string streamId)
        {
            var envelope = JsonObject(
                ("error", detail),
                ("status", Number(status)),
                ("type", String("error")));
            return await WriteEventAsync(envelope, streamId).ConfigureAwait(false);
        }

        private async Task<bool> WriteEventAsync(byte[] envelope, string streamId)
        {
            byte[] data;
            try
            {
                data = WithStreamId(envelope, streamId);
            }
            catch (Exception)
            {
                return false;
            }

            return await WriteMessageAsync(data).ConfigureAwait(false);
        }

        private async Task<bool> WriteMessageAsync(byte[] data)
        {
            try
            {
                await _writeLock.WaitAsync(_sessionCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    _sessionCts.Token);
                timeout.CancelAfter(WriteTimeout);
                await _socket
                    .SendAsync(
                        new ArraySegment<byte>(data),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        timeout.Token)
                    .ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private (Lane? Lane, ResponseError? Error) GetLane(string streamId)
        {
            lock (_stateLock)
            {
                if (_lanes.TryGetValue(streamId, out var existing))
                {
                    return (existing, null);
                }

                if (streamId.Length > 0 && _namedStreams >= MaxNamedStreams)
                {
                    return (null, RequestError(
                        "This WebSocket connection has reached its maximum number of distinct stream IDs (32). Reuse an existing stream_id or open a new WebSocket connection.",
                        "stream_id",
                        "websocket_stream_limit_reached"));
                }

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ready.TrySetResult();
                var lane = new Lane(ready.Task, _retainedBudget);
                _lanes[streamId] = lane;
                if (streamId.Length > 0)
                {
                    _namedStreams++;
                }

                return (lane, null);
            }
        }

        private bool Reserve(int size)
        {
            if (!_queueBudget.Replace(0, size))
            {
                return false;
            }

            lock (_stateLock)
            {
                if (_pending >= MaxPending)
                {
                    _queueBudget.Replace(size, 0);
                    return false;
                }

                _pending++;
                return true;
            }
        }

        private void Abort()
        {
            try
            {
                _sessionCts.Cancel();
            }
            catch (Exception)
            {
                // Already cancelled.
            }

            try
            {
                // gorilla's Conn.Close closes the transport without a close frame.
                _socket.Abort();
            }
            catch (Exception)
            {
                // Abort is best-effort.
            }
        }

        private async Task TryCloseAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(WriteTimeout);
                await _socket
                    .CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, string.Empty, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection is going away either way.
            }
        }

        private async Task TryReplyCloseAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(WriteTimeout);
                await _socket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The peer may already be gone.
            }
        }
    }

    /// <summary>The inbound request shape every prepared message clones.</summary>
    private sealed record RequestInfo(
        string Url,
        string Path,
        Dictionary<string, List<string>> Query,
        Dictionary<string, List<string>> Headers,
        string ClientIp,
        string UserAgent,
        string SessionId,
        string? SessionScope);

    /// <summary>responsesWebSocketLane: one serialized tail per stream id.</summary>
    private sealed class Lane(Task ready, Budget retainedBudget)
    {
        public Session Session { get; } = new(retainedBudget);

        public Task Tail { get; set; } = ready;
    }

    /// <summary>responsesWebSocketWarmup.</summary>
    private sealed record Warmup(string Id, string Model, string PreviousResponseId);

    private sealed class PreparedRequest
    {
        public HttpRequestData? Request { get; init; }

        public Warmup? Warmup { get; init; }

        public ResponseError? Error { get; init; }
    }

    private sealed record ResponseError(int StatusCode, byte[] Detail);

    /// <summary>Port of <c>shared.ResponsesBudget</c> (responses_lane.go:8-24).</summary>
    private sealed class Budget(int limit)
    {
        private readonly object _gate = new();
        private long _used;

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
    /// Port of responsesWebSocketSession: the warmup payload retained for the
    /// next <c>previous_response_id</c> continuation. The reference also binds a
    /// <c>shared.ResponsesLane</c> owner to the lane for the server-side
    /// Responses continuation; that subsystem is not ported (C244), so only the
    /// retained budget carries over.
    /// </summary>
    private sealed class Session(Budget budget)
    {
        private string _warmupId = string.Empty;
        private Dictionary<string, byte[]>? _warmupPayload;
        private int _warmupBytes;

        public void ReleaseWarmup()
        {
            budget.Replace(_warmupBytes, 0);
            _warmupBytes = 0;
            _warmupId = string.Empty;
            _warmupPayload = null;
        }

        /// <summary>Port of responsesWebSocketSession.releaseConsumedWarmup.</summary>
        public void ReleaseConsumedWarmup()
        {
            if (_warmupId.Length == 0)
            {
                ReleaseWarmup();
            }
        }

        /// <summary>Port of responsesWebSocketSession.prepareRequest.</summary>
        public PreparedRequest PrepareRequest(RequestInfo info, byte[] message)
        {
            var payload = ParsePayload(message);
            if (payload is null)
            {
                return new PreparedRequest
                {
                    Error = InvalidRequest("invalid response.create JSON payload", string.Empty),
                };
            }

            if (!payload.TryGetValue("type", out var rawType)
                || !TryGetString(rawType, out var eventType)
                || !string.Equals(eventType, ResponseCreateEventType, StringComparison.Ordinal))
            {
                return new PreparedRequest
                {
                    Error = InvalidRequest("expected a response.create event", "type"),
                };
            }

            var generate = true;
            if (payload.TryGetValue("generate", out var rawGenerate))
            {
                if (TryGetBoolean(rawGenerate, out var parsedGenerate))
                {
                    generate = parsedGenerate;
                }
                else if (!IsJsonNull(rawGenerate))
                {
                    // Unmarshalling null into a bool is a no-op in Go.
                    return new PreparedRequest
                    {
                        Error = InvalidRequest("generate must be a boolean", "generate"),
                    };
                }
            }

            payload.Remove("type");
            payload.Remove("generate");
            payload.Remove("background");
            payload.Remove("stream_id");

            var previousResponseId = StringField(payload, "previous_response_id");
            if (_warmupId.Length > 0
                && string.Equals(previousResponseId, _warmupId, StringComparison.Ordinal))
            {
                var mergedBytes = WarmupBytes(payload) + _warmupBytes;
                if (mergedBytes > MaxContextBytes || !budget.Replace(0, mergedBytes))
                {
                    return new PreparedRequest { Error = RetentionLimitError() };
                }

                var merged = MergeWarmup(_warmupPayload!, payload);
                if (merged is null)
                {
                    budget.Replace(mergedBytes, 0);
                    return new PreparedRequest
                    {
                        Error = InvalidRequest("input must be a string or an array", "input"),
                    };
                }

                payload = merged;
                _warmupId = string.Empty;
                _warmupPayload = null;
                budget.Replace(_warmupBytes, 0);
                _warmupBytes = mergedBytes;
            }

            if (!generate)
            {
                var model = StringField(payload, "model");
                if (model.Length == 0)
                {
                    return new PreparedRequest
                    {
                        Error = InvalidRequest("model is required", "model"),
                    };
                }

                var id = "resp_" + Guid.NewGuid().ToString().Replace("-", string.Empty);

                var size = WarmupBytes(payload);
                if (!budget.Replace(_warmupBytes, size))
                {
                    return new PreparedRequest { Error = RetentionLimitError() };
                }

                _warmupBytes = size;
                _warmupId = id;
                _warmupPayload = ClonePayload(payload);

                return new PreparedRequest
                {
                    Warmup = new Warmup(
                        id,
                        model,
                        StringField(payload, "previous_response_id")),
                };
            }

            payload["stream"] = Encoding.UTF8.GetBytes("true");
            var body = JsonObjectFromFields(payload);

            var request = new HttpRequestData
            {
                Method = "POST",
                Url = info.Url,
                Path = info.Path,
                Query = CloneFields(info.Query),
                RouteValues = new Dictionary<string, string?>(),
                Headers = BuildForwardHeaders(info.Headers),
                ContentType = "application/json",
                Body = body,
                JsonBody = [.. message],
                Auth = new AuthConfig(),
                RequestId = string.Empty,
                ClientIp = info.ClientIp,
                UserAgent = info.UserAgent,
                SessionId = info.SessionId,
                SessionScope = info.SessionScope,
            };

            return new PreparedRequest { Request = request };
        }
    }

    // ------------------------------------------------------------ envelope

    /// <summary>Port of responsesWebSocketEnvelopeStreamID.</summary>
    private static (string StreamId, ResponseError? Error) EnvelopeStreamId(byte[] message)
    {
        var payload = ParsePayload(message);
        if (payload is null)
        {
            return (string.Empty, InvalidRequest("invalid response.create JSON payload", string.Empty));
        }

        if (!payload.TryGetValue("type", out var rawType)
            || !TryGetString(rawType, out var eventType)
            || !string.Equals(eventType, ResponseCreateEventType, StringComparison.Ordinal))
        {
            return (string.Empty, InvalidRequest("expected a response.create event", "type"));
        }

        if (!payload.TryGetValue("stream_id", out var rawStreamId))
        {
            return (string.Empty, null);
        }

        if (!TryGetString(rawStreamId, out var streamId)
            || !ValidStreamId(streamId))
        {
            return (string.Empty, RequestError(
                "The 'stream_id' field must be a non-empty string with at most 256 characters and may only contain letters, numbers, underscores, hyphens, and periods.",
                "stream_id",
                "invalid_stream_id"));
        }

        return (streamId, null);
    }

    private static bool ValidStreamId(string streamId)
    {
        if (streamId.Length == 0 || streamId.Length > 256)
        {
            return false;
        }

        foreach (var character in streamId)
        {
            if (character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '_' or '-' or '.')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>Port of mergeResponsesWebSocketWarmup.</summary>
    private static Dictionary<string, byte[]>? MergeWarmup(
        Dictionary<string, byte[]> basePayload,
        Dictionary<string, byte[]> continuation)
    {
        var merged = ClonePayload(basePayload);
        foreach (var (key, value) in continuation)
        {
            if (key is "input" or "previous_response_id")
            {
                continue;
            }

            merged[key] = [.. value];
        }

        var baseHasInput = basePayload.TryGetValue("input", out var baseInput);
        var continuationHasInput = continuation.TryGetValue("input", out var continuationInput);
        if (baseHasInput && continuationHasInput)
        {
            var items = AppendInput(baseInput!, continuationInput!);
            if (items is null)
            {
                return null;
            }

            merged["input"] = items;
        }
        else if (continuationHasInput)
        {
            merged["input"] = [.. continuationInput!];
        }

        if (basePayload.TryGetValue("previous_response_id", out var previous))
        {
            merged["previous_response_id"] = [.. previous];
        }
        else
        {
            merged.Remove("previous_response_id");
        }

        return merged;
    }

    /// <summary>Port of appendResponsesWebSocketInput.</summary>
    private static byte[]? AppendInput(byte[] first, byte[] second)
    {
        if (!TryInputItems(first, out var firstItems, out var firstIsNull)
            || !TryInputItems(second, out var secondItems, out _))
        {
            return null;
        }

        // json.Marshal of a nil slice is "null": append(append(nil), empty...)
        // stays nil, which is exactly Go's first-null plus empty-second case.
        if (firstIsNull && secondItems.Count == 0)
        {
            return Encoding.UTF8.GetBytes("null");
        }

        var items = new List<byte[]>(firstItems.Count + secondItems.Count);
        items.AddRange(firstItems);
        items.AddRange(secondItems);
        return JsonArray(items);
    }

    /// <summary>Port of responsesWebSocketInputItems.</summary>
    private static bool TryInputItems(byte[] input, out List<byte[]> items, out bool isNull)
    {
        items = [];
        isNull = false;

        if (!TryParseJson(input, out var document))
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var element in root.EnumerateArray())
                    {
                        items.Add(Encoding.UTF8.GetBytes(element.GetRawText()));
                    }

                    return true;

                case JsonValueKind.Null:
                    isNull = true;
                    return true;

                case JsonValueKind.String:
                    var text = root.GetString() ?? string.Empty;
                    items.Add(JsonObject(
                        ("content", String(text)),
                        ("role", String("user")),
                        ("type", String("message"))));
                    return true;

                default:
                    return false;
            }
        }
    }

    // ------------------------------------------------------------- helpers

    private static Dictionary<string, byte[]>? ParsePayload(byte[] message)
    {
        if (!TryParseJson(message, out var document))
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                payload[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
            }

            return payload;
        }
    }

    private static Dictionary<string, byte[]> ClonePayload(Dictionary<string, byte[]> payload)
    {
        var cloned = new Dictionary<string, byte[]>(payload.Count, StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            cloned[key] = [.. value];
        }

        return cloned;
    }

    private static Dictionary<string, List<string>> CloneFields(
        Dictionary<string, List<string>> fields)
    {
        var cloned = new Dictionary<string, List<string>>(fields.Count, StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            cloned[key] = [.. value];
        }

        return cloned;
    }

    private static string StringField(Dictionary<string, byte[]> payload, string key)
        => payload.TryGetValue(key, out var raw) && TryGetString(raw, out var value)
            ? value
            : string.Empty;

    private static bool TryGetString(byte[] raw, out string value)
    {
        value = string.Empty;
        if (!TryParseJson(raw, out var document))
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            value = document.RootElement.GetString() ?? string.Empty;
            return true;
        }
    }

    private static bool TryGetBoolean(byte[] raw, out bool value)
    {
        value = false;
        if (!TryParseJson(raw, out var document))
        {
            return false;
        }

        using (document)
        {
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.True:
                    value = true;
                    return true;
                case JsonValueKind.False:
                    value = false;
                    return true;
                default:
                    return false;
            }
        }
    }

    private static bool IsJsonNull(byte[] raw)
    {
        if (!TryParseJson(raw, out var document))
        {
            return false;
        }

        using (document)
        {
            return document.RootElement.ValueKind == JsonValueKind.Null;
        }
    }

    private static bool TryParseJson(byte[] raw, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(raw);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static bool IsValidJson(byte[] body)
    {
        if (!TryParseJson(body, out var document))
        {
            return false;
        }

        document.Dispose();
        return true;
    }

    private static int WarmupBytes(Dictionary<string, byte[]> payload)
    {
        // responsesWarmupBytes counts the raw key and value bytes; keys are
        // UTF-8 here, so byte count, not UTF-16 length.
        var total = 0;
        foreach (var (key, value) in payload)
        {
            total += Encoding.UTF8.GetByteCount(key) + value.Length;
        }

        return total;
    }

    private static string DataString(byte[] data, params string[] path)
        => DataString(data, path.AsSpan());

    private static string DataString(byte[] data, ReadOnlySpan<string> path)
    {
        if (!TryParseJson(data, out var document))
        {
            return string.Empty;
        }

        using (document)
        {
            var element = document.RootElement;
            foreach (var segment in path)
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty(segment, out var next))
                {
                    return string.Empty;
                }

                element = next;
            }

            // gjson's Result.String() renders numbers and booleans instead of
            // returning empty; only null/absent yield "".
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? string.Empty,
                JsonValueKind.Number => element.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => string.Empty,
            };
        }
    }

    /// <summary>Port of withResponsesWebSocketStreamID.</summary>
    private static byte[] WithStreamId(byte[] data, string streamId)
    {
        if (streamId.Length == 0)
        {
            return data;
        }

        var payload = ParsePayload(data)
            ?? throw new InvalidOperationException("failed to decode Responses WebSocket event");

        payload["stream_id"] = String(streamId);
        return JsonObjectFromFields(payload);
    }

    /// <summary>Port of responseWebSocketRequestHeaders.</summary>
    private static Dictionary<string, List<string>> BuildForwardHeaders(
        Dictionary<string, List<string>> headers)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            // Host/Transfer-Encoding never reach Go's http.Request.Header, so
            // the reference's clone cannot forward them either.
            if (name is "Host" or "Transfer-Encoding"
                or "Connection" or "Upgrade"
                or "Content-Encoding" or "Content-Length"
                || name.StartsWith("Sec-Websocket-", StringComparison.Ordinal))
            {
                continue;
            }

            result[name] = [.. values];
        }

        result["Content-Type"] = ["application/json"];
        return result;
    }

    /// <summary>Port of httpclient.ReadHTTPRequest's client-IP extraction.</summary>
    private static string ClientIp(Dictionary<string, List<string>> headers, string remoteIp)
    {
        var forwarded = HttpClientUtils.HeaderGet(headers, "X-Forwarded-For");
        if (forwarded.Length > 0)
        {
            var comma = forwarded.IndexOf(',', StringComparison.Ordinal);
            return comma >= 0 ? forwarded[..comma].Trim() : forwarded;
        }

        var realIp = HttpClientUtils.HeaderGet(headers, "X-Real-IP");
        return realIp.Length > 0 ? realIp : remoteIp;
    }

    private static Dictionary<string, List<string>> ToMultiMap(IQueryCollection query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            result[key] = [.. values.Where(static value => value is not null).Select(static value => value!)];
        }

        return result;
    }

    /// <summary>auth.go:338-344 withSessionScopeForAPIKey.</summary>
    private static string? SessionScope(RequestIdentity identity)
    {
        if (identity.ApiKeyId is not { } keyId)
        {
            return null;
        }

        var scope = "api_key:" + keyId.ToString(CultureInfo.InvariantCulture);
        if (identity.Project is { } project)
        {
            scope += ":project:" + project.Id.ToString(CultureInfo.InvariantCulture);
        }

        return scope;
    }

    // -------------------------------------------------------------- errors

    private static ResponseError InvalidRequest(string message, string param)
        => RequestError(message, param, "invalid_request_error");

    private static ResponseError RequestError(string message, string param, string code)
    {
        var detail = param.Length == 0
            ? JsonObject(
                ("code", String(code)),
                ("message", String(message)),
                ("type", String("invalid_request_error")))
            : JsonObject(
                ("code", String(code)),
                ("message", String(message)),
                ("param", String(param)),
                ("type", String("invalid_request_error")));
        return new ResponseError(StatusCodes.Status400BadRequest, detail);
    }

    private static ResponseError QueueLimitError()
        => new(
            StatusCodes.Status429TooManyRequests,
            RequestError(
                "This WebSocket connection has reached its pending response limit (64) or queued raw-frame byte limit (128 MiB). Wait for an active response to finish before sending more response.create events.",
                string.Empty,
                "websocket_queue_limit_reached").Detail);

    private static ResponseError RetentionLimitError()
        => RequestError(
            "This WebSocket connection has reached its retained context byte limit (64 MiB).",
            "input",
            "websocket_context_limit_reached");

    /// <summary>Port of writeResponsesWebSocketError's envelope extraction.</summary>
    private static bool TryExtractErrorDetail(byte[] body, out byte[] detail)
    {
        detail = [];
        if (!TryParseJson(body, out var document))
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Go matches the struct field case-insensitively and fails the
            // whole unmarshal when the value is not an object.
            JsonElement? error = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase))
                {
                    error = property.Value;
                }
            }

            if (!error.HasValue || error.Value.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var value = error.Value;
            if (!value.EnumerateObject().Any())
            {
                return false;
            }

            detail = NormalizeJson(value);
            return true;
        }
    }

    /// <summary>
    /// Go <c>json.Marshal</c> of a value decoded into <c>map[string]any</c>:
    /// object keys sorted by code point, strings re-encoded with Go's escaping,
    /// number literals left as they appeared (Go's float64 round-trip can
    /// normalize exotic literals; registered with C244).
    /// </summary>
    private static byte[] NormalizeJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    fields[property.Name] = property.Value;
                }

                var sorted = new (string Name, byte[] Raw)[fields.Count];
                var index = 0;
                foreach (var (name, value) in fields)
                {
                    sorted[index++] = (name, NormalizeJson(value));
                }

                return JsonObject(sorted);

            case JsonValueKind.Array:
                var items = new List<byte[]>();
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(NormalizeJson(item));
                }

                return JsonArray(items);

            case JsonValueKind.String:
                return String(element.GetString() ?? string.Empty);

            case JsonValueKind.Number:
                return Encoding.UTF8.GetBytes(element.GetRawText());

            case JsonValueKind.True:
                return Encoding.UTF8.GetBytes("true");

            case JsonValueKind.False:
                return Encoding.UTF8.GetBytes("false");

            default:
                return Encoding.UTF8.GetBytes("null");
        }
    }

    // --------------------------------------------------------- JSON writer

    private static byte[] String(string value)
        => AxonHub.Llm.Json.GoJson.Marshal(value);

    private static byte[] Number(long value)
        => Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture));

    private static byte[] EmptyArray() => [(byte)'[', (byte)']'];

    private static byte[] JsonArray(IReadOnlyList<byte[]> items)
    {
        var output = new List<byte> { (byte)'[' };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                output.Add((byte)',');
            }

            output.AddRange(AxonHub.Llm.Json.GoJson.Compact(items[i]));
        }

        output.Add((byte)']');
        return [.. output];
    }

    private static byte[] JsonObjectFromFields(Dictionary<string, byte[]> members)
    {
        var fields = new (string Name, byte[] Raw)[members.Count];
        var index = 0;
        foreach (var (key, value) in members)
        {
            fields[index++] = (key, value);
        }

        return JsonObject(fields);
    }

    private static byte[] JsonObject(params (string Name, byte[] Raw)[] members)
    {
        var sorted = members.ToArray();
        Array.Sort(sorted, static (left, right) => CompareCodePoints(left.Name, right.Name));

        var output = new List<byte> { (byte)'{' };
        for (var i = 0; i < sorted.Length; i++)
        {
            if (i > 0)
            {
                output.Add((byte)',');
            }

            output.AddRange(String(sorted[i].Name));
            output.Add((byte)':');
            output.AddRange(AxonHub.Llm.Json.GoJson.Compact(sorted[i].Raw));
        }

        output.Add((byte)'}');
        return [.. output];
    }

    /// <summary>Go sorts map keys by UTF-8 bytes, i.e. Unicode code points.</summary>
    private static int CompareCodePoints(string left, string right)
    {
        var leftRunes = left.EnumerateRunes();
        var rightRunes = right.EnumerateRunes();
        while (true)
        {
            var hasLeft = leftRunes.MoveNext();
            var hasRight = rightRunes.MoveNext();
            if (!hasLeft || !hasRight)
            {
                return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;
            }

            if (leftRunes.Current.Value != rightRunes.Current.Value)
            {
                return leftRunes.Current.Value < rightRunes.Current.Value ? -1 : 1;
            }
        }
    }
}
