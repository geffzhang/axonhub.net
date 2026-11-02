using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the xAI subscription outbound
/// (llm/transformer/xai/subscription/outbound_test.go): the Responses protocol
/// on the Grok CLI endpoint with the OAuth bearer and CLI identity headers, and
/// rejection of every non-chat request type.
/// </summary>
public sealed class XaiSubscriptionOutboundTests
{
    [Fact]
    public void TransformRequest_uses_xAI_CLI_responses_identity()
    {
        const string accessToken = "synthetic-access-token";
        var transformer = SubscriptionOutboundTransformer.NewOutboundTransformer(
            new StaticTokenProvider(new OAuthCredentials
            {
                AccessToken = accessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            }));
        var request = new LlmRequest
        {
            Model = "grok-4.5",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("hello") }],
            Stream = true,
        };

        var httpRequest = transformer.TransformRequest(request);

        Assert.Equal(SubscriptionConstants.DefaultBaseUrl + "/responses", httpRequest.Url);
        Assert.Equal(accessToken, httpRequest.Auth?.ApiKey);
        Assert.Equal(
            SubscriptionConstants.CliTokenAuth,
            HttpClientUtils.HeaderGet(httpRequest.Headers, SubscriptionConstants.CliTokenAuthHeader));
        Assert.Equal(
            SubscriptionConstants.CliClientVersion,
            HttpClientUtils.HeaderGet(httpRequest.Headers, SubscriptionConstants.CliClientVersionHeader));
        Assert.Equal(
            SubscriptionConstants.CliClientIdentifier,
            HttpClientUtils.HeaderGet(httpRequest.Headers, SubscriptionConstants.CliClientIdentifierHeader));
        Assert.Equal(
            SubscriptionConstants.CliUserAgent,
            HttpClientUtils.HeaderGet(httpRequest.Headers, "User-Agent"));
    }

    [Fact]
    public void ApiFormat_returns_responses()
    {
        var transformer = SubscriptionOutboundTransformer.NewOutboundTransformer(
            new StaticTokenProvider(new OAuthCredentials { AccessToken = "synthetic-token" }));

        Assert.Equal(ApiFormats.OpenAIResponse, transformer.ApiFormat);
    }

    [Theory]
    [InlineData(RequestTypes.Image)]
    [InlineData(RequestTypes.Compact)]
    public void TransformRequest_rejects_non_chat_requests(string requestType)
    {
        var transformer = SubscriptionOutboundTransformer.NewOutboundTransformer(
            new StaticTokenProvider(new OAuthCredentials { AccessToken = "synthetic-token" }));

        var error = Assert.Throws<TransformerException>(
            () => transformer.TransformRequest(new LlmRequest
            {
                RequestType = requestType,
                Model = "synthetic-model",
            }));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(
            "invalid request: xAI subscription does not support " + requestType + " requests",
            error.Message);
    }

    [Fact]
    public void NewOutboundTransformer_requires_token_provider()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SubscriptionOutboundTransformer.NewOutboundTransformer(null));

        Assert.Equal("token provider is required", error.Message);
    }

    // Port-added: the reference resolves the token on every transform, so a
    // refresh between two requests must be visible on the wire.
    [Fact]
    public void TransformRequest_reads_the_token_on_every_call()
    {
        var transformer = SubscriptionOutboundTransformer.NewOutboundTransformer(
            new CountingTokenGetter("token-1", "token-2"));

        Assert.Equal("token-1", transformer.TransformRequest(Request()).Auth?.ApiKey);
        Assert.Equal("token-2", transformer.TransformRequest(Request()).Auth?.ApiKey);
    }

    private static LlmRequest Request() => new()
    {
        Model = "grok-4.5",
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("hello") }],
    };

    private sealed class CountingTokenGetter : ITokenGetter
    {
        private readonly string[] _tokens;

        private int _index;

        public CountingTokenGetter(params string[] tokens) => _tokens = tokens;

        public OAuthCredentials Get(CancellationToken cancellationToken)
            => new() { AccessToken = _tokens[Math.Min(_index++, _tokens.Length - 1)] };
    }
}
