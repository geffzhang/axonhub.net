using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.TypeSafe;

/// <summary>
/// Port of typesafe.OutboundTransformer (llm/transformer/typesafe/outbound.go):
/// speaks the TypeSafe SystemOne decision protocol to an upstream.
/// </summary>
public sealed class TypeSafeOutboundTransformer
    : IOutboundTransformer, IApiKeyProvenance, IPassThroughBodyPolicy
{
    private readonly TypeSafeOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    public TypeSafeOutboundTransformer(TypeSafeOutboundConfig config)
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
            config.BaseUrl = "https://api.typesafe.ai/v1";
        }

        config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(
            config.BaseUrl,
            config.EndpointPath.Length > 0 ? string.Empty : "v1");

        _config = config;
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static TypeSafeOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>
    /// Builds a transformer with a key provider and its audit index, mirroring
    /// the OpenAI outbound factory (getAPIKeyProvider + channel_api_key_index).
    /// </summary>
    public static TypeSafeOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null,
        string endpointPath = "")
        => new(new TypeSafeOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
            EndpointPath = endpointPath,
        });

    /// <summary>
    /// Port of TypeSafeOutboundTransformer.AllowPassThroughBody: only SystemOne
    /// requests may replay the raw body.
    /// </summary>
    public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
    {
        _ = providerRequest;
        return llmRequest is not null && llmRequest.RequestType == RequestTypes.SystemOne;
    }

    /// <summary>Port of APIFormat.</summary>
    public string ApiFormat => ApiFormats.TypeSafeSystemOne;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
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

        // The reference builds the wire request fresh and never copies
        // request.Stream, so the body has no "stream" member at all.
        var wireReq = new SystemOneWireRequest
        {
            Model = request.Model,
            State = request.SystemOne.State,
            Questions = request.SystemOne.Questions,
        };

        var body = GoJson.Marshal(wireReq);

        var apiKey = _config.ApiKeyProvider.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildUrl(),
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

    /// <summary>Port of buildURL.</summary>
    public string BuildUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/systemone";

    /// <summary>Port of TransformResponse.</summary>
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

        // Go checks json.Valid first: trailing bytes make the body invalid,
        // unlike the streaming decode the inbound transformer uses.
        if (GoJsonSyntax.Validate(response.Body) is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response: invalid json");
        }

        // Some deployments (e.g. Cloudflare Workers AI) wrap the SystemOne
        // payload in their universal envelope: {"success":true,"result":{...}}.
        // The envelope keys do not collide with SystemOne fields, so decoding
        // the raw body directly would silently yield a zero-valued response.
        using var envelopeDocument = JsonDocument.Parse(response.Body);
        var envelopeRoot = envelopeDocument.RootElement;
        if (envelopeRoot.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response envelope: "
                + GoStructJson.TypeMismatch(
                    GoStructJson.RootKind(response.Body)!,
                    "typesafe.systemOneWireEnvelope"));
        }

        JsonElement successRaw = default;
        JsonElement resultRaw = default;
        JsonElement errorRaw = default;
        JsonElement errorsRaw = default;
        JsonElement messagesRaw = default;
        var hasSuccess = envelopeRoot.ValueKind == JsonValueKind.Object
            && TryGetEnvelopeProperty(envelopeRoot, "success", out successRaw);
        var hasResult = envelopeRoot.ValueKind == JsonValueKind.Object
            && TryGetEnvelopeProperty(envelopeRoot, "result", out resultRaw);
        var hasError = envelopeRoot.ValueKind == JsonValueKind.Object
            && TryGetEnvelopeProperty(envelopeRoot, "error", out errorRaw);
        var hasErrors = envelopeRoot.ValueKind == JsonValueKind.Object
            && TryGetEnvelopeProperty(envelopeRoot, "errors", out errorsRaw);
        var hasMessages = envelopeRoot.ValueKind == JsonValueKind.Object
            && TryGetEnvelopeProperty(envelopeRoot, "messages", out messagesRaw);

        ReadOnlyMemory<byte> wireBody = response.Body;
        if (hasSuccess)
        {
            if (!TryReadEnvelopeSuccess(successRaw, out var success))
            {
                throw EnvelopeError(
                    "systemone response envelope has an invalid success flag");
            }

            if (!success)
            {
                var message = EnvelopeErrorMessage(
                    hasError ? errorRaw : null,
                    hasErrors ? errorsRaw : null,
                    hasMessages ? messagesRaw : null);
                if (message.Length == 0)
                {
                    message = "upstream request failed";
                }

                throw EnvelopeError(message);
            }

            if (!hasResult || resultRaw.ValueKind == JsonValueKind.Null)
            {
                throw EnvelopeError(
                    "systemone response envelope is missing result");
            }

            // Result is already part of the validated document; keep its raw
            // text so decoding preserves the number lexemes (UseNumber).
            wireBody = Encoding.UTF8.GetBytes(resultRaw.GetRawText());
        }

        var rootKind = GoStructJson.RootKind(wireBody.Span);
        if (rootKind is not null)
        {
            // null leaves the zero struct in place; other roots are a type error.
            return rootKind == "null"
                ? WireToLlm(new SystemOneWireResponse())
                : throw new InvalidOperationException(
                    "failed to unmarshal systemone response: "
                    + GoStructJson.TypeMismatch(rootKind, "typesafe.systemOneWireResponse"));
        }

        SystemOneWireResponse wireResp;
        try
        {
            wireResp = LlmJson.Deserialize<SystemOneWireResponse>(wireBody.Span)
                ?? new SystemOneWireResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (hasSuccess
            && string.IsNullOrEmpty(wireResp.Model)
            && (wireResp.Answers is null || wireResp.Answers.Count == 0))
        {
            throw EnvelopeError(
                "systemone response envelope result is empty");
        }

        return WireToLlm(wireResp);
    }

    /// <summary>
    /// Go's encoding/json field lookup folds ASCII case and keeps the last
    /// duplicate key; the envelope keys are all lowercase in practice.
    /// </summary>
    private static bool TryGetEnvelopeProperty(
        JsonElement root,
        string foldedName,
        out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, foldedName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Port of systemOneEnvelopeSuccess: an explicit null or non-boolean value
    /// is an envelope with an invalid flag, not a plain SystemOne response.
    /// </summary>
    private static bool TryReadEnvelopeSuccess(JsonElement value, out bool success)
    {
        success = false;
        if (value.ValueKind == JsonValueKind.True)
        {
            success = true;
            return true;
        }

        return value.ValueKind == JsonValueKind.False;
    }

    /// <summary>
    /// Port of systemOneEnvelopeErrorMessage: string, object-with-message or
    /// object-with-detail, and arrays joined with "; ".
    /// </summary>
    private static string EnvelopeErrorMessage(
        JsonElement? error,
        JsonElement? errors,
        JsonElement? messages)
    {
        foreach (var raw in new[] { error, errors, messages })
        {
            if (raw is not { } value || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var message = MessageFromValue(value);
            if (message.Length > 0)
            {
                return message;
            }
        }

        return string.Empty;
    }

    private static string MessageFromValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Object:
                if (TryGetEnvelopeProperty(value, "message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(message.GetString()))
                {
                    return message.GetString()!;
                }

                if (TryGetEnvelopeProperty(value, "detail", out var detail)
                    && detail.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(detail.GetString()))
                {
                    return detail.GetString()!;
                }

                return string.Empty;
            case JsonValueKind.Array:
                var messages = new List<string>(value.GetArrayLength());
                foreach (var item in value.EnumerateArray())
                {
                    var itemMessage = MessageFromValue(item);
                    if (itemMessage.Length > 0)
                    {
                        messages.Add(itemMessage);
                    }
                }

                return string.Join("; ", messages);
            default:
                return string.Empty;
        }
    }

    private static ResponseError EnvelopeError(string message)
        => new()
        {
            StatusCode = 502,
            Detail = new ErrorDetail
            {
                Message = message,
                Type = "api_error",
            },
        };

    /// <summary>Port of the response-to-unified mapping in TransformResponse.</summary>
    private static LlmResponse WireToLlm(SystemOneWireResponse wireResp)
    {
        var llmResp = new LlmResponse
        {
            // Go decodes a JSON null string into the zero value "".
            Model = wireResp.Model ?? string.Empty,
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
            SystemOne = new SystemOneResponse
            {
                // A null map value decodes as the zero answer in Go.
                Answers = SystemOneJson.CoalesceAnswers(wireResp.Answers),
            },
        };

        if (wireResp.Usage is { } usage)
        {
            llmResp.Usage = new Usage
            {
                PromptTokens = usage.InputTokens,
                CompletionTokens = usage.OutputTokens,
                TotalTokens = usage.InputTokens + usage.OutputTokens,
            };
        }

        return llmResp;
    }

    /// <summary>Port of TransformError.</summary>
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

        // error is also the inner exception so Go's Unwrap chain — and therefore
        // errors.As(err, &httpErr) — still reaches the transport error, exactly
        // like llm.ResponseError.Unwrap (model.go:918).
        var wireError = TryParseError(error.Body);
        if (wireError is { Message.Length: > 0 })
        {
            return new ResponseError(string.Empty, error)
            {
                StatusCode = error.StatusCode,
                CauseException = error,
                Detail = new ErrorDetail
                {
                    Message = wireError.Message,
                    Type = wireError.Type ?? string.Empty,
                    // Go: code has omitempty; type does not.
                    Code = string.IsNullOrEmpty(wireError.Code) ? null : wireError.Code,
                },
            };
        }

        return new ResponseError(string.Empty, error)
        {
            StatusCode = error.StatusCode,
            CauseException = error,
            Detail = new ErrorDetail
            {
                Message = error.Body is { Length: > 0 } body ? Encoding.UTF8.GetString(body) : string.Empty,
                Type = "api_error",
            },
        };
    }

    /// <summary>Port of TransformStream: systemone never streams.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => throw new InvalidOperationException("systemone does not support streaming");

    /// <summary>Port of AggregateStreamChunks: systemone never streams.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => throw new InvalidOperationException("systemone does not support streaming");

    /// <summary>
    /// Go <c>json.Unmarshal(body, &amp;struct{ Error struct{...} })</c>: invalid
    /// JSON, a non-object root or a mistyped member all fail and fall back to
    /// the raw-body envelope.
    /// </summary>
    private static WireError? TryParseError(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        if (GoJsonSyntax.Validate(body) is not null || GoStructJson.RootKind(body) is not null)
        {
            return null;
        }

        try
        {
            return LlmJson.Deserialize<SystemOneWireErrorEnvelope>(body)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class SystemOneWireErrorEnvelope
    {
        [JsonPropertyName("error")]
        public WireError? Error { get; set; }
    }

    private sealed class WireError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}

/// <summary>Port of typesafe.Config (llm/transformer/typesafe/outbound.go).</summary>
public sealed class TypeSafeOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider ApiKeyProvider { get; set; } = null!;

    /// <summary>Optional custom path override, replacing "/systemone".</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>1-based index over the channel's full key list, or null when unambiguous.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}
