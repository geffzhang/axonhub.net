using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/responses/http_executor_test.go: the HTTP/SSE
/// transport wrapper strips WebSocket-only request metadata while preserving
/// continuation fields, and never mutates the request retained by the pipeline.
/// </summary>
public sealed class ResponsesHttpTransportExecutorTests
{
    [Fact]
    public void HTTPTransportExecutorStripsWebSocketMetadataAndKeepsContinuation()
    {
        var outbound = new ResponsesOutboundTransformer(new ResponsesOutboundConfig
        {
            BaseUrl = "https://api.example.com/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
            Transport = ResponsesOutboundTransformer.TransportHttp,
        });

        var inner = new CaptureExecutor();
        var executor = outbound.CustomizeExecutor(inner);
        var request = new HttpRequestData
        {
            Headers = Headers(("OpenAI-Beta", WebSocketExecutor.BetaHeaderValue + ", other_beta=v1")),
            Body = Encoding.UTF8.GetBytes(
                """{"model":"gpt-5","previous_response_id":"resp_ws","stream":true,"input":"hello"}"""),
        };

        using (var stream = executor.DoStream(CancellationToken.None, request))
        {
            Assert.Null(stream.Err);
        }

        Assert.NotNull(inner.Request);
        Assert.Equal(
            "other_beta=v1",
            HttpClientUtils.HeaderGet(inner.Request!.Headers, "OpenAI-Beta"));
        using (var body = JsonDocument.Parse(inner.Request!.Body!))
        {
            var root = body.RootElement;
            Assert.Equal("resp_ws", root.GetProperty("previous_response_id").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal("hello", root.GetProperty("input").GetString());
        }

        // Transport cleanup must not mutate the request retained by the pipeline.
        Assert.Equal(
            WebSocketExecutor.BetaHeaderValue + ", other_beta=v1",
            HttpClientUtils.HeaderGet(request.Headers, "OpenAI-Beta"));
        using (var body = JsonDocument.Parse(request.Body!))
        {
            Assert.Equal("resp_ws", body.RootElement.GetProperty("previous_response_id").GetString());
        }
    }

    [Fact]
    public void PrepareHTTPTransportRequestKeepsContinuationWithoutInput()
    {
        var request = new HttpRequestData
        {
            Headers = Headers(("OpenAI-Beta", WebSocketExecutor.BetaHeaderValue)),
            Body = Encoding.UTF8.GetBytes(
                """{"model":"gpt-5","previous_response_id":"resp_ws","stream":true}"""),
        };

        var prepared = ResponsesHttpTransport.PrepareHttpTransportRequest(request, false);
        Assert.NotNull(prepared);
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(prepared!.Headers, "OpenAI-Beta"));
        using var body = JsonDocument.Parse(prepared.Body!);
        Assert.Equal("resp_ws", body.RootElement.GetProperty("previous_response_id").GetString());
        Assert.False(body.RootElement.TryGetProperty("input", out _));
    }

    [Fact]
    public void HTTPTransportExecutorKeepsNormalHTTPContinuation()
    {
        var outbound = new ResponsesOutboundTransformer(new ResponsesOutboundConfig
        {
            BaseUrl = "https://api.example.com/v1",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var inner = new CaptureExecutor();
        var executor = outbound.CustomizeExecutor(inner);
        var request = new HttpRequestData
        {
            Headers = Headers(("OpenAI-Beta", "other_beta=v1")),
            Body = Encoding.UTF8.GetBytes(
                """{"model":"gpt-5","previous_response_id":"resp_http","stream":true}"""),
        };

        using (var stream = executor.DoStream(CancellationToken.None, request))
        {
            Assert.Null(stream.Err);
        }

        Assert.Same(request, inner.Request);
        Assert.Equal(
            "other_beta=v1",
            HttpClientUtils.HeaderGet(inner.Request!.Headers, "OpenAI-Beta"));
        using var body = JsonDocument.Parse(inner.Request!.Body!);
        Assert.Equal("resp_http", body.RootElement.GetProperty("previous_response_id").GetString());
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

    private sealed class CaptureExecutor : IExecutor
    {
        public HttpRequestData? Request { get; private set; }

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        {
            Request = request;
            return new HttpResponseData { StatusCode = 200 };
        }

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
        {
            Request = request;
            return new SliceStream<StreamEvent>([]);
        }
    }
}
