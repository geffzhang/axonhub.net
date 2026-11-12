using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of api.RequestPreviewHandlers.PreviewRequest
/// (internal/server/api/request_live.go:97-153), mounted by routes.go:150 on
/// the admin group. The live SSE branch depends on the reference's
/// LiveStreamRegistry, which the port does not host (C192): the registry
/// lookup is therefore always nil, and the handler serves the reference's
/// static-fetch fallback.
/// </summary>
public static class RequestPreviewHandlers
{
    public const string StaticFetchMode = "static-fetch";
    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";
    public const string CanceledStatus = "canceled";
    public const string ProcessingStatus = "processing";

    /// <summary>Port of PreviewRequest (request_live.go:97-126).</summary>
    public static async Task PreviewRequestAsync(
        HttpContext context,
        string? requestId,
        IManagementIdentity identity,
        IRequestStore requests,
        RequestPayloadStorage payloads)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(payloads);

        var cancellationToken = context.RequestAborted;

        var projectId = await AdminRequestEndpoints
            .RequireProjectIdAsync(context, identity)
            .ConfigureAwait(false);
        if (projectId is null)
        {
            return;
        }

        var request = await AdminRequestEndpoints
            .ResolveRequestAsync(context, requestId, projectId.Value, requests)
            .ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        // request_live.go:137-142: only an active stream can reach the live
        // branch, and only when the registry still holds its buffer. The port
        // has no registry (C192), so both branches converge on the static
        // fetch the reference performs when the buffer is gone.
        await WriteStaticPreviewAsync(context, request, payloads, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of writeStaticPreview (request_live.go:155-171).</summary>
    private static async Task WriteStaticPreviewAsync(
        HttpContext context,
        RequestRecord request,
        RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> chunks;
        try
        {
            chunks = RequestPayloadStorage.SanitizeLoadedResponseChunks(request.ResponseChunks);
        }
        catch (JsonException)
        {
            chunks = [];
        }

        if (chunks.Count == 0)
        {
            try
            {
                chunks = await payloads.LoadResponseChunksAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // The reference's ent row already holds decoded raw messages,
                // so its loader cannot fail here; a corrupt text column in the
                // port degrades to the empty list instead of a 500.
                chunks = [];
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", StaticFetchMode);
            writer.WritePropertyName("responseChunks");
            writer.WriteStartArray();
            foreach (var chunk in chunks)
            {
                // encoding/json compacts each json.RawMessage on the way out.
                writer.WriteRawValue(GoJson.Compact(Encoding.UTF8.GetBytes(chunk)));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body
            .WriteAsync(buffer.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }
}
