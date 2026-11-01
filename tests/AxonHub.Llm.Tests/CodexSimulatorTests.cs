using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/codex/codex_simulator_test.go. The reference
/// drives these through simulator.Simulate with the trace session in the
/// context; the port passes it through the transformer's SessionIdProvider
/// seam (difference C062).
/// </summary>
public sealed class CodexSimulatorTests
{
    [Fact]
    public void TestCodexOutbound_MinimalIdentityHeaders()
    {
        var sim = NewSimulator();
        var request = CodexTestSupport.NewChatCompletionRequest(
            ("Conversation_id", "legacy-conversation"),
            ("Session_id", "provided-session"),
            ("Version", "9.9.9"));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(CodexOutboundTransformer.CodexApiUrl, finalRequest.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(finalRequest.Headers, "Content-Type"));
        Assert.Equal(
            CodexConstants.AxonHubOriginator,
            HttpClientUtils.HeaderGet(finalRequest.Headers, "Originator"));
        Assert.Equal("axonhub/1.0", HttpClientUtils.HeaderGet(finalRequest.Headers, "User-Agent"));
        Assert.Equal(
            "provided-session",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));
        Assert.Equal(
            CodexTestSupport.TestChatAccountId,
            HttpClientUtils.HeaderGet(finalRequest.Headers, "Chatgpt-Account-Id"));
        Assert.Equal(
            "Bearer " + CodexTestSupport.TestAccessTokenWithAccountId(),
            HttpClientUtils.HeaderGet(finalRequest.Headers, "Authorization"));
        Assert.Equal("legacy-conversation", HttpClientUtils.HeaderGet(finalRequest.Headers, "Conversation_id"));
        Assert.Equal("9.9.9", HttpClientUtils.HeaderGet(finalRequest.Headers, "Version"));
    }

    [Fact]
    public void TestCodexOutbound_AllowsInboundIdentityOverrides()
    {
        var sim = NewSimulator();
        var request = CodexTestSupport.NewChatCompletionRequest(
            ("Originator", CodexTestSupport.LegacyCodexOriginator()),
            ("User-Agent", CodexTestSupport.LegacyCodexUserAgent()));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            CodexTestSupport.LegacyCodexOriginator(),
            HttpClientUtils.HeaderGet(finalRequest.Headers, "Originator"));
        Assert.Equal(
            CodexTestSupport.LegacyCodexUserAgent(),
            HttpClientUtils.HeaderGet(finalRequest.Headers, "User-Agent"));

        var userAgent = HttpClientUtils.HeaderGet(finalRequest.Headers, "User-Agent").ToLowerInvariant();
        Assert.Contains(CodexTestSupport.LegacyCodexOriginator(), userAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void TestCodexOutbound_PassthroughModernCodexHeaders()
    {
        var sim = NewSimulator();
        var request = CodexTestSupport.NewChatCompletionRequest(
            ("X-Codex-Turn-Metadata", """{"session_id":"turn-session","turn_id":"turn-123"}"""),
            ("X-Codex-Window-Id", "window-123"),
            ("X-Client-Request-Id", "request-123"),
            ("X-Codex-Beta-Features", "js_repl"),
            ("Thread-Id", "thread-123"),
            (CodexHeaders.ResponsesLiteHeader, "true"));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            """{"session_id":"turn-session","turn_id":"turn-123"}""",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.TurnMetadataHeader));
        Assert.Equal(
            "window-123",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.WindowIdHeader));
        Assert.Equal(
            "request-123",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.ClientRequestIdHeader));
        Assert.Equal(
            "js_repl",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.BetaFeaturesHeader));
        Assert.Equal(
            "thread-123",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.ThreadIdHeader));
        Assert.Equal(
            "true",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.ResponsesLiteHeader));
    }

    [Fact]
    public void TestCodexOutbound_NonCodexInboundDefaults()
    {
        var sim = NewSimulator();
        // Plain OpenAI-compatible client: no Session-Id / Thread-Id / X-Codex-* headers.
        var request = CodexTestSupport.NewChatCompletionRequest(
            (CodexHeaders.SessionHeaderHyphen, "fixed-session"));

        var finalRequest = Simulate(sim, request);

        const string sessionId = "fixed-session";
        var windowId = sessionId + ":0";

        // Identity headers are fabricated from the resolved session so the
        // upstream sees a complete Codex session shape even for non-Codex
        // clients.
        Assert.Equal(
            sessionId,
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.ThreadIdHeader));
        Assert.Equal(sessionId, HttpClientUtils.HeaderGet(finalRequest.Headers, "Conversation_id"));
        Assert.Equal(
            windowId,
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.WindowIdHeader));
        Assert.Equal(
            CodexConstants.FabricatedBetaFeatures,
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.BetaFeaturesHeader));

        // Responses Lite is a private protocol mode and must not be fabricated
        // for ordinary OpenAI-compatible clients.
        Assert.Equal(
            string.Empty,
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.ResponsesLiteHeader));

        // X-Client-Request-Id and the turn id are per-request UUIDs.
        var clientRequestId = HttpClientUtils.HeaderGet(
            finalRequest.Headers,
            CodexHeaders.ClientRequestIdHeader);
        Assert.NotEmpty(clientRequestId);
        Assert.True(Guid.TryParse(clientRequestId, out _), "X-Client-Request-Id must be a UUID");

        var turnMetadata = ParseTurnMetadata(finalRequest);
        // installation_id is deterministically derived from the ChatGPT account id.
        Assert.Equal(
            CodexUtils.NewUuidV5(
                CodexUtils.NameSpaceOid,
                Encoding.UTF8.GetBytes(CodexTestSupport.TestChatAccountId)),
            turnMetadata.InstallationId);
        Assert.Equal(sessionId, turnMetadata.SessionId);
        Assert.Equal(sessionId, turnMetadata.ThreadId);
        Assert.Equal(windowId, turnMetadata.WindowId);
        Assert.Equal("turn", turnMetadata.RequestKind);
        Assert.Equal("user", turnMetadata.ThreadSource);
        Assert.Equal("none", turnMetadata.Sandbox);
        Assert.Null(turnMetadata.Workspaces);
        Assert.True(Guid.TryParse(turnMetadata.TurnId, out _), "turn_id must be a UUID");

        // turn_started_at_unix_ms is deterministic per session and stays inside
        // the current 10-minute bucket.
        const long bucketMs = 10 * 60 * 1000;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var bucketStart = nowMs - (nowMs % bucketMs);
        Assert.InRange(turnMetadata.TurnStartedAtUnixMs, bucketStart, bucketStart + bucketMs - 1);

        // A second request for the same session fabricates the same turn
        // started time.
        var request2 = CodexTestSupport.NewChatCompletionRequest(
            (CodexHeaders.SessionHeaderHyphen, "fixed-session"));
        var finalRequest2 = Simulate(sim, request2);
        var turnMetadata2 = ParseTurnMetadata(finalRequest2);
        Assert.Equal(turnMetadata.TurnStartedAtUnixMs, turnMetadata2.TurnStartedAtUnixMs);
    }

    [Fact]
    public void TestCodexOutbound_SessionIDPrecedence_InboundSessionIdHeaderIsUsed()
    {
        var sim = NewSimulator(() => "context-session");
        var request = CodexTestSupport.NewChatCompletionRequest(
            (CodexHeaders.SessionHeader, "header-session"));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            "header-session",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));
    }

    [Fact]
    public void TestCodexOutbound_SessionIDPrecedence_InboundHyphenatedHeaderIsUsed()
    {
        var sim = NewSimulator(() => "context-session");
        var request = CodexTestSupport.NewChatCompletionRequest(
            (CodexHeaders.SessionHeaderHyphen, "hyphen-session"));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            "hyphen-session",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));
    }

    [Fact]
    public void TestCodexOutbound_SessionIDPrecedence_TurnMetadataSessionIsUsed()
    {
        var sim = NewSimulator(() => "context-session");
        var request = CodexTestSupport.NewChatCompletionRequest(
            (CodexHeaders.TurnMetadataHeader, """{"session_id":"turn-session","turn_id":"turn-123"}"""));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            "turn-session",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));
    }

    [Fact]
    public void TestCodexOutbound_SessionIDPrecedence_ContextSessionIsUsed()
    {
        var sim = NewSimulator(() => "context-session");
        var request = CodexTestSupport.NewChatCompletionRequest();

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            "context-session",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));
    }

    [Fact]
    public void TestCodexOutbound_SessionIDPrecedence_InvalidTurnMetadataFallsBackToContext()
    {
        var sim = NewSimulator(() => "context-session");
        var request = CodexTestSupport.NewChatCompletionRequest(
            (CodexHeaders.TurnMetadataHeader, """{"session_id":"""));

        var finalRequest = Simulate(sim, request);

        Assert.Equal(
            "context-session",
            HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));
    }

    [Fact]
    public void TestCodexOutbound_SessionIDPrecedence_GeneratesUuid()
    {
        var sim = NewSimulator();
        var request = CodexTestSupport.NewChatCompletionRequest();

        var finalRequest = Simulate(sim, request);

        var sessionId = HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeaderHyphen);
        Assert.NotEmpty(sessionId);
        Assert.True(Guid.TryParse(sessionId, out _), "Session-Id must be a UUID");
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(finalRequest.Headers, CodexHeaders.SessionHeader));

        Assert.Equal(sessionId, HttpClientUtils.HeaderGet(finalRequest.Headers, "Conversation_id"));
        Assert.Equal(CodexConstants.DefaultVersion, HttpClientUtils.HeaderGet(finalRequest.Headers, "Version"));
    }

    /// <summary>Port of <c>newCodexSimulator</c>.</summary>
    private static CodexOutboundTransformer NewSimulator(Func<string?>? sessionId = null)
        => CodexTestSupport.NewTestCodexOutbound(sessionId);

    private static BuiltHttpRequest Simulate(CodexOutboundTransformer outbound, HttpRequestData request)
        => CodexTestSupport.Simulate(new OpenAiInboundTransformer(), outbound, request);

    private static TurnMetadata ParseTurnMetadata(BuiltHttpRequest request)
    {
        var metadata = LlmJson.Deserialize<TurnMetadata>(
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.TurnMetadataHeader));
        Assert.NotNull(metadata);
        return metadata!;
    }
}
