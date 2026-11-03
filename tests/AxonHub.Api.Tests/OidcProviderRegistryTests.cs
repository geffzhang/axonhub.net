using AxonHub.Api.Auth.Oidc;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks NewOIDCService's startup checks (internal/server/biz/oidc.go:192-304):
/// normalization, duplicate detection, discovery with manual overrides, the
/// endpoint requirement, icon inlining, redirect/scopes defaults and the
/// per-provider activity bookkeeping.
/// </summary>
public sealed class OidcProviderRegistryTests
{
    private static readonly DateTimeOffset Start =
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public async Task DiscoverySuppliesTheEndpointsRedirectAndDefaultScopes()
    {
        var http = new FakeOidcHttpClient();
        http.Documents["https://idp.example.com"] = Discovery();
        var registry = CreateRegistry(
            http,
            Provider(issuerUrl: "https://idp.example.com"));

        await registry.InitializeAsync();

        Assert.Equal(["https://idp.example.com"], http.Requests);
        var runtime = registry.GetRuntimeProvider("okta");
        Assert.NotNull(runtime);
        Assert.Equal("https://idp.example.com/auth", runtime.AuthorizationEndpoint);
        Assert.Equal("https://idp.example.com/token", runtime.TokenEndpoint);
        Assert.Equal("https://idp.example.com/userinfo", runtime.UserInfoEndpoint);
        Assert.Equal("https://idp.example.com/jwks", runtime.JwksUri);
        Assert.True(runtime.HasDiscovery);
        Assert.True(runtime.HasVerifier);
        Assert.Equal("/oauth/oidc/callback", runtime.RedirectUrl);
        Assert.Equal(["openid", "profile", "email"], runtime.Scopes);
        Assert.True(registry.GetProviderStatus("okta").Active);
        Assert.Equal(1, registry.CountProviders());
    }

    [Fact]
    public async Task AManualEndpointPairOverridesDiscovery()
    {
        var http = new FakeOidcHttpClient();
        http.Documents["https://idp.example.com"] = Discovery();
        var registry = CreateRegistry(
            http,
            Provider(
                issuerUrl: "https://idp.example.com",
                authUrl: "https://manual.example.com/auth",
                tokenUrl: "https://manual.example.com/token"));

        await registry.InitializeAsync();

        var runtime = registry.GetRuntimeProvider("okta");
        Assert.NotNull(runtime);
        Assert.Equal("https://manual.example.com/auth", runtime.AuthorizationEndpoint);
        Assert.Equal("https://manual.example.com/token", runtime.TokenEndpoint);
        Assert.True(runtime.HasDiscovery);
    }

    [Fact]
    public async Task DiscoveryFailureFallsBackToTheManualEndpoints()
    {
        var http = new FakeOidcHttpClient
        {
            Failure = new OidcDiscoveryException("connection refused"),
        };
        var registry = CreateRegistry(
            http,
            Provider(
                issuerUrl: "https://idp.example.com",
                authUrl: "https://manual.example.com/auth",
                tokenUrl: "https://manual.example.com/token"));

        await registry.InitializeAsync();

        var runtime = registry.GetRuntimeProvider("okta");
        Assert.NotNull(runtime);
        Assert.Equal("https://manual.example.com/auth", runtime.AuthorizationEndpoint);
        Assert.Equal("https://manual.example.com/token", runtime.TokenEndpoint);
        Assert.False(runtime.HasDiscovery);
        Assert.False(runtime.HasVerifier);
        Assert.True(registry.GetProviderStatus("okta").Active);
    }

    [Fact]
    public async Task AManualJwksUrlAloneProvidesTheVerifier()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(
                authUrl: "https://manual.example.com/auth",
                tokenUrl: "https://manual.example.com/token",
                jwksUrl: "https://manual.example.com/jwks"));

        await registry.InitializeAsync();

        var runtime = registry.GetRuntimeProvider("okta");
        Assert.NotNull(runtime);
        Assert.True(runtime.HasVerifier);
        Assert.False(runtime.HasDiscovery);
        Assert.Null(runtime.UserInfoEndpoint);
    }

    [Fact]
    public async Task AProviderWithoutEndpointsStaysInactiveAndCarriesTheStartupCheck()
    {
        var registry = CreateRegistry(new FakeOidcHttpClient(), Provider());

        await registry.InitializeAsync();

        Assert.Null(registry.GetRuntimeProvider("okta"));
        var status = registry.GetProviderStatus("okta");
        Assert.False(status.Active);
        Assert.Equal(Start.ToUnixTimeSeconds(), status.LastCheck);

        // The provider stays in the configuration list so the login page can
        // show it as inactive.
        Assert.Equal(1, registry.CountProviders());
        var configured = Assert.Single(registry.ConfiguredProviders);
        Assert.Equal("okta", configured.Id);
        Assert.Equal("okta", configured.Name);
        Assert.Equal("okta", configured.DisplayName);
    }

    [Fact]
    public async Task ASkippedProviderKeepsTheRawIcon()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(iconUrl: "/does/not/exist.png"));

        await registry.InitializeAsync();

        Assert.Equal(
            "/does/not/exist.png",
            Assert.Single(registry.ConfiguredProviders).IconUrl);
    }

    [Fact]
    public async Task AMissingIdentifierFailsInitialization()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(),
            new OidcProviderConfig { Id = "  ", Name = " ", DisplayName = " " });

        var exception = await Assert.ThrowsAsync<OidcConfigurationException>(
            () => registry.InitializeAsync());

        Assert.Equal("OIDC provider at index 1 requires id or name", exception.Message);
    }

    [Fact]
    public async Task DuplicateNormalizedIdentifiersFailInitialization()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(id: "Okta"),
            Provider(id: "ok ta"));

        var exception = await Assert.ThrowsAsync<OidcConfigurationException>(
            () => registry.InitializeAsync());

        Assert.Equal(
            "duplicate OIDC provider id \"ok ta\" conflicts with \"Okta\"",
            exception.Message);
    }

    [Fact]
    public async Task TheDefaultRedirectIncludesTheProviderIdForMultipleProviders()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(id: "alpha", authUrl: "https://a/auth", tokenUrl: "https://a/token"),
            Provider(id: "beta", authUrl: "https://b/auth", tokenUrl: "https://b/token"));

        await registry.InitializeAsync();

        Assert.Equal(
            "/oauth/oidc/callback/alpha",
            registry.GetRuntimeProvider("alpha")?.RedirectUrl);
        Assert.Equal(
            "/oauth/oidc/callback/beta",
            registry.GetRuntimeProvider("beta")?.RedirectUrl);
    }

    [Fact]
    public async Task AnExplicitRedirectUrlIsKept()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(
                authUrl: "https://a/auth",
                tokenUrl: "https://a/token",
                redirectUrl: "https://app.example.com/oauth/callback"));

        await registry.InitializeAsync();

        Assert.Equal(
            "https://app.example.com/oauth/callback",
            registry.GetRuntimeProvider("okta")?.RedirectUrl);
    }

    [Fact]
    public async Task ExtraScopesReplaceTheDefaultScopes()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(
                authUrl: "https://a/auth",
                tokenUrl: "https://a/token",
                extraScopes: ["openid", "groups"]));

        await registry.InitializeAsync();

        Assert.Equal(["openid", "groups"], registry.GetRuntimeProvider("okta")?.Scopes);
    }

    [Fact]
    public async Task MarkProviderCheckRefreshesAtMostOncePerMinute()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(authUrl: "https://a/auth", tokenUrl: "https://a/token"));
        await registry.InitializeAsync();
        var start = Start.ToUnixTimeSeconds();

        Assert.Equal(start, registry.MarkProviderCheck("okta", start + 30));
        Assert.Equal(start, registry.GetProviderStatus("okta").LastCheck);

        Assert.Equal(start, registry.MarkProviderCheck("okta", start + 60));
        Assert.Equal(start + 60, registry.GetProviderStatus("okta").LastCheck);

        Assert.Equal(start + 60, registry.MarkProviderCheck("okta", start + 90));
    }

    [Fact]
    public async Task SetProviderReplacesTheRuntimeEntry()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(authUrl: "https://a/auth", tokenUrl: "https://a/token"));
        await registry.InitializeAsync();
        var runtime = registry.GetRuntimeProvider("okta")!;

        registry.SetProvider("okta", runtime with { TokenEndpoint = "https://b/token" });

        Assert.Equal(
            "https://b/token",
            registry.GetRuntimeProvider("okta")?.TokenEndpoint);
        Assert.True(registry.GetProviderStatus("okta").Active);
    }

    [Fact]
    public async Task IdentifierLookupCoversTheIdAndTheName()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(id: "okta", name: "Okta Corp", authUrl: "https://a/auth", tokenUrl: "https://a/token"));
        await registry.InitializeAsync();

        Assert.NotNull(registry.GetRuntimeProvider("okta"));
        Assert.NotNull(registry.GetRuntimeProvider("OKTA CORP"));
        Assert.NotNull(registry.GetRuntimeProvider("okta corp"));
        Assert.Null(registry.GetRuntimeProvider("github"));
        Assert.Null(registry.GetRuntimeProvider(string.Empty));
    }

    [Fact]
    public async Task RemoteAndDataIconsPassThroughUnchanged()
    {
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(
                authUrl: "https://a/auth",
                tokenUrl: "https://a/token",
                iconUrl: "https://cdn.example.com/okta.png"),
            Provider(
                id: "github",
                authUrl: "https://b/auth",
                tokenUrl: "https://b/token",
                iconUrl: "data:image/png;base64,AAAA"));

        await registry.InitializeAsync();

        Assert.Equal(
            "https://cdn.example.com/okta.png",
            registry.GetRuntimeProvider("okta")?.Config.IconUrl);
        Assert.Equal(
            "data:image/png;base64,AAAA",
            registry.GetRuntimeProvider("github")?.Config.IconUrl);
    }

    [Fact]
    public async Task ALocalIconFileBecomesADataUrl()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "axonhub-oidc-icon-" + Guid.NewGuid().ToString("N") + ".png");
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var registry = CreateRegistry(
                new FakeOidcHttpClient(),
                Provider(
                    authUrl: "https://a/auth",
                    tokenUrl: "https://a/token",
                    iconUrl: path));

            await registry.InitializeAsync();

            var icon = registry.GetRuntimeProvider("okta")?.Config.IconUrl;
            Assert.NotNull(icon);
            Assert.StartsWith("data:image/png;base64,", icon, StringComparison.Ordinal);
            Assert.Equal(
                bytes,
                Convert.FromBase64String(icon["data:image/png;base64,".Length..]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnExtensionlessIconIsSniffed()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "axonhub-oidc-icon-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF, 0x01]);
            var registry = CreateRegistry(
                new FakeOidcHttpClient(),
                Provider(
                    authUrl: "https://a/auth",
                    tokenUrl: "https://a/token",
                    iconUrl: path));

            await registry.InitializeAsync();

            Assert.StartsWith(
                "data:image/jpeg;base64,",
                registry.GetRuntimeProvider("okta")?.Config.IconUrl,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AnUnreadableIconFileLeavesTheRawPathAndTheProviderActive()
    {
        var missing = Path.Combine(
            Path.GetTempPath(),
            "axonhub-oidc-missing-" + Guid.NewGuid().ToString("N") + ".png");
        var registry = CreateRegistry(
            new FakeOidcHttpClient(),
            Provider(
                authUrl: "https://a/auth",
                tokenUrl: "https://a/token",
                iconUrl: missing));

        await registry.InitializeAsync();

        Assert.Equal(missing, registry.GetRuntimeProvider("okta")?.Config.IconUrl);
        Assert.True(registry.GetProviderStatus("okta").Active);
    }

    [Fact]
    public async Task InitializeIsIdempotent()
    {
        var http = new FakeOidcHttpClient();
        http.Documents["https://idp.example.com"] = Discovery();
        var registry = CreateRegistry(http, Provider(issuerUrl: "https://idp.example.com"));

        await registry.InitializeAsync();
        await registry.InitializeAsync();

        Assert.Single(http.Requests);
    }

    [Fact]
    public void LoginOnlyProvidersMatchThePreferredIssuer()
    {
        var providers = new OidcLoginOnlyProviders(
        [
            new OidcProviderConfig
            {
                Id = "okta",
                IssuerUrl = "https://idp.example.com",
                OidcLoginOnly = true,
            },
            new OidcProviderConfig
            {
                Id = "github",
                IssuerUrl = "https://github.com",
                Issuer = "https://tokens.github.com",
                OidcLoginOnly = true,
            },
            new OidcProviderConfig
            {
                Id = "google",
                IssuerUrl = "https://accounts.google.com",
            },
        ]);

        Assert.True(providers.IsLoginOnly("https://idp.example.com"));
        Assert.True(providers.IsLoginOnly("https://tokens.github.com"));
        Assert.False(providers.IsLoginOnly("https://github.com"));
        Assert.False(providers.IsLoginOnly("https://accounts.google.com"));
        Assert.False(providers.IsLoginOnly(string.Empty));
    }

    private static OidcProviderRegistry CreateRegistry(
        FakeOidcHttpClient http,
        params OidcProviderConfig[] providers)
        => new(
            providers,
            http,
            new TestTimeProvider(Start),
            NullLogger<OidcProviderRegistry>.Instance);

    private static OidcProviderConfig Provider(
        string id = "okta",
        string? name = null,
        string? issuerUrl = null,
        string? authUrl = null,
        string? tokenUrl = null,
        string? jwksUrl = null,
        string? iconUrl = null,
        string? redirectUrl = null,
        string[]? extraScopes = null)
        => new()
        {
            Id = id,
            Name = name ?? id,
            IssuerUrl = issuerUrl ?? string.Empty,
            AuthUrl = authUrl ?? string.Empty,
            TokenUrl = tokenUrl ?? string.Empty,
            JwksUrl = jwksUrl ?? string.Empty,
            IconUrl = iconUrl ?? string.Empty,
            RedirectUrl = redirectUrl ?? string.Empty,
            ExtraScopes = extraScopes ?? [],
        };

    private static OidcDiscoveryDocument Discovery(
        string issuer = "https://idp.example.com")
        => new(
            issuer,
            "https://idp.example.com/auth",
            "https://idp.example.com/token",
            "https://idp.example.com/userinfo",
            "https://idp.example.com/jwks",
            ["RS256"]);
}
