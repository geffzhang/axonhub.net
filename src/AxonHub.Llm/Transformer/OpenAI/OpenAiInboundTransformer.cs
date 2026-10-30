using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>Port of llm/transformer/openai/inbound.go.</summary>
public sealed partial class OpenAiInboundTransformer : IInboundTransformer
{
    /// <summary>Port of TransformRequest: client HTTP request to unified request.</summary>
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
        if (!contentType.ToLowerInvariant().Contains("application/json", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("unsupported content type: " + contentType);
        }

        // Go validates the whole document before decoding, so syntax errors are
        // always reported with encoding/json's own wording.
        var syntaxError = GoJsonSyntax.Validate(request.Body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode openai request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(request.Body);
        if (rootKind is not null)
        {
            // null leaves the zero struct in place; other roots are a type error.
            if (rootKind != "null")
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode openai request: " + GoStructJson.TypeMismatch(rootKind, "openai.Request"));
            }

            throw TransformerErrors.InvalidRequest("model is required");
        }

        // Go's decoder reports field-level type errors with encoding/json's own
        // wording. The walker runs first so the errors keep that text and the
        // saved-error precedence (a failing UnmarshalJSON aborts and overrides
        // an earlier type error).
        var typeError = OpenAiRequestTypeCheck.Validate(request.Body);
        if (typeError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode openai request: " + typeError);
        }

        OpenAiRequest? oaiReq;
        try
        {
            oaiReq = LlmJson.Deserialize<OpenAiRequest>(request.Body);
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode openai request: " + JsonErrorText(ex), ex);
        }

        if (oaiReq is null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode openai request: unexpected null");
        }

        if (string.IsNullOrEmpty(oaiReq.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (oaiReq.Messages is null || oaiReq.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        var chatReq = oaiReq.ToLlmRequest() ?? throw TransformerErrors.InvalidRequest("failed to decode openai request: unexpected null");
        chatReq.RawRequest = request;
        chatReq.RequestType = RequestTypes.Chat;
        chatReq.ApiFormat = ApiFormats.OpenAIChatCompletion;

        return chatReq;
    }

    /// <summary>Port of TransformResponse: unified response to the client HTTP response.</summary>
    public HttpResponseData TransformResponse(LlmResponse? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("chat completion response is nil");
        }

        var oaiResp = OpenAiConvertFromLlm.ResponseFromLlm(response);
        var body = GoJson.Marshal(oaiResp);

        return new HttpResponseData
        {
            StatusCode = 200,
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
                ["Cache-Control"] = ["no-cache"],
            },
        };
    }

    /// <summary>
    /// Strips the decoder's position suffix so messages read like Go's.
    /// </summary>
    internal static string JsonErrorText(JsonException ex)
    {
        var message = ex.Message;
        var pathIndex = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return pathIndex > 0 ? message[..pathIndex] : message;
    }
}
