using System.Text;
using System.Text.Json;
using AxonHub.Api.Persistence;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// The result of writing a response body: whether the DB column is updated and
/// with which value, plus the external key left behind when the payload went to
/// object storage (empty when it did not).
/// </summary>
public readonly record struct ResponseBodyWrite(bool SetBody, string? DbValue, string ExternalKey);

/// <summary>
/// Port of the external-storage halves of biz.RequestService and the
/// data-storage read/write helpers they use: getDataStorage
/// (request_internal.go:10-22), shouldUseExternalStorage (request.go:64-72),
/// the loaders (request.go:1369-1683) and the SaveData/LoadData/DeleteData
/// subset of data_storage.go:530-733.
/// </summary>
/// <remarks>
/// The reference caches data storages and filesystems in the process; the port
/// reads the row on every call and builds a backend per operation (C199).
/// Failures that the reference only logs (external read/write errors) degrade
/// to the same observable values here: "{}", an empty chunk list, or "no
/// marker written". The two chunk loaders consult the live preview registry
/// first for an active stream, exactly like RequestService.LoadResponseChunks
/// and LoadRequestExecutionResponseChunks (request.go:1509-1683).
/// </remarks>
public sealed class RequestPayloadStorage(
    IDataStorageStore dataStorages,
    ISystemSettingsAdminService settings,
    IDataStorageBackendFactory backends,
    LiveStreamRegistry? liveStreamRegistry = null)
{
    public const string EmptyJson = "{}";

    /// <summary>ExternalResponseBodyMarker (request.go:80).</summary>
    public const string ExternalResponseBodyMarker = "{\"_ext\":1}";

    /// <summary>_InvalidRequestBodyJSON (request.go:74-76): the retry placeholder.</summary>
    public const string InvalidRequestBodyJson = "{\"message\":\"invalid text\"}";

    private const string DatabaseStorageType = "database";
    private const string CompletedStatus = "completed";
    private const string ProcessingStatus = "processing";

    /// <summary>
    /// The storage policy read used by the write paths: a failure keeps the
    /// reference's default (store for the two body flags, false for chunks).
    /// </summary>
    public async Task<StoragePolicy?> TryGetStoragePolicyAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await settings.GetStoragePolicyAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>CreateRequest's storeRequestBody decision (default true).</summary>
    public async Task<bool> GetStoreRequestBodyAsync(CancellationToken cancellationToken = default)
    {
        var policy = await TryGetStoragePolicyAsync(cancellationToken).ConfigureAwait(false);
        return policy?.StoreRequestBody ?? true;
    }

    /// <summary>UpdateRequestFinalized's storeResponseBody decision (default true).</summary>
    public async Task<bool> GetStoreResponseBodyAsync(CancellationToken cancellationToken = default)
    {
        var policy = await TryGetStoragePolicyAsync(cancellationToken).ConfigureAwait(false);
        return policy?.StoreResponseBody ?? true;
    }

    /// <summary>SaveRequestChunks' StoreChunks decision (default false).</summary>
    public async Task<bool> GetStoreChunksAsync(CancellationToken cancellationToken = default)
    {
        var policy = await TryGetStoragePolicyAsync(cancellationToken).ConfigureAwait(false);
        return policy?.StoreChunks ?? false;
    }

    /// <summary>
    /// Port of DataStorageService.GetDefaultDataStorage (data_storage.go:337-361):
    /// the configured default, or the primary storage when the id is absent or
    /// points at a deleted row. Both wraps are preserved.
    /// </summary>
    public async Task<DataStorageRecord> GetDefaultDataStorageAsync(
        CancellationToken cancellationToken = default)
    {
        long id;
        try
        {
            id = await settings.GetDefaultDataStorageIdAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get default data storage ID: " + ex.Message,
                ex);
        }

        var storage = await dataStorages.GetByIdAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (storage is not null)
        {
            return storage;
        }

        return await GetPrimaryDataStorageAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of RequestService.getDataStorage: id 0 means the primary storage.
    /// Errors use the reference's wrapped ent NotFound texts.
    /// </summary>
    public async Task<DataStorageRecord> GetDataStorageAsync(
        long? dataStorageId,
        CancellationToken cancellationToken = default)
    {
        if (dataStorageId is null or 0)
        {
            return await GetPrimaryDataStorageAsync(cancellationToken).ConfigureAwait(false);
        }

        var storage = await dataStorages.GetByIdAsync(dataStorageId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (storage is null)
        {
            throw new InvalidOperationException(
                "failed to get data storage by ID " + dataStorageId.Value
                + ": ent: data storage not found");
        }

        return storage;
    }

    /// <summary>Port of GetDataStorageByID (data_storage.go:289-309).</summary>
    public async Task<DataStorageRecord> GetDataStorageByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var storage = await dataStorages.GetByIdAsync(id, cancellationToken)
            .ConfigureAwait(false);
        if (storage is null)
        {
            throw new InvalidOperationException(
                "failed to get data storage by ID " + id + ": ent: data storage not found");
        }

        return storage;
    }

    private async Task<DataStorageRecord> GetPrimaryDataStorageAsync(
        CancellationToken cancellationToken)
    {
        var storage = await dataStorages.GetPrimaryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (storage is null)
        {
            throw new InvalidOperationException(
                "failed to get primary data storage: ent: data storage not found");
        }

        return storage;
    }

    /// <summary>Port of shouldUseExternalStorage (request.go:64-72).</summary>
    public static bool ShouldUseExternalStorage(DataStorageRecord? storage)
        => storage is not null && !storage.Primary;

    /// <summary>Port of SaveData (data_storage.go:531-584).</summary>
    public async Task SaveDataAsync(
        DataStorageRecord storage,
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (storage.Type == DatabaseStorageType)
        {
            // The row itself is the storage; there is nothing to write.
            return;
        }

        using var backend = backends.Create(storage);
        await backend.WriteAsync(key, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of LoadData (data_storage.go:696-733).</summary>
    public async Task<byte[]> LoadDataAsync(
        DataStorageRecord storage,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (storage.Type == DatabaseStorageType)
        {
            // For database storage the key is the data itself.
            return Encoding.UTF8.GetBytes(key);
        }

        using var backend = backends.Create(storage);
        return await backend.ReadAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of DeleteData (data_storage.go:651-691).</summary>
    public async Task DeleteDataAsync(
        DataStorageRecord storage,
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (storage.Type == DatabaseStorageType)
        {
            return;
        }

        using var backend = backends.Create(storage);
        await backend.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// UpdateRequestFinalized's body branch: store in the column, or write the
    /// object and mark the row. A failed external write leaves the column
    /// untouched, exactly like the reference.
    /// </summary>
    public async Task<ResponseBodyWrite> WriteResponseBodyAsync(
        DataStorageRecord? storage,
        string key,
        string bodyJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bodyJson);

        if (!ShouldUseExternalStorage(storage))
        {
            return new ResponseBodyWrite(true, bodyJson, string.Empty);
        }

        try
        {
            await SaveDataAsync(storage!, key, Encoding.UTF8.GetBytes(bodyJson), cancellationToken)
                .ConfigureAwait(false);
            return new ResponseBodyWrite(true, ExternalResponseBodyMarker, key);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ResponseBodyWrite(false, null, string.Empty);
        }
    }

    /// <summary>
    /// Port of UpdateRequestStatusExternalIDAndResponseBody's persistence half
    /// (request.go:725-814), used by the video task snapshot flow.
    /// </summary>
    public async Task SetRequestStatusExternalIdAndResponseBodyAsync(
        IRequestStore requestStore,
        RequestRecord request,
        string status,
        string externalId,
        string responseBodyJson,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestStore);
        ArgumentNullException.ThrowIfNull(request);

        var storeResponseBody = await GetStoreResponseBodyAsync(cancellationToken)
            .ConfigureAwait(false);
        var dataStorage = await TryGetRequestDataStorageAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var dbBody = (string?)null;
        var setBody = false;
        var externalKey = string.Empty;

        if (storeResponseBody)
        {
            var write = await WriteResponseBodyAsync(
                    dataStorage,
                    RequestDataKeys.ResponseBody(request.ProjectId, request.Id),
                    responseBodyJson,
                    cancellationToken)
                .ConfigureAwait(false);
            dbBody = write.DbValue;
            setBody = write.SetBody;
            externalKey = write.ExternalKey;
        }

        try
        {
            await requestStore.UpdateStatusExternalIdAndResponseBodyAsync(
                    request.Id,
                    status,
                    externalId,
                    dbBody,
                    setBody,
                    updatedAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await RollbackExternalPayloadAsync(dataStorage, externalKey, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The data storage lookup shared by the update paths: the request row's
    /// storage; a lookup error is logged and treated as "not configured".
    /// </summary>
    public async Task<DataStorageRecord?> TryGetRequestDataStorageAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await TryGetDataStorageAsync(request.DataStorageId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The data storage lookup shared by the update paths: the row's storage;
    /// a lookup error is logged and treated as "not configured". An execution
    /// uses <c>client.DataStorage.Get</c> directly rather than the wrapping
    /// GetDataStorageByID, but the outcome (warn and continue) is the same.
    /// </summary>
    public async Task<DataStorageRecord?> TryGetDataStorageAsync(
        long? dataStorageId,
        CancellationToken cancellationToken = default)
    {
        if (dataStorageId is not > 0)
        {
            return null;
        }

        try
        {
            return await dataStorages.GetByIdAsync(dataStorageId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Port of rollbackExternalPayload (request.go:113-124): warn only.</summary>
    public async Task RollbackExternalPayloadAsync(
        DataStorageRecord? storage,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (storage is null || string.IsNullOrEmpty(key))
        {
            return;
        }

        try
        {
            await DeleteDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The reference logs and continues.
        }
    }

    /// <summary>Port of LoadRequestBody (request.go:1369-1401).</summary>
    public async Task<string> LoadRequestBodyAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        DataStorageRecord storage;
        try
        {
            storage = await GetDataStorageAsync(request.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }

        if (!ShouldUseExternalStorage(storage))
        {
            return string.IsNullOrEmpty(request.RequestBody) ? EmptyJson : request.RequestBody;
        }

        var key = RequestDataKeys.RequestBody(request.ProjectId, request.Id);

        try
        {
            var data = await LoadDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            return IsValidJson(data) ? Encoding.UTF8.GetString(data) : EmptyJson;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }
    }

    /// <summary>Port of LoadResponseBody (request.go:1403-1436).</summary>
    public async Task<string> LoadResponseBodyAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Status != CompletedStatus)
        {
            return EmptyJson;
        }

        DataStorageRecord storage;
        try
        {
            storage = await GetDataStorageAsync(request.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }

        if (!ShouldUseExternalStorage(storage))
        {
            return SanitizeLoadedResponseBody(request.ResponseBody);
        }

        var key = RequestDataKeys.ResponseBody(request.ProjectId, request.Id);

        try
        {
            var data = await LoadDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            return IsValidJson(data) ? Encoding.UTF8.GetString(data) : EmptyJson;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }
    }

    /// <summary>Port of LoadResponseChunks (request.go:1509-1560).</summary>
    public async Task<IReadOnlyList<string>> LoadResponseChunksAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Live preview for active streaming requests (request.go:1511-1515).
        if (request.Stream && request.Status == ProcessingStatus)
        {
            return liveStreamRegistry?.GetRequestChunks(request.Id) ?? [];
        }

        if (!request.Stream || !IsFinishedStatus(request.Status))
        {
            return [];
        }

        DataStorageRecord storage;
        try
        {
            storage = await GetDataStorageAsync(request.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No external storage configured: the row is authoritative.
            return SanitizeLoadedResponseChunks(request.ResponseChunks);
        }

        if (!ShouldUseExternalStorage(storage))
        {
            return SanitizeLoadedResponseChunks(request.ResponseChunks);
        }

        var key = RequestDataKeys.ResponseChunks(request.ProjectId, request.Id);

        try
        {
            var data = await LoadDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            return ParseChunkArray(data, requireValidJson: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Port of LoadRequestExecutionRequestBody (request.go:1562-1594).</summary>
    public async Task<string> LoadExecutionRequestBodyAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        DataStorageRecord storage;
        try
        {
            storage = await GetDataStorageAsync(execution.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }

        if (!ShouldUseExternalStorage(storage))
        {
            return string.IsNullOrEmpty(execution.RequestBody) ? EmptyJson : execution.RequestBody;
        }

        var key = RequestDataKeys.ExecutionRequestBody(
            execution.ProjectId,
            execution.RequestId,
            execution.Id);

        try
        {
            var data = await LoadDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            return IsValidJson(data) ? Encoding.UTF8.GetString(data) : EmptyJson;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }
    }

    /// <summary>Port of LoadRequestExecutionResponseBody (request.go:1596-1629).</summary>
    public async Task<string> LoadExecutionResponseBodyAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.Status != CompletedStatus)
        {
            return EmptyJson;
        }

        DataStorageRecord storage;
        try
        {
            storage = await GetDataStorageAsync(execution.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }

        if (!ShouldUseExternalStorage(storage))
        {
            return SanitizeLoadedResponseBody(execution.ResponseBody);
        }

        var key = RequestDataKeys.ExecutionResponseBody(
            execution.ProjectId,
            execution.RequestId,
            execution.Id);

        try
        {
            var data = await LoadDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            return IsValidJson(data) ? Encoding.UTF8.GetString(data) : EmptyJson;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return EmptyJson;
        }
    }

    /// <summary>Port of LoadRequestExecutionResponseChunks (request.go:1631-1683).</summary>
    public async Task<IReadOnlyList<string>> LoadExecutionResponseChunksAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        // Live preview for active streaming executions (request.go:1641-1645).
        if (execution.Stream && execution.Status == ProcessingStatus)
        {
            return liveStreamRegistry?.GetExecutionChunks(execution.Id) ?? [];
        }

        if (!execution.Stream || !IsFinishedStatus(execution.Status))
        {
            return [];
        }

        DataStorageRecord storage;
        try
        {
            storage = await GetDataStorageAsync(execution.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SanitizeLoadedResponseChunks(execution.ResponseChunks);
        }

        if (!ShouldUseExternalStorage(storage))
        {
            return SanitizeLoadedResponseChunks(execution.ResponseChunks);
        }

        var key = RequestDataKeys.ExecutionResponseChunks(
            execution.ProjectId,
            execution.RequestId,
            execution.Id);

        try
        {
            var data = await LoadDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            return ParseChunkArray(data, requireValidJson: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Port of sanitizeLoadedResponseBody (request.go:97-103).</summary>
    public static string SanitizeLoadedResponseBody(string? body)
    {
        if (string.IsNullOrEmpty(body) || IsExternalResponseBodyMarker(body))
        {
            return EmptyJson;
        }

        return body;
    }

    /// <summary>Port of isExternalResponseBodyMarker (request.go:84-91).</summary>
    public static bool IsExternalResponseBodyMarker(string body)
        => string.Equals(
            Encoding.UTF8.GetString(
                AxonHub.Llm.Json.GoJson.Compact(Encoding.UTF8.GetBytes(body))),
            ExternalResponseBodyMarker,
            StringComparison.Ordinal);

    /// <summary>
    /// Port of sanitizeLoadedResponseChunks (request.go:105-111): the single
    /// marker element collapses to an empty list.
    /// </summary>
    public static IReadOnlyList<string> SanitizeLoadedResponseChunks(string? chunksJson)
    {
        if (string.IsNullOrEmpty(chunksJson))
        {
            return [];
        }

        using var document = JsonDocument.Parse(chunksJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chunks = document.RootElement.EnumerateArray()
            .Select(static chunk => chunk.GetRawText())
            .ToList();

        return chunks.Count == 1 && IsExternalResponseBodyMarker(chunks[0])
            ? []
            : chunks;
    }

    /// <summary>
    /// The external-chunks branch of the loaders: an empty object, invalid JSON
    /// or a non-array document all render as an empty list.
    /// </summary>
    private static IReadOnlyList<string> ParseChunkArray(byte[] data, bool requireValidJson)
    {
        if (data.Length == 0)
        {
            return [];
        }

        if (requireValidJson && !IsValidJson(data))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement.EnumerateArray()
                .Select(static chunk => chunk.GetRawText())
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsFinishedStatus(string status)
        => status is "completed" or "failed" or "canceled";

    /// <summary>Go's json.Valid: syntax only, values do not need to be decoded.</summary>
    private static bool IsValidJson(byte[] data)
    {
        try
        {
            using var _ = JsonDocument.Parse(data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
