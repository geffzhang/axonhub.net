using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Go: <c>codex.ImageParams</c>.</summary>
public sealed class CodexImageParams
{
    public ITokenGetter? TokenProvider { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string EndpointPath { get; set; } = string.Empty;
}

/// <summary>
/// Port of codex/image_outbound.go: Codex channels serve native Images requests
/// through OpenAI's image transformer while reusing the Codex OAuth token and
/// identity headers. Responses image-tool bridging is no longer used.
/// </summary>
public sealed class CodexImageOutboundTransformer : IOutboundTransformer, IPassThroughBodyPolicy
{
    private readonly OpenAiOutboundTransformer _inner;
    private readonly ITokenGetter _tokens;

    public CodexImageOutboundTransformer(CodexImageParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _tokens = parameters.TokenProvider
            ?? throw new InvalidOperationException("token provider is required");

        var baseUrl = parameters.BaseUrl;
        if (baseUrl.Length == 0 || baseUrl == "https://api.openai.com/v1")
        {
            baseUrl = CodexOutboundTransformer.CodexBaseUrl;
        }

        baseUrl = baseUrl
            .Replace("wss://", "https://", StringComparison.Ordinal)
            .Replace("ws://", "http://", StringComparison.Ordinal);
        _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = baseUrl,
            EndpointPath = parameters.EndpointPath,
            ImageEditJson = true,
            ApiKeyProvider = new StaticApiKeyProvider(string.Empty),
        });
    }

    public string ApiFormat => _inner.ApiFormat;

    /// <summary>Go: imageOutbound.AllowPassThroughBody.</summary>
    public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
        => llmRequest?.ApiFormat != ApiFormats.OpenAIImageEdit;

    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null || request.RequestType != RequestTypes.Image)
        {
            throw TransformerErrors.InvalidRequest(
                "native Images endpoint requires an image request");
        }

        if (request.Stream == true)
        {
            throw TransformerErrors.InvalidRequest("image request does not support streaming");
        }

        var credentials = _tokens.Get(request.RequestCancellationToken);
        var httpRequest = _inner.TransformRequest(request);
        httpRequest.Auth = new AuthConfig
        {
            Type = AuthTypes.Bearer,
            ApiKey = credentials.AccessToken,
        };

        var rawHeaders = request.RawRequest?.Headers;
        var headers = httpRequest.Headers
            ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        httpRequest.Headers = headers;
        HttpClientUtils.HeaderSet(headers, "Originator", CodexConstants.AxonHubOriginator);

        var passthrough = new List<string> { "Originator", "User-Agent" };
        passthrough.AddRange(CodexHeaders.PassthroughHeaders);
        foreach (var name in passthrough)
        {
            var value = HttpClientUtils.HeaderGet(rawHeaders, name);
            if (value.Length > 0)
            {
                HttpClientUtils.HeaderSet(headers, name, value);
            }
        }

        var session = HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.SessionHeaderHyphen);
        if (session.Length == 0)
        {
            session = HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.SessionHeader);
        }

        if (session.Length == 0)
        {
            session = CodexHeaders.ExtractSessionIdFromTurnMetadata(
                HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.TurnMetadataHeader));
        }

        if (session.Length == 0)
        {
            session = request.RawRequest?.SessionId ?? string.Empty;
        }

        if (session.Length == 0)
        {
            session = Guid.NewGuid().ToString();
        }

        HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, session);

        var accountId = CodexUtils.ExtractChatGptAccountIdFromJwt(credentials.AccessToken);
        if (accountId.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, "Chatgpt-Account-Id", accountId);
        }

        return httpRequest;
    }

    public LlmResponse TransformResponse(HttpResponseData? response)
        => _inner.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error)
        => _inner.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);
}
