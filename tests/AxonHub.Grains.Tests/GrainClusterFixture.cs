using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace AxonHub.Grains.Tests;

/// <summary>
/// One in-process Orleans cluster for the whole test class. Channel state comes
/// from the fake provider so selection can be tested without a database.
/// </summary>
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
            services.AddSingleton(RateLimitTracker!);
            services.AddSingleton(ChannelLimiterManager!);
        });
    }
}

/// <summary>
/// Stands in for <c>enabledChannelsCache</c>; tests replace the snapshot before
/// selecting.
/// </summary>
public sealed class FakeEnabledChannelProvider : IEnabledChannelProvider
{
    private ChannelDescriptor[] _channels = [];

    public void Set(params ChannelDescriptor[] channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channels = channels;
    }

    public Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_channels);
}
