using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.OpenCode;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the claudecode and opencode_go halves of the channel wiring against the
/// reference: the type switch in <c>buildChannel</c> (channel_llm.go:924-1194)
/// and the <c>NewClaudeCodeOutbound</c> credential split. The reference builds
/// these outbounds while loading a channel; the port re-dispatches per attempt,
/// so the same decisions are locked through <see cref="ChannelOutboundFactory"/>.
/// </summary>
public sealed class ClaudeCodeAndOpenCodeChannelWiringTests
{
    private const string ClaudeCodeApiKeyCredentials = """{"apiKey":"sk-ant-api-key"}""";

    private const string ClaudeCodeOAuthCredentials = """
        {
          "oauth": {
            "client_id": "claude-code-client",
            "access_token": "oauth-access",
            "refresh_token": "oauth-refresh",
            "expires_at": "2030-01-01T00:00:00Z",
            "token_type": "Bearer"
          }
        }
        """;

    private const string OpenCodeCredentials = """{"apiKey":"oc-key"}""";

    private static ChannelCandidate Candidate(
        string type,
        string credentials,
        string? baseUrl = null) => new()
        {
            ChannelId = 42,
            ChannelName = "wiring-test",
            ActualModel = "claude-sonnet-4-5",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials,
        };

    private static LlmRequest Request(params string[] toolNames)
    {
        var request = new LlmRequest
        {
            Model = "claude-sonnet-4-5",
            MaxTokens = 1024,
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello") },
            ],
        };

        if (toolNames.Length > 0)
        {
            request.Tools = [.. toolNames.Select(Tool)];
        }

        return request;
    }

    private static ClaudeCodeOutboundTransformer CreateClaudeCode(
        string credentials,
        LlmRequest request)
        => Assert.IsType<ClaudeCodeOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.ClaudeCodeChannelType, credentials),
                request,
                DateTimeOffset.UnixEpoch));

    [Fact]
    public void ClaudeCodeApiKeyChannelSendsTheKeyAsABearerToken()
    {
        // channel_llm.go: an api-key claudecode channel wraps the per-attempt key
        // provider, and the outbound always authenticates with Bearer.
        var transformer = CreateClaudeCode(ClaudeCodeApiKeyCredentials, Request());

        var httpRequest = transformer.TransformRequest(Request());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("sk-ant-api-key", httpRequest.Auth.ApiKey);
    }

    [Fact]
    public void ClaudeCodeApiKeyChannelAuditsTheKeyItSends()
    {
        // channel_llm.go:974-975: the non-OAuth branch wraps the per-attempt
        // key provider in ChannelAPIKeyContextProvider, which is what the
        // execution audit reads (C096).
        var transformer = CreateClaudeCode(ClaudeCodeApiKeyCredentials, Request());
        Assert.Null(transformer.SelectedApiKey);

        transformer.TransformRequest(Request());

        Assert.Equal("sk-ant-api-key", transformer.SelectedApiKey);
        Assert.Null(transformer.SelectedApiKeyIndex);
    }

    [Fact]
    public void ClaudeCodeOAuthChannelDoesNotAuditTheAccessToken()
    {
        // The OAuth branch never goes through getAPIKeyProvider, so its token
        // is not a channel key and stays out of the audit row.
        var transformer = CreateClaudeCode(ClaudeCodeOAuthCredentials, Request());

        transformer.TransformRequest(Request());

        Assert.Null(transformer.SelectedApiKey);
        Assert.Null(transformer.SelectedApiKeyIndex);
    }

    [Fact]
    public void ClaudeCodeApiKeyChannelDoesNotApplyTheOfficialToolPrefix()
    {
        // IsOfficial=false for key channels, so tools are forwarded unchanged and
        // no strip_tool_prefix marker is set.
        var transformer = CreateClaudeCode(ClaudeCodeApiKeyCredentials, Request());

        var httpRequest = transformer.TransformRequest(Request("bash"));

        Assert.Equal(
            "bash",
            Body(httpRequest).GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.False(httpRequest.Metadata!.ContainsKey("strip_tool_prefix"));
    }

    [Fact]
    public void ClaudeCodeApiKeyChannelDerivesTheFakeUserIdFromTheChannelId()
    {
        // strconv.Itoa(c.ID) is the account identity: with ID 42 the device id is
        // SHA-256("42") and the account uuid is its URL-namespace UUID v5.
        var transformer = CreateClaudeCode(ClaudeCodeApiKeyCredentials, Request());

        var userId = Body(transformer.TransformRequest(Request()))
            .GetProperty("metadata").GetProperty("user_id").GetString();

        var parsed = ClaudeCodeUserID.ParseUserId(userId!);
        Assert.NotNull(parsed);
        Assert.Equal(
            "73475cb40a568e8da8a045ced110137e159f890ac4da883b6b17dc651b3a8049",
            parsed!.DeviceId);
        Assert.Equal("5c2b23de-4bad-58ee-a4b3-f22f3b9cfd7d", parsed.AccountUuid);
    }

    [Fact]
    public void ClaudeCodeOAuthChannelUsesTheAccessTokenAndAppliesTheToolPrefix()
    {
        // channel_llm.go: an oauth claudecode channel gets a refreshing token
        // provider and IsOfficial=true (the first-party Claude Code behaviour).
        var transformer = CreateClaudeCode(ClaudeCodeOAuthCredentials, Request());

        var httpRequest = transformer.TransformRequest(Request("bash"));

        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("oauth-access", httpRequest.Auth.ApiKey);
        Assert.Equal(
            "proxy_bash",
            Body(httpRequest).GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("true", httpRequest.Metadata!["strip_tool_prefix"]);
    }

    [Fact]
    public void ClaudeCodeChannelWithoutCredentialsUsesTheOAuthOrKeyMessage()
    {
        // channel_llm.go:662-665: claudecode accepts either credential kind.
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.ClaudeCodeChannelType, "{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "missing credentials: oauth or api key required for channel wiring-test",
            error.Message);
    }

    [Theory]
    [InlineData("deepseek-v4-flash", "/v1/chat/completions", "deepseek")]
    [InlineData("grok-4.5", "/v1/responses", "responses")]
    [InlineData("minimax-m3", "/v1/messages", "anthropic")]
    [InlineData("glm-5.2", "/v1/chat/completions", "chat")]
    public void OpenCodeGoChannelRoutesByModelThroughOneOutbound(
        string model,
        string path,
        string route)
    {
        var request = Request();
        request.Model = model;
        request.RawRequest = new HttpRequestData { SessionId = "trace-session" };

        var transformer = Assert.IsType<OpenCodeOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(
                    ChannelOutboundFactory.OpenCodeGoChannelType,
                    OpenCodeCredentials,
                    "https://opencode.ai/zen/go"),
                request,
                DateTimeOffset.UnixEpoch));

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://opencode.ai/zen/go" + path, httpRequest.Url);
        Assert.Equal(
            "trace-session",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
        Assert.Equal(
            route,
            httpRequest.TransformerMetadata![OpenCodeOutboundTransformer.RouteMetadataKey]
                .GetString());
    }

    [Fact]
    public void OpenCodeGoChannelWithoutCredentialsUsesTheMissingApiKeyMessage()
    {
        // Only codex/claudecode/fenno accept OAuth; opencode_go still needs a key.
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.OpenCodeGoChannelType, "{}"),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel wiring-test", error.Message);
    }

    [Fact]
    public void OpenCodeGoAnthropicChannelAddsSessionAffinity()
    {
        // channel_llm.go:1170-1181: the anthropic outbound wrapped in the
        // OpenCode session header decorator.
        var request = Request();
        request.Model = "minimax-m3";
        request.RawRequest = new HttpRequestData { SessionId = " trace-session " };

        var transformer = ChannelOutboundFactory.Create(
            Candidate(
                ChannelOutboundFactory.OpenCodeGoAnthropicChannelType,
                OpenCodeCredentials,
                "https://opencode.ai/zen/go"),
            request,
            DateTimeOffset.UnixEpoch);

        Assert.IsType<OpenCodeSessionHeaderOutbound>(transformer);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal("https://opencode.ai/zen/go/v1/messages", httpRequest.Url);
        Assert.Equal(
            "trace-session",
            HttpClientUtils.HeaderGet(httpRequest.Headers, OpenCodeHeaders.SessionHeader));
    }

    private static Tool Tool(string name)
        => new()
        {
            Type = ToolTypes.Function,
            Function = new Function { Name = name, Description = "Execute bash" },
        };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
