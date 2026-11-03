using AxonHub.Llm.Transformer.Shared;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of biz.OIDCProvider (internal/server/biz/oidc.go:53-98): one entry of
/// the <c>oidc.providers</c> configuration section, with the reference's
/// snake_case keys.
/// </summary>
/// <remarks>
/// The reference binds these fields from YAML/viper with <c>conf</c> and
/// <c>yaml</c> tags. The port reads the same snake_case keys from the .NET
/// configuration tree (<c>Oidc:Providers:0:id</c>, ...); the PascalCase
/// spelling of each key is accepted as well (C168).
/// </remarks>
public sealed record OidcProviderConfig
{
    /// <summary><c>id</c>.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary><c>name</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary><c>display_name</c>.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary><c>issuer_url</c>: enables discovery when non-empty.</summary>
    public string IssuerUrl { get; init; } = string.Empty;

    /// <summary><c>client_id</c>.</summary>
    public string ClientId { get; init; } = string.Empty;

    /// <summary><c>client_secret</c>.</summary>
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary><c>extra_scopes</c> (defaults to openid, profile, email).</summary>
    public IReadOnlyList<string> ExtraScopes { get; init; } = [];

    /// <summary><c>jit_enabled</c>.</summary>
    public bool JitEnabled { get; init; }

    /// <summary><c>auto_link_by_email</c>.</summary>
    public bool AutoLinkByEmail { get; init; }

    /// <summary><c>require_email_verified</c>.</summary>
    public bool RequireEmailVerified { get; init; }

    /// <summary><c>enable_pkce</c>.</summary>
    public bool EnablePkce { get; init; }

    /// <summary><c>icon_url</c>: http(s), data: URI, or local file path.</summary>
    public string IconUrl { get; init; } = string.Empty;

    /// <summary><c>button_color</c>.</summary>
    public string ButtonColor { get; init; } = string.Empty;

    /// <summary><c>sync_user_info</c>.</summary>
    public bool SyncUserInfo { get; init; }

    /// <summary><c>issuer</c>: manual override used by the verifier.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary><c>auth_url</c>.</summary>
    public string AuthUrl { get; init; } = string.Empty;

    /// <summary><c>token_url</c>.</summary>
    public string TokenUrl { get; init; } = string.Empty;

    /// <summary><c>user_info_url</c>.</summary>
    public string UserInfoUrl { get; init; } = string.Empty;

    /// <summary><c>jwks_url</c>.</summary>
    public string JwksUrl { get; init; } = string.Empty;

    /// <summary><c>redirect_url</c>.</summary>
    public string RedirectUrl { get; init; } = string.Empty;

    /// <summary><c>oidc_login_only</c>.</summary>
    public bool OidcLoginOnly { get; init; }

    /// <summary><c>group_claims</c> (defaults to groups, roles).</summary>
    public IReadOnlyList<string> GroupClaims { get; init; } = [];

    /// <summary><c>group_parser</c>.</summary>
    public OidcGroupParserConfig GroupParser { get; init; } = new();

    /// <summary><c>role_mappings</c>.</summary>
    public IReadOnlyList<OidcRoleMappingRule> RoleMappingRules { get; init; } = [];

    /// <summary><c>default_roles</c>.</summary>
    public IReadOnlyList<string> DefaultRoles { get; init; } = [];

    /// <summary><c>default_scopes</c>.</summary>
    public IReadOnlyList<string> DefaultScopes { get; init; } = [];

    /// <summary><c>sync_role_strategy</c>: always, create_only, merge, manual_first.</summary>
    public string SyncRoleStrategy { get; init; } = string.Empty;

    /// <summary><c>role_precedence_mode</c>: merge or highest.</summary>
    public string RolePrecedenceMode { get; init; } = string.Empty;

    /// <summary>Port of <c>OIDCProvider.normalize</c> (oidc.go:100-123).</summary>
    public OidcProviderConfig Normalize()
    {
        var id = GoStrings.TrimSpace(Id);
        var name = GoStrings.TrimSpace(Name);
        var displayName = GoStrings.TrimSpace(DisplayName);

        if (id.Length == 0)
        {
            id = name.Length > 0 ? name : displayName;
        }

        if (name.Length == 0)
        {
            name = id;
        }

        if (displayName.Length == 0)
        {
            displayName = name;
        }

        return this with { Id = id, Name = name, DisplayName = displayName };
    }

    /// <summary>Port of <c>OIDCProvider.providerID</c>.</summary>
    public string ProviderId => Normalize().Id;

    /// <summary>Port of <c>OIDCProvider.providerDisplayName</c>.</summary>
    public string ProviderDisplayName => Normalize().DisplayName;

    /// <summary>Port of <c>OIDCProvider.issuer</c>: Issuer wins over IssuerURL.</summary>
    public string GetIssuer() => Issuer.Length > 0 ? Issuer : IssuerUrl;

    /// <summary>Port of <c>OIDCProvider.matchesIdentifier</c> (oidc.go:145-158).</summary>
    public bool MatchesIdentifier(string identifier)
    {
        var normalizedIdentifier = NormalizeIdentifier(identifier);
        if (normalizedIdentifier.Length == 0)
        {
            return false;
        }

        foreach (var candidate in new[] { ProviderId, Name })
        {
            if (string.Equals(
                    NormalizeIdentifier(candidate),
                    normalizedIdentifier,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of <c>normalizeOIDCProviderIdentifier</c> (oidc.go:141-143).</summary>
    public static string NormalizeIdentifier(string value)
        => GoStrings.TrimSpace(value)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

    /// <summary>Binds the <c>oidc</c> configuration section (viper <c>providers</c>).</summary>
    public static IReadOnlyList<OidcProviderConfig> Load(IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);

        var providers = new List<OidcProviderConfig>();
        foreach (var child in section.GetSection("providers").GetChildren())
        {
            providers.Add(Bind(child));
        }

        return providers;
    }

    private static OidcProviderConfig Bind(IConfigurationSection section)
        => new()
        {
            Id = GetString(section, "id"),
            Name = GetString(section, "name"),
            DisplayName = GetString(section, "display_name"),
            IssuerUrl = GetString(section, "issuer_url"),
            ClientId = GetString(section, "client_id"),
            ClientSecret = GetString(section, "client_secret"),
            ExtraScopes = GetList(section, "extra_scopes"),
            JitEnabled = GetBool(section, "jit_enabled"),
            AutoLinkByEmail = GetBool(section, "auto_link_by_email"),
            RequireEmailVerified = GetBool(section, "require_email_verified"),
            EnablePkce = GetBool(section, "enable_pkce"),
            IconUrl = GetString(section, "icon_url"),
            ButtonColor = GetString(section, "button_color"),
            SyncUserInfo = GetBool(section, "sync_user_info"),
            Issuer = GetString(section, "issuer"),
            AuthUrl = GetString(section, "auth_url"),
            TokenUrl = GetString(section, "token_url"),
            UserInfoUrl = GetString(section, "user_info_url"),
            JwksUrl = GetString(section, "jwks_url"),
            RedirectUrl = GetString(section, "redirect_url"),
            OidcLoginOnly = GetBool(section, "oidc_login_only"),
            GroupClaims = GetList(section, "group_claims"),
            GroupParser = BindGroupParser(GetContainer(section, "group_parser")),
            RoleMappingRules = BindRoleMappings(GetContainer(section, "role_mappings")),
            DefaultRoles = GetList(section, "default_roles"),
            DefaultScopes = GetList(section, "default_scopes"),
            SyncRoleStrategy = GetString(section, "sync_role_strategy"),
            RolePrecedenceMode = GetString(section, "role_precedence_mode"),
        };

    private static OidcGroupParserConfig BindGroupParser(IConfigurationSection section)
        => new()
        {
            RegexReplacePattern = GetString(section, "regex_replace_pattern"),
            RegexReplaceWith = GetString(section, "regex_replace_with"),
            CaseSensitive = GetBool(section, "case_sensitive"),
        };

    private static List<OidcRoleMappingRule> BindRoleMappings(IConfigurationSection section)
    {
        var rules = new List<OidcRoleMappingRule>();
        foreach (var child in section.GetChildren())
        {
            rules.Add(new OidcRoleMappingRule
            {
                MatchGroup = GetString(child, "match_group"),
                IsRegex = GetBool(child, "is_regex"),
                DbRole = GetString(child, "db_role"),
                Priority = GetInt(child, "priority"),
            });
        }

        return rules;
    }

    /// <summary>
    /// Reads a snake_case key, falling back to the PascalCase spelling so a
    /// .NET-native configuration file can use either (C168).
    /// </summary>
    private static string GetString(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (value is null)
        {
            value = section[ToPascalCase(key)];
        }

        return value ?? string.Empty;
    }

    /// <summary>
    /// Resolves a nested section or list by its snake_case key, falling back to
    /// the PascalCase spelling (C168).
    /// </summary>
    private static IConfigurationSection GetContainer(IConfigurationSection section, string key)
    {
        var child = section.GetSection(key);
        if (child.Value is not null || child.GetChildren().Any())
        {
            return child;
        }

        return section.GetSection(ToPascalCase(key));
    }

    private static string ToPascalCase(string key)
        => string.Concat(key.Split('_').Select(
            static (part, index) => index == 0
                ? part
                : char.ToUpperInvariant(part[0]) + part[1..]));

    private static bool GetBool(IConfigurationSection section, string key)
        => bool.TryParse(GetString(section, key), out var value) && value;

    private static int GetInt(IConfigurationSection section, string key)
        => int.TryParse(GetString(section, key), out var value) ? value : 0;

    private static List<string> GetList(IConfigurationSection section, string key)
    {
        var listSection = GetContainer(section, key);
        var values = listSection.GetChildren()
            .Select(static child => child.Value)
            .OfType<string>()
            .ToList();

        if (values.Count > 0)
        {
            return values;
        }

        // A single scalar value is accepted as a one-element list (the
        // reference's YAML list is the only documented shape; C168).
        return listSection.Value is { Length: > 0 } scalar ? [scalar] : [];
    }
}

/// <summary>Port of biz.GroupParserConfig (oidc.go:94-98).</summary>
public sealed record OidcGroupParserConfig
{
    /// <summary><c>regex_replace_pattern</c>.</summary>
    public string RegexReplacePattern { get; init; } = string.Empty;

    /// <summary><c>regex_replace_with</c>.</summary>
    public string RegexReplaceWith { get; init; } = string.Empty;

    /// <summary><c>case_sensitive</c>.</summary>
    public bool CaseSensitive { get; init; }
}

/// <summary>Port of biz.RoleMappingRule (oidc.go:87-92).</summary>
public sealed record OidcRoleMappingRule
{
    /// <summary><c>match_group</c>.</summary>
    public string MatchGroup { get; init; } = string.Empty;

    /// <summary><c>is_regex</c>.</summary>
    public bool IsRegex { get; init; }

    /// <summary><c>db_role</c>.</summary>
    public string DbRole { get; init; } = string.Empty;

    /// <summary><c>priority</c>.</summary>
    public int Priority { get; init; }
}
