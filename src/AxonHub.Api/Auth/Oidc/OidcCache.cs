namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// The byte cache behind the OIDC login flow (reference:
/// <c>OIDCService.cache xcache.Cache[[]byte]</c>, oidc.go:176-182). The
/// reference builds it from <c>cache.mode</c>; the default is <c>memory</c>
/// (conf/conf.go:312), which the port reproduces here. The redis and two-level
/// modes are unported (C171).
/// </summary>
public interface IOidcCache
{
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        byte[] value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IOidcCache"/>, semantically equivalent to the
/// patrickmn/go-cache store the reference's default mode selects: an entry
/// lives until its expiration, and expiry uses go-cache's <c>time.Now().After</c>
/// test (a lookup exactly at the deadline still sees the value).
/// </summary>
public sealed class OidcMemoryCache(TimeProvider timeProvider) : IOidcCache
{
    private readonly Lock _mutex = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_mutex)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult<byte[]?>(null);
            }

            if (timeProvider.GetUtcNow() > entry.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<byte[]?>(null);
            }

            return Task.FromResult<byte[]?>(entry.Value);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(
        string key,
        byte[] value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_mutex)
        {
            _entries[key] = new Entry(value, timeProvider.GetUtcNow() + expiration);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_mutex)
        {
            _entries.Remove(key);
        }

        return Task.CompletedTask;
    }

    private sealed record Entry(byte[] Value, DateTimeOffset ExpiresAt);
}
