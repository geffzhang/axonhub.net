namespace AxonHub.Api.Auth;

/// <summary>Port of api.copilotDeviceFlowState (copilot.go:85-92).</summary>
public sealed record CopilotDeviceFlowState(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    long ExpiresIn,
    long Interval,
    long CreatedAt);

/// <summary>
/// The typed cache behind the GitHub Copilot device flow (reference:
/// <c>CopilotHandlers.deviceCodeCache xcache.Cache[copilotDeviceFlowState]</c>,
/// copilot.go:78-82). The reference builds it from <c>cache.mode</c>; the
/// default is <c>memory</c> (conf/conf.go:312), which the port reproduces here.
/// The redis and two-level modes are unported (C171).
/// </summary>
public interface ICopilotDeviceFlowCache
{
    Task<CopilotDeviceFlowState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string key,
        CopilotDeviceFlowState value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// In-memory <see cref="ICopilotDeviceFlowCache"/>, semantically equivalent to
/// the patrickmn/go-cache store the reference's default mode selects.
/// </summary>
/// <remarks>
/// Go's go-cache treats the expiration argument specially: zero selects the
/// cache's default expiration (5 minutes for <c>cache.mode=memory</c>,
/// internal/pkg/xcache/cache.go:75) and a negative value means "never expires".
/// The device flow reproduces both, because a GitHub <c>expires_in</c> of zero
/// or below reaches <c>Set</c> unchanged (copilot.go:198-203).
/// </remarks>
public sealed class CopilotDeviceFlowCache(TimeProvider timeProvider)
    : ICopilotDeviceFlowCache
{
    /// <summary>internal/pkg/xcache/cache.go:75: the memory cache default.</summary>
    public static readonly TimeSpan DefaultExpiration = TimeSpan.FromMinutes(5);

    private readonly Lock _mutex = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<CopilotDeviceFlowState?> GetAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_mutex)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return Task.FromResult<CopilotDeviceFlowState?>(null);
            }

            if (timeProvider.GetUtcNow() > entry.ExpiresAt)
            {
                _entries.Remove(key);
                return Task.FromResult<CopilotDeviceFlowState?>(null);
            }

            return Task.FromResult<CopilotDeviceFlowState?>(entry.Value);
        }
    }

    /// <inheritdoc />
    public Task SetAsync(
        string key,
        CopilotDeviceFlowState value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        var now = timeProvider.GetUtcNow();
        var expiresAt = expiration == TimeSpan.Zero
            ? now + DefaultExpiration
            : expiration < TimeSpan.Zero
                ? DateTimeOffset.MaxValue
                : now + expiration;

        lock (_mutex)
        {
            _entries[key] = new Entry(value, expiresAt);
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

    private sealed record Entry(CopilotDeviceFlowState Value, DateTimeOffset ExpiresAt);
}
