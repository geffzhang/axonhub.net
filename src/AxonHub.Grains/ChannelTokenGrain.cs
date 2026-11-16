using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's per-channel <c>oauth.TokenProvider</c> as an actor
/// (llm/oauth/token_provider.go; <c>startTokenProvider</c>, channel.go:310-323):
/// cached OAuth credentials, the 3-minute expiry skew, the five-minute
/// proactive-refresh window, the credential-conflict retry and the one-minute
/// background refresh.
/// </summary>
/// <remarks>
/// <para>
/// The reference hands each channel one provider instance whose state lives for
/// the process lifetime; the port's channel objects are rebuilt per attempt, so
/// the credentials are held here instead. Orleans serializes every turn of one
/// activation, which replaces the reference provider's singleflight: two
/// requests can never run two refreshes for the same channel.
/// </para>
/// <para>
/// HTTP and persistence live behind <see cref="IChannelTokenSource"/> because
/// the grains module references neither the LLM nor the persistence modules.
/// The credentials are a cache over the database: a lost activation re-seeds
/// from the next request's candidate snapshot, and a refresh is always
/// persisted before it is served.
/// </para>
/// <para>
/// Differences (§146): the provider starts on the first request that reaches
/// the channel instead of at enabled-cache swap; the auto-refresh loop ticks
/// once a minute and refreshes inside the window instead of sleeping until
/// <c>expires_at - refreshBefore</c>; and the 30s cache-swap resource cleanup
/// has no counterpart because the timer dies with the activation.
/// </para>
/// </remarks>
public sealed class ChannelTokenGrain(
    IChannelTokenSource source,
    TimeProvider timeProvider,
    ILogger<ChannelTokenGrain> logger) : Grain, IChannelTokenGrain
{
    /// <summary>Reference <c>runAutoRefresh</c> fallback interval (token_provider.go:359).</summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>Reference <c>EnsureFresh</c> default <c>refreshBefore</c> (token_provider.go:274).</summary>
    private static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(5);

    /// <summary>Port of <c>OAuthCredentials.IsExpired</c>: expired 3 minutes early.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(3);

    private static readonly Action<ILogger, long, Exception?> LogAutoRefreshFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ChannelTokenAutoRefreshFailed"),
            "channel token auto refresh failed channel={ChannelId}");

    private ChannelOAuthToken? _credentials;
    private string _credentialsJson = "{}";
    private DateTime _updatedAt;
    private string _channelType = string.Empty;
    private string? _settings;
    private bool _seeded;
    private bool _stopped;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // The reference starts the provider's goroutine when the channel enters
        // the enabled cache. The grain only exists once a request reaches the
        // channel; from then on the loop runs for the activation's lifetime.
        this.RegisterGrainTimer(EnsureFreshTickAsync, AutoRefreshInterval, AutoRefreshInterval);
        return Task.CompletedTask;
    }

    public async Task<ChannelOAuthToken> GetAccessTokenAsync(ChannelTokenSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        AdoptSeed(seed);

        if (_stopped || _credentials is null)
        {
            // StopAutoRefresh + "credentials is nil" (token_provider.go:327-330).
            throw new InvalidOperationException("credentials is nil");
        }

        if (!IsExpired(_credentials, timeProvider.GetUtcNow()))
        {
            return _credentials;
        }

        return await RefreshAsync(ensureFresh: false, CancellationToken.None).ConfigureAwait(true);
    }

    public async Task EnsureFreshAsync()
    {
        // An unseeded grain has no provider in the reference; a stopped one
        // retired its auto-refresh loop. Neither schedules a refresh.
        if (!_seeded || _stopped || _credentials is null)
        {
            return;
        }

        if (_credentials.RefreshToken.Length == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var shouldRefresh = _credentials.ExpiresAt == DateTimeOffset.MinValue
            || now.Add(RefreshBefore) > _credentials.ExpiresAt;
        if (!shouldRefresh)
        {
            return;
        }

        _ = await RefreshAsync(ensureFresh: true, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task EnsureFreshTickAsync(CancellationToken cancellationToken)
    {
        if (!_seeded || _stopped || _credentials is null)
        {
            return;
        }

        try
        {
            // EnsureFresh's own early-outs are re-evaluated inside; keeping the
            // pre-check out here would duplicate the window logic.
            if (_credentials.RefreshToken.Length == 0)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            var shouldRefresh = _credentials.ExpiresAt == DateTimeOffset.MinValue
                || now.Add(RefreshBefore) > _credentials.ExpiresAt;
            if (!shouldRefresh)
            {
                return;
            }

            _ = await RefreshAsync(ensureFresh: true, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // token_provider.go:437-440 logs and retries after the fallback
            // interval, which the periodic timer provides.
            LogAutoRefreshFailed(logger, this.GetPrimaryKeyLong(), ex);
        }
    }

    /// <summary>
    /// Runs the API-side <c>Get</c>/<c>EnsureFresh</c> over the current
    /// snapshot, then adopts the returned credentials and row timestamp.
    /// Errors leave the snapshot untouched, like the reference provider.
    /// </summary>
    private async Task<ChannelOAuthToken> RefreshAsync(
        bool ensureFresh,
        CancellationToken cancellationToken)
    {
        var credentials = _credentials;
        if (credentials is null)
        {
            throw new InvalidOperationException("credentials is nil");
        }

        var result = await source.RefreshAsync(
                new ChannelTokenRefreshRequest
                {
                    ChannelId = this.GetPrimaryKeyLong(),
                    ChannelType = _channelType,
                    Credentials = credentials,
                    CredentialsJson = _credentialsJson,
                    UpdatedAt = _updatedAt,
                    EnsureFresh = ensureFresh,
                    Settings = _settings,
                },
                cancellationToken)
            .ConfigureAwait(true);

        if (result.Stopped || result.Credentials is null)
        {
            // The winning row is no longer OAuth: retire the provider.
            _stopped = true;
            _credentials = null;
            throw new InvalidOperationException("credentials is nil");
        }

        _credentials = result.Credentials;
        _credentialsJson = result.CredentialsJson;
        _updatedAt = Normalize(result.UpdatedAt);
        return _credentials;
    }

    /// <summary>
    /// Adopts a candidate snapshot only when it is newer than the row this
    /// grain last persisted against. Equal timestamps keep the stored
    /// credentials, matching the reference provider's snapshot identity; an
    /// older cached candidate (the enabled-channel cache can lag a refresh)
    /// never rolls a rotated token back.
    /// <para>
    /// The settings document is versioned by the same <c>updated_at</c>, but a
    /// refresh this grain persisted advances that version itself. The enabled
    /// cache rebuild that follows reads the row at the same timestamp, so its
    /// seed must still deliver settings a concurrent channel edit changed —
    /// the reference rebuilds every channel's HTTP client on each cache swap
    /// (channel.go:291-298, §178). Equal timestamps therefore refresh the
    /// settings snapshot while keeping the adopted credentials.
    /// </para>
    /// </summary>
    private void AdoptSeed(ChannelTokenSeed seed)
    {
        var incoming = Normalize(seed.UpdatedAt);

        if (!_seeded || incoming > _updatedAt)
        {
            AdoptSnapshot(seed, incoming);
            return;
        }

        if (incoming == _updatedAt)
        {
            _settings = seed.Settings;
        }
    }

    private void AdoptSnapshot(ChannelTokenSeed seed, DateTime updatedAt)
    {
        _seeded = true;
        _stopped = false;
        _channelType = seed.ChannelType;
        _credentials = seed.Credentials;
        _credentialsJson = seed.CredentialsJson;
        _updatedAt = updatedAt;
        _settings = seed.Settings;
    }

    private static bool IsExpired(ChannelOAuthToken credentials, DateTimeOffset now)
    {
        if (credentials.ExpiresAt == DateTimeOffset.MinValue)
        {
            return true;
        }

        return now.Add(ExpirySkew) > credentials.ExpiresAt;
    }

    /// <summary>
    /// PostgreSQL returns UTC timestamps; Npgsql hands them back as
    /// <see cref="DateTimeKind.Utc"/>. Unspecified values are treated as UTC
    /// like the rest of the persistence layer, and local values are converted.
    /// </summary>
    private static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
