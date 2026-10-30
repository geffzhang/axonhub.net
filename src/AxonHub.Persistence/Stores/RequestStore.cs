using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>requests</c> table.</summary>
public interface IRequestStore
{
    Task<long> CreateAsync(RequestRecord request, CancellationToken cancellationToken = default);

    Task<RequestRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of UpdateRequestStatus: status only, leaving response data untouched.</summary>
    Task<bool> UpdateStatusAsync(
        long id,
        string status,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateRequestFinalized: status and external id are always
    /// written, the body is stored under the default storage policy, metrics
    /// only when the caller supplies them, and headers never (they go through
    /// <see cref="UpdateResponseHeadersAsync"/>).
    /// </summary>
    Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? responseBody,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
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

        // The reference stores the finalized body whenever the storage policy
        // allows it (default true); response chunks are written only by
        // SaveRequestChunks, which this slice does not port (C020).
        request.ResponseBody = responseBody;

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
}
