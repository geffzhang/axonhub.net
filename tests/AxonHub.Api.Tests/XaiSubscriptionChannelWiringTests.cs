using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the xai_subscription branch of the channel transformer switch
/// (channel_llm.go:666-669, 867-884; objects.ChannelCredentials
/// .ResolveOAuthCredentials): the channel is OAuth-only, a typed oauth object
/// with a non-blank access token wins over the legacy apiKey document, the
/// stored base URL is ignored in favour of the Grok CLI endpoint, and only
/// chat/Responses requests pass the method gate.
/// </summary>
public sealed class XaiSubscriptionChannelWiringTests
{
    private const string OAuthCredentials =
        """{"apiKey":"legacy-key","oauth":{"access_token":"oauth-access","expires_at":"2099-01-01T00:00:00Z"}}""";

    private static ChannelCandidate Candidate(string credentials) => new()
    {
        ChannelId = 93,
        ChannelName = "xai_subscription-wiring",
        ActualModel = "grok-4.5",
        Type = ChannelOutboundFactory.XaiSubscriptionChannelType,
        BaseUrl = "https://ignored.example.com/v1",
        Credentials = credentials,
    };

    private static LlmRequest ChatRequest() => new()
    {
        Model = "grok-4.5",
        ApiFormat = ApiFormats.OpenAIResponse,
        RequestType = RequestTypes.Chat,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    private static IOutboundTransformer Create(string credentials, LlmRequest? request = null)
        => ChannelOutboundFactory.Create(
            Candidate(credentials),
            request ?? ChatRequest(),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void RejectsApiKeyOnlyCredentials()
    {
        // channel_llm.go:666-669: xai_subscription is OAuth-only, and its own
        // message wins over the generic api-key check.
        var error = Assert.Throws<InvalidOperationException>(
            () => Create("""{"apiKey":"plain-key"}"""));

        Assert.Equal(
            "missing oauth credentials for channel xai_subscription-wiring",
            error.Message);
    }

    [Fact]
    public void WrapsInvalidCredentialDocuments()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Create("""{"apiKey":"{\"access_token\": 123}"}"""));

        Assert.StartsWith(
            "xAI subscription channel xai_subscription-wiring has invalid credentials: ",
            error.Message);
    }

    [Fact]
    public void PrefersTheOAuthObjectOverTheLegacyApiKey()
    {
        var transformer = Assert.IsType<SubscriptionOutboundTransformer>(
            Create(OAuthCredentials));

        Assert.Equal(
            "oauth-access",
            transformer.TransformRequest(ChatRequest()).Auth?.ApiKey);
    }

    [Fact]
    public void FallsBackToOAuthCredentialsInTheLegacyApiKey()
    {
        // objects/channel.go:460-468: no typed oauth object, so the apiKey
        // string is parsed as the credentials document.
        var transformer = Assert.IsType<SubscriptionOutboundTransformer>(
            Create(
                """{"apiKey":"{\"access_token\":\"legacy-access\",\"expires_at\":\"2099-01-01T00:00:00Z\"}"}"""));

        Assert.Equal(
            "legacy-access",
            transformer.TransformRequest(ChatRequest()).Auth?.ApiKey);
    }

    [Fact]
    public void FallsBackWhenTheOAuthObjectHasNoAccessToken()
    {
        // The Go check trims before deciding, so a whitespace-only access
        // token falls through to the legacy apiKey document.
        var transformer = Assert.IsType<SubscriptionOutboundTransformer>(
            Create(
                """{"apiKey":"{\"access_token\":\"legacy-access\",\"expires_at\":\"2099-01-01T00:00:00Z\"}","oauth":{"access_token":"   ","expires_at":"2099-01-01T00:00:00Z"}}"""));

        Assert.Equal(
            "legacy-access",
            transformer.TransformRequest(ChatRequest()).Auth?.ApiKey);
    }

    [Fact]
    public void IgnoresTheStoredBaseUrl()
    {
        // channel_llm.go:867-884: the outbound is pinned to the Grok CLI
        // endpoint; the channel row's base URL is not consulted.
        var transformer = Assert.IsType<SubscriptionOutboundTransformer>(
            Create(OAuthCredentials));

        Assert.Equal(
            SubscriptionConstants.DefaultBaseUrl + "/responses",
            transformer.TransformRequest(ChatRequest()).Url);
    }

    [Theory]
    [InlineData(RequestTypes.Image)]
    [InlineData(RequestTypes.Compact)]
    public void RejectsNonChatRequestsThroughTheFactory(string requestType)
    {
        var transformer = Create(OAuthCredentials);

        var error = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(new LlmRequest
            {
                RequestType = requestType,
                Model = "grok-4.5",
            }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(
            "invalid request: xAI subscription does not support " + requestType + " requests",
            error.Message);
    }

    [Fact]
    public void MissingCredentialsAreRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Create("{}"));

        Assert.Equal(
            "missing oauth credentials for channel xai_subscription-wiring",
            error.Message);
    }
}
