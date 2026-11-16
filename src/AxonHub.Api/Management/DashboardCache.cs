using System.Collections.Concurrent;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of <c>cachedDashboardQuery</c> (internal/server/gql/dashboard_cache.go):
/// a process-local cache with single-flight computation. Callers that lack the
/// dashboard scope are rejected before any cached value or computation is
/// touched, because some dashboard queries execute raw SQL that does not run
/// through Ent's privacy policy.
/// </summary>
/// <remarks>
/// The reference keys entries by the ent client pointer; the port has a single
/// store per scope and omits it (C195). The shared computation runs in its own
/// DI scope with a detached two-minute timeout, so the first caller's
/// disconnect does not cancel it for the other dashboard viewers. In the
/// reference a panic inside <c>compute</c> is recovered and shared as an error;
/// in .NET the exception is shared directly with every waiter.
/// </remarks>
public sealed class DashboardCache
{
    private sealed class Entry(object value, DateTime expiresAt)
    {
        public object Value { get; } = value;

        public DateTime ExpiresAt { get; } = expiresAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _flights =
        new(StringComparer.Ordinal);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public DashboardCache(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the fresh cached value, or runs <paramref name="compute"/> once
    /// for all concurrent callers on this key. Cancelling the request only
    /// stops this caller's wait; the shared computation continues.
    /// </summary>
    public async Task<T> GetOrComputeAsync<T>(
        string key,
        TimeSpan ttl,
        Func<IDashboardStore, CancellationToken, Task<T>> compute,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(compute);

        if (TryRead<T>(key, out var cached))
        {
            return cached;
        }

        // The lazy may reference itself: when the flight wins GetOrAdd it runs
        // the computation and removes itself from the in-flight map afterwards.
        Lazy<Task<object>>? lazy = null;
        lazy = new Lazy<Task<object>>(
            () => ComputeFlightAsync(key, ttl, compute, lazy!),
            LazyThreadSafetyMode.ExecutionAndPublication);

        var flight = _flights.GetOrAdd(key, lazy);

        object result;
        try
        {
            result = await flight.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The reference's select on ctx.Done returns ctx.Err() unchanged.
            throw;
        }

        if (result is T typed)
        {
            return typed;
        }

        throw new InvalidOperationException(
            "unexpected dashboard cache result type " + result.GetType());
    }

    private async Task<object> ComputeFlightAsync<T>(
        string key,
        TimeSpan ttl,
        Func<IDashboardStore, CancellationToken, Task<T>> compute,
        Lazy<Task<object>> self)
    {
        try
        {
            // Another flight may have stored the value while this one queued.
            if (TryRead<T>(key, out var cached))
            {
                return cached!;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IDashboardStore>();

            // Detach the caller's cancellation; the timeout is the shared
            // flight's only deadline (context.WithTimeout(WithoutCancel(ctx))).
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var value = await compute(store, timeout.Token).ConfigureAwait(false);

            var entry = new Entry(
                value!,
                _timeProvider.GetUtcNow().UtcDateTime + ttl);
            _entries[key] = entry;
            ScheduleExpiry(key, entry, ttl);

            return value!;
        }
        finally
        {
            _flights.TryRemove(new KeyValuePair<string, Lazy<Task<object>>>(key, self));
        }
    }

    /// <summary>
    /// Removes never-looked-up entries after their TTL. The comparison removes
    /// only the exact observed entry, so a refreshed allocation survives the
    /// stale timer (the reference's CompareAndDelete).
    /// </summary>
    private void ScheduleExpiry(string key, Entry entry, TimeSpan ttl)
    {
        _ = Task.Delay(ttl).ContinueWith(
            _ =>
            {
                if (_timeProvider.GetUtcNow().UtcDateTime < entry.ExpiresAt)
                {
                    return;
                }

                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryRead<T>(string key, out T value)
    {
        value = default!;

        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow().UtcDateTime > entry.ExpiresAt)
        {
            // Only remove the exact stale entry that was observed; a concurrent
            // refresh stored a new allocation and must survive.
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            return false;
        }

        if (entry.Value is not T typed)
        {
            return false;
        }

        value = typed;
        return true;
    }
}
