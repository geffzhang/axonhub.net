namespace AxonHub.Api.Scopes;

/// <summary>
/// Scope levels (internal/scopes/scopes.go:63-70). The GraphQL admin surface
/// carries them as strings, exactly like the gqlgen SDL.
/// </summary>
public static class ScopeLevels
{
    /// <summary>System-wide operations.</summary>
    public const string System = "system";

    /// <summary>Project-scoped operations.</summary>
    public const string Project = "project";
}

/// <summary>
/// The static permission catalog (internal/scopes/scopes.go:76-202). The order
/// is the reference's declaration order and is part of the <c>allScopes</c>
/// wire contract.
/// </summary>
public static class ScopeCatalog
{
    private static readonly ScopeDefinition[] Configs =
    [
        new("read_dashboard", "View dashboard", [ScopeLevels.System]),
        new("read_settings", "View system settings", [ScopeLevels.System]),
        new("write_settings", "Manage system settings", [ScopeLevels.System]),
        new("read_channels", "View channel information", [ScopeLevels.System]),
        new("write_channels", "Manage channels/models (create, edit, delete)", [ScopeLevels.System]),
        new("read_data_storages", "View data storage information", [ScopeLevels.System]),
        new("write_data_storages", "Manage data storages (create, edit, delete)", [ScopeLevels.System]),
        new("read_users", "View user information", [ScopeLevels.System, ScopeLevels.Project]),
        new("write_users", "Manage users (create, edit, delete)", [ScopeLevels.System, ScopeLevels.Project]),
        new("read_roles", "View role information", [ScopeLevels.System, ScopeLevels.Project]),
        new("write_roles", "Manage roles (create, edit, delete)", [ScopeLevels.System, ScopeLevels.Project]),
        new("read_projects", "View project information", [ScopeLevels.System]),
        new("write_projects", "Manage projects (create, edit, delete)", [ScopeLevels.System]),
        new("read_api_keys", "View API keys", [ScopeLevels.System, ScopeLevels.Project]),
        new("write_api_keys", "Manage API keys (create, edit, delete)", [ScopeLevels.System, ScopeLevels.Project]),
        new("read_requests", "View request records", [ScopeLevels.System, ScopeLevels.Project]),
        new("write_requests", "Manage request records", [ScopeLevels.System, ScopeLevels.Project]),
        new("read_prompts", "View prompts", [ScopeLevels.System, ScopeLevels.Project]),
        new("write_prompts", "Manage prompts (create, edit, delete)", [ScopeLevels.System, ScopeLevels.Project]),
    ];

    /// <summary>
    /// Port of scopes.AllScopes (internal/scopes/scopes.go:205-220): a nil level
    /// keeps every entry; any other value filters by level membership, so an
    /// unknown level yields an empty list.
    /// </summary>
    public static IReadOnlyList<ScopeDefinition> AllScopes(string? level = null)
    {
        if (level is null)
        {
            return Configs;
        }

        return [.. Configs.Where(scope => scope.Levels.Contains(level, StringComparer.Ordinal))];
    }
}

/// <summary>Port of scopes.Scope.</summary>
public sealed record ScopeDefinition(
    string Slug,
    string Description,
    IReadOnlyList<string> Levels);
