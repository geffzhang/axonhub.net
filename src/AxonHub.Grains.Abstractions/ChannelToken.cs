using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Orleans-serializable twin of <c>oauth.OAuthCredentials</c>
/// (llm/oauth/credentials.go:10-18). The grains project cannot reference the
/// LLM module, so the per-channel token grain exchanges this shape with the API
/// layer, which owns the <c>OAuthCredentials</c> mapping (§146).
/// </summary>
[GenerateSerializer]
public sealed class ChannelOAuthToken
{
    [Id(0)]
    public string ClientId { get; set; } = string.Empty;

    [Id(1)]
    public string AccessToken { get; set; } = string.Empty;

    [Id(2)]
    public string RefreshToken { get; set; } = string.Empty;

    [Id(3)]
    public string IdToken { get; set; } = string.Empty;

    /// <summary>Go's zero <c>time.Time</c> is <see cref="DateTimeOffset.MinValue"/>.</summary>
    [Id(4)]
    public DateTimeOffset ExpiresAt { get; set; }

    [Id(5)]
    public string TokenType { get; set; } = string.Empty;

    [Id(6)]
    public List<string>? Scopes { get; set; }
}

/// <summary>
/// What one request attempt knows about a channel's OAuth credentials: the
/// credentials the candidate carried, the raw <c>credentials</c> document they
/// came from, and the row's <c>updated_at</c>.
/// <para>
/// The reference gives each channel one <c>TokenProvider</c> whose snapshot is
/// replaced when the enabled-channel cache swaps
/// (<c>startTokenProvider</c> over the newly built channel). The grain keeps
/// the same snapshot identity: it adopts a seed only when the seed's
/// <see cref="UpdatedAt"/> is newer than the row the grain last saw (§146).
/// </para>
/// </summary>
[GenerateSerializer]
public sealed class ChannelTokenSeed
{
    [Id(0)]
    public long ChannelId { get; set; }

    /// <summary>Ent channel type; selects the token recipe.</summary>
    [Id(1)]
    public string ChannelType { get; set; } = string.Empty;

    /// <summary>The OAuth credentials resolved from the channel document.</summary>
    [Id(2)]
    public ChannelOAuthToken Credentials { get; set; } = new();

    /// <summary>
    /// The raw <c>credentials</c> jsonb text the credentials were resolved
    /// from. Refreshing rewrites it the way
    /// <c>refreshOAuthTokenWithClient</c> mutates its snapshot
    /// (channel_llm.go:1647-1667).
    /// </summary>
    [Id(3)]
    public string CredentialsJson { get; set; } = "{}";

    /// <summary>Ent <c>updated_at</c> of the row behind the seed.</summary>
    [Id(4)]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One refresh request from <c>ChannelTokenGrain</c> to the API-side source
/// that owns HTTP and persistence. The grain never references the LLM or
/// persistence modules.
/// </summary>
[GenerateSerializer]
public sealed class ChannelTokenRefreshRequest
{
    [Id(0)]
    public long ChannelId { get; set; }

    [Id(1)]
    public string ChannelType { get; set; } = string.Empty;

    [Id(2)]
    public ChannelOAuthToken Credentials { get; set; } = new();

    /// <summary>The snapshot's current credentials document (write base).</summary>
    [Id(3)]
    public string CredentialsJson { get; set; } = "{}";

    /// <summary>The snapshot's <c>updated_at</c> (optimistic write predicate).</summary>
    [Id(4)]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// <see langword="true"/> runs the reference's <c>EnsureFresh</c> (window
    /// refresh; no-op without a refresh token), <see langword="false"/> runs
    /// <c>Get</c> (refresh only when expired).
    /// </summary>
    [Id(5)]
    public bool EnsureFresh { get; set; }
}

/// <summary>The outcome of one refresh attempt (channel_llm.go:1638-1702).</summary>
[GenerateSerializer]
public sealed class ChannelTokenRefreshResult
{
    /// <summary>
    /// The credentials after refresh, conflict adoption or a no-op; null when
    /// the provider retired because the winning row is no longer OAuth.
    /// </summary>
    [Id(0)]
    public ChannelOAuthToken? Credentials { get; set; }

    /// <summary>The credentials document these credentials belong to.</summary>
    [Id(1)]
    public string CredentialsJson { get; set; } = "{}";

    /// <summary>The <c>updated_at</c> the snapshot now stands on.</summary>
    [Id(2)]
    public DateTime UpdatedAt { get; set; }

    /// <summary><c>StopAutoRefresh</c>: the channel lost its OAuth credentials.</summary>
    [Id(3)]
    public bool Stopped { get; set; }
}

/// <summary>
/// Database/HTTP half of the per-channel token provider. Implemented by the API
/// layer, which can reference both the LLM module (token recipes and refresh
/// HTTP) and the persistence module (the optimistic credentials update).
/// </summary>
public interface IChannelTokenSource
{
    /// <summary>
    /// Ports <c>TokenProvider.Get</c>/<c>EnsureFresh</c> over a throwaway
    /// provider built from <paramref name="request"/>; persistence failures are
    /// reported through <see cref="ChannelTokenRefreshResult"/> like the
    /// reference's <c>OnRefreshed</c> callback.
    /// </summary>
    Task<ChannelTokenRefreshResult> RefreshAsync(
        ChannelTokenRefreshRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-channel owner of the reference's <c>TokenProvider</c> state
/// (llm/oauth/token_provider.go; <c>startTokenProvider</c>,
/// channel.go:310-323): credentials, the channel row snapshot they were
/// persisted against, the conflict-retry loop and the background refresh.
/// </summary>
public interface IChannelTokenGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// Adopts <paramref name="seed"/> when it is newer than the stored
    /// snapshot, then returns valid credentials, refreshing when they are
    /// expired ("<c>TokenProvider.Get</c>").
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>credentials is nil</c>, including after a conflict winner retired
    /// the provider.
    /// </exception>
    Task<ChannelOAuthToken> GetAccessTokenAsync(ChannelTokenSeed seed);

    /// <summary>
    /// The reference's <c>EnsureFresh(ctx, 0)</c> (five-minute window), run by
    /// the grain's periodic timer and by tests. A refresh failure propagates to
    /// this caller and leaves the snapshot untouched; only the periodic tick
    /// logs and keeps the current credentials, like the reference's
    /// auto-refresh loop.
    /// </summary>
    Task EnsureFreshAsync();
}
