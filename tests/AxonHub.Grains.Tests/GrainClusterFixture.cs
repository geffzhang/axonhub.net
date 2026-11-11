using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace AxonHub.Grains.Tests;

/// <summary>
/// One in-process Orleans cluster shared by every grain test class. The silo
/// resolves its fakes from statics, so classes must not build clusters
/// concurrently; a collection fixture gives them one cluster and one provider.
/// </summary>
[CollectionDefinition(Name)]
public sealed class GrainClusterCollection : ICollectionFixture<GrainClusterFixture>
{
    public const string Name = "grain-cluster";
}

public sealed class GrainClusterFixture : IAsyncLifetime
{
    public TestCluster Cluster { get; private set; } = null!;

    public FakeEnabledChannelProvider Channels { get; } = new();

    /// <summary>
    /// The silo's rate-limit state, mirrored here so tests can seed the tracker
    /// or inspect the limiter manager before and after selection.
    /// </summary>
    public ChannelRequestTracker RateLimitTracker { get; } = new();

    public ChannelLimiterManager ChannelLimiterManager { get; } = new();

    /// <summary>The silo's token-refresh half, driven by the token grain tests.</summary>
    public FakeChannelTokenSource TokenSource { get; } = new();

    /// <summary>
    /// The previous-channel grain's settings. Production keeps the reference's
    /// thirty-minute TTL; the shared cluster shortens it so expiry is observable
    /// without sleeping for half an hour. Only <c>PreviousChannelGrain</c>
    /// consumes this type.
    /// </summary>
    public PreviousChannelOptions PreviousChannels { get; } = new()
    {
        Ttl = TimeSpan.FromSeconds(1),
    };

    /// <summary>The scheduler grains' seams (§149); tests script the timings.</summary>
    public FakeAutoBackupScheduleSource AutoBackupSchedule { get; } = new();

    public FakeAutoBackupRunner AutoBackupRunner { get; } = new();

    public FakeGcScheduleSource GcSchedule { get; } = new();

    public FakeGcCleanupRunner GcCleanup { get; } = new();

    /// <summary>The provider quota grain's seams (§150).</summary>
    public FakeProviderQuotaScheduleSource QuotaSchedule { get; } = new();

    public FakeProviderQuotaCoordinatorOperations QuotaOperations { get; } = new();

    public FakeProviderQuotaGroupRunner QuotaRunner { get; } = new();

    /// <summary>The video-storage scan grain's seams (C234).</summary>
    public FakeVideoStorageScheduleSource VideoStorageSchedule { get; } = new();

    public FakeVideoStorageScanRunner VideoStorageScan { get; } = new();

    /// <summary>The channel-probe minute task's runner seam (C249).</summary>
    public FakeChannelProbeRunRunner ChannelProbeRun { get; } = new();

    public async Task InitializeAsync()
    {
        GrainTestSiloConfigurator.ChannelProvider = Channels;
        GrainTestSiloConfigurator.RateLimitTracker = RateLimitTracker;
        GrainTestSiloConfigurator.ChannelLimiterManager = ChannelLimiterManager;
        GrainTestSiloConfigurator.TokenSource = TokenSource;
        GrainTestSiloConfigurator.PreviousChannels = PreviousChannels;
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
        GrainTestSiloConfigurator.PreviousChannels = null;
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
    }
}

internal sealed class GrainTestSiloConfigurator : ISiloConfigurator
{
    public static FakeEnabledChannelProvider? ChannelProvider { get; set; }

    public static ChannelRequestTracker? RateLimitTracker { get; set; }

    public static ChannelLimiterManager? ChannelLimiterManager { get; set; }

    public static FakeChannelTokenSource? TokenSource { get; set; }

    public static PreviousChannelOptions? PreviousChannels { get; set; }

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
            services.AddSingleton(PreviousChannels!);
            services.AddSingleton<IAutoBackupScheduleSource>(AutoBackupSchedule!);
            services.AddSingleton<IAutoBackupRunner>(AutoBackupRunner!);
            services.AddSingleton<IGcScheduleSource>(GcSchedule!);
            services.AddSingleton<IGcCleanupRunner>(GcCleanup!);
            services.AddSingleton<IProviderQuotaScheduleSource>(QuotaSchedule!);
            services.AddSingleton<IProviderQuotaCoordinatorOperations>(QuotaOperations!);
            services.AddSingleton<IProviderQuotaGroupRunner>(QuotaRunner!);
            services.AddSingleton<IVideoStorageScheduleSource>(VideoStorageSchedule!);
            services.AddSingleton<IVideoStorageScanRunner>(VideoStorageScan!);
            services.AddSingleton<IChannelProbeRunRunner>(ChannelProbeRun!);
        });
    }
}

/// <summary>
/// Stands in for <c>enabledChannelsCache</c> and its database half: the
/// provider side feeds selectors that are constructed directly, the source side
/// feeds <c>EnabledChannelsGrain</c>.
/// </summary>
public sealed class FakeEnabledChannelProvider : IEnabledChannelProvider, IEnabledChannelSource
{
    private ChannelDescriptor[] _channels = [];

    /// <summary>The fingerprint query's answer (null for an empty table).</summary>
    public DateTime? LatestUpdatedAt { get; set; }

    public int LatestUpdatedAtCalls { get; private set; }

    public int ListEnabledCalls { get; private set; }

    public Exception? LatestUpdatedAtFailure { get; set; }

    public Exception? ListEnabledFailure { get; set; }

    public void Set(params ChannelDescriptor[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
    }

    /// <summary>
    /// Clears the counters and injected failures. Tests in one class run
    /// sequentially against the shared fake, so each starts from zero.
    /// </summary>
    public void Reset()
    {
        LatestUpdatedAtCalls = 0;
        ListEnabledCalls = 0;
        LatestUpdatedAtFailure = null;
        ListEnabledFailure = null;
    }

    public Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        ListEnabledCalls++;
        return ListEnabledFailure is null
            ? Task.FromResult(_channels)
            : Task.FromException<ChannelDescriptor[]>(ListEnabledFailure);
    }

    public Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default)
    {
        LatestUpdatedAtCalls++;
        return LatestUpdatedAtFailure is null
            ? Task.FromResult(LatestUpdatedAt)
            : Task.FromException<DateTime?>(LatestUpdatedAtFailure);
    }
}

/// <summary>
/// Stands in for the API-side <c>ChannelTokenSource</c>: records the requests
/// the token grain makes and answers through <see cref="OnRefresh"/> (default:
/// return the snapshot unchanged, a no-op refresh).
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
