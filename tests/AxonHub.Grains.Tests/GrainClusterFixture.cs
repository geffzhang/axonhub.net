using AxonHub.Abstractions;
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

    public async Task InitializeAsync()
    {
        GrainTestSiloConfigurator.ChannelProvider = Channels;

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<GrainTestSiloConfigurator>();

        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    public async Task DisposeAsync()
    {
        await Cluster.DisposeAsync();
        GrainTestSiloConfigurator.ChannelProvider = null;
    }
}

internal sealed class GrainTestSiloConfigurator : ISiloConfigurator
{
    public static FakeEnabledChannelProvider? ChannelProvider { get; set; }

    public void Configure(ISiloBuilder siloBuilder)
    {
        ArgumentNullException.ThrowIfNull(siloBuilder);

        siloBuilder.ConfigureServices(services =>
            services.AddSingleton<IEnabledChannelProvider>(ChannelProvider!));
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
