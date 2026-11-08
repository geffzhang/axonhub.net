using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>
/// Port of subscription.OutboundTransformer (outbound.go:17-86): the xAI CLI
/// Responses transport. Everything but the method gate, the bearer token and
/// the CLI identity headers delegates to the OpenAI Responses transformer.
/// </summary>
public sealed class SubscriptionOutboundTransformer : IOutboundTransformer
{
    private readonly ITokenGetter _tokens;
    private readonly ResponsesOutboundTransformer _responses;

    private SubscriptionOutboundTransformer(
        ITokenGetter tokens,
        ResponsesOutboundTransformer responses)
    {
        _tokens = tokens;
        _responses = responses;
    }

    /// <summary>
    /// Port of <c>NewOutboundTransformer</c> (outbound.go:22-36). The session
    /// seams are port-only (C062): the reference's inner Responses transformer
    /// reads them from the ctx the wrapper forwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">No token provider was given.</exception>
    public static SubscriptionOutboundTransformer NewOutboundTransformer(
        ITokenGetter? tokenProvider,
        Func<string?>? sessionIdProvider = null,
        Func<string?>? sessionScopeProvider = null)
    {
        if (tokenProvider is null)
        {
            throw new InvalidOperationException("token provider is required");
        }

        // Go: responses.NewOutboundTransformerWithConfig{BaseURL: DefaultBaseURL,
        // APIKeyProvider: static "unused"} — the real key is the OAuth Bearer
        // applied below.
        var responses = ResponsesOutboundTransformer.Create(
            SubscriptionConstants.DefaultBaseUrl,
            "unused",
            sessionIdProvider: sessionIdProvider,
            sessionScopeProvider: sessionScopeProvider);

        return new SubscriptionOutboundTransformer(tokenProvider, responses);
    }

    public string ApiFormat => ApiFormats.OpenAIResponse;

    /// <summary>Port of TransformRequest (outbound.go:42-69).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is not null)
        {
            // The subscription proxy only exposes text Responses.
            switch (request.RequestType)
            {
                case null or "" or RequestTypes.Chat:
                    break;
                default:
                    throw TransformerErrors.InvalidRequest(
                        "xAI subscription does not support " + request.RequestType + " requests");
            }
        }

        // The port's transformer interface carries no context; the reference
        // reads the token through ctx only for cancellation (C094-family).
        var credentials = _tokens.Get(CancellationToken.None);

        var httpRequest = _responses.TransformRequest(request);

        httpRequest.Auth = new AuthConfig
        {
            Type = AuthTypes.Bearer,
            ApiKey = credentials.AccessToken,
        };
        var headers = httpRequest.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(
            headers,
            SubscriptionConstants.CliTokenAuthHeader,
            SubscriptionConstants.CliTokenAuth);
        HttpClientUtils.HeaderSet(
            headers,
            SubscriptionConstants.CliClientVersionHeader,
            SubscriptionConstants.CliClientVersion);
        HttpClientUtils.HeaderSet(
            headers,
            SubscriptionConstants.CliClientIdentifierHeader,
            SubscriptionConstants.CliClientIdentifier);
        HttpClientUtils.HeaderSet(headers, "User-Agent", SubscriptionConstants.CliUserAgent);

        return httpRequest;
    }

    public LlmResponse TransformResponse(HttpResponseData? response)
        => _responses.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _responses.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error)
        => _responses.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _responses.AggregateStreamChunks(request, chunks, out meta);
}
