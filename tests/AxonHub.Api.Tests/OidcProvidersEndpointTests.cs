using System.Net;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Auth.Oidc;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks GET /oauth/oidc/providers (internal/server/api/oidc.go:57-71,
/// routes.go:101-104): the anonymous provider list, the optional bearer that
/// decorates linked identities, and the reference JSON shape (data wrapper,
/// snake_case fields, omitempty markers).
/// </summary>
public sealed class OidcProvidersEndpointTests
{
    private const string Secret = "test-secret";

    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidcOnly = new();
    private readonly FakeOidcHttpClient _idp = new();
    private readonly TestTimeProvider _clock =
        new(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    [Fact]
    public async Task TheProviderJsonUsesTheReferenceFieldNamesAndOrder()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        await using var host = await StartAsync(new OidcProviderConfig
        {
            Id = "okta",
            Name = "okta",
            DisplayName = "Okta Work",
            IssuerUrl = "https://idp.example.com",
            IconUrl = "https://cdn.example.com/okta.png",
            ButtonColor = "#00297A",
            JitEnabled = true,
        });

        var (status, body, raw) = await GetAsync(host.Client);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "{\"data\":[{\"id\":\"okta\",\"name\":\"okta\",\"display_name\":\"Okta Work\","
            + "\"jit_enabled\":true,\"icon_url\":\"https://cdn.example.com/okta.png\","
            + "\"button_color\":\"#00297A\",\"active\":true,\"oidc_login_only\":false,"
            + "\"is_linked\":false}]}",
            raw);
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task WithoutProvidersTheDataIsNull()
    {
        await using var host = await StartAsync();

        var (status, _, raw) = await GetAsync(host.Client);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("{\"data\":null}", raw);
    }

    [Fact]
    public async Task AnInactiveProviderCarriesTheStartupCheckTimestamp()
    {
        await using var host = await StartAsync(new OidcProviderConfig { Id = "okta" });

        var (status, body, _) = await GetAsync(host.Client);

        Assert.Equal(HttpStatusCode.OK, status);
        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.Equal("okta", provider.GetProperty("display_name").GetString());
        Assert.False(provider.GetProperty("active").GetBoolean());
        Assert.Equal(
            _clock.GetUtcNow().ToUnixTimeSeconds(),
            provider.GetProperty("last_check").GetInt64());
    }

    [Fact]
    public async Task AValidBearerDecoratesTheLinkedIdentity()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        var user = _users.Add();
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        _profiles.OidcIdentities.Add(new OidcIdentityRecord
        {
            Id = 7,
            Issuer = "https://idp.example.com",
            Subject = "sub-1",
            Email = "sso@example.com",
            UserId = user.Id,
        });
        var token = await CreateAuthService().GenerateJwtTokenAsync(user);
        await using var host = await StartAsync(DiscoveryProvider());

        var (status, body, _) = await GetAsync(host.Client, "Bearer " + token);

        Assert.Equal(HttpStatusCode.OK, status);
        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.True(provider.GetProperty("is_linked").GetBoolean());
        Assert.Equal(
            "gid://axonhub/OIDCIdentity/7",
            provider.GetProperty("linked_identity_id").GetString());
        Assert.Equal("sso@example.com", provider.GetProperty("linked_email").GetString());
    }

    [Fact]
    public async Task ALinkedIdentityWithoutEmailOmitsTheEmailField()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        var user = _users.Add();
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        _profiles.OidcIdentities.Add(new OidcIdentityRecord
        {
            Id = 7,
            Issuer = "https://idp.example.com",
            Subject = "sub-1",
            Email = null,
            UserId = user.Id,
        });
        var token = await CreateAuthService().GenerateJwtTokenAsync(user);
        await using var host = await StartAsync(DiscoveryProvider());

        var (_, body, _) = await GetAsync(host.Client, "Bearer " + token);

        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.True(provider.GetProperty("is_linked").GetBoolean());
        Assert.False(provider.TryGetProperty("linked_email", out _));
    }

    [Fact]
    public async Task AnIdentityForAnotherIssuerDoesNotLinkTheProvider()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        var user = _users.Add();
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        _profiles.OidcIdentities.Add(new OidcIdentityRecord
        {
            Id = 7,
            Issuer = "https://other.example.com",
            Subject = "sub-1",
            Email = "sso@example.com",
            UserId = user.Id,
        });
        var token = await CreateAuthService().GenerateJwtTokenAsync(user);
        await using var host = await StartAsync(DiscoveryProvider());

        var (_, body, _) = await GetAsync(host.Client, "Bearer " + token);

        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.False(provider.GetProperty("is_linked").GetBoolean());
        Assert.False(provider.TryGetProperty("linked_identity_id", out _));
        Assert.False(provider.TryGetProperty("linked_email", out _));
    }

    [Fact]
    public async Task AnInvalidBearerTokenIsIgnored()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        await using var host = await StartAsync(DiscoveryProvider());

        var (status, body, _) = await GetAsync(host.Client, "Bearer not-a-jwt");

        Assert.Equal(HttpStatusCode.OK, status);
        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.False(provider.GetProperty("is_linked").GetBoolean());
    }

    [Fact]
    public async Task ANonBearerAuthorizationHeaderIsIgnored()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        await using var host = await StartAsync(DiscoveryProvider());

        var (status, body, _) = await GetAsync(host.Client, "Basic abc");

        Assert.Equal(HttpStatusCode.OK, status);
        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.False(provider.GetProperty("is_linked").GetBoolean());
    }

    [Fact]
    public async Task AnIdentityLookupFailureLeavesTheListAnonymous()
    {
        _idp.Documents["https://idp.example.com"] = Discovery();
        var user = _users.Add();
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        _profiles.ListOidcIdentitiesError = new InvalidOperationException("db down");
        var token = await CreateAuthService().GenerateJwtTokenAsync(user);
        await using var host = await StartAsync(DiscoveryProvider());

        var (status, body, _) = await GetAsync(host.Client, "Bearer " + token);

        Assert.Equal(HttpStatusCode.OK, status);
        var provider = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.False(provider.GetProperty("is_linked").GetBoolean());
    }

    private static OidcProviderConfig DiscoveryProvider() => new()
    {
        Id = "okta",
        Name = "okta",
        IssuerUrl = "https://idp.example.com",
    };

    private static OidcDiscoveryDocument Discovery() => new(
        "https://idp.example.com",
        "https://idp.example.com/auth",
        "https://idp.example.com/token",
        "https://idp.example.com/userinfo",
        "https://idp.example.com/jwks",
        ["RS256"]);

    private ManagementAuthService CreateAuthService()
        => new(_users, _systems, _profiles, _passwords, _oidcOnly);

    private async Task<TestHost> StartAsync(params OidcProviderConfig[] providers)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserProfileStore>(_profiles);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<IOidcLoginOnlyProviders>(_oidcOnly);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IOidcHttpClient>(_idp);
        builder.Services.AddSingleton<IReadOnlyList<OidcProviderConfig>>(providers);
        builder.Services.AddSingleton<OidcProviderRegistry>();
        builder.Services.AddHostedService<OidcInitializationHostedService>();
        builder.Services.AddScoped<OidcService>();
        builder.Services.AddScoped<ManagementAuthService>();

        var app = builder.Build();
        RequestDelegate handler = context => OidcHandlers.GetProvidersAsync(
            context,
            context.RequestServices.GetRequiredService<OidcService>(),
            context.RequestServices.GetRequiredService<ManagementAuthService>(),
            context.RequestAborted);
        app.MapGet("/oauth/oidc/providers", handler);
        await app.StartAsync();
        return new TestHost(app);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> GetAsync(
        HttpClient client,
        string? authorization = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/oauth/oidc/providers");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.Clone(), raw);
    }

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
