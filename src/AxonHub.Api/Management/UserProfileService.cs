using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of gql's UpdateMeInput (me.graphql:31-36).</summary>
public sealed record UpdateMeCommand
{
    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? PreferLanguage { get; init; }

    public string? Avatar { get; init; }
}

/// <summary>Port of gql's UpdateMyPasswordInput (me.graphql:47-50).</summary>
public sealed record UpdateMyPasswordCommand
{
    public string? OldPassword { get; init; }

    public required string NewPassword { get; init; }
}

/// <summary>Port of objects.RoleInfo.</summary>
public sealed record UserProfileRole(string Name);

/// <summary>Port of objects.UserProjectInfo.</summary>
public sealed record UserProfileProject(
    long ProjectId,
    bool IsOwner,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> EffectiveScopes,
    IReadOnlyList<UserProfileRole> Roles);

/// <summary>Port of objects.OIDCIdentityInfo.</summary>
public sealed record UserProfileOidcIdentity(
    long Id,
    string IdpName,
    string Issuer,
    string Subject,
    string Email);

/// <summary>
/// Port of objects.UserInfo as produced by ConvertUserToUserInfo
/// (internal/server/biz/user.go:296-394).
/// </summary>
public sealed record UserProfileInfo(
    long Id,
    string Email,
    string FirstName,
    string LastName,
    bool IsOwner,
    string PreferLanguage,
    string Avatar,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<UserProfileRole> Roles,
    IReadOnlyList<UserProfileProject> Projects,
    IReadOnlyList<UserProfileOidcIdentity> OidcIdentities,
    bool HasPassword);

/// <summary>
/// Port of the self-service resolvers of me.graphql: Query.me / Query.myProjects /
/// Mutation.updateMe / Mutation.updateMyPassword / Mutation.unlinkOIDCIdentity
/// (me.resolvers.go) plus the read side of UserService.GetUserByID and
/// UserService.UpdateOwnProfile (user.go:169-220, 247-394).
/// </summary>
/// <remarks>
/// The reference reads the current user from the Gin context. The port resolves
/// it through <see cref="IManagementIdentity"/>, which defaults to the owner
/// account until admin login is ported (C161). The reference's unordered
/// collections (map keys and eager-loaded edges) are returned in a deterministic
/// order here (C164).
/// </remarks>
public sealed class UserProfileService(
    IUserProfileStore store,
    IManagementIdentity identity,
    IPasswordHasher passwords)
{
    /// <summary>Port of queryResolver.Me.</summary>
    public async Task<UserProfileInfo> GetMeAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        try
        {
            // GetUserByID loads the user together with roles, project users and
            // OIDC identities; every failure keeps its two prefixes.
            var user = await store.GetUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("ent: user not found");

            var roles = await store.ListUserRolesAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            var userProjects = await store.ListUserProjectsAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            var oidcIdentities = await store.ListOidcIdentitiesAsync(
                    current.Id,
                    cancellationToken)
                .ConfigureAwait(false);

            return BuildUserInfo(user, roles, userProjects, oidcIdentities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get user details: failed to get user: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of queryResolver.MyProjects.</summary>
    public async Task<IReadOnlyList<ProjectRecord>> GetMyProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        return await store.ListActiveProjectsAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of mutationResolver.UpdateMe + UserService.UpdateOwnProfile.</summary>
    public async Task<UserRecord> UpdateMeAsync(
        UpdateMeCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        UserRecord? updated;
        try
        {
            updated = await store.UpdateOwnProfileAsync(
                    new OwnProfileUpdate
                    {
                        Id = current.Id,
                        FirstName = command.FirstName,
                        LastName = command.LastName,
                        PreferLanguage = command.PreferLanguage,
                        Avatar = command.Avatar,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update user profile: " + ex.Message,
                ex);
        }

        return updated ?? throw new InvalidOperationException(
            "failed to update user profile: ent: user not found");
    }

    /// <summary>Port of mutationResolver.UpdateMyPassword.</summary>
    public async Task<bool> UpdateMyPasswordAsync(
        UpdateMyPasswordCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        var user = await store.GetUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("user not found in context");

        // OIDC-only users set their first local password without the old one.
        if (user.Password != PasswordHasher.OidcOnlyPlaceholder)
        {
            if (command.OldPassword is null)
            {
                throw new InvalidOperationException("current password is required");
            }

            try
            {
                passwords.Verify(user.Password, command.OldPassword);
            }
            catch (InvalidOperationException)
            {
                // VerifyPassword failures are all folded into this message.
                throw new InvalidOperationException("incorrect old password");
            }
        }

        // HashPassword errors are returned unwrapped by the reference.
        var hashedPassword = passwords.Hash(command.NewPassword);

        UserRecord? updated;
        try
        {
            updated = await store.UpdateOwnProfileAsync(
                    new OwnProfileUpdate
                    {
                        Id = current.Id,
                        Password = hashedPassword,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update user profile: " + ex.Message,
                ex);
        }

        if (updated is null)
        {
            throw new InvalidOperationException(
                "failed to update user profile: ent: user not found");
        }

        return true;
    }

    /// <summary>Port of mutationResolver.UnlinkOIDCIdentity.</summary>
    public async Task<bool> UnlinkOidcIdentityAsync(
        long identityId,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        OidcIdentityRecord? oidcIdentity;
        try
        {
            oidcIdentity = await store.GetOidcIdentityAsync(identityId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get identity: " + ex.Message, ex);
        }

        if (oidcIdentity is null)
        {
            throw new InvalidOperationException(
                "failed to get identity: ent: oidc_identity not found");
        }

        if (oidcIdentity.UserId != current.Id)
        {
            throw new InvalidOperationException(
                "permission denied: this identity does not belong to you");
        }

        var user = await store.GetUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);

        // OIDC-only users must set a local password before dropping their last
        // identity, otherwise they would be locked out.
        if (user?.Password == PasswordHasher.OidcOnlyPlaceholder)
        {
            int identityCount;
            try
            {
                identityCount = await store.CountOidcIdentitiesAsync(
                        current.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "failed to count identities: " + ex.Message,
                    ex);
            }

            if (identityCount <= 1)
            {
                throw new InvalidOperationException(
                    "please set a local password before unlinking your last OIDC identity");
            }
        }

        bool deleted;
        try
        {
            deleted = await store.UnlinkOidcIdentityAsync(identityId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to unlink identity: " + ex.Message,
                ex);
        }

        if (!deleted)
        {
            throw new InvalidOperationException(
                "failed to unlink identity: ent: oidc_identity not found");
        }

        return true;
    }

    internal static UserProfileInfo BuildUserInfo(
        UserRecord user,
        IReadOnlyList<RoleRecord> roles,
        IReadOnlyList<UserProjectRecord> userProjects,
        IReadOnlyList<OidcIdentityRecord> oidcIdentities)
    {
        // Ent's Role.IsSystemRole: project_id is null or 0. Collection order is
        // pinned by the store (C164).
        var userRoles = new List<UserProfileRole>();
        var allScopes = new SortedSet<string>(StringComparer.Ordinal);
        var projectRoles = new Dictionary<long, List<RoleRecord>>();

        foreach (var scope in ParseScopes(user.Scopes))
        {
            allScopes.Add(scope);
        }

        foreach (var role in roles)
        {
            if (role.ProjectId is null or 0)
            {
                userRoles.Add(new UserProfileRole(role.Name));

                foreach (var scope in ParseScopes(role.Scopes))
                {
                    allScopes.Add(scope);
                }

                continue;
            }

            if (!projectRoles.TryGetValue(role.ProjectId.Value, out var byProject))
            {
                byProject = [];
                projectRoles[role.ProjectId.Value] = byProject;
            }

            byProject.Add(role);
        }

        var projects = new List<UserProfileProject>(userProjects.Count);
        foreach (var userProject in userProjects)
        {
            var scopes = ParseScopes(userProject.Scopes);
            var effectiveScopes = new SortedSet<string>(scopes, StringComparer.Ordinal);
            var projectRoleInfos = new List<UserProfileRole>();

            if (projectRoles.TryGetValue(userProject.ProjectId, out var projectRoleRows))
            {
                foreach (var role in projectRoleRows)
                {
                    projectRoleInfos.Add(new UserProfileRole(role.Name));

                    foreach (var scope in ParseScopes(role.Scopes))
                    {
                        effectiveScopes.Add(scope);
                    }
                }
            }

            projects.Add(new UserProfileProject(
                userProject.ProjectId,
                userProject.IsOwner,
                scopes,
                [.. effectiveScopes],
                projectRoleInfos));
        }

        var identities = new List<UserProfileOidcIdentity>(oidcIdentities.Count);
        foreach (var oidcIdentity in oidcIdentities)
        {
            identities.Add(new UserProfileOidcIdentity(
                oidcIdentity.Id,
                oidcIdentity.IdpName ?? string.Empty,
                oidcIdentity.Issuer,
                oidcIdentity.Subject,
                oidcIdentity.Email ?? string.Empty));
        }

        return new UserProfileInfo(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.IsOwner,
            user.PreferLanguage,
            // Ent's ent.User.Avatar is a plain string; ConvertUserToUserInfo
            // takes its address, so the GraphQL value is never null.
            user.Avatar ?? string.Empty,
            [.. allScopes],
            userRoles,
            projects,
            identities,
            user.Password != PasswordHasher.OidcOnlyPlaceholder);
    }

    private static List<string> ParseScopes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }
}
