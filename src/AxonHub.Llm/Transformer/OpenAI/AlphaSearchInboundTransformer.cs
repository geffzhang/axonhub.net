using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.AlphaSearchInboundTransformer
/// (llm/transformer/openai/alpha_search_inbound.go): accepts the Codex/CPA
/// alpha search envelope while preserving all provider-specific fields for
/// transparent forwarding.
/// </summary>
public sealed class AlphaSearchInboundTransformer : IInboundTransformer
{
    /// <summary>Go's encoding/json accepts nesting up to 10000 levels.</summary>
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 10_000 };

    /// <summary>Port of TransformRequest.</summary>
    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (request.Body is null || request.Body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        if (contentType.Length > 0
            && !contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        var model = DecodeModel(request.Body);

        return new LlmRequest
        {
            Model = model,
            Messages = [],
            RawRequest = request,
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
            AlphaSearch = new AlphaSearchRequest
            {
                // append([]byte(nil), httpReq.Body...)
                Body = (byte[])request.Body.Clone(),
            },
        };
    }

    /// <summary>Port of TransformResponse.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null || response.AlphaSearch is null || response.AlphaSearch.Body.Length == 0)
        {
            throw new InvalidOperationException("alpha search response is empty");
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = (byte[])response.AlphaSearch.Body.Clone(),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
        };
    }

    /// <summary>Go: AlphaSearchInboundTransformer.TransformStream.</summary>
    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
        => throw TransformerErrors.InvalidRequest("alpha search does not support streaming");

    /// <summary>Go: AlphaSearchInboundTransformer.AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("alpha search does not support streaming");
    }

    /// <summary>Go: TransformError delegates to the chat transformer.</summary>
    public HttpResponseData TransformError(Exception? error)
        => new OpenAiInboundTransformer().TransformError(error);

    /// <summary>
    /// Port of the <c>json.Unmarshal(body, &amp;envelope)</c> + <c>envelope["model"]</c>
    /// + trim check in TransformRequest. Only an exactly-cased <c>model</c>
    /// string sets the routed model, as with a Go map lookup.
    /// </summary>
    private static string DecodeModel(byte[] body)
    {
        // Go validates the whole document before decoding.
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode alpha search request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            // Unmarshal into a nil map leaves it nil and reports no error; the
            // reference still rejects the request with "<nil>" in the message.
            throw TransformerErrors.InvalidRequest("failed to decode alpha search request: <nil>");
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode alpha search request: "
                + GoStructJson.TypeMismatch(rootKind, "map[string]json.RawMessage"));
        }

        var model = string.Empty;
        // Go's json.Unmarshal repairs ill-formed UTF-8 in string literals before
        // decoding; System.Text.Json refuses it outright (difference C008).
        using (var document = JsonDocument.Parse(
            GoJsonUtf8.SanitizeStringLiterals(body) ?? body,
            ParseOptions))
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "model", StringComparison.Ordinal))
                {
                    continue;
                }

                // json.Unmarshal(raw, &model): a non-string value errors and is
                // ignored by the reference, leaving model unchanged.
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    model = property.Value.GetString() ?? string.Empty;
                }
            }
        }

        model = model.Trim();
        if (model.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("model is required for alpha search routing");
        }

        return model;
    }
}
