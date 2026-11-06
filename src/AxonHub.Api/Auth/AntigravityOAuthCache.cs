namespace AxonHub.Api.Auth;

/// <summary>Port of api.antigravityOAuthState (antigravity.go:54-58).</summary>
public sealed record AntigravityOAuthState(string CodeVerifier, string ProjectId, long CreatedAt);

/// <summary>
/// The typed cache behind the Antigravity OAuth flow (reference:
/// <c>AntigravityHandlers.stateCache xcache.Cache[antigravityOAuthState]</c>,
/// antigravity.go:33-42). The reference builds it from <c>cache.mode</c>; the
/// default is <c>memory</c> (conf/conf.go:312), which the port reproduces here.
/// The redis and two-level modes are unported (C171).
/// </summary>
public interface IAntigravityOAuthStateCache
{
    Task<AntigravityOAuthState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        AntigravityOAuthState value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="IAntigravityOAuthStateCache"/>, semantically equivalent
/// to the patrickmn/go-cache store the reference's default mode selects: an
/// entry lives until its expiration, and expiry uses go-cache's
/// <c>time.Now().After</c> test (a lookup exactly at the deadline still sees the
/// value).
/// </summary>
public sealed class AntigravityOAuthStateCache(TimeProvider timeProvider)
    : IAntigravityOAuthStateCache
{
    private readonly Lock _mutex = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<AntigravityOAuthState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_mutex)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult<AntigravityOAuthState?>(null);
            }

            if (timeProvider.GetUtcNow() > entry.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<AntigravityOAuthState?>(null);
            }

            return Task.FromResult<AntigravityOAuthState?>(entry.Value);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(
        string key,
        AntigravityOAuthState value,
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

    private sealed record Entry(AntigravityOAuthState Value, DateTimeOffset ExpiresAt);
}
