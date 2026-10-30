using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>request_executions</c> table.</summary>
public interface IRequestExecutionStore
{
    Task<long> CreateAsync(RequestExecutionRecord execution, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateRequestExecutionStatusWithMetrics: status, error details and
    /// metrics only. Response headers, body and external id captured earlier stay
    /// untouched, so a later status update cannot clear them.
    /// </summary>
    Task<bool> UpdateStatusAsync(
        long id,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Port of UpdateRequestExecutionFinalized (response_status_code is never set here).</summary>
    Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseHeaders,
        string? responseBody,
        string? responseChunks,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>request_executions</c> table. Writes go through
/// EF Core; reads use Dapper.
/// </summary>
public sealed class RequestExecutionStore(AxonHubDbContext db) : IRequestExecutionStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "project_id", "channel_api_key_index",
        "external_id", "model_id", "upstream_model_id", "format", "reasoning_effort",
        "channel_api_key_suffix", "request_body", "response_headers", "response_body",
        "response_chunks", "error_message", "response_status_code", "status", "stream",
        "metrics_latency_ms", "metrics_first_token_latency_ms",
        "metrics_reasoning_duration_ms", "request_headers", "request_url",
        "pass_through_applied", "channel_id", "data_storage_id", "request_id"
        """;

    public async Task<long> CreateAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        db.RequestExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return execution.Id;
    }

    public async Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"request_executions\" WHERE \"request_id\" = @RequestId ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequestExecutionRecord>(
            new CommandDefinition(
                sql,
                new { RequestId = requestId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<bool> UpdateStatusAsync(
        long id,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.RequestExecutions
            .FirstOrDefaultAsync(execution => execution.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return false;
        }

        execution.Status = status;

        // Ent only writes these when the caller supplied a value; a later
        // status-only update must not clear metadata captured at finalization.
        if (!string.IsNullOrEmpty(upstreamModelId))
        {
            execution.UpstreamModelId = upstreamModelId;
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            execution.ErrorMessage = errorMessage;
        }

        if (responseStatusCode is not null)
        {
            execution.ResponseStatusCode = responseStatusCode;
        }

        if (latencyMs is not null)
        {
            execution.MetricsLatencyMs = latencyMs;
        }

        if (firstTokenLatencyMs is not null)
        {
            execution.MetricsFirstTokenLatencyMs = firstTokenLatencyMs;
        }

        if (reasoningDurationMs is not null)
        {
            execution.MetricsReasoningDurationMs = reasoningDurationMs;
        }

        execution.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseHeaders,
        string? responseBody,
        string? responseChunks,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.RequestExecutions
            .FirstOrDefaultAsync(execution => execution.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return false;
        }

        execution.Status = status;
        execution.ExternalId = externalId;

        if (!string.IsNullOrEmpty(upstreamModelId))
        {
            execution.UpstreamModelId = upstreamModelId;
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            execution.ErrorMessage = errorMessage;
        }

        execution.ResponseHeaders = responseHeaders;
        execution.ResponseBody = responseBody;
        execution.ResponseChunks = responseChunks;
        execution.MetricsLatencyMs = latencyMs;
        execution.MetricsFirstTokenLatencyMs = firstTokenLatencyMs;
        execution.MetricsReasoningDurationMs = reasoningDurationMs;
        execution.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
