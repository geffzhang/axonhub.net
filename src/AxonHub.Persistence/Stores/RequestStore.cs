using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>requests</c> table.</summary>
public interface IRequestStore
{
    Task<long> CreateAsync(RequestRecord request, CancellationToken cancellationToken = default);

    Task<RequestRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Video task lookup (biz/video.go GetTaskByExternalID): the request row
    /// carrying the provider task id within the API key's project.
    /// </summary>
    Task<RequestRecord?> GetByExternalIdAndProjectAsync(
        string externalId,
        long projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of biz.RequestService.LoadCompletedResponsesSession's query
    /// (request.go:1442-1471): the newest completed Responses request carrying
    /// the provider response id for this API key (and project, when the caller
    /// is project-scoped). Missing rows return null.
    /// </summary>
    Task<RequestRecord?> FindLatestCompletedResponsesSessionAsync(
        string externalId,
        long apiKeyId,
        IReadOnlyList<string> formats,
        long? projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateRequestStatusExternalIDAndResponseBody (async video task
    /// polling): status and external id are always written; the response body
    /// snapshot only when <paramref name="setResponseBody"/> is set (the
    /// storage policy is checked, and an external write records its marker).
    /// </summary>
    Task<bool> UpdateStatusExternalIdAndResponseBodyAsync(
        long id,
        string status,
        string externalId,
        string? responseBody,
        bool setResponseBody,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Port of UpdateRequestStatus: status only, leaving response data untouched.</summary>
    Task<bool> UpdateStatusAsync(
        long id,
        string status,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateRequestFinalized: status and external id are always
    /// written, the body only when <paramref name="setResponseBody"/> is set
    /// (the storage policy is checked, and an external write records its
    /// marker), metrics only when the caller supplies them, and headers never
    /// (they go through <see cref="UpdateResponseHeadersAsync"/>).
    /// </summary>
    Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? responseBody,
        bool setResponseBody,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SaveRequestChunks' column write: the marshalled chunk array
    /// (or the external marker) with a fresh updated_at.
    /// </summary>
    Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Port of UpdateRequestResponseHeaders: the downstream response headers only.</summary>
    Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateChannelAsync(
        long id,
        long? channelId,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The video-storage scan's query (video_storage/worker.go:137-145):
    /// processing or completed video requests whose payload has not been
    /// archived yet, oldest id first, capped at <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<RequestRecord>> ListUnsavedVideoRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the content columns write in video_storage.Worker.processOne
    /// (worker.go:203-212): content_saved, its storage id/key/time, and the
    /// ent TimeMixin's updated_at. Returns false when the row no longer exists.
    /// </summary>
    Task<bool> MarkContentSavedAsync(
        long id,
        long dataStorageId,
        string storageKey,
        DateTime savedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>requests</c> table. Writes go through EF Core;
/// reads use Dapper.
/// </summary>
public sealed class RequestStore(AxonHubDbContext db) : IRequestStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "user_id", "source", "model_id",
        "reasoning_effort", "format", "request_headers", "request_body",
        "response_headers", "response_body", "response_chunks", "external_id",
        "status", "stream", "client_ip", "user_agent", "metrics_latency_ms",
        "metrics_first_token_latency_ms", "metrics_reasoning_duration_ms",
        "content_saved", "content_storage_id", "content_storage_key",
        "content_saved_at", "api_key_id", "channel_id", "data_storage_id",
        "project_id", "trace_id"
        """;

    public async Task<long> CreateAsync(RequestRecord request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        db.Requests.Add(request);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return request.Id;
    }

    public async Task<RequestRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns + " FROM \"requests\" WHERE \"id\" = @Id;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RequestRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<RequestRecord?> GetByExternalIdAndProjectAsync(
        string externalId,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        // QuerySingleOrDefault mirrors ent's Only: the reference lookup expects
        // at most one request row per provider task id within a project.
        var sql = "SELECT " + Columns
            + " FROM \"requests\" WHERE \"external_id\" = @ExternalId AND \"project_id\" = @ProjectId;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RequestRecord>(
            new CommandDefinition(
                sql,
                new { ExternalId = externalId, ProjectId = projectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<RequestRecord?> FindLatestCompletedResponsesSessionAsync(
        string externalId,
        long apiKeyId,
        IReadOnlyList<string> formats,
        long? projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formats);

        var sql = "SELECT " + Columns
            + " FROM \"requests\""
            + " WHERE \"external_id\" = @ExternalId"
            + " AND \"api_key_id\" = @ApiKeyId"
            + " AND \"format\" = ANY(@Formats)"
            + " AND \"status\" = @Status";
        if (projectId is not null)
        {
            sql += " AND \"project_id\" = @ProjectId";
        }

        sql += " ORDER BY \"created_at\" DESC LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RequestRecord>(
            new CommandDefinition(
                sql,
                new
                {
                    ExternalId = externalId,
                    ApiKeyId = apiKeyId,
                    Formats = formats.ToArray(),
                    Status = "completed",
                    ProjectId = projectId,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> UpdateStatusExternalIdAndResponseBodyAsync(
        long id,
        string status,
        string externalId,
        string? responseBody,
        bool setResponseBody,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.Status = status;
        request.ExternalId = externalId;

        if (setResponseBody)
        {
            request.ResponseBody = responseBody;
        }

        request.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateStatusAsync(
        long id,
        string status,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.Status = status;
        request.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? responseBody,
        bool setResponseBody,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.Status = status;
        request.ExternalId = externalId;

        // Ent only sets the column when the caller supplied the field: the
        // storage policy may disable it, and a failed external write leaves the
        // body untouched.
        if (setResponseBody)
        {
            request.ResponseBody = responseBody;
        }

        if (latencyMs is not null)
        {
            request.MetricsLatencyMs = latencyMs;
        }

        if (firstTokenLatencyMs is not null)
        {
            request.MetricsFirstTokenLatencyMs = firstTokenLatencyMs;
        }

        if (reasoningDurationMs is not null)
        {
            request.MetricsReasoningDurationMs = reasoningDurationMs;
        }

        request.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.ResponseChunks = responseChunks;
        request.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.ResponseHeaders = responseHeaders;
        request.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateChannelAsync(
        long id,
        long? channelId,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.ChannelId = channelId;
        request.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<RequestRecord>> ListUnsavedVideoRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        // request.StatusIn(processing, completed)
        // request.FormatIn(openai/video, seedance/video)
        // request.ContentSaved(false) — ent's equality predicate never matches
        // NULL, which the column's non-null default makes unreachable here.
        var sql = "SELECT " + Columns
            + " FROM \"requests\""
            + " WHERE \"status\" IN ('processing', 'completed')"
            + " AND \"format\" IN ('openai/video', 'seedance/video')"
            + " AND \"content_saved\" = @ContentSaved"
            + " ORDER BY \"id\" ASC LIMIT @Limit;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequestRecord>(
            new CommandDefinition(
                sql,
                new { ContentSaved = false, Limit = limit },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    public async Task<bool> MarkContentSavedAsync(
        long id,
        long dataStorageId,
        string storageKey,
        DateTime savedAt,
        CancellationToken cancellationToken = default)
    {
        var request = await db.Requests
            .FirstOrDefaultAsync(request => request.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return false;
        }

        request.ContentSaved = true;
        request.ContentStorageId = dataStorageId;
        request.ContentStorageKey = storageKey;
        request.ContentSavedAt = savedAt;
        request.UpdatedAt = savedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
