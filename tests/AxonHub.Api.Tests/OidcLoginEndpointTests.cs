using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Auth.Oidc;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the OIDC login HTTP surface (internal/server/api/oidc.go:73-277 and
/// routes.go:101-104,131): the authorize/link/callback/exchange responses, the
/// redirect targets, gin's ShouldBindJSON behaviour on the exchange body, the
/// ent.User JSON of the exchange payload and the JWT gate in front of the link
/// route.
/// </summary>
public sealed class OidcLoginEndpointTests
{
    private const string Secret = "test-secret";
    private const string PublicUrl = "https://app.example.com";

    private static readonly DateTimeOffset Start =
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly FakeOidcCache _cache = new();
    private readonly FakeOidcTokenClient _tokens = new();
    private readonly FakeOidcUserInfoClient _userInfo = new();
    private readonly FakeOidcIdTokenVerifier _verifier = new();
    private readonly FakeOidcHttpClient _http = new();
    private readonly FakeOidcLoginStore _loginStore = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidcOnly = new();
    private readonly TestTimeProvider _clock = new(Start);

    [Fact]
    public async Task TheAuthorizeEndpointReturnsTheStateAndUrl()
    {
        await using var host = await StartAsync(Provider());

        using var response = await host.Client.GetAsync("/oauth/oidc/authorize/okta");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(raw);
        var data = document.RootElement.GetProperty("data");
        var state = data.GetProperty("state").GetString();
        var url = data.GetProperty("url").GetString();
        Assert.NotNull(state);
        Assert.NotNull(url);
        Assert.Equal(43, state.Length);
        Assert.StartsWith("https://idp.example.com/auth?", url, StringComparison.Ordinal);
        // GoJson escapes '&' the way Go's encoding/json escapes it for HTML.
        Assert.Equal(
            "{\"data\":{\"state\":\"" + state + "\",\"url\":\""
            + url.Replace("&", "\\u0026", StringComparison.Ordinal) + "\"}}",
            raw);
        Assert.Equal("1", _cache.Text(OidcLoginService.StateKeyPrefix + state));
    }

    [Fact]
    public async Task TheAuthorizeEndpointWithoutAPublicUrlReturns500()
    {
        await using var host = await StartWithPublicUrlAsync(null, Provider());

        using var response = await host.Client.GetAsync("/oauth/oidc/authorize/okta");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            OidcLoginOptions.PublicUrlRequired,
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task TheAuthorizeEndpointForAnUnknownProviderReturns500()
    {
        await using var host = await StartAsync(Provider());

        using var response = await host.Client.GetAsync("/oauth/oidc/authorize/github");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Provider not found", await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task TheLinkEndpointRequiresABearerToken()
    {
        await using var host = await StartAsync(Provider());

        using var response = await host.Client.GetAsync("/admin/oidc/link/okta");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("API key is required", await NestedErrorMessageAsync(response));
    }

    [Fact]
    public async Task TheLinkEndpointRejectsANonBearerAuthorization()
    {
        await using var host = await StartAsync(Provider());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/oidc/link/okta");
        request.Headers.TryAddWithoutValidation("Authorization", "Basic abc");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "invalid token: Authorization header must start with 'Bearer '",
            await NestedErrorMessageAsync(response));
    }

    [Fact]
    public async Task TheLinkEndpointRejectsAnInvalidJwt()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        await using var host = await StartAsync(Provider());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/oidc/link/okta");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-jwt");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Invalid token", await NestedErrorMessageAsync(response));
    }

    [Fact]
    public async Task TheLinkEndpointReturnsTheAuthorizeUrlAndCachesTheUser()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = _users.Add();
        var token = await CreateAuthService().GenerateJwtTokenAsync(user);
        await using var host = await StartAsync(Provider());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/oidc/link/okta");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var response = await host.Client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        var state = document.RootElement.GetProperty("data").GetProperty("state").GetString();
        Assert.NotNull(state);
        Assert.Equal(
            user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _cache.Text(OidcLoginService.LinkStateKeyPrefix + state));
    }

    [Fact]
    public async Task TheCallbackRedirectsToTheIdpCallbackWithTheExchangeCode()
    {
        await using var host = await StartAsync(Provider());
        await SeedLoginCallbackAsync();

        using var response = await host.Client.GetAsync(
            "/oauth/oidc/callback/okta?code=code-1&state=state-1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString();
        Assert.NotNull(location);
        Assert.StartsWith(
            PublicUrl + "/oauth/oidc/idp-callback?code=",
            location,
            StringComparison.Ordinal);
        var code = location[(PublicUrl.Length + "/oauth/oidc/idp-callback?code=".Length)..];
        Assert.Matches("^[0-9a-f]{64}$", code);
        Assert.NotNull(_cache.Text(OidcLoginService.ExchangeKeyPrefix + code));
    }

    [Fact]
    public async Task TheCallbackWithoutTheProviderSegmentUsesTheOnlyProvider()
    {
        await using var host = await StartAsync(Provider());
        await SeedLoginCallbackAsync();

        using var response = await host.Client.GetAsync(
            "/oauth/oidc/callback?code=code-1&state=state-1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            PublicUrl + "/oauth/oidc/idp-callback?code=",
            response.Headers.Location?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCallbackWithoutAProviderAndWithSeveralProvidersReturns400()
    {
        await using var host = await StartAsync(
            Provider(),
            Provider(id: "github", authUrl: "https://github/auth", tokenUrl: "https://github/token"));

        using var response = await host.Client.GetAsync(
            "/oauth/oidc/callback?code=code-1&state=state-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Provider is required", await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task AProviderErrorReturns400WithTheDescription()
    {
        await using var host = await StartAsync(Provider());

        using var response = await host.Client.GetAsync(
            "/oauth/oidc/callback/okta?error=access_denied"
            + "&error_description=The+user+denied+the+request");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "The user denied the request",
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task AMissingCodeOrStateReturns400()
    {
        await using var host = await StartAsync(Provider());

        using var response = await host.Client.GetAsync("/oauth/oidc/callback/okta?code=code-1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Code and state are required", await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task AFlowFailureRedirectsToTheIdpCallbackWithTheEscapedError()
    {
        await using var host = await StartAsync(Provider());

        using var response = await host.Client.GetAsync(
            "/oauth/oidc/callback/okta?code=code-1&state=unknown");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            PublicUrl + "/oauth/oidc/idp-callback?error=auth_failed"
            + "&error_description=invalid+or+expired+state+parameter",
            response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task ALinkCallbackRedirectsToTheProfilePage()
    {
        await using var host = await StartAsync(Provider());
        await SeedLoginCallbackAsync();
        _cache.Entries[OidcLoginService.LinkStateKeyPrefix + "state-1"] = "42"u8.ToArray();

        using var response = await host.Client.GetAsync(
            "/oauth/oidc/callback/okta?code=code-1&state=state-1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(
            PublicUrl + "/settings/profile?oidc_link=success",
            response.Headers.Location?.ToString());
        Assert.Equal(42, Assert.Single(_loginStore.Identities).UserId);
    }

    [Fact]
    public async Task TheExchangeReturnsATokenAndTheEntUserJson()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        // The exchange redeems the code through IOidcLoginStore, not IUserStore.
        var user = _loginStore.AddUser(row =>
        {
            row.CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            row.UpdatedAt = new DateTime(2026, 2, 3, 4, 5, 6, 123, DateTimeKind.Utc);
            row.Email = "user@example.com";
            row.Status = "activated";
            row.PreferLanguage = "en";
            row.FirstName = string.Empty;
            row.LastName = string.Empty;
            row.Avatar = null;
            row.IsOwner = true;
            row.Scopes = "[\"read_users\"]";
        });
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] =
            Encoding.UTF8.GetBytes(user.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, "{\"code\":\"code-1\"}");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        var data = document.RootElement.GetProperty("data");
        var token = data.GetProperty("token").GetString();
        Assert.NotNull(token);
        Assert.Equal(
            "{\"data\":{\"token\":\"" + token + "\",\"user\":{\"id\":" + user.Id
            + ",\"created_at\":\"2026-01-02T03:04:05Z\""
            + ",\"updated_at\":\"2026-02-03T04:05:06.123Z\""
            + ",\"email\":\"user@example.com\",\"status\":\"activated\""
            + ",\"prefer_language\":\"en\",\"is_owner\":true"
            + ",\"scopes\":[\"read_users\"],\"edges\":{}}}}",
            raw);
        Assert.Equal("2026-01-02T03:04:05Z", data.GetProperty("user").GetProperty("created_at").GetString());
    }

    [Theory]
    [InlineData("", "EOF")]
    [InlineData("   ", "EOF")]
    [InlineData("{", "unexpected EOF")]
    [InlineData(
        "{\"code\":1}",
        "json: cannot unmarshal number into Go struct field .code of type string")]
    [InlineData(
        "{\"code\":true}",
        "json: cannot unmarshal bool into Go struct field .code of type string")]
    [InlineData(
        "{\"code\":{}}",
        "json: cannot unmarshal object into Go struct field .code of type string")]
    [InlineData(
        "{\"code\":[]}",
        "json: cannot unmarshal array into Go struct field .code of type string")]
    public async Task TheExchangeBindingErrorsMatchGin(string body, string error)
    {
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(error, await ErrorMessageAsync(response));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"code\":\"\"}")]
    public async Task AMissingExchangeCodeFailsTheRequiredValidation(string body)
    {
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Key: 'Code' Error:Field validation for 'Code' failed on the 'required' tag",
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task ANonObjectExchangeRootUsesTheAnonymousStructTypeName()
    {
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, "[1]");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "json: cannot unmarshal array into Go value of type struct { Code string "
            + "\"json:\\\"code\\\" binding:\\\"required\\\"\" }",
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task TrailingBytesAfterTheExchangeObjectAreIgnored()
    {
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, "{\"code\":\"unknown\"} trailing");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "invalid or expired exchange code",
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task AnUnknownExchangeCodeReturns400()
    {
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, "{\"code\":\"unknown\"}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "invalid or expired exchange code",
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task AnExchangeForADeletedUserReturns500()
    {
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] = "99"u8.ToArray();
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, "{\"code\":\"code-1\"}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "user not found: ent: user not found",
            await ErrorMessageAsync(response));
    }

    [Fact]
    public async Task AJwtGenerationFailureReturns500WithTheReferencePrefix()
    {
        var user = _loginStore.AddUser();
        _cache.Entries[OidcLoginService.ExchangeKeyPrefix + "code-1"] =
            Encoding.UTF8.GetBytes(user.Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        await using var host = await StartAsync(Provider());

        using var response = await PostExchangeAsync(host.Client, "{\"code\":\"code-1\"}");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "Failed to generate token: failed to get secret key: system not "
            + "initialized: secret key not found",
            await ErrorMessageAsync(response));
    }

    private async Task SeedLoginCallbackAsync()
    {
        _cache.Entries[OidcLoginService.StateKeyPrefix + "state-1"] = "1"u8.ToArray();
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

    private ManagementAuthService CreateAuthService()
        => new(_users, _systems, _profiles, _passwords, _oidcOnly);

    private Task<TestHost> StartAsync(params OidcProviderConfig[] providers)
        => StartWithPublicUrlAsync(PublicUrl, providers);

    private async Task<TestHost> StartWithPublicUrlAsync(
        string? publicUrl,
        params OidcProviderConfig[] providers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var configuration = new Dictionary<string, string?>
        {
            ["Server:PublicUrl"] = publicUrl ?? string.Empty,
        };
        builder.Configuration.AddInMemoryCollection(configuration);

        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserProfileStore>(_profiles);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<IOidcLoginOnlyProviders>(_oidcOnly);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IOidcHttpClient>(_http);
        builder.Services.AddSingleton<IReadOnlyList<OidcProviderConfig>>(providers);
        builder.Services.AddSingleton(OidcLoginOptions.FromConfiguration(builder.Configuration));
        builder.Services.AddSingleton<OidcProviderRegistry>();
        builder.Services.AddHostedService<OidcInitializationHostedService>();
        builder.Services.AddScoped<OidcService>();
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddSingleton<IOidcCache>(_cache);
        builder.Services.AddSingleton<IOidcTokenClient>(_tokens);
        builder.Services.AddSingleton<IOidcUserInfoClient>(_userInfo);
        builder.Services.AddSingleton<IOidcIdTokenVerifier>(_verifier);
        builder.Services.AddSingleton<IOidcLoginStore>(_loginStore);
        builder.Services.AddScoped<OidcLoginService>();

        var app = builder.Build();
        app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/oidc/link"),
            static branch =>
            {
                branch.UseMiddleware<OidcLinkAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });
        app.MapOidcLoginRoutes();
        await app.StartAsync();
        return new TestHost(app);
    }

    private static async Task<HttpResponseMessage> PostExchangeAsync(
        HttpClient client,
        string body)
        => await client.PostAsync(
            "/oauth/oidc/exchange",
            new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("error").GetString() ?? string.Empty;
    }

    private static async Task<string> NestedErrorMessageAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.GetProperty("error").GetProperty("message").GetString()
            ?? string.Empty;
    }

    private static OidcProviderConfig Provider(
        string id = "okta",
        string? authUrl = "https://idp.example.com/auth",
        string? tokenUrl = "https://idp.example.com/token")
        => new()
        {
            Id = id,
            Name = id,
            DisplayName = "Okta",
            ClientId = "client-1",
            ClientSecret = "secret-1",
            AuthUrl = authUrl ?? string.Empty,
            TokenUrl = tokenUrl ?? string.Empty,
            Issuer = "https://issuer.example.com",
            JwksUrl = "https://idp.example.com/jwks",
            JitEnabled = true,
        };

    private sealed class TestHost(WebApplication app) : IAsyncDisposable
    {
        private readonly HttpClient _client = app.GetTestClient();

        public HttpClient Client => _client;

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await app.DisposeAsync();
        }
    }
}
