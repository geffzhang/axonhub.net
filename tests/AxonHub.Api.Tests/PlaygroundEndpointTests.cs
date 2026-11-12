using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behaviour locks for the admin playground chat
/// (internal/server/api/playground.go: POST /admin/playground/chat). Covers the
/// request-level 400s, the playground HandleError classification, the AI SDK
/// data stream framing and the playground identity on the request row.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class PlaygroundEndpointTests(GrainClusterFixture fixture)
{
    private readonly FakeChannelWriteStore _channels = new();

    private const string ChatBody =
        """{"model":"play-model","messages":[{"role":"user","parts":[{"type":"text","text":"hi"}]}]}""";

    private const string StreamBody =
        """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"play-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":null}]}""";

    private const string FinishBody =
        """{"id":"chatcmpl-1","object":"chat.completion.chunk","created":1,"model":"play-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""";

    // ------------------------------------------------------------------
    // Request-level 400s (playground.go:213-285)
    // ------------------------------------------------------------------

    [Fact]
    public async Task EmptyBodyFailsInTheAiSdkInboundAsBadRequest()
    {
        // The playground has no chat.go-style empty-body check: the empty body
        // reaches the aisdk transformer, whose InvalidRequest maps to a bare
        // 400 "Bad Request" (playground.go:229-273).
        var context = NewContext(string.Empty);

        await PlaygroundEndpoint.HandleAsync(context, NewOrchestrator(new ScriptedExecutor()), _channels);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"code\":400,\"message\":\"Bad Request\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task InvalidProjectIdReturnsBadRequestEnvelope()
    {
        var context = NewContext(ChatBody, "?project_id=not-a-gid");

        await PlaygroundEndpoint.HandleAsync(context, NewOrchestrator(new ScriptedExecutor()), _channels);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"code\":400,\"message\":\"Invalid project ID: guid must start with gid://axonhub/\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task InvalidChannelIdReturnsBadRequestEnvelope()
    {
        var context = NewContext(ChatBody, "?channel_id=not-a-gid");

        await PlaygroundEndpoint.HandleAsync(context, NewOrchestrator(new ScriptedExecutor()), _channels);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":400,\"message\":\"guid must start with gid://axonhub/\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task UnknownChannelIsHiddenBehindInternalServerError()
    {
        var context = NewContext(ChatBody, "?channel_id=gid://axonhub/Channel/999");

        await PlaygroundEndpoint.HandleAsync(context, NewOrchestrator(new ScriptedExecutor()), _channels);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"code\":500,\"message\":\"Internal Server Error\"}}",
            ResponseBody(context));
    }

    // ------------------------------------------------------------------
    // Successful streaming chat (playground.go:295-327)
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamingChatWritesAiSdkDataStream()
    {
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(
            [
                TestChannels.Data(StreamBody),
                TestChannels.Data(FinishBody),
                TestChannels.Data("[DONE]"),
            ]),
        };
        var recorder = new RecordingRequestChainRecorder();
        var context = NewContext(
            ChatBody,
            "?channel_id=gid://axonhub/Channel/" + channel.Id
                + "&project_id=gid://axonhub/Project/7");

        await PlaygroundEndpoint.HandleAsync(
            context,
            NewOrchestrator(executor, recorder),
            _channels);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("keep-alive", context.Response.Headers["Connection"].ToString());
        Assert.Equal("*", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("v1", context.Response.Headers["X-Vercel-AI-Data-Stream"].ToString());

        var body = ResponseBody(context);
        Assert.Contains("\"type\":\"start\"", body, StringComparison.Ordinal);
        Assert.Contains("\"messageId\":\"chatcmpl-1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"text-start\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"text-delta\"", body, StringComparison.Ordinal);
        Assert.Contains("\"delta\":\"hi\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"finish\"", body, StringComparison.Ordinal);
        Assert.EndsWith("data:[DONE]\n\n", body, StringComparison.Ordinal);

        // The playground request row carries the JWT user as its source user,
        // no API key, and the project selected by the query override
        // (playground.go:246-268; request.go:279-283).
        var request = Assert.Single(recorder.Requests);
        Assert.Equal("playground", request.Source);
        Assert.Equal(7L, request.ProjectId);
        Assert.Equal(42L, request.UserId);
        Assert.Null(request.ApiKeyId);
        Assert.True(request.Stream);
    }

    [Fact]
    public async Task QueryChannelIdWinsOverHeader()
    {
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(
            [
                TestChannels.Data(StreamBody),
                TestChannels.Data(FinishBody),
                TestChannels.Data("[DONE]"),
            ]),
        };
        var context = NewContext(
            ChatBody,
            "?channel_id=gid://axonhub/Channel/" + channel.Id);
        context.Request.Headers["X-Channel-ID"] = "not-a-gid";

        await PlaygroundEndpoint.HandleAsync(context, NewOrchestrator(executor), _channels);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task UpstreamErrorUsesProviderMessageAndStatus()
    {
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => throw new HttpClientError(
                "POST",
                "http://upstream.test/v1/chat/completions",
                429,
                "429 Too Many Requests",
                Encoding.UTF8.GetBytes(
                    """{"error":{"message":"slow down","type":"rate_limit_error"}}""")),
        };
        var context = NewContext(
            ChatBody,
            "?channel_id=gid://axonhub/Channel/" + channel.Id);

        await PlaygroundEndpoint.HandleAsync(
            context,
            NewOrchestrator(executor, retryPolicy: NoRetryPolicy()),
            _channels);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"code\":429,\"message\":\"slow down\"}}",
            ResponseBody(context));
    }

    // ------------------------------------------------------------------
    // HandleError (playground.go:121-211)
    // ------------------------------------------------------------------

    [Fact]
    public void HandleErrorMapsQuotaExhaustionTo503()
    {
        var response = PlaygroundEndpoint.HandleError(
            new QuotaExhaustedException("play-model"));

        Assert.Equal(503, response.Status);
        Assert.Equal(503L, response.Error.Code);
        Assert.Equal("all channels quota exhausted for model play-model", response.Error.Message);
    }

    [Fact]
    public void HandleErrorPrefersUpstreamErrorMessage()
    {
        var response = PlaygroundEndpoint.HandleError(new HttpClientError(
            "POST",
            "http://upstream.test/v1/chat/completions",
            429,
            "429 Too Many Requests",
            Encoding.UTF8.GetBytes("""{"error":{"message":"slow down"}}""")));

        Assert.Equal(429, response.Status);
        Assert.Equal(429L, response.Error.Code);
        Assert.Equal("slow down", response.Error.Message);
    }

    [Fact]
    public void HandleErrorFallsBackToStatusText()
    {
        var response = PlaygroundEndpoint.HandleError(new HttpClientError(
            "POST",
            "http://upstream.test/v1/chat/completions",
            502,
            "502 Bad Gateway"));

        Assert.Equal(502, response.Status);
        Assert.Equal(502L, response.Error.Code);
        Assert.Equal("Bad Gateway", response.Error.Message);
    }

    [Fact]
    public void HandleErrorMapsInvalidRequestToBadRequest()
    {
        var response = PlaygroundEndpoint.HandleError(
            TransformerErrors.InvalidRequest("failed to parse AI SDK request: x"));

        Assert.Equal(400, response.Status);
        Assert.Equal(400L, response.Error.Code);
        Assert.Equal("Bad Request", response.Error.Message);
    }

    [Fact]
    public void HandleErrorUsesNumericResponseErrorCode()
    {
        var response = PlaygroundEndpoint.HandleError(new ResponseError
        {
            StatusCode = 400,
            Detail = new ErrorDetail
            {
                Code = "4001",
                Message = "bad input",
                Type = "invalid_request_error",
            },
        });

        Assert.Equal(400, response.Status);
        Assert.Equal(4001L, response.Error.Code);
        Assert.Equal("bad input", response.Error.Message);
    }

    [Fact]
    public void HandleErrorFallsBackToStatusCodeForNonNumericCode()
    {
        var response = PlaygroundEndpoint.HandleError(new ResponseError
        {
            StatusCode = 429,
            Detail = new ErrorDetail
            {
                Code = "rate_limit_exceeded",
                Message = "slow down",
                Type = "rate_limit_error",
            },
        });

        Assert.Equal(429, response.Status);
        Assert.Equal(429L, response.Error.Code);
        Assert.Equal("slow down", response.Error.Message);
    }

    [Fact]
    public void HandleErrorUsesStatusTextWhenResponseErrorMessageIsEmpty()
    {
        var response = PlaygroundEndpoint.HandleError(new ResponseError
        {
            StatusCode = 503,
            Detail = new ErrorDetail { Code = "77" },
        });

        Assert.Equal(503, response.Status);
        Assert.Equal(503L, response.Error.Code);
        Assert.Equal("Service Unavailable", response.Error.Message);
    }

    [Fact]
    public void HandleErrorFallsBackToInternalServerError()
    {
        var response = PlaygroundEndpoint.HandleError(new InvalidOperationException("boom"));

        Assert.Equal(500, response.Status);
        Assert.Equal(500L, response.Error.Code);
        Assert.Equal("Internal Server Error", response.Error.Message);
    }

    // ------------------------------------------------------------------
    // tryExtractUpstreamErrorMessage (playground.go:74-119)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"error":{"message":"boom"}}""", "boom")]
    [InlineData("""{"message":"flat"}""", "flat")]
    [InlineData("""{"error":"plain"}""", "plain")]
    [InlineData("""{"error":{"message":"wrapped"},"message":"flat"}""", "wrapped")]
    [InlineData("""{"error":{"message":""}}""", "")]
    [InlineData("""{"error":{"message":123}}""", "")]
    [InlineData("""{"error":null}""", "")]
    [InlineData("not-json", "")]
    [InlineData("[1,2]", "")]
    public void TryExtractUpstreamErrorMessageMatchesGoShapes(string body, string expected)
    {
        Assert.Equal(
            expected,
            PlaygroundEndpoint.TryExtractUpstreamErrorMessage(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void TryExtractUpstreamErrorMessageIgnoresEmptyBodies()
    {
        Assert.Equal(string.Empty, PlaygroundEndpoint.TryExtractUpstreamErrorMessage(null));
        Assert.Equal(string.Empty, PlaygroundEndpoint.TryExtractUpstreamErrorMessage([]));
    }

    // ------------------------------------------------------------------
    // GoAtoi (strconv.Atoi semantics)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", 0L)]
    [InlineData("+", 0L)]
    [InlineData("-", 0L)]
    [InlineData("0", 0L)]
    [InlineData("00123", 123L)]
    [InlineData("+123", 123L)]
    [InlineData("-123", -123L)]
    [InlineData("12a", 0L)]
    [InlineData(" 12", 0L)]
    [InlineData("12 ", 0L)]
    [InlineData("١٢", 0L)]
    [InlineData("9223372036854775807", 9223372036854775807L)]
    [InlineData("9223372036854775808", 9223372036854775807L)]
    [InlineData("-9223372036854775808", -9223372036854775808L)]
    [InlineData("-9223372036854775809", -9223372036854775808L)]
    public void GoAtoiMatchesStrconvAtoi(string value, long expected)
    {
        Assert.Equal(expected, PlaygroundEndpoint.GoAtoi(value));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private ChannelRecord SeedChannel()
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Type = "openai",
            Name = "playground-channel",
            Status = "enabled",
            BaseUrl = "http://upstream.test",
            Credentials = """{"apiKey":"sk-test"}""",
            SupportedModels = """["play-model"]""",
            DefaultTestModel = "play-model",
        };

        _channels.Seed(record);
        return record;
    }

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder? recorder = null,
        RetryPolicy? retryPolicy = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(
                retryPolicy ?? new RetryPolicy
                {
                    Enabled = true,
                    MaxChannelRetries = 3,
                    MaxSingleChannelRetries = 0,
                    RetryDelayMs = 0,
                }),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static RetryPolicy NoRetryPolicy()
        => new()
        {
            MaxChannelRetries = 0,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
            UpstreamErrorPolicy = new UpstreamErrorPolicy(
                RetryPolicy.UpstreamErrorModePassthrough,
                string.Empty),
        };

    private static DefaultHttpContext NewContext(string body, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = "/admin/playground/chat";
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();

        // What AdminJwtAuthMiddleware + ProjectIdMiddleware publish.
        context.Items[AdminGraphqlIdentity.ContextKey] = new AdminGraphqlIdentity(
            new ManagementUser(42, IsOwner: true),
            ApiKeyId: null,
            ProjectId: 3,
            IsApiKey: false);

        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
}
