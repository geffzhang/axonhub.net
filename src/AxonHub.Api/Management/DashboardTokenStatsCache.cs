using System.Collections.Concurrent;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Management;

/// <summary>All-time token sums held by the token-stats cache.</summary>
public sealed record DashboardAllTimeTokenStats(
    long InputTokens,
    long OutputTokens,
    long CachedTokens);

/// <summary>A cached aggregate plus the instant it was computed (UTC).</summary>
public sealed record DashboardTokenStatsSnapshot(
    DashboardAllTimeTokenStats Stats,
    DateTime CacheTime);

/// <summary>
/// Port of the all-time token-statistics stale-while-revalidate cache
/// (internal/server/gql/dashboard_helpers.go:20-40 and the tokenStats resolver):
/// fresh for the soft TTL, usable until the hard TTL, refreshed synchronously
/// through a single flight once the hard TTL has passed.
/// </summary>
/// <remarks>
/// In the reference the soft-TTL background refresh starts from
/// <c>context.Background()</c>; <c>getUserFromContext</c> then returns
/// <c>privacy.Denyf(ErrNoUser)</c>, so that refresh always fails and the cached
/// values simply remain until the hard TTL (C195). The port therefore has no
/// background refresh path at all: the observable result is identical (the
/// cache is extended only by a successful synchronous refresh).
/// </remarks>
public sealed class DashboardTokenStatsCache
{
    /// <summary>The reference's initial <c>softTTL</c>.</summary>
    public static readonly TimeSpan DefaultSoftTtl = TimeSpan.FromHours(1);

    /// <summary>The reference's initial <c>hardTTL</c>.</summary>
    public static readonly TimeSpan DefaultHardTtl = TimeSpan.FromHours(24);

    private const string FlightKey = "allTimeTokenStats";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<DashboardTokenStatsSnapshot?>>> _flights =
        new(StringComparer.Ordinal);

    private DashboardTokenStatsSnapshot? _snapshot;

    public DashboardTokenStatsCache(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider)
        : this(scopeFactory, timeProvider, DefaultSoftTtl, DefaultHardTtl)
    {
    }

    /// <summary>
    /// The reference's test-only <c>SetTokenStatsCacheTTL</c>; the port's tests
    /// choose the TTLs through the constructor instead.
    /// </summary>
    public DashboardTokenStatsCache(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        TimeSpan softTtl,
        TimeSpan hardTtl)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        SoftTtl = softTtl;
        HardTtl = hardTtl;
    }

    public TimeSpan SoftTtl { get; }

    public TimeSpan HardTtl { get; }

    /// <summary>The current snapshot, or null when no successful refresh has happened.</summary>
    public DashboardTokenStatsSnapshot? Read()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    /// <summary>
    /// Runs the shared refresh once for all concurrent callers. Returns null
    /// when the computation failed; failures are not cached.
    /// </summary>
    public async Task<DashboardTokenStatsSnapshot?> RefreshAsync(
        Func<IDashboardStore, CancellationToken, Task<DashboardAllTimeTokenStats>> compute,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compute);

        Lazy<Task<DashboardTokenStatsSnapshot?>>? lazy = null;
        lazy = new Lazy<Task<DashboardTokenStatsSnapshot?>>(
            () => RefreshCoreAsync(compute, lazy!),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var flight = _flights.GetOrAdd(FlightKey, lazy);
        return await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DashboardTokenStatsSnapshot?> RefreshCoreAsync(
        Func<IDashboardStore, CancellationToken, Task<DashboardAllTimeTokenStats>> compute,
        Lazy<Task<DashboardTokenStatsSnapshot?>> self)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDashboardStore>();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var stats = await compute(store, timeout.Token).ConfigureAwait(false);

            var snapshot = new DashboardTokenStatsSnapshot(
                stats,
                _timeProvider.GetUtcNow().UtcDateTime);
            lock (_gate)
            {
                _snapshot = snapshot;
            }

            return snapshot;
        }
        catch (Exception)
        {
            // singleflight propagates the error to every waiter; the port's
            // callers treat null as that shared failure.
            return null;
        }
        finally
        {
            _flights.TryRemove(
                new KeyValuePair<string, Lazy<Task<DashboardTokenStatsSnapshot?>>>(FlightKey, self));
        }
    }
}
