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
    }
}

internal sealed class GrainTestSiloConfigurator : ISiloConfigurator
{
    public static FakeEnabledChannelProvider? ChannelProvider { get; set; }

    public static ChannelRequestTracker? RateLimitTracker { get; set; }

    public static ChannelLimiterManager? ChannelLimiterManager { get; set; }

    public static FakeChannelTokenSource? TokenSource { get; set; }

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
            services.AddSingleton<IChannelTokenSource>(TokenSource!);

            // The previous-channel grain reads the reference's thirty-minute
            // TTL; the cluster only needs the type registered to activate it.
            services.AddSingleton(new PreviousChannelOptions());
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
