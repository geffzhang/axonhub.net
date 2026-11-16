using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.TypeSafe;

namespace AxonHub.Llm.Transformer.Cloudflare;

/// <summary>Port of cloudflare.Config (outbound.go:20-27).</summary>
public sealed class CloudflareSystemOneConfig
{
    /// <summary>
    /// Account-scoped Workers AI base:
    /// <c>https://api.cloudflare.com/client/v4/accounts/&lt;account_id&gt;/ai</c>.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Optional route prefix replacing <c>/run</c>; the model is appended.</summary>
    public string EndpointPath { get; set; } = string.Empty;

    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of cloudflare.SystemOneOutboundTransformer (outbound.go:29-315): sends
/// System One decisions to Workers AI decision models on the native
/// <c>/run/{model}</c> route and unwraps Cloudflare's v4 response envelope.
/// </summary>
public sealed class CloudflareSystemOneOutboundTransformer
    : IOutboundTransformer, IApiKeyProvenance, IPassThroughBodyPolicy,
      IPassThroughResponsePolicy
{
    private readonly CloudflareSystemOneConfig _config;
    private string? _selectedApiKey;

    public CloudflareSystemOneOutboundTransformer(CloudflareSystemOneConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException("config cannot be nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required");
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new InvalidOperationException("base URL is required");
        }

        // The OpenAI-compatible surface lives under {base}/v1, so tolerate a
        // base URL that already carries it: native runs start at the account base.
        var baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, string.Empty);
        if (baseUrl.EndsWith("/v1", StringComparison.Ordinal))
        {
            baseUrl = baseUrl[..^3];
        }

        _config = new CloudflareSystemOneConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = config.ApiKeyProvider,
            EndpointPath = config.EndpointPath ?? string.Empty,
            ChannelApiKeyIndex = config.ChannelApiKeyIndex,
        };
    }

    public string ApiFormat => ApiFormats.TypeSafeSystemOne;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
    {
        _ = llmRequest;
        _ = providerRequest;
        return false;
    }

    public bool AllowPassThroughResponse(LlmRequest? llmRequest, HttpRequestData? providerRequest)
    {
        _ = llmRequest;
        _ = providerRequest;
        return false;
    }

    /// <summary>Port of TransformRequest (outbound.go:79-121).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("llm request is nil");
        }

        if (request.SystemOne is null)
        {
            throw new InvalidOperationException("systemone request is nil");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        var wireRequest = new CloudflareWireRequest
        {
            Model = ModelSelector(request.Model),
            State = request.SystemOne.State,
            Questions = request.SystemOne.Questions,
        };

        var body = GoJson.Marshal(wireRequest);
        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildUrl(request.Model),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
        };
    }

    /// <summary>Port of buildURL (outbound.go:123-137).</summary>
    public string BuildUrl(string model)
    {
        var prefix = "/run";
        if (_config.EndpointPath.Length > 0)
        {
            var trimmed = _config.EndpointPath.Trim('/');
            prefix = trimmed.Length > 0 ? "/" + trimmed : string.Empty;
        }

        return _config.BaseUrl + prefix + "/" + model;
    }

    /// <summary>
    /// Port of modelSelector (outbound.go:139-152): <c>@cf/author/name</c>
    /// becomes <c>name</c>; other model ids are unchanged.
    /// </summary>
    internal static string ModelSelector(string model)
    {
        if (!model.StartsWith("@cf/", StringComparison.Ordinal))
        {
            return model;
        }

        var rest = model[4..];
        var slash = rest.IndexOf('/');
        if (slash >= 0 && slash + 1 < rest.Length)
        {
            return rest[(slash + 1)..];
        }

        return model;
    }

    /// <summary>Port of TransformResponse (outbound.go:154-244).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            throw TransformError(new HttpClientError(
                "POST",
                string.Empty,
                response.StatusCode,
                GoHttpStatus.Text(response.StatusCode),
                response.Body));
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        if (GoJsonSyntax.Validate(response.Body) is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response: invalid json");
        }

        var payload = response.Body;
        var envelope = TryParseEnvelope(response.Body);
        if (envelope is not null)
        {
            if (envelope.Success == false || envelope.Errors.Count > 0)
            {
                var statusCode = response.StatusCode < 400 ? 502 : response.StatusCode;
                throw TransformError(new HttpClientError(
                    "POST",
                    string.Empty,
                    statusCode,
                    GoHttpStatus.Text(statusCode),
                    response.Body));
            }

            if (envelope.Success == true && envelope.Result is null)
            {
                throw new InvalidOperationException(
                    "failed to unmarshal systemone response: envelope has no result");
            }

            if (envelope.Result is { } result)
            {
                var bytes = Encoding.UTF8.GetBytes(result.GetRawText());
                var start = 0;
                var end = bytes.Length;
                while (start < end && IsJsonWhitespace(bytes[start]))
                {
                    start++;
                }

                while (end > start && IsJsonWhitespace(bytes[end - 1]))
                {
                    end--;
                }

                if (start == end || bytes[start] != (byte)'{')
                {
                    throw new InvalidOperationException(
                        "failed to unmarshal systemone response: unexpected result "
                        + Encoding.UTF8.GetString(bytes, start, end - start));
                }

                payload = bytes;
            }
        }

        CloudflareWireResponse wireResponse;
        try
        {
            wireResponse = LlmJson.Deserialize<CloudflareWireResponse>(payload)
                ?? new CloudflareWireResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (wireResponse.Answers is not { Count: > 0 })
        {
            throw new InvalidOperationException("systemone response has no answers");
        }

        var llmResponse = new LlmResponse
        {
            Model = wireResponse.Model ?? string.Empty,
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
            SystemOne = new SystemOneResponse
            {
                Answers = SystemOneJson.CoalesceAnswers(wireResponse.Answers),
            },
        };

        if (wireResponse.Usage is { } usage)
        {
            llmResponse.Usage = new Usage
            {
                PromptTokens = usage.InputTokens,
                CompletionTokens = usage.OutputTokens,
                TotalTokens = usage.InputTokens + usage.OutputTokens,
            };
        }

        return llmResponse;
    }

    /// <summary>Port of TransformError (outbound.go:246-297).</summary>
    public ResponseError TransformError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = GoHttpStatus.Text(500),
                    Type = "api_error",
                },
            };
        }

        var envelope = error.Body is null ? null : TryParseEnvelope(error.Body);
        if (envelope is not null)
        {
            var messages = new List<string>(envelope.Errors.Count);
            var code = string.Empty;
            foreach (var item in envelope.Errors)
            {
                if (item.Message.Length == 0)
                {
                    continue;
                }

                messages.Add(item.Message);
                if (code.Length == 0)
                {
                    code = item.Code.Trim('"');
                }
            }

            if (messages.Count > 0)
            {
                return new ResponseError
                {
                    StatusCode = error.StatusCode,
                    CauseException = error,
                    Detail = new ErrorDetail
                    {
                        Message = string.Join("; ", messages),
                        Type = "api_error",
                        Code = code,
                    },
                };
            }
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            CauseException = error,
            Detail = new ErrorDetail
            {
                Message = error.Body is null ? string.Empty : Encoding.UTF8.GetString(error.Body),
                Type = "api_error",
            },
        };
    }

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => throw new InvalidOperationException("systemone does not support streaming");

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => throw new InvalidOperationException("systemone does not support streaming");

    private static bool IsJsonWhitespace(byte value)
        => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private sealed record ParsedEnvelope(
        bool? Success,
        JsonElement? Result,
        List<CloudflareEnvelopeError> Errors);

    /// <summary>
    /// Port of <c>json.Unmarshal(body, &amp;envelope)</c>: a type error makes
    /// the reference ignore the envelope and decode the raw body, so null here
    /// means "not an envelope". <c>Success == null</c> means the key was absent
    /// or JSON null; an explicit null result stays distinct from an absent one.
    /// </summary>
    private static ParsedEnvelope? TryParseEnvelope(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool? success = null;
            JsonElement? result = null;
            var errors = new List<CloudflareEnvelopeError>();

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "success":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            success = null;
                        }
                        else if (property.Value.ValueKind is JsonValueKind.True
                                 or JsonValueKind.False)
                        {
                            success = property.Value.GetBoolean();
                        }
                        else
                        {
                            return null;
                        }

                        break;
                    case "result":
                        result = property.Value.Clone();
                        break;
                    case "errors":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            errors.Clear();
                            break;
                        }

                        if (property.Value.ValueKind != JsonValueKind.Array)
                        {
                            return null;
                        }

                        var parsedErrors = new List<CloudflareEnvelopeError>();
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object)
                            {
                                return null;
                            }

                            var message = string.Empty;
                            var code = string.Empty;
                            foreach (var field in item.EnumerateObject())
                            {
                                switch (field.Name.ToLowerInvariant())
                                {
                                    case "message":
                                        if (field.Value.ValueKind == JsonValueKind.Null)
                                        {
                                            message = string.Empty;
                                        }
                                        else if (field.Value.ValueKind == JsonValueKind.String)
                                        {
                                            message = field.Value.GetString() ?? string.Empty;
                                        }
                                        else
                                        {
                                            return null;
                                        }

                                        break;
                                    case "code":
                                        code = field.Value.GetRawText();
                                        break;
                                }
                            }

                            parsedErrors.Add(new CloudflareEnvelopeError
                            {
                                Code = code,
                                Message = message,
                            });
                        }

                        errors = parsedErrors;
                        break;
                }
            }

            return new ParsedEnvelope(success, result, errors);
        }
    }
}
