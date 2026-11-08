using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Llm.Http;
using AxonHub.Persistence;

namespace AxonHub.Api.Persistence;

/// <summary>
/// Write seam for the request chain: one request row, one execution row per
/// attempt, the usage log of the attempt that produced a response, and the
/// buffered stream chunks of both.
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

    /// <summary>
    /// Port of SaveRequestChunks: writes the buffered stream chunks when the
    /// storage policy enables it.
    /// </summary>
    void SaveRequestChunks(long requestId, IReadOnlyList<StreamEvent> chunks);

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

    /// <summary>Port of SaveRequestExecutionChunks.</summary>
    void SaveExecutionChunks(long executionId, IReadOnlyList<StreamEvent> chunks);

    void CreateUsageLog(UsageLogRecord usageLog);
}

/// <summary>
/// PostgreSQL implementation: each call is its own unit of work, so a failed
/// update can never leave a request holding a poisoned <c>DbContext</c>.
/// </summary>
/// <remarks>
/// The storage-policy gates, the external data-storage writes and the payload
/// markers are ported from biz.RequestService (C020). The reference shares one
/// ent client and its cached data storages across the whole request; the port
/// reads the row and builds a backend per operation (C199).
/// </remarks>
public sealed class PostgresRequestChainRecorder(
    IAxonHubDbContextFactory dbFactory,
    IDataStorageBackendFactory dataStorageBackends) : IRequestChainRecorder
{
    public long CreateRequest(RequestRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var context = dbFactory.CreateDbContext();
        var payloads = CreatePayloadStorage(context);

        var storeRequestBody = payloads.GetStoreRequestBodyAsync().GetAwaiter().GetResult();

        // CreateRequest starts both columns at "{}" and only fills them when
        // the policy allows storing (request.go:203-224).
        var requestBody = storeRequestBody
            ? string.IsNullOrEmpty(request.RequestBody)
                ? RequestPayloadStorage.EmptyJson
                : request.RequestBody
            : RequestPayloadStorage.EmptyJson;
        var requestHeaders = storeRequestBody
            ? string.IsNullOrEmpty(request.RequestHeaders)
                ? RequestPayloadStorage.EmptyJson
                : request.RequestHeaders
            : RequestPayloadStorage.EmptyJson;

        DataStorageRecord? dataStorage = null;
        try
        {
            dataStorage = payloads.GetDefaultDataStorageAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // log.Warn: the request is created without a data storage.
        }

        var useExternalStorage = storeRequestBody
            && RequestPayloadStorage.ShouldUseExternalStorage(dataStorage);

        request.RequestBody = useExternalStorage
            ? RequestPayloadStorage.EmptyJson
            : requestBody;
        request.RequestHeaders = requestHeaders;

        if (dataStorage is not null)
        {
            request.DataStorageId = dataStorage.Id;
        }

        long requestId;
        try
        {
            requestId = new RequestStore(context).CreateAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception) when (!useExternalStorage)
        {
            // The reference retries once with an invalid-body placeholder; an
            // external-storage request fails outright.
            request.Id = 0;
            request.RequestBody = RequestPayloadStorage.InvalidRequestBodyJson;

            using var retryContext = dbFactory.CreateDbContext();
            requestId = new RequestStore(retryContext)
                .CreateAsync(request)
                .GetAwaiter()
                .GetResult();
        }

        if (useExternalStorage)
        {
            // A failed external write only logs; the row keeps the empty body.
            var key = RequestDataKeys.RequestBody(request.ProjectId, requestId);
            try
            {
                payloads.SaveDataAsync(dataStorage!, key, Encoding.UTF8.GetBytes(requestBody))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception)
            {
                // log.Error in the reference; creation continues.
            }
        }

        return requestId;
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
        var payloads = CreatePayloadStorage(context);
        var store = new RequestStore(context);

        var request = store.GetByIdAsync(requestId).GetAwaiter().GetResult();
        if (request is null)
        {
            // UpdateRequestFinalized returns the ent NotFound; the orchestrator
            // only logs it.
            return;
        }

        var storeResponseBody = payloads.GetStoreResponseBodyAsync().GetAwaiter().GetResult();
        var dataStorage = payloads.TryGetRequestDataStorageAsync(request)
            .GetAwaiter()
            .GetResult();

        string? dbBody = null;
        var setBody = false;
        var externalKey = string.Empty;

        if (storeResponseBody)
        {
            // xjson.Marshal of the response payload: the recorder receives the
            // JSON text the reference would have marshalled.
            var write = payloads.WriteResponseBodyAsync(
                    dataStorage,
                    RequestDataKeys.ResponseBody(request.ProjectId, requestId),
                    responseBody ?? "null")
                .GetAwaiter()
                .GetResult();
            dbBody = write.DbValue;
            setBody = write.SetBody;
            externalKey = write.ExternalKey;
        }

        try
        {
            _ = store.UpdateResultAsync(
                    requestId,
                    status,
                    externalId,
                    dbBody,
                    setBody,
                    latencyMs,
                    firstTokenLatencyMs,
                    null,
                    updatedAt)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            payloads.RollbackExternalPayloadAsync(dataStorage, externalKey)
                .GetAwaiter()
                .GetResult();
            throw;
        }
    }

    public void UpdateRequestResponseHeaders(long requestId, string responseHeaders, DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestStore(context)
            .UpdateResponseHeadersAsync(requestId, responseHeaders, updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void SaveRequestChunks(long requestId, IReadOnlyList<StreamEvent> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return;
        }

        using var context = dbFactory.CreateDbContext();
        var payloads = CreatePayloadStorage(context);

        if (!payloads.GetStoreChunksAsync().GetAwaiter().GetResult())
        {
            return;
        }

        var chunkJson = RequestChunkStorage.MarshalChunks(chunks);
        if (chunkJson == "[]")
        {
            return;
        }

        var request = new RequestStore(context).GetByIdAsync(requestId).GetAwaiter().GetResult();
        if (request is null)
        {
            throw new InvalidOperationException(
                "failed to get request: ent: request not found");
        }

        var dataStorage = payloads.TryGetDataStorageAsync(request.DataStorageId)
            .GetAwaiter()
            .GetResult();

        if (RequestPayloadStorage.ShouldUseExternalStorage(dataStorage))
        {
            var key = RequestDataKeys.ResponseChunks(request.ProjectId, requestId);
            try
            {
                payloads.SaveDataAsync(dataStorage!, key, Encoding.UTF8.GetBytes(chunkJson))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to save chunks to external storage: " + ex.Message,
                    ex);
            }

            try
            {
                _ = new RequestStore(context)
                    .UpdateResponseChunksAsync(
                        requestId,
                        RequestChunkStorage.ExternalResponseChunksMarker,
                        DateTime.UtcNow)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                payloads.RollbackExternalPayloadAsync(dataStorage, key)
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException(
                    "failed to mark request chunks as external: " + ex.Message,
                    ex);
            }

            return;
        }

        try
        {
            _ = new RequestStore(context)
                .UpdateResponseChunksAsync(requestId, chunkJson, DateTime.UtcNow)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to save response chunks: " + ex.Message,
                ex);
        }
    }

    public long CreateExecution(RequestExecutionRecord execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        using var context = dbFactory.CreateDbContext();
        var payloads = CreatePayloadStorage(context);

        var storeRequestBody = payloads.GetStoreRequestBodyAsync().GetAwaiter().GetResult();

        var requestBody = storeRequestBody
            ? string.IsNullOrEmpty(execution.RequestBody)
                ? RequestPayloadStorage.EmptyJson
                : execution.RequestBody
            : RequestPayloadStorage.EmptyJson;
        var requestHeaders = storeRequestBody
            ? string.IsNullOrEmpty(execution.RequestHeaders)
                ? RequestPayloadStorage.EmptyJson
                : execution.RequestHeaders
            : RequestPayloadStorage.EmptyJson;

        // CreateRequestExecution reads the storage from the request it belongs
        // to; the port reloads the row on the execution's unit of work.
        var request = new RequestStore(context)
            .GetByIdAsync(execution.RequestId)
            .GetAwaiter()
            .GetResult();

        var requestDataStorageId = request?.DataStorageId;

        DataStorageRecord? dataStorage = null;
        if (requestDataStorageId is > 0)
        {
            dataStorage = payloads.TryGetDataStorageAsync(requestDataStorageId)
                .GetAwaiter()
                .GetResult();
        }

        var useExternalStorage = storeRequestBody
            && RequestPayloadStorage.ShouldUseExternalStorage(dataStorage);

        execution.RequestBody = useExternalStorage
            ? RequestPayloadStorage.EmptyJson
            : requestBody;
        execution.RequestHeaders = requestHeaders;

        if (requestDataStorageId is > 0)
        {
            // The execution inherits the request's storage even when the
            // lookup above failed.
            execution.DataStorageId = requestDataStorageId;
        }

        long executionId;
        try
        {
            executionId = new RequestExecutionStore(context)
                .CreateAsync(execution)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception) when (!useExternalStorage)
        {
            // The retry semantics are inverted here: an external-storage
            // execution fails outright, a database one retries once.
            execution.Id = 0;
            execution.RequestBody = RequestPayloadStorage.InvalidRequestBodyJson;

            using var retryContext = dbFactory.CreateDbContext();
            executionId = new RequestExecutionStore(retryContext)
                .CreateAsync(execution)
                .GetAwaiter()
                .GetResult();
        }

        if (useExternalStorage)
        {
            var key = RequestDataKeys.ExecutionRequestBody(
                execution.ProjectId,
                execution.RequestId,
                executionId);
            try
            {
                payloads.SaveDataAsync(dataStorage!, key, Encoding.UTF8.GetBytes(requestBody))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception)
            {
                // log.Error in the reference; creation continues.
            }
        }

        return executionId;
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
        var payloads = CreatePayloadStorage(context);
        var store = new RequestExecutionStore(context);

        var execution = store.GetByIdAsync(executionId).GetAwaiter().GetResult();
        if (execution is null)
        {
            // UpdateRequestExecutionFinalized returns the ent NotFound; the
            // orchestrator only logs it.
            return;
        }

        var storeResponseBody = payloads.GetStoreResponseBodyAsync().GetAwaiter().GetResult();
        var dataStorage = payloads.TryGetDataStorageAsync(execution.DataStorageId)
            .GetAwaiter()
            .GetResult();

        string? dbBody = null;
        var setBody = false;
        var externalKey = string.Empty;

        if (storeResponseBody)
        {
            var write = payloads.WriteResponseBodyAsync(
                    dataStorage,
                    RequestDataKeys.ExecutionResponseBody(
                        execution.ProjectId,
                        execution.RequestId,
                        executionId),
                    responseBody ?? "null")
                .GetAwaiter()
                .GetResult();
            dbBody = write.DbValue;
            setBody = write.SetBody;
            externalKey = write.ExternalKey;
        }

        try
        {
            _ = store.UpdateResultAsync(
                    executionId,
                    status,
                    externalId,
                    upstreamModelId,
                    dbBody,
                    setBody,
                    errorMessage,
                    latencyMs,
                    firstTokenLatencyMs,
                    null,
                    updatedAt)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            payloads.RollbackExternalPayloadAsync(dataStorage, externalKey)
                .GetAwaiter()
                .GetResult();
            throw;
        }
    }

    public void UpdateExecutionResponseHeaders(long executionId, string responseHeaders, DateTime updatedAt)
    {
        using var context = dbFactory.CreateDbContext();
        _ = new RequestExecutionStore(context)
            .UpdateResponseHeadersAsync(executionId, responseHeaders, updatedAt)
            .GetAwaiter()
            .GetResult();
    }

    public void SaveExecutionChunks(long executionId, IReadOnlyList<StreamEvent> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return;
        }

        using var context = dbFactory.CreateDbContext();
        var payloads = CreatePayloadStorage(context);

        if (!payloads.GetStoreChunksAsync().GetAwaiter().GetResult())
        {
            return;
        }

        var chunkJson = RequestChunkStorage.MarshalChunks(chunks);
        if (chunkJson == "[]")
        {
            return;
        }

        var execution = new RequestExecutionStore(context)
            .GetByIdAsync(executionId)
            .GetAwaiter()
            .GetResult();
        if (execution is null)
        {
            throw new InvalidOperationException(
                "failed to get request execution: ent: request execution not found");
        }

        var dataStorage = payloads.TryGetDataStorageAsync(execution.DataStorageId)
            .GetAwaiter()
            .GetResult();

        if (RequestPayloadStorage.ShouldUseExternalStorage(dataStorage))
        {
            var key = RequestDataKeys.ExecutionResponseChunks(
                execution.ProjectId,
                execution.RequestId,
                executionId);
            try
            {
                payloads.SaveDataAsync(dataStorage!, key, Encoding.UTF8.GetBytes(chunkJson))
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to save chunks to external storage: " + ex.Message,
                    ex);
            }

            try
            {
                _ = new RequestExecutionStore(context)
                    .UpdateResponseChunksAsync(
                        executionId,
                        RequestChunkStorage.ExternalResponseChunksMarker,
                        DateTime.UtcNow)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                payloads.RollbackExternalPayloadAsync(dataStorage, key)
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException(
                    "failed to mark execution chunks as external: " + ex.Message,
                    ex);
            }

            return;
        }

        try
        {
            _ = new RequestExecutionStore(context)
                .UpdateResponseChunksAsync(executionId, chunkJson, DateTime.UtcNow)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to save response chunks: " + ex.Message,
                ex);
        }
    }

    public void CreateUsageLog(UsageLogRecord usageLog)
    {
        ArgumentNullException.ThrowIfNull(usageLog);

        using var context = dbFactory.CreateDbContext();
        _ = new UsageLogStore(context).CreateAsync(usageLog).GetAwaiter().GetResult();
    }

    private RequestPayloadStorage CreatePayloadStorage(AxonHubDbContext context)
        => new(
            new DataStorageStore(context),
            new SystemSettingsAdminService(new SystemStore(context)),
            dataStorageBackends);
}
