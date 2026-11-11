using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>Port of gc.TriggerGcCleanupInput (gc.go:29-35).</summary>
public sealed record GcCleanupRequest
{
    public int RequestsCleanupDays { get; init; }

    public int UsageLogsCleanupDays { get; init; }

    public int RequestBodiesCleanupDays { get; init; }

    public int ResponseBodiesCleanupDays { get; init; }

    public int ResponseChunksCleanupDays { get; init; }
}

/// <summary>Port of gc.GcCleanupPreviewItem (gc.go:37-42).</summary>
public sealed record GcCleanupPreviewItem
{
    public string ResourceType { get; init; } = string.Empty;

    /// <summary>Go <c>int</c>; ent's Count is also an int.</summary>
    public int EstimatedCount { get; init; }

    public DateTime CutoffTime { get; init; }

    public int RetentionDays { get; init; }
}

/// <summary>Port of gc.Config (gc.go:44-48, conf/conf.go:303-305).</summary>
public sealed record GcOptions
{
    public const string DefaultCron = "0 2 * * *";

    public string Cron { get; init; } = DefaultCron;

    public bool VacuumEnabled { get; init; } = true;

    public bool VacuumFull { get; init; }

    /// <summary>
    /// Reads <c>gc.cron</c> / <c>gc.vacuum_enabled</c> / <c>gc.vacuum_full</c>
    /// with the reference's AXONHUB_* environment fallbacks.
    /// </summary>
    public static GcOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var cron = configuration["Gc:Cron"];
        if (string.IsNullOrEmpty(cron))
        {
            cron = configuration["AXONHUB_GC_CRON"];
        }

        return new GcOptions
        {
            Cron = string.IsNullOrEmpty(cron) ? DefaultCron : cron,
            VacuumEnabled = ReadBool(configuration, "Gc:VacuumEnabled", "AXONHUB_GC_VACUUM_ENABLED", true),
            VacuumFull = ReadBool(configuration, "Gc:VacuumFull", "AXONHUB_GC_VACUUM_FULL", false),
        };
    }

    private static bool ReadBool(
        IConfiguration configuration,
        string key,
        string environmentKey,
        bool fallback)
    {
        var value = configuration[key];
        if (string.IsNullOrEmpty(value))
        {
            value = configuration[environmentKey];
        }

        return bool.TryParse(value, out var parsed) ? parsed : fallback;
    }
}

/// <summary>Port of gc.Worker's public cleanup surface (gc.go:692-795).</summary>
public interface IGcCleanupService
{
    /// <summary>
    /// Port of Worker.RunCleanupNow (gc.go:697-717): builds the manual-day
    /// overrides from the positive inputs and always completes successfully —
    /// every cleanup failure is logged inside runCleanup.
    /// </summary>
    Task RunCleanupNowAsync(
        GcCleanupRequest input,
        CancellationToken cancellationToken = default);

    /// <summary>Port of Worker.runAutomaticCleanup (gc_internal.go:8-12).</summary>
    Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of Worker.PreviewCleanup (gc.go:719-795).</summary>
    Task<IReadOnlyList<GcCleanupPreviewItem>> PreviewCleanupAsync(
        GcCleanupRequest input,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of internal/server/gc (gc.go, gc_bodies.go) at f272549f. The reference
/// receives an ent client through the context and calls
/// <c>schematype.SkipSoftDelete</c>; the port talks to <see cref="IGcStore"/>
/// and the data-storage stores directly, and keeps the reference's batch
/// order, retained-trace exclusions, error wrapping and body-payload
/// predicates.
/// </summary>
/// <remarks>
/// The channel-probes pass (gc.go:226-233, 606-640) always runs with the fixed
/// three-day window, independent of the storage policy and the manual-day
/// overrides. The reference's process-wide data-storage caches are absent
/// (C196); a per-pass dictionary reproduces the per-cleanup memoisation.
/// </remarks>
public sealed class GcWorker(
    ISystemSettingsAdminService settings,
    IGcStore gc,
    IDataStorageStore storages,
    IDataStorageBackendFactory dataStorageBackends,
    GcOptions options,
    TimeProvider time,
    ILogger<GcWorker> logger) : IGcCleanupService
{
    /// <summary>gc.defaultBatchSize (gc.go:27).</summary>
    public const int DefaultBatchSize = 500;

    /// <summary>
    /// The fixed retention the reference passes to cleanupChannelProbes
    /// (gc.go:226): not part of the storage policy and not overridable manually.
    /// </summary>
    public const int ChannelProbeCleanupDays = 3;

    private static readonly Action<ILogger, bool, Exception?> LogStartingCleanup =
        LoggerMessage.Define<bool>(
            LogLevel.Information,
            new EventId(0, "GcStartingCleanup"),
            "Starting cleanup process manual={Manual}");

    private static readonly Action<ILogger, string, Exception?> LogResourceFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(0, "GcResourceFailed"),
            "Cleanup failed resource={Resource}");

    private static readonly Action<ILogger, string, Exception?> LogUnknownResource =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(0, "GcUnknownResource"),
            "Unknown resource type for cleanup resource={Resource}");

    private static readonly Action<ILogger, string, int, Exception?> LogResourceSucceeded =
        LoggerMessage.Define<string, int>(
            LogLevel.Information,
            new EventId(0, "GcResourceSucceeded"),
            "Cleanup succeeded resource={Resource} cleanup_days={CleanupDays}");

    private static readonly Action<ILogger, int, int, Exception?> LogDeletedBatch =
        LoggerMessage.Define<int, int>(
            LogLevel.Debug,
            new EventId(0, "GcDeletedBatch"),
            "Deleted batch of records batch_size={BatchSize} total_deleted={TotalDeleted}");

    private static readonly Action<ILogger, string, int, int, DateTime, Exception?> LogStrippedPayloads =
        LoggerMessage.Define<string, int, int, DateTime>(
            LogLevel.Debug,
            new EventId(0, "GcStrippedPayloads"),
            "Stripped stored payloads resource={Resource} requests={Requests} executions={Executions} cutoff_time={CutoffTime}");

    private static readonly Action<ILogger, long, string, Exception?> LogSkipStripRequestPayload =
        LoggerMessage.Define<long, string>(
            LogLevel.Warning,
            new EventId(0, "GcSkipStripRequestPayload"),
            "Skip stripping request payload; external delete failed request_id={RequestId} resource={Resource}");

    private static readonly Action<ILogger, long, string, Exception?> LogSkipStripExecutionPayload =
        LoggerMessage.Define<long, string>(
            LogLevel.Warning,
            new EventId(0, "GcSkipStripExecutionPayload"),
            "Skip stripping execution payload; external delete failed execution_id={ExecutionId} resource={Resource}");

    private static readonly Action<ILogger, long, Exception?> LogLoadStorageForRequestFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "GcLoadStorageForRequestFailed"),
            "Failed to load data storage for request cleanup request_id={RequestId}");

    private static readonly Action<ILogger, long, Exception?> LogLoadStorageForExecutionFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "GcLoadStorageForExecutionFailed"),
            "Failed to load data storage for execution cleanup execution_id={ExecutionId}");

    private static readonly Action<ILogger, long, string, Exception?> LogDeleteRequestDataFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Warning,
            new EventId(0, "GcDeleteRequestDataFailed"),
            "Failed to delete request external data request_id={RequestId} key={Key}");

    private static readonly Action<ILogger, long, string, Exception?> LogDeleteExecutionDataFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Warning,
            new EventId(0, "GcDeleteExecutionDataFailed"),
            "Failed to delete execution external data execution_id={ExecutionId} key={Key}");

    private static readonly Action<ILogger, Exception?> LogChannelProbesFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "GcChannelProbesFailed"),
            "Failed to cleanup channel probes");

    private static readonly Action<ILogger, int, Exception?> LogChannelProbesSucceeded =
        LoggerMessage.Define<int>(
            LogLevel.Information,
            new EventId(0, "GcChannelProbesSucceeded"),
            "Successfully cleaned up channel probes cleanup_days={CleanupDays}");

    private static readonly Action<ILogger, int, DateTime, Exception?> LogCleanedChannelProbes =
        LoggerMessage.Define<int, DateTime>(
            LogLevel.Debug,
            new EventId(0, "GcCleanedChannelProbes"),
            "Cleaned up channel probes deleted_count={DeletedCount} cutoff_time={CutoffTime}");

    private static readonly Action<ILogger, Exception?> LogNoChannelProbeCleanup =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(0, "GcNoChannelProbeCleanup"),
            "No cleanup needed for channel probes");

    private static readonly Action<ILogger, string, bool, Exception?> LogVacuumStarting =
        LoggerMessage.Define<string, bool>(
            LogLevel.Information,
            new EventId(0, "GcVacuumStarting"),
            "Starting database VACUUM operation command={Command} vacuum_full={VacuumFull}");

    private static readonly Action<ILogger, string, long, Exception?> LogVacuumCompleted =
        LoggerMessage.Define<string, long>(
            LogLevel.Information,
            new EventId(0, "GcVacuumCompleted"),
            "Database VACUUM completed successfully command={Command} duration_ms={DurationMs}");

    private static readonly Action<ILogger, Exception?> LogVacuumFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "GcVacuumFailed"),
            "Failed to run VACUUM after cleanup");

    private static readonly Action<ILogger, Exception?> LogCleanupCompleted =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(0, "GcCleanupCompleted"),
            "Cleanup process completed");

    private static readonly Action<ILogger, Exception?> LogStoragePolicyFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "GcStoragePolicyFailed"),
            "Failed to get storage policy for cleanup");

    public async Task RunCleanupNowAsync(
        GcCleanupRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var manualDays = new Dictionary<string, int>(StringComparer.Ordinal);
        if (input.RequestsCleanupDays > 0)
        {
            manualDays[CleanupResourceTypes.Requests] = input.RequestsCleanupDays;
        }

        if (input.UsageLogsCleanupDays > 0)
        {
            manualDays[CleanupResourceTypes.UsageLogs] = input.UsageLogsCleanupDays;
        }

        if (input.RequestBodiesCleanupDays > 0)
        {
            manualDays[CleanupResourceTypes.RequestBodies] = input.RequestBodiesCleanupDays;
        }

        if (input.ResponseBodiesCleanupDays > 0)
        {
            manualDays[CleanupResourceTypes.ResponseBodies] = input.ResponseBodiesCleanupDays;
        }

        if (input.ResponseChunksCleanupDays > 0)
        {
            manualDays[CleanupResourceTypes.ResponseChunks] = input.ResponseChunksCleanupDays;
        }

        await RunCleanupAsync(manual: true, manualDays, cancellationToken).ConfigureAwait(false);
    }

    public Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default)
        => RunCleanupAsync(manual: false, manualDays: null, cancellationToken);

    /// <summary>Port of Worker.runCleanup (gc.go:112-243).</summary>
    private async Task RunCleanupAsync(
        bool manual,
        IReadOnlyDictionary<string, int>? manualDays,
        CancellationToken cancellationToken)
    {
        LogStartingCleanup(logger, manual, null);

        StoragePolicy policy;
        try
        {
            policy = await settings.GetStoragePolicyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStoragePolicyFailed(logger, ex);
            return;
        }

        var cleanupOptions = policy.CleanupOptions ?? Array.Empty<CleanupOption>();
        foreach (var option in cleanupOptions)
        {
            if (!option.Enabled && !manual)
            {
                continue;
            }

            if (manual && manualDays is not null
                && !manualDays.ContainsKey(option.ResourceType))
            {
                continue;
            }

            var days = option.CleanupDays;
            if (manual && manualDays is not null
                && manualDays.TryGetValue(option.ResourceType, out var overrideDays))
            {
                days = overrideDays;
            }

            switch (option.ResourceType)
            {
                case CleanupResourceTypes.RequestBodies:
                    await RunResourceAsync(
                            option.ResourceType,
                            days,
                            () => CleanupBodyPayloadsAsync(
                                GcPayloadKind.RequestBodies, days, cancellationToken))
                        .ConfigureAwait(false);
                    break;
                case CleanupResourceTypes.ResponseBodies:
                    await RunResourceAsync(
                            option.ResourceType,
                            days,
                            () => CleanupBodyPayloadsAsync(
                                GcPayloadKind.ResponseBodies, days, cancellationToken))
                        .ConfigureAwait(false);
                    break;
                case CleanupResourceTypes.ResponseChunks:
                    await RunResourceAsync(
                            option.ResourceType,
                            days,
                            () => CleanupBodyPayloadsAsync(
                                GcPayloadKind.ResponseChunks, days, cancellationToken))
                        .ConfigureAwait(false);
                    break;
                case CleanupResourceTypes.Requests:
                    await RunResourceAsync(
                            option.ResourceType,
                            days,
                            () => CleanupRequestsAsync(days, cancellationToken))
                        .ConfigureAwait(false);
                    await RunResourceAsync(
                            "threads",
                            days,
                            () => CleanupThreadsAsync(days, cancellationToken))
                        .ConfigureAwait(false);
                    await RunResourceAsync(
                            "traces",
                            days,
                            () => CleanupTracesAsync(days, cancellationToken))
                        .ConfigureAwait(false);
                    break;
                case CleanupResourceTypes.UsageLogs:
                    await RunResourceAsync(
                            option.ResourceType,
                            days,
                            () => CleanupUsageLogsAsync(days, cancellationToken))
                        .ConfigureAwait(false);
                    break;
                default:
                    LogUnknownResource(logger, option.ResourceType, null);
                    break;
            }
        }

        // gc.go:226-233: the channel-probes pass always runs (fixed 3 days),
        // independent of the policy and of the manual-day map. A failure is
        // logged and the pass continues to VACUUM.
        try
        {
            await CleanupChannelProbesAsync(ChannelProbeCleanupDays, cancellationToken)
                .ConfigureAwait(false);
            LogChannelProbesSucceeded(logger, ChannelProbeCleanupDays, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogChannelProbesFailed(logger, ex);
        }

        if (options.VacuumEnabled)
        {
            try
            {
                await RunVacuumAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogVacuumFailed(logger, ex);
            }
        }

        LogCleanupCompleted(logger, null);
    }

    private async Task RunResourceAsync(
        string resource,
        int days,
        Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            LogResourceSucceeded(logger, resource, days, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogResourceFailed(logger, resource, ex);
        }
    }

    /// <summary>Port of Worker.cleanupRequests (gc.go:245-274).</summary>
    private async Task CleanupRequestsAsync(int cleanupDays, CancellationToken cancellationToken)
    {
        if (cleanupDays <= 0)
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-cleanupDays);

        try
        {
            await CleanupOldRequestExecutionsAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to cleanup request executions: " + ex.Message,
                ex);
        }

        try
        {
            await CleanupOldRequestsRecordsAsync(cutoff, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to cleanup requests: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of Worker.cleanupOldRequestExecutions (gc.go:276-328).</summary>
    private async Task<int> CleanupOldRequestExecutionsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var totalDeleted = 0;
        var cache = new Dictionary<long, DataStorageRecord?>();

        while (true)
        {
            IReadOnlyList<GcExecutionRef> executions;
            try
            {
                executions = await gc.ListOldExecutionsAsync(
                        cutoff,
                        DefaultBatchSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to query old request executions: " + ex.Message,
                    ex);
            }

            if (executions.Count == 0)
            {
                break;
            }

            var ids = new List<long>(executions.Count);
            foreach (var execution in executions)
            {
                ids.Add(execution.Id);
                await CleanupExecutionExternalStorageAsync(execution, cache, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await gc.DeleteExecutionsAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to delete request executions batch: " + ex.Message,
                    ex);
            }

            totalDeleted += ids.Count;
            LogDeletedBatch(logger, ids.Count, totalDeleted, null);
        }

        return totalDeleted;
    }

    /// <summary>Port of Worker.cleanupOldRequestsRecords (gc.go:330-373).</summary>
    private async Task<int> CleanupOldRequestsRecordsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken)
    {
        var totalDeleted = 0;
        var cache = new Dictionary<long, DataStorageRecord?>();

        while (true)
        {
            IReadOnlyList<GcRequestRef> requests;
            try
            {
                requests = await gc.ListOldRequestsAsync(
                        cutoff,
                        DefaultBatchSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to query old requests: " + ex.Message,
                    ex);
            }

            if (requests.Count == 0)
            {
                break;
            }

            var ids = new List<long>(requests.Count);
            foreach (var request in requests)
            {
                ids.Add(request.Id);
                await CleanupRequestExternalStorageAsync(request, cache, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                await gc.DeleteRequestsAsync(ids, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to delete requests batch: " + ex.Message,
                    ex);
            }

            totalDeleted += ids.Count;
        }

        return totalDeleted;
    }

    /// <summary>Port of Worker.cleanupExecutionExternalStorage (gc.go:375-416).</summary>
    private async Task CleanupExecutionExternalStorageAsync(
        GcExecutionRef execution,
        Dictionary<long, DataStorageRecord?> cache,
        CancellationToken cancellationToken)
    {
        if (execution.DataStorageId is null or 0)
        {
            return;
        }

        DataStorageRecord? storage;
        try
        {
            storage = await GetDataStorageCachedAsync(
                    execution.DataStorageId.Value,
                    cache,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLoadStorageForExecutionFailed(logger, execution.Id, ex);
            return;
        }

        if (storage is null || storage.Primary)
        {
            return;
        }

        var keys = new List<string>
        {
            RequestDataKeys.ExecutionRequestBody(
                execution.ProjectId, execution.RequestId, execution.Id),
            RequestDataKeys.ExecutionResponseBody(
                execution.ProjectId, execution.RequestId, execution.Id),
            RequestDataKeys.ExecutionResponseChunks(
                execution.ProjectId, execution.RequestId, execution.Id),
        };

        // Object stores have no real directories, so the directory marker only
        // exists on filesystem-like backends (gc.go:400-405).
        if (HasRealDirectories(storage.Type))
        {
            keys.Add(RequestDataKeys.ExecutionRequestDir(
                execution.ProjectId, execution.RequestId, execution.Id));
        }

        foreach (var key in keys)
        {
            try
            {
                await DeleteDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDeleteExecutionDataFailed(logger, execution.Id, key, ex);
            }
        }
    }

    /// <summary>Port of Worker.cleanupRequestExternalStorage (gc.go:418-461).</summary>
    private async Task CleanupRequestExternalStorageAsync(
        GcRequestRef request,
        Dictionary<long, DataStorageRecord?> cache,
        CancellationToken cancellationToken)
    {
        if (request.DataStorageId is null or 0)
        {
            return;
        }

        DataStorageRecord? storage;
        try
        {
            storage = await GetDataStorageCachedAsync(
                    request.DataStorageId.Value,
                    cache,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogLoadStorageForRequestFailed(logger, request.Id, ex);
            return;
        }

        if (storage is null || storage.Primary)
        {
            return;
        }

        var keys = new List<string>
        {
            RequestDataKeys.RequestBody(request.ProjectId, request.Id),
            RequestDataKeys.ResponseBody(request.ProjectId, request.Id),
            RequestDataKeys.ResponseChunks(request.ProjectId, request.Id),
        };

        if (HasRealDirectories(storage.Type))
        {
            keys.Add(RequestDataKeys.RequestExecutionsDir(request.ProjectId, request.Id));
            keys.Add(RequestDataKeys.RequestDir(request.ProjectId, request.Id));
        }

        foreach (var key in keys)
        {
            try
            {
                await DeleteDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogDeleteRequestDataFailed(logger, request.Id, key, ex);
            }
        }
    }

    /// <summary>gc.hasRealDirectories (gc.go:463-471).</summary>
    private static bool HasRealDirectories(string storageType)
        => storageType is "fs" or "webdav";

    /// <summary>
    /// gc.getDataStorageCached (gc.go:473-486) over
    /// <c>DataStorageService.GetDataStorageByID</c>: a missing row is ent's
    /// NotFound error, which the callers treat as a failure (skip the strip,
    /// or log and move on while deleting rows).
    /// </summary>
    private async Task<DataStorageRecord?> GetDataStorageCachedAsync(
        long id,
        Dictionary<long, DataStorageRecord?> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        DataStorageRecord? storage;
        try
        {
            storage = await storages.GetByIdIncludingDeletedAsync(id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get data storage by ID "
                + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ": " + ex.Message,
                ex);
        }

        if (storage is null)
        {
            throw new InvalidOperationException(
                "failed to get data storage by ID "
                + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ": ent: data storage not found");
        }

        cache[id] = storage;
        return storage;
    }

    /// <summary>Port of DataStorageService.DeleteData (data_storage.go:678-718).</summary>
    private Task DeleteDataAsync(
        DataStorageRecord storage,
        string key,
        CancellationToken cancellationToken)
    {
        if (storage.Type == "database")
        {
            return Task.CompletedTask;
        }

        var backend = CreateBackendForDelete(storage);
        return DeleteCoreAsync(backend, key, cancellationToken);

        static async Task DeleteCoreAsync(
            IDataStorageBackend backend,
            string key,
            CancellationToken cancellationToken)
        {
            using (backend)
            {
                await backend.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// DeleteData probes the native object store first: S3 construction failures
    /// are <c>failed to create s3 object store: %w</c> and settings errors are
    /// bare, while fs/gcs/webdav go through GetFileSystem, which wraps
    /// buildFileSystem errors with <c>failed to get file system: %w</c>
    /// (data_storage.go:687-696, objectstore.go:59-85). Unknown types and
    /// database are handled by the caller/factory.
    /// </summary>
    private IDataStorageBackend CreateBackendForDelete(DataStorageRecord storage)
    {
        const string S3FileSystemPrefix = "failed to create s3 filesystem: ";

        try
        {
            return dataStorageBackends.Create(storage);
        }
        catch (InvalidOperationException ex)
        {
            var message = ex.Message;

            if (storage.Type == "s3")
            {
                if (message.StartsWith(S3FileSystemPrefix, StringComparison.Ordinal))
                {
                    message = "failed to create s3 object store: "
                        + message[S3FileSystemPrefix.Length..];
                }
            }
            else if (storage.Type is "fs" or "gcs" or "webdav")
            {
                message = "failed to get file system: " + message;
            }

            throw new InvalidOperationException(message, ex);
        }
    }

    /// <summary>Port of Worker.cleanupUsageLogs (gc.go:488-526).</summary>
    private async Task CleanupUsageLogsAsync(int cleanupDays, CancellationToken cancellationToken)
    {
        if (cleanupDays <= 0)
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-cleanupDays);

        try
        {
            await DeleteInBatchesAsync(
                    async () =>
                    {
                        IReadOnlyList<long> ids;
                        try
                        {
                            ids = await gc.ListOldUsageLogIdsAsync(
                                    cutoff,
                                    DefaultBatchSize,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "failed to query old usage logs: " + ex.Message,
                                ex);
                        }

                        if (ids.Count == 0)
                        {
                            return 0;
                        }

                        return await gc.DeleteUsageLogsAsync(ids, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to delete old usage logs: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of Worker.cleanupThreads (gc.go:528-565).</summary>
    private async Task CleanupThreadsAsync(int cleanupDays, CancellationToken cancellationToken)
    {
        if (cleanupDays <= 0)
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-cleanupDays);

        try
        {
            await DeleteInBatchesAsync(
                    async () =>
                    {
                        IReadOnlyList<long> ids;
                        try
                        {
                            ids = await gc.ListOldThreadIdsAsync(
                                    cutoff,
                                    DefaultBatchSize,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "failed to query old threads: " + ex.Message,
                                ex);
                        }

                        if (ids.Count == 0)
                        {
                            return 0;
                        }

                        return await gc.DeleteThreadsAsync(ids, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to delete old threads: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of Worker.cleanupTraces (gc.go:567-604).</summary>
    private async Task CleanupTracesAsync(int cleanupDays, CancellationToken cancellationToken)
    {
        if (cleanupDays <= 0)
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-cleanupDays);

        try
        {
            await DeleteInBatchesAsync(
                    async () =>
                    {
                        IReadOnlyList<long> ids;
                        try
                        {
                            ids = await gc.ListOldTraceIdsAsync(
                                    cutoff,
                                    DefaultBatchSize,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "failed to query old traces: " + ex.Message,
                                ex);
                        }

                        if (ids.Count == 0)
                        {
                            return 0;
                        }

                        return await gc.DeleteTracesAsync(ids, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to delete old traces: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of Worker.cleanupChannelProbes (gc.go:606-640).</summary>
    private async Task CleanupChannelProbesAsync(
        int cleanupDays,
        CancellationToken cancellationToken)
    {
        if (cleanupDays <= 0)
        {
            LogNoChannelProbeCleanup(logger, null);
            return;
        }

        var cutoff = time.GetUtcNow().AddDays(-cleanupDays);
        var cutoffUnix = cutoff.ToUnixTimeSeconds();

        int deleted;
        try
        {
            deleted = await DeleteInBatchesAsync(
                    async () =>
                    {
                        IReadOnlyList<long> ids;
                        try
                        {
                            ids = await gc.ListOldChannelProbeIdsAsync(
                                    cutoffUnix,
                                    DefaultBatchSize,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            throw new InvalidOperationException(
                                "failed to query old channel probes: " + ex.Message,
                                ex);
                        }

                        if (ids.Count == 0)
                        {
                            return 0;
                        }

                        return await gc.DeleteChannelProbesAsync(ids, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to delete old channel probes: " + ex.Message,
                ex);
        }

        LogCleanedChannelProbes(logger, deleted, cutoff.UtcDateTime, null);
    }

    /// <summary>gc.deleteInBatches (gc.go:86-105).</summary>
    private async Task<int> DeleteInBatchesAsync(
        Func<Task<int>> deleteFunc,
        CancellationToken cancellationToken)
    {
        var totalDeleted = 0;

        while (true)
        {
            int deleted;
            try
            {
                deleted = await deleteFunc().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to delete batch: " + ex.Message,
                    ex);
            }

            if (deleted == 0)
            {
                break;
            }

            totalDeleted += deleted;
            LogDeletedBatch(logger, deleted, totalDeleted, null);
        }

        return totalDeleted;
    }

    /// <summary>Port of Worker.cleanupBodyPayloads (gc_bodies.go:46-69).</summary>
    private async Task CleanupBodyPayloadsAsync(
        GcPayloadKind kind,
        int cleanupDays,
        CancellationToken cancellationToken)
    {
        if (cleanupDays <= 0)
        {
            return;
        }

        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-cleanupDays);
        var primaryId = await PrimaryStorageIdAsync(cancellationToken).ConfigureAwait(false);

        var (requestCount, requestError) = await StripRequestPayloadsAsync(
                kind, cutoff, primaryId, cancellationToken)
            .ConfigureAwait(false);
        var (executionCount, executionError) = await StripExecutionPayloadsAsync(
                kind, cutoff, primaryId, cancellationToken)
            .ConfigureAwait(false);

        LogStrippedPayloads(
            logger,
            ResourceTypeOf(kind),
            requestCount,
            executionCount,
            cutoff,
            null);

        // errors.Join(reqErr, execErr): both passes always run, and both
        // messages survive the join.
        if (requestError is not null && executionError is not null)
        {
            throw new InvalidOperationException(
                requestError.Message + "\n" + executionError.Message,
                requestError);
        }

        if (requestError is not null)
        {
            throw requestError;
        }

        if (executionError is not null)
        {
            throw executionError;
        }
    }

    /// <summary>gc_bodies.go:111-180.</summary>
    private async Task<(int Total, Exception? Error)> StripRequestPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryId,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var failed = 0;
        var skipIds = new List<long>();
        var cache = new Dictionary<long, DataStorageRecord?>();

        while (true)
        {
            IReadOnlyList<GcRequestRef> requests;
            try
            {
                requests = await gc.ListRequestPayloadsAsync(
                        kind,
                        cutoff,
                        primaryId,
                        skipIds,
                        DefaultBatchSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (total, new InvalidOperationException(
                    "failed to query requests for " + ResourceTypeOf(kind) + " cleanup: " + ex.Message,
                    ex));
            }

            if (requests.Count == 0)
            {
                if (failed > 0)
                {
                    return (total, new InvalidOperationException(
                        "external payload delete failed for " + failed.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " request " + ResourceTypeOf(kind) + " rows"));
                }

                return (total, null);
            }

            var ids = new List<long>(requests.Count);
            foreach (var request in requests)
            {
                try
                {
                    await DeleteRequestExternalPayloadAsync(
                            request, kind, cache, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Never mark the row stripped while its external object is
                    // still there: Load* would keep reading the object and the
                    // next GC pass would no longer select the row.
                    LogSkipStripRequestPayload(
                        logger, request.Id, ResourceTypeOf(kind), ex);
                    skipIds.Add(request.Id);
                    failed++;
                    continue;
                }

                ids.Add(request.Id);
            }

            if (ids.Count == 0)
            {
                continue;
            }

            try
            {
                await gc.ClearRequestPayloadsAsync(kind, ids, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (total, new InvalidOperationException(
                    "failed to clear request " + ResourceTypeOf(kind) + ": " + ex.Message,
                    ex));
            }

            total += ids.Count;
        }
    }

    /// <summary>gc_bodies.go:182-253.</summary>
    private async Task<(int Total, Exception? Error)> StripExecutionPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryId,
        CancellationToken cancellationToken)
    {
        var total = 0;
        var failed = 0;
        var skipIds = new List<long>();
        var cache = new Dictionary<long, DataStorageRecord?>();

        while (true)
        {
            IReadOnlyList<GcExecutionRef> executions;
            try
            {
                executions = await gc.ListExecutionPayloadsAsync(
                        kind,
                        cutoff,
                        primaryId,
                        skipIds,
                        DefaultBatchSize,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (total, new InvalidOperationException(
                    "failed to query executions for " + ResourceTypeOf(kind) + " cleanup: " + ex.Message,
                    ex));
            }

            if (executions.Count == 0)
            {
                if (failed > 0)
                {
                    return (total, new InvalidOperationException(
                        "external payload delete failed for " + failed.ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        + " execution " + ResourceTypeOf(kind) + " rows"));
                }

                return (total, null);
            }

            var ids = new List<long>(executions.Count);
            foreach (var execution in executions)
            {
                try
                {
                    await DeleteExecutionExternalPayloadAsync(
                            execution, kind, cache, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogSkipStripExecutionPayload(
                        logger, execution.Id, ResourceTypeOf(kind), ex);
                    skipIds.Add(execution.Id);
                    failed++;
                    continue;
                }

                ids.Add(execution.Id);
            }

            if (ids.Count == 0)
            {
                continue;
            }

            try
            {
                await gc.ClearExecutionPayloadsAsync(kind, ids, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (total, new InvalidOperationException(
                    "failed to clear execution " + ResourceTypeOf(kind) + ": " + ex.Message,
                    ex));
            }

            total += ids.Count;
        }
    }

    /// <summary>gc_bodies.go:299-331.</summary>
    private async Task DeleteRequestExternalPayloadAsync(
        GcRequestRef request,
        GcPayloadKind kind,
        Dictionary<long, DataStorageRecord?> cache,
        CancellationToken cancellationToken)
    {
        if (request.DataStorageId is null or 0)
        {
            return;
        }

        DataStorageRecord? storage;
        try
        {
            storage = await GetDataStorageCachedAsync(
                    request.DataStorageId.Value,
                    cache,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "load data storage for request " + request.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + ": " + ex.Message,
                ex);
        }

        if (storage is null || storage.Primary)
        {
            return;
        }

        var key = kind switch
        {
            GcPayloadKind.RequestBodies => RequestDataKeys.RequestBody(
                request.ProjectId, request.Id),
            GcPayloadKind.ResponseBodies => RequestDataKeys.ResponseBody(
                request.ProjectId, request.Id),
            GcPayloadKind.ResponseChunks => RequestDataKeys.ResponseChunks(
                request.ProjectId, request.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        try
        {
            await DeleteDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "delete request " + request.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + " key " + key + ": " + ex.Message,
                ex);
        }
    }

    /// <summary>gc_bodies.go:333-365.</summary>
    private async Task DeleteExecutionExternalPayloadAsync(
        GcExecutionRef execution,
        GcPayloadKind kind,
        Dictionary<long, DataStorageRecord?> cache,
        CancellationToken cancellationToken)
    {
        if (execution.DataStorageId is null or 0)
        {
            return;
        }

        DataStorageRecord? storage;
        try
        {
            storage = await GetDataStorageCachedAsync(
                    execution.DataStorageId.Value,
                    cache,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "load data storage for execution " + execution.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + ": " + ex.Message,
                ex);
        }

        if (storage is null || storage.Primary)
        {
            return;
        }

        var key = kind switch
        {
            GcPayloadKind.RequestBodies => RequestDataKeys.ExecutionRequestBody(
                execution.ProjectId, execution.RequestId, execution.Id),
            GcPayloadKind.ResponseBodies => RequestDataKeys.ExecutionResponseBody(
                execution.ProjectId, execution.RequestId, execution.Id),
            GcPayloadKind.ResponseChunks => RequestDataKeys.ExecutionResponseChunks(
                execution.ProjectId, execution.RequestId, execution.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        try
        {
            await DeleteDataAsync(storage, key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "delete execution " + execution.Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                + " key " + key + ": " + ex.Message,
                ex);
        }
    }

    /// <summary>gc_bodies.go:367-382.</summary>
    private async Task<long> PrimaryStorageIdAsync(CancellationToken cancellationToken)
    {
        DataStorageRecord? storage;
        try
        {
            // gc_bodies.go:367-382 runs under the worker's SkipSoftDelete context.
            storage = await storages.GetPrimaryIncludingDeletedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "get primary data storage for body cleanup: failed to get primary data storage: "
                + ex.Message,
                ex);
        }

        if (storage is null)
        {
            // ent's First() reports NotFound as an error, so a database with no
            // primary row aborts body cleanup in the reference too.
            throw new InvalidOperationException(
                "get primary data storage for body cleanup: failed to get primary data storage: "
                + "ent: data storage not found");
        }

        return storage.Id;
    }

    /// <summary>gc.runVacuum (gc.go:642-690).</summary>
    private async Task RunVacuumAsync(CancellationToken cancellationToken)
    {
        if (!options.VacuumEnabled)
        {
            return;
        }

        // The reference only VACUUMs SQLite/PostgreSQL; the port is PostgreSQL.
        // VACUUM FULL is PostgreSQL-only (the SQLite branch always uses VACUUM).
        var command = options.VacuumFull ? "VACUUM FULL" : "VACUUM";

        LogVacuumStarting(logger, command, options.VacuumFull, null);

        var started = time.GetTimestamp();
        try
        {
            await gc.VacuumAsync(options.VacuumFull, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to execute " + command + ": " + ex.Message,
                ex);
        }

        var elapsed = time.GetElapsedTime(started);
        LogVacuumCompleted(logger, command, (long)elapsed.TotalMilliseconds, null);
    }

    /// <summary>Port of Worker.PreviewCleanup (gc.go:719-795).</summary>
    public async Task<IReadOnlyList<GcCleanupPreviewItem>> PreviewCleanupAsync(
        GcCleanupRequest input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var items = new List<GcCleanupPreviewItem>();
        var now = time.GetUtcNow().UtcDateTime;

        if (input.RequestsCleanupDays > 0)
        {
            var cutoff = now.AddDays(-input.RequestsCleanupDays);

            long count;
            try
            {
                count = await gc.CountOldRequestsAsync(cutoff, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to count requests for preview: " + ex.Message,
                    ex);
            }

            items.Add(new GcCleanupPreviewItem
            {
                ResourceType = CleanupResourceTypes.Requests,
                EstimatedCount = (int)count,
                CutoffTime = cutoff,
                RetentionDays = input.RequestsCleanupDays,
            });
        }

        if (input.UsageLogsCleanupDays > 0)
        {
            var cutoff = now.AddDays(-input.UsageLogsCleanupDays);

            long count;
            try
            {
                count = await gc.CountOldUsageLogsAsync(cutoff, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to count usage logs for preview: " + ex.Message,
                    ex);
            }

            items.Add(new GcCleanupPreviewItem
            {
                ResourceType = CleanupResourceTypes.UsageLogs,
                EstimatedCount = (int)count,
                CutoffTime = cutoff,
                RetentionDays = input.UsageLogsCleanupDays,
            });
        }

        var bodySpecs = new (string ResourceType, int Days)[]
        {
            (CleanupResourceTypes.RequestBodies, input.RequestBodiesCleanupDays),
            (CleanupResourceTypes.ResponseBodies, input.ResponseBodiesCleanupDays),
            (CleanupResourceTypes.ResponseChunks, input.ResponseChunksCleanupDays),
        };
        var bodyWindows = new Dictionary<int, GcCleanupPreviewItem>();

        foreach (var spec in bodySpecs)
        {
            if (spec.Days <= 0)
            {
                continue;
            }

            if (!bodyWindows.TryGetValue(spec.Days, out var item))
            {
                item = await PreviewBodyCleanupAsync(
                        spec.ResourceType, spec.Days, now, cancellationToken)
                    .ConfigureAwait(false);
                bodyWindows[spec.Days] = item;
            }

            items.Add(item with { ResourceType = spec.ResourceType });
        }

        return items;
    }

    /// <summary>gc_bodies.go:71-109.</summary>
    private async Task<GcCleanupPreviewItem> PreviewBodyCleanupAsync(
        string resourceType,
        int days,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-days);

        // The reference deliberately counts rows in the window without
        // inspecting the JSON payload columns; the estimate is an upper bound.
        long requestCount;
        try
        {
            requestCount = await gc.CountOldRequestsAsync(cutoff, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to count " + resourceType + " on requests: " + ex.Message,
                ex);
        }

        long executionCount;
        try
        {
            executionCount = await gc.CountOldExecutionsAsync(cutoff, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to count " + resourceType + " on executions: " + ex.Message,
                ex);
        }

        return new GcCleanupPreviewItem
        {
            ResourceType = resourceType,
            EstimatedCount = (int)(requestCount + executionCount),
            CutoffTime = cutoff,
            RetentionDays = days,
        };
    }

    private static string ResourceTypeOf(GcPayloadKind kind) => kind switch
    {
        GcPayloadKind.RequestBodies => CleanupResourceTypes.RequestBodies,
        GcPayloadKind.ResponseBodies => CleanupResourceTypes.ResponseBodies,
        GcPayloadKind.ResponseChunks => CleanupResourceTypes.ResponseChunks,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
