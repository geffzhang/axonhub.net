using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI.Copilot;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the github_copilot branch of the channel transformer switch
/// (channel_llm.go:670-674, 1218-1276): OAuth-only admission, the typed-oauth
/// re-marshal without id_token, the credential parse and the token provider
/// wired to the channel's executor. The reference requires a non-empty legacy
/// apiKey even when a typed oauth object is present; the port keeps that quirk.
/// </summary>
public sealed class GithubCopilotChannelWiringTests
{
    private const string ValidOAuth = """
        {"access_token":"ghu_test","refresh_token":"ghr_test","expires_at":"2099-01-01T00:00:00Z","client_id":"Iv1.test","token_type":"bearer","scopes":["read:user"]}
        """;

    private static ChannelCandidate Candidate(string credentials, string baseUrl = "")
        => new()
        {
            ChannelId = 99,
            ChannelName = "github_copilot-wiring",
            ActualModel = "gpt-5.4",
            Type = ChannelOutboundFactory.GithubCopilotChannelType,
            BaseUrl = baseUrl,
            Credentials = credentials,
        };

    private static LlmRequest ChatRequest(string model = "gpt-5.4")
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static ScriptedExecutor TokenExecutor(string accessToken = "ghu_test")
        => new()
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = Encoding.UTF8.GetBytes(
                    $$"""{"token":"copilot_token_{{accessToken}}","expires_at":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""),
            },
        };

    [Fact]
    public void TypedOAuthBuildsTheCopilotTransformer()
    {
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";
        var executor = TokenExecutor();

        var transformer = Assert.IsType<CopilotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                executor));

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);
        Assert.Equal(CopilotConstants.DefaultCopilotBaseUrl, transformer.BaseUrl);

        var httpRequest = transformer.TransformRequest(ChatRequest("gpt-4o"));

        Assert.Equal("https://api.githubcopilot.com/chat/completions", httpRequest.Url);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("copilot_token_ghu_test", httpRequest.Auth.ApiKey);
        Assert.Equal(
            CopilotConstants.DefaultEditorVersion,
            HttpClientUtils.HeaderGet(httpRequest.Headers, CopilotConstants.EditorVersionHeader));

        // The token exchange used the channel's executor and the OAuth access
        // token, not the placeholder apiKey.
        var exchangeRequest = Assert.Single(executor.Requests);
        Assert.Equal(CopilotConstants.DefaultCopilotTokenEndpoint, exchangeRequest.Url);
        Assert.Equal(
            "token ghu_test",
            HttpClientUtils.HeaderGet(exchangeRequest.Headers, "Authorization"));
    }

    [Fact]
    public void LegacyOAuthJsonInTheApiKeyIsAccepted()
    {
        var credentials = """
            {"apiKey":"{\"access_token\":\"ghu_legacy\",\"refresh_token\":\"ghr_legacy\",\"expires_at\":\"2099-01-01T00:00:00Z\"}"}
            """;
        var executor = TokenExecutor("ghu_legacy");

        var transformer = Assert.IsType<CopilotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                executor));

        var httpRequest = transformer.TransformRequest(ChatRequest("gpt-4o"));

        Assert.Equal("copilot_token_ghu_legacy", httpRequest.Auth!.ApiKey);
    }

    [Fact]
    public void ResponsesModelsRouteToTheResponsesApi()
    {
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";
        var executor = TokenExecutor();

        var transformer = Assert.IsType<CopilotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                executor));

        var httpRequest = transformer.TransformRequest(ChatRequest("gpt-5.4"));

        Assert.Equal("https://api.githubcopilot.com/v1/responses", httpRequest.Url);
        Assert.Equal(ApiFormats.OpenAIResponse, httpRequest.ApiFormat);
        Assert.Equal("copilot_token_ghu_test", httpRequest.Auth!.ApiKey);
    }

    [Fact]
    public void CustomBaseUrlIsPreserved()
    {
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";
        var executor = TokenExecutor();

        var transformer = Assert.IsType<CopilotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(credentials, "https://custom.test/"),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                executor));

        Assert.Equal("https://custom.test", transformer.BaseUrl);

        var httpRequest = transformer.TransformRequest(ChatRequest("gpt-4o"));
        Assert.Equal("https://custom.test/chat/completions", httpRequest.Url);
    }

    [Fact]
    public void ASharedExchangerCacheReusesTheCopilotTokenAcrossBuilds()
    {
        // channel_llm.go:1218-1276 keeps the provider — and its SHA-256 keyed
        // Copilot-token cache — on the enabled-channel cache entry, so a second
        // attempt must not exchange the GitHub access token again (§194).
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";
        var cache = new ChannelCopilotTokenCache();
        var executor = TokenExecutor();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var transformer = Assert.IsType<CopilotOutboundTransformer>(
                ChannelOutboundFactory.Create(
                    Candidate(credentials),
                    ChatRequest(),
                    DateTimeOffset.UnixEpoch,
                    executor,
                    copilotTokens: cache));

            var httpRequest = transformer.TransformRequest(ChatRequest("gpt-4o"));
            Assert.Equal("copilot_token_ghu_test", httpRequest.Auth!.ApiKey);
        }

        Assert.Single(executor.Requests);
    }

    [Fact]
    public void WithoutTheCacheEveryBuildExchangesAgain()
    {
        // Direct callers and tests keep the reference's per-provider exchanger.
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";
        var executor = TokenExecutor();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var transformer = Assert.IsType<CopilotOutboundTransformer>(
                ChannelOutboundFactory.Create(
                    Candidate(credentials),
                    ChatRequest(),
                    DateTimeOffset.UnixEpoch,
                    executor));

            transformer.TransformRequest(ChatRequest("gpt-4o"));
        }

        Assert.Equal(2, executor.Requests.Count);
    }

    [Fact]
    public void AChannelRowUpdateReplacesTheCachedExchanger()
    {
        // The reference rebuilds the provider when the enabled-channel cache is
        // invalidated by a channel update, discarding the exchange cache; the
        // cache entry follows the row timestamp for the same effect.
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";
        var cache = new ChannelCopilotTokenCache();
        var executor = TokenExecutor();

        var first = Candidate(credentials);
        first.UpdatedAt = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var second = Candidate(credentials);
        second.UpdatedAt = first.UpdatedAt.AddMinutes(1);

        foreach (var candidate in new[] { first, second })
        {
            var transformer = Assert.IsType<CopilotOutboundTransformer>(
                ChannelOutboundFactory.Create(
                    candidate,
                    ChatRequest(),
                    DateTimeOffset.UnixEpoch,
                    executor,
                    copilotTokens: cache));

            transformer.TransformRequest(ChatRequest("gpt-4o"));
        }

        Assert.Equal(2, executor.Requests.Count);
    }

    [Fact]
    public void AnApiKeyWithoutOAuthIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate("""{"apiKey":"plain-key"}"""),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                TokenExecutor()));

        Assert.Equal(
            "missing oauth credentials for channel github_copilot-wiring",
            error.Message);
    }

    [Fact]
    public void ATypedOAuthChannelWithoutALegacyApiKeyIsRejected()
    {
        // The reference checks c.Credentials.APIKey before re-marshalling the
        // typed oauth object, so a typed-only channel reports "no credentials".
        var credentials = "{\"oauth\":" + ValidOAuth + "}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                TokenExecutor()));

        Assert.Equal(
            "github_copilot channel github_copilot-wiring has no credentials",
            error.Message);
    }

    [Fact]
    public void MalformedLegacyOAuthJsonIsRejectedAsInvalidCredentials()
    {
        // IsOAuth recognises the {..."access_token"... fragment, then the parse
        // fails with the channel-specific message.
        var credentials = "{\"apiKey\":\"{\\\"access_token\\\":\"}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                TokenExecutor()));

        Assert.StartsWith(
            "github_copilot channel github_copilot-wiring has invalid credentials: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedTypedOAuthJsonIsRejectedAtEncoding()
    {
        var credentials =
            "{\"apiKey\":\"placeholder\",\"oauth\":{\"access_token\":\"ghu_test\",\"expires_at\":\"not-a-time\"}}";

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch,
                TokenExecutor()));

        Assert.StartsWith(
            "failed to encode github_copilot oauth credentials for channel github_copilot-wiring: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformRequestWithoutATokenExecutorReportsTheMissingClient()
    {
        // The reference falls back to a default http client; the port has no
        // transport without an injected executor (C107).
        var credentials = "{\"apiKey\":\"placeholder\",\"oauth\":" + ValidOAuth + "}";

        var transformer = Assert.IsType<CopilotOutboundTransformer>(
            ChannelOutboundFactory.Create(
                Candidate(credentials),
                ChatRequest(),
                DateTimeOffset.UnixEpoch));

        var error = Assert.Throws<AxonHub.Llm.Pipeline.WrappedException>(
            () => transformer.TransformRequest(ChatRequest("gpt-4o")));

        Assert.Contains("failed to get copilot token:", error.Message);
        Assert.Contains("http client is nil", error.Message);
    }
}
