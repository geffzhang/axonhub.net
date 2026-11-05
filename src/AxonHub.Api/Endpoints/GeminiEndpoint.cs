using System.Text.Json.Serialization;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Gemini;
using Microsoft.AspNetCore.Http;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.GeminiHandlers (internal/server/api/gemini.go): the native
/// Gemini surface mounted at <c>/gemini/:gemini-api-version</c> and its
/// <c>/v1beta</c> alias (routes.go:266-289). GenerateContent picks the writer
/// from the <c>alt</c> query parameter; ListModels mirrors the reference's
/// error envelope, mapping an ent-privacy deny to 403 permission_error (C178).
/// </summary>
public static class GeminiEndpoint
{
    /// <summary>
    /// Port of GeminiHandlers.GenerateContent: <c>alt=sse</c> switches to SSE
    /// framing, otherwise the JSON-array writer (WriteGeminiStream) is used.
    /// </summary>
    public static Task GenerateContentAsync(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        GeminiInboundTransformer gemini)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(gemini);

        var alt = context.Request.Query["alt"].FirstOrDefault();
        var writer = alt == "sse"
            ? new ChatCompletionEndpoint.StreamWriterDelegate(SseStreamWriter.WriteAsync)
            : new ChatCompletionEndpoint.StreamWriterDelegate(GeminiStreamWriter.WriteAsync);

        return ChatCompletionEndpoint.HandleAsync(
            context,
            orchestrator.WithInbound(gemini),
            streamWriter: writer);
    }

    /// <summary>Port of GeminiHandlers.ListModels.</summary>
    public static async Task ListModelsAsync(HttpContext context, ModelService models)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(models);

        var cancellationToken = context.RequestAborted;

        IReadOnlyList<ModelFacade> visibleModels;
        try
        {
            visibleModels = await models.ListEnabledModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // entprivacy.Deny → 403 permission_error; everything else is the
            // 500 internal_server_error branch.
            var denied = PrivacyDenyException.IsDeny(ex);
            await WriteGeminiErrorAsync(
                context,
                denied
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status500InternalServerError,
                ex.Message,
                denied ? "permission_error" : "internal_server_error",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var geminiModels = new GeminiModelBody[visibleModels.Count];
        for (var i = 0; i < visibleModels.Count; i++)
        {
            var model = visibleModels[i];
            geminiModels[i] = new GeminiModelBody
            {
                Name = "models/" + model.Id,
                BaseModelId = model.Id,
                Version = model.Id + "-" + i,
                DisplayName = model.DisplayName,
                Description = model.DisplayName,
                SupportedGenerationMethods = ["generateContent", "streamGenerateContent"],
            };
        }

        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            GoJson.Marshal(new GeminiModelListBody { Models = geminiModels }),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>gin: <c>c.JSON(status, gemini.GeminiError{...})</c>.</summary>
    private static Task WriteGeminiErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        string geminiStatus,
        CancellationToken cancellationToken)
        => WriteJsonAsync(
            context,
            statusCode,
            GoJson.Marshal(new GeminiError
            {
                Error = new GeminiErrorDetail
                {
                    Code = statusCode,
                    Message = message,
                    Status = geminiStatus,
                },
            }),
            cancellationToken);

    /// <summary>gin: <c>c.JSON(status, ...)</c> — UTF-8 JSON, no trailing newline.</summary>
    private static Task WriteJsonAsync(
        HttpContext context,
        int statusCode,
        byte[] body,
        CancellationToken cancellationToken)
        => ChatCompletionEndpoint.WriteResponseAsync(
            context,
            new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json; charset=utf-8"],
                },
                Body = body,
            },
            cancellationToken);

    /// <summary>gin.H{"models": [...]}. "models" is the only key.</summary>
    private sealed class GeminiModelListBody
    {
        [JsonPropertyName("models")]
        public required GeminiModelBody[] Models { get; init; }
    }

    /// <summary>Port of api.GeminiModel (internal/server/api/gemini.go).</summary>
    private sealed class GeminiModelBody
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("baseModelId")]
        public required string BaseModelId { get; init; }

        [JsonPropertyName("version")]
        public required string Version { get; init; }

        [JsonPropertyName("displayName")]
        public required string DisplayName { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("supportedGenerationMethods")]
        public required string[] SupportedGenerationMethods { get; init; }
    }
}
