using AxonHub.Api.Auth;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;
using JsonGoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.VideoService (internal/server/biz/video.go): the async video
/// task query/delete flow behind <c>GET/DELETE /v1/videos/:id</c>. The provider
/// task id lives on the request row's external_id; the provider API format is
/// recovered from the latest request execution that carries one, falling back
/// to the channel-type default.
/// </summary>
/// <remarks>
/// The reference reads the request/execution rows and the channel entity from
/// the ent client on the request context and looks the outbound up in
/// <c>ch.Outbounds[videoKey]</c>. The port reads the same rows through the
/// stores and rebuilds the outbound from the channel's stored configuration
/// through <see cref="ChannelOutboundFactory"/>; in this build the <c>openai</c>
/// channel type carries the <c>openai/video</c> key and the <c>doubao</c> type
/// the <c>seedance/video</c> key (the zenmux video transformer is the deferred
/// scope cut, C010).
/// </remarks>
public sealed class VideoService(
    IRequestStore requestStore,
    IRequestExecutionStore executionStore,
    IChannelStore channelStore,
    IExecutor executor,
    TimeProvider timeProvider)
{
    // The reference's request.Status values (ent/request/request.go).
    private const string StatusProcessing = "processing";
    private const string StatusCompleted = "completed";
    private const string StatusFailed = "failed";
    private const string StatusCanceled = "canceled";

    private const string InternalError = "server internal error, please try again later";

    /// <summary>Port of GetTask: fetch the provider task, then persist the snapshot.</summary>
    public async Task<LlmResponse> GetTaskAsync(long requestId, CancellationToken cancellationToken = default)
    {
        var (task, outbound) = await LoadTaskAsync(requestId, cancellationToken).ConfigureAwait(false);

        var httpReq = outbound.BuildGetVideoTaskRequest(task.ExternalId!);
        var httpResp = executor.Do(cancellationToken, httpReq);

        var video = outbound.ParseGetVideoTaskResponse(httpResp);
        var videoResponse = video.Video
            ?? throw new InvalidOperationException("video response is nil");

        // Persist snapshot to request table for task tracking; a failed write
        // is non-fatal (biz/video.go: return data anyway).
        var status = MapVideoStatusToRequestStatus(videoResponse.Status);
        try
        {
            await requestStore.UpdateStatusExternalIdAndResponseBodyAsync(
                requestId,
                status,
                task.ExternalId!,
                JsonGoJson.MarshalToString(videoResponse),
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // non-fatal: return data anyway.
        }

        return video;
    }

    /// <summary>Port of GetTaskByExternalID: looks a task up by the provider's task id.</summary>
    public async Task<LlmResponse> GetTaskByExternalIdAsync(
        string externalId,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var task = await FindTaskByExternalIdAsync(externalId, identity, cancellationToken)
            .ConfigureAwait(false);
        return await GetTaskAsync(task.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of DeleteTask: issue the provider delete, then mark the task
    /// canceled locally on a best-effort basis.
    /// </summary>
    public async Task DeleteTaskAsync(long requestId, CancellationToken cancellationToken = default)
    {
        var (task, outbound) = await LoadTaskAsync(requestId, cancellationToken).ConfigureAwait(false);

        // The reference's zenmux ErrVideoTaskDeleteUnsupported branch falls
        // back to a local cancel; the zenmux transformer is the deferred scope
        // cut (C010), so every outbound in this build either builds a DELETE
        // or throws.
        var httpReq = outbound.BuildDeleteVideoTaskRequest(task.ExternalId!);
        _ = executor.Do(cancellationToken, httpReq);

        // Best effort: mark canceled locally.
        try
        {
            await requestStore.UpdateStatusAsync(
                requestId,
                StatusCanceled,
                timeProvider.GetUtcNow().UtcDateTime,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort only, like the reference's ignored error.
        }
    }

    /// <summary>Port of DeleteTaskByExternalID.</summary>
    public async Task DeleteTaskByExternalIdAsync(
        string externalId,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var task = await FindTaskByExternalIdAsync(externalId, identity, cancellationToken)
            .ConfigureAwait(false);
        await DeleteTaskAsync(task.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of loadTask: resolves the request row, its channel and the outbound
    /// transformer that owns the provider task operations.
    /// </summary>
    internal async Task<(RequestRecord Task, IVideoTaskOutbound Outbound)> LoadTaskAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        var task = await requestStore.GetByIdAsync(requestId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ent: request not found");

        if (string.IsNullOrWhiteSpace(task.ExternalId))
        {
            throw new InvalidOperationException(Internal("missing external_id for task"));
        }

        if (task.ChannelId is null or 0)
        {
            throw new InvalidOperationException(Internal("missing channel_id for task"));
        }

        var channel = await channelStore.GetByIdAsync(task.ChannelId.Value, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("channel not found: ent: channel not found");

        IReadOnlyList<RequestExecutionRecord> executions;
        try
        {
            executions = await executionStore.ListByRequestIdDescAsync(requestId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to query request executions: " + ex.Message, ex);
        }

        var videoKey = string.Empty;
        foreach (var execution in executions)
        {
            if (string.IsNullOrWhiteSpace(execution.Format) || string.IsNullOrWhiteSpace(execution.ExternalId))
            {
                continue;
            }

            videoKey = execution.Format;
            break;
        }

        if (videoKey.Length == 0)
        {
            videoKey = channel.Type == ChannelOutboundFactory.DoubaoChannelType
                ? ApiFormats.SeedanceVideo
                : ApiFormats.OpenAIVideo;
        }
        else if (videoKey is not (ApiFormats.OpenAIVideo or ApiFormats.SeedanceVideo or ApiFormats.ZenmuxVideo))
        {
            throw new InvalidOperationException(
                Internal("persisted execution format " + GoCast.GoQuote(videoKey)
                    + " does not support video task operations"));
        }

        // The reference looks the format up in the channel's outbound map
        // (ch.Outbounds[videoKey], built from the default endpoint table in
        // channel_endpoint.go). The port has no endpoint records, so mirror the
        // lookup explicitly: an openai channel maps openai/video and a doubao
        // channel maps seedance/video to the outbound built for that channel
        // type (channel_endpoint.go:271-275 vs openAICompatibleDefaultEndpoints).
        IVideoTaskOutbound? outbound = null;
        if ((channel.Type == ChannelOutboundFactory.OpenAiChannelType
                && videoKey == ApiFormats.OpenAIVideo)
            || (channel.Type == ChannelOutboundFactory.DoubaoChannelType
                && videoKey == ApiFormats.SeedanceVideo))
        {
            var candidate = new ChannelCandidate
            {
                ChannelId = channel.Id,
                ChannelName = channel.Name,
                Type = channel.Type,
                BaseUrl = channel.BaseUrl,
                Credentials = channel.Credentials,
                DisabledApiKeys = channel.DisabledApiKeys,
            };

            outbound = ChannelOutboundFactory.Create(
                candidate,
                new LlmRequest { ApiFormat = videoKey },
                timeProvider.GetUtcNow()) as IVideoTaskOutbound;
        }

        if (outbound is null)
        {
            throw new InvalidOperationException(Internal("channel does not support video task operations"));
        }

        return (task, outbound);
    }

    /// <summary>Port of mapVideoStatusToRequestStatus.</summary>
    internal static string MapVideoStatusToRequestStatus(string status)
        => status.Trim().ToLowerInvariant() switch
        {
            "succeeded" => StatusCompleted,
            "failed" => StatusFailed,
            "queued" or "running" => StatusProcessing,
            _ => StatusProcessing,
        };

    private async Task<RequestRecord> FindTaskByExternalIdAsync(
        string externalId,
        RequestIdentity identity,
        CancellationToken cancellationToken)
        => await requestStore
                .GetByExternalIdAndProjectAsync(externalId, identity.ProjectId, cancellationToken)
                .ConfigureAwait(false)
            ?? throw new InvalidOperationException("ent: request not found");

    /// <summary>Go: fmt.Errorf("%w: ...", ErrInternal).</summary>
    private static string Internal(string detail) => InternalError + ": " + detail;
}
