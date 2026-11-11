using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using WebSocketStream = AxonHub.Llm.Transformer.OpenAI.Responses.WebSocketStream;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/responses/websocket_executor_test.go: the
/// Responses WebSocket transport, its connection pool, payload reconciliation
/// and stream semantics, exercised against a loopback WebSocket server.
/// </summary>
/// <remarks>
/// The reference tests build the request context with
/// <c>shared.WithSessionScope(ctx, "test-scope")</c>; the port carries the scope
/// on <see cref="HttpRequestData.SessionScope"/> (difference C062). Proxy and
/// TLS inheritance assertions are omitted because the port's
/// <see cref="HttpExecutor"/> exposes no seam for them (difference C061).
/// </remarks>
public sealed class ResponsesWebSocketExecutorTests
{
    private const string Scope = "test-scope";

    private const string CompletedEvent =
        """{"type":"response.completed","sequence_number":1,"response":{"id":"resp_test","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}""";

    // ------------------------------------------------------------------
    // Outbound transformer integration
    // ------------------------------------------------------------------

    [Fact]
    public void CustomizeExecutorCachesWebSocketExecutorPerInnerExecutor()
    {
        var outbound = new ResponsesOutboundTransformer(new ResponsesOutboundConfig
        {
            BaseUrl = "https://api.openai.com/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            Transport = ResponsesOutboundTransformer.TransportWebSocket,
        });
        var firstClient = new StubExecutor();
        var secondClient = new StubExecutor();

        var first = Assert.IsType<WebSocketExecutor>(outbound.CustomizeExecutor(firstClient));
        var second = Assert.IsType<WebSocketExecutor>(outbound.CustomizeExecutor(secondClient));
        var again = Assert.IsType<WebSocketExecutor>(outbound.CustomizeExecutor(firstClient));

        Assert.NotSame(first, second);
        Assert.Same(first, again);
        Assert.Same(firstClient, first.Inner());
        Assert.Same(secondClient, second.Inner());
    }

    // ------------------------------------------------------------------
    // Basic transports
    // ------------------------------------------------------------------

    [Fact]
    public void DoStreamSendsResponseCreate()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            Assert.Equal(WebSocketExecutor.BetaHeaderValue, conn.Headers["OpenAI-Beta"]);
            Assert.Equal("Bearer test-key", conn.Headers["Authorization"]);

            var payload = conn.ReadJson();
            Assert.Equal("response.create", payload.GetProperty("type").GetString());
            Assert.Equal("gpt-5", payload.GetProperty("model").GetString());
            Assert.Equal("Be concise", payload.GetProperty("instructions").GetString());
            Assert.False(payload.TryGetProperty("stream", out _));
            Assert.False(payload.TryGetProperty("background", out _));

            conn.WriteJson(CompletedEvent);
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        using var stream = executor.DoStream(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5","instructions":"Be concise","stream":true,"background":false}"""));

        Assert.True(stream.Next());
        var ev = Assert.IsType<StreamEvent>(stream.Current);
        Assert.Equal("response.completed", ev.Type);
        AssertJsonEqual(CompletedEvent, Encoding.UTF8.GetString(ev.Data));
        Assert.False(stream.Next());
        Assert.Null(stream.Err);

        Finish(server);
    }

    [Fact]
    public void StreamStopsAfterTerminalEventWithoutCloseFrame()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(CompletedEvent);
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        using var stream = executor.DoStream(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5"}"""));

        Assert.True(stream.Next());
        Assert.Equal("response.completed", stream.Current?.Type);
        Assert.False(stream.Next());
        Assert.Null(stream.Err);

        Finish(server);
    }

    [Fact]
    public void StreamReportsContextCancellationWhileReadBlocks()
    {
        using var payloadRead = new ManualResetEventSlim();
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            payloadRead.Set();
            conn.WaitUntilClosed();
        };

        using var cts = new CancellationTokenSource();
        var executor = new WebSocketExecutor(inner: null);
        using var stream = executor.DoStream(
            cts.Token,
            Request(server, """{"model":"gpt-5"}"""));

        Assert.True(payloadRead.Wait(TimeSpan.FromSeconds(5)));

        var nextReturned = new ManualResetEventSlim();
        var nextResult = true;
        var thread = new Thread(() =>
        {
            nextResult = stream.Next();
            nextReturned.Set();
        })
        {
            IsBackground = true,
        };
        thread.Start();

        Thread.Sleep(10);
        cts.Cancel();

        Assert.True(nextReturned.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(nextResult);
        var err = Assert.IsAssignableFrom<OperationCanceledException>(stream.Err);
        Assert.Equal(cts.Token, err.CancellationToken);
        thread.Join(TimeSpan.FromSeconds(5));

        Finish(server);
    }

    [Fact]
    public void StreamReportsContextCancellationWhenAlreadyClosed()
    {
        using var cts = new CancellationTokenSource();
        var stream = new WebSocketStream(cts.Token, lease: null);

        stream.Finish(evict: true);
        cts.Cancel();

        Assert.False(stream.Next());
        var err = Assert.IsAssignableFrom<OperationCanceledException>(stream.Err);
        Assert.Equal(cts.Token, err.CancellationToken);
    }

    [Fact]
    public void StreamDoesNotOverwriteTerminalResponseWithContextCancellation()
    {
        using var cts = new CancellationTokenSource();
        var stream = new WebSocketStream(cts.Token, lease: null);

        stream.MarkTerminal();
        stream.Finish(evict: false);
        cts.Cancel();

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
    }

    // ------------------------------------------------------------------
    // Top-level errors and aggregation
    // ------------------------------------------------------------------

    [Fact]
    public void DoReturnsErrorForTopLevelErrorEvent()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson("""{"type":"error","code":"bad_request","message":"invalid websocket request"}""");
        };

        var executor = new WebSocketExecutor(inner: null);
        var ex = Assert.Throws<InvalidOperationException>(() => executor.Do(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5"}""")));

        Assert.Contains("bad_request: invalid websocket request", ex.Message, StringComparison.Ordinal);
        Finish(server);
    }

    public static TheoryData<string, string, string> TopLevelErrorCases => new()
    {
        {
            "official nested error",
            """
            {
                "type":"error",
                "status":400,
                "error":{
                    "type":"invalid_request_error",
                    "code":"invalid_value",
                    "message":"invalid websocket request",
                    "param":"input"
                }
            }
            """,
            """
            {
                "error":{
                    "type":"invalid_request_error",
                    "code":"invalid_value",
                    "message":"invalid websocket request",
                    "param":"input"
                }
            }
            """
        },
        {
            "legacy flattened error",
            """
            {
                "type":"error",
                "status":400,
                "code":"invalid_value",
                "message":"invalid websocket request",
                "param":"input"
            }
            """,
            """
            {
                "error":{
                    "type":"invalid_value",
                    "code":"invalid_value",
                    "message":"invalid websocket request",
                    "param":"input"
                }
            }
            """
        },
        {
            "partial nested error retains flattened fields",
            """
            {
                "type":"error",
                "status":400,
                "code":"invalid_value",
                "message":"invalid websocket request",
                "param":"input",
                "error":{"type":"invalid_request_error","request_id":"req_123"}
            }
            """,
            """
            {
                "error":{
                    "type":"invalid_request_error",
                    "code":"invalid_value",
                    "message":"invalid websocket request",
                    "param":"input",
                    "request_id":"req_123"
                }
            }
            """
        },
    };

    [Theory]
    [MemberData(nameof(TopLevelErrorCases))]
    public void TopLevelWebSocketErrorPreservesOfficialErrorStatusAndBody(
        string name,
        string data,
        string want)
    {
        _ = name;
        var chunks = new List<StreamEvent?>
        {
            new()
            {
                Type = "error",
                Data = Encoding.UTF8.GetBytes(data),
            },
        };

        var error = WebSocketExecutor.TopLevelWebSocketError(chunks);
        var httpError = Assert.IsType<HttpClientError>(error);
        Assert.Equal(400, httpError.StatusCode);
        AssertJsonEqual(want, Encoding.UTF8.GetString(httpError.Body));
    }

    [Fact]
    public void DoAggregatesFailedResponseEvent()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(
                """{"type":"response.failed","response":{"id":"resp_failed","object":"response","created_at":1700000000,"model":"gpt-5","status":"failed","error":{"code":"server_error","message":"upstream failed"}}}""");
        };

        var executor = new WebSocketExecutor(inner: null);
        var response = executor.Do(CancellationToken.None, Request(server, """{"model":"gpt-5"}"""));

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        var root = body.RootElement;
        Assert.Equal("resp_failed", root.GetProperty("id").GetString());
        Assert.Equal("failed", root.GetProperty("status").GetString());
        Assert.Equal("server_error", root.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("upstream failed", root.GetProperty("error").GetProperty("message").GetString());

        Finish(server);
    }

    public static TheoryData<string, string, string, string> AggregatedTerminalCases => new()
    {
        { "cancelled", "response.cancelled", "resp_cancelled", "canceled" },
        { "incomplete", "response.incomplete", "resp_incomplete", "incomplete" },
    };

    [Theory]
    [MemberData(nameof(AggregatedTerminalCases))]
    public void DoAggregatesCancelledAndIncompleteResponseEvents(
        string name,
        string eventType,
        string responseId,
        string status)
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            var extra = name == "incomplete"
                ? ""","incomplete_details":{"reason":"max_output_tokens"}"""
                : string.Empty;
            conn.WriteJson(
                """{"type":""" + JsonSerializer.Serialize(eventType)
                + ""","response":{"id":""" + JsonSerializer.Serialize(responseId)
                + ""","object":"response","created_at":1700000000,"model":"gpt-5","status":"""
                + JsonSerializer.Serialize(status) + ""","output":[]""" + extra + "}}");
        };

        var executor = new WebSocketExecutor(inner: null);
        var response = executor.Do(CancellationToken.None, Request(server, """{"model":"gpt-5"}"""));

        Assert.Equal(200, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body!);
        var root = body.RootElement;
        Assert.Equal(responseId, root.GetProperty("id").GetString());
        Assert.Equal(status, root.GetProperty("status").GetString());
        if (name == "incomplete")
        {
            Assert.Equal(
                "max_output_tokens",
                root.GetProperty("incomplete_details").GetProperty("reason").GetString());
        }

        Finish(server);
    }

    // ------------------------------------------------------------------
    // Session pooling and incremental input
    // ------------------------------------------------------------------

    [Fact]
    public void ReusesConnectionForSameSession()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            Assert.Equal("session-1", conn.Headers["Session_id"]);
            for (var i = 0; i < 2; i++)
            {
                var payload = conn.ReadJson();
                Assert.Equal("response.create", payload.GetProperty("type").GetString());
                Assert.Equal("turn-" + (i + 1), payload.GetProperty("instructions").GetString());
                conn.WriteJson(Completed("resp_" + (i + 1), sequenceNumber: i + 1));
            }
        };

        var executor = new WebSocketExecutor(inner: null);
        for (var i = 0; i < 2; i++)
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, """{"model":"gpt-5","instructions":"turn-""" + (i + 1) + "\"}",
                    sessionId: "session-1"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(1, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void SharedExecutorSurvivesAttemptStopAndReusesTheConnection()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            for (var i = 0; i < 2; i++)
            {
                var payload = conn.ReadJson();
                Assert.Equal("turn-" + (i + 1), payload.GetProperty("instructions").GetString());
                conn.WriteJson(Completed("resp_" + (i + 1), sequenceNumber: i + 1));
            }

            conn.WaitUntilClosed();
        };

        // Every attempt builds its own transformer and stops it when the
        // attempt ends (ChannelOutboundFactory + ChatCompletionOrchestrator).
        // The process-wide executor cache keeps the pooled connection alive, so
        // the second attempt reuses the same upstream WebSocket (C072).
        var inner = new StubExecutor();
        for (var i = 0; i < 2; i++)
        {
            var attempt = ResponsesAttempt(server, channelId: 7001);
            var executor = Assert.IsType<WebSocketExecutor>(attempt.CustomizeExecutor(inner));
            using (var stream = executor.DoStream(
                CancellationToken.None,
                Request(
                    server,
                    """{"model":"gpt-5","instructions":"turn-""" + (i + 1) + "\"}",
                    sessionId: "shared-attempt-session")))
            {
                Assert.True(stream.Next());
                Assert.Equal("response.completed", stream.Current?.Type);
                Assert.False(stream.Next());
                Assert.Null(stream.Err);
            }

            attempt.Stop();
        }

        Assert.Equal(1, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void ChannelScopedExecutorsDoNotSharePools()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(Completed("resp_" + conn.UpgradeIndex));
            conn.WaitUntilClosed();
        };

        // webSocketPoolKey carries ChannelID, and the reference's executor is
        // per channel; two channels never share an upstream WebSocket even when
        // the session id, scope and credentials match.
        var inner = new StubExecutor();
        foreach (var channelId in new long[] { 7002, 7003 })
        {
            var attempt = ResponsesAttempt(server, channelId);
            var executor = Assert.IsType<WebSocketExecutor>(attempt.CustomizeExecutor(inner));
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, """{"model":"gpt-5"}""", sessionId: "channel-scoped-session"));
            Assert.True(stream.Next());
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
            attempt.Stop();
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    private static ResponsesOutboundTransformer ResponsesAttempt(
        ResponsesWebSocketTestServer server,
        long channelId)
        => new(new ResponsesOutboundConfig
        {
            BaseUrl = server.Url + "/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            Transport = ResponsesOutboundTransformer.TransportWebSocket,
            ChannelId = channelId,
        });

    [Fact]
    public void DoesNotPoolWithoutSession()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(CompletedEvent);
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        for (var i = 0; i < 2; i++)
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, """{"model":"gpt-5"}"""));
            Assert.True(stream.Next());
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void KeepsExplicitPreviousResponseIdOnFreshConnection()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var payload = conn.ReadJson();
            Assert.Equal("client_prev", payload.GetProperty("previous_response_id").GetString());
            conn.WriteJson(CompletedEvent);
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        using var stream = executor.DoStream(
            CancellationToken.None,
            Request(
                server,
                """{"model":"gpt-5","previous_response_id":"client_prev","input":[{"id":"first","type":"message"}]}""",
                sessionId: "explicit-previous"));
        Assert.True(stream.Next());
        Assert.Equal("response.completed", stream.Current?.Type);
        Assert.False(stream.Next());
        Assert.Null(stream.Err);

        Finish(server);
    }

    [Fact]
    public void ReconnectsForDifferentExplicitPreviousResponseId()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var payload = conn.ReadJson();
            if (conn.UpgradeIndex == 1)
            {
                Assert.False(payload.TryGetProperty("previous_response_id", out _));
                conn.WriteJson(Completed("resp_1"));
                conn.WaitUntilClosed();
                return;
            }

            Assert.Equal("client_prev", payload.GetProperty("previous_response_id").GetString());
            Assert.Equal(2, payload.GetProperty("input").GetArrayLength());
            conn.WriteJson(Completed("resp_2"));
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        foreach (var body in new[]
        {
            """{"model":"gpt-5","input":[{"id":"first","type":"message","role":"user"}]}""",
            """{"model":"gpt-5","previous_response_id":"client_prev","input":[{"id":"first","type":"message","role":"user"},{"id":"second","type":"message","role":"user"}]}""",
        })
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "explicit-reconnect"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void KeepsConnectionForMatchingExplicitPreviousResponseId()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var first = conn.ReadJson();
            Assert.False(first.TryGetProperty("previous_response_id", out _));
            conn.WriteJson(Completed("resp_1"));

            var second = conn.ReadJson();
            Assert.Equal("resp_1", second.GetProperty("previous_response_id").GetString());
            var input = second.GetProperty("input");
            Assert.Equal(1, input.GetArrayLength());
            Assert.Equal("tool_1", input[0].GetProperty("id").GetString());
            conn.WriteJson(Completed("resp_2"));
        };

        var executor = new WebSocketExecutor(inner: null);
        foreach (var body in new[]
        {
            """{"model":"gpt-5","input":[{"id":"user_1","type":"message","role":"user"}]}""",
            """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"id":"tool_1","type":"function_call_output","call_id":"call_1","output":"ok"}]}""",
        })
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "matching-explicit-previous"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(1, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void PreparePayloadChecksExplicitPreviousIdWithEmptyCachedInput()
    {
        var pooled = new PooledWebSocketConnection(default, conn: null, now: DateTime.UtcNow);
        pooled.SeedPreviousTurn([], "resp_cached");
        var lease = new WebSocketLease(conn: null, pooled: pooled, owner: null, reused: true);
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["model"] = JsonSerializer.SerializeToElement("gpt-5"),
            ["previous_response_id"] = JsonSerializer.SerializeToElement("resp_other"),
            ["input"] = JsonSerializer.SerializeToElement(new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = "next",
                },
            }),
        };

        var ex = Assert.Throws<WebSocketReconnectRequiredException>(
            () => WebSocketExecutor.PrepareWebSocketPayloadForLease(payload, lease));
        Assert.Equal(
            "websocket session context changed; reconnect and send full context",
            ex.Message);
    }

    [Fact]
    public void RestorePayloadMapRestoresMutatedPayload()
    {
        var originalInput = JsonSerializer.SerializeToElement(new[]
        {
            new Dictionary<string, object?> { ["id"] = "user_1", ["type"] = "message", ["role"] = "user" },
            new Dictionary<string, object?> { ["id"] = "user_2", ["type"] = "message", ["role"] = "user" },
        });
        var payload = new Dictionary<string, JsonElement?>(StringComparer.Ordinal)
        {
            ["model"] = JsonSerializer.SerializeToElement("gpt-5"),
            ["input"] = originalInput,
        };
        var original = WebSocketExecutor.ClonePayloadMap(payload);

        payload["input"] = JsonSerializer.SerializeToElement(new[]
        {
            new Dictionary<string, object?> { ["id"] = "user_2", ["type"] = "message", ["role"] = "user" },
        });
        payload["previous_response_id"] = JsonSerializer.SerializeToElement("resp_1");
        WebSocketExecutor.RestorePayloadMap(payload, original);

        Assert.Equal("gpt-5", payload["model"]!.Value.GetString());
        Assert.True(JsonElement.DeepEquals(originalInput, payload["input"]!.Value));
        Assert.False(payload.ContainsKey("previous_response_id"));
    }

    [Fact]
    public void ReconnectsWhenSuffixStartsWithAssistantOutput()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var payload = conn.ReadJson();
            Assert.False(payload.TryGetProperty("previous_response_id", out _));
            if (conn.UpgradeIndex == 1)
            {
                conn.WriteJson(Completed("resp_1"));
                conn.WaitUntilClosed();
                return;
            }

            Assert.Equal(3, payload.GetProperty("input").GetArrayLength());
            conn.WriteJson(Completed("resp_2"));
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        foreach (var body in new[]
        {
            """{"model":"gpt-5","input":[{"id":"user_1","type":"message","role":"user"}]}""",
            """{"model":"gpt-5","input":[{"id":"user_1","type":"message","role":"user"},{"id":"assistant_1","type":"message","role":"assistant"},{"id":"user_2","type":"message","role":"user"}]}""",
        })
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "assistant-reconnect"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void StreamReturnsErrorWhenReusedConnectionClosesBeforeEvent()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var payload = conn.ReadJson();
            if (conn.UpgradeIndex == 1)
            {
                Assert.Equal(1, payload.GetProperty("input").GetArrayLength());
                conn.WriteJson(Completed("resp_1"));
                conn.WriteClose();
                conn.WaitUntilClosed();
            }
        };

        var executor = new WebSocketExecutor(inner: null);
        using (var stream = executor.DoStream(
            CancellationToken.None,
            Request(
                server,
                """{"model":"gpt-5","input":[{"id":"first","type":"message","role":"user"}]}""",
                sessionId: "stale-close")))
        {
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        // The pooled connection dies before the next turn answers; the reference
        // tolerates a failed write and only asserts the close error otherwise.
        IStream<StreamEvent> second;
        try
        {
            second = executor.DoStream(
                CancellationToken.None,
                Request(
                    server,
                    """{"model":"gpt-5","input":[{"id":"first","type":"message","role":"user"},{"id":"second","type":"message","role":"user"}]}""",
                    sessionId: "stale-close"));
        }
        catch (Exception)
        {
            Finish(server);
            return;
        }

        using (second)
        {
            Assert.False(second.Next());
            Assert.Contains(
                "websocket closed before response event",
                second.Err?.Message ?? string.Empty,
                StringComparison.Ordinal);
        }

        Finish(server);
    }

    [Fact]
    public void SeparatesPoolByOrganizationHeaders()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(CompletedEvent);
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        foreach (var org in new[] { "org-a", "org-b" })
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(
                    server,
                    """{"model":"gpt-5","input":[{"id":"first","type":"message"}]}""",
                    sessionId: "org-session",
                    organization: org));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    public static TheoryData<string, string> EvictingTerminalEvents => new()
    {
        { "response.failed", "failed" },
        { "response.cancelled", "cancelled" },
        { "response.incomplete", "incomplete" },
    };

    [Theory]
    [MemberData(nameof(EvictingTerminalEvents))]
    public void EvictsPooledConnectionOnFailedOrCancelled(string terminalType, string status)
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var payload = conn.ReadJson();
            Assert.False(payload.TryGetProperty("previous_response_id", out _));
            if (conn.UpgradeIndex == 1)
            {
                conn.WriteJson(
                    """{"type":""" + JsonSerializer.Serialize(terminalType)
                    + ""","response":{"id":"resp_bad","status":"""
                    + JsonSerializer.Serialize(status) + "}}");
                conn.WaitUntilClosed();
                return;
            }

            conn.WriteJson(Completed("resp_ok"));
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        var body = """{"model":"gpt-5","input":[{"id":"first","type":"message"}]}""";
        for (var i = 0; i < 2; i++)
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "terminal-evict-" + terminalType));
            Assert.True(stream.Next());
            Assert.Equal(i == 0 ? terminalType : "response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void SendsOnlyNewInputOnReusedSession()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var first = conn.ReadJson();
            var firstInput = first.GetProperty("input");
            Assert.Equal(1, firstInput.GetArrayLength());
            Assert.False(first.TryGetProperty("previous_response_id", out _));
            conn.WriteJson(
                """{"type":"response.completed","response":{"id":"resp_1","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[{"id":"fc_1","type":"function_call","call_id":"call_1","name":"run","arguments":"{}"}]}}""");

            var second = conn.ReadJson();
            Assert.Equal("resp_1", second.GetProperty("previous_response_id").GetString());
            var secondInput = second.GetProperty("input");
            Assert.Equal(1, secondInput.GetArrayLength());
            Assert.Equal("second", secondInput[0].GetProperty("id").GetString());
            conn.WriteJson(Completed("resp_2"));
        };

        var executor = new WebSocketExecutor(inner: null);
        var firstBody =
            """{"model":"gpt-5","input":[{"id":"first","type":"message","role":"user","content":[{"type":"input_text","text":"first"}]}]}""";
        var secondBody =
            """{"model":"gpt-5","input":[{"id":"first","type":"message","role":"user","content":[{"type":"input_text","text":"first"}]},{"id":"fc_1","type":"function_call","call_id":"call_1","name":"run","arguments":"{}"},{"id":"second","type":"message","role":"user","content":[{"type":"input_text","text":"second"}]}]}""";

        foreach (var body in new[] { firstBody, secondBody })
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "diff-session"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(1, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void KeepsConnectionWhenInputExceedsRetainedLimit()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            for (var i = 0; i < 2; i++)
            {
                var payload = conn.ReadJson();
                Assert.False(payload.TryGetProperty("previous_response_id", out _));
                Assert.Equal(1, payload.GetProperty("input").GetArrayLength());
                conn.WriteJson(Completed("resp_" + (i + 1), sequenceNumber: i + 1));
            }
        };

        var executor = new WebSocketExecutor(inner: null)
        {
            MaxRetainedInput = 1,
        };
        var body =
            """{"model":"gpt-5","input":[{"id":"large","type":"message","role":"user","content":[{"type":"input_text","text":"larger than retained limit"}]}]}""";

        for (var i = 0; i < 2; i++)
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "large-input-session"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(1, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void LeaseRemembersTheResponseIdWithoutCachedInput()
    {
        var pooled = new PooledWebSocketConnection(default, conn: null, now: DateTime.UtcNow);
        var lease = new WebSocketLease(conn: null, pooled: pooled, owner: null, reused: true);

        // websocket_retention.go:39-40: with no cached full input the lease
        // still remembers the response id, so the next turn agrees on it even
        // though it sends full context.
        Assert.True(lease.RememberTurn("resp_lease", []));

        var (input, responseId) = pooled.PreviousTurn();
        Assert.Empty(input);
        Assert.Equal("resp_lease", responseId);
    }

    [Fact]
    public void RetainsTheResponseIdWhenTheInputExceedsTheRetainedBudget()
    {
        var budget = new ResponsesRetainedBudget(16);
        var pooled = new PooledWebSocketConnection(default, conn: null, now: DateTime.UtcNow)
        {
            Budget = budget,
        };

        Assert.False(pooled.RememberTurn([BudgetItem(20)], "resp_1", maxInputBytes: 0));

        var (input, responseId) = pooled.PreviousTurn();
        Assert.Empty(input);
        Assert.Equal("resp_1", responseId);

        // The failed reservation released the budget for another connection.
        var next = new PooledWebSocketConnection(default, conn: null, now: DateTime.UtcNow)
        {
            Budget = budget,
        };
        Assert.True(next.RememberTurn([BudgetItem(12)], "resp_2", maxInputBytes: 0));

        pooled.Close();
        next.Close();
    }

    [Fact]
    public void SharesTheRetainedBudgetAcrossPooledConnections()
    {
        var budget = new ResponsesRetainedBudget(16);
        var first = new PooledWebSocketConnection(default, conn: null, now: DateTime.UtcNow)
        {
            Budget = budget,
        };
        var second = new PooledWebSocketConnection(default, conn: null, now: DateTime.UtcNow)
        {
            Budget = budget,
        };

        Assert.True(first.RememberTurn([BudgetItem(12)], "resp_1", maxInputBytes: 0));
        Assert.False(second.RememberTurn([BudgetItem(12)], "resp_2", maxInputBytes: 0));

        // Closing a connection releases what it retained (responses_lane.go's
        // budget Replace(retainedBytes, 0)).
        first.Close();
        Assert.True(second.RememberTurn([BudgetItem(12)], "resp_2", maxInputBytes: 0));

        second.Close();
    }

    private static JsonElement BudgetItem(int bytes)
        => JsonSerializer.SerializeToElement(new string('x', bytes - 2));

    [Fact]
    public void ReconnectsWhenInputIsNotAppendOnly()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            var payload = conn.ReadJson();
            var input = payload.GetProperty("input");
            Assert.Equal(1, input.GetArrayLength());
            Assert.False(payload.TryGetProperty("previous_response_id", out _));

            if (conn.UpgradeIndex == 1)
            {
                conn.WriteJson(Completed("resp_1"));
                conn.ExpectClosed();
                return;
            }

            Assert.Equal("rewritten", input[0].GetProperty("id").GetString());
            conn.WriteJson(Completed("resp_rewritten"));
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        var firstBody =
            """{"model":"gpt-5","input":[{"id":"first","type":"message","role":"user","content":[{"type":"input_text","text":"first"}]}]}""";
        var rewrittenBody =
            """{"model":"gpt-5","input":[{"id":"rewritten","type":"message","role":"user","content":[{"type":"input_text","text":"rewritten"}]}]}""";

        foreach (var body in new[] { firstBody, rewrittenBody })
        {
            using var stream = executor.DoStream(
                CancellationToken.None,
                Request(server, body, sessionId: "rewrite-session"));
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void EvictsPooledConnectionOnEarlyClose()
    {
        using var payloadRead = new ManualResetEventSlim();
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            if (conn.UpgradeIndex == 1)
            {
                payloadRead.Set();
                conn.ExpectClosed();
                return;
            }

            conn.WriteJson(CompletedEvent);
            conn.WaitUntilClosed();
        };

        var executor = new WebSocketExecutor(inner: null);
        var first = executor.DoStream(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5"}""", sessionId: "session-early-close"));
        Assert.True(payloadRead.Wait(TimeSpan.FromSeconds(5)));
        first.Dispose();

        using var second = executor.DoStream(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5"}""", sessionId: "session-early-close"));
        Assert.True(second.Next());
        Assert.Equal("response.completed", second.Current?.Type);
        Assert.False(second.Next());
        Assert.Null(second.Err);

        Assert.Equal(2, server.Upgrades);
        Finish(server);
    }

    [Fact]
    public void HeaderPoolIdentityIgnoresPerRequestTraceHeaders()
    {
        var baseHeaders = Headers(
            ("X-Trace-Id", "trace-1"),
            ("X-Request-Id", "request-1"),
            ("Traceparent", "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01"),
            ("X-Custom-Routing", "stable"),
            ("OpenAI-Beta", WebSocketExecutor.BetaHeaderValue),
            (WebSocketExecutor.SessionHeader, "session-1"));

        var changedTrace = Clone(baseHeaders);
        HttpClientUtils.HeaderSet(changedTrace, "X-Trace-Id", "trace-2");
        HttpClientUtils.HeaderSet(changedTrace, "X-Request-Id", "request-2");
        HttpClientUtils.HeaderSet(
            changedTrace,
            "Traceparent",
            "00-cccccccccccccccccccccccccccccccc-dddddddddddddddd-01");

        var reKeyed = Clone(baseHeaders);
        HttpClientUtils.HeaderSet(reKeyed, "X-Custom-Routing", "other");

        Assert.Equal(
            WebSocketExecutor.HeaderPoolIdentity(baseHeaders),
            WebSocketExecutor.HeaderPoolIdentity(changedTrace));
        Assert.NotEqual(
            WebSocketExecutor.HeaderPoolIdentity(baseHeaders),
            WebSocketExecutor.HeaderPoolIdentity(reKeyed));
    }

    // ------------------------------------------------------------------
    // Cleanup and shutdown
    // ------------------------------------------------------------------

    [Fact]
    public void BackgroundCleanupClosesIdleConnections()
    {
        using var connectionClosed = new ManualResetEventSlim();
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(Completed("resp_cleanup"));
            conn.WaitUntilClosed();
            connectionClosed.Set();
        };

        var executor = new WebSocketExecutor(inner: null)
        {
            IdleTtl = TimeSpan.FromMilliseconds(20),
            MaxLifetime = TimeSpan.FromHours(1),
        };
        using (var stream = executor.DoStream(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5"}""", sessionId: "cleanup-session")))
        {
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        var closed = connectionClosed.Wait(TimeSpan.FromSeconds(5));
        Assert.True(
            closed,
            "idle websocket connection was not closed by background cleanup; pool="
            + executor.Pool.Count
            + " scheduled="
            + executor.CleanupScheduled);

        Finish(server);
    }

    [Fact]
    public void CloseClosesIdleConnections()
    {
        using var connectionClosed = new ManualResetEventSlim();
        using var server = new ResponsesWebSocketTestServer();
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(Completed("resp_close"));
            conn.WaitUntilClosed();
            connectionClosed.Set();
        };

        var executor = new WebSocketExecutor(inner: null);
        using (var stream = executor.DoStream(
            CancellationToken.None,
            Request(server, """{"model":"gpt-5"}""", sessionId: "close-session")))
        {
            Assert.True(stream.Next());
            Assert.Equal("response.completed", stream.Current?.Type);
            Assert.False(stream.Next());
            Assert.Null(stream.Err);
        }

        executor.Close();

        Assert.True(
            connectionClosed.Wait(TimeSpan.FromSeconds(5)),
            "idle websocket connection was not closed by executor Close");
        Assert.Empty(executor.Pool);
        Assert.False(executor.CleanupScheduled);

        Finish(server);
    }

    // ------------------------------------------------------------------
    // Event normalization, URL mapping and handshake observation
    // ------------------------------------------------------------------

    [Fact]
    public void NormalizeWebSocketEventFlattensNestedError()
    {
        var raw =
            """{"type":"error","status":400,"error":{"type":"invalid_request_error","message":"bad request","param":"model","code":"bad_model"}}""";

        var normalized = WebSocketExecutor.NormalizeWebSocketEvent(Encoding.UTF8.GetBytes(raw));

        using var document = JsonDocument.Parse(normalized);
        var payload = document.RootElement;
        Assert.Equal("error", payload.GetProperty("type").GetString());
        Assert.Equal("bad_model", payload.GetProperty("code").GetString());
        Assert.Equal("bad request", payload.GetProperty("message").GetString());
        Assert.Equal("model", payload.GetProperty("param").GetString());
    }

    [Fact]
    public void NormalizeWebSocketEventPreservesFlattenedFields()
    {
        var raw =
            """{"type":"error","status":400,"code":"invalid_value","message":"flat message","error":{"type":"invalid_request_error","message":"nested message"}}""";

        var normalized = WebSocketExecutor.NormalizeWebSocketEvent(Encoding.UTF8.GetBytes(raw));

        using var document = JsonDocument.Parse(normalized);
        var payload = document.RootElement;
        Assert.Equal("invalid_value", payload.GetProperty("code").GetString());
        Assert.Equal("flat message", payload.GetProperty("message").GetString());
    }

    [Fact]
    public void ToWebSocketUrlMapsHttpSchemes()
    {
        Assert.Equal(
            "wss://api.openai.com/v1/responses",
            WebSocketExecutor.ToWebSocketUrl("https://api.openai.com/v1/responses"));
        Assert.Equal(
            "ws://localhost:8080/v1/responses",
            WebSocketExecutor.ToWebSocketUrl("http://localhost:8080/v1/responses"));
    }

    [Fact]
    public void ObservesHandshakeHeaders()
    {
        using var server = new ResponsesWebSocketTestServer();
        server.ExtraHandshakeHeaders["X-Handshake-ID"] = "review";
        server.Handler = conn =>
        {
            _ = conn.ReadJson();
            conn.WriteJson(
                """{"type":"response.completed","response":{"id":"resp_review","object":"response","status":"completed","output":[]}}""");
            conn.WaitUntilClosed();
        };

        var observed = 0;
        var executor = new WebSocketExecutor(inner: null);
        using var stream = executor.DoStream(
            CancellationToken.None,
            Request(
                server,
                """{"model":"gpt-5","stream":true}""",
                onResponseHeaders: headers =>
                {
                    observed++;
                    Assert.Equal("review", headers["X-Handshake-Id"][0]);
                }));
        while (stream.Next())
        {
            _ = stream.Current;
        }

        Assert.Null(stream.Err);
        Assert.Equal(1, observed);

        executor.Close();
        Finish(server);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static HttpRequestData Request(
        ResponsesWebSocketTestServer server,
        string body,
        string? sessionId = null,
        string? organization = null,
        Action<IReadOnlyDictionary<string, List<string>>>? onResponseHeaders = null)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (sessionId is not null)
        {
            HttpClientUtils.HeaderSet(headers, WebSocketExecutor.SessionHeader, sessionId);
        }

        if (organization is not null)
        {
            HttpClientUtils.HeaderSet(headers, WebSocketExecutor.OrgHeader, organization);
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = server.Url + "/v1/responses",
            Headers = headers,
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = "test-key" },
            Body = Encoding.UTF8.GetBytes(body),
            SessionScope = Scope,
            OnResponseHeaders = onResponseHeaders,
        };
    }

    private static Dictionary<string, List<string>> Headers(params (string Name, string Value)[] values)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            HttpClientUtils.HeaderSet(headers, name, value);
        }

        return headers;
    }

    private static Dictionary<string, List<string>> Clone(Dictionary<string, List<string>> headers)
    {
        var clone = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            clone[key] = [.. values];
        }

        return clone;
    }

    private static string Completed(
        string responseId,
        string outputJson = "[]",
        int sequenceNumber = 1,
        long createdAt = 1700000000)
        => """{"type":"response.completed","sequence_number":"""
            + sequenceNumber
            + ""","response":{"id":"""
            + JsonSerializer.Serialize(responseId)
            + ""","object":"response","created_at":"""
            + createdAt
            + ""","model":"gpt-5","status":"completed","output":"""
            + outputJson
            + "}}";

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "JSON mismatch\nexpected: " + expected + "\nactual: " + actual);
    }

    private static void Finish(ResponsesWebSocketTestServer server)
    {
        server.Dispose();
        server.AssertNoHandlerErrors();
    }

    private sealed class StubExecutor : IExecutor
    {
        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => new() { StatusCode = 200 };

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => new SliceStream<StreamEvent>([]);
    }
}
