using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/codex/outbound_executor_test.go. The
/// reference stands up <c>httptest.Server</c> instances; the port drives the
/// same code through <see cref="HttpExecutor"/> and
/// <see cref="CodexTestHttpHandler"/> so <c>BuildHttpRequest</c> still produces
/// the bytes the assertions inspect.
/// </summary>
public sealed class CodexOutboundExecutorTests
{
    private const string UpstreamJsonBody =
        """
        {
        	"id":"resp_json_123",
        	"object":"response",
        	"created_at":1700000000,
        	"status":"completed",
        	"model":"gpt-5.6-luna",
        	"output":[{
        		"id":"msg_json_123",
        		"type":"message",
        		"status":"completed",
        		"role":"assistant",
        		"content":[{"type":"output_text","text":"OK"}]
        	}]
        }
        """;

    private static readonly string[] Formats =
    [
        ApiFormats.OpenAIImageGeneration,
        ApiFormats.OpenAIImageEdit,
    ];

    [Fact]
    public void TestCodexOutbound_StreamAcceptHeader()
    {
        var accessToken = CodexTestSupport.TestAccessTokenWithAccountId();
        var handler = NewSseHandler();
        using var httpClient = new HttpClient(handler);

        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://upstream.example");

        var request = CodexTestSupport.BuildCodexStreamRequest(outbound, withInboundIdentity: false);
        HttpClientUtils.HeaderSet(request.Headers!, CodexHeaders.TurnStateHeader, "ts-1");

        var executor = new HttpExecutor(httpClient);
        using var stream = executor.DoStream(CancellationToken.None, request);

        Assert.Equal("text/event-stream", handler.LastHeader("Accept"));
        Assert.Equal("application/json", handler.LastHeader("Content-Type"));
        Assert.Equal(CodexConstants.AxonHubOriginator, handler.LastHeader("Originator"));
        Assert.Equal(CodexConstants.DefaultUserAgent, handler.LastHeader("User-Agent"));
        Assert.Equal(CodexTestSupport.TestChatAccountId, handler.LastHeader("Chatgpt-Account-Id"));
        Assert.Equal("Bearer " + accessToken, handler.LastHeader("Authorization"));
        Assert.Equal("ts-1", handler.LastHeader(CodexHeaders.TurnStateHeader));
    }

    [Fact]
    public void TestCodexOutbound_TurnStateHeaderPassesThrough()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var inboundRequest = CodexTestSupport.NewInboundRequest(
            """{"model":"gpt-5-codex","stream":true,"messages":[{"role":"user","content":"hello"}]}""",
            (CodexHeaders.TurnStateHeader, "ts-1"));

        var inbound = new OpenAiInboundTransformer().TransformRequest(inboundRequest);
        inbound.RawRequest = inboundRequest;

        var outboundRequest = outbound.TransformRequest(inbound);
        outboundRequest = HttpClientUtils.MergeInboundRequest(outboundRequest, inboundRequest);

        Assert.Equal(
            "ts-1",
            HttpClientUtils.HeaderGet(outboundRequest.Headers, CodexHeaders.TurnStateHeader));
    }

    [Theory]
    [InlineData(
        "max_output_tokens",
        """{"model":"gpt-5.4-mini","input":"hi","stream":true,"max_output_tokens":12000}""")]
    [InlineData(
        "max_completion_tokens",
        """{"model":"gpt-5.4-mini","input":"hi","stream":true,"max_completion_tokens":12000}""")]
    [InlineData(
        "max_tokens",
        """{"model":"gpt-5.4-mini","input":"hi","stream":true,"max_tokens":12000}""")]
    public void TestCodexOutbound_RejectsPassThroughBodyWithTokenLimitFields(string name, string body)
    {
        _ = name;

        var outbound = NewPassThroughOutbound();
        var llmRequest = new LlmRequest
        {
            ApiFormat = ApiFormats.OpenAIResponse,
            RawRequest = new HttpRequestData { Body = Encoding.UTF8.GetBytes(body) },
        };

        Assert.False(outbound.AllowPassThroughBody(llmRequest, new HttpRequestData()));
    }

    [Fact]
    public void TestCodexOutbound_AllowsPassThroughBodyWithoutTokenLimitFields()
    {
        var outbound = NewPassThroughOutbound();
        var llmRequest = new LlmRequest
        {
            ApiFormat = ApiFormats.OpenAIResponse,
            RawRequest = new HttpRequestData
            {
                Body = Encoding.UTF8.GetBytes(
                    """{"model":"gpt-5.4-mini","input":"hi","stream":true}"""),
            },
        };

        Assert.True(outbound.AllowPassThroughBody(llmRequest, new HttpRequestData()));
    }

    // Differential corpus recorded by running gjson v1.18.0 (the reference's
    // dependency) over every body: gjson.GetBytes(body, field).Exists() for
    // max_output_tokens, max_completion_tokens and max_tokens. The body is
    // allowed through when none of the three probes hits. The expected value
    // is the recorded reference result, not the port's behaviour.
    [Theory]
    [InlineData(
        """{"model":"gpt-5.4-mini","input":"hi","stream":true,"max_tokens":12000}""",
        false)]
    [InlineData("""{"model":"gpt-5.4-mini","input":"hi","stream":true}""", true)]
    [InlineData("""{"max_tokens":12000""", false)]
    [InlineData("{\"max_tokens\"", true)]
    [InlineData("""{"max_tokens":null}""", false)]
    [InlineData("""{"max_tokens":{"a":1}}""", false)]
    [InlineData("""{"max_tokens":"abc""", true)]
    [InlineData("""{"prompt":"max_tokens"}""", true)]
    [InlineData("""{"a":{"max_tokens":12000}}""", true)]
    [InlineData("""oops {"max_tokens":12000}""", false)]
    [InlineData("""[{"max_tokens":12000}]""", true)]
    [InlineData("""{"a":1 "max_tokens":12000}""", false)]
    [InlineData("""{"max_tokens" 12000}""", false)]
    [InlineData("{\"max\\u005ftokens\":12000}", false)]
    [InlineData("""{"a":"unterminated""", true)]
    [InlineData("""{"max_tokens":true}""", false)]
    [InlineData("""{max_tokens:12000}""", true)]
    [InlineData("""{"max_tokens"} 5""", false)]
    [InlineData("""{"max_tokens"}""", true)]
    [InlineData("""{"x", "max_tokens": 1}""", true)]
    [InlineData("{\"max_tokens\":\"", true)]
    [InlineData("""{"max_tokens":""", true)]
    [InlineData("""{"max_tokens":nul}""", false)]
    [InlineData("\"text {\\\"max_tokens\\\": 1}\"", true)]
    [InlineData("""{"a":1,"max_tokens":2}xyz""", false)]
    [InlineData("""{"max_tokens":12000}{"x":1}""", false)]
    [InlineData("""{"a": [1,2, "max_tokens": 3]}""", true)]
    [InlineData("""{"a":1,""", true)]
    [InlineData("""{"max_completion_tokens": 5, "max_tokens": 6}""", false)]
    [InlineData("", true)]
    [InlineData("""{}""", true)]
    [InlineData("""{"max_output_tokens":1}""", false)]
    [InlineData("""{"max_tokens": 1}x""", false)]
    [InlineData("{\"max_tokens\": \"x\", \"a\"", false)]
    [InlineData("""{"max_tokens": [1,2""", false)]
    [InlineData("""{"max_tokens": false}""", false)]
    [InlineData("""{"max_tokens": -3}""", false)]
    [InlineData("""{"max_tokens": NaN}""", false)]
    public void TestCodexOutbound_PassThroughBodyMatchesTheGjsonProbe(string body, bool allowed)
    {
        var outbound = NewPassThroughOutbound();
        var llmRequest = new LlmRequest
        {
            ApiFormat = ApiFormats.OpenAIResponse,
            RawRequest = new HttpRequestData { Body = Encoding.UTF8.GetBytes(body) },
        };

        Assert.Equal(allowed, outbound.AllowPassThroughBody(llmRequest, new HttpRequestData()));
    }

    [Fact]
    public void TestCodexOutbound_PassThroughBodyFindsKeysAfterUtf8Bom()
    {
        // gjson scans for the first '{' anywhere in the bytes, so a UTF-8 BOM
        // in front of the object does not hide the key.
        var outbound = NewPassThroughOutbound();
        var body = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("""{"max_tokens":1}"""))
            .ToArray();
        var llmRequest = new LlmRequest
        {
            ApiFormat = ApiFormats.OpenAIResponse,
            RawRequest = new HttpRequestData { Body = body },
        };

        Assert.False(outbound.AllowPassThroughBody(llmRequest, new HttpRequestData()));
    }

    [Fact]
    public void TestCodexOutbound_StreamAllowsDownstreamIdentityOverrides()
    {
        var accessToken = CodexTestSupport.TestAccessTokenWithAccountId();
        var handler = NewSseHandler();
        using var httpClient = new HttpClient(handler);

        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://upstream.example");

        var request = CodexTestSupport.BuildCodexStreamRequest(outbound, withInboundIdentity: true);

        var executor = new HttpExecutor(httpClient);
        using var stream = executor.DoStream(CancellationToken.None, request);

        Assert.Equal(CodexTestSupport.LegacyCodexOriginator(), handler.LastHeader("Originator"));
        // Only Originator is an inbound identity override; the transformer
        // owns User-Agent and never copies the client's.
        Assert.Equal(CodexConstants.DefaultUserAgent, handler.LastHeader("User-Agent"));
        Assert.NotEqual(CodexTestSupport.LegacyCodexUserAgent(), handler.LastHeader("User-Agent"));

        Assert.Equal(CodexTestSupport.TestChatAccountId, handler.LastHeader("Chatgpt-Account-Id"));
        Assert.Equal("Bearer " + accessToken, handler.LastHeader("Authorization"));
    }

    [Fact]
    public void TestCodexOutbound_RejectsImageRequestsWithoutNativeEndpoint()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var ex = Assert.Throws<TransformerException>(() => outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-image-2",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest { Prompt = "draw a tree" },
        }));

        Assert.Contains(
            "native Images endpoint",
            ex.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TestCodexImageOutbound_BuildsNativeGenerationRequest()
    {
        var accessToken = CodexTestSupport.TestAccessTokenWithAccountId();
        var outbound = new CodexImageOutboundTransformer(new CodexImageParams
        {
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = new OAuthCredentials { AccessToken = accessToken },
            },
            BaseUrl = "https://relay.example/v1",
        });

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-image-2",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageGeneration,
            Image = new ImageRequest { Prompt = "draw a tree" },
        });

        Assert.Equal("https://relay.example/v1/images/generations", request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(accessToken, request.Auth.ApiKey);
        Assert.Equal(CodexConstants.AxonHubOriginator, HttpClientUtils.HeaderGet(request.Headers, "Originator"));
        Assert.NotEmpty(HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(
            CodexTestSupport.TestChatAccountId,
            HttpClientUtils.HeaderGet(request.Headers, "Chatgpt-Account-Id"));
        var body = CodexJson.Root(request.Body!);
        Assert.Equal("gpt-image-2", CodexJson.PathString(body, "model"));
        Assert.Equal("draw a tree", CodexJson.PathString(body, "prompt"));
    }

    [Fact]
    public void TestCodexImageOutbound_BuildsJsonEditWithFileReferences()
    {
        var outbound = new CodexImageOutboundTransformer(new CodexImageParams
        {
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = new OAuthCredentials
                {
                    AccessToken = CodexTestSupport.TestAccessTokenWithAccountId(),
                },
            },
            BaseUrl = "https://relay.example/v1",
        });

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-image-2",
            RequestType = RequestTypes.Image,
            ApiFormat = ApiFormats.OpenAIImageEdit,
            Image = new ImageRequest
            {
                Prompt = "replace the background",
                ImageReferences =
                [
                    new ImageReference { FileId = "file-1" },
                    new ImageReference { ImageUrl = "https://example.org/frame.png" },
                ],
            },
        });

        Assert.Equal("https://relay.example/v1/images/edits", request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        var body = CodexJson.Root(request.Body!);
        Assert.Equal("replace the background", CodexJson.PathString(body, "prompt"));
        Assert.Equal("file-1", CodexJson.PathString(body, "images", 0, "file_id"));
        Assert.Equal(
            "https://example.org/frame.png",
            CodexJson.PathString(body, "images", 1, "image_url"));
        Assert.False(body.TryGetProperty("mask", out _));
    }

    [Fact]
    public void TestCodexOutbound_TransformRequestStripsUserField()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5.6-luna",
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            Stream = true,
            User = "user-123",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("hello") },
            ],
        });

        Assert.False(
            CodexJson.Has(CodexJson.Root(request.Body!), "user"),
            "Codex backend rejects the user field with a 400 Bad Request; it must not be sent upstream");
    }

    [Fact]
    public void TestCodexOutbound_TransformImageResponse()
    {
        var upstream = new ResponsesResponse
        {
            Id = "resp_image",
            CreatedAt = 1760000000,
            Model = CodexConstants.DefaultImageMainModel,
            Output =
            [
                new ResponsesItem { Type = "image_generation_call", Result = "iVBORw0KGgo=" },
            ],
        };

        var response = ResponsesImageConvert.BuildImageResponse(
            upstream,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["codex_image_output_format"] = JsonSerializer.SerializeToElement("png"),
                ["codex_image_quality"] = JsonSerializer.SerializeToElement("high"),
                ["codex_image_size"] = JsonSerializer.SerializeToElement("1024x1024"),
                ["codex_image_model"] = JsonSerializer.SerializeToElement("gpt-image-2"),
            });

        Assert.Equal(RequestTypes.Image, response.RequestType);
        Assert.Equal("gpt-image-2", response.Model);
        Assert.NotNull(response.Image);
        Assert.Equal("png", response.Image!.OutputFormat);
        Assert.Equal("high", response.Image.Quality);
        Assert.Equal("1024x1024", response.Image.Size);
        Assert.Single(response.Image.Data);
        Assert.Equal("iVBORw0KGgo=", response.Image.Data[0].B64JSON);
    }

    [Fact]
    public void TestCodexOutbound_CustomizeExecutorUsesCurrentExecutor()
    {
        var outbound = NewWebSocketOutbound();
        var firstClient = new CodexMockExecutor();
        var secondClient = new CodexMockExecutor();

        var first = (CodexOutboundTransformer.CodexExecutor)outbound.CustomizeExecutor(firstClient);
        var firstInner = Assert.IsType<WebSocketExecutor>(first.Inner);

        var second = (CodexOutboundTransformer.CodexExecutor)outbound.CustomizeExecutor(secondClient);
        var secondInner = Assert.IsType<WebSocketExecutor>(second.Inner);

        var again = (CodexOutboundTransformer.CodexExecutor)outbound.CustomizeExecutor(firstClient);
        var againInner = Assert.IsType<WebSocketExecutor>(again.Inner);

        Assert.NotSame(firstInner, secondInner);
        Assert.Same(firstInner, againInner);
        Assert.Same(firstClient, firstInner.Inner());
        Assert.Same(secondClient, secondInner.Inner());
        Assert.Same(firstInner, first.Executor());
    }

    [Fact]
    public void TestCodexOutbound_DownstreamTransportDoesNotOverrideOfficialUpstreamTransport()
    {
        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://chatgpt.com/backend-api/codex#");

        var inner = new CodexMockExecutor();
        var executor = (CodexOutboundTransformer.CodexExecutor)outbound.CustomizeExecutor(inner);

        // Go re-checks with shared.WithResponsesWebSocket(ctx); the context flag
        // has no port equivalent (difference C094), so the downstream hint is
        // observed through the single Executor() accessor.
        Assert.Same(inner, executor.Executor());
    }

    [Fact]
    public void TestCodexOutbound_DownstreamResponsesWebSocketKeepsThirdPartyTransport()
    {
        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://api.fenno.ai");

        var inner = new CodexMockExecutor();
        var executor = (CodexOutboundTransformer.CodexExecutor)outbound.CustomizeExecutor(inner);

        Assert.Same(inner, executor.Executor());
    }

    [Theory]
    [InlineData("websocket beta header present", true)]
    [InlineData("websocket beta header already absent", false)]
    public void TestCodexOutbound_HTTPTransportStripsWebSocketOnlyFields(string name, bool hasBeta)
    {
        _ = name;

        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://chatgpt.com/backend-api/codex#");

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (hasBeta)
        {
            HttpClientUtils.HeaderSet(
                headers,
                "Openai-Beta",
                WebSocketExecutor.BetaHeaderValue + ", other_beta=v1");
        }

        var inner = new CodexMockExecutor();
        var executor = outbound.CustomizeExecutor(inner);
        var request = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(
                """{"model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"message","role":"user","content":"hello"}]}"""),
        };

        using (var stream = executor.DoStream(CancellationToken.None, request))
        {
            _ = stream;
        }

        Assert.NotNull(inner.Request);
        var innerBody = CodexJson.Root(inner.Request!.Body!);
        Assert.Null(CodexJson.Path(innerBody, "previous_response_id"));
        Assert.Equal("hello", CodexJson.PathString(innerBody, "input", 0, "content"));
        Assert.Equal(
            "resp_1",
            CodexJson.PathString(CodexJson.Root(request.Body!), "previous_response_id"));

        if (hasBeta)
        {
            Assert.Equal(
                "other_beta=v1",
                HttpClientUtils.HeaderGet(inner.Request.Headers, "OpenAI-Beta"));
            Assert.Equal(
                WebSocketExecutor.BetaHeaderValue + ", other_beta=v1",
                HttpClientUtils.HeaderGet(request.Headers, "OpenAI-Beta"));
        }
    }

    [Fact]
    public void TestCodexOutbound_WebSocketTransportKeepsContinuationFields()
    {
        var outbound = NewWebSocketOutbound();

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Openai-Beta", WebSocketExecutor.BetaHeaderValue);

        var request = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(
                """{"model":"gpt-5","previous_response_id":"resp_1","input":[]}"""),
        };

        var inner = new CodexMockExecutor();
        var executor = new CodexOutboundTransformer.CodexExecutor(inner, inner, outbound);

        var prepared = executor.RequestForTransport(request);

        Assert.Same(request, prepared);
        Assert.Equal(
            "resp_1",
            CodexJson.PathString(CodexJson.Root(prepared.Body!), "previous_response_id"));
        Assert.Equal(
            WebSocketExecutor.BetaHeaderValue,
            HttpClientUtils.HeaderGet(prepared.Headers, "OpenAI-Beta"));
    }

    [Fact]
    public void TestCodexOutbound_CustomizeExecutorAggregatesNonStreamRequests()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var request = CodexTestSupport.BuildCodexStreamRequest(outbound, withInboundIdentity: false);

        var mock = new CodexMockExecutor
        {
            StreamEvents = CodexTestSupport.RelayStreamEvents("resp_test_123", "msg_test_456"),
        };

        var executor = outbound.CustomizeExecutor(mock);
        var response = executor.Do(CancellationToken.None, request);

        Assert.Equal(0, mock.DoCalls);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));

        var body = CodexJson.Root(response.Body!);
        Assert.Equal("resp_test_123", CodexJson.PathString(body, "id"));
        Assert.Equal("completed", CodexJson.PathString(body, "status"));
        Assert.Equal("gpt-5-codex", CodexJson.PathString(body, "model"));
    }

    [Fact]
    public void TestCodexOutbound_CustomizeExecutorAlphaSearchUsesNonStreamingDo()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var request = new HttpRequestData { RequestType = RequestTypes.AlphaSearch };
        var mock = new CodexMockExecutor
        {
            StreamEvents = [CodexTestSupport.Event(string.Empty, """{"ok":true}""")],
        };

        var executor = outbound.CustomizeExecutor(mock);
        var response = executor.Do(CancellationToken.None, request);

        Assert.Equal(1, mock.DoCalls);
        Assert.Equal(
            "text/event-stream",
            HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));
    }

    [Fact]
    public void TestCodexOutbound_CustomizeExecutorAlphaSearchBypassesWebSocketTransport()
    {
        var outbound = NewWebSocketOutbound();
        var request = new HttpRequestData { RequestType = RequestTypes.AlphaSearch };
        var mock = new CodexMockExecutor
        {
            StreamEvents = [CodexTestSupport.Event(string.Empty, """{"ok":true}""")],
        };

        var executor = outbound.CustomizeExecutor(mock);
        _ = executor.Do(CancellationToken.None, request);

        Assert.Equal(1, mock.DoCalls);
        Assert.Equal(0, mock.DoStreamCalls);
    }

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/vnd.compat+json; charset=utf-8")]
    public void TestCodexOutbound_CustomizeExecutorPassesThroughJSONForNonStreamRequests(
        string contentType)
    {
        var accessToken = CodexTestSupport.TestAccessTokenWithAccountId();
        string? capturedAccept = null;
        var capturedStream = false;

        var handler = new CodexTestHttpHandler((request, _) =>
        {
            capturedAccept = request.Headers.TryGetValues("Accept", out var values)
                ? values.First()
                : null;

            var requestBody = request.Content!
                .ReadAsStringAsync(CancellationToken.None)
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            var streamValue = CodexJson.Path(
                CodexJson.Root(Encoding.UTF8.GetBytes(requestBody)),
                "stream");
            capturedStream = streamValue is { } element
                && element.ValueKind == JsonValueKind.True;

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(UpstreamJsonBody)),
            };
            response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            return response;
        });

        using var httpClient = new HttpClient(handler);

        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://relay.example.com/v1");

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5.6-luna",
            ApiFormat = ApiFormats.OpenAIResponse,
            Stream = false,
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Only reply OK") },
            ],
        });

        var executor = outbound.CustomizeExecutor(new HttpExecutor(httpClient));
        var response = executor.Do(CancellationToken.None, request);

        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("text/event-stream", capturedAccept);
        Assert.True(capturedStream, "Codex upstream requests remain stream-enabled");
        Assert.Equal(200, response.StatusCode);
        Assert.Equal(contentType, HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));
        AssertJsonEqual(UpstreamJsonBody, response.Body!);

        var llmResponse = outbound.TransformResponse(response);
        Assert.Single(llmResponse.Choices);
        Assert.NotNull(llmResponse.Choices[0].Message?.Content);
        Assert.Equal("OK", llmResponse.Choices[0].Message!.Content!.Content);
    }

    [Fact]
    public void TestCodexOutbound_CustomizeExecutorAggregatesRelaySSEForNonStreamRequests()
    {
        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://relay.example.com/v1");
        var request = CodexTestSupport.BuildCodexStreamRequest(outbound, withInboundIdentity: false);

        var mock = new CodexMockExecutor
        {
            StreamEvents = CodexTestSupport.RelayStreamEvents("resp_relay_123", "msg_relay_456"),
        };

        var executor = outbound.CustomizeExecutor(mock);
        var response = executor.Do(CancellationToken.None, request);

        Assert.Equal(1, mock.DoCalls);
        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(response.Headers, "Content-Type"));

        var body = CodexJson.Root(response.Body!);
        Assert.Equal("resp_relay_123", CodexJson.PathString(body, "id"));
        Assert.Equal("completed", CodexJson.PathString(body, "status"));
        Assert.Equal("gpt-5-codex", CodexJson.PathString(body, "model"));
    }

    [Fact]
    public void TestCodexOutbound_DoReturnsWebSocketErrorEvents()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var request = CodexTestSupport.BuildCodexStreamRequest(outbound, withInboundIdentity: false);

        var executor = outbound.CustomizeExecutor(new CodexMockExecutor
        {
            StreamEvents =
            [
                CodexTestSupport.Event(
                    "error",
                    """{"type":"error","code":"bad_request","message":"invalid websocket request"}"""),
            ],
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => executor.Do(CancellationToken.None, request));

        Assert.Contains(
            "bad_request: invalid websocket request",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TestNewCodexSSEResponseEncodesMultilineData()
    {
        var response = CodexTestSupport.NewCodexSseResponse(
        [
            new StreamEvent
            {
                Type = "response.created",
                LastEventId = "event_123",
                Data = Encoding.UTF8.GetBytes("{\r\n  \"type\": \"response.created\"\n}"),
            },
        ]);

        Assert.Equal(
            "event: response.created\nid: event_123\ndata: {\ndata:   \"type\": \"response.created\"\ndata: }\n\n",
            Encoding.UTF8.GetString(response.Body!));
    }

    [Fact]
    public void TestCodexOutbound_DoesNotInjectCLIInstructions()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5-codex",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
            Stream = true,
        });

        var root = CodexJson.Root(request.Body!);

        Assert.True(
            CodexJson.Has(root, "instructions"),
            "instructions field must always be present for Codex");
        Assert.Equal(string.Empty, CodexJson.PathString(root, "instructions"));

        var bodyText = Encoding.UTF8.GetString(request.Body!);
        Assert.DoesNotContain("You are a coding agent running in the Codex CLI", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Codex", bodyText, StringComparison.Ordinal);
        Assert.False(CodexJson.Path(root, "store")!.Value.GetBoolean());
    }

    [Fact]
    public void TestCodexOutbound_PreservesMinimalCompatTransforms()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5-codex",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "shell",
                        Parameters = JsonElement.Parse(
                            """{"type":"object","properties":{}}""").Clone(),
                    },
                },
            ],
            Store = true,
            ParallelToolCalls = false,
            MaxTokens = 128,
            MaxCompletionTokens = 256,
            TopP = 0.8,
            ServiceTier = "flex",
            ReasoningSummary = "detailed",
            Metadata = new Dictionary<string, string> { ["source"] = "caller" },
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        });

        var root = CodexJson.Root(request.Body!);

        Assert.False(CodexJson.Path(root, "store")!.Value.GetBoolean());
        Assert.True(CodexJson.Path(root, "stream")!.Value.GetBoolean());
        Assert.False(CodexJson.Has(root, "max_output_tokens"));
        Assert.True(CodexJson.Path(root, "parallel_tool_calls")!.Value.GetBoolean());
        Assert.Equal(0.8, CodexJson.Path(root, "top_p")!.Value.GetDouble());
        Assert.Equal("flex", CodexJson.PathString(root, "service_tier"));
        Assert.False(CodexJson.Has(root, "metadata"));

        var include = CodexJson.Path(root, "include");
        Assert.NotNull(include);
        Assert.Equal(JsonValueKind.Array, include!.Value.ValueKind);
        Assert.Equal(1, include.Value.GetArrayLength());
        Assert.Equal("reasoning.encrypted_content", include.Value[0].GetString());

        Assert.Equal("detailed", CodexJson.PathString(root, "reasoning", "summary"));

        var bodyText = Encoding.UTF8.GetString(request.Body!);
        Assert.DoesNotContain("You are a coding agent running in the Codex CLI", bodyText, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Codex", bodyText, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCodexOutbound_AppliesReasoningDefaultsWhenMissing()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5-codex",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
            Tools =
            [
                new Tool
                {
                    Type = "function",
                    Function = new Function
                    {
                        Name = "shell",
                        Parameters = JsonElement.Parse(
                            """{"type":"object","properties":{}}""").Clone(),
                    },
                },
            ],
        });

        var root = CodexJson.Root(request.Body!);

        Assert.True(CodexJson.Path(root, "parallel_tool_calls")!.Value.GetBoolean());

        var include = CodexJson.Path(root, "include");
        Assert.NotNull(include);
        Assert.Equal("reasoning.encrypted_content", include!.Value[0].GetString());

        Assert.Equal("auto", CodexJson.PathString(root, "reasoning", "summary"));
        Assert.Equal(string.Empty, CodexJson.PathString(root, "reasoning", "context"));
        Assert.False(CodexJson.Has(root, "metadata"));
    }

    [Fact]
    public void TestCodexOutbound_ResponsesLite_PlainRequestDoesNotFabricateLiteOrReasoningContext()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5-codex",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
            Stream = true,
        });

        Assert.Equal(
            string.Empty,
            HttpClientUtils.HeaderGet(request.Headers, ResponsesKeys.ResponsesLiteHeader));

        var root = CodexJson.Root(request.Body!);
        Assert.Equal(string.Empty, CodexJson.PathString(root, "reasoning", "context"));
    }

    [Fact]
    public void TestCodexOutbound_ResponsesLite_OfficialBackendPreservesClientSelectedLiteSemantics()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, ResponsesKeys.ResponsesLiteHeader, "true");
        var inboundRequest = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                	"model": "gpt-5.3-codex-spark",
                	"input": "Hello",
                	"stream": true,
                	"parallel_tool_calls": false,
                	"reasoning": {"effort": "high", "context": "current_turn"}
                }
                """),
        };

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var outboundRequest = outbound.TransformRequest(llmRequest);
        outboundRequest = HttpClientUtils.MergeInboundRequest(outboundRequest, inboundRequest);

        Assert.Equal(
            "true",
            HttpClientUtils.HeaderGet(outboundRequest.Headers, ResponsesKeys.ResponsesLiteHeader));

        var root = CodexJson.Root(outboundRequest.Body!);
        Assert.False(CodexJson.Path(root, "parallel_tool_calls")!.Value.GetBoolean());
        Assert.Equal("current_turn", CodexJson.PathString(root, "reasoning", "context"));
    }

    [Fact]
    public void TestCodexOutbound_ResponsesLite_RelayRemovesLiteWithoutRewritingClientReasoning()
    {
        var outbound = CodexTestSupport.NewTestCodexOutboundForBaseUrl("https://relay.example/v1");
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, ResponsesKeys.ResponsesLiteHeader, "true");
        var inboundRequest = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                	"model": "gpt-5.3-codex-spark",
                	"input": "Hello",
                	"stream": true,
                	"reasoning": {"context": "current_turn"}
                }
                """),
        };

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var outboundRequest = outbound.TransformRequest(llmRequest);

        Assert.Equal(
            string.Empty,
            HttpClientUtils.HeaderGet(outboundRequest.Headers, ResponsesKeys.ResponsesLiteHeader));

        var root = CodexJson.Root(outboundRequest.Body!);
        Assert.Equal("current_turn", CodexJson.PathString(root, "reasoning", "context"));
    }

    [Fact]
    public void TestCodexOutbound_ForcesArrayInputsForSingleMessage()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        // A single simple user message — without ArrayInputs=true this would be
        // serialized as a plain string "input". With the fix, it must be an array.
        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5-codex",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
            Stream = true,
        });

        var root = CodexJson.Root(request.Body!);
        var input = CodexJson.Path(root, "input");
        Assert.NotNull(input);
        Assert.Equal(JsonValueKind.Array, input!.Value.ValueKind);
        Assert.True(input.Value.GetArrayLength() > 0);

        var first = input.Value[0];
        Assert.Equal(JsonValueKind.Object, first.ValueKind);
        Assert.Equal("message", CodexJson.PathString(first, "type"));
        Assert.Equal("user", CodexJson.PathString(first, "role"));
    }

    [Fact]
    public void TestCodexOutbound_PreservesResponsesLiteRequirements()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, ResponsesKeys.ResponsesLiteHeader, "true");
        var inboundRequest = new HttpRequestData
        {
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(
                """
                {
                	"model": "gpt-5.6-sol",
                	"input": "Hello",
                	"stream": true,
                	"parallel_tool_calls": false,
                	"reasoning": {
                		"effort": "xhigh",
                		"context": "all_turns"
                	}
                }
                """),
        };

        var llmRequest = new ResponsesInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var outboundRequest = outbound.TransformRequest(llmRequest);
        outboundRequest = HttpClientUtils.MergeInboundRequest(outboundRequest, inboundRequest);

        Assert.Equal(
            "true",
            HttpClientUtils.HeaderGet(outboundRequest.Headers, ResponsesKeys.ResponsesLiteHeader));

        var root = CodexJson.Root(outboundRequest.Body!);
        Assert.False(CodexJson.Path(root, "parallel_tool_calls")!.Value.GetBoolean());
        Assert.Equal("all_turns", CodexJson.PathString(root, "reasoning", "context"));
    }

    // ------------------------------------------------------------------
    // Execution audit (C096)
    // ------------------------------------------------------------------

    [Fact]
    public void ApiKeyChannelAuditTracksTheTokenItSends()
    {
        var outbound = new CodexOutboundTransformer(new CodexOutboundParams
        {
            BaseUrl = "https://upstream.example",
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = CodexTestSupport.Credentials("sk-codex-key"),
            },
            AuditApiKey = true,
            ChannelApiKeyIndex = 2,
        });

        Assert.Null(outbound.SelectedApiKey);
        Assert.Equal(2, outbound.SelectedApiKeyIndex);

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5.4",
            ApiFormat = ApiFormats.OpenAIResponse,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        });

        Assert.Equal("sk-codex-key", outbound.SelectedApiKey);
        Assert.Equal("sk-codex-key", request.Auth?.ApiKey);
    }

    [Fact]
    public void OAuthChannelIsNotAudited()
    {
        var outbound = CodexTestSupport.NewTestCodexOutbound();

        outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-5.4",
            ApiFormat = ApiFormats.OpenAIResponse,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
        });

        Assert.Null(outbound.SelectedApiKey);
        Assert.Null(outbound.SelectedApiKeyIndex);
    }

    private static CodexOutboundTransformer NewPassThroughOutbound()
        => new(new CodexOutboundParams
        {
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = new OAuthCredentials { AccessToken = "test-token" },
            },
        });

    private static CodexOutboundTransformer NewWebSocketOutbound()
        => new(new CodexOutboundParams
        {
            BaseUrl = "wss://chatgpt.com/backend-api/codex#",
            Transport = ResponsesOutboundTransformer.TransportWebSocket,
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = CodexTestSupport.Credentials(CodexTestSupport.TestAccessTokenWithAccountId()),
            },
        });

    private static CodexTestHttpHandler NewSseHandler()
        => new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {}\n\n", Encoding.UTF8, "text/event-stream"),
        });

    private static ResponsesRequest DecodePayload(HttpRequestData request)
    {
        var payload = LlmJson.Deserialize<ResponsesRequest>(request.Body!);
        Assert.NotNull(payload);
        return payload!;
    }

    private static void AssertJsonEqual(string expected, byte[] actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "upstream JSON body must pass through unchanged");
    }
}
