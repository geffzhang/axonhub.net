using AxonHub.Abstractions;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace AxonHub.Api.Tests;

/// <summary>
/// One in-process Orleans cluster shared by every API test class. The client
/// provider seam is a static that the silo resolves at deploy time, so classes
/// must not build clusters concurrently; a collection fixture gives them one
/// cluster and one provider.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OrleansCollection : ICollectionFixture<GrainClusterFixture>
{
    public const string Name = "orleans";
}

public sealed class GrainClusterFixture : IAsyncLifetime
{
    public GrainClusterFixture()
    {
        CandidateSelector = new ModelCandidateSelector(
            new FakeModelStore(),
            new StaticModelSettingsProvider(ModelSettings.Default),
            new FakeChannelStore(),
            Channels,
            TimeProvider.System,
            bypassModelPrivacy: true);
    }

    public TestCluster Cluster { get; private set; } = null!;

    public FakeEnabledChannelProvider Channels { get; } = new();

    /// <summary>
    /// The silo's rate-limit state, mirrored here so orchestrator tests drive
    /// the same instances the pool grain scores against.
    /// </summary>
    public ChannelRequestTracker RateLimitTracker { get; } = new();

    public ChannelLimiterManager ChannelLimiterManager { get; } = new();

    /// <summary>The silo's token-refresh half (ChannelTokenGrain dependency).</summary>
    public FakeChannelTokenSource TokenSource { get; } = new();

    /// <summary>The scheduler grains' seams (§149); the bridges are tested here.</summary>
    public FakeAutoBackupScheduleSource AutoBackupSchedule { get; } = new();

    public FakeAutoBackupRunner AutoBackupRunner { get; } = new();

    public FakeGcScheduleSource GcSchedule { get; } = new();

    public FakeGcCleanupRunner GcCleanup { get; } = new();

    /// <summary>The provider quota grain's seams (§150); the bridges are tested here.</summary>
    public FakeProviderQuotaScheduleSource QuotaSchedule { get; } = new();

    public FakeProviderQuotaCoordinatorOperations QuotaOperations { get; } = new();

    public FakeProviderQuotaGroupRunner QuotaRunner { get; } = new();

    /// <summary>The video-storage scan grain's seams (C234); the bridge is tested here.</summary>
    public FakeVideoStorageScheduleSource VideoStorageSchedule { get; } = new();

    public FakeVideoStorageScanRunner VideoStorageScan { get; } = new();

    /// <summary>The channel-probe grain's runner seam (C249).</summary>
    public FakeChannelProbeRunRunner ChannelProbeRun { get; } = new();

    /// <summary>The expired-disabled-key cleanup grain's runner seam.</summary>
    public FakeChannelCredentialRecoveryRunner ChannelCredentialRecovery { get; } = new();

    /// <summary>
    /// The orchestrator's model-association selector. The reference's tests
    /// mark the context with <c>authz.WithTestBypass</c>, which allows every
    /// privacy query rule; the port passes the equivalent constructor flag.
    /// The model store starts empty, so requests fall back to the channel pool
    /// exactly like the reference's unconfigured-model path.
    /// </summary>
    public IModelCandidateSelector CandidateSelector { get; }

    public async Task InitializeAsync()
    {
        GrainTestSiloConfigurator.ChannelProvider = Channels;
        GrainTestSiloConfigurator.RateLimitTracker = RateLimitTracker;
        GrainTestSiloConfigurator.ChannelLimiterManager = ChannelLimiterManager;
        GrainTestSiloConfigurator.TokenSource = TokenSource;
        GrainTestSiloConfigurator.AutoBackupSchedule = AutoBackupSchedule;
        GrainTestSiloConfigurator.AutoBackupRunner = AutoBackupRunner;
        GrainTestSiloConfigurator.GcSchedule = GcSchedule;
        GrainTestSiloConfigurator.GcCleanup = GcCleanup;
        GrainTestSiloConfigurator.QuotaSchedule = QuotaSchedule;
        GrainTestSiloConfigurator.QuotaOperations = QuotaOperations;
        GrainTestSiloConfigurator.QuotaRunner = QuotaRunner;
        GrainTestSiloConfigurator.VideoStorageSchedule = VideoStorageSchedule;
        GrainTestSiloConfigurator.VideoStorageScan = VideoStorageScan;
        GrainTestSiloConfigurator.ChannelProbeRun = ChannelProbeRun;
        GrainTestSiloConfigurator.ChannelCredentialRecovery = ChannelCredentialRecovery;

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<GrainTestSiloConfigurator>();
        Cluster = builder.Build();

        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await Cluster.DisposeAsync();
        GrainTestSiloConfigurator.ChannelProvider = null;
        GrainTestSiloConfigurator.RateLimitTracker = null;
        GrainTestSiloConfigurator.ChannelLimiterManager = null;
        GrainTestSiloConfigurator.TokenSource = null;
        GrainTestSiloConfigurator.AutoBackupSchedule = null;
        GrainTestSiloConfigurator.AutoBackupRunner = null;
        GrainTestSiloConfigurator.GcSchedule = null;
        GrainTestSiloConfigurator.GcCleanup = null;
        GrainTestSiloConfigurator.QuotaSchedule = null;
        GrainTestSiloConfigurator.QuotaOperations = null;
        GrainTestSiloConfigurator.QuotaRunner = null;
        GrainTestSiloConfigurator.VideoStorageSchedule = null;
        GrainTestSiloConfigurator.VideoStorageScan = null;
        GrainTestSiloConfigurator.ChannelProbeRun = null;
        GrainTestSiloConfigurator.ChannelCredentialRecovery = null;
    }
}

internal sealed class GrainTestSiloConfigurator : ISiloConfigurator
{
    public static FakeEnabledChannelProvider? ChannelProvider { get; set; }

    public static ChannelRequestTracker? RateLimitTracker { get; set; }

    public static ChannelLimiterManager? ChannelLimiterManager { get; set; }

    public static FakeChannelTokenSource? TokenSource { get; set; }

    public static FakeAutoBackupScheduleSource? AutoBackupSchedule { get; set; }

    public static FakeAutoBackupRunner? AutoBackupRunner { get; set; }

    public static FakeGcScheduleSource? GcSchedule { get; set; }

    public static FakeGcCleanupRunner? GcCleanup { get; set; }

    public static FakeProviderQuotaScheduleSource? QuotaSchedule { get; set; }

    public static FakeProviderQuotaCoordinatorOperations? QuotaOperations { get; set; }

    public static FakeProviderQuotaGroupRunner? QuotaRunner { get; set; }

    public static FakeVideoStorageScheduleSource? VideoStorageSchedule { get; set; }

    public static FakeVideoStorageScanRunner? VideoStorageScan { get; set; }

    public static FakeChannelProbeRunRunner? ChannelProbeRun { get; set; }

    public static FakeChannelCredentialRecoveryRunner? ChannelCredentialRecovery { get; set; }

    public void Configure(ISiloBuilder siloBuilder)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);

        // The backup and GC grains (§149) register reminders.
        siloBuilder.UseInMemoryReminderService();

        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IEnabledChannelProvider>(ChannelProvider!);
            services.AddSingleton<IEnabledChannelSource>(ChannelProvider!);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(RateLimitTracker!);
            services.AddSingleton(ChannelLimiterManager!);
            services.AddSingleton<IChannelTokenSource>(TokenSource!);

            // The previous-channel grain reads the reference's thirty-minute
            // TTL; the cluster only needs the type registered to activate it.
            services.AddSingleton(new PreviousChannelOptions());
            services.AddSingleton<IAutoBackupScheduleSource>(AutoBackupSchedule!);
            services.AddSingleton<IAutoBackupRunner>(AutoBackupRunner!);
            services.AddSingleton<IGcScheduleSource>(GcSchedule!);
            services.AddSingleton<IGcCleanupRunner>(GcCleanup!);
            services.AddSingleton<IProviderQuotaScheduleSource>(QuotaSchedule!);
            services.AddSingleton<IProviderQuotaCoordinatorOperations>(QuotaOperations!);
            services.AddSingleton<IProviderQuotaGroupRunner>(QuotaRunner!);
            services.AddSingleton<IVideoStorageScheduleSource>(VideoStorageSchedule!);
            services.AddSingleton<IVideoStorageScanRunner>(VideoStorageScan!);

            // The scheduler activator arms ChannelProbeGrain at startup, and the
            // grain's constructor requires this runner.
            services.AddSingleton<IChannelProbeRunRunner>(ChannelProbeRun!);

            // The activator also arms the expired-disabled-key cleanup grain
            // (channel.go:204-223), whose constructor requires this runner.
            services.AddSingleton<IChannelCredentialRecoveryRunner>(ChannelCredentialRecovery!);
        });
    }
}

public sealed class FakeEnabledChannelProvider : IEnabledChannelProvider, IEnabledChannelSource
{
    private ChannelDescriptor[] _channels = [];

    /// <summary>The fingerprint query's answer (null for an empty table).</summary>
    public DateTime? LatestUpdatedAt { get; set; }

    public void Set(params ChannelDescriptor[] channels)
    {
        _channels = channels;
    }

    public Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_channels);

    public Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(LatestUpdatedAt);
}

/// <summary>
/// Stands in for the API-side <c>ChannelTokenSource</c> so the silo can
/// activate <c>ChannelTokenGrain</c> without HTTP or a database.
/// </summary>
public sealed class FakeChannelTokenSource : IChannelTokenSource
{
    public List<ChannelTokenRefreshRequest> Requests { get; } = [];

    public Func<ChannelTokenRefreshRequest, ChannelTokenRefreshResult>? OnRefresh { get; set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Requests.Clear();
        OnRefresh = null;
        Failure = null;
    }

    public Task<ChannelTokenRefreshResult> RefreshAsync(
        ChannelTokenRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Requests.Add(request);

        if (Failure is not null)
        {
            return Task.FromException<ChannelTokenRefreshResult>(Failure);
        }

        if (OnRefresh is not null)
        {
            return Task.FromResult(OnRefresh(request));
        }

        return Task.FromResult(new ChannelTokenRefreshResult
        {
            Credentials = request.Credentials,
            CredentialsJson = request.CredentialsJson,
            UpdatedAt = request.UpdatedAt,
        });
    }
}

/// <summary>Scripted IAutoBackupScheduleSource (§149): a queue of instants, one per arm.</summary>
public sealed class FakeAutoBackupScheduleSource : IAutoBackupScheduleSource
{
    private readonly Queue<DateTimeOffset> _results = new();

    public List<DateTimeOffset> Calls { get; } = [];

    private DateTimeOffset Fallback { get; set; }

    public void Reset(params DateTimeOffset[] results)
    {
        Calls.Clear();
        _results.Clear();
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }

        Fallback = results.Length == 0 ? default : results[^1];
    }

    public Task<DateTimeOffset> GetNextOccurrenceAsync(
        DateTimeOffset after,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls.Add(after);
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Fallback);
    }
}

public sealed class FakeAutoBackupRunner : IAutoBackupRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

/// <summary>Scripted IGcScheduleSource (§149): a queue of instants, one per arm.</summary>
public sealed class FakeGcScheduleSource : IGcScheduleSource
{
    private readonly Queue<DateTimeOffset> _results = new();

    public List<DateTimeOffset> Calls { get; } = [];

    private DateTimeOffset Fallback { get; set; }

    public void Reset(params DateTimeOffset[] results)
    {
        Calls.Clear();
        _results.Clear();
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }

        Fallback = results.Length == 0 ? default : results[^1];
    }

    public DateTimeOffset NextOccurrence(DateTimeOffset after)
    {
        Calls.Add(after);
        return _results.Count > 0 ? _results.Dequeue() : Fallback;
    }
}

public sealed class FakeGcCleanupRunner : IGcCleanupRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

/// <summary>Scripted IVideoStorageScheduleSource (C234): a queue of intervals.</summary>
public sealed class FakeVideoStorageScheduleSource : IVideoStorageScheduleSource
{
    private readonly Queue<TimeSpan> _intervals = new();

    public int Calls { get; private set; }

    private TimeSpan Fallback { get; set; } = TimeSpan.FromMinutes(1);

    public void Reset(params TimeSpan[] intervals)
    {
        Calls = 0;
        _intervals.Clear();
        foreach (var interval in intervals)
        {
            _intervals.Enqueue(interval);
        }

        Fallback = intervals.Length == 0 ? TimeSpan.FromMinutes(1) : intervals[^1];
    }

    public Task<TimeSpan> GetIntervalAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.FromResult(_intervals.Count > 0 ? _intervals.Dequeue() : Fallback);
    }
}

public sealed class FakeChannelProbeRunRunner : IChannelProbeRunRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task RunProbeAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

public sealed class FakeChannelCredentialRecoveryRunner : IChannelCredentialRecoveryRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task RunRecoveryAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}

public sealed class FakeVideoStorageScanRunner : IVideoStorageScanRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
