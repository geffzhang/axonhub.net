using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the downstream Responses WebSocket wire shape
/// (internal/server/api/responses_websocket.go at f272549f, §160/C244):
/// per-lane FIFO, cross-lane concurrency, named/active/pending limits, the
/// warmup continuation merge, error envelopes and the upgrade guard. The
/// reference's per-event LLMRequestTimeout (responses_websocket.go:267-283) is
/// ported in §214; the session id and Responses context markers still ride the
/// request record instead of the wire.
/// </summary>
public sealed class ResponsesWebSocketEndpointTests
{
    private const string CreatedEvent =
        """{"type":"response.created","response":{"id":"resp_1"}}""";

    private const string CompletedEvent =
        """{"type":"response.completed","response":{"id":"resp_1","status":"completed"}}""";

    [Fact]
    public async Task ProcessesSequentialResponseCreateEvents()
    {
        var requests = Channel.CreateUnbounded<HttpRequestData>();
        await using var host = await TestHost.StartAsync((request, _, _) =>
        {
            requests.Writer.TryWrite(request);
            return Task.FromResult(StreamResult(CreatedEvent, CompletedEvent));
        });

        using var socket = await host.ConnectAsync("Bearer test-key");

        string[] frames =
        [
            """{"type":"response.create","model":"gpt-test","stream":false,"background":true,"input":"hello"}""",
            """{"type":"response.create","model":"gpt-test","previous_response_id":"resp_1","input":[{"type":"function_call_output","call_id":"call_1","output":"ok"}]}""",
        ];
        foreach (var frame in frames)
        {
            await SendTextAsync(socket, frame);
            Assert.Equal("response.created", Text(Parse(await ReadTextAsync(socket)), "type"));
            Assert.Equal("response.completed", Text(Parse(await ReadTextAsync(socket)), "type"));
        }

        var first = await requests.Reader.ReadAsync(Timeout());
        var second = await requests.Reader.ReadAsync(Timeout());

        Assert.Equal("POST", first.Method);
        Assert.Equal("/v1/responses", first.Path);
        Assert.Equal("Bearer test-key", HeaderGet(first.Headers!, "Authorization"));
        Assert.Equal("application/json", HeaderGet(first.Headers!, "Content-Type"));
        Assert.Equal(string.Empty, HeaderGet(first.Headers!, "Connection"));
        Assert.Equal(string.Empty, HeaderGet(first.Headers!, "Upgrade"));

        var firstBody = Parse(first.Body!);
        Assert.Equal("true", Text(firstBody, "stream"));
        Assert.False(firstBody.TryGetProperty("type", out _));
        Assert.False(firstBody.TryGetProperty("background", out _));
        // json_body is the untouched inbound frame, exactly as gorilla hands it
        // to the reference handler.
        Assert.Equal(frames[0], Encoding.UTF8.GetString(first.JsonBody!));

        var secondBody = Parse(second.Body!);
        Assert.Equal("true", Text(secondBody, "stream"));
        Assert.Equal("resp_1", Text(secondBody, "previous_response_id"));
        var secondJson = Parse(second.JsonBody!);
        Assert.Equal("response.create", Text(secondJson, "type"));
        Assert.False(secondJson.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task KeepsWritingEventsAfterTheTerminalEventUntilTheStreamEnds()
    {
        await using var host = await TestHost.StartAsync((_, _, _) =>
            Task.FromResult(StreamResult(
                CreatedEvent,
                CompletedEvent,
                """{"type":"response.output_text.delta","delta":"tail"}""")));

        using var socket = await host.ConnectAsync();
        await SendTextAsync(
            socket,
            """{"type":"response.create","model":"gpt-test","input":"hello"}""");

        Assert.Equal("response.created", Text(Parse(await ReadTextAsync(socket)), "type"));
        Assert.Equal("response.completed", Text(Parse(await ReadTextAsync(socket)), "type"));

        // responses_websocket.go:657-682 keeps draining after the terminal
        // event so the persistent stream can record the turn's final state.
        var tail = Parse(await ReadTextAsync(socket));
        Assert.Equal("response.output_text.delta", Text(tail, "type"));
        Assert.Equal("tail", Text(tail, "delta"));
    }

    [Fact]
    public async Task StreamIdIsClientOnlyAndReturnedOnEveryEvent()
    {
        var requests = Channel.CreateUnbounded<HttpRequestData>();
        await using var host = await TestHost.StartAsync((request, _, _) =>
        {
            requests.Writer.TryWrite(request);
            return Task.FromResult(StreamResult(
                """{"type":"response.created","response":{"id":"resp_lane"}}""",
                """{"type":"response.completed","response":{"id":"resp_lane","status":"completed"}}"""));
        });

        using var socket = await host.ConnectAsync();
        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane.alpha-1","model":"gpt-test","input":"hello"}""");

        foreach (var eventType in new[] { "response.created", "response.completed" })
        {
            var envelope = Parse(await ReadTextAsync(socket));
            Assert.Equal(eventType, Text(envelope, "type"));
            Assert.Equal("lane.alpha-1", Text(envelope, "stream_id"));
        }

        var request = await requests.Reader.ReadAsync(Timeout());
        var body = Parse(request.Body!);
        Assert.False(body.TryGetProperty("stream_id", out _));
        Assert.Equal("lane.alpha-1", Text(Parse(request.JsonBody!), "stream_id"));
    }

    [Fact]
    public async Task RunsDifferentStreamsConcurrentlyAndSameStreamFifo()
    {
        var startedA1 = NewSignal();
        var startedA2 = NewSignal();
        var startedB = NewSignal();
        var releaseA1 = NewSignal();

        await using var host = await TestHost.StartAsync(async (request, _, _) =>
        {
            var input = Text(Parse(request.Body!), "input");
            switch (input)
            {
                case "a1":
                    startedA1.TrySetResult();
                    await releaseA1.Task;
                    break;
                case "a2":
                    startedA2.TrySetResult();
                    break;
                case "b1":
                    startedB.TrySetResult();
                    break;
            }

            return StreamResult(CompletedFor(input));
        });

        using var socket = await host.ConnectAsync();
        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-a","model":"gpt-test","input":"a1"}""");
        await startedA1.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-a","model":"gpt-test","input":"a2"}""");
        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-b","model":"gpt-test","input":"b1"}""");

        await startedB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(startedA2.Task.IsCompleted);

        var eventB = Parse(await ReadTextAsync(socket));
        Assert.Equal("lane-b", Text(eventB, "stream_id"));
        Assert.Equal("resp_b1", Text(eventB, "response", "id"));

        releaseA1.TrySetResult();
        await startedA2.Task.WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var responseId in new[] { "resp_a1", "resp_a2" })
        {
            var envelope = Parse(await ReadTextAsync(socket));
            Assert.Equal("lane-a", Text(envelope, "stream_id"));
            Assert.Equal(responseId, Text(envelope, "response", "id"));
        }
    }

    [Fact]
    public async Task AppliesPerEventTimeoutWithoutClosingTheConnection()
    {
        await using var host = await TestHost.StartAsync(
            async (request, _, cancellationToken) =>
            {
                var input = Text(Parse(request.Body!), "input");
                if (input == "slow")
                {
                    await Task.Delay(
                        System.Threading.Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }

                return StreamResult(CompletedFor(input));
            },
            (_, _) => Task.FromResult<HttpResponseData>(null!),
            TimeSpan.FromMilliseconds(200));

        using var socket = await host.ConnectAsync();
        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-a","model":"gpt-test","input":"slow"}""");

        // transformResponsesWebSocketError's fixed 500 envelope when the
        // transformer declines the deadline error.
        var error = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(error, "type"));
        Assert.Equal(500, error.GetProperty("status").GetInt32());
        Assert.Equal("internal server error", Text(error, "error", "message"));
        Assert.Equal("server_error", Text(error, "error", "type"));

        // The deadline is per response.create event; the socket stays usable.
        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-a","model":"gpt-test","input":"fast"}""");
        var completed = Parse(await ReadTextAsync(socket));
        Assert.Equal("response.completed", Text(completed, "type"));
        Assert.Equal("resp_fast", Text(completed, "response", "id"));
    }

    [Fact]
    public async Task ValidatesAndLimitsNamedStreams()
    {
        await using var host = await TestHost.StartAsync();
        using var socket = await host.ConnectAsync();

        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"bad/lane","model":"gpt-test","generate":false}""");
        var invalid = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(invalid, "type"));
        Assert.Equal("invalid_stream_id", Text(invalid, "error", "code"));
        Assert.False(invalid.TryGetProperty("stream_id", out _));

        for (var i = 0; i < 32; i++)
        {
            await SendTextAsync(
                socket,
                "{\"type\":\"response.create\",\"stream_id\":\"lane-" + i
                    + "\",\"model\":\"gpt-test\",\"generate\":false}");
        }

        var completed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 32 * 3; i++)
        {
            var envelope = Parse(await ReadTextAsync(socket));
            if (Text(envelope, "type") == "response.completed")
            {
                completed.Add(Text(envelope, "stream_id"));
            }
        }

        Assert.Equal(32, completed.Count);

        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-over-limit","model":"gpt-test","generate":false}""");
        var limit = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(limit, "type"));
        Assert.Equal("websocket_stream_limit_reached", Text(limit, "error", "code"));
        Assert.Equal("lane-over-limit", Text(limit, "stream_id"));
    }

    [Fact]
    public async Task LimitsActiveResponsesPerConnection()
    {
        var started = Channel.CreateUnbounded<string>();
        var gate = new SemaphoreSlim(0);
        try
        {
            await using var host = await TestHost.StartAsync(async (request, _, ct) =>
            {
                var input = Text(Parse(request.Body!), "input");
                started.Writer.TryWrite(input);
                await gate.WaitAsync(ct);
                return StreamResult(CompletedFor(input));
            });

            using var socket = await host.ConnectAsync();
            for (var i = 0; i <= 16; i++)
            {
                await SendTextAsync(
                    socket,
                    "{\"type\":\"response.create\",\"stream_id\":\"active-" + i
                        + "\",\"model\":\"gpt-test\",\"input\":\"" + i + "\"}");
            }

            var startTimeout = Timeout();
            for (var i = 0; i < 16; i++)
            {
                await started.Reader.ReadAsync(startTimeout);
            }

            await Task.Delay(100);
            Assert.False(started.Reader.TryRead(out _));

            // The reference does not promise which queued event starts first;
            // only that a queued one starts once a slot is free.
            gate.Release();
            await started.Reader.ReadAsync(Timeout());

            gate.Release(100);
            for (var i = 0; i <= 16; i++)
            {
                Assert.Equal("response.completed", Text(Parse(await ReadTextAsync(socket)), "type"));
            }
        }
        finally
        {
            gate.Release(200);
            gate.Dispose();
        }
    }

    [Fact]
    public async Task BoundsPendingResponsesPerConnection()
    {
        var started = NewSignal();
        var gate = new SemaphoreSlim(0);
        try
        {
            await using var host = await TestHost.StartAsync(async (_, _, ct) =>
            {
                started.TrySetResult();
                await gate.WaitAsync(ct);
                return StreamResult(
                    """{"type":"response.completed","response":{"id":"resp_pending","status":"completed"}}""");
            });

            using var socket = await host.ConnectAsync();
            for (var i = 0; i <= 64; i++)
            {
                await SendTextAsync(
                    socket,
                    "{\"type\":\"response.create\",\"model\":\"gpt-test\",\"input\":\"" + i + "\"}");
            }

            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var limit = Parse(await ReadTextAsync(socket));
            Assert.Equal("error", Text(limit, "type"));
            Assert.Equal("429", Text(limit, "status"));
            Assert.Equal("websocket_queue_limit_reached", Text(limit, "error", "code"));

            gate.Release(100);
            for (var i = 0; i < 64; i++)
            {
                Assert.Equal("response.completed", Text(Parse(await ReadTextAsync(socket)), "type"));
            }
        }
        finally
        {
            gate.Release(200);
            gate.Dispose();
        }
    }

    [Fact]
    public async Task WarmupReturnsIdAndMergesContinuation()
    {
        var requests = Channel.CreateUnbounded<HttpRequestData>();
        await using var host = await TestHost.StartAsync((request, _, _) =>
        {
            requests.Writer.TryWrite(request);
            return Task.FromResult(StreamResult(
                """{"type":"response.completed","response":{"id":"resp_generated","status":"completed"}}"""));
        });

        using var socket = await host.ConnectAsync();
        await SendTextAsync(
            socket,
            """{"type":"response.create","model":"gpt-test","generate":false,"previous_response_id":"resp_upstream","instructions":"be concise","input":"hello"}""");

        var created = Parse(await ReadTextAsync(socket));
        Assert.Equal("response.created", Text(created, "type"));
        var warmupId = Text(created, "response", "id");
        Assert.NotEmpty(warmupId);
        Assert.Equal("resp_upstream", Text(created, "response", "previous_response_id"));

        var inProgress = Parse(await ReadTextAsync(socket));
        Assert.Equal("response.in_progress", Text(inProgress, "type"));

        var warmupCompleted = Parse(await ReadTextAsync(socket));
        Assert.Equal("response.completed", Text(warmupCompleted, "type"));
        Assert.Equal(warmupId, Text(warmupCompleted, "response", "id"));

        await SendTextAsync(
            socket,
            """{"type":"response.create","model":"gpt-test","input":"unrelated"}""");
        Assert.Equal(
            "resp_generated",
            Text(Parse(await ReadTextAsync(socket)), "response", "id"));

        await SendTextAsync(
            socket,
            "{\"type\":\"response.create\",\"model\":\"gpt-test\",\"previous_response_id\":\""
                + warmupId + "\",\"input\":[{\"type\":\"function_call_output\",\"call_id\":\"call_1\",\"output\":\"ok\"}]}");
        Assert.Equal(
            "resp_generated",
            Text(Parse(await ReadTextAsync(socket)), "response", "id"));

        var unrelated = Parse((await requests.Reader.ReadAsync(Timeout())).Body!);
        Assert.Equal("unrelated", Text(unrelated, "input"));
        Assert.False(unrelated.TryGetProperty("instructions", out _));

        var continuation = Parse((await requests.Reader.ReadAsync(Timeout())).Body!);
        Assert.Equal("be concise", Text(continuation, "instructions"));
        Assert.Equal("resp_upstream", Text(continuation, "previous_response_id"));
        Assert.Equal(2, continuation.GetProperty("input").GetArrayLength());
        Assert.Equal("hello", Text(continuation, "input", 0, "content"));
        Assert.Equal("function_call_output", Text(continuation, "input", 1, "type"));
    }

    [Fact]
    public async Task WarmupsAreIsolatedByStream()
    {
        var requests = Channel.CreateUnbounded<HttpRequestData>();
        await using var host = await TestHost.StartAsync((request, _, _) =>
        {
            requests.Writer.TryWrite(request);
            return Task.FromResult(StreamResult(
                """{"type":"response.completed","response":{"id":"resp_generated","status":"completed"}}"""));
        });

        using var socket = await host.ConnectAsync();
        var warmupIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var streamId in new[] { "lane-a", "lane-b" })
        {
            await SendTextAsync(
                socket,
                "{\"type\":\"response.create\",\"stream_id\":\"" + streamId
                    + "\",\"model\":\"gpt-test\",\"generate\":false,\"input\":\"" + streamId + "-base\"}");
            for (var i = 0; i < 3; i++)
            {
                var envelope = Parse(await ReadTextAsync(socket));
                Assert.Equal(streamId, Text(envelope, "stream_id"));
                if (i == 0)
                {
                    warmupIds[streamId] = Text(envelope, "response", "id");
                }
            }
        }

        foreach (var streamId in new[] { "lane-a", "lane-b" })
        {
            await SendTextAsync(
                socket,
                "{\"type\":\"response.create\",\"stream_id\":\"" + streamId
                    + "\",\"model\":\"gpt-test\",\"previous_response_id\":\"" + warmupIds[streamId]
                    + "\",\"input\":\"" + streamId + "-next\"}");
            Assert.Equal(
                streamId,
                Text(Parse(await ReadTextAsync(socket)), "stream_id"));
        }

        var seen = new Dictionary<string, string[]>(StringComparer.Ordinal);
        for (var i = 0; i < 2; i++)
        {
            var body = Parse((await requests.Reader.ReadAsync(Timeout())).Body!);
            var items = body.GetProperty("input");
            Assert.Equal(2, items.GetArrayLength());
            var baseContent = Text(body, "input", 0, "content");
            seen[baseContent[..^"-base".Length]] =
            [
                baseContent,
                Text(body, "input", 1, "content"),
            ];
        }

        Assert.Equal(new[] { "lane-a-base", "lane-a-next" }, seen["lane-a"]);
        Assert.Equal(new[] { "lane-b-base", "lane-b-next" }, seen["lane-b"]);
    }

    [Fact]
    public async Task WritesProtocolAndProcessingErrorsWithoutClosingConnection()
    {
        await using var host = await TestHost.StartAsync(
            (request, _, _) =>
            {
                if (Text(Parse(request.Body!), "model") == "fail")
                {
                    throw new InvalidOperationException("upstream unavailable");
                }

                return Task.FromResult(new ChatCompletionResult
                {
                    Response = new HttpResponseData
                    {
                        StatusCode = 200,
                        Body = Encoding.UTF8.GetBytes(
                            """{"id":"resp_ok","object":"response","status":"completed","output":[]}"""),
                    },
                });
            },
            (_, _) => Task.FromResult(new HttpResponseData
            {
                StatusCode = 502,
                Body = Encoding.UTF8.GetBytes(
                    """{"error":{"message":"upstream unavailable","type":"upstream_error","code":"provider_unavailable"}}"""),
            }));

        using var socket = await host.ConnectAsync();

        await SendTextAsync(socket, """{"type":"not.response.create"}""");
        var invalid = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(invalid, "type"));
        Assert.Equal("400", Text(invalid, "status"));
        Assert.Equal("type", Text(invalid, "error", "param"));

        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-errors","model":"fail","input":"hello"}""");
        var processError = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(processError, "type"));
        Assert.Equal("502", Text(processError, "status"));
        Assert.Equal("provider_unavailable", Text(processError, "error", "code"));
        Assert.Equal("lane-errors", Text(processError, "stream_id"));

        await SendTextAsync(
            socket,
            """{"type":"response.create","stream_id":"lane-errors","model":"ok","input":"hello"}""");
        var completed = Parse(await ReadTextAsync(socket));
        Assert.Equal("response.completed", Text(completed, "type"));
        Assert.Equal("resp_ok", Text(completed, "response", "id"));
        Assert.Equal("lane-errors", Text(completed, "stream_id"));
    }

    [Fact]
    public async Task RejectsNonTextFramesAndMalformedJsonWithoutClosing()
    {
        await using var host = await TestHost.StartAsync((_, _, _) =>
            Task.FromResult(StreamResult(
                """{"type":"response.completed","response":{"id":"resp_ok","status":"completed"}}""")));
        using var socket = await host.ConnectAsync();

        await socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes("x")),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            CancellationToken.None);
        var binaryError = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(binaryError, "type"));
        Assert.Equal(
            "response.create must be sent as a text message",
            Text(binaryError, "error", "message"));

        await SendTextAsync(socket, "{");
        var jsonError = Parse(await ReadTextAsync(socket));
        Assert.Equal("error", Text(jsonError, "type"));
        Assert.Equal(
            "invalid response.create JSON payload",
            Text(jsonError, "error", "message"));

        await SendTextAsync(
            socket,
            """{"type":"response.create","model":"gpt-test","input":"hello"}""");
        Assert.Equal(
            "resp_ok",
            Text(Parse(await ReadTextAsync(socket)), "response", "id"));
    }

    [Fact]
    public async Task RequiresUpgrade()
    {
        await using var host = await TestHost.StartAsync();

        var response = await host.Http.GetAsync("/v1/responses");

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Contains(
            "websocket upgrade required",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- helpers

    private static ChatCompletionResult StreamResult(params string[] events)
        => new()
        {
            Stream = new SliceStream<StreamEvent>(events.Select(data => new StreamEvent
            {
                Type = Text(Parse(Encoding.UTF8.GetBytes(data)), "type"),
                Data = Encoding.UTF8.GetBytes(data),
            })),
        };

    private static string CompletedFor(string input)
        => "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_" + input
            + "\",\"status\":\"completed\"}}";

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationToken Timeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private static async Task SendTextAsync(WebSocket socket, string text)
        => await socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);

    private static async Task<string> ReadTextAsync(WebSocket socket)
    {
        var timeout = Timeout();
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    private static JsonElement Parse(string text)
        => Parse(Encoding.UTF8.GetBytes(text));

    private static JsonElement Parse(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        return document.RootElement.Clone();
    }

    private static string Text(JsonElement element, params object[] path)
    {
        foreach (var segment in path)
        {
            if (segment is int index)
            {
                if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() <= index)
                {
                    return string.Empty;
                }

                element = element[index];
                continue;
            }

            var name = (string)segment;
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(name, out element))
            {
                return string.Empty;
            }
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : element.GetRawText();
    }

    private static string HeaderGet(Dictionary<string, List<string>> headers, string name)
        => headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0]
            : string.Empty;

    /// <summary>
    /// The reference's <c>newResponsesWebSocketTestServer</c>: a bare router with
    /// only the WebSocket route and both handler seams injected.
    /// </summary>
    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly WebSocketClient _client;

        private TestHost(WebApplication app)
        {
            _app = app;
            _client = app.GetTestServer().CreateWebSocketClient();
        }

        public HttpClient Http => _app.GetTestClient();

        public static async Task<TestHost> StartAsync(
            Func<HttpRequestData, RequestIdentity, CancellationToken, Task<ChatCompletionResult>>? process = null,
            Func<Exception, CancellationToken, Task<HttpResponseData>>? transformError = null,
            TimeSpan requestTimeout = default)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();

            var processFunc = process
                ?? ((_, _, _) => Task.FromResult(new ChatCompletionResult()));
            var errorFunc = transformError
                ?? ((_, _) => Task.FromResult(new HttpResponseData { StatusCode = 500 }));

            var app = builder.Build();
            app.UseWebSockets();
            app.MapGet(
                "/v1/responses",
                context => ResponsesWebSocketEndpoint.HandleAsync(
                    context,
                    processFunc,
                    errorFunc,
                    requestTimeout));

            await app.StartAsync();
            return new TestHost(app);
        }

        public Task<WebSocket> ConnectAsync(string? authorization = null)
        {
            if (authorization is not null)
            {
                _client.ConfigureRequest = request =>
                    request.Headers["Authorization"] = authorization;
            }

            return _client.ConnectAsync(new Uri("ws://localhost/v1/responses"), CancellationToken.None);
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
