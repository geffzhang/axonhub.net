using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.OpenAIHandlers.GetVideo / DeleteVideo
/// (internal/server/api/openai.go:519-578) for <c>GET/DELETE /v1/videos/:id</c>:
/// the video task query/delete flow backed by <see cref="VideoService"/>.
/// </summary>
public static class VideoEndpoint
{
    /// <summary>Port of OpenAIHandlers.GetVideo.</summary>
    public static async Task GetVideoAsync(HttpContext context, string? id, VideoService videoService)
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

        response.Object = "video";
        response.ApiFormat = ApiFormats.OpenAIVideo;
        response.Choices = [];

        HttpResponseData httpResponse;
        try
        {
            httpResponse = new VideoInboundTransformer().TransformResponse(response);
        }
        catch (Exception ex)
        {
            await WriteTaskErrorAsync(context, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ChatCompletionEndpoint.WriteResponseAsync(context, httpResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of OpenAIHandlers.DeleteVideo.</summary>
    public static async Task DeleteVideoAsync(HttpContext context, string? id, VideoService videoService)
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
