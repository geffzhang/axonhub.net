using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// A login-flow failure whose message is handed to the caller verbatim: the
/// reference returns these errors from OIDCService.GetAuthorizeURL/Callback/
/// ExchangeCode and the API layer relocates or renders them unchanged.
/// </summary>
public sealed class OidcFlowException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The authorize URL plus the state that was cached for it.</summary>
public sealed record OidcAuthorizeUrl(string Url, string State);

/// <summary>The callback outcome: an exchange code for a login, or the link intent.</summary>
public sealed record OidcCallbackResult(string ExchangeCode, string Intent);

/// <summary>
/// Port of the login-flow half of biz.OIDCService
/// (internal/server/biz/oidc.go:509-1300): the authorize URL (including the
/// reinitialization of providers that failed their startup check), the
/// callback (state/PKCE validation, token exchange, ID-token/UserInfo claims,
/// link intent, resolveUser, role mapping and the short-lived exchange code)
/// and single-use exchange-code redemption.
/// </summary>
public sealed class OidcLoginService(
    OidcProviderRegistry registry,
    IOidcCache cache,
    IOidcTokenClient tokenClient,
    IOidcUserInfoClient userInfoClient,
    IOidcIdTokenVerifier idTokenVerifier,
    IOidcHttpClient httpClient,
    IOidcLoginStore store,
    TimeProvider timeProvider,
    ILogger<OidcLoginService> logger)
{
    /// <summary>Cache prefix of an authorize state (10 minutes).</summary>
    public const string StateKeyPrefix = "oidc_state:";

    /// <summary>Cache prefix of a PKCE verifier, keyed by state (10 minutes).</summary>
    public const string PkceKeyPrefix = "oidc_pkce:";

    /// <summary>Cache prefix of a link intent, keyed by state (10 minutes).</summary>
    public const string LinkStateKeyPrefix = "oidc_link_state:";

    /// <summary>Cache prefix of a redemption code (5 minutes).</summary>
    public const string ExchangeKeyPrefix = "oidc_exchange:";

    /// <summary>biz.OIDC_ONLY_PLACEHOLDER.</summary>
    public const string OidcOnlyPlaceholder = PasswordHasher.OidcOnlyPlaceholder;

    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExchangeCodeLifetime = TimeSpan.FromMinutes(5);

    private static readonly Action<ILogger, string, Exception?> LogUserInfoFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(0, "OidcUserInfoFailed"),
            "Failed to fetch UserInfo for provider {Provider}");

    private static readonly Action<ILogger, long, Exception?> LogSyncLoginFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "OidcSyncUserInfoLoginFailed"),
            "Failed to sync user info during OIDC login for user {UserId}");

    private static readonly Action<ILogger, long, Exception?> LogSyncLinkFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "OidcSyncUserInfoLinkFailed"),
            "Failed to sync user info during OIDC link for user {UserId}");

    private static readonly ConcurrentExchangeLocks ExchangeLocks = new();

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Port of <c>OIDCService.GetAuthorizeURL</c> (oidc.go:509-600).</summary>
    public async Task<OidcAuthorizeUrl> GetAuthorizeUrlAsync(
        string providerIdentifier,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.GetRuntimeProvider(providerIdentifier);
        if (provider is null)
        {
            var configured = registry.FindConfiguredProvider(providerIdentifier);
            if (configured is null)
            {
                throw new OidcFlowException("Provider not found");
            }

            var providerId = configured.ProviderId;
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var lastCheck = registry.MarkProviderCheck(providerId, now);
            var remaining = 60 - (now - lastCheck);
            if (remaining > 0)
            {
                throw new OidcFlowException(
                    "Please wait " + remaining.ToString(CultureInfo.InvariantCulture)
                    + " seconds before retrying this provider");
            }

            // The reference rebuilds the provider from the issuer alone: manual
            // auth/token/jwks overrides and an explicit redirect_url are
            // ignored on this path (C171).
            OidcDiscoveryDocument discovered;
            try
            {
                discovered = await DiscoveryAsync(configured.IssuerUrl, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException(
                    "Failed to initialize OIDC provider: " + ex.Message, ex);
            }

            var redirectUrl = OidcProviderRegistry.DefaultAuthorizeRedirectPath;
            if (registry.CountProviders() > 1)
            {
                redirectUrl = OidcProviderRegistry.DefaultAuthorizeRedirectPath + "/" + providerId;
            }

            var scopes = configured.ExtraScopes.Count == 0
                ? OidcProviderRegistry.DefaultScopes
                : configured.ExtraScopes;

            provider = new OidcProviderRuntime(
                configured,
                discovered.AuthorizationEndpoint ?? string.Empty,
                discovered.TokenEndpoint ?? string.Empty,
                discovered.UserInfoEndpoint,
                discovered.JwksUri,
                HasDiscovery: true,
                HasVerifier: true,
                redirectUrl,
                scopes)
            {
                VerifierIssuer = discovered.Issuer,
            };

            registry.SetProvider(providerId, provider);
        }

        var redirect = provider.RedirectUrl;
        if (baseUrl.Length > 0 && !redirect.StartsWith("http", StringComparison.Ordinal))
        {
            redirect = baseUrl + redirect;
        }

        var state = Pkce.GenerateState(32);
        try
        {
            await cache.SetAsync(
                    StateKeyPrefix + state,
                    Encoding.UTF8.GetBytes("1"),
                    StateLifetime,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException("failed to cache state: " + ex.Message, ex);
        }

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["response_type"] = "code",
            ["client_id"] = provider.Config.ClientId,
        };

        if (redirect.Length > 0)
        {
            parameters["redirect_uri"] = redirect;
        }

        if (provider.Scopes.Count > 0)
        {
            parameters["scope"] = string.Join(" ", provider.Scopes);
        }

        if (state.Length > 0)
        {
            parameters["state"] = state;
        }

        if (provider.Config.EnablePkce)
        {
            var verifier = Pkce.GenerateState(32);
            try
            {
                await cache.SetAsync(
                        PkceKeyPrefix + state,
                        Encoding.UTF8.GetBytes(verifier),
                        StateLifetime,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException(
                    "failed to cache PKCE verifier: " + ex.Message, ex);
            }

            parameters["code_challenge"] = Pkce.GenerateCodeChallenge(verifier);
            parameters["code_challenge_method"] = "S256";
        }

        var authUrl = provider.AuthorizationEndpoint
            + (provider.AuthorizationEndpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?")
            + OidcProtocol.FormEncode(parameters);

        return new OidcAuthorizeUrl(authUrl, state);
    }

    /// <summary>Port of <c>OIDCService.GetLinkAuthorizeURL</c> (oidc.go:602-617).</summary>
    public async Task<OidcAuthorizeUrl> GetLinkAuthorizeUrlAsync(
        string providerIdentifier,
        string baseUrl,
        long userId,
        CancellationToken cancellationToken = default)
    {
        var result = await GetAuthorizeUrlAsync(providerIdentifier, baseUrl, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await cache.SetAsync(
                    LinkStateKeyPrefix + result.State,
                    Encoding.UTF8.GetBytes(userId.ToString(CultureInfo.InvariantCulture)),
                    StateLifetime,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException("failed to cache link state: " + ex.Message, ex);
        }

        return result;
    }

    /// <summary>Port of <c>OIDCService.Callback</c> (oidc.go:619-785).</summary>
    public async Task<OidcCallbackResult> CallbackAsync(
        string providerIdentifier,
        string code,
        string state,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var provider = registry.GetRuntimeProvider(providerIdentifier);
        if (provider is null)
        {
            throw new OidcFlowException("OIDC provider not found: " + providerIdentifier);
        }

        byte[]? stateValue;
        try
        {
            stateValue = await cache.GetAsync(StateKeyPrefix + state, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException("invalid or expired state parameter", ex);
        }

        if (stateValue is null || stateValue.Length == 0)
        {
            throw new OidcFlowException("invalid or expired state parameter");
        }

        await TryDeleteAsync(StateKeyPrefix + state, cancellationToken).ConfigureAwait(false);

        string? codeVerifier = null;
        if (provider.Config.EnablePkce)
        {
            byte[]? verifierBytes;
            try
            {
                verifierBytes = await cache.GetAsync(PkceKeyPrefix + state, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException("invalid PKCE verifier or verifier expired", ex);
            }

            if (verifierBytes is null || verifierBytes.Length == 0)
            {
                throw new OidcFlowException("invalid PKCE verifier or verifier expired");
            }

            codeVerifier = Encoding.UTF8.GetString(verifierBytes);
            await TryDeleteAsync(PkceKeyPrefix + state, cancellationToken).ConfigureAwait(false);
        }

        var redirect = provider.RedirectUrl;
        if (baseUrl.Length > 0 && !redirect.StartsWith("http", StringComparison.Ordinal))
        {
            redirect = baseUrl + redirect;
        }

        OidcTokenResult token;
        try
        {
            token = await tokenClient.ExchangeAsync(
                    new OidcTokenRequest(
                        provider.TokenEndpoint,
                        provider.Config.ClientId,
                        provider.Config.ClientSecret,
                        redirect,
                        code,
                        codeVerifier),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException(
                "failed to exchange authorization code: " + ex.Message, ex);
        }

        var rawIdToken = token.IdToken ?? string.Empty;
        var subject = string.Empty;
        var claims = new OidcClaims();

        if (rawIdToken.Length > 0)
        {
            if (!provider.HasVerifier)
            {
                throw new OidcFlowException(
                    "OIDC verifier not initialized for provider " + providerIdentifier);
            }

            OidcVerifiedIdToken verified;
            try
            {
                verified = await idTokenVerifier.VerifyAsync(
                        new OidcIdTokenRequest(
                            rawIdToken,
                            provider.VerifierIssuer,
                            provider.Config.ClientId,
                            provider.JwksUri),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException("failed to verify ID token: " + ex.Message, ex);
            }

            subject = verified.Subject;
            claims = OidcClaims.FromRaw(verified.Claims) with
            {
                Groups = OidcClaims.ExtractGroups(verified.Claims, provider.Config, logger),
            };
        }

        if (rawIdToken.Length == 0 || provider.Config.UserInfoUrl.Length > 0)
        {
            if (rawIdToken.Length == 0 && provider.Config.UserInfoUrl.Length == 0)
            {
                throw new OidcFlowException(
                    "no id_token and no user_info_url provided for provider "
                    + providerIdentifier);
            }

            OidcUserInfo? userInfo = null;
            try
            {
                userInfo = await userInfoClient.GetAsync(
                        new OidcUserInfoRequest(
                            provider.HasDiscovery,
                            provider.HasDiscovery
                                ? provider.UserInfoEndpoint
                                : provider.Config.UserInfoUrl,
                            token.AccessToken,
                            token.TokenType),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (rawIdToken.Length == 0)
                {
                    throw new OidcFlowException(
                        "failed to fetch user info for OAuth2-only provider: " + ex.Message,
                        ex);
                }

                LogUserInfoFailed(logger, providerIdentifier, ex);
            }

            if (userInfo is not null)
            {
                if (subject.Length == 0)
                {
                    subject = userInfo.Claims.Sub;
                }

                if (userInfo.Claims.Email.Length > 0)
                {
                    claims = claims with { Email = userInfo.Claims.Email };
                }

                if (userInfo.Claims.EmailVerified)
                {
                    claims = claims with { EmailVerified = true };
                }

                if (userInfo.Claims.Name.Length > 0)
                {
                    claims = claims with { Name = userInfo.Claims.Name };
                }

                if (userInfo.Claims.GivenName.Length > 0)
                {
                    claims = claims with { GivenName = userInfo.Claims.GivenName };
                }

                if (userInfo.Claims.FamilyName.Length > 0)
                {
                    claims = claims with { FamilyName = userInfo.Claims.FamilyName };
                }

                if (userInfo.Claims.Picture.Length > 0)
                {
                    claims = claims with { Picture = userInfo.Claims.Picture };
                }

                var userInfoGroups = OidcClaims.ExtractGroups(
                    userInfo.Raw,
                    provider.Config,
                    logger);
                if (userInfoGroups is { Count: > 0 })
                {
                    claims = claims with { Groups = userInfoGroups };
                }
            }
        }

        if (subject.Length == 0)
        {
            throw new OidcFlowException(
                "failed to resolve subject from id_token or user_info");
        }

        byte[]? linkValue;
        try
        {
            linkValue = await cache.GetAsync(LinkStateKeyPrefix + state, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            linkValue = null;
        }

        if (linkValue is not null && linkValue.Length > 0)
        {
            await TryDeleteAsync(LinkStateKeyPrefix + state, cancellationToken)
                .ConfigureAwait(false);

            var linkText = Encoding.UTF8.GetString(linkValue);
            if (!OidcProtocol.TryAtoi(linkText, out var userId, out var atoiError))
            {
                throw new OidcFlowException("invalid cached link user ID: " + atoiError);
            }

            try
            {
                await CreateIdentityAsync(
                        userId,
                        provider,
                        subject,
                        claims.Email,
                        provider.Config.ProviderDisplayName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException("failed to link identity: " + ex.Message, ex);
            }

            return new OidcCallbackResult(string.Empty, "link");
        }

        var user = await ResolveUserAsync(
                provider,
                subject,
                claims,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(user.Status, "deactivated", StringComparison.Ordinal))
        {
            throw new OidcFlowException("User account is deactivated");
        }

        var exchangeCode = OidcProtocol.HexLower(RandomNumberGenerator.GetBytes(32));
        try
        {
            await cache.SetAsync(
                    ExchangeKeyPrefix + exchangeCode,
                    Encoding.UTF8.GetBytes(
                        user.Id.ToString(CultureInfo.InvariantCulture)),
                    ExchangeCodeLifetime,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException("failed to cache exchange code: " + ex.Message, ex);
        }

        return new OidcCallbackResult(exchangeCode, "login");
    }

    /// <summary>
    /// Port of <c>OIDCService.resolveUser</c> (oidc.go:937-1085): identity
    /// first, then account-first auto-linking by verified email, then JIT
    /// provisioning with the role mappings applied in the create transaction.
    /// </summary>
    public async Task<UserRecord> ResolveUserAsync(
        OidcProviderRuntime provider,
        string subject,
        OidcClaims claims,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(claims);

        var config = provider.Config;
        var issuer = config.GetIssuer();

        OidcIdentityWithUser? identity;
        try
        {
            identity = await store.GetIdentityWithUserAsync(issuer, subject, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException(
                "database error querying identity: " + ex.Message, ex);
        }

        if (identity is not null)
        {
            try
            {
                await store.TouchIdentityLastLoginAsync(
                        identity.Identity.Id,
                        Now,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The reference ignores a failed last_login_at update.
            }

            if (config.SyncUserInfo && identity.User is not null)
            {
                try
                {
                    return await SyncUserInfoAsync(
                            identity.User,
                            claims.Name,
                            claims.GivenName,
                            claims.FamilyName,
                            claims.Picture,
                            claims.Groups,
                            config,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogSyncLoginFailed(logger, identity.User.Id, ex);
                }
            }

            if (identity.User is null)
            {
                throw new OidcFlowException(
                    "OIDC identity linked to missing or deleted user");
            }

            return identity.User;
        }

        if (config.AutoLinkByEmail && claims.Email.Length > 0 && claims.EmailVerified)
        {
            UserRecord? existing;
            try
            {
                existing = await store.GetUserByEmailAsync(claims.Email, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException(
                    "database error querying user by email: " + ex.Message, ex);
            }

            if (existing is not null)
            {
                try
                {
                    await CreateIdentityAsync(
                            existing.Id,
                            provider,
                            subject,
                            claims.Email,
                            config.Name,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new OidcFlowException(
                        "failed to link OIDC identity: " + ex.Message, ex);
                }

                if (config.SyncUserInfo)
                {
                    try
                    {
                        return await SyncUserInfoAsync(
                                existing,
                                claims.Name,
                                claims.GivenName,
                                claims.FamilyName,
                                claims.Picture,
                                claims.Groups,
                                config,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogSyncLinkFailed(logger, existing.Id, ex);
                    }
                }

                return existing;
            }
        }

        if (!config.JitEnabled)
        {
            throw new OidcFlowException(
                "account not found and JIT provisioning is disabled");
        }

        if (config.RequireEmailVerified && !claims.EmailVerified)
        {
            throw new OidcFlowException("email not verified");
        }

        var email = claims.Email;
        if (email.Length == 0)
        {
            email = subject + "@" + config.Name + ".oidc";
        }

        var (firstName, lastName) = DeriveNames(claims.Name, claims.GivenName, claims.FamilyName);

        var plan = await ApplyRoleMappingsAsync(
                claims.Groups,
                config,
                isCreate: true,
                userId: null,
                cancellationToken)
            .ConfigureAwait(false);

        var user = new UserRecord
        {
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            Password = OidcOnlyPlaceholder,
            Status = "activated",
            PreferLanguage = "en",
        };

        if (plan.IsOwner == true)
        {
            user.IsOwner = true;
        }

        if (plan.Scopes is { Count: > 0 } scopes)
        {
            user.Scopes = System.Text.Json.JsonSerializer.Serialize(scopes);
        }

        if (claims.Picture.Length > 0)
        {
            user.Avatar = claims.Picture;
        }

        var identityRecord = new OidcIdentityRecord
        {
            Issuer = issuer,
            Subject = subject,
            Email = email,
            IdpName = config.ProviderDisplayName,
            LastLoginAt = Now,
        };

        try
        {
            return await store.CreateUserWithIdentityAsync(
                    user,
                    plan.RoleNamesForCreate,
                    identityRecord,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OidcLoginStoreException ex)
        {
            var prefix = ex.Stage switch
            {
                "identity" => "failed to create identity for new user: ",
                "roles" => "failed to apply role mappings: ",
                _ => "failed to create user: ",
            };
            throw new OidcFlowException(prefix + ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new OidcFlowException(ex.Message, ex);
        }
    }

    /// <summary>Port of <c>OIDCService.syncUserInfo</c> (oidc.go:1087-1120).</summary>
    public async Task<UserRecord> SyncUserInfoAsync(
        UserRecord user,
        string name,
        string givenName,
        string familyName,
        string picture,
        IReadOnlyList<string>? groups,
        OidcProviderConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(config);

        var (firstName, lastName) = DeriveNames(name, givenName, familyName);

        RoleMappingPlan? plan = null;
        if (!string.Equals(config.SyncRoleStrategy, "create_only", StringComparison.Ordinal))
        {
            plan = await ApplyRoleMappingsAsync(
                    groups,
                    config,
                    isCreate: false,
                    userId: user.Id,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var hasNames = firstName.Length > 0 || lastName.Length > 0;
        var update = new OidcSyncUpdate
        {
            UserId = user.Id,
            FirstName = hasNames ? firstName : null,
            LastName = hasNames ? lastName : null,
            Avatar = picture.Length > 0 ? picture : null,
            IsOwner = plan?.IsOwner,
            Scopes = plan?.Scopes,
            ClearScopes = plan?.ClearScopes ?? false,
            ClearRoles = plan?.ClearRoles ?? false,
            AddRoleIds = plan?.AddRoleIds ?? [],
        };

        var updated = await store.UpdateUserForSyncAsync(update, cancellationToken)
            .ConfigureAwait(false);
        if (updated is null)
        {
            throw new OidcFlowException("ent: user not found");
        }

        return updated;
    }

    /// <summary>Port of <c>OIDCService.ExchangeCode</c> (oidc.go:1240-1300).</summary>
    public async Task<UserRecord> ExchangeCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = ExchangeKeyPrefix + code;
        var gate = ExchangeLocks.GetOrAdd(cacheKey);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? userBytes;
            try
            {
                userBytes = await cache.GetAsync(cacheKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException("invalid or expired exchange code", ex);
            }

            if (userBytes is null)
            {
                throw new OidcFlowException("invalid or expired exchange code");
            }

            await TryDeleteAsync(cacheKey, cancellationToken).ConfigureAwait(false);

            if (!OidcProtocol.TryAtoi(Encoding.UTF8.GetString(userBytes), out var userId, out var atoiError))
            {
                throw new OidcFlowException("invalid user ID format in cache: " + atoiError);
            }

            UserRecord? user;
            try
            {
                user = await store.GetUserByIdAsync(userId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new OidcFlowException("user not found: " + ex.Message, ex);
            }

            if (user is null)
            {
                throw new OidcFlowException("user not found: ent: user not found");
            }

            return user;
        }
        finally
        {
            gate.Release();
            ExchangeLocks.TryRemove(cacheKey);
        }
    }

    private async Task<OidcIdentityRecord> CreateIdentityAsync(
        long userId,
        OidcProviderRuntime provider,
        string subject,
        string email,
        string idpName,
        CancellationToken cancellationToken)
    {
        var identity = new OidcIdentityRecord
        {
            UserId = userId,
            Issuer = provider.Config.GetIssuer(),
            Subject = subject,
            Email = email,
            IdpName = idpName,
            LastLoginAt = Now,
        };

        await store.CreateIdentityAsync(identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <summary>Port of <c>OIDCService.applyRoleMappings</c> (oidc.go:1122-1238).</summary>
    private async Task<RoleMappingPlan> ApplyRoleMappingsAsync(
        IReadOnlyList<string>? groups,
        OidcProviderConfig config,
        bool isCreate,
        long? userId,
        CancellationToken cancellationToken)
    {
        var matchedRules = new List<OidcRoleMappingRule>();
        var matchedAnyGroup = false;

        foreach (var group in groups ?? [])
        {
            foreach (var rule in config.RoleMappingRules)
            {
                var matchGroup = config.GroupParser.CaseSensitive
                    ? rule.MatchGroup
                    : rule.MatchGroup.ToLowerInvariant();

                var matched = false;
                if (rule.IsRegex)
                {
                    matched = TryRegexMatch(matchGroup, group);
                }
                else
                {
                    matched = OidcGlob.Match(matchGroup, group);
                    if (!matched && string.Equals(matchGroup, group, StringComparison.Ordinal))
                    {
                        matched = true;
                    }
                }

                if (matched)
                {
                    matchedAnyGroup = true;
                    matchedRules.Add(rule);
                }
            }
        }

        var scopes = new List<string>();
        if (!matchedAnyGroup)
        {
            scopes.AddRange(config.DefaultScopes);
            foreach (var defaultRole in config.DefaultRoles)
            {
                matchedRules.Add(new OidcRoleMappingRule { DbRole = defaultRole });
            }
        }
        else if (string.Equals(config.RolePrecedenceMode, "highest", StringComparison.Ordinal)
            && matchedRules.Count > 0)
        {
            matchedRules = [matchedRules.OrderByDescending(static rule => rule.Priority).First()];
        }

        var dbRolesToCompile = new List<string>();
        var isOwner = false;
        foreach (var rule in matchedRules)
        {
            var dbRole = GoStrings.TrimSpace(rule.DbRole);
            if (string.Equals(dbRole, "system:owner", StringComparison.Ordinal))
            {
                isOwner = true;
            }
            else if (dbRole.StartsWith("scope:", StringComparison.Ordinal))
            {
                scopes.Add(dbRole["scope:".Length..]);
            }
            else
            {
                dbRolesToCompile.Add(dbRole);
            }
        }

        bool? owner = isOwner
            ? true
            : config.RoleMappingRules.Count > 0 || config.DefaultRoles.Count > 0
                ? false
                : null;

        IReadOnlyList<string>? scopeValue = scopes.Count > 0 ? scopes : null;
        var clearScopes = scopes.Count == 0
            && (config.RoleMappingRules.Count > 0 || config.DefaultScopes.Count > 0);

        var strategy = config.SyncRoleStrategy.Length == 0
            ? "always"
            : config.SyncRoleStrategy;

        var addRoleIds = new List<long>();
        var clearRoles = false;

        if (isCreate)
        {
            return new RoleMappingPlan(
                owner,
                scopeValue,
                clearScopes,
                ClearRoles: false,
                addRoleIds,
                dbRolesToCompile);
        }

        if (dbRolesToCompile.Count > 0)
        {
            IReadOnlyList<RoleRecord> roles;
            try
            {
                roles = await store.ListRolesByNameAsync(dbRolesToCompile, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // applyRoleMappings ignores a failed role query entirely.
                roles = [];
            }

            if (roles.Count > 0)
            {
                var roleIds = roles.Select(static role => role.Id).ToList();
                switch (strategy)
                {
                    case "always":
                        clearRoles = true;
                        addRoleIds.AddRange(roleIds);
                        break;
                    case "merge":
                        addRoleIds.AddRange(roleIds);
                        break;
                    case "manual_first":
                        var count = 0;
                        if (userId is long id)
                        {
                            try
                            {
                                count = await store.CountUserRolesAsync(id, cancellationToken)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                count = 0;
                            }
                        }

                        if (count == 0)
                        {
                            addRoleIds.AddRange(roleIds);
                        }

                        break;
                }
            }
        }
        else if (strategy == "always"
            && (config.RoleMappingRules.Count > 0 || config.DefaultRoles.Count > 0))
        {
            clearRoles = true;
        }

        return new RoleMappingPlan(
            owner,
            scopeValue,
            clearScopes,
            clearRoles,
            addRoleIds,
            []);
    }

    private static bool TryRegexMatch(string pattern, string value)
    {
        try
        {
            return Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static (string FirstName, string LastName) DeriveNames(
        string name,
        string givenName,
        string familyName)
    {
        var firstName = givenName;
        var lastName = familyName;
        if (firstName.Length == 0 && lastName.Length == 0 && name.Length > 0)
        {
            // strings.SplitN(name, " ", 2)
            var index = name.IndexOf(' ', StringComparison.Ordinal);
            if (index < 0)
            {
                firstName = name;
            }
            else
            {
                firstName = name[..index];
                lastName = name[(index + 1)..];
            }
        }

        return (firstName, lastName);
    }

    private async Task TryDeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await cache.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference discards every cache delete error.
        }
    }

    /// <summary>
    /// <c>oidc.NewProvider(ctx, cfgProvider.IssuerURL)</c> from the
    /// reinitialization path: discovery only, no manual endpoint overrides.
    /// </summary>
    private Task<OidcDiscoveryDocument> DiscoveryAsync(
        string issuerUrl,
        CancellationToken cancellationToken)
        => httpClient.DiscoverAsync(issuerUrl, cancellationToken);

    private sealed record RoleMappingPlan(
        bool? IsOwner,
        IReadOnlyList<string>? Scopes,
        bool ClearScopes,
        bool ClearRoles,
        IReadOnlyList<long> AddRoleIds,
        IReadOnlyList<string> RoleNamesForCreate);

    /// <summary>
    /// The reference's <c>sync.Map</c> of per-code mutexes. A small wrapper
    /// keeps the remove-after-release discipline in one place.
    /// </summary>
    private sealed class ConcurrentExchangeLocks
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks =
            new(StringComparer.Ordinal);

        public SemaphoreSlim GetOrAdd(string key)
            => _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        public void TryRemove(string key) => _locks.TryRemove(key, out _);
    }
}
