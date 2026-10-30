using AxonHub.Persistence;

namespace AxonHub.Api.Persistence;

/// <summary>
/// Write seam for the request chain: one request row, one execution row per
/// attempt, and the usage log of the attempt that produced a response.
/// </summary>
/// <remarks>
/// The pipeline and the SSE writer are synchronous (the reference persists from
/// synchronous middleware too), while the stores are async. The production
/// implementation therefore creates a short-lived unit of work per call and
/// blocks the calling thread; see difference C018.
/// </remarks>
public interface IRequestChainRecorder
{
    long CreateRequest(RequestRecord request);

    void UpdateRequestChannel(long requestId, long channelId);

    /// <summary>Port of UpdateRequestStatus: status only.</summary>
    void UpdateRequestStatus(long requestId, string status, DateTime updatedAt);

    /// <summary>Port of UpdateRequestFinalized.</summary>
    void UpdateRequestResult(
        long requestId,
        string status,
        string? externalId,
        string? responseHeaders,
        string? responseBody,
        string? responseChunks,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt);

    long CreateExecution(RequestExecutionRecord execution);

    /// <summary>Port of UpdateRequestExecutionStatusWithMetrics.</summary>
    void UpdateExecutionStatus(
        long executionId,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt);

    /// <summary>Port of UpdateRequestExecutionFinalized.</summary>
    void UpdateExecutionResult(
        long executionId,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseHeaders,
        string? responseBody,
        string? responseChunks,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt);

    void CreateUsageLog(UsageLogRecord usageLog);
}

/// <summary>
/// PostgreSQL implementation: each call is its own unit of work, so a failed
/// update can never leave a request holding a poisoned <c>DbContext</c>.
/// </summary>
public sealed class PostgresRequestChainRecorder(IAxonHubDbContextFactory dbFactory) : IRequestChainRecorder
{
    public long CreateRequest(RequestRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var context = dbFactory.CreateDbContext();
        return new RequestStore(context).CreateAsync(request).GetAwaiter().GetResult();
    }

    public void UpdateRequestChannel(long requestId, long channelId)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestStore(context)
            .UpdateChannelAsync(requestId, channelId, DateTime.UtcNow)
            .GetAwaiter()
            .GetResult();
    }

    public void UpdateRequestStatus(long requestId, string status, DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestStore(context)
            .UpdateStatusAsync(requestId, status, updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void UpdateRequestResult(
        long requestId,
        string status,
        string? externalId,
        string? responseHeaders,
        string? responseBody,
        string? responseChunks,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestStore(context)
            .UpdateResultAsync(
                requestId,
                status,
                externalId,
                responseHeaders,
                responseBody,
                responseChunks,
                latencyMs,
                firstTokenLatencyMs,
                null,
                updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public long CreateExecution(RequestExecutionRecord execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        using var context = dbFactory.CreateDbContext();
        return new RequestExecutionStore(context).CreateAsync(execution).GetAwaiter().GetResult();
    }

    public void UpdateExecutionStatus(
        long executionId,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestExecutionStore(context)
            .UpdateStatusAsync(
                executionId,
                status,
                responseStatusCode,
                upstreamModelId,
                errorMessage,
                latencyMs,
                firstTokenLatencyMs,
                null,
                updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void UpdateExecutionResult(
        long executionId,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseHeaders,
        string? responseBody,
        string? responseChunks,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestExecutionStore(context)
            .UpdateResultAsync(
                executionId,
                status,
                externalId,
                upstreamModelId,
                responseHeaders,
                responseBody,
                responseChunks,
                errorMessage,
                latencyMs,
                firstTokenLatencyMs,
                null,
                updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void CreateUsageLog(UsageLogRecord usageLog)
    {
        ArgumentNullException.ThrowIfNull(usageLog);

        using var context = dbFactory.CreateDbContext();
        _ = new UsageLogStore(context).CreateAsync(usageLog).GetAwaiter().GetResult();
    }
}
