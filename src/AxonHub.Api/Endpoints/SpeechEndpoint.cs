using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.OpenAIHandlers.CreateSpeech + shouldUseBinarySpeechStream
/// (internal/server/api/openai.go:416-465) for <c>POST /v1/audio/speech</c>:
/// the request is read once to decide between SSE and binary streaming, then
/// delegated to the shared chat handler with the chosen writer.
/// </summary>
public static class SpeechEndpoint
{
    public static async Task HandleAsync(HttpContext context, ChatCompletionOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(orchestrator);

        var cancellationToken = context.RequestAborted;

        HttpRequestData genericRequest;
        try
        {
            genericRequest = await HttpRequestReader.ReadAsync(context.Request, cancellationToken);
        }
        catch (Exception ex)
        {
            // ReadHTTPRequest failures go through the speech inbound transformer.
            await ChatCompletionEndpoint.WriteTransformedErrorAsync(
                context,
                orchestrator.Inbound.TransformError(ex),
                cancellationToken);
            return;
        }

        bool useBinaryStream;
        try
        {
            useBinaryStream = ShouldUseBinarySpeechStream(genericRequest);
        }
        catch (Exception ex)
        {
            await ChatCompletionEndpoint.WriteTransformedErrorAsync(
                context,
                orchestrator.Inbound.TransformError(ex),
                cancellationToken);
            return;
        }

        if (!useBinaryStream)
        {
            await ChatCompletionEndpoint.HandleAsync(context, orchestrator, preReadRequest: genericRequest);
            return;
        }

        await ChatCompletionEndpoint.HandleAsync(
            context,
            orchestrator,
            preReadRequest: genericRequest,
            streamWriter: BinaryStreamWriter.WriteAsync);
    }

    /// <summary>Port of shouldUseBinarySpeechStream (openai.go:437-465).</summary>
    public static bool ShouldUseBinarySpeechStream(HttpRequestData? genericRequest)
    {
        if (genericRequest is null)
        {
            throw TransformerErrors.InvalidRequest("http request is nil");
        }

        if (genericRequest.Body is null || genericRequest.Body.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("request body is empty");
        }

        var contentType = HttpClientUtils.HeaderGet(genericRequest.Headers, "Content-Type").ToLowerInvariant();
        if (contentType.Length > 0 && !contentType.Contains("application/json", StringComparison.Ordinal))
        {
            return false;
        }

        // The reference unmarshals the route probe struct directly: whole-
        // document validation first, wrapped in the route's decode wording.
        var syntaxError = GoJsonSyntax.Validate(genericRequest.Body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode speech request: " + syntaxError);
        }

        SpeechRouteRequestBody body;
        var rootKind = GoStructJson.RootKind(genericRequest.Body);
        if (rootKind is not null)
        {
            if (rootKind != "null")
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode speech request: "
                    + GoStructJson.TypeMismatch(rootKind, "api.speechRouteRequestBody"));
            }

            // null leaves the zero struct in place: no stream format, not binary.
            body = new SpeechRouteRequestBody();
        }
        else
        {
            var typeError = OpenAiRequestTypeCheck.ValidateSpeechRoute(genericRequest.Body);
            if (typeError is not null)
            {
                throw TransformerErrors.InvalidRequest("failed to decode speech request: " + typeError);
            }

            try
            {
                body = LlmJson.Deserialize<SpeechRouteRequestBody>(genericRequest.Body)
                    ?? new SpeechRouteRequestBody();
            }
            catch (JsonException ex)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode speech request: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
            }
        }

        var streamFormat = (body.StreamFormat ?? string.Empty).Trim().ToLowerInvariant();

        return streamFormat.Length > 0 && streamFormat != "sse";
    }

    /// <summary>
    /// Port of the api.speechRouteRequestBody wire struct (openai.go:78): the
    /// route probe only needs the stream format to pick a writer.
    /// </summary>
    private sealed class SpeechRouteRequestBody
    {
        [JsonPropertyName("stream_format")]
        public string? StreamFormat { get; set; }
    }
}
