using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the codex/fenno half of the channel wiring against the reference:
/// <c>buildCodexOutbound</c> (channel_llm.go:296-387), the codex dispatch in
/// <c>buildChannelWithOutbounds</c> (channel_llm.go:448-520), and the
/// <c>TypeFenno</c> test (channel_llm_fenno_test.go). The reference builds these
/// outbounds while loading a channel; the port re-dispatches per attempt, so the
/// same decisions are locked through <see cref="ChannelOutboundFactory"/>.
/// </summary>
public sealed class CodexChannelWiringTests
{
    private static ChannelCandidate Candidate(
        string type,
        string credentials = """{"apiKey":"sk-codex"}""",
        string? baseUrl = "https://relay.example/backend-api/codex#",
        string defaultTestModel = "") => new()
        {
            ChannelId = 1,
            ChannelName = "codex-test",
            ActualModel = "gpt-5.4",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials,
            DefaultTestModel = defaultTestModel,
        };

    private static LlmRequest Request(string apiFormat, string model = "gpt-5.4")
        => new() { Model = model, ApiFormat = apiFormat };

    private static CodexOutboundTransformer Create(
        ChannelCandidate candidate,
        LlmRequest request)
        => Assert.IsType<CodexOutboundTransformer>(
            ChannelOutboundFactory.Create(candidate, request, DateTimeOffset.UnixEpoch));

    [Theory]
    [InlineData(ChannelOutboundFactory.CodexChannelType, ApiFormats.OpenAIResponse)]
    [InlineData(ChannelOutboundFactory.CodexChannelType, ApiFormats.OpenAIAlphaSearch)]
    [InlineData(ChannelOutboundFactory.CodexChannelType, ApiFormats.OpenAIImageGeneration)]
    [InlineData(ChannelOutboundFactory.CodexChannelType, ApiFormats.OpenAIImageEdit)]
    [InlineData(ChannelOutboundFactory.FennoChannelType, ApiFormats.OpenAIResponse)]
    [InlineData(ChannelOutboundFactory.FennoChannelType, ApiFormats.OpenAIImageGeneration)]
    [InlineData(ChannelOutboundFactory.FennoChannelType, ApiFormats.OpenAIImageEdit)]
    public void CodexAndFennoChannelsUseTheCodexOutbound(string type, string apiFormat)
    {
        var transformer = Create(Candidate(type), Request(apiFormat));

        // The codex outbound always advertises the Responses format, exactly like
        // codex.OutboundTransformer.APIFormat().
        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);
        Assert.True(transformer.SupportsCodexResponseHeaders());
    }

    [Fact]
    public void FennoChannelsDoNotServeAlphaSearch()
    {
        // channel_llm.go: only the codex branch has an alpha-search endpoint;
        // the fenno channel's endpoint map is responses-only
        // (channel_endpoint_mapping_test.go).
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.FennoChannelType),
                Request(ApiFormats.OpenAIAlphaSearch),
                DateTimeOffset.UnixEpoch));

        Assert.Contains("fenno", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FennoChannelBuildsACodexResponsesRequest()
    {
        // Port of TestFennoChannel_UsesThirdPartyCodexTransformer: the base URL
        // is normalized with /v1 and the api key is sent as a bearer token.
        var transformer = Create(
            Candidate(
                ChannelOutboundFactory.FennoChannelType,
                credentials: """{"apiKey":"test-key"}""",
                baseUrl: "https://api.fenno.ai",
                defaultTestModel: "gpt-5.2-codex"),
            Request(ApiFormats.OpenAIResponse, "gpt-5.2-codex"));

        var request = transformer.TransformRequest(new LlmRequest
        {
            Model = "gpt-5.2-codex",
            ApiFormat = ApiFormats.OpenAIResponse,
            Messages = [new Message { Role = "user", Content = new MessageContent { Content = "hello" } }],
        });

        Assert.Equal("https://api.fenno.ai/v1/responses", request.Url);
        Assert.Equal(AuthTypes.Bearer, request.Auth?.Type);
        Assert.Equal("test-key", request.Auth?.ApiKey);
    }

    [Fact]
    public void CodexOutboundUsesTheWireRequestSessionId()
    {
        // ChatCompletionEndpoint copies the trace id published by TraceMiddleware
        // onto the wire request's session seam; the codex outbound reads it back
        // through SessionIdProvider (channel_llm.go's buildCodexOutbound wiring).
        var raw = TestChannels.ChatRequest("gpt-5.4");
        raw.SessionId = "trace-session";
        var request = new LlmRequest
        {
            Model = "gpt-5.4",
            ApiFormat = ApiFormats.OpenAIResponse,
            Messages = [new Message { Role = "user", Content = new MessageContent { Content = "hi" } }],
            RawRequest = raw,
        };

        var transformer = Create(Candidate(ChannelOutboundFactory.CodexChannelType), request);
        var http = transformer.TransformRequest(request);

        Assert.Equal(
            "trace-session",
            HttpClientUtils.HeaderGet(http.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(
            "trace-session",
            HttpClientUtils.HeaderGet(http.Headers, CodexHeaders.ThreadIdHeader));
    }

    [Fact]
    public void CodexApiKeyChannelUsesTheApiKeyTokenProvider()
    {
        var transformer = Create(Candidate(ChannelOutboundFactory.CodexChannelType), Request(ApiFormats.OpenAIResponse));

        var tokens = Assert.IsType<ApiKeyTokenProvider>(transformer.TokenProvider);
        Assert.Equal("sk-codex", tokens.Get(CancellationToken.None).AccessToken);
    }

    [Fact]
    public void CodexOAuthObjectChannelUsesARefreshingTokenProvider()
    {
        const string Credentials = """
            {
              "oauth": {
                "client_id": "codex-client",
                "access_token": "oauth-access",
                "refresh_token": "oauth-refresh",
                "expires_at": "2030-01-01T00:00:00Z",
                "token_type": "Bearer"
              }
            }
            """;

        // An oauth object alone is valid credentials for a codex channel, so the
        // api-key requirement does not apply (channel_llm.go:662-665).
        var transformer = Create(
            Candidate(ChannelOutboundFactory.CodexChannelType, credentials: Credentials),
            Request(ApiFormats.OpenAIResponse));

        var tokens = Assert.IsType<TokenProvider>(transformer.TokenProvider);
        var credentials = tokens.Get(CancellationToken.None);
        Assert.Equal("oauth-access", credentials.AccessToken);
        Assert.Equal("oauth-refresh", credentials.RefreshToken);
        Assert.Equal("codex-client", credentials.ClientId);
        Assert.Equal("Bearer", credentials.TokenType);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), credentials.ExpiresAt);
    }

    [Fact]
    public void CodexLegacyOAuthJsonApiKeyIsParsedAsOAuth()
    {
        var legacyJson = JsonSerializer.Serialize(new
        {
            access_token = "legacy-access",
            refresh_token = "legacy-refresh",
            expires_at = "2030-01-01T00:00:00Z",
        });
        var credentials = JsonSerializer.Serialize(new { apiKey = legacyJson });

        var transformer = Create(
            Candidate(ChannelOutboundFactory.CodexChannelType, credentials: credentials),
            Request(ApiFormats.OpenAIResponse));

        var tokens = Assert.IsType<TokenProvider>(transformer.TokenProvider);
        var parsed = tokens.Get(CancellationToken.None);
        Assert.Equal("legacy-access", parsed.AccessToken);
        Assert.Equal("legacy-refresh", parsed.RefreshToken);
    }

    [Fact]
    public void CodexOAuthObjectTakesPrecedenceOverTheLegacyApiKey()
    {
        // Port of TestChannelCredentialsResolveOAuthCredentialsPrefersNonEmptyOAuth:
        // buildCodexOutbound re-marshals the typed oauth object, never the
        // legacy apiKey, when one is configured.
        var legacyJson = JsonSerializer.Serialize(new
        {
            access_token = "legacy-access",
            expires_at = "2030-01-01T00:00:00Z",
        });
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["oauth"] = new { access_token = "current-access", expires_at = "2030-01-01T00:00:00Z" },
            ["apiKey"] = legacyJson,
        });

        var transformer = Create(
            Candidate(ChannelOutboundFactory.CodexChannelType, credentials: credentials),
            Request(ApiFormats.OpenAIResponse));

        var tokens = Assert.IsType<TokenProvider>(transformer.TokenProvider);
        Assert.Equal("current-access", tokens.Get(CancellationToken.None).AccessToken);
    }

    [Fact]
    public void CodexChannelWithoutCredentialsUsesTheOAuthOrKeyMessage()
    {
        // Port of channel_llm.go:662-665: codex channels accept either credential
        // kind, so their missing-credentials message says both.
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.CodexChannelType, credentials: "{}"),
                Request(ApiFormats.OpenAIResponse),
                DateTimeOffset.UnixEpoch));

        Assert.Equal(
            "missing credentials: oauth or api key required for channel codex-test",
            error.Message);
    }

    [Fact]
    public void OtherChannelsKeepTheMissingApiKeyMessage()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(ChannelOutboundFactory.OpenAiChannelType, credentials: "{}"),
                Request(ApiFormats.OpenAIChatCompletion),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel codex-test", error.Message);
    }

    [Fact]
    public void PersistentOutboundAdvertisesCodexResponseHeadersOnlyForCodexChannels()
    {
        // Port of SupportsCodexResponseHeaders (outbound.go:505-510): the
        // orchestrator wrapper forwards the current provider transformer's
        // answer. The API layer's header forwarding itself is still a gap
        // (C094).
        var codex = new PersistentOutboundTransformer(
            [Candidate(ChannelOutboundFactory.CodexChannelType)],
            TimeProvider.System,
            new RecordingMetricsRecorder());
        codex.TransformRequest(Request(ApiFormats.OpenAIResponse));
        Assert.True(codex.SupportsCodexResponseHeaders());

        var chat = Request(ApiFormats.OpenAIChatCompletion);
        chat.Messages.Add(new Message { Role = "user", Content = new MessageContent { Content = "hello" } });

        var openAi = new PersistentOutboundTransformer(
            [Candidate(ChannelOutboundFactory.OpenAiChannelType)],
            TimeProvider.System,
            new RecordingMetricsRecorder());
        openAi.TransformRequest(chat);
        Assert.False(openAi.SupportsCodexResponseHeaders());
    }

    [Theory]
    [InlineData("wss://relay.example/backend-api/codex#", false)]
    [InlineData("WS://relay.example/backend-api/codex#", false)]
    [InlineData("https://relay.example/backend-api/codex#", true)]
    [InlineData("https://api.example.com/v1", true)]
    public void CodexTransportIsInferredFromTheBaseUrlScheme(string baseUrl, bool stripsWebSocketBeta)
    {
        // channel_llm.go:263-274 (endpointTransport): a ws/wss base URL selects
        // the websocket transport; everything else is HTTP. Only the HTTP path
        // strips the WebSocket-only beta flag (codex outbound.go:666-672).
        var transformer = Create(
            Candidate(ChannelOutboundFactory.CodexChannelType, baseUrl: baseUrl),
            Request(ApiFormats.OpenAIResponse));

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "OpenAI-Beta", "responses_websockets=2026-02-06");
        var request = new HttpRequestData { Url = "https://relay.example/backend-api/codex/responses", Headers = headers };

        var finalized = transformer.FinalizeTransportRequest(request);

        if (stripsWebSocketBeta)
        {
            Assert.NotNull(finalized);
            Assert.NotSame(request, finalized);
            Assert.False(finalized.Headers!.ContainsKey("Openai-Beta"));
            // The shallow clone leaves the original request untouched.
            Assert.True(request.Headers!.ContainsKey("Openai-Beta"));
        }
        else
        {
            Assert.Same(request, finalized);
            Assert.Equal(["responses_websockets=2026-02-06"], finalized!.Headers!["Openai-Beta"]);
        }
    }
}
