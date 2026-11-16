using System.Text.Json;
using AxonHub.Api.Scopes;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of biz.PermissionValidator
/// (internal/server/biz/permission_validator.go): validates permission
/// hierarchies for the management services. The reference reads the current
/// user from the Gin context with its Roles/ProjectUsers edges preloaded by
/// the auth middleware; the port identity seam carries only id/isOwner, so the
/// edges are fetched on demand from <see cref="IUserAdminStore"/> (C161).
/// </summary>
/// <remarks>
/// The reference iterates a Go map when reporting the first scope the current
/// user lacks, so which of several missing scopes appears in the message is
/// nondeterministic there; the port reports them in edge-load order (direct,
/// system roles, project user, project roles). Both implementations report the
/// first requested scope in input order for the grant checks.
/// </remarks>
public sealed class PermissionValidator(
    IManagementIdentity identity,
    IUserAdminStore users,
    IRoleStore roles)
{
    public const string UserNotFoundInContext = "user not found in context";

    public const string CannotEditOwnerUsers =
        "insufficient permissions: cannot edit owner users";

    public const string OnlySystemOwnersCanDeleteProjects =
        "insufficient permissions: only system owners can delete projects";

    public const string OnlySystemOwnersCanDeleteUsers =
        "insufficient permissions: only system owners can delete users";

    public const string CannotDeleteYourself = "cannot delete yourself";

    public const string UserNotFound = "ent: user not found";

    public const string RoleNotFound = "ent: role not found";

    /// <summary>Port of CanGrantScopes (permission_validator.go:96-131).</summary>
    public async Task CanGrantScopesAsync(
        IReadOnlyList<string> scopesToGrant,
        long? projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopesToGrant);

        var current = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);

        // isProjectOwner: the global owner check needs no edge data.
        if (current.IsOwner)
        {
            return;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);

        if (IsProjectOwnerEdge(loaded, projectId))
        {
            return;
        }

        var userScopes = GetUserScopes(loaded, projectId);
        var userScopeSet = new HashSet<string>(userScopes, StringComparer.Ordinal);
        foreach (var scope in scopesToGrant)
        {
            if (!userScopeSet.Contains(scope))
            {
                throw new InvalidOperationException(
                    "insufficient permissions: cannot grant scope '"
                    + scope
                    + "' that you don't possess");
            }
        }
    }

    /// <summary>Port of CanGrantRole (permission_validator.go:134-136).</summary>
    public Task CanGrantRoleAsync(
        IReadOnlyList<string> roleScopes,
        long? projectId,
        CancellationToken cancellationToken = default)
        => CanGrantScopesAsync(roleScopes, projectId, cancellationToken);

    public Task CanAccessProjectScopeAsync(
        string scope,
        long projectId,
        CancellationToken cancellationToken = default)
        => CanGrantScopesAsync([scope], projectId, cancellationToken);

    /// <summary>
    /// Port of CanGrantScopes for an already loaded user
    /// (permission_validator.go:96-131). APIKeyService.requireProjectAdmin
    /// loads the creator through a system bypass and validates that snapshot
    /// instead of the context user (biz/api_key.go:396-411).
    /// </summary>
    internal static void CanGrantScopesForUser(
        PermissionUser? currentUser,
        IReadOnlyList<string> scopesToGrant,
        long? projectId)
    {
        ArgumentNullException.ThrowIfNull(scopesToGrant);

        if (currentUser is null)
        {
            throw new InvalidOperationException(UserNotFoundInContext);
        }

        if (projectId is { } ownerProjectId
            && UserIsProjectOwner(currentUser, ownerProjectId))
        {
            return;
        }

        var userScopeSet = new HashSet<string>(
            GetUserScopes(currentUser, projectId),
            StringComparer.Ordinal);
        foreach (var scope in scopesToGrant)
        {
            if (!userScopeSet.Contains(scope))
            {
                throw new InvalidOperationException(
                    "insufficient permissions: cannot grant scope '"
                    + scope
                    + "' that you don't possess");
            }
        }
    }

    /// <summary>
    /// Port of CanGrantAPIKeyScopes (permission_validator.go:134-158): a
    /// service-account key carries scopes without a project dimension, so a
    /// system-only scope requires a system-level grant before the regular
    /// project-capable grant check runs.
    /// </summary>
    internal static void CanGrantApiKeyScopesForUser(
        PermissionUser? currentUser,
        IReadOnlyList<string> scopesToGrant,
        long projectId)
    {
        ArgumentNullException.ThrowIfNull(scopesToGrant);

        foreach (var scope in scopesToGrant)
        {
            if (!ScopeCatalog.IsValidScope(scope))
            {
                throw new InvalidOperationException("invalid scope '" + scope + "'");
            }

            if (ScopeCatalog.IsSystemOnlyScope(scope)
                && !HasSystemScope(currentUser, scope))
            {
                throw new InvalidOperationException(
                    "insufficient permissions: cannot grant system scope '"
                    + scope
                    + "' that you don't possess at system level");
            }
        }

        CanGrantScopesForUser(currentUser, scopesToGrant, projectId);
    }

    /// <summary>Port of scopes.HasSystemScope (internal/scopes/rule.go:45-60).</summary>
    internal static bool HasSystemScope(PermissionUser? user, string requiredScope)
    {
        if (user is null)
        {
            return false;
        }

        // Owner has all permissions.
        if (user.User.IsOwner)
        {
            return true;
        }

        if (ParseScopes(user.User.Scopes).Contains(requiredScope, StringComparer.Ordinal))
        {
            return true;
        }

        // System-role scopes: Ent's Role.IsSystemRole() is project_id nil/0.
        foreach (var role in user.Roles)
        {
            if (role.ProjectId is null or 0
                && ParseScopes(role.Scopes).Contains(requiredScope, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of scopes.userHasProjectScope
    /// (internal/scopes/rule_user_project_scope.go:15-45).
    /// </summary>
    internal static bool UserHasProjectScope(
        PermissionUser? user,
        long projectId,
        string requiredScope)
    {
        if (user is null)
        {
            return false;
        }

        if (user.User.IsOwner)
        {
            return true;
        }

        // Only the first matching membership is consulted, like lo.Find.
        var membership = user.ProjectUsers
            .FirstOrDefault(projectUser => projectUser.ProjectId == projectId);
        if (membership is null)
        {
            return false;
        }

        if (membership.IsOwner)
        {
            return true;
        }

        if (ParseScopes(membership.Scopes).Contains(requiredScope, StringComparer.Ordinal))
        {
            return true;
        }

        foreach (var role in user.Roles)
        {
            if (role.ProjectId == projectId
                && ParseScopes(role.Scopes).Contains(requiredScope, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of scopes.userIsProjectOwner (rule_user_project_scope.go:47-62).</summary>
    internal static bool UserIsProjectOwner(PermissionUser? user, long projectId)
    {
        if (user is null)
        {
            return false;
        }

        if (user.User.IsOwner)
        {
            return true;
        }

        return user.ProjectUsers.Any(
            membership => membership.ProjectId == projectId && membership.IsOwner);
    }

    public async Task<PermissionUser?> GetPermissionUserAsync(
        long userId,
        CancellationToken cancellationToken = default)
        => await users.GetPermissionUserAsync(userId, cancellationToken).ConfigureAwait(false);

    /// <summary>Port of CanEditUserPermissions (permission_validator.go:140-214).</summary>
    public async Task CanEditUserPermissionsAsync(
        long targetUserId,
        long? projectId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);

        PermissionUser? target;
        try
        {
            target = await users.GetPermissionUserAsync(targetUserId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to get target user: " + ex.Message, ex);
        }

        if (target is null)
        {
            throw new InvalidOperationException("failed to get target user: " + UserNotFound);
        }

        // Check if the target is an owner (global or project-specific).
        if (target.User.IsOwner || IsProjectOwnerEdge(target, projectId))
        {
            // The current user must be an owner to edit another owner.
            if (!await CurrentIsProjectOwnerAsync(current, projectId, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(CannotEditOwnerUsers);
            }

            return;
        }

        // Owners can edit anyone.
        if (await CurrentIsProjectOwnerAsync(current, projectId, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        var currentLoaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        var currentScopeSet = new HashSet<string>(
            GetUserScopes(currentLoaded, projectId),
            StringComparer.Ordinal);

        foreach (var scope in GetUserScopes(target, projectId))
        {
            if (!currentScopeSet.Contains(scope))
            {
                throw new InvalidOperationException(
                    "insufficient permissions: target user has scope '"
                    + scope
                    + "' that you don't possess");
            }
        }
    }

    /// <summary>Port of CanEditRole (permission_validator.go:217-230).</summary>
    public async Task CanEditRoleAsync(
        long roleId,
        long? projectId,
        CancellationToken cancellationToken = default)
    {
        RoleRecord? role;
        try
        {
            role = await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to get role: " + ex.Message, ex);
        }

        if (role is null)
        {
            throw new InvalidOperationException("failed to get role: " + RoleNotFound);
        }

        if (projectId is null && !role.IsSystemRole)
        {
            projectId = role.ProjectId;
        }

        await CanGrantRoleAsync(ParseScopes(role.Scopes), projectId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of CanDeleteProject (permission_validator.go:234-246).</summary>
    public async Task CanDeleteProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        _ = projectId;

        var current = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!current.IsOwner)
        {
            throw new InvalidOperationException(OnlySystemOwnersCanDeleteProjects);
        }
    }

    /// <summary>Port of CanDeleteUser (permission_validator.go:250-267).</summary>
    public async Task CanDeleteUserAsync(
        long targetUserId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);

        // Cannot delete yourself.
        if (current.Id == targetUserId)
        {
            throw new InvalidOperationException(CannotDeleteYourself);
        }

        // Only system owners can delete users.
        if (!current.IsOwner)
        {
            throw new InvalidOperationException(OnlySystemOwnersCanDeleteUsers);
        }
    }

    private async Task<ManagementUser> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(UserNotFoundInContext);
        }

        return current;
    }

    /// <summary>Port of isProjectOwner for an already loaded permission user.</summary>
    private static bool IsProjectOwnerEdge(PermissionUser? user, long? projectId)
        => projectId is not null
            && user is not null
            && user.ProjectUsers.Any(up => up.ProjectId == projectId.Value && up.IsOwner);

    /// <summary>Port of isProjectOwner for the current (context) user.</summary>
    private async Task<bool> CurrentIsProjectOwnerAsync(
        ManagementUser current,
        long? projectId,
        CancellationToken cancellationToken)
    {
        if (current.IsOwner)
        {
            return true;
        }

        if (projectId is null)
        {
            return false;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        return IsProjectOwnerEdge(loaded, projectId);
    }

    /// <summary>
    /// Port of getUserScopes (permission_validator.go:27-72): direct scopes,
    /// system-role scopes, then — when a project is in scope — the matching
    /// project-user scopes and project-role scopes.
    /// </summary>
    internal static List<string> GetUserScopes(PermissionUser? user, long? projectId)
    {
        var scopes = new List<string>();
        if (user is null)
        {
            return scopes;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        AddScopes(scopes, seen, ParseScopes(user.User.Scopes));

        foreach (var role in user.Roles)
        {
            if (role.IsSystemRole)
            {
                AddScopes(scopes, seen, ParseScopes(role.Scopes));
            }
        }

        if (projectId is not null)
        {
            foreach (var projectUser in user.ProjectUsers)
            {
                if (projectUser.ProjectId == projectId.Value)
                {
                    AddScopes(scopes, seen, ParseScopes(projectUser.Scopes));
                    break;
                }
            }

            foreach (var role in user.Roles)
            {
                if (!role.IsSystemRole && role.ProjectId == projectId.Value)
                {
                    AddScopes(scopes, seen, ParseScopes(role.Scopes));
                }
            }
        }

        return scopes;
    }

    private static void AddScopes(
        List<string> scopes,
        HashSet<string> seen,
        IReadOnlyList<string> additions)
    {
        foreach (var scope in additions)
        {
            if (seen.Add(scope))
            {
                scopes.Add(scope);
            }
        }
    }

    /// <summary>Ent stores scopes as a JSON string array; nil means none.</summary>
    internal static List<string> ParseScopes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
