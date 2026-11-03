using AxonHub.Api.Auth.Oidc;
using AxonHub.Llm.OAuth;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the login-flow half of biz.OIDCService (internal/server/biz/oidc.go:
/// 509-1300): the authorize URL, the callback (state/PKCE, token exchange,
/// claims, link intent), resolveUser with its auto-link and JIT branches, the
/// role mappings of syncUserInfo and the single-use exchange code.
/// </summary>
public sealed class OidcLoginServiceTests
{
    private static readonly DateTimeOffset Start =
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private const string BaseUrl = "https://app.example.com";

    private readonly TestTimeProvider _clock = new(Start);
    private readonly FakeOidcCache _cache = new();
    private readonly FakeOidcTokenClient _tokens = new();
    private readonly FakeOidcUserInfoClient _userInfo = new();
    private readonly FakeOidcIdTokenVerifier _verifier = new();
    private readonly FakeOidcHttpClient _http = new();
    private readonly FakeOidcLoginStore _store = new();
    private OidcProviderRegistry _registry = null!;
    private OidcLoginService _login = null!;

    [Fact]
    public async Task TheAuthorizeUrlCarriesTheOAuth2ParametersAndCachesTheState()
    {
        await CreateAsync(Provider());

        var result = await _login.GetAuthorizeUrlAsync("okta", BaseUrl);

        Assert.Equal(
            "https://idp.example.com/auth?client_id=client-1"
            + "&redirect_uri=https%3A%2F%2Fapp.example.com%2Foauth%2Foidc%2Fcallback"
            + "&response_type=code&scope=openid+profile+email&state=" + result.State,
            result.Url);
        Assert.Equal(43, result.State.Length);

        var key = OidcLoginService.StateKeyPrefix + result.State;
        Assert.Equal("1", _cache.Text(key));
        Assert.Equal(TimeSpan.FromMinutes(10), _cache.Expirations[key]);
    }

    [Fact]
    public async Task WithoutABaseUrlTheRedirectStaysRelative()
    {
        await CreateAsync(Provider());

        var result = await _login.GetAuthorizeUrlAsync("okta", string.Empty);

        Assert.Contains(
            "redirect_uri=%2Foauth%2Foidc%2Fcallback&",
            result.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitAbsoluteRedirectIsNotPrefixed()
    {
        await CreateAsync(Provider(redirectUrl: "https://sso.example.com/callback"));

        var result = await _login.GetAuthorizeUrlAsync("okta", BaseUrl);

        Assert.Contains(
            "redirect_uri=https%3A%2F%2Fsso.example.com%2Fcallback&",
            result.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownProviderFailsWithProviderNotFound()
    {
        await CreateAsync(Provider());

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.GetAuthorizeUrlAsync("github", BaseUrl));

        Assert.Equal("Provider not found", exception.Message);
    }

    [Fact]
    public async Task AnInactiveProviderWaitsSixtySecondsBeforeReinitializing()
    {
        await CreateAsync(Provider(
            authUrl: null,
            tokenUrl: null,
            issuer: null,
            issuerUrl: "https://idp.example.com",
            jwksUrl: null));

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.GetAuthorizeUrlAsync("okta", BaseUrl));

        Assert.Equal(
            "Please wait 60 seconds before retrying this provider",
            exception.Message);
        Assert.False(_registry.GetProviderStatus("okta").Active);
    }

    [Fact]
    public async Task ReinitializationUsesDiscoveryOnlyAndReplacesTheRuntimeProvider()
    {
        await CreateAsync(Provider(
            authUrl: "https://manual.example.com/auth",
            tokenUrl: null,
            issuer: null,
            issuerUrl: "https://idp.example.com",
            jwksUrl: null));
        Assert.Null(_registry.GetRuntimeProvider("okta"));

        _http.Documents["https://idp.example.com"] = new OidcDiscoveryDocument(
            "https://idp.example.com",
            "https://discovered.example.com/auth",
            "https://discovered.example.com/token",
            "https://discovered.example.com/userinfo",
            "https://discovered.example.com/jwks",
            ["RS256"]);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var result = await _login.GetAuthorizeUrlAsync("okta", BaseUrl);

        Assert.StartsWith(
            "https://discovered.example.com/auth?",
            result.Url,
            StringComparison.Ordinal);
        var runtime = _registry.GetRuntimeProvider("okta");
        Assert.NotNull(runtime);
        Assert.Equal("https://discovered.example.com/token", runtime.TokenEndpoint);
        Assert.Equal("https://discovered.example.com/jwks", runtime.JwksUri);
        Assert.True(runtime.HasDiscovery);
        Assert.True(runtime.HasVerifier);
        Assert.Equal("https://idp.example.com", runtime.VerifierIssuer);
    }

    [Fact]
    public async Task AFailedReinitializationWrapsTheDiscoveryError()
    {
        await CreateAsync(Provider(
            authUrl: null,
            tokenUrl: null,
            issuer: null,
            issuerUrl: "https://idp.example.com",
            jwksUrl: null));
        _clock.Advance(TimeSpan.FromSeconds(60));

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.GetAuthorizeUrlAsync("okta", BaseUrl));

        Assert.Equal(
            "Failed to initialize OIDC provider: oidc: failed to query provider "
            + "\"https://idp.example.com\"",
            exception.Message);
    }

    [Fact]
    public async Task AMultiProviderReinitializationUsesTheProviderScopedCallback()
    {
        await CreateAsync(
            Provider(
                id: "alpha",
                authUrl: null,
                tokenUrl: null,
                issuer: null,
                issuerUrl: "https://alpha.example.com",
                jwksUrl: null),
            Provider(
                id: "beta",
                authUrl: "https://beta/auth",
                tokenUrl: "https://beta/token",
                issuer: null));
        _http.Documents["https://alpha.example.com"] = new OidcDiscoveryDocument(
            "https://alpha.example.com",
            "https://alpha/auth",
            "https://alpha/token",
            null,
            null,
            ["RS256"]);
        _clock.Advance(TimeSpan.FromSeconds(60));

        var result = await _login.GetAuthorizeUrlAsync("alpha", BaseUrl);

        Assert.Contains(
            "redirect_uri=https%3A%2F%2Fapp.example.com%2Foauth%2Foidc%2Fcallback%2Falpha&",
            result.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PkceCachesAVerifierAndAddsTheS256Challenge()
    {
        await CreateAsync(Provider(enablePkce: true));

        var result = await _login.GetAuthorizeUrlAsync("okta", BaseUrl);

        var verifier = _cache.Text(OidcLoginService.PkceKeyPrefix + result.State);
        Assert.NotNull(verifier);
        Assert.Equal(43, verifier.Length);
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            _cache.Expirations[OidcLoginService.PkceKeyPrefix + result.State]);
        Assert.Contains(
            "code_challenge=" + Pkce.GenerateCodeChallenge(verifier) + "&",
            result.Url,
            StringComparison.Ordinal);
        Assert.Contains(
            "&code_challenge_method=S256&",
            result.Url,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACacheFailureFailsTheAuthorizeFlow()
    {
        await CreateAsync(Provider());
        _cache.SetFailure = new InvalidOperationException("boom");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.GetAuthorizeUrlAsync("okta", BaseUrl));

        Assert.Equal("failed to cache state: boom", exception.Message);
    }

    [Fact]
    public async Task APkceCacheFailureFailsTheAuthorizeFlow()
    {
        await CreateAsync(Provider(enablePkce: true));
        _cache.SetFailure = new InvalidOperationException("boom");
        _cache.SetFailureKeyPrefix = OidcLoginService.PkceKeyPrefix;

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.GetAuthorizeUrlAsync("okta", BaseUrl));

        Assert.Equal("failed to cache PKCE verifier: boom", exception.Message);
    }

    [Fact]
    public async Task TheLinkAuthorizeUrlCachesTheUserId()
    {
        await CreateAsync(Provider());

        var result = await _login.GetLinkAuthorizeUrlAsync("okta", BaseUrl, 42);

        Assert.Equal(
            "42",
            _cache.Text(OidcLoginService.LinkStateKeyPrefix + result.State));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            _cache.Expirations[OidcLoginService.LinkStateKeyPrefix + result.State]);
    }

    [Fact]
    public async Task ALinkStateCacheFailureFailsTheLinkFlow()
    {
        await CreateAsync(Provider());
        _cache.SetFailure = new InvalidOperationException("boom");
        _cache.SetFailureKeyPrefix = OidcLoginService.LinkStateKeyPrefix;

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.GetLinkAuthorizeUrlAsync("okta", BaseUrl, 42));

        Assert.Equal("failed to cache link state: boom", exception.Message);
    }

    [Fact]
    public async Task TheCallbackExchangesTheCodeVerifiesTheIdTokenAndIssuesAnExchangeCode()
    {
        await CreateLoggedInCallbackAsync();

        var result = await _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl);

        Assert.Equal("login", result.Intent);
        Assert.Matches("^[0-9a-f]{64}$", result.ExchangeCode);

        var token = Assert.Single(_tokens.Requests);
        Assert.Equal("https://idp.example.com/token", token.TokenUrl);
        Assert.Equal("client-1", token.ClientId);
        Assert.Equal("secret-1", token.ClientSecret);
        Assert.Equal(
            "https://app.example.com/oauth/oidc/callback",
            token.RedirectUrl);
        Assert.Equal("code-1", token.Code);
        Assert.Null(token.CodeVerifier);

        var verification = Assert.Single(_verifier.Requests);
        Assert.Equal("raw.id.token", verification.RawToken);
        Assert.Equal("https://issuer.example.com", verification.Issuer);
        Assert.Equal("client-1", verification.ClientId);
        Assert.Equal("https://idp.example.com/jwks", verification.JwksUri);

        var exchangeKey = OidcLoginService.ExchangeKeyPrefix + result.ExchangeCode;
        Assert.Equal("1", _cache.Text(exchangeKey));
        Assert.Equal(TimeSpan.FromMinutes(5), _cache.Expirations[exchangeKey]);
        Assert.DoesNotContain(OidcLoginService.StateKeyPrefix + "state-1", _cache.Entries.Keys);

        var (user, roles, identity) = Assert.Single(_store.CreationRequests);
        Assert.Equal("new@example.com", user.Email);
        Assert.Equal("New", user.FirstName);
        Assert.Equal("User", user.LastName);
        Assert.Equal("!OIDC_SSO_ONLY!", user.Password);
        Assert.Equal("activated", user.Status);
        Assert.Equal("en", user.PreferLanguage);
        Assert.Empty(roles);
        Assert.Equal("https://issuer.example.com", identity.Issuer);
        Assert.Equal("subject-1", identity.Subject);
        Assert.Equal("Okta", identity.IdpName);
        Assert.Equal(Start.UtcDateTime, identity.LastLoginAt);
    }

    [Fact]
    public async Task APkceCallbackSendsTheCachedVerifierAndConsumesIt()
    {
        await CreateLoggedInCallbackAsync(Provider(enablePkce: true));
        _cache.Entries[OidcLoginService.PkceKeyPrefix + "state-1"] = "verifier-1"u8.ToArray();

        await _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl);

        Assert.Equal("verifier-1", Assert.Single(_tokens.Requests).CodeVerifier);
        Assert.DoesNotContain(
            OidcLoginService.PkceKeyPrefix + "state-1",
            _cache.Entries.Keys);
    }

    [Fact]
    public async Task AnUnknownCallbackProviderFails()
    {
        await CreateAsync(Provider());

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("github", "code", "state", BaseUrl));

        Assert.Equal("OIDC provider not found: github", exception.Message);
    }

    [Fact]
    public async Task AnUnknownOrExpiredStateFails()
    {
        await CreateAsync(Provider());

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code", "state", BaseUrl));

        Assert.Equal("invalid or expired state parameter", exception.Message);
    }

    [Fact]
    public async Task AStateCacheFailureIsReportedAsAnInvalidState()
    {
        await CreateAsync(Provider());
        _cache.GetFailure = new InvalidOperationException("boom");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code", "state", BaseUrl));

        Assert.Equal("invalid or expired state parameter", exception.Message);
    }

    [Fact]
    public async Task APkceProviderRejectsAMissingVerifier()
    {
        await CreateAsync(Provider(enablePkce: true));
        _cache.Entries[OidcLoginService.StateKeyPrefix + "state-1"] = "1"u8.ToArray();

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal("invalid PKCE verifier or verifier expired", exception.Message);
    }

    [Fact]
    public async Task ATokenExchangeFailureIsWrapped()
    {
        await CreateLoggedInCallbackAsync();
        _tokens.Failure = new OidcProtocolException("oauth2: \"invalid_grant\"");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "failed to exchange authorization code: oauth2: \"invalid_grant\"",
            exception.Message);
    }

    [Fact]
    public async Task AnIdTokenWithoutAVerifierFails()
    {
        await CreateLoggedInCallbackAsync(Provider(jwksUrl: null));

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "OIDC verifier not initialized for provider okta",
            exception.Message);
    }

    [Fact]
    public async Task AnIdTokenVerificationFailureIsWrapped()
    {
        await CreateLoggedInCallbackAsync();
        _verifier.Failure = new OidcProtocolException("oidc: token is expired");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "failed to verify ID token: oidc: token is expired",
            exception.Message);
    }

    [Fact]
    public async Task AProviderWithNeitherIdTokenNorUserInfoUrlIsRejected()
    {
        await CreateCallbackStateAsync();
        _tokens.Result = new OidcTokenResult("access-1", "Bearer", null);

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "no id_token and no user_info_url provided for provider okta",
            exception.Message);
    }

    [Fact]
    public async Task UserInfoFillsTheSubjectForAnOAuth2OnlyProvider()
    {
        await CreateAsync(Provider(jwksUrl: null, userInfoUrl: "https://idp.example.com/userinfo"));
        _cache.Entries[OidcLoginService.StateKeyPrefix + "state-1"] = "1"u8.ToArray();
        _tokens.Result = new OidcTokenResult("access-1", "Bearer", null);
        _userInfo.Result = new OidcUserInfo(
            new OidcClaims
            {
                Sub = "subject-9",
                Email = "sso@example.com",
                EmailVerified = true,
                Name = "SSO User",
            },
            new Dictionary<string, object?>());

        var result = await _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl);

        Assert.Equal("login", result.Intent);
        var request = Assert.Single(_userInfo.Requests);
        Assert.False(request.UseDiscovery);
        Assert.Equal("https://idp.example.com/userinfo", request.Endpoint);
        Assert.Equal("access-1", request.AccessToken);
        Assert.Equal("Bearer", request.TokenType);
        var (user, _, identity) = Assert.Single(_store.CreationRequests);
        Assert.Equal("sso@example.com", user.Email);
        Assert.Equal("subject-9", identity.Subject);
        Assert.Empty(_verifier.Requests);
    }

    [Fact]
    public async Task NonEmptyUserInfoClaimsOverrideTheIdTokenClaims()
    {
        await CreateLoggedInCallbackAsync(Provider(userInfoUrl: "https://idp.example.com/userinfo"));
        _verifier.Result = new OidcVerifiedIdToken(
            "subject-1",
            new Dictionary<string, object?>
            {
                ["email"] = "id@example.com",
                ["email_verified"] = false,
                ["name"] = "Id Name",
            });
        _userInfo.Result = new OidcUserInfo(
            new OidcClaims
            {
                Email = string.Empty,
                EmailVerified = true,
                Name = "User Info Name",
                Picture = "https://cdn.example.com/avatar.png",
            },
            new Dictionary<string, object?>());

        await _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl);

        var (user, _, _) = Assert.Single(_store.CreationRequests);
        Assert.Equal("id@example.com", user.Email);
        // Reference: strings.SplitN(name, " ", 2) — the first token is the
        // first name, the remainder (inner spaces kept) is the last name.
        Assert.Equal("User", user.FirstName);
        Assert.Equal("Info Name", user.LastName);
        Assert.Equal("https://cdn.example.com/avatar.png", user.Avatar);
    }

    [Fact]
    public async Task AUserInfoFailureWithAnIdTokenOnlyLogsAWarning()
    {
        await CreateLoggedInCallbackAsync(Provider(userInfoUrl: "https://idp.example.com/userinfo"));
        _userInfo.Failure = new OidcProtocolException("userinfo request failed with status: 500");

        var result = await _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl);

        Assert.Equal("login", result.Intent);
        var (user, _, _) = Assert.Single(_store.CreationRequests);
        Assert.Equal("new@example.com", user.Email);
    }

    [Fact]
    public async Task AUserInfoFailureWithoutAnIdTokenIsAnOAuth2OnlyError()
    {
        await CreateAsync(Provider(jwksUrl: null, userInfoUrl: "https://idp.example.com/userinfo"));
        _cache.Entries[OidcLoginService.StateKeyPrefix + "state-1"] = "1"u8.ToArray();
        _tokens.Result = new OidcTokenResult("access-1", "Bearer", null);
        _userInfo.Failure = new OidcProtocolException("userinfo request failed with status: 500");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "failed to fetch user info for OAuth2-only provider: "
            + "userinfo request failed with status: 500",
            exception.Message);
    }

    [Fact]
    public async Task AnEmptySubjectFailsTheCallback()
    {
        await CreateLoggedInCallbackAsync();
        _verifier.Result = new OidcVerifiedIdToken(
            string.Empty,
            new Dictionary<string, object?>());

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "failed to resolve subject from id_token or user_info",
            exception.Message);
    }

    [Fact]
    public async Task ALinkCallbackCreatesTheIdentityAndReturnsTheLinkIntent()
    {
        await CreateLoggedInCallbackAsync();
        _cache.Entries[OidcLoginService.LinkStateKeyPrefix + "state-1"] = "42"u8.ToArray();

        var result = await _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl);

        Assert.Equal(string.Empty, result.ExchangeCode);
        Assert.Equal("link", result.Intent);
        var identity = Assert.Single(_store.Identities);
        Assert.Equal(42, identity.UserId);
        Assert.Equal("subject-1", identity.Subject);
        Assert.Equal("new@example.com", identity.Email);
        Assert.Equal("Okta", identity.IdpName);
        Assert.Equal(Start.UtcDateTime, identity.LastLoginAt);
        Assert.Empty(_store.CreationRequests);
        Assert.DoesNotContain(
            OidcLoginService.LinkStateKeyPrefix + "state-1",
            _cache.Entries.Keys);
    }

    [Fact]
    public async Task AnInvalidCachedLinkUserIdFails()
    {
        await CreateLoggedInCallbackAsync();
        _cache.Entries[OidcLoginService.LinkStateKeyPrefix + "state-1"] = "abc"u8.ToArray();

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal(
            "invalid cached link user ID: strconv.Atoi: parsing \"abc\": invalid syntax",
            exception.Message);
    }

    [Fact]
    public async Task ALinkIdentityFailureIsWrapped()
    {
        await CreateLoggedInCallbackAsync();
        _cache.Entries[OidcLoginService.LinkStateKeyPrefix + "state-1"] = "42"u8.ToArray();
        _store.CreateIdentityFailure = new InvalidOperationException("duplicate key");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal("failed to link identity: duplicate key", exception.Message);
    }

    [Fact]
    public async Task ADeactivatedUserCannotLogIn()
    {
        await CreateLoggedInCallbackAsync();
        var user = _store.AddUser(row =>
        {
            row.Email = "linked@example.com";
            row.Status = "deactivated";
        });
        _store.AddIdentity(identity =>
        {
            identity.Issuer = "https://issuer.example.com";
            identity.Subject = "subject-1";
            identity.UserId = user.Id;
        });

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.CallbackAsync("okta", "code-1", "state-1", BaseUrl));

        Assert.Equal("User account is deactivated", exception.Message);
    }

    [Fact]
    public async Task AnExistingIdentityIsTouchedAndReturned()
    {
        await CreateAsync(Provider(syncUserInfo: false));
        var user = _store.AddUser(row => row.Email = "linked@example.com");
        _store.AddIdentity(identity =>
        {
            identity.Id = 5;
            identity.Issuer = "https://issuer.example.com";
            identity.Subject = "sub-1";
            identity.UserId = user.Id;
        });

        var resolved = await _login.ResolveUserAsync(
            Runtime(Provider(syncUserInfo: false)),
            "sub-1",
            new OidcClaims());

        Assert.Same(user, resolved);
        var touch = Assert.Single(_store.LastLoginTouches);
        Assert.Equal(5, touch.IdentityId);
        Assert.Equal(Start.UtcDateTime, touch.LastLoginAt);
        Assert.Empty(_store.SyncUpdates);
    }

    [Fact]
    public async Task AFailedLastLoginUpdateIsIgnored()
    {
        await CreateAsync(Provider());
        var user = _store.AddUser(row => row.Email = "linked@example.com");
        _store.AddIdentity(identity =>
        {
            identity.Issuer = "https://issuer.example.com";
            identity.Subject = "sub-1";
            identity.UserId = user.Id;
        });
        _store.TouchFailure = new InvalidOperationException("locked");

        var resolved = await _login.ResolveUserAsync(
            Runtime(Provider()),
            "sub-1",
            new OidcClaims());

        Assert.Same(user, resolved);
    }

    [Fact]
    public async Task AnIdentityWhoseUserWasDeletedFails()
    {
        await CreateAsync(Provider());
        _store.AddIdentity(identity =>
        {
            identity.Issuer = "https://issuer.example.com";
            identity.Subject = "sub-1";
            identity.UserId = 999;
        });

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ResolveUserAsync(Runtime(Provider()), "sub-1", new OidcClaims()));

        Assert.Equal(
            "OIDC identity linked to missing or deleted user",
            exception.Message);
    }

    [Fact]
    public async Task AnIdentityLookupFailureIsWrapped()
    {
        await CreateAsync(Provider());
        _store.IdentityLookupFailure = new InvalidOperationException("db down");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ResolveUserAsync(Runtime(Provider()), "sub-1", new OidcClaims()));

        Assert.Equal(
            "database error querying identity: db down",
            exception.Message);
    }

    [Fact]
    public async Task AVerifiedEmailAutoLinksTheExistingAccount()
    {
        await CreateAsync(Provider());
        var existing = _store.AddUser(row => row.Email = "known@example.com");
        var provider = Provider(autoLinkByEmail: true);

        var resolved = await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-9",
            new OidcClaims { Email = "known@example.com", EmailVerified = true });

        Assert.Same(existing, resolved);
        var identity = Assert.Single(_store.Identities);
        Assert.Equal(existing.Id, identity.UserId);
        Assert.Equal("sub-9", identity.Subject);
        Assert.Equal("known@example.com", identity.Email);
        Assert.Equal("okta", identity.IdpName);
        Assert.Equal("https://issuer.example.com", identity.Issuer);
        Assert.Equal(Start.UtcDateTime, identity.LastLoginAt);
        Assert.Empty(_store.CreationRequests);
    }

    [Fact]
    public async Task AnUnverifiedEmailDoesNotAutoLink()
    {
        await CreateAsync(Provider());
        var existing = _store.AddUser(row => row.Email = "known@example.com");
        var provider = Provider(autoLinkByEmail: true);

        var resolved = await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-9",
            new OidcClaims { Email = "known@example.com", EmailVerified = false });

        // The JIT branch still uses the claimed email, but it creates a new
        // user instead of linking the existing one.
        Assert.NotEqual(existing.Id, resolved.Id);
        Assert.Equal("known@example.com", resolved.Email);
        var (_, _, identity) = Assert.Single(_store.CreationRequests);
        Assert.Equal("Okta", identity.IdpName);
    }

    [Fact]
    public async Task AutoLinkSyncUpdatesTheExistingUser()
    {
        await CreateAsync(Provider());
        var existing = _store.AddUser(row => row.Email = "known@example.com");
        var provider = Provider(autoLinkByEmail: true, syncUserInfo: true);

        var resolved = await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-9",
            new OidcClaims
            {
                Email = "known@example.com",
                EmailVerified = true,
                Name = "Known User",
                Picture = "https://cdn.example.com/k.png",
            });

        Assert.Same(existing, resolved);
        Assert.Equal("Known", resolved.FirstName);
        Assert.Equal("User", resolved.LastName);
        Assert.Equal("https://cdn.example.com/k.png", resolved.Avatar);
        Assert.Single(_store.SyncUpdates);
    }

    [Fact]
    public async Task JitDisabledRejectsAnUnknownAccount()
    {
        await CreateAsync(Provider());
        var provider = Provider(jitEnabled: false);

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ResolveUserAsync(
                Runtime(provider),
                "sub-1",
                new OidcClaims { Email = "new@example.com", EmailVerified = true }));

        Assert.Equal(
            "account not found and JIT provisioning is disabled",
            exception.Message);
    }

    [Fact]
    public async Task AnUnverifiedEmailIsRejectedWhenVerificationIsRequired()
    {
        await CreateAsync(Provider());
        var provider = Provider(requireEmailVerified: true);

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ResolveUserAsync(
                Runtime(provider),
                "sub-1",
                new OidcClaims { Email = "new@example.com" }));

        Assert.Equal("email not verified", exception.Message);
    }

    [Fact]
    public async Task AJitUserGetsTheDerivedEmailNamesAvatarScopesAndOwnerFlag()
    {
        await CreateAsync(Provider());
        var provider = Provider(
            displayName: "Okta SSO",
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "owners", DbRole = "system:owner" },
                new OidcRoleMappingRule { MatchGroup = "readers", DbRole = " scope:read_users " },
                new OidcRoleMappingRule { MatchGroup = "devs", DbRole = " developer " },
            ]);

        var resolved = await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-1",
            new OidcClaims
            {
                GivenName = "Given",
                FamilyName = "Family",
                Picture = "https://cdn.example.com/avatar.png",
                Groups = ["owners", "readers", "devs"],
            });

        Assert.Equal("sub-1@okta.oidc", resolved.Email);
        Assert.Equal("Given", resolved.FirstName);
        Assert.Equal("Family", resolved.LastName);
        Assert.Equal("https://cdn.example.com/avatar.png", resolved.Avatar);
        Assert.Equal("!OIDC_SSO_ONLY!", resolved.Password);
        Assert.True(resolved.IsOwner);
        Assert.Equal("[\"read_users\"]", resolved.Scopes);

        var (user, roles, identity) = Assert.Single(_store.CreationRequests);
        Assert.Same(resolved, user);
        Assert.Equal(["developer"], roles);
        Assert.Equal("Okta SSO", identity.IdpName);
    }

    [Fact]
    public async Task DefaultScopesAndRolesApplyWhenNoGroupMatches()
    {
        await CreateAsync(Provider());
        var provider = Provider(
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ],
            defaultRoles: ["viewer"],
            defaultScopes: ["read_users"]);

        var resolved = await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-1",
            new OidcClaims { Email = "new@example.com", Groups = ["unmatched"] });

        Assert.False(resolved.IsOwner);
        Assert.Equal("[\"read_users\"]", resolved.Scopes);
        var (_, roles, _) = Assert.Single(_store.CreationRequests);
        Assert.Equal(["viewer"], roles);
    }

    [Fact]
    public async Task HighestPrecedenceKeepsOnlyTheTopPriorityRule()
    {
        await CreateAsync(Provider());
        var provider = Provider(
            rolePrecedenceMode: "highest",
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "team", DbRole = "low", Priority = 1 },
                new OidcRoleMappingRule { MatchGroup = "team", DbRole = "high", Priority = 10 },
            ]);

        await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-1",
            new OidcClaims { Email = "new@example.com", Groups = ["team"] });

        var (_, roles, _) = Assert.Single(_store.CreationRequests);
        Assert.Equal(["high"], roles);
    }

    [Fact]
    public async Task GlobAndRegexRulesMatchTheGroups()
    {
        await CreateAsync(Provider());
        var provider = Provider(
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "team-*", DbRole = "dev" },
                new OidcRoleMappingRule
                {
                    MatchGroup = "^admin$",
                    IsRegex = true,
                    DbRole = "admin",
                },
            ]);

        await _login.ResolveUserAsync(
            Runtime(provider),
            "sub-1",
            new OidcClaims { Email = "new@example.com", Groups = ["team-a", "admin"] });

        var (_, roles, _) = Assert.Single(_store.CreationRequests);
        Assert.Equal(["dev", "admin"], roles);
    }

    [Fact]
    public async Task AJitCreateFailureKeepsTheReferencePrefix()
    {
        await CreateAsync(Provider());
        _store.CreateUserWithIdentityFailure = new OidcLoginStoreException(
            "user",
            "insert failed",
            new InvalidOperationException("insert failed"));

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ResolveUserAsync(
                Runtime(Provider()),
                "sub-1",
                new OidcClaims { Email = "new@example.com" }));

        Assert.Equal("failed to create user: insert failed", exception.Message);
    }

    [Fact]
    public async Task AJitIdentityFailureKeepsTheReferencePrefix()
    {
        await CreateAsync(Provider());
        _store.CreateUserWithIdentityFailure = new OidcLoginStoreException(
            "identity",
            "duplicate key",
            new InvalidOperationException("duplicate key"));

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ResolveUserAsync(
                Runtime(Provider()),
                "sub-1",
                new OidcClaims { Email = "new@example.com" }));

        Assert.Equal(
            "failed to create identity for new user: duplicate key",
            exception.Message);
    }

    [Fact]
    public async Task TheJitNameIsSplitOnTheFirstSpace()
    {
        await CreateAsync(Provider());

        var resolved = await _login.ResolveUserAsync(
            Runtime(Provider()),
            "sub-1",
            new OidcClaims { Email = "new@example.com", Name = "Van Der Berg" });

        Assert.Equal("Van", resolved.FirstName);
        Assert.Equal("Der Berg", resolved.LastName);
    }

    [Fact]
    public async Task SyncAlwaysReplacesTheMappedRoles()
    {
        await CreateAsync(Provider());
        var admin = _store.AddRole("admin", id: 3);
        var other = _store.AddRole("other", id: 9);
        var user = _store.AddUser();
        _store.UserRoles[user.Id] = [other.Id];
        var provider = Provider(
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        var updated = await _login.SyncUserInfoAsync(
            user,
            "New Name",
            string.Empty,
            string.Empty,
            string.Empty,
            ["admins"],
            provider);

        Assert.Equal("New", updated.FirstName);
        Assert.Equal("Name", updated.LastName);
        var update = Assert.Single(_store.SyncUpdates);
        Assert.True(update.ClearRoles);
        Assert.Equal([admin.Id], update.AddRoleIds);
        Assert.Equal([admin.Id], _store.UserRoles[user.Id]);
    }

    [Fact]
    public async Task SyncMergeKeepsTheExistingRoles()
    {
        await CreateAsync(Provider());
        var admin = _store.AddRole("admin", id: 3);
        var other = _store.AddRole("other", id: 9);
        var user = _store.AddUser();
        _store.UserRoles[user.Id] = [other.Id];
        var provider = Provider(
            syncRoleStrategy: "merge",
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        await _login.SyncUserInfoAsync(
            user,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ["admins"],
            provider);

        var update = Assert.Single(_store.SyncUpdates);
        Assert.False(update.ClearRoles);
        Assert.Equal([admin.Id], update.AddRoleIds);
        Assert.Equal([other.Id, admin.Id], _store.UserRoles[user.Id]);
    }

    [Fact]
    public async Task ManualFirstKeepsRolesThatWereAssignedByHand()
    {
        await CreateAsync(Provider());
        _store.AddRole("admin", id: 3);
        var manual = _store.AddRole("manual", id: 9);
        var user = _store.AddUser();
        _store.UserRoles[user.Id] = [manual.Id];
        var provider = Provider(
            syncRoleStrategy: "manual_first",
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        await _login.SyncUserInfoAsync(
            user,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ["admins"],
            provider);

        var update = Assert.Single(_store.SyncUpdates);
        Assert.Empty(update.AddRoleIds);
        Assert.False(update.ClearRoles);
        Assert.Equal([manual.Id], _store.UserRoles[user.Id]);
    }

    [Fact]
    public async Task ManualFirstAddsRolesWhenTheUserHasNone()
    {
        await CreateAsync(Provider());
        var admin = _store.AddRole("admin", id: 3);
        var user = _store.AddUser();
        var provider = Provider(
            syncRoleStrategy: "manual_first",
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        await _login.SyncUserInfoAsync(
            user,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ["admins"],
            provider);

        var update = Assert.Single(_store.SyncUpdates);
        Assert.Equal([admin.Id], update.AddRoleIds);
    }

    [Fact]
    public async Task CreateOnlySkipsTheRoleMappingOnSync()
    {
        await CreateAsync(Provider());
        _store.AddRole("admin", id: 3);
        var user = _store.AddUser();
        var provider = Provider(
            syncRoleStrategy: "create_only",
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        await _login.SyncUserInfoAsync(
            user,
            "New Name",
            string.Empty,
            string.Empty,
            "https://cdn.example.com/a.png",
            ["admins"],
            provider);

        Assert.Empty(_store.RoleQueries);
        var update = Assert.Single(_store.SyncUpdates);
        Assert.Null(update.IsOwner);
        Assert.False(update.ClearRoles);
        Assert.Empty(update.AddRoleIds);
        Assert.Equal("New", update.FirstName);
        Assert.Equal("https://cdn.example.com/a.png", update.Avatar);
    }

    [Fact]
    public async Task AFailedRoleQueryIsIgnored()
    {
        await CreateAsync(Provider());
        var user = _store.AddUser();
        _store.ListRolesFailure = new InvalidOperationException("db down");
        var provider = Provider(
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        await _login.SyncUserInfoAsync(
            user,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ["admins"],
            provider);

        var update = Assert.Single(_store.SyncUpdates);
        Assert.False(update.ClearRoles);
        Assert.Empty(update.AddRoleIds);
    }

    [Fact]
    public async Task WithoutMatchingGroupsTheAlwaysStrategyClearsRoles()
    {
        await CreateAsync(Provider());
        var user = _store.AddUser();
        var provider = Provider(
            roleMappings:
            [
                new OidcRoleMappingRule { MatchGroup = "admins", DbRole = "admin" },
            ]);

        await _login.SyncUserInfoAsync(
            user,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            [],
            provider);

        var update = Assert.Single(_store.SyncUpdates);
        Assert.True(update.ClearRoles);
        Assert.Empty(update.AddRoleIds);
    }

    [Fact]
    public async Task ASyncForADeletedUserFails()
    {
        await CreateAsync(Provider());
        var user = _store.AddUser();
        user.DeletedAt = Start.ToUnixTimeSeconds();

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.SyncUserInfoAsync(
                user,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                [],
                Provider()));

        Assert.Equal("ent: user not found", exception.Message);
    }

    [Fact]
    public async Task AnExchangeCodeIsSingleUse()
    {
        await CreateAsync(Provider());
        var user = _store.AddUser();
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] =
            System.Text.Encoding.UTF8.GetBytes(user.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        var resolved = await _login.ExchangeCodeAsync("code-1");

        Assert.Same(user, resolved);
        Assert.DoesNotContain(
            OidcLoginService.ExchangeKeyPrefix + "code-1",
            _cache.Entries.Keys);

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ExchangeCodeAsync("code-1"));
        Assert.Equal("invalid or expired exchange code", exception.Message);
    }

    [Fact]
    public async Task AnUnknownExchangeCodeFails()
    {
        await CreateAsync(Provider());

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ExchangeCodeAsync("missing"));

        Assert.Equal("invalid or expired exchange code", exception.Message);
    }

    [Fact]
    public async Task AnExchangeCacheFailureFailsAsAnInvalidCode()
    {
        await CreateAsync(Provider());
        _cache.GetFailure = new InvalidOperationException("boom");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ExchangeCodeAsync("code-1"));

        Assert.Equal("invalid or expired exchange code", exception.Message);
    }

    [Fact]
    public async Task AMalformedExchangeUseridFails()
    {
        await CreateAsync(Provider());
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] = "abc"u8.ToArray();

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ExchangeCodeAsync("code-1"));

        Assert.Equal(
            "invalid user ID format in cache: strconv.Atoi: parsing \"abc\": invalid syntax",
            exception.Message);
    }

    [Fact]
    public async Task AnExchangeForADeletedUserFails()
    {
        await CreateAsync(Provider());
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] = "99"u8.ToArray();

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ExchangeCodeAsync("code-1"));

        Assert.Equal("user not found: ent: user not found", exception.Message);
    }

    [Fact]
    public async Task AnExchangeStoreFailureIsWrapped()
    {
        await CreateAsync(Provider());
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] = "99"u8.ToArray();
        _store.UserByIdFailure = new InvalidOperationException("db down");

        var exception = await Assert.ThrowsAsync<OidcFlowException>(
            () => _login.ExchangeCodeAsync("code-1"));

        Assert.Equal("user not found: db down", exception.Message);
    }

    private async Task CreateLoggedInCallbackAsync(OidcProviderConfig? provider = null)
    {
        await CreateCallbackStateAsync(provider);
        _tokens.Result = new OidcTokenResult("access-1", "Bearer", "raw.id.token");
        _verifier.Result = new OidcVerifiedIdToken(
            "subject-1",
            new Dictionary<string, object?>
            {
                ["email"] = "new@example.com",
                ["email_verified"] = true,
                ["name"] = "New User",
            });
    }

    private async Task CreateCallbackStateAsync(OidcProviderConfig? provider = null)
    {
        await CreateAsync(provider ?? Provider());
        _cache.Entries[OidcLoginService.StateKeyPrefix + "state-1"] = "1"u8.ToArray();
    }

    private async Task CreateAsync(params OidcProviderConfig[] providers)
    {
        _registry = new OidcProviderRegistry(
            providers,
            _http,
            _clock,
            NullLogger<OidcProviderRegistry>.Instance);
        await _registry.InitializeAsync();
        _login = new OidcLoginService(
            _registry,
            _cache,
            _tokens,
            _userInfo,
            _verifier,
            _http,
            _store,
            _clock,
            NullLogger<OidcLoginService>.Instance);
    }

    private static OidcProviderRuntime Runtime(OidcProviderConfig config)
    {
        var normalized = config.Normalize();
        return new OidcProviderRuntime(
            normalized,
            "https://idp.example.com/auth",
            "https://idp.example.com/token",
            null,
            config.JwksUrl.Length > 0 ? config.JwksUrl : null,
            HasDiscovery: false,
            HasVerifier: config.JwksUrl.Length > 0,
            OidcProviderRegistry.DefaultAuthorizeRedirectPath,
            OidcProviderRegistry.DefaultScopes);
    }

    private static OidcProviderConfig Provider(
        string id = "okta",
        string clientId = "client-1",
        string clientSecret = "secret-1",
        string? authUrl = "https://idp.example.com/auth",
        string? tokenUrl = "https://idp.example.com/token",
        string? issuer = "https://issuer.example.com",
        string? issuerUrl = null,
        string? jwksUrl = "https://idp.example.com/jwks",
        string? userInfoUrl = null,
        string? redirectUrl = null,
        string displayName = "Okta",
        bool enablePkce = false,
        bool jitEnabled = true,
        bool autoLinkByEmail = false,
        bool requireEmailVerified = false,
        bool syncUserInfo = false,
        string syncRoleStrategy = "",
        string rolePrecedenceMode = "",
        IReadOnlyList<OidcRoleMappingRule>? roleMappings = null,
        IReadOnlyList<string>? defaultRoles = null,
        IReadOnlyList<string>? defaultScopes = null)
        => new()
        {
            Id = id,
            Name = id,
            DisplayName = displayName,
            ClientId = clientId,
            ClientSecret = clientSecret,
            AuthUrl = authUrl ?? string.Empty,
            TokenUrl = tokenUrl ?? string.Empty,
            Issuer = issuer ?? string.Empty,
            IssuerUrl = issuerUrl ?? string.Empty,
            JwksUrl = jwksUrl ?? string.Empty,
            UserInfoUrl = userInfoUrl ?? string.Empty,
            RedirectUrl = redirectUrl ?? string.Empty,
            EnablePkce = enablePkce,
            JitEnabled = jitEnabled,
            AutoLinkByEmail = autoLinkByEmail,
            RequireEmailVerified = requireEmailVerified,
            SyncUserInfo = syncUserInfo,
            SyncRoleStrategy = syncRoleStrategy,
            RolePrecedenceMode = rolePrecedenceMode,
            RoleMappingRules = roleMappings ?? [],
            DefaultRoles = defaultRoles ?? [],
            DefaultScopes = defaultScopes ?? [],
        };
}
