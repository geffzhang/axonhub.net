using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>
/// Port of gemini.OutboundTransformer (llm/transformer/gemini/outbound.go): the
/// native Gemini/Vertex provider protocol. The embedding and image branches
/// live in the GeminiOutboundEmbedding/GeminiOutboundImage partials; streaming
/// (outbound_stream.go/aggregator.go) lives in the GeminiOutboundStream/
/// GeminiAggregator files.
/// </summary>
public sealed partial class GeminiOutboundTransformer : IOutboundTransformer
{
    private readonly GeminiOutboundConfig _config;

    /// <summary>Port of NewOutboundTransformerWithConfig (outbound.go:90-96).</summary>
    public GeminiOutboundTransformer(GeminiOutboundConfig config)
    {
        _config = config.Cleanup();
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey) (outbound.go:56-63).</summary>
    public static GeminiOutboundTransformer Create(string baseUrl, string apiKey)
        => new(new GeminiOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider(apiKey),
        });

    /// <summary>Port of NewOutboundTransformerWithConfig (outbound.go:90-96).</summary>
    public static GeminiOutboundTransformer CreateWithConfig(GeminiOutboundConfig config) => new(config);

    /// <summary>Port of APIFormat (outbound.go:99-101).</summary>
    public string ApiFormat => ApiFormats.GeminiContents;

    /// <summary>Port of TransformRequest (outbound.go:104-192).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        var apiKey = _config.ApiKeyProvider?.Get() ?? "";

        switch (request.RequestType)
        {
            case RequestTypes.Embedding:
                return TransformEmbeddingRequest(request);
            case RequestTypes.Image:
                return BuildImageGenerationRequest(request);
            case RequestTypes.Chat:
            case "":
            case null:
                break;
            case RequestTypes.Compact:
                throw TransformerErrors.InvalidRequest("compact is only supported by OpenAI Responses API");
            default:
                throw TransformerErrors.InvalidRequest(request.RequestType + " is not supported");
        }

        if (request.Model.Length == 0)
        {
            throw new InvalidOperationException($"model is required,{request.Model}");
        }

        if (request.Messages.Count == 0)
        {
            // Go formats the (guaranteed-empty) slice: "messages are required,[]".
            throw TransformerErrors.InvalidRequest("messages are required,[]");
        }

        // Convert to Gemini request format with config
        var geminiReq = GeminiOutboundConvert.ConvertLlmToGeminiRequestWithConfig(request, _config);

        // System and developer messages become systemInstruction, so a request made
        // up only of them leaves contents empty. Gemini requires at least one
        // content entry; fail here with an error that names the cause instead of
        // forwarding an empty contents array that upstream rejects without
        // mentioning the developer role.
        if (geminiReq.Contents is not { Count: > 0 })
        {
            throw TransformerErrors.InvalidRequest(
                "contents must contain at least one user or model turn; system and developer messages are converted to systemInstruction");
        }

        // Clear function call/response IDs for Vertex AI (not supported)
        if (_config.PlatformType == GeminiConstants.PlatformVertex)
        {
            ClearFunctionIDsForVertexAI(geminiReq);
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(geminiReq);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal gemini request: " + ex.Message, ex);
        }

        // Prepare headers
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
            ["Accept"] = ["application/json"],
        };

        // Prepare authentication
        AuthConfig? authConfig = null;

        if (apiKey.Length > 0)
        {
            authConfig = new AuthConfig
            {
                Type = AuthTypes.ApiKey,
                ApiKey = apiKey,
                HeaderKey = "x-goog-api-key",
            };
        }

        if (request.RawRequest is not null)
        {
            // We need to remove the alt query parameter to avoid passing through to the backend.
            request.RawRequest.Query = null;
        }

        // Build URL
        var url = BuildFullRequestUrl(request);

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = authConfig,
            ApiFormat = ApiFormats.GeminiContents,
            SkipInboundQueryMerge = true,
            Metadata = null,
        };
    }

    /// <summary>Port of buildFullRequestURL (outbound.go:195-235).</summary>
    public string BuildFullRequestUrl(LlmRequest llmReq)
    {
        if (_config.EndpointPath is { Length: > 0 } endpointPath)
        {
            return TrimSingleTrailingSlash(_config.BaseUrl) + endpointPath;
        }

        // Determine endpoint based on streaming
        var action = llmReq.Stream == true ? "streamGenerateContent?alt=sse" : "generateContent";

        var version = _config.ApiVersion ?? "";
        if (version.Length == 0)
        {
            version = GeminiConstants.DefaultAPIVersion;

            if (llmReq.RawRequest?.RouteValues is { } routeValues
                && routeValues.TryGetValue("gemini-api-version", out var requestVersion)
                && requestVersion is { Length: > 0 })
            {
                version = requestVersion;
            }
        }

        // For Vertex AI platform, use different URL format:
        // https://${API_ENDPOINT}/v1/publishers/google/models/${MODEL_ID}:${ACTION}?key=${API_KEY}
        // If base URL starts with Cloudflare gateway, don't add /v1 prefix
        if (_config.PlatformType == GeminiConstants.PlatformVertex)
        {
            var baseUrl = TrimSingleTrailingSlash(_config.BaseUrl);
            if (baseUrl.Contains("/v1/", StringComparison.Ordinal))
            {
                return $"{baseUrl}/publishers/google/models/{llmReq.Model}:{action}";
            }

            return $"{baseUrl}/v1/publishers/google/models/{llmReq.Model}:{action}";
        }

        // Format: /base_url/{version}/models/{model}:generateContent
        return $"{_config.BaseUrl}/{version}/models/{llmReq.Model}:{action}";
    }

    /// <summary>Port of TransformResponse (outbound.go:238-270).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        // Check if this is an embedding request
        if (response.Request?.ApiFormat == ApiFormats.GeminiEmbedding)
        {
            return TransformEmbeddingResponse(response);
        }

        // Check if this is an image generation request
        if (response.Request?.RequestType == RequestTypes.Image)
        {
            return TransformImageGenerationResponse(response);
        }

        // Check for HTTP error status
        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException($"HTTP error {response.StatusCode}");
        }

        // Check for empty response body
        if (response.Body is not { Length: > 0 })
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Go decodes a JSON "null" body into the zero struct; the port maps the
        // null result of the deserializer back to the zero struct.
        GenerateContentResponse geminiResp;
        try
        {
            geminiResp = LlmJson.Deserialize<GenerateContentResponse>(response.Body)
                ?? new GenerateContentResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal gemini response: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        // Convert to unified response (non-streaming)
        return GeminiOutboundConvert.ConvertGeminiToLlmResponse(geminiResp, false);
    }

    /// <summary>Port of TransformError (outbound.go:273-303).</summary>
    public ResponseError TransformError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = "Request failed.",
                    Type = "api_error",
                },
            };
        }

        var geminiErr = new GeminiError();
        if (error.Body is { Length: > 0 } body)
        {
            try
            {
                geminiErr = LlmJson.Deserialize<GeminiError>(body) ?? new GeminiError();
            }
            catch (JsonException)
            {
                geminiErr = new GeminiError();
            }
        }

        // STJ may leave Error null for {"error": null}; the reference treats the
        // zero struct (empty message) the same way.
        if (geminiErr.Error is { Message.Length: > 0 } detail)
        {
            return new ResponseError
            {
                StatusCode = error.StatusCode,
                Detail = new ErrorDetail
                {
                    Type = detail.Status,
                    Message = detail.Message,
                    Code = detail.Code.ToString(CultureInfo.InvariantCulture),
                },
            };
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = Encoding.UTF8.GetString(error.Body),
                Type = "api_error",
            },
        };
    }

    /// <summary>Port of SetAPIKey (outbound.go:306-308).</summary>
    public void SetAPIKey(string apiKey)
    {
        _config.ApiKeyProvider = new StaticApiKeyProvider(apiKey);
    }

    /// <summary>Port of SetBaseURL (outbound.go:311-313).</summary>
    public void SetBaseURL(string baseUrl)
    {
        _config.BaseUrl = baseUrl;
    }

    /// <summary>
    /// Port of clearFunctionIDsForVertexAI (outbound.go:318-330): clears the ID
    /// field from all FunctionCall and FunctionResponse parts in the request.
    /// Vertex AI does not support the ID field in function_call or
    /// function_response objects. Go sets the zero value ""; the port's
    /// string? + WhenWritingNull omits either way, so null keeps the wire shape.
    /// </summary>
    internal static void ClearFunctionIDsForVertexAI(GenerateContentRequest req)
    {
        foreach (var content in req.Contents ?? [])
        {
            foreach (var part in content.Parts ?? [])
            {
                if (part.FunctionCall is not null)
                {
                    part.FunctionCall.Id = null;
                }

                if (part.FunctionResponse is not null)
                {
                    part.FunctionResponse.Id = null;
                }
            }
        }
    }

    /// <summary>Go's strings.TrimSuffix(url, "/") removes at most one slash.</summary>
    private static string TrimSingleTrailingSlash(string url)
        => url.EndsWith('/') ? url[..^1] : url;
}
