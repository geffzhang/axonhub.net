using AxonHub.Api.Auth.Oidc;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the oidc.providers configuration surface: the reference normalize()
/// derivations (internal/server/biz/oidc.go:100-123), identifier matching
/// (oidc.go:141-158) and the .NET binding of the snake_case YAML keys (C168).
/// </summary>
public sealed class OidcProviderConfigTests
{
    [Fact]
    public void NormalizeTrimsAndDerivesTheMissingNames()
    {
        var provider = new OidcProviderConfig
        {
            Id = "  ",
            Name = " Okta ",
            DisplayName = "   ",
        }.Normalize();

        Assert.Equal("Okta", provider.Id);
        Assert.Equal("Okta", provider.Name);
        Assert.Equal("Okta", provider.DisplayName);
    }

    [Fact]
    public void NormalizeDerivesTheIdentifierFromTheDisplayName()
    {
        var provider = new OidcProviderConfig { DisplayName = " GitHub " }.Normalize();

        Assert.Equal("GitHub", provider.Id);
        Assert.Equal("GitHub", provider.Name);
        Assert.Equal("GitHub", provider.DisplayName);
    }

    [Fact]
    public void NormalizeKeepsADistinctDisplayName()
    {
        var provider = new OidcProviderConfig
        {
            Id = "okta",
            Name = "   ",
            DisplayName = "Okta Work",
        }.Normalize();

        Assert.Equal("okta", provider.Id);
        Assert.Equal("okta", provider.Name);
        Assert.Equal("Okta Work", provider.DisplayName);
    }

    [Fact]
    public void TheIdentifierAccessorsReadThroughNormalization()
    {
        var provider = new OidcProviderConfig
        {
            Name = "Okta",
            DisplayName = "Okta Work",
        };

        Assert.Equal("Okta", provider.ProviderId);
        Assert.Equal("Okta Work", provider.ProviderDisplayName);
    }

    [Theory]
    [InlineData("okta")]
    [InlineData("OKTA")]
    [InlineData(" ok ta ")]
    [InlineData("okta corp")]
    [InlineData("OktaCorp")]
    public void MatchesIdentifierIgnoresCaseAndSpaces(string identifier)
    {
        var provider = new OidcProviderConfig { Id = "Okta", Name = "Okta Corp" };

        Assert.True(provider.MatchesIdentifier(identifier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("github")]
    public void RejectsUnknownOrEmptyIdentifiers(string identifier)
    {
        var provider = new OidcProviderConfig { Id = "Okta", Name = "Okta Corp" };

        Assert.False(provider.MatchesIdentifier(identifier));
    }

    [Fact]
    public void GetIssuerPrefersTheExplicitIssuer()
    {
        var provider = new OidcProviderConfig
        {
            IssuerUrl = "https://discovered.example.com",
            Issuer = "https://tokens.example.com",
        };

        Assert.Equal("https://tokens.example.com", provider.GetIssuer());
        Assert.Equal(
            "https://discovered.example.com",
            (provider with { Issuer = string.Empty }).GetIssuer());
    }

    [Fact]
    public void LoadReadsTheSnakeCaseProviderKeys()
    {
        var providers = Load(new Dictionary<string, string?>
        {
            ["Oidc:providers:0:id"] = "okta",
            ["Oidc:providers:0:name"] = "Okta",
            ["Oidc:providers:0:display_name"] = "Okta Work",
            ["Oidc:providers:0:issuer_url"] = "https://idp.example.com",
            ["Oidc:providers:0:client_id"] = "client",
            ["Oidc:providers:0:client_secret"] = "secret",
            ["Oidc:providers:0:extra_scopes:0"] = "openid",
            ["Oidc:providers:0:extra_scopes:1"] = "groups",
            ["Oidc:providers:0:jit_enabled"] = "true",
            ["Oidc:providers:0:auto_link_by_email"] = "true",
            ["Oidc:providers:0:require_email_verified"] = "true",
            ["Oidc:providers:0:enable_pkce"] = "true",
            ["Oidc:providers:0:icon_url"] = "https://cdn.example.com/okta.png",
            ["Oidc:providers:0:button_color"] = "#00297A",
            ["Oidc:providers:0:sync_user_info"] = "true",
            ["Oidc:providers:0:issuer"] = "https://issuer.example.com",
            ["Oidc:providers:0:auth_url"] = "https://idp.example.com/auth",
            ["Oidc:providers:0:token_url"] = "https://idp.example.com/token",
            ["Oidc:providers:0:user_info_url"] = "https://idp.example.com/userinfo",
            ["Oidc:providers:0:jwks_url"] = "https://idp.example.com/jwks",
            ["Oidc:providers:0:redirect_url"] = "/callback",
            ["Oidc:providers:0:oidc_login_only"] = "true",
            ["Oidc:providers:0:group_claims:0"] = "groups",
            ["Oidc:providers:0:group_parser:regex_replace_pattern"] = "^corp/",
            ["Oidc:providers:0:group_parser:regex_replace_with"] = "corp-",
            ["Oidc:providers:0:group_parser:case_sensitive"] = "true",
            ["Oidc:providers:0:role_mappings:0:match_group"] = "^admins$",
            ["Oidc:providers:0:role_mappings:0:is_regex"] = "true",
            ["Oidc:providers:0:role_mappings:0:db_role"] = "owner",
            ["Oidc:providers:0:role_mappings:0:priority"] = "10",
            ["Oidc:providers:0:default_roles:0"] = "viewer",
            ["Oidc:providers:0:default_scopes:0"] = "openid",
            ["Oidc:providers:0:sync_role_strategy"] = "merge",
            ["Oidc:providers:0:role_precedence_mode"] = "highest",
        });

        var provider = Assert.Single(providers);
        Assert.Equal("okta", provider.Id);
        Assert.Equal("Okta", provider.Name);
        Assert.Equal("Okta Work", provider.DisplayName);
        Assert.Equal("https://idp.example.com", provider.IssuerUrl);
        Assert.Equal("client", provider.ClientId);
        Assert.Equal("secret", provider.ClientSecret);
        Assert.Equal(["openid", "groups"], provider.ExtraScopes);
        Assert.True(provider.JitEnabled);
        Assert.True(provider.AutoLinkByEmail);
        Assert.True(provider.RequireEmailVerified);
        Assert.True(provider.EnablePkce);
        Assert.Equal("https://cdn.example.com/okta.png", provider.IconUrl);
        Assert.Equal("#00297A", provider.ButtonColor);
        Assert.True(provider.SyncUserInfo);
        Assert.Equal("https://issuer.example.com", provider.Issuer);
        Assert.Equal("https://idp.example.com/auth", provider.AuthUrl);
        Assert.Equal("https://idp.example.com/token", provider.TokenUrl);
        Assert.Equal("https://idp.example.com/userinfo", provider.UserInfoUrl);
        Assert.Equal("https://idp.example.com/jwks", provider.JwksUrl);
        Assert.Equal("/callback", provider.RedirectUrl);
        Assert.True(provider.OidcLoginOnly);
        Assert.Equal(["groups"], provider.GroupClaims);
        Assert.Equal("^corp/", provider.GroupParser.RegexReplacePattern);
        Assert.Equal("corp-", provider.GroupParser.RegexReplaceWith);
        Assert.True(provider.GroupParser.CaseSensitive);
        var rule = Assert.Single(provider.RoleMappingRules);
        Assert.Equal("^admins$", rule.MatchGroup);
        Assert.True(rule.IsRegex);
        Assert.Equal("owner", rule.DbRole);
        Assert.Equal(10, rule.Priority);
        Assert.Equal(["viewer"], provider.DefaultRoles);
        Assert.Equal(["openid"], provider.DefaultScopes);
        Assert.Equal("merge", provider.SyncRoleStrategy);
        Assert.Equal("highest", provider.RolePrecedenceMode);
    }

    [Fact]
    public void LoadAcceptsPascalCaseAliases()
    {
        var providers = Load(new Dictionary<string, string?>
        {
            ["Oidc:Providers:0:DisplayName"] = "GitHub",
            ["Oidc:Providers:0:IssuerUrl"] = "https://github.com",
            ["Oidc:Providers:0:JitEnabled"] = "true",
            ["Oidc:Providers:0:ExtraScopes:0"] = "openid",
            ["Oidc:Providers:0:GroupParser:CaseSensitive"] = "true",
            ["Oidc:Providers:0:RoleMappings:0:MatchGroup"] = "admins",
            ["Oidc:Providers:0:OidcLoginOnly"] = "true",
        });

        var provider = Assert.Single(providers);
        Assert.Equal("GitHub", provider.DisplayName);
        Assert.Equal("https://github.com", provider.IssuerUrl);
        Assert.True(provider.JitEnabled);
        Assert.Equal(["openid"], provider.ExtraScopes);
        Assert.True(provider.GroupParser.CaseSensitive);
        Assert.Equal("admins", Assert.Single(provider.RoleMappingRules).MatchGroup);
        Assert.True(provider.OidcLoginOnly);
    }

    [Fact]
    public void LoadAcceptsAScalarWhereTheReferenceUsesAList()
    {
        var providers = Load(new Dictionary<string, string?>
        {
            ["Oidc:providers:0:id"] = "okta",
            ["Oidc:providers:0:extra_scopes"] = "openid profile email",
        });

        Assert.Equal(["openid profile email"], Assert.Single(providers).ExtraScopes);
    }

    [Fact]
    public void LoadReturnsAnEmptyListWithoutProviders()
    {
        Assert.Empty(Load(new Dictionary<string, string?>()));
    }

    [Fact]
    public void AnUnsetProviderKeepsTheReferenceDefaults()
    {
        var providers = Load(new Dictionary<string, string?>
        {
            ["Oidc:providers:0:id"] = "okta",
        });

        var provider = Assert.Single(providers);
        Assert.False(provider.JitEnabled);
        Assert.False(provider.OidcLoginOnly);
        Assert.Empty(provider.ExtraScopes);
        Assert.Empty(provider.GroupClaims);
        Assert.Empty(provider.RoleMappingRules);
        Assert.Empty(provider.DefaultRoles);
        Assert.Equal(string.Empty, provider.ButtonColor);
        Assert.Equal(string.Empty, provider.SyncRoleStrategy);
        Assert.False(provider.GroupParser.CaseSensitive);
    }

    private static IReadOnlyList<OidcProviderConfig> Load(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return OidcProviderConfig.Load(configuration.GetSection("Oidc"));
    }
}
