using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks Worker.runCleanup / cleanupBodyPayloads (gc.go:112-243,
/// gc_bodies.go:46-109): policy filtering, manual-day overrides, per-resource
/// error isolation, retained-batch order, the external-delete skip rule and the
/// preview window caching.
/// </summary>
public sealed class GcWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AutomaticCleanupRunsOnlyEnabledResources()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.Requests, false, 3),
            new CleanupOption(CleanupResourceTypes.UsageLogs, true, 30)));

        await harness.Worker.RunAutomaticCleanupAsync();

        Assert.Empty(harness.Gc.OldRequestCutoffs);
        Assert.Empty(harness.Gc.ThreadCutoffs);
        Assert.Empty(harness.Gc.TraceCutoffs);
        Assert.Equal(
            Now.UtcDateTime.AddDays(-30),
            Assert.Single(harness.Gc.UsageLogCutoffs));
        Assert.Empty(harness.Gc.VacuumCalls);
    }

    [Fact]
    public async Task ManualCleanupFiltersResourcesAndOverridesRetentionDays()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.Requests, false, 3),
            new CleanupOption(CleanupResourceTypes.UsageLogs, false, 30),
            new CleanupOption(CleanupResourceTypes.RequestBodies, false, 7),
            new CleanupOption(CleanupResourceTypes.ResponseBodies, false, 7),
            new CleanupOption(CleanupResourceTypes.ResponseChunks, false, 3)));
        harness.Storages.Primary = Storage(1, "database", primary: true);

        await harness.Worker.RunCleanupNowAsync(new GcCleanupRequest
        {
            RequestsCleanupDays = 4,
            RequestBodiesCleanupDays = 6,
        });

        // The requests resource also drives threads and traces, with the manual
        // override applied to all three.
        Assert.Equal(
            Now.UtcDateTime.AddDays(-4),
            Assert.Single(harness.Gc.OldRequestCutoffs));
        Assert.Equal(
            Now.UtcDateTime.AddDays(-4),
            Assert.Single(harness.Gc.ThreadCutoffs));
        Assert.Equal(
            Now.UtcDateTime.AddDays(-4),
            Assert.Single(harness.Gc.TraceCutoffs));

        // Resources absent from the manual map are skipped even though the
        // policy lists them. Usage logs are never queried; the request-body
        // pass still probes the execution side once.
        Assert.Empty(harness.Gc.UsageLogCutoffs);

        var requestCall = Assert.Single(harness.Gc.RequestPayloadCalls);
        Assert.Equal(GcPayloadKind.RequestBodies, requestCall.Kind);
        Assert.Equal(Now.UtcDateTime.AddDays(-6), requestCall.Cutoff);
        Assert.Equal(1L, requestCall.PrimaryStorageId);
        Assert.Empty(requestCall.SkipIds);

        var executionCall = Assert.Single(harness.Gc.ExecutionPayloadCalls);
        Assert.Equal(GcPayloadKind.RequestBodies, executionCall.Kind);
        Assert.Equal(Now.UtcDateTime.AddDays(-6), executionCall.Cutoff);
        Assert.Equal(1L, executionCall.PrimaryStorageId);
    }

    [Fact]
    public async Task ZeroManualDaysSkipEveryPolicyResource()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.Requests, true, 3),
            new CleanupOption(CleanupResourceTypes.RequestBodies, true, 7)));

        await harness.Worker.RunCleanupNowAsync(new GcCleanupRequest());

        Assert.Empty(harness.Gc.OldExecutionCutoffs);
        Assert.Empty(harness.Gc.OldRequestCutoffs);
        Assert.Empty(harness.Gc.RequestPayloadCalls);
        Assert.Empty(harness.Gc.ExecutionPayloadCalls);
    }

    [Fact]
    public async Task StoragePolicyFailureAbortsWithoutTouchingTheStore()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.Requests, true, 3)));
        harness.Settings.Failure = new InvalidOperationException("policy is corrupt");

        await harness.Worker.RunCleanupNowAsync(new GcCleanupRequest
        {
            RequestsCleanupDays = 1,
            RequestBodiesCleanupDays = 1,
        });

        Assert.Empty(harness.Gc.OldRequestCutoffs);
        Assert.Empty(harness.Gc.RequestPayloadCalls);
        Assert.Empty(harness.Gc.VacuumCalls);
    }

    [Fact]
    public async Task BodyCleanupAbortsWhenNoPrimaryStorageExists()
    {
        // The reference's GetPrimaryDataStorage surfaces ent NotFound, so the
        // whole body pass aborts before querying payload rows.
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.RequestBodies, true, 7)));
        harness.Storages.Primary = null;

        await harness.Worker.RunAutomaticCleanupAsync();

        Assert.Empty(harness.Gc.RequestPayloadCalls);
        Assert.Empty(harness.Gc.ExecutionPayloadCalls);
    }

    [Fact]
    public async Task ExternalDeleteFailureSkipsThatRowAndKeepsItSelectable()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.RequestBodies, true, 5)));
        harness.Storages.Primary = Storage(1, "database", primary: true);
        harness.Storages.Storages[2] = Storage(2, "s3");
        harness.Backends.DeleteFailure = storage => storage.Id == 2
            ? new InvalidOperationException("s3 down")
            : null;

        var batches = new Queue<IReadOnlyList<GcRequestRef>>();
        batches.Enqueue(
        [
            new GcRequestRef { Id = 10, ProjectId = 7, DataStorageId = 2 },
            new GcRequestRef { Id = 11, ProjectId = 7, DataStorageId = null },
        ]);
        harness.Gc.RequestPayloadBatches[GcPayloadKind.RequestBodies] = batches;

        await harness.Worker.RunAutomaticCleanupAsync();

        Assert.Equal(2, harness.Gc.RequestPayloadCalls.Count);
        Assert.Empty(harness.Gc.RequestPayloadCalls[0].SkipIds);
        Assert.Equal([10L], harness.Gc.RequestPayloadCalls[1].SkipIds);

        // Only the row whose external object is gone gets its DB columns
        // cleared; the failed row stays matching for the next GC run.
        var cleared = Assert.Single(harness.Gc.ClearedRequests);
        Assert.Equal(GcPayloadKind.RequestBodies, cleared.Kind);
        Assert.Equal([11L], cleared.Ids);
    }

    [Fact]
    public async Task RequestDeletionRemovesExternalObjectsInReferenceKeyOrder()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.Requests, true, 1)));
        harness.Storages.Primary = Storage(1, "database", primary: true);
        harness.Storages.Storages[2] = Storage(2, "fs");
        harness.Storages.Storages[4] = Storage(4, "s3");

        harness.Gc.OldExecutionBatches.Enqueue(
        [
            new GcExecutionRef
            {
                Id = 30,
                RequestId = 20,
                ProjectId = 7,
                DataStorageId = 2,
            },
        ]);
        harness.Gc.OldRequestBatches.Enqueue(
        [
            new GcRequestRef { Id = 20, ProjectId = 7, DataStorageId = 2 },
            new GcRequestRef { Id = 21, ProjectId = 7, DataStorageId = 4 },
            new GcRequestRef { Id = 22, ProjectId = 7, DataStorageId = 1 },
        ]);

        await harness.Worker.RunAutomaticCleanupAsync();

        var fsKeys = harness.Backends.Created
            .Where(static backend => backend.Storage.Id == 2)
            .SelectMany(static backend => backend.DeletedKeys)
            .ToArray();
        Assert.Equal(
            [
                "/7/requests/20/executions/30/request_body.json",
                "/7/requests/20/executions/30/response_body.json",
                "/7/requests/20/executions/30/response_chunks.json",
                "/7/requests/20/executions/30",
                "/7/requests/20/request_body.json",
                "/7/requests/20/response_body.json",
                "/7/requests/20/response_chunks.json",
                "/7/requests/20/executions",
                "/7/requests/20",
            ],
            fsKeys);

        // Object stores never materialize the directory markers.
        var s3Keys = harness.Backends.Created
            .Where(static backend => backend.Storage.Id == 4)
            .SelectMany(static backend => backend.DeletedKeys)
            .ToArray();
        Assert.Equal(
            [
                "/7/requests/21/request_body.json",
                "/7/requests/21/response_body.json",
                "/7/requests/21/response_chunks.json",
            ],
            s3Keys);

        // Primary-storage rows keep their payloads in the database.
        Assert.DoesNotContain(
            harness.Backends.Created,
            static backend => backend.Storage.Id == 1);
        Assert.All(
            harness.Backends.Created,
            static backend => Assert.True(backend.Disposed));

        Assert.Equal([30L], Assert.Single(harness.Gc.DeletedExecutions));
        Assert.Equal([20L, 21L, 22L], Assert.Single(harness.Gc.DeletedRequests));
    }

    [Fact]
    public async Task VacuumRunsWhenEnabledWithTheConfiguredMode()
    {
        var harness = new Harness(
            PolicyWith(),
            new GcOptions { VacuumEnabled = true, VacuumFull = true });

        await harness.Worker.RunAutomaticCleanupAsync();

        Assert.Equal([true], harness.Gc.VacuumCalls);
    }

    [Fact]
    public async Task PreviewCountsEachWindowOnceAndKeepsTheReferenceOrder()
    {
        var harness = new Harness(PolicyWith());
        harness.Gc.RequestCount = 10;
        harness.Gc.ExecutionCount = 4;
        harness.Gc.UsageLogCount = 7;

        var items = await harness.Worker.PreviewCleanupAsync(new GcCleanupRequest
        {
            RequestsCleanupDays = 3,
            UsageLogsCleanupDays = 5,
            RequestBodiesCleanupDays = 7,
            ResponseBodiesCleanupDays = 7,
            ResponseChunksCleanupDays = 2,
        });

        Assert.Equal(
            [
                CleanupResourceTypes.Requests,
                CleanupResourceTypes.UsageLogs,
                CleanupResourceTypes.RequestBodies,
                CleanupResourceTypes.ResponseBodies,
                CleanupResourceTypes.ResponseChunks,
            ],
            items.Select(static item => item.ResourceType).ToArray());
        Assert.Equal(
            [10, 7, 14, 14, 14],
            items.Select(static item => item.EstimatedCount).ToArray());
        Assert.Equal(
            [3, 5, 7, 7, 2],
            items.Select(static item => item.RetentionDays).ToArray());
        Assert.Equal(Now.UtcDateTime.AddDays(-3), items[0].CutoffTime);
        Assert.Equal(Now.UtcDateTime.AddDays(-5), items[1].CutoffTime);
        Assert.Equal(Now.UtcDateTime.AddDays(-7), items[2].CutoffTime);

        // requests window + one 7-day body window + one 2-day body window.
        Assert.Equal(3, harness.Gc.CountRequestCutoffs.Count);
        Assert.Equal(2, harness.Gc.CountExecutionCutoffs.Count);
        Assert.Single(harness.Gc.CountUsageLogCutoffs);
    }

    [Fact]
    public async Task PreviewSurfacesTheReferenceErrorWrappers()
    {
        var harness = new Harness(PolicyWith());
        harness.Gc.CountRequestsFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Worker.PreviewCleanupAsync(new GcCleanupRequest
            {
                RequestsCleanupDays = 1,
            }));

        Assert.Equal("failed to count requests for preview: boom", error.Message);
    }

    private static StoragePolicy PolicyWith(params CleanupOption[] options) => new()
    {
        StoreRequestBody = true,
        StoreResponseBody = true,
        CleanupOptions = options,
    };

    private static DataStorageRecord Storage(long id, string type, bool primary = false) => new()
    {
        Id = id,
        Name = type + "-" + id,
        Description = string.Empty,
        Primary = primary,
        Type = type,
        Settings = "{}",
        Status = "active",
    };

    [Fact]
    public async Task ChannelProbesAlwaysUseTheFixedThreeDayWindow()
    {
        // The pass is independent of the storage policy and of the manual map:
        // gc.go:226 always calls cleanupChannelProbes(ctx, 3, manual).
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.UsageLogs, true, 30)));
        harness.Gc.OldChannelProbeBatches.Enqueue([2L, 1L]);
        harness.Gc.OldChannelProbeBatches.Enqueue([3L]);

        await harness.Worker.RunAutomaticCleanupAsync();

        Assert.All(
            harness.Gc.ChannelProbeCutoffs,
            cutoff => Assert.Equal(
                Now.AddDays(-3).ToUnixTimeSeconds(),
                cutoff));
        Assert.Equal(3, harness.Gc.ChannelProbeCutoffs.Count);
        Assert.Equal(2, harness.Gc.DeletedChannelProbes.Count);
        Assert.Equal([2L, 1L], harness.Gc.DeletedChannelProbes[0]);
        Assert.Equal([3L], harness.Gc.DeletedChannelProbes[1]);
    }

    [Fact]
    public async Task ManualCleanupKeepsTheFixedProbeWindow()
    {
        var harness = new Harness(PolicyWith(
            new CleanupOption(CleanupResourceTypes.Requests, false, 3)));

        await harness.Worker.RunCleanupNowAsync(new GcCleanupRequest
        {
            RequestsCleanupDays = 30,
        });

        Assert.Equal(
            Now.AddDays(-3).ToUnixTimeSeconds(),
            Assert.Single(harness.Gc.ChannelProbeCutoffs));
    }

    [Fact]
    public async Task ProbeCleanupFailuresDoNotAbortTheRestOfThePass()
    {
        var harness = new Harness(
            PolicyWith(),
            new GcOptions { VacuumEnabled = true, VacuumFull = false });
        harness.Gc.ChannelProbeListFailure = new InvalidOperationException("probe cleanup failed");

        await harness.Worker.RunAutomaticCleanupAsync();

        Assert.Equal([false], harness.Gc.VacuumCalls);
        Assert.Empty(harness.Gc.DeletedChannelProbes);
    }

    private sealed class Harness
    {
        public Harness(StoragePolicy policy, GcOptions? options = null)
        {
            Settings.Policy = policy;
            Worker = new GcWorker(
                Settings,
                Gc,
                Storages,
                Backends,
                options ?? new GcOptions { VacuumEnabled = false, VacuumFull = false },
                Clock,
                NullLogger<GcWorker>.Instance);
        }

        public FakeGcStore Gc { get; } = new();

        public FakeGcSettingsService Settings { get; } = new();

        public FakeGcDataStorageStore Storages { get; } = new();

        public FakeGcDataStorageBackendFactory Backends { get; } = new();

        public TestTimeProvider Clock { get; } = new(Now);

        public GcWorker Worker { get; }
    }
}
