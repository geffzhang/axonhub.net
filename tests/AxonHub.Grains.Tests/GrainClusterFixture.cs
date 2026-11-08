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

    public async Task InitializeAsync()
    {
        GrainTestSiloConfigurator.ChannelProvider = Channels;
        GrainTestSiloConfigurator.RateLimitTracker = RateLimitTracker;
        GrainTestSiloConfigurator.ChannelLimiterManager = ChannelLimiterManager;

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
    }
}

internal sealed class GrainTestSiloConfigurator : ISiloConfigurator
{
    public static FakeEnabledChannelProvider? ChannelProvider { get; set; }

    public static ChannelRequestTracker? RateLimitTracker { get; set; }

    public static ChannelLimiterManager? ChannelLimiterManager { get; set; }

    public void Configure(ISiloBuilder siloBuilder)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);

        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IEnabledChannelProvider>(ChannelProvider!);
            services.AddSingleton<IEnabledChannelSource>(ChannelProvider!);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(RateLimitTracker!);
            services.AddSingleton(ChannelLimiterManager!);
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
