using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Responses session seam on the request path. The reference's
/// Responses transformer reads <c>shared.GetSessionID(ctx)</c> /
/// <c>GetSessionScope(ctx)</c> on every request, both when built directly and
/// through the wrappers that delegate to it (opencode, github_copilot,
/// xai_subscription). The port carries the trace session on the wire request
/// (C062); every factory branch must forward it, and without a trace session
/// the <c>prompt_cache_key</c> fallback stays absent (C072).
/// </summary>
public sealed class ResponsesSessionWiringTests
{
    private const string SessionId = "trace-session-1";
    private const string SessionScope = "ws-scope-1";

    private static LlmRequest ChatRequest(string model = "gpt-5", bool withSession = true)
    {
        var raw = new HttpRequestData
        {
            SessionId = withSession ? SessionId : null,
            SessionScope = withSession ? SessionScope : null,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Body = Encoding.UTF8.GetBytes("""{"model":"gpt-5"}"""),
        };

        return new LlmRequest
        {
            Model = model,
            ApiFormat = ApiFormats.OpenAIResponse,
            RequestType = RequestTypes.Chat,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            RawRequest = raw,
        };
    }

    private static ChannelCandidate Candidate(
        string type,
        string? baseUrl,
        string credentials = """{"apiKey":"provider-key"}""") => new()
    {
        ChannelId = 95,
        ChannelName = type + "-session-wiring",
        ActualModel = "gpt-5",
        Type = type,
        BaseUrl = baseUrl,
        Credentials = credentials,
    };

    private static string? PromptCacheKey(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.TryGetProperty("prompt_cache_key", out var value)
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// The fallback key is <c>&lt;session&gt;-&lt;conversation anchor&gt;</c>
    /// (cache_anchor.go); the anchor is a 16-char lowercase hash, so the prefix
    /// is a stable observable and the suffix must be present.
    /// </summary>
    private static void AssertSessionScopedCacheKey(HttpRequestData httpRequest)
    {
        var key = PromptCacheKey(httpRequest);
        Assert.NotNull(key);
        Assert.StartsWith(SessionId + "-", key, StringComparison.Ordinal);
        Assert.True(
            key.Length > SessionId.Length + 1,
            "the prompt cache key must carry the conversation anchor");
        Assert.Equal(SessionId, httpRequest.SessionId);
        Assert.Equal(SessionScope, httpRequest.SessionScope);
    }

    private static IOutboundTransformer Create(
        ChannelCandidate candidate,
        LlmRequest request,
        IExecutor? executor = null)
        => ChannelOutboundFactory.Create(candidate, request, DateTimeOffset.UnixEpoch, executor);

    [Fact]
    public void OpenaiResponsesChannelForwardsTheTraceSession()
    {
        var request = ChatRequest();
        var transformer = Create(
            Candidate(ChannelOutboundFactory.OpenaiResponsesChannelType, "https://api.openai.com/v1"),
            request);

        AssertSessionScopedCacheKey(transformer.TransformRequest(request));
    }

    [Fact]
    public void XaiResponsesChannelForwardsTheTraceSession()
    {
        // The shared Create helper path, used by every channel type whose
        // primary outbound is the Responses transformer.
        var request = ChatRequest();
        var transformer = Create(
            Candidate(ChannelOutboundFactory.XaiResponsesChannelType, "https://api.x.ai/v1"),
            request);

        AssertSessionScopedCacheKey(transformer.TransformRequest(request));
    }

    [Fact]
    public void OpenCodeResponsesRouteForwardsTheTraceSession()
    {
        // opencode/outbound.go delegates to the Responses transformer with the
        // same ctx when the model routes to the Responses API.
        var request = ChatRequest("gpt-5");
        var transformer = Create(
            Candidate(ChannelOutboundFactory.OpenCodeGoChannelType, "https://opencode.ai/v1"),
            request);

        AssertSessionScopedCacheKey(transformer.TransformRequest(request));
    }

    [Fact]
    public void CopilotResponsesRouteForwardsTheTraceSession()
    {
        var credentials =
            """{"apiKey":"placeholder","oauth":{"access_token":"ghu_test","refresh_token":"ghr_test","expires_at":"2099-01-01T00:00:00Z","client_id":"Iv1.test","token_type":"bearer","scopes":["read:user"]}}""";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = Encoding.UTF8.GetBytes(
                    $$"""{"token":"copilot_token","expires_at":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""),
            },
        };

        var request = ChatRequest("gpt-5.4");
        var transformer = Create(
            Candidate(ChannelOutboundFactory.GithubCopilotChannelType, null, credentials),
            request,
            executor);

        AssertSessionScopedCacheKey(transformer.TransformRequest(request));
    }

    [Fact]
    public void XaiSubscriptionForwardsTheTraceSession()
    {
        var credentials =
            """{"apiKey":"legacy-key","oauth":{"access_token":"oauth-access","expires_at":"2099-01-01T00:00:00Z"}}""";

        var request = ChatRequest("grok-4.5");
        var transformer = Create(
            Candidate(ChannelOutboundFactory.XaiSubscriptionChannelType, null, credentials),
            request);

        AssertSessionScopedCacheKey(transformer.TransformRequest(request));
    }

    [Fact]
    public void WithoutATraceSessionThePromptCacheKeyStaysAbsent()
    {
        var request = ChatRequest(withSession: false);
        var transformer = Create(
            Candidate(ChannelOutboundFactory.OpenaiResponsesChannelType, "https://api.openai.com/v1"),
            request);

        var httpRequest = transformer.TransformRequest(request);

        Assert.Null(PromptCacheKey(httpRequest));
        Assert.Null(httpRequest.SessionId);
        Assert.Null(httpRequest.SessionScope);
    }
}
