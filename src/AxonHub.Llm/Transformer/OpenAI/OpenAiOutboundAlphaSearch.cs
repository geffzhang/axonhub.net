using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of the alpha search half of llm/transformer/openai/alpha_search_outbound.go:
/// the opaque request body is replayed upstream with only the routed model
/// patched in, and the upstream body is wrapped verbatim for the client.
/// </summary>
public sealed partial class OpenAiOutboundTransformer
{
    /// <summary>Port of transformAlphaSearchRequest.</summary>
    private HttpRequestData BuildAlphaSearchApiRequest(LlmRequest request)
    {
        if (request.AlphaSearch is null || request.AlphaSearch.Body.Length == 0)
        {
            throw new InvalidOperationException("alpha search request is nil");
        }

        byte[] body;
        try
        {
            body = RawJsonModelPatch.SetModel(request.AlphaSearch.Body, request.Model);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "failed to patch alpha search model: " + ex.Message, ex);
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildAlphaSearchUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
        };
    }

    /// <summary>Port of buildAlphaSearchURL.</summary>
    private string BuildAlphaSearchUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/alpha/search";

    /// <summary>
    /// Port of transformAlphaSearchResponse. The caller dispatches here before
    /// the generic status/empty-body checks, because alpha search owns its error
    /// conversion: the upstream body can carry provider-specific details the
    /// generic "HTTP error N" wording would drop.
    /// </summary>
    private LlmResponse TransformAlphaSearchResponse(HttpResponseData response)
    {
        if (response.StatusCode >= 400)
        {
            var httpError = new HttpClientError(
                string.Empty,
                string.Empty,
                response.StatusCode,
                GoHttpStatus.Text(response.StatusCode),
                response.Body);
            throw TransformError(httpError);
        }

        var body = response.Body ?? [];
        if (body.Length == 0)
        {
            throw new InvalidOperationException("alpha search response body is empty");
        }

        return new LlmResponse
        {
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
            AlphaSearch = new AlphaSearchResponse
            {
                Body = (byte[])body.Clone(),
            },
        };
    }
}
