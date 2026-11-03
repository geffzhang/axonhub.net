using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Auth;

/// <summary>
/// The OIDC login-only flag of the configured providers, as consulted by
/// OIDCService.IsUserRestrictedToOIDC (internal/server/biz/oidc.go:464-504).
/// The OIDC provider configuration is not ported yet, so the default registry
/// has no providers and only the password placeholder restricts a user (C014).
/// </summary>
public interface IOidcLoginOnlyProviders
{
    bool IsLoginOnly(string issuer);
}

/// <inheritdoc cref="IOidcLoginOnlyProviders"/>
public sealed class EmptyOidcLoginOnlyProviders : IOidcLoginOnlyProviders
{
    public bool IsLoginOnly(string issuer) => false;
}

/// <summary>The result of AuthService.RefreshJWTToken: an empty token means "do not renew".</summary>
public readonly record struct JwtRefreshResult(string? Token, bool Renewed);

/// <summary>
/// Port of the user/password and JWT methods of biz.AuthService
/// (internal/server/biz/auth.go:91-260): secret-key lookup, HS256 token
/// generation, credentials authentication and the sliding-session refresh
/// rules. API key authentication lives in <see cref="ApiKeyAuthenticator"/>.
/// </summary>
/// <remarks>
/// AuthService runs every lookup under authz.RunWithSystemBypass because the
/// sign-in path has no viewer yet; the port has no ent privacy layer, so the
/// store reads are already unprivileged. The OIDC provider config is not
/// ported: a linked identity only restricts local login when its provider is
/// registered as OIDC_LOGIN_ONLY (C014).
/// </remarks>
public sealed class ManagementAuthService(
    IUserStore users,
    ISystemStore systems,
    IUserProfileStore profiles,
    IPasswordHasher passwords,
    IOidcLoginOnlyProviders oidcProviders)
{
    /// <summary>biz.SystemKeySecretKey.</summary>
    public const string SecretKeyKey = "system_jwt_secret_key";

    /// <summary>user.StatusActivated.</summary>
    public const string ActivatedStatus = "activated";

    /// <summary>biz.AdminSessionIdle.</summary>
    public static readonly TimeSpan AdminSessionIdle = TimeSpan.FromDays(30);

    /// <summary>biz.AdminSessionMaximum.</summary>
    public static readonly TimeSpan AdminSessionMaximum = TimeSpan.FromDays(90);

    /// <summary>biz.AdminSessionRenewal.</summary>
    public static readonly TimeSpan AdminSessionRenewal = TimeSpan.FromDays(7);

    /// <summary>Port of AuthService.GenerateJWTToken.</summary>
    public Task<string> GenerateJwtTokenAsync(
        UserRecord user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = DateTimeOffset.UtcNow;
        return GenerateJwtTokenAtAsync(user, now, now, cancellationToken);
    }

    /// <summary>Port of AuthService.GenerateJWTTokenAt.</summary>
    public async Task<string> GenerateJwtTokenAtAsync(
        UserRecord user,
        DateTimeOffset now,
        DateTimeOffset authTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        string secretKey;
        try
        {
            secretKey = await GetSecretKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get secret key: " + ex.Message,
                ex);
        }

        var expiresAt = now.Add(AdminSessionIdle);
        var maximum = authTime.Add(AdminSessionMaximum);
        if (expiresAt > maximum)
        {
            expiresAt = maximum;
        }

        return JwtTokenCodec.EncodeHs256(
            secretKey,
            user.Id,
            now.ToUnixTimeSeconds(),
            authTime.ToUnixTimeSeconds(),
            expiresAt.ToUnixTimeSeconds());
    }

    /// <summary>Port of AuthService.AuthenticateUser.</summary>
    public async Task<UserRecord> AuthenticateUserAsync(
        string email,
        string password,
        CancellationToken cancellationToken = default)
    {
        UserRecord? user;
        try
        {
            // Ent queries email = ? AND status = activated, and eager-loads the
            // user's roles. The unique (email, deleted_at) index guarantees at
            // most one live row.
            user = await users.GetActivatedByEmailAsync(email, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(ManagementAuthErrors.Internal, ex);
        }

        if (user is null)
        {
            throw new InvalidPasswordException(
                $"invalid email or password: {ManagementAuthErrors.InvalidPassword}");
        }

        // OIDCService.IsUserRestrictedToOIDC runs before VerifyPassword.
        if (user.Password == PasswordHasher.OidcOnlyPlaceholder
            || await IsRestrictedToOidcAsync(user, cancellationToken).ConfigureAwait(false))
        {
            throw new OidcLoginRequiredException(ManagementAuthErrors.OidcLoginRequired);
        }

        try
        {
            passwords.Verify(user.Password, password);
        }
        catch (InvalidOperationException ex)
        {
            // The reference message omits the colon before ErrInvalidPassword.
            throw new InvalidPasswordException(
                $"invalid email or password {ManagementAuthErrors.InvalidPassword}",
                ex);
        }

        return user;
    }

    /// <summary>Port of AuthService.AuthenticateJWTToken.</summary>
    public Task<UserRecord> AuthenticateJwtTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
        => AuthenticateJwtTokenAsync(token, DateTimeOffset.UtcNow, cancellationToken);

    /// <summary>Test seam for AuthService.AuthenticateJWTToken's clock.</summary>
    public async Task<UserRecord> AuthenticateJwtTokenAsync(
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var claims = await ParseClaimsAsync(token, now, cancellationToken)
            .ConfigureAwait(false);

        if (claims.AuthTime is long authTime
            && now >= DateTimeOffset.FromUnixTimeSeconds(authTime).Add(AdminSessionMaximum))
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: maximum session age exceeded");
        }

        if (claims.UserId is not double userId)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: invalid token claims");
        }

        var user = await GetUserForJwtAsync((long)userId, cancellationToken)
            .ConfigureAwait(false);

        if (user.Status != ActivatedStatus)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: user not activated");
        }

        return user;
    }

    /// <summary>Port of AuthService.RefreshJWTToken.</summary>
    public async Task<JwtRefreshResult> RefreshJwtTokenAsync(
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var claims = await ParseClaimsAsync(token, now, cancellationToken)
            .ConfigureAwait(false);

        if (claims.ExpiresAt is not long expiresAtUnix)
        {
            return new JwtRefreshResult(null, false);
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix);
        if (expiresAt - now >= AdminSessionRenewal)
        {
            return new JwtRefreshResult(null, false);
        }

        if (claims.AuthTime is not long authTimeUnix)
        {
            return new JwtRefreshResult(null, false);
        }

        var authTime = DateTimeOffset.FromUnixTimeSeconds(authTimeUnix);
        var maximum = authTime.Add(AdminSessionMaximum);
        if (now >= maximum)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: maximum session age exceeded");
        }

        // Once the session cannot leave its maximum age, a renewal is a no-op.
        if (now.Add(AdminSessionIdle) >= maximum && expiresAt >= maximum)
        {
            return new JwtRefreshResult(null, false);
        }

        if (claims.UserId is not double userId)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: invalid token claims");
        }

        UserRecord? user;
        try
        {
            user = await users.GetByIdAsync((long)userId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get user: " + ex.Message, ex);
        }

        if (user is null)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: failed to get user: ent: user not found");
        }

        if (user.Status != ActivatedStatus)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: user not activated");
        }

        var refreshed = await GenerateJwtTokenAtAsync(
                user,
                now,
                authTime,
                cancellationToken)
            .ConfigureAwait(false);

        return new JwtRefreshResult(refreshed, true);
    }

    /// <summary>
    /// Builds the sign-in response user. AuthenticateUser eager-loads only the
    /// roles edge, so ConvertUserToUserInfo sees empty project and OIDC lists.
    /// </summary>
    public async Task<UserProfileInfo> GetSignInUserInfoAsync(
        UserRecord user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var roles = await profiles.ListUserRolesAsync(user.Id, cancellationToken)
            .ConfigureAwait(false);

        return UserProfileService.BuildUserInfo(user, roles, [], []);
    }

    private async Task<bool> IsRestrictedToOidcAsync(
        UserRecord user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<OidcIdentityRecord> identities;
        try
        {
            identities = await profiles.ListOidcIdentitiesAsync(user.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The reference logs a failed identity lookup and falls back to
            // "not restricted" rather than failing the sign-in.
            return false;
        }

        foreach (var identity in identities)
        {
            if (oidcProviders.IsLoginOnly(identity.Issuer))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<JwtTokenCodec.Claims> ParseClaimsAsync(
        string token,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string secretKey;
        try
        {
            secretKey = await GetSecretKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // AuthService.parseJWTClaims wraps SystemService.SecretKey's error.
            throw new InvalidOperationException(
                "failed to get secret key: " + ex.Message,
                ex);
        }

        try
        {
            return JwtTokenCodec.Decode(token, secretKey, now);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: failed to parse jwt token: {ex.Message}",
                ex);
        }
    }

    private async Task<UserRecord> GetUserForJwtAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        UserRecord? user;
        try
        {
            user = await users.GetByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidJwtException(
                $"{ManagementAuthErrors.InvalidJwt}: failed to get user: {ex.Message}",
                ex);
        }

        return user ?? throw new InvalidJwtException(
            $"{ManagementAuthErrors.InvalidJwt}: failed to get user: ent: user not found");
    }

    private async Task<string> GetSecretKeyAsync(CancellationToken cancellationToken)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(SecretKeyKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SystemService.SecretKey's own wrapper; GenerateJWTTokenAt adds the
            // second "failed to get secret key: " prefix, matching the reference.
            throw new InvalidOperationException(
                "failed to get secret key: " + ex.Message,
                ex);
        }

        if (row is null)
        {
            throw new InvalidOperationException(
                $"{ManagementAuthErrors.SystemNotInitialized}: secret key not found");
        }

        return row.Value;
    }
}
