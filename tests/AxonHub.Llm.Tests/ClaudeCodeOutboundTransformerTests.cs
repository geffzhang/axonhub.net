using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Claude Code outbound transformer
/// (llm/transformer/anthropic/claudecode/outbound.go and outbound_test.go).
/// </summary>
public sealed class ClaudeCodeOutboundTransformerTests
{
    // ------------------------------------------------------------------
    // Request shaping
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_AlwaysUsesBearerAuth()
    {
        var request = Transformer()
            .TransformRequest(SimpleRequest());

        Assert.Equal(ApiFormats.AnthropicMessage, request.ApiFormat);
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("sk-ant-oat01-oauth-token", request.Auth.ApiKey);
    }

    [Fact]
    public void TransformRequest_UsesTheDefaultBaseUrlAndBetaQuery()
    {
        var request = Transformer().TransformRequest(SimpleRequest());

        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Url);
        Assert.Equal(["true"], request.Query!["beta"]);
    }

    [Fact]
    public void TransformRequest_InjectsClaudeCodeSystemMessageWithCacheControl()
    {
        var body = Body(Transformer().TransformRequest(SimpleRequest()));

        var system = body.GetProperty("system");
        Assert.Equal(JsonValueKind.Array, system.ValueKind);

        var first = system[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        Assert.Equal(ClaudeCodeConstants.SystemMessage, first.GetProperty("text").GetString());
        Assert.Equal("ephemeral", first.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Fact]
    public void TransformRequest_SetsAllClaudeCodeHeaders()
    {
        var request = Transformer().TransformRequest(SimpleRequest());

        Assert.Contains(
            "interleaved-thinking-2025-05-14",
            HttpClientUtils.HeaderGet(request.Headers, "Anthropic-Beta"),
            StringComparison.Ordinal);
        Assert.Equal("2023-06-01", HttpClientUtils.HeaderGet(request.Headers, "Anthropic-Version"));
        Assert.Equal(
            "true",
            HttpClientUtils.HeaderGet(request.Headers, "Anthropic-Dangerous-Direct-Browser-Access"));
        Assert.Equal("cli", HttpClientUtils.HeaderGet(request.Headers, "X-App"));
        Assert.Equal(
            "stream",
            HttpClientUtils.HeaderGet(request.Headers, "X-Stainless-Helper-Method"));
        Assert.Equal(
            ClaudeCodeConstants.UserAgent,
            HttpClientUtils.HeaderGet(request.Headers, "User-Agent"));
    }

    [Fact]
    public void TransformRequest_AcceptDependsOnStreaming()
    {
        Assert.Equal(
            "application/json",
            HttpClientUtils.HeaderGet(
                Transformer().TransformRequest(SimpleRequest()).Headers,
                "Accept"));

        var streamRequest = SimpleRequest();
        streamRequest.Stream = true;
        Assert.Equal(
            "text/event-stream",
            HttpClientUtils.HeaderGet(
                Transformer().TransformRequest(streamRequest).Headers,
                "Accept"));
    }

    [Fact]
    public void TransformRequest_AppliesToolPrefixForOfficialNonCliClients()
    {
        var request = SimpleRequest();
        request.Tools = [CalculatorTool("bash")];

        var httpRequest = Transformer(isOfficial: true).TransformRequest(request);

        var body = Body(httpRequest);
        Assert.Equal("proxy_bash", body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("true", httpRequest.Metadata!["strip_tool_prefix"]);

        // The caller's request keeps its tool names.
        Assert.Equal("bash", request.Tools[0].Function.Name);
    }

    [Fact]
    public void TransformRequest_DoesNotApplyToolPrefixForClaudeCliClients()
    {
        var request = SimpleRequest();
        request.Tools = [CalculatorTool("bash")];
        request.RawRequest = new HttpRequestData
        {
            Headers = Headers(("User-Agent", "claude-cli/1.0.83")),
        };

        var httpRequest = Transformer(isOfficial: true).TransformRequest(request);

        var body = Body(httpRequest);
        Assert.Equal("bash", body.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.False(httpRequest.Metadata!.ContainsKey("strip_tool_prefix"));

        // The CLI user agent is kept, both outbound and inbound.
        Assert.Equal("claude-cli/1.0.83", HttpClientUtils.HeaderGet(httpRequest.Headers, "User-Agent"));
        Assert.Equal(
            "claude-cli/1.0.83",
            HttpClientUtils.HeaderGet(request.RawRequest.Headers, "User-Agent"));
    }

    [Fact]
    public void TransformRequest_ReplacesNonCliUserAgent()
    {
        var request = SimpleRequest();
        request.RawRequest = new HttpRequestData
        {
            Headers = Headers(("User-Agent", "Mozilla/5.0")),
        };

        var httpRequest = Transformer(isOfficial: true).TransformRequest(request);

        Assert.Equal(
            ClaudeCodeConstants.UserAgent,
            HttpClientUtils.HeaderGet(httpRequest.Headers, "User-Agent"));

        // The reference deletes the client UA from the shared inbound headers.
        Assert.Equal(
            string.Empty,
            HttpClientUtils.HeaderGet(request.RawRequest.Headers, "User-Agent"));
    }

    [Fact]
    public void TransformRequest_PassesInboundHeadersThrough()
    {
        var request = SimpleRequest();
        request.RawRequest = new HttpRequestData
        {
            Headers = Headers(
                ("X-Claude-Code-Session-Id", "session-123"),
                ("X-Stainless-Timeout", "120"),
                ("Anthropic-Beta", "custom-beta-2026-01-01")),
        };

        var httpRequest = Transformer().TransformRequest(request);

        Assert.Equal(
            "session-123",
            HttpClientUtils.HeaderGet(httpRequest.Headers, "X-Claude-Code-Session-Id"));
        Assert.Equal(
            "120",
            HttpClientUtils.HeaderGet(httpRequest.Headers, "X-Stainless-Timeout"));

        // Anthropic-Beta is merged, not replaced.
        var beta = HttpClientUtils.HeaderGet(httpRequest.Headers, "Anthropic-Beta");
        Assert.Contains("interleaved-thinking-2025-05-14", beta, StringComparison.Ordinal);
        Assert.EndsWith("custom-beta-2026-01-01", beta, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequest_InjectsFakeUserId()
    {
        var body = Body(Transformer().TransformRequest(SimpleRequest()));

        var userId = body.GetProperty("metadata").GetProperty("user_id").GetString();
        Assert.NotNull(userId);

        var parsed = ClaudeCodeUserID.ParseUserId(userId!);
        Assert.NotNull(parsed);
        Assert.Equal(
            "73475cb40a568e8da8a045ced110137e159f890ac4da883b6b17dc651b3a8049",
            parsed!.DeviceId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransformRequest_PreservesBillingSystemMessage(bool isOfficial)
    {
        const string Billing =
            "x-anthropic-billing-header: cc_version=2.1.42.c31; cc_entrypoint=cli; cch=38a80;";

        var request = SimpleRequest();
        request.Messages.Insert(0, new Message
        {
            Role = "system",
            Content = MessageContent.FromText(Billing),
        });

        var body = Body(Transformer(isOfficial: isOfficial).TransformRequest(request));

        var found = false;
        foreach (var part in body.GetProperty("system").EnumerateArray())
        {
            if (part.GetProperty("text").GetString()!.Contains(
                "x-anthropic-billing-header",
                StringComparison.Ordinal))
            {
                found = true;
                Assert.Contains("cch=38a80;", part.GetProperty("text").GetString()!, StringComparison.Ordinal);
            }
        }

        Assert.True(found, "the billing system message must be forwarded");
    }

    [Fact]
    public void TransformRequest_DisablesThinkingWhenToolChoiceForcesToolUse()
    {
        var request = SimpleRequest();
        request.ReasoningEffort = "high";
        request.ReasoningBudget = 10_000;
        request.Tools = [CalculatorTool("bash")];
        request.ToolChoice = new ToolChoice { ToolChoiceMode = "any" };

        var body = Body(Transformer().TransformRequest(request));

        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.False(body.TryGetProperty("output_config", out _));
    }

    [Fact]
    public void TransformRequest_KeepsThinkingWithoutForcedToolChoice()
    {
        // Control case: the ClaudeCode platform expresses the effort level as
        // output_config.effort plus adaptive thinking.
        var request = SimpleRequest();
        request.ReasoningEffort = "high";
        request.ReasoningBudget = 10_000;
        request.Tools = [CalculatorTool("bash")];

        var body = Body(Transformer().TransformRequest(request));

        Assert.Equal(
            "adaptive",
            body.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(
            "high",
            body.GetProperty("output_config").GetProperty("effort").GetString());
    }

    // ------------------------------------------------------------------
    // Responses (reference TestClaudeCodeTransformer_TransformResponse)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformResponse_StripsToolPrefixWhenApplied()
    {
        var response = Response(
            """{"id":"msg_123","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"tool_use","id":"toolu_123","name":"proxy_bash","input":{"command":"ls"}}],"stop_reason":"tool_use","usage":{"input_tokens":100,"output_tokens":50}}""",
            new HttpRequestData
            {
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["strip_tool_prefix"] = "true",
                },
            });

        var result = Transformer().TransformResponse(response);

        Assert.Single(result.Choices);
        Assert.Single(result.Choices[0].Message!.ToolCalls!);
        Assert.Equal("bash", result.Choices[0].Message!.ToolCalls![0].Function.Name);
    }

    [Fact]
    public void TransformResponse_DoesNotStripWhenPrefixWasNotApplied()
    {
        var response = Response(
            """{"id":"msg_123","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"tool_use","id":"toolu_123","name":"proxy_bash","input":{"command":"ls"}}],"stop_reason":"tool_use","usage":{"input_tokens":100,"output_tokens":50}}""",
            new HttpRequestData());

        var result = Transformer().TransformResponse(response);

        Assert.Single(result.Choices);
        Assert.Equal(
            "proxy_bash",
            result.Choices[0].Message!.ToolCalls![0].Function.Name);
    }

    // ------------------------------------------------------------------
    // Streams (reference TestClaudeCodeTransformer_TransformStream)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformStream_StripsToolPrefixFromDeltas()
    {
        var events = new[]
        {
            Event(
                "content_block_start",
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_123","name":"proxy_bash"}}"""),
        };

        using var transformed = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(events));
        var responses = GoStreams.All(transformed, out var error);

        Assert.Null(error);
        Assert.NotEmpty(responses);

        var found = false;
        foreach (var response in responses)
        {
            foreach (var choice in response.Choices)
            {
                foreach (var toolCall in choice.Delta?.ToolCalls ?? [])
                {
                    if (toolCall.Function.Name.Length > 0)
                    {
                        found = true;
                        Assert.Equal("bash", toolCall.Function.Name);
                    }
                }
            }
        }

        Assert.True(found, "should have found at least one tool call");
    }

    [Fact]
    public void TransformStream_HandlesStreamsWithoutToolCalls()
    {
        var events = new[]
        {
            Event(
                "message_start",
                """{"type":"message_start","message":{"id":"msg_123","model":"claude-3-5-sonnet-20241022"}}"""),
        };

        using var transformed = Transformer().TransformStream(
            null,
            new SliceStream<StreamEvent>(events));

        GoStreams.All(transformed, out var error);
        Assert.Null(error);
    }

    [Fact]
    public void AggregateStreamChunks_StripsToolPrefixBeforeAggregation()
    {
        var chunks = new List<StreamEvent>
        {
            Event(
                "message_start",
                """{"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5","usage":{"input_tokens":10}}}"""),
            Event(
                "content_block_start",
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_1","name":"proxy_bash"}}"""),
            Event(
                "content_block_delta",
                """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"ls\"}"}}"""),
            Event("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            Event(
                "message_delta",
                """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":5}}"""),
            Event("message_stop", """{"type":"message_stop"}"""),
        };

        var body = Transformer().AggregateStreamChunks(null, chunks, out _);

        using var document = JsonDocument.Parse(body);
        var block = document.RootElement.GetProperty("content")[0];
        Assert.Equal("tool_use", block.GetProperty("type").GetString());
        Assert.Equal("bash", block.GetProperty("name").GetString());
    }

    // ------------------------------------------------------------------
    // Errors and construction
    // ------------------------------------------------------------------

    [Fact]
    public void TransformError_DelegatesToAnthropic()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "https://api.anthropic.com/v1/messages",
            400,
            "400 Bad Request",
            Encoding.UTF8.GetBytes(
                """{"type":"error","error":{"type":"invalid_request_error","message":"bad"}}""")));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("bad", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void TransformRequest_NilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("request is nil", error.Message);
    }

    [Fact]
    public void ConstructorRequiresTokenProvider()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ClaudeCodeOutboundTransformer(new ClaudeCodeParams()));

        Assert.Equal("TokenProvider is required", error.Message);
    }

    [Fact]
    public void TransformRequest_WrapsTokenErrors()
    {
        var transformer = new ClaudeCodeOutboundTransformer(new ClaudeCodeParams
        {
            TokenProvider = new ThrowingTokenGetter(),
            AccountIdentity = "42",
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => transformer.TransformRequest(SimpleRequest()));

        Assert.Equal("failed to get oauth token: boom", error.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ClaudeCodeOutboundTransformer Transformer(
        bool isOfficial = false,
        ITokenGetter? tokens = null,
        string baseUrl = "")
        => new(new ClaudeCodeParams
        {
            TokenProvider = tokens ?? new StaticTokenGetter("sk-ant-oat01-oauth-token"),
            BaseUrl = baseUrl,
            IsOfficial = isOfficial,
            AccountIdentity = "42",
        });

    private static LlmRequest SimpleRequest()
        => new()
        {
            Model = "claude-sonnet-4-5",
            MaxTokens = 1024,
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
        };

    private static Tool CalculatorTool(string name)
        => new()
        {
            Type = ToolTypes.Function,
            Function = new Function
            {
                Name = name,
                Description = "Execute bash",
                Parameters = Json(
                    """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""),
            },
        };

    private static Dictionary<string, List<string>> Headers(
        params (string Name, string Value)[] headers)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            HttpClientUtils.HeaderSet(result, name, value);
        }

        return result;
    }

    private static HttpResponseData Response(string body, HttpRequestData request)
        => new()
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
            Request = request,
        };

    private static StreamEvent Event(string type, string data)
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(data) };

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    private sealed class StaticTokenGetter(string accessToken) : ITokenGetter
    {
        public OAuthCredentials Get(CancellationToken cancellationToken)
            => new()
            {
                AccessToken = accessToken,
                RefreshToken = "mock-refresh-token",
                TokenType = "Bearer",
            };
    }

    private sealed class ThrowingTokenGetter : ITokenGetter
    {
        public OAuthCredentials Get(CancellationToken cancellationToken)
            => throw new InvalidOperationException("boom");
    }
}
