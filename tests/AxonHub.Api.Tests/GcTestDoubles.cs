using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// The settings methods the GC worker and the auto-disable/webhook slices read;
/// every other member is outside those slices and throws so an accidental call
/// fails the test loudly.
/// </summary>
internal sealed class FakeGcSettingsService : ISystemSettingsAdminService
{
    public StoragePolicy Policy { get; set; } = StoragePolicy.Default;

    public Exception? Failure { get; set; }

    public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.Default;

    public Exception? RetryPolicyFailure { get; set; }

    public WebhookNotifierConfig WebhookNotifier { get; set; } = WebhookNotifierConfigCodec.Default;

    public Exception? WebhookNotifierFailure { get; set; }

    public Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default)
        => RetryPolicyFailure is null
            ? Task.FromResult(RetryPolicy)
            : Task.FromException<RetryPolicy>(RetryPolicyFailure);

    public Task<ModelSettings> GetModelSettingsAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SecuritySettings> GetSecuritySettingsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetRetryPolicyAsync(
        RetryPolicy policy,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetModelSettingsAsync(
        ModelSettings settings,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetSecuritySettingsAsync(
        SecuritySettings settings,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<StoragePolicy> GetStoragePolicyAsync(
        CancellationToken cancellationToken = default)
        => Failure is null
            ? Task.FromResult(Policy)
            : Task.FromException<StoragePolicy>(Failure);

    public Task SetStoragePolicyAsync(
        StoragePolicy policy,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AutoBackupSettings> GetAutoBackupSettingsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetAutoBackupSettingsAsync(
        AutoBackupSettings settings,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task UpdateAutoBackupLastRunAsync(
        string lastError,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<long> GetDefaultDataStorageIdAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetDefaultDataStorageIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SystemGeneralSettings> GetGeneralSettingsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetGeneralSettingsAsync(
        SystemGeneralSettings settings,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SystemChannelSettings> GetChannelSettingsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task UpdateChannelSettingsAsync(
        SystemChannelSettingsPatch patch,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> GetBrandNameAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> GetBrandLogoAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<string> GetTitleAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetBrandNameAsync(
        string brandName,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetBrandLogoAsync(
        string brandLogo,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetTitleAsync(string title, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> GetUserAgentPassThroughAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetUserAgentPassThroughAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> GetPassThroughAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetPassThroughAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<bool> GetInjectUsageCostEnabledAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetInjectUsageCostEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    /// <summary>The video-storage scan's settings; scripted by the C234 tests.</summary>
    public VideoStorageSettings VideoStorage { get; set; } = SystemSettingsDefaults.VideoStorage;

    public Task<VideoStorageSettings> GetVideoStorageSettingsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(VideoStorage);

    public Task SetVideoStorageSettingsAsync(
        VideoStorageSettings settings,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<ProxyPreset>> GetProxyPresetsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SaveProxyPresetAsync(
        ProxyPreset preset,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteProxyPresetAsync(
        string url,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OnboardingRecord?> GetOnboardingInfoAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task CompleteSystemModelSettingOnboardingAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task CompleteAutoDisableChannelOnboardingAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<WebhookNotifierConfig> GetWebhookNotifierConfigAsync(
        CancellationToken cancellationToken = default)
        => WebhookNotifierFailure is null
            ? Task.FromResult(WebhookNotifier)
            : Task.FromException<WebhookNotifierConfig>(WebhookNotifierFailure);

    public Task SetWebhookNotifierConfigAsync(
        WebhookNotifierConfig config,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CatalogSettings> GetCatalogSettingsAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CatalogSettings> GetCatalogSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetCatalogSettingsAsync(
        CatalogSettings settings,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

/// <summary>
/// Records the GraphQL entry points and completes <see cref="CleanupStarted"/>
/// when the trigger mutation's detached task reaches the worker, so the
/// endpoint test can await the background run without a wall-clock sleep.
/// </summary>
internal sealed class FakeGcCleanupService : IGcCleanupService
{
    public List<GcCleanupRequest> CleanupRequests { get; } = [];

    public List<GcCleanupRequest> PreviewInputs { get; } = [];

    public IReadOnlyList<GcCleanupPreviewItem> PreviewResult { get; set; } = [];

    public TaskCompletionSource<GcCleanupRequest> CleanupStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RunCleanupNowAsync(
        GcCleanupRequest input,
        CancellationToken cancellationToken = default)
    {
        CleanupRequests.Add(input);
        CleanupStarted.TrySetResult(input);
        return Task.CompletedTask;
    }

    public Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<GcCleanupPreviewItem>> PreviewCleanupAsync(
        GcCleanupRequest input,
        CancellationToken cancellationToken = default)
    {
        PreviewInputs.Add(input);
        return Task.FromResult(PreviewResult);
    }
}

/// <summary>
/// Batch-replaying IGcStore: each list method pops one scripted batch per call
/// and records every argument, so the worker's batching and skip-id
/// accumulation are observable without a database.
/// </summary>
internal sealed class FakeGcStore : IGcStore
{
    public Queue<IReadOnlyList<GcRequestRef>> OldRequestBatches { get; } = new();

    public Queue<IReadOnlyList<GcExecutionRef>> OldExecutionBatches { get; } = new();

    public Queue<IReadOnlyList<long>> OldUsageLogBatches { get; } = new();

    public Queue<IReadOnlyList<long>> OldThreadBatches { get; } = new();

    public Queue<IReadOnlyList<long>> OldTraceBatches { get; } = new();

    public Queue<IReadOnlyList<long>> OldChannelProbeBatches { get; } = new();

    public Dictionary<GcPayloadKind, Queue<IReadOnlyList<GcRequestRef>>> RequestPayloadBatches { get; } = [];

    public Dictionary<GcPayloadKind, Queue<IReadOnlyList<GcExecutionRef>>> ExecutionPayloadBatches { get; } = [];

    public List<DateTime> OldExecutionCutoffs { get; } = [];

    public List<DateTime> OldRequestCutoffs { get; } = [];

    public List<DateTime> UsageLogCutoffs { get; } = [];

    public List<DateTime> ThreadCutoffs { get; } = [];

    public List<DateTime> TraceCutoffs { get; } = [];

    public List<long> ChannelProbeCutoffs { get; } = [];

    public Exception? ChannelProbeListFailure { get; set; }

    public List<long> CountRequestCutoffs { get; } = [];

    public List<long> CountExecutionCutoffs { get; } = [];

    public List<long> CountUsageLogCutoffs { get; } = [];

    public List<PayloadListCall<GcRequestRef>> RequestPayloadCalls { get; } = [];

    public List<PayloadListCall<GcExecutionRef>> ExecutionPayloadCalls { get; } = [];

    public List<(GcPayloadKind Kind, IReadOnlyList<long> Ids)> ClearedRequests { get; } = [];

    public List<(GcPayloadKind Kind, IReadOnlyList<long> Ids)> ClearedExecutions { get; } = [];

    public List<IReadOnlyList<long>> DeletedExecutions { get; } = [];

    public List<IReadOnlyList<long>> DeletedRequests { get; } = [];

    public List<IReadOnlyList<long>> DeletedUsageLogs { get; } = [];

    public List<IReadOnlyList<long>> DeletedThreads { get; } = [];

    public List<IReadOnlyList<long>> DeletedTraces { get; } = [];

    public List<IReadOnlyList<long>> DeletedChannelProbes { get; } = [];

    public List<bool> VacuumCalls { get; } = [];

    public long RequestCount { get; set; }

    public long ExecutionCount { get; set; }

    public long UsageLogCount { get; set; }

    public Exception? CountRequestsFailure { get; set; }

    public Task<IReadOnlyList<GcExecutionRef>> ListOldExecutionsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        OldExecutionCutoffs.Add(cutoff);
        return Task.FromResult(Dequeue(OldExecutionBatches));
    }

    public Task<IReadOnlyList<GcRequestRef>> ListOldRequestsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        OldRequestCutoffs.Add(cutoff);
        return Task.FromResult(Dequeue(OldRequestBatches));
    }

    public Task<IReadOnlyList<long>> ListOldUsageLogIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        UsageLogCutoffs.Add(cutoff);
        return Task.FromResult(Dequeue(OldUsageLogBatches));
    }

    public Task<IReadOnlyList<long>> ListOldThreadIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        ThreadCutoffs.Add(cutoff);
        return Task.FromResult(Dequeue(OldThreadBatches));
    }

    public Task<IReadOnlyList<long>> ListOldTraceIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        TraceCutoffs.Add(cutoff);
        return Task.FromResult(Dequeue(OldTraceBatches));
    }

    public Task<IReadOnlyList<long>> ListOldChannelProbeIdsAsync(
        long cutoffUnix,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        ChannelProbeCutoffs.Add(cutoffUnix);
        return ChannelProbeListFailure is { } failure
            ? Task.FromException<IReadOnlyList<long>>(failure)
            : Task.FromResult(Dequeue(OldChannelProbeBatches));
    }

    public Task<int> DeleteExecutionsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        DeletedExecutions.Add(ids);
        return Task.FromResult(ids.Count);
    }

    public Task<int> DeleteRequestsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        DeletedRequests.Add(ids);
        return Task.FromResult(ids.Count);
    }

    public Task<int> DeleteUsageLogsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        DeletedUsageLogs.Add(ids);
        return Task.FromResult(ids.Count);
    }

    public Task<int> DeleteThreadsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        DeletedThreads.Add(ids);
        return Task.FromResult(ids.Count);
    }

    public Task<int> DeleteTracesAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        DeletedTraces.Add(ids);
        return Task.FromResult(ids.Count);
    }

    public Task<int> DeleteChannelProbesAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        DeletedChannelProbes.Add(ids);
        return Task.FromResult(ids.Count);
    }

    public Task<long> CountOldRequestsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        CountRequestCutoffs.Add(cutoff.Ticks);
        return CountRequestsFailure is null
            ? Task.FromResult(RequestCount)
            : Task.FromException<long>(CountRequestsFailure);
    }

    public Task<long> CountOldExecutionsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        CountExecutionCutoffs.Add(cutoff.Ticks);
        return Task.FromResult(ExecutionCount);
    }

    public Task<long> CountOldUsageLogsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        CountUsageLogCutoffs.Add(cutoff.Ticks);
        return Task.FromResult(UsageLogCount);
    }

    public Task<IReadOnlyList<GcRequestRef>> ListRequestPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryStorageId,
        IReadOnlyList<long> skipIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        RequestPayloadCalls.Add(new PayloadListCall<GcRequestRef>(
            kind, cutoff, primaryStorageId, skipIds.ToArray()));
        return Task.FromResult(DequeuePayload(RequestPayloadBatches, kind));
    }

    public Task<IReadOnlyList<GcExecutionRef>> ListExecutionPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryStorageId,
        IReadOnlyList<long> skipIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Assert.Equal(GcWorker.DefaultBatchSize, limit);
        ExecutionPayloadCalls.Add(new PayloadListCall<GcExecutionRef>(
            kind, cutoff, primaryStorageId, skipIds.ToArray()));
        return Task.FromResult(DequeuePayload(ExecutionPayloadBatches, kind));
    }

    public Task<int> ClearRequestPayloadsAsync(
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ClearedRequests.Add((kind, ids.ToArray()));
        return Task.FromResult(ids.Count);
    }

    public Task<int> ClearExecutionPayloadsAsync(
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ClearedExecutions.Add((kind, ids.ToArray()));
        return Task.FromResult(ids.Count);
    }

    public Task VacuumAsync(bool full, CancellationToken cancellationToken = default)
    {
        VacuumCalls.Add(full);
        return Task.CompletedTask;
    }

    private static IReadOnlyList<T> Dequeue<T>(Queue<IReadOnlyList<T>> batches)
        => batches.Count > 0 ? batches.Dequeue() : [];

    private static IReadOnlyList<T> DequeuePayload<T>(
        Dictionary<GcPayloadKind, Queue<IReadOnlyList<T>>> batches,
        GcPayloadKind kind)
        => batches.TryGetValue(kind, out var queue) && queue.Count > 0 ? queue.Dequeue() : [];

    internal sealed record PayloadListCall<T>(
        GcPayloadKind Kind,
        DateTime Cutoff,
        long PrimaryStorageId,
        IReadOnlyList<long> SkipIds);
}

/// <summary>The two storage lookups the GC worker performs.</summary>
internal sealed class FakeGcDataStorageStore : IDataStorageStore
{
    public Dictionary<long, DataStorageRecord> Storages { get; } = [];

    public DataStorageRecord? Primary { get; set; }

    public Task<long> CreateAsync(
        DataStorageRecord storage,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<DataStorageRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(
            Storages.TryGetValue(id, out var row) && row.DeletedAt == 0 ? row : null);

    public Task<DataStorageRecord?> GetByIdIncludingDeletedAsync(
        long id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Storages.TryGetValue(id, out var row) ? row : null);

    public Task<DataStorageRecord?> GetPrimaryAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Primary);

    public Task<DataStorageRecord?> GetPrimaryIncludingDeletedAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(Primary);

    public Task<bool> ExistsByNameAsync(
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ListPage<DataStorageRecord>> ListAsync(
        DataStorageListQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<DataStorageRecord?> UpdateAsync(
        DataStorageUpdate update,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

internal sealed class FakeGcDataStorageBackendFactory : IDataStorageBackendFactory
{
    public List<FakeGcDataStorageBackend> Created { get; } = [];

    public Exception? CreateFailure { get; set; }

    public Func<DataStorageRecord, Exception?>? DeleteFailure { get; set; }

    public IDataStorageBackend Create(DataStorageRecord storage)
    {
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        var backend = new FakeGcDataStorageBackend(storage, DeleteFailure?.Invoke(storage));
        Created.Add(backend);
        return backend;
    }
}

internal sealed class FakeGcDataStorageBackend(
    DataStorageRecord storage,
    Exception? deleteFailure) : IDataStorageBackend
{
    public DataStorageRecord Storage { get; } = storage;

    public List<string> DeletedKeys { get; } = [];

    public bool Disposed { get; private set; }

    public Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        if (deleteFailure is not null)
        {
            return Task.FromException(deleteFailure);
        }

        DeletedKeys.Add(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DataStorageEntry>> ListAsync(
        CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public void Dispose() => Disposed = true;
}
