using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Doubao;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.DoubaoHandlers (internal/server/api/doubao.go) for the Seedance
/// task surface mounted under <c>/doubao/v3</c>.
/// </summary>
public static class DoubaoVideoEndpoint
{
    /// <summary>Port of DoubaoHandlers.CreateTask.</summary>
    public static Task CreateTaskAsync(HttpContext context, ChatCompletionOrchestrator orchestrator)
        => ChatCompletionEndpoint.HandleAsync(
            context,
            orchestrator.WithInbound(new DoubaoVideoInboundTransformer()),
            nilResultIsInternalError: true);

    /// <summary>Port of DoubaoHandlers.GetTask.</summary>
    public static async Task GetTaskAsync(HttpContext context, string? id, VideoService videoService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(videoService);

        var cancellationToken = context.RequestAborted;

        if (string.IsNullOrEmpty(id))
        {
            await ChatCompletionEndpoint.WriteJsonErrorAsync(
                context, 400, GoHttpStatus.Text(400), "invalid id", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        LlmResponse response;
        try
        {
            response = await videoService
                .GetTaskByExternalIdAsync(id, RequestIdentity.FromHttpContext(context), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteTaskErrorAsync(context, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        // api/doubao.go:120-122: the Seedance surface tags the unified response
        // with video.task/seedance before the inbound transformer renders it.
        response.Object = "video.task";
        response.ApiFormat = ApiFormats.SeedanceVideo;
        response.Choices = [];

        HttpResponseData httpResponse;
        try
        {
            httpResponse = new DoubaoVideoInboundTransformer().TransformResponse(response);
        }
        catch (Exception ex)
        {
            await WriteTaskErrorAsync(context, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ChatCompletionEndpoint.WriteResponseAsync(context, httpResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of DoubaoHandlers.DeleteTask.</summary>
    public static async Task DeleteTaskAsync(HttpContext context, string? id, VideoService videoService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(videoService);

        var cancellationToken = context.RequestAborted;

        if (string.IsNullOrEmpty(id))
        {
            await ChatCompletionEndpoint.WriteJsonErrorAsync(
                context, 400, GoHttpStatus.Text(400), "invalid id", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await videoService
                .DeleteTaskByExternalIdAsync(id, RequestIdentity.FromHttpContext(context), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteTaskErrorAsync(context, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = 204;
    }

    /// <summary>api.JSONError(c, http.StatusInternalServerError, err).</summary>
    private static Task WriteTaskErrorAsync(
        HttpContext context,
        Exception error,
        CancellationToken cancellationToken)
        => ChatCompletionEndpoint.WriteJsonErrorAsync(
            context, 500, GoHttpStatus.Text(500), error.Message, cancellationToken);
}
