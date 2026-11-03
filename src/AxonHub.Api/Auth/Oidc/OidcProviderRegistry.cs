using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>Thrown when the configured provider list cannot be built (reference: NewOIDCService error).</summary>
public sealed class OidcConfigurationException(string message) : Exception(message);

/// <summary>
/// A provider that passed the startup checks and is usable for the login flow.
/// The reference stores these in <c>OIDCService.providers</c> (oidc.go:176-182);
/// the port keeps the same information without the go-oidc/oauth2 objects.
/// </summary>
public sealed record OidcProviderRuntime(
    OidcProviderConfig Config,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? UserInfoEndpoint,
    string? JwksUri,
    bool HasDiscovery,
    bool HasVerifier,
    string RedirectUrl,
    IReadOnlyList<string> Scopes);

/// <summary>The active/last-check pair of <c>OIDCService.getProviderInfo</c>.</summary>
public readonly record struct OidcProviderStatus(bool Active, long LastCheck);

/// <summary>
/// Port of the provider registry half of biz.OIDCService
/// (internal/server/biz/oidc.go:160-460): configuration normalization, the
/// startup discovery/manual-endpoint checks, the runtime provider map and the
/// per-provider activity state.
/// </summary>
/// <remarks>
/// The reference runs all of this in <c>NewOIDCService</c> at dependency
/// injection time. The port builds the same state in <see cref="InitializeAsync"/>,
/// called by <see cref="OidcInitializationHostedService"/> at startup and
/// lazily by the first request; a configuration error therefore still fails
/// startup instead of the first login attempt.
/// </remarks>
public sealed class OidcProviderRegistry(
    IReadOnlyList<OidcProviderConfig> providers,
    IOidcHttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<OidcProviderRegistry> logger)
{
    /// <summary>Default IdP callback, relative to <c>server.public_url</c>.</summary>
    public const string DefaultAuthorizeRedirectPath = "/oauth/oidc/callback";

    /// <summary>go-oidc's default scopes when <c>extra_scopes</c> is empty.</summary>
    public static readonly IReadOnlyList<string> DefaultScopes =
        ["openid", "profile", "email"];

    private static readonly Action<ILogger, string, string, Exception?> LogDiscoveryFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(0, "OidcDiscoveryFailed"),
            "OIDC discovery failed for provider {Provider} (issuer {Issuer})");

    private static readonly Action<ILogger, string, Exception?> LogMissingEndpoints =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(0, "OidcMissingEndpoints"),
            "OIDC provider {Provider} is missing required endpoints "
            + "(discovery failed and no manual endpoints provided)");

    private static readonly Action<ILogger, string, Exception?> LogIconResolveFailed =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(0, "OidcIconResolveFailed"),
            "Failed to resolve icon for OIDC provider {Provider}");

    private readonly Lock _mutex = new();
    private readonly Lock _initializeGate = new();
    private readonly Dictionary<string, OidcProviderRuntime> _runtimeProviders =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastCheck = new(StringComparer.Ordinal);
    private List<OidcProviderConfig> _configuredProviders = [];
    private Task? _initializeTask;

    /// <summary>Normalized providers in configuration order (OIDCService.cfg.Providers).</summary>
    public IReadOnlyList<OidcProviderConfig> ConfiguredProviders
    {
        get
        {
            lock (_mutex)
            {
                return _configuredProviders;
            }
        }
    }

    /// <summary>Port of <c>OIDCService.CountProviders</c> (oidc.go:338-340).</summary>
    public int CountProviders()
    {
        lock (_mutex)
        {
            return _configuredProviders.Count;
        }
    }

    /// <summary>Port of <c>NewOIDCService</c>'s provider loop (oidc.go:192-304).</summary>
    /// <remarks>
    /// The reference builds this state synchronously in the constructor; the
    /// port memoizes the first run as a task, so a configuration error faults
    /// every caller (including startup) with the same exception.
    /// </remarks>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_initializeGate)
        {
            _initializeTask ??= InitializeCoreAsync();
        }

        return _initializeTask.WaitAsync(cancellationToken);
    }

    private async Task InitializeCoreAsync()
    {
        var prepared = new List<OidcProviderConfig>(providers.Count);
        var seen = new Dictionary<string, string>(providers.Count, StringComparer.Ordinal);

        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index].Normalize();
            if (provider.Id.Length == 0)
            {
                throw new OidcConfigurationException(
                    $"OIDC provider at index {index} requires id or name");
            }

            var normalizedId = OidcProviderConfig.NormalizeIdentifier(provider.Id);
            if (seen.TryGetValue(normalizedId, out var previousProviderId))
            {
                throw new OidcConfigurationException(
                    $"duplicate OIDC provider id \"{provider.Id}\" conflicts with "
                    + $"\"{previousProviderId}\"");
            }

            seen[normalizedId] = provider.Id;
            prepared.Add(provider);

            lock (_mutex)
            {
                _lastCheck[provider.Id] = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            }
        }

        for (var index = 0; index < prepared.Count; index++)
        {
            var provider = prepared[index];
            var providerId = provider.Id;

            OidcDiscoveryDocument? discovered = null;
            if (provider.IssuerUrl.Length > 0)
            {
                try
                {
                    // NewOIDCService runs under context.Background(), so one
                    // request's cancellation does not abort the shared state.
                    discovered = await httpClient.DiscoverAsync(provider.IssuerUrl, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // oidc.NewProvider failure is logged; the provider may
                    // still be usable through manual endpoints.
                    LogDiscoveryFailed(logger, providerId, provider.IssuerUrl, ex);
                }
            }

            var authorizationEndpoint = discovered?.AuthorizationEndpoint ?? string.Empty;
            var tokenEndpoint = discovered?.TokenEndpoint ?? string.Empty;
            var userInfoEndpoint = discovered?.UserInfoEndpoint;
            var jwksUri = discovered?.JwksUri;

            if (provider.AuthUrl.Length > 0 && provider.TokenUrl.Length > 0)
            {
                authorizationEndpoint = provider.AuthUrl;
                tokenEndpoint = provider.TokenUrl;
            }

            var hasVerifier = discovered is not null || provider.JwksUrl.Length > 0;

            if (authorizationEndpoint.Length == 0 || tokenEndpoint.Length == 0)
            {
                LogMissingEndpoints(logger, providerId, null);
                prepared[index] = provider;
                continue;
            }

            try
            {
                provider = provider with { IconUrl = OidcIconResolver.Resolve(provider.IconUrl) };
            }
            catch (Exception ex)
            {
                LogIconResolveFailed(logger, providerId, ex);
            }

            prepared[index] = provider;

            var redirectUrl = provider.RedirectUrl;
            if (redirectUrl.Length == 0)
            {
                redirectUrl = DefaultAuthorizeRedirectPath;
                if (providers.Count > 1)
                {
                    redirectUrl = DefaultAuthorizeRedirectPath + "/" + providerId;
                }
            }

            var scopes = provider.ExtraScopes.Count == 0
                ? DefaultScopes
                : provider.ExtraScopes;

            lock (_mutex)
            {
                _runtimeProviders[providerId] = new OidcProviderRuntime(
                    provider,
                    authorizationEndpoint,
                    tokenEndpoint,
                    userInfoEndpoint,
                    jwksUri,
                    discovered is not null,
                    hasVerifier,
                    redirectUrl,
                    scopes);
            }
        }

        lock (_mutex)
        {
            _configuredProviders = prepared;
        }
    }

    /// <summary>Port of <c>OIDCService.getProviderInfo</c> (oidc.go:353-366).</summary>
    public OidcProviderStatus GetProviderStatus(string identifier)
    {
        lock (_mutex)
        {
            if (GetRuntimeProviderLocked(identifier) is { } runtime)
            {
                return new OidcProviderStatus(
                    true,
                    LookupLastCheckLocked(runtime.Config.ProviderId));
            }

            if (FindConfiguredProviderLocked(identifier) is { } configured)
            {
                return new OidcProviderStatus(false, LookupLastCheckLocked(configured.ProviderId));
            }

            return new OidcProviderStatus(false, 0);
        }
    }

    /// <summary>Port of <c>OIDCService.getProviderByIdentifier</c> (oidc.go:379-384).</summary>
    public OidcProviderRuntime? GetRuntimeProvider(string identifier)
    {
        lock (_mutex)
        {
            return GetRuntimeProviderLocked(identifier);
        }
    }

    /// <summary>
    /// Port of <c>OIDCService.markProviderCheck</c> (oidc.go:386-398): returns
    /// the previous check timestamp and refreshes it only when it is at least
    /// 60 seconds old.
    /// </summary>
    public long MarkProviderCheck(string providerId, long now)
    {
        lock (_mutex)
        {
            var lastCheck = LookupLastCheckLocked(providerId);
            if (now - lastCheck < 60)
            {
                return lastCheck;
            }

            _lastCheck[providerId] = now;
            return lastCheck;
        }
    }

    /// <summary>Port of <c>OIDCService.setProvider</c> (oidc.go:400-405).</summary>
    public void SetProvider(string providerId, OidcProviderRuntime provider)
    {
        lock (_mutex)
        {
            _runtimeProviders[providerId] = provider;
        }
    }

    private OidcProviderRuntime? GetRuntimeProviderLocked(string identifier)
    {
        var normalizedIdentifier = OidcProviderConfig.NormalizeIdentifier(identifier);
        foreach (var (providerId, provider) in _runtimeProviders)
        {
            if (string.Equals(
                    OidcProviderConfig.NormalizeIdentifier(providerId),
                    normalizedIdentifier,
                    StringComparison.Ordinal)
                || provider.Config.MatchesIdentifier(identifier))
            {
                return provider;
            }
        }

        return null;
    }

    private OidcProviderConfig? FindConfiguredProviderLocked(string identifier)
    {
        foreach (var provider in _configuredProviders)
        {
            var normalized = provider.Normalize();
            if (normalized.MatchesIdentifier(identifier))
            {
                return normalized;
            }
        }

        return null;
    }

    private long LookupLastCheckLocked(string providerId)
        => _lastCheck.TryGetValue(providerId, out var value) ? value : 0;
}

/// <summary>
/// Port of the OIDCService provider constructors for
/// <see cref="IOidcLoginOnlyProviders"/>: a linked identity restricts local
/// login when its issuer belongs to a provider with <c>oidc_login_only</c>
/// (internal/server/biz/oidc.go:497-503).
/// </summary>
public sealed class OidcLoginOnlyProviders(IReadOnlyList<OidcProviderConfig> providers)
    : IOidcLoginOnlyProviders
{
    /// <inheritdoc />
    public bool IsLoginOnly(string issuer)
    {
        foreach (var provider in providers)
        {
            if (string.Equals(provider.GetIssuer(), issuer, StringComparison.Ordinal)
                && provider.OidcLoginOnly)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Runs the reference's startup provider checks. NewOIDCService performs them
/// while the FX graph is built, so a bad configuration aborts startup; the
/// hosted service keeps that timing for the port.
/// </summary>
public sealed class OidcInitializationHostedService(OidcProviderRegistry registry)
    : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
        => registry.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
