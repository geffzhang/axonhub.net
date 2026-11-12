namespace AxonHub.Api.Auth;

/// <summary>Port of api.codexOAuthState (internal/server/api/codex.go:47-50).</summary>
public sealed record CodexOAuthState(string CodeVerifier, long CreatedAt);

/// <summary>
/// The typed cache behind the Codex OAuth flow (reference:
/// <c>CodexHandlers.stateCache xcache.Cache[codexOAuthState]</c>,
/// codex.go:28-38). The reference builds it from <c>cache.mode</c>; the default
/// is <c>memory</c> (conf/conf.go:312), which the port reproduces here. The
/// redis and two-level modes are unported (C171).
/// </summary>
public interface ICodexOAuthStateCache
{
    Task<CodexOAuthState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        CodexOAuthState value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="ICodexOAuthStateCache"/>, semantically equivalent to the
/// patrickmn/go-cache store the reference's default mode selects: an entry lives
/// until its expiration, and expiry uses go-cache's <c>time.Now().After</c> test
/// (a lookup exactly at the deadline still sees the value).
/// </summary>
public sealed class CodexOAuthStateCache(TimeProvider timeProvider)
    : ICodexOAuthStateCache
{
    private readonly Lock _mutex = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<CodexOAuthState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_mutex)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult<CodexOAuthState?>(null);
            }

            if (timeProvider.GetUtcNow() > entry.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<CodexOAuthState?>(null);
            }

            return Task.FromResult<CodexOAuthState?>(entry.Value);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(
        string key,
        CodexOAuthState value,
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

    private sealed record Entry(CodexOAuthState Value, DateTimeOffset ExpiresAt);
}
