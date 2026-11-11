using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/antigravity/transformer_test.go: the Antigravity
/// envelope structure, schema/tool/thinking patching, and the OAuth-only
/// auth wiring (no API key fallback, no X-Goog-Api-Key header).
/// </summary>
[Collection("antigravity-version-state")]
public sealed class AntigravityTransformerTests
{
    private const string BaseURL = "https://api.antigravity.dev";
    private const string APIKey = "test-refresh-token|test-project-id";
    private const string Project = "my-project";

    private static AntigravityTransformer NewTransformer(OAuthTestExecutor executor)
    {
        // The reference keeps one package-level version state; reset it so the
        // User-Agent assertions never depend on earlier tests.
        AntigravityVersion.ResetForTests();

        return AntigravityTransformer.Create(
            new AntigravityConfig
            {
                BaseURL = BaseURL,
                APIKey = APIKey,
                Project = Project,
            },
            new AntigravityTransformerOptions { HttpClient = executor });
    }

    /// <summary>Scripts the OAuth token endpoint; everything else 404s.</summary>
    private static OAuthTestExecutor TokenExecutor(string accessToken = "mock-access-token")
    {
        var tokenJson = $$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":3600}""";

        return new OAuthTestExecutor
        {
            Handler = request => request.Url == AntigravityConstants.TokenURL
                ? OAuthTestSupport.Json(tokenJson)
                : new HttpResponseData { StatusCode = 404 },
        };
    }

    private static AntigravityEnvelope ParseEnvelope(HttpRequestData httpReq)
    {
        var envelope = JsonSerializer.Deserialize<AntigravityEnvelope>(httpReq.Body ?? []);
        Assert.NotNull(envelope);
        return envelope;
    }

    private static JsonElement InnerRequest(AntigravityEnvelope envelope)
    {
        Assert.IsType<JsonElement>(envelope.Request);
        return (JsonElement)envelope.Request!;
    }

    private static LlmRequest ChatRequest(string model, string content = "Hello") => new()
    {
        Model = model,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText(content) }],
    };

    // ------------------------------------------------------------------
    // TransformRequest (reference TestTransformRequest_Antigravity)
    // ------------------------------------------------------------------

    [Fact]
    public void TestTransformRequest_Antigravity_BasicRequestStructure()
    {
        var executor = TokenExecutor();
        var transformer = NewTransformer(executor);

        var httpReq = transformer.TransformRequest(ChatRequest("gemini-2.5-flash"));

        // Check headers.
        Assert.Equal("application/json", OAuthTestSupport.Header(httpReq, "Content-Type"));
        Assert.Equal(AntigravityVersion.GetUserAgent(), OAuthTestSupport.Header(httpReq, "User-Agent"));
        Assert.Equal(AntigravityConstants.ApiClient, OAuthTestSupport.Header(httpReq, "X-Goog-Api-Client"));
        Assert.Equal(AntigravityConstants.ClientMetadata, OAuthTestSupport.Header(httpReq, "Client-Metadata"));

        // Since the token response is scripted, the auth config carries the mock access token.
        Assert.NotNull(httpReq.Auth);
        Assert.Equal(AuthTypes.Bearer, httpReq.Auth.Type);
        Assert.Equal("mock-access-token", httpReq.Auth.ApiKey);

        // Check URL.
        Assert.Equal(BaseURL + "/v1internal:generateContent", httpReq.Url);

        // Check body (envelope).
        var envelope = ParseEnvelope(httpReq);
        Assert.Equal(Project, envelope.Project);
        Assert.Equal("gemini-2.5-flash", envelope.Model);

        // Check the inner request.
        var inner = InnerRequest(envelope);
        var contents = inner.GetProperty("contents");
        var first = Assert.Single(contents.EnumerateArray());
        Assert.Equal("Hello", first.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void TestTransformRequest_Antigravity_SchemaSanitization()
    {
        var executor = TokenExecutor();
        var transformer = NewTransformer(executor);

        // Create a schema with unsupported features (const).
        var schemaJSON = """{"type":"object","properties":{"mode":{"const":"auto"}}}""";

        var req = ChatRequest("claude-3-5-sonnet", "Gen JSON");
        req.ResponseFormat = new ResponseFormat
        {
            Type = "json_schema",
            JsonSchema = JsonElement.Parse(schemaJSON).Clone(),
        };

        var httpReq = transformer.TransformRequest(req);

        var inner = InnerRequest(ParseEnvelope(httpReq));
        var responseSchema = inner.GetProperty("generationConfig").GetProperty("responseSchema");
        var mode = responseSchema.GetProperty("properties").GetProperty("mode");

        // Verify const became enum.
        Assert.False(mode.TryGetProperty("const", out _));
        var enumValues = mode.GetProperty("enum");
        var first = Assert.Single(enumValues.EnumerateArray());
        Assert.Equal("auto", first.GetString());
    }

    [Fact]
    public void TestTransformRequest_Antigravity_ToolConfigurationForClaude()
    {
        var executor = TokenExecutor();
        var transformer = NewTransformer(executor);

        var req = ChatRequest("claude-3-5-sonnet", "Help");
        req.Tools =
        [
            new Tool
            {
                Type = "function",
                Function = new Function
                {
                    Name = "test_tool",
                    Parameters = JsonElement.Parse("""{"type":"object"}""").Clone(),
                },
            },
        ];

        var httpReq = transformer.TransformRequest(req);

        var inner = InnerRequest(ParseEnvelope(httpReq));

        // Check ToolConfig: VALIDATED mode.
        Assert.Equal(
            "VALIDATED",
            inner.GetProperty("toolConfig").GetProperty("functionCallingConfig").GetProperty("mode").GetString());

        // Check the system instruction hardening.
        var systemParts = inner.GetProperty("systemInstruction").GetProperty("parts");
        Assert.True(systemParts.GetArrayLength() > 0);
        Assert.Contains("CRITICAL: DO NOT guess tool parameters", systemParts[0].GetProperty("text").GetString());
    }

    [Fact]
    public void TestTransformRequest_Antigravity_StripThinkingForClaude()
    {
        var executor = TokenExecutor();
        var transformer = NewTransformer(executor);

        var req = new LlmRequest
        {
            Model = "claude-3-5-sonnet",
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
                new Message
                {
                    Role = "assistant",
                    ReasoningContent = "I am thinking...",
                    Content = MessageContent.FromText("Hi"),
                },
            ],
        };

        var httpReq = transformer.TransformRequest(req);

        var inner = InnerRequest(ParseEnvelope(httpReq));
        var contents = inner.GetProperty("contents");
        Assert.Equal(2, contents.GetArrayLength());

        // Assistant message should only have the text part, reasoning/thought
        // part stripped (the converter marks ReasoningContent as Thought=true).
        var assistantContent = contents[1];
        Assert.Equal("model", assistantContent.GetProperty("role").GetString());
        var parts = assistantContent.GetProperty("parts");
        var first = Assert.Single(parts.EnumerateArray());
        Assert.Equal("Hi", first.GetProperty("text").GetString());
        Assert.False(first.TryGetProperty("thought", out var thought) && thought.GetBoolean());
    }

    [Fact]
    public void TestTransformRequest_Antigravity_ToolParametersUseParametersField()
    {
        // CRITICAL: the Antigravity API expects "parameters", not
        // "parametersJsonSchema" (the malformed tool call fix).
        var executor = TokenExecutor();
        var transformer = NewTransformer(executor);

        var req = ChatRequest("gemini-3-pro", "Help");
        req.Tools =
        [
            new Tool
            {
                Type = "function",
                Function = new Function
                {
                    Name = "test_tool",
                    Description = "A test tool",
                    Parameters = JsonElement.Parse(
                        """{"type":"object","properties":{"arg":{"type":"string"}},"required":["arg"]}""")
                        .Clone(),
                },
            },
        ];

        var httpReq = transformer.TransformRequest(req);

        var inner = InnerRequest(ParseEnvelope(httpReq));
        var tools = inner.GetProperty("tools");
        var firstTool = tools[0];
        var declarations = firstTool.GetProperty("functionDeclarations");
        var firstDecl = declarations[0];

        // Must have "parameters", not "parametersJsonSchema".
        Assert.True(firstDecl.TryGetProperty("parameters", out var parameters));
        Assert.False(firstDecl.TryGetProperty("parametersJsonSchema", out _));

        // UPPERCASE type, as the Antigravity API expects.
        Assert.Equal("OBJECT", parameters.GetProperty("type").GetString());
        Assert.True(parameters.TryGetProperty("properties", out _));
    }

    // ------------------------------------------------------------------
    // OAuth-only auth (reference TestOAuthFailureNoFallback etc.)
    // ------------------------------------------------------------------

    [Fact]
    public void TestOAuthFailureNoFallback()
    {
        // When OAuth token retrieval fails, an error is returned (no fallback
        // to the API key).
        var executor = new OAuthTestExecutor
        {
            Handler = request => request.Url == AntigravityConstants.TokenURL
                ? throw new InvalidOperationException("token retrieval failed")
                : new HttpResponseData { StatusCode = 404 },
        };
        var transformer = NewTransformer(executor);

        var ex = Assert.Throws<InvalidOperationException>(
            () => transformer.TransformRequest(ChatRequest("gemini-2.5-flash")));

        Assert.Contains("failed to get OAuth token", ex.Message);
    }

    [Fact]
    public void TestNoAPIKeyAuthConfig()
    {
        // AuthConfig is never set to the api_key type.
        var executor = TokenExecutor();
        var transformer = NewTransformer(executor);

        var httpReq = transformer.TransformRequest(ChatRequest("gemini-2.5-flash"));

        // Auth is the OAuth Bearer token.
        Assert.NotNull(httpReq.Auth);
        Assert.Equal(AuthTypes.Bearer, httpReq.Auth.Type);
        Assert.Equal("mock-access-token", httpReq.Auth.ApiKey);

        // No X-Goog-Api-Key header is set.
        Assert.Empty(OAuthTestSupport.Header(httpReq, "X-Goog-Api-Key"));
    }

    [Fact]
    public void TestOAuthOnlyHeaders()
    {
        // X-Goog-Api-Key must never be sent; the OAuth client identity headers
        // are present.
        var executor = TokenExecutor("oauth-access-token");
        var transformer = NewTransformer(executor);

        var httpReq = transformer.TransformRequest(ChatRequest("claude-3-5-sonnet", "Test"));

        // Auth config is the Bearer token.
        Assert.NotNull(httpReq.Auth);
        Assert.Equal(AuthTypes.Bearer, httpReq.Auth.Type);
        Assert.Equal("oauth-access-token", httpReq.Auth.ApiKey);

        // X-Goog-Api-Key header is NOT present.
        Assert.Empty(OAuthTestSupport.Header(httpReq, "X-Goog-Api-Key"));

        // Other required headers are present.
        Assert.Equal("application/json", OAuthTestSupport.Header(httpReq, "Content-Type"));
        Assert.Equal(AntigravityVersion.GetUserAgent(), OAuthTestSupport.Header(httpReq, "User-Agent"));
        Assert.Equal(AntigravityConstants.ApiClient, OAuthTestSupport.Header(httpReq, "X-Goog-Api-Client"));
        Assert.Equal(AntigravityConstants.ClientMetadata, OAuthTestSupport.Header(httpReq, "Client-Metadata"));
    }
}
