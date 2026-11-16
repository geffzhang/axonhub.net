using System.Security.Cryptography;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.AuthService's user/password and JWT semantics
/// (internal/server/biz/auth.go:91-260): the 30/90/7-day session windows, the
/// OIDC-only guard order, every wrapped error string, and the refresh
/// transitions (renew, cap reached, expired, maximum age).
/// </summary>
public sealed class ManagementAuthServiceTests
{
    private const string Secret = "test-secret";
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidc = new();

    [Fact]
    public async Task GenerateWritesTheReferenceClaimsAndIdleWindow()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var authTime = Now.AddDays(-10);

        var token = await CreateService().GenerateJwtTokenAtAsync(user, Now, authTime);
        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        Assert.Equal((double?)user.Id, claims.UserId);
        Assert.Equal((long?)authTime.ToUnixTimeSeconds(), claims.AuthTime);
        Assert.Equal((long?)Now.AddDays(30).ToUnixTimeSeconds(), claims.ExpiresAt);
    }

    [Fact]
    public async Task GenerateCapsExpiryAtTheMaximumSessionAge()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var authTime = Now.AddDays(-80);

        var token = await CreateService().GenerateJwtTokenAtAsync(user, Now, authTime);
        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        // now + 30d would be day 110 of the session; auth_time + 90d wins.
        Assert.Equal(authTime.AddDays(90).ToUnixTimeSeconds(), claims.ExpiresAt);
    }

    [Fact]
    public async Task GenerateReportsAMissingSecretKey()
    {
        var user = SeedUser();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().GenerateJwtTokenAtAsync(user, Now, Now));

        Assert.Equal(
            "failed to get secret key: system not initialized: secret key not found",
            error.Message);
    }

    [Fact]
    public async Task GenerateDoubleWrapsASecretLookupFailure()
    {
        var user = SeedUser();
        _systems.GetFailure = new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().GenerateJwtTokenAtAsync(user, Now, Now));

        // SystemService.SecretKey and AuthService.GenerateJWTTokenAt each add
        // one "failed to get secret key: " prefix.
        Assert.Equal(
            "failed to get secret key: failed to get secret key: db down",
            error.Message);
    }

    [Fact]
    public async Task AuthenticateUserReturnsTheActivatedUserAndVerifiesThePassword()
    {
        var user = SeedUser(password: "stored-hash");

        var actual = await CreateService().AuthenticateUserAsync(
            "user@example.com",
            "s3cret");

        Assert.Same(user, actual);
        Assert.Equal(("stored-hash", "s3cret"), Assert.Single(_passwords.Verifications));
    }

    [Fact]
    public async Task AuthenticateUserTreatsAnUnknownEmailAsInvalidPassword()
    {
        var error = await Assert.ThrowsAsync<InvalidPasswordException>(
            () => CreateService().AuthenticateUserAsync("nobody@example.com", "s3cret"));

        Assert.Equal("invalid email or password: invalid password", error.Message);
        Assert.Empty(_passwords.Verifications);
    }

    [Fact]
    public async Task AuthenticateUserTreatsAWrongPasswordAsInvalidPassword()
    {
        SeedUser(password: "stored-hash");
        _passwords.VerifyError = new InvalidOperationException("bcrypt mismatch");

        var error = await Assert.ThrowsAsync<InvalidPasswordException>(
            () => CreateService().AuthenticateUserAsync("user@example.com", "wrong"));

        // The reference omits the colon before ErrInvalidPassword here.
        Assert.Equal("invalid email or password invalid password", error.Message);
    }

    [Fact]
    public async Task AuthenticateUserSkipsDeactivatedUsers()
    {
        SeedUser(status: "deactivated");

        var error = await Assert.ThrowsAsync<InvalidPasswordException>(
            () => CreateService().AuthenticateUserAsync("user@example.com", "s3cret"));

        Assert.Equal("invalid email or password: invalid password", error.Message);
    }

    [Fact]
    public async Task AuthenticateUserMapsAStoreFailureToInternal()
    {
        _users.GetActivatedByEmailFailure = new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().AuthenticateUserAsync("user@example.com", "s3cret"));

        Assert.Equal("server internal error, please try again later", error.Message);
    }

    [Fact]
    public async Task AuthenticateUserRejectsTheOidcOnlyPlaceholderBeforeVerifying()
    {
        SeedUser(password: PasswordHasher.OidcOnlyPlaceholder);

        var error = await Assert.ThrowsAsync<OidcLoginRequiredException>(
            () => CreateService().AuthenticateUserAsync("user@example.com", "anything"));

        Assert.Equal(
            "OIDC user without password, please login via OIDC or set a password",
            error.Message);
        Assert.Empty(_passwords.Verifications);
        Assert.DoesNotContain(nameof(FakeUserProfileStore.ListOidcIdentitiesAsync), _profiles.Calls);
    }

    [Fact]
    public async Task AuthenticateUserRejectsALinkedProviderConfiguredAsLoginOnly()
    {
        var user = SeedUser();
        _profiles.OidcIdentities.Add(NewIdentity(user.Id, "https://login-only.example.com"));
        _oidc.LoginOnlyIssuers.Add("https://login-only.example.com");

        var error = await Assert.ThrowsAsync<OidcLoginRequiredException>(
            () => CreateService().AuthenticateUserAsync("user@example.com", "s3cret"));

        Assert.Equal(
            "OIDC user without password, please login via OIDC or set a password",
            error.Message);
        Assert.Empty(_passwords.Verifications);
    }

    [Fact]
    public async Task AuthenticateUserIgnoresAProviderThatAllowsLocalLogin()
    {
        var user = SeedUser();
        _profiles.OidcIdentities.Add(NewIdentity(user.Id, "https://sso.example.com"));

        var actual = await CreateService().AuthenticateUserAsync(
            "user@example.com",
            "s3cret");

        Assert.Same(user, actual);
    }

    [Fact]
    public async Task AuthenticateUserTreatsAFailedIdentityLookupAsUnrestricted()
    {
        var user = SeedUser();
        _profiles.ListOidcIdentitiesError = new InvalidOperationException("identity db down");

        var actual = await CreateService().AuthenticateUserAsync(
            "user@example.com",
            "s3cret");

        Assert.Same(user, actual);
    }

    [Fact]
    public async Task AuthenticateJwtTokenReturnsTheActivatedUser()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();

        var actual = await CreateService().AuthenticateJwtTokenAsync(
            await CreateService().GenerateJwtTokenAsync(user),
            Now);

        Assert.Equal(user.Id, actual.Id);
    }

    [Fact]
    public async Task AuthenticateJwtTokenRejectsAnExpiredToken()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            user.Id,
            Now.AddDays(-1).ToUnixTimeSeconds(),
            Now.AddDays(-1).ToUnixTimeSeconds(),
            Now.AddSeconds(-1).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().AuthenticateJwtTokenAsync(token, Now));

        Assert.Equal(
            "invalid jwt token: failed to parse jwt token: token is expired",
            error.Message);
    }

    [Fact]
    public async Task AuthenticateJwtTokenRejectsATokenPastTheMaximumSessionAge()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var token = CustomToken(
            $$"""{"auth_time":{{Now.AddDays(-91).ToUnixTimeSeconds()}},"exp":{{Now.AddDays(1).ToUnixTimeSeconds()}},"user_id":{{user.Id}}}""");

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().AuthenticateJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: maximum session age exceeded", error.Message);
    }

    [Fact]
    public async Task AuthenticateJwtTokenSkipsTheCeilingForANonPositiveAuthTime()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        // jwtTimeClaim treats 0 as absent, so the 90-day ceiling is not checked.
        var token = CustomToken(
            $$"""{"auth_time":0,"exp":{{Now.AddDays(1).ToUnixTimeSeconds()}},"user_id":{{user.Id}}}""");

        var actual = await CreateService().AuthenticateJwtTokenAsync(token, Now);

        Assert.Equal(user.Id, actual.Id);
    }

    [Fact]
    public async Task AuthenticateJwtTokenRejectsANonNumericUserId()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var token = CustomToken(
            $$"""{"exp":{{Now.AddDays(1).ToUnixTimeSeconds()}},"user_id":"9"}""");

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().AuthenticateJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: invalid token claims", error.Message);
    }

    [Fact]
    public async Task AuthenticateJwtTokenReportsAMissingUserAsInvalidJwt()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            424242,
            Now.ToUnixTimeSeconds(),
            Now.ToUnixTimeSeconds(),
            Now.AddDays(1).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().AuthenticateJwtTokenAsync(token, Now));

        Assert.Equal(
            "invalid jwt token: failed to get user: ent: user not found",
            error.Message);
    }

    [Fact]
    public async Task AuthenticateJwtTokenReportsAStoreFailureAsInvalidJwt()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        _users.GetByIdFailure = new InvalidOperationException("db down");
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            user.Id,
            Now.ToUnixTimeSeconds(),
            Now.ToUnixTimeSeconds(),
            Now.AddDays(1).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().AuthenticateJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: failed to get user: db down", error.Message);
    }

    [Fact]
    public async Task AuthenticateJwtTokenRejectsDeactivatedUsers()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        user.Status = "deactivated";
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            user.Id,
            Now.ToUnixTimeSeconds(),
            Now.ToUnixTimeSeconds(),
            Now.AddDays(1).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().AuthenticateJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: user not activated", error.Message);
    }

    [Fact]
    public async Task AuthenticateJwtTokenReportsSecretLookupFailuresWithoutTheJwtPrefix()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().AuthenticateJwtTokenAsync("token", Now));

        Assert.Equal(
            "failed to get secret key: system not initialized: secret key not found",
            error.Message);
        Assert.IsNotType<InvalidJwtException>(error);
    }

    [Fact]
    public async Task RefreshDoesNotRotateWhileSevenDaysRemain()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var authTime = Now.AddDays(-10);
        var token = await CreateService().GenerateJwtTokenAtAsync(user, authTime, authTime);

        var result = await CreateService().RefreshJwtTokenAsync(token, Now);

        Assert.False(result.Renewed);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task RefreshRotatesInsideTheRenewalWindowAndPreservesAuthTime()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var authTime = Now.AddDays(-25);
        var refreshable = await CreateService().GenerateJwtTokenAtAsync(user, authTime, authTime);

        var result = await CreateService().RefreshJwtTokenAsync(refreshable, Now);

        Assert.True(result.Renewed);
        Assert.NotNull(result.Token);
        Assert.NotEqual(refreshable, result.Token);

        var claims = JwtTokenCodec.Decode(result.Token, Secret, Now);
        Assert.Equal((double?)user.Id, claims.UserId);
        Assert.Equal((long?)authTime.ToUnixTimeSeconds(), claims.AuthTime);
        Assert.Equal((long?)Now.AddDays(30).ToUnixTimeSeconds(), claims.ExpiresAt);
    }

    [Fact]
    public async Task RefreshDoesNotRenewOnceTheSessionCannotBeExtended()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        var authTime = Now.AddDays(-70);
        // Issued yesterday: now + 30d and the token's expiry both cross the
        // auth_time + 90d ceiling, so the reference returns "not renewed".
        var token = await CreateService().GenerateJwtTokenAtAsync(user, Now.AddDays(-1), authTime);

        var result = await CreateService().RefreshJwtTokenAsync(token, Now);

        Assert.False(result.Renewed);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task RefreshRejectsAnExpiredToken()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            1,
            Now.AddDays(-2).ToUnixTimeSeconds(),
            Now.AddDays(-2).ToUnixTimeSeconds(),
            Now.AddSeconds(-1).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().RefreshJwtTokenAsync(token, Now));

        Assert.Equal(
            "invalid jwt token: failed to parse jwt token: token is expired",
            error.Message);
    }

    [Fact]
    public async Task RefreshWithoutAnAuthTimeDoesNotRenew()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var token = CustomToken(
            $$"""{"exp":{{Now.AddDays(1).ToUnixTimeSeconds()}},"user_id":1}""");

        var result = await CreateService().RefreshJwtTokenAsync(token, Now);

        Assert.False(result.Renewed);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task RefreshDoesNotRenewWhenAuthTimeIsNonPositive()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        SeedUser();
        // exp is inside the renewal window, but jwtTimeClaim rejects auth_time 0
        // as absent, so the reference returns "not renewed" before any lookup.
        var token = CustomToken(
            $$"""{"auth_time":0,"exp":{{Now.AddDays(1).ToUnixTimeSeconds()}},"user_id":1}""");

        var result = await CreateService().RefreshJwtTokenAsync(token, Now);

        Assert.False(result.Renewed);
        Assert.Null(result.Token);
    }

    [Fact]
    public async Task RefreshRejectsATokenPastTheMaximumSessionAge()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var token = CustomToken(
            $$"""{"auth_time":{{Now.AddDays(-91).ToUnixTimeSeconds()}},"exp":{{Now.AddDays(1).ToUnixTimeSeconds()}},"user_id":1}""");

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().RefreshJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: maximum session age exceeded", error.Message);
    }

    [Fact]
    public async Task RefreshReportsAMissingUserAsInvalidJwt()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var authTime = Now.AddDays(-25);
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            424242,
            authTime.ToUnixTimeSeconds(),
            authTime.ToUnixTimeSeconds(),
            authTime.AddDays(30).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().RefreshJwtTokenAsync(token, Now));

        Assert.Equal(
            "invalid jwt token: failed to get user: ent: user not found",
            error.Message);
    }

    [Fact]
    public async Task RefreshReportsAStoreFailureWithoutTheJwtPrefix()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        _users.GetByIdFailure = new InvalidOperationException("db down");
        var authTime = Now.AddDays(-25);
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            user.Id,
            authTime.ToUnixTimeSeconds(),
            authTime.ToUnixTimeSeconds(),
            authTime.AddDays(30).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().RefreshJwtTokenAsync(token, Now));

        Assert.Equal("failed to get user: db down", error.Message);
        Assert.IsNotType<InvalidJwtException>(error);
    }

    [Fact]
    public async Task RefreshRejectsDeactivatedUsers()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = SeedUser();
        user.Status = "deactivated";
        var authTime = Now.AddDays(-25);
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            user.Id,
            authTime.ToUnixTimeSeconds(),
            authTime.ToUnixTimeSeconds(),
            authTime.AddDays(30).ToUnixTimeSeconds());

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().RefreshJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: user not activated", error.Message);
    }

    [Fact]
    public async Task RefreshRejectsANonNumericUserId()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var token = CustomToken(
            $$"""{"auth_time":{{Now.AddDays(-25).ToUnixTimeSeconds()}},"exp":{{Now.AddDays(5).ToUnixTimeSeconds()}},"user_id":"1"}""");

        var error = await Assert.ThrowsAsync<InvalidJwtException>(
            () => CreateService().RefreshJwtTokenAsync(token, Now));

        Assert.Equal("invalid jwt token: invalid token claims", error.Message);
    }

    [Fact]
    public async Task SignInUserInfoOnlyUsesTheRolesEdge()
    {
        var user = SeedUser();
        user.Scopes = """["direct"]""";
        _profiles.Roles.Add(NewRole(1, "System", projectId: 0, """["system"]"""));
        _profiles.Roles.Add(NewRole(2, "Project", projectId: 7, """["project"]"""));
        _profiles.UserProjects.Add(new UserProjectRecord
        {
            Id = 1,
            ProjectId = 7,
            UserId = user.Id,
            IsOwner = false,
            Scopes = """["member"]""",
        });
        _profiles.OidcIdentities.Add(NewIdentity(user.Id, "https://sso.example.com"));

        var info = await CreateService().GetSignInUserInfoAsync(user);

        // AuthenticateUser eager-loads only WithRoles(): project users and OIDC
        // identities stay empty in the sign-in payload.
        Assert.Equal(["direct", "system"], info.Scopes);
        Assert.Equal(["System"], info.Roles.Select(static role => role.Name));
        Assert.Empty(info.Projects);
        Assert.Empty(info.OidcIdentities);
        Assert.True(info.HasPassword);
    }

    private ManagementAuthService CreateService()
        => new(_users, _systems, _profiles, _passwords, _oidc);

    private UserRecord SeedUser(string password = "hashed", string status = "activated")
        => _users.Add(user =>
        {
            user.Email = "user@example.com";
            user.Password = password;
            user.Status = status;
        });

    private static RoleRecord NewRole(long id, string name, long? projectId, string scopes) => new()
    {
        Id = id,
        Name = name,
        ProjectId = projectId,
        Scopes = scopes,
    };

    private static OidcIdentityRecord NewIdentity(long userId, string issuer) => new()
    {
        Id = 1,
        Issuer = issuer,
        Subject = "subject",
        UserId = userId,
    };

    private static string CustomToken(string payload)
    {
        const string Header = """{"alg":"HS256","typ":"JWT"}""";
        var headerSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(Header));
        var payloadSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = headerSegment + "." + payloadSegment;
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.ASCII.GetBytes(signingInput));

        return signingInput + "." + Base64UrlEncode(signature);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal sealed class FakeOidcLoginOnlyProviders : IOidcLoginOnlyProviders
{
    public HashSet<string> LoginOnlyIssuers { get; } = new(StringComparer.Ordinal);

    public bool IsLoginOnly(string issuer) => LoginOnlyIssuers.Contains(issuer);
}
