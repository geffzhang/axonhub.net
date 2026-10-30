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
        string? responseBody,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt);

    /// <summary>
    /// Port of UpdateRequestResponseHeaders: the masked downstream response
    /// headers, captured at the HTTP boundary after the handler returns.
    /// </summary>
    void UpdateRequestResponseHeaders(long requestId, string responseHeaders, DateTime updatedAt);

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
        string? responseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt);

    /// <summary>
    /// Port of UpdateRequestExecutionResponseHeaders: the masked upstream
    /// response headers, written as soon as the transport reports them.
    /// </summary>
    void UpdateExecutionResponseHeaders(long executionId, string responseHeaders, DateTime updatedAt);

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
        string? responseBody,
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
                responseBody,
                latencyMs,
                firstTokenLatencyMs,
                null,
                updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void UpdateRequestResponseHeaders(long requestId, string responseHeaders, DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestStore(context)
            .UpdateResponseHeadersAsync(requestId, responseHeaders, updatedAt)
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
        string? responseBody,
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
                responseBody,
                errorMessage,
                latencyMs,
                firstTokenLatencyMs,
                null,
                updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void UpdateExecutionResponseHeaders(long executionId, string responseHeaders, DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestExecutionStore(context)
            .UpdateResponseHeadersAsync(executionId, responseHeaders, updatedAt)
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
