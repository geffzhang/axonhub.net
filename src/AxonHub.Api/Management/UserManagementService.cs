using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of the fields UserService.CreateUser reads from ent.CreateUserInput.</summary>
public sealed record CreateUserCommand
{
    public required string Email { get; init; }

    public required string Password { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<long>? RoleIds { get; init; }
}

/// <summary>Port of the fields UserService.UpdateUser applies from ent.UpdateUserInput.</summary>
public sealed record UpdateUserCommand
{
    public required long Id { get; init; }

    public string? Email { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? PreferLanguage { get; init; }

    public string? Avatar { get; init; }

    public bool ClearAvatar { get; init; }

    public bool? IsOwner { get; init; }

    public string? Password { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<string>? AppendScopes { get; init; }

    public bool ClearScopes { get; init; }

    public IReadOnlyList<long>? AddRoleIds { get; init; }

    public IReadOnlyList<long>? RemoveRoleIds { get; init; }

    public bool ClearRoles { get; init; }
}

/// <summary>Port of ent.AddUserToProjectInput.</summary>
public sealed record AddProjectUserCommand
{
    public required long UserId { get; init; }

    public required long ProjectId { get; init; }

    public bool? IsOwner { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<long> RoleIds { get; init; } = [];
}

/// <summary>Port of ent.UpdateProjectUserInput.</summary>
public sealed record UpdateProjectUserCommand
{
    public required long UserId { get; init; }

    public required long ProjectId { get; init; }

    public bool? IsOwner { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<long> AddRoleIds { get; init; } = [];

    public IReadOnlyList<long> RemoveRoleIds { get; init; } = [];
}

/// <summary>
/// Port of the management methods of biz.UserService
/// (internal/server/biz/user.go:62-642) over <see cref="IUserAdminStore"/>.
/// </summary>
public sealed class UserManagementService(
    IUserAdminStore users,
    IPasswordHasher passwords,
    PermissionValidator validator,
    IManagementIdentity identity,
    IRoleStore roles,
    UserPrivacy privacy,
    UserProjectPrivacy userProjectPrivacy,
    RolePrivacy rolePrivacy)
{
    /// <summary>Port of UserService.CreateUser.</summary>
    public async Task<UserRecord> CreateAsync(
        CreateUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // HashPassword failures are returned unwrapped by the reference.
        var hashedPassword = passwords.Hash(command.Password);

        var now = DateTime.UtcNow;
        var user = new UserRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Email = command.Email,
            // Ent's create defaults: status "activated", prefer_language "en",
            // first_name/last_name "", is_owner false.
            Status = "activated",
            PreferLanguage = "en",
            Password = hashedPassword,
            FirstName = command.FirstName ?? string.Empty,
            LastName = command.LastName ?? string.Empty,
            Scopes = command.Scopes is null
                ? null
                : JsonSerializer.Serialize(command.Scopes),
        };

        long id;
        try
        {
            // The ent create runs the User mutation policy before inserting.
            await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);
            id = await users.CreateAsync(user, command.RoleIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to create user: " + ex.Message, ex);
        }

        return await users.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("failed to get created user: ent: user not found");
    }

    /// <summary>Port of UserService.UpdateUser (user.go:87-166).</summary>
    public async Task<UserRecord> UpdateAsync(
        UpdateUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validate permissions before updating (user.go:88-107).
        try
        {
            await validator.CanEditUserPermissionsAsync(command.Id, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw PermissionDenied(ex);
        }

        // Validate scope grants if scopes are being updated.
        if (command.Scopes is not null)
        {
            try
            {
                await validator.CanGrantScopesAsync(command.Scopes, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw PermissionDenied(ex);
            }
        }

        // Validate role grants if roles are being added.
        if (command.AddRoleIds is not null)
        {
            foreach (var roleId in command.AddRoleIds)
            {
                try
                {
                    await validator.CanEditRoleAsync(roleId, null, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw PermissionDenied(ex);
                }
            }
        }

        string? hashedPassword = null;
        if (command.Password is not null)
        {
            // HashPassword failures are returned unwrapped by the reference.
            hashedPassword = passwords.Hash(command.Password);
        }

        UserRecord? updated;
        try
        {
            // The ent UpdateOneID runs the User mutation policy before the update.
            await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);
            updated = await users.UpdateAsync(
                    new UserUpdate
                    {
                        Id = command.Id,
                        Email = command.Email,
                        FirstName = command.FirstName,
                        LastName = command.LastName,
                        PreferLanguage = command.PreferLanguage,
                        Avatar = command.Avatar,
                        ClearAvatar = command.ClearAvatar,
                        IsOwner = command.IsOwner,
                        Password = hashedPassword,
                        Scopes = command.Scopes,
                        AppendScopes = command.AppendScopes,
                        ClearScopes = command.ClearScopes,
                        AddRoleIds = command.AddRoleIds,
                        RemoveRoleIds = command.RemoveRoleIds,
                        ClearRoles = command.ClearRoles,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to update user: " + ex.Message, ex);
        }

        return updated
            ?? throw new InvalidOperationException("failed to update user: ent: user not found");
    }

    /// <summary>Port of UserService.UpdateUserStatus.</summary>
    public async Task<UserRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        UserRecord? updated;
        try
        {
            // The ent UpdateOneID runs the User mutation policy before the update.
            await privacy.EvaluateMutationAsync(cancellationToken).ConfigureAwait(false);
            updated = await users.UpdateStatusAsync(id, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's final wording.
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to update user status: " + ex.Message,
                ex);
        }

        return updated
            ?? throw new InvalidOperationException("failed to update user status: ent: user not found");
    }

    /// <summary>Port of UserService.DeleteUser (user.go:590-642).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        // Validate permissions before deleting (user.go:591-594).
        try
        {
            await validator.CanDeleteUserAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw PermissionDenied(ex);
        }

        UserRecord? user;
        try
        {
            user = await users.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to get user: " + ex.Message, ex);
        }

        if (user is null)
        {
            throw new InvalidOperationException("failed to get user: ent: user not found");
        }

        if (user.IsOwner)
        {
            throw new InvalidOperationException(
                "cannot delete owner user, transfer ownership first");
        }

        bool deleted;
        try
        {
            deleted = await users.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's final wording.
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to delete user: " + ex.Message, ex);
        }

        if (!deleted)
        {
            throw new InvalidOperationException("failed to delete user: ent: user not found");
        }
    }

    /// <summary>Port of UserService.AddUserToProject (user.go:394-461).</summary>
    public async Task<UserProjectRecord> AddUserToProjectAsync(
        AddProjectUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The reference skips every permission check when the principal is a
        // test principal (authz.WithTestBypass, user.go:396-397).
        var principal = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (principal is null || !principal.IsTest)
        {
            if (command.Scopes is not null)
            {
                try
                {
                    await validator
                        .CanGrantScopesAsync(command.Scopes, command.ProjectId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw PermissionDenied(ex);
                }
            }

            foreach (var roleId in command.RoleIds)
            {
                // The reference loads the role under a system bypass
                // (user.go:404-406); the port's management read path has no
                // privacy rules, so a plain store read is the same seam.
                RoleRecord? role;
                try
                {
                    role = await roles.GetByIdAsync(roleId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // NOT wrapped in "permission denied" in the reference
                    // (user.go:411).
                    throw new InvalidOperationException(
                        $"failed to load project role {roleId}: " + ex.Message,
                        ex);
                }

                if (role is null || role.ProjectId != command.ProjectId)
                {
                    // Reference wording for the ent.IsNotFound branch
                    // (user.go:409); ProjectIDEQ also rejects system roles
                    // whose project_id is NULL.
                    throw new InvalidOperationException($"project role {roleId} not found");
                }

                try
                {
                    await validator
                        .CanGrantRoleAsync(ParseScopes(role.Scopes), command.ProjectId, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw PermissionDenied(ex);
                }
            }
        }

        // The reference evaluates these policies inside its transaction, in
        // this order: UserProject.Create → (when roles are added) User.Get →
        // User.Update().AddRoleIDs (user.go:420-452). The store runs each
        // callback at that position, so a policy deny and a store failure keep
        // the reference's precedence (for example, a duplicate membership
        // fails at the insert before the User read policy runs).
        UserProjectRecord? created;
        try
        {
            created = await users.AddProjectUserAsync(
                    command.UserId,
                    command.ProjectId,
                    command.IsOwner,
                    command.Scopes,
                    command.RoleIds,
                    cancellationToken,
                    beforeCreate: ct => userProjectPrivacy.EvaluateMutationAsync(
                        UserProjectMutationOp.Create,
                        command.ProjectId,
                        ct),
                    beforeUserRead: ct => privacy.GetVisibleUserOrThrowAsync(
                        command.UserId,
                        ct),
                    beforeUserRoleUpdate: privacy.EvaluateMutationAsync)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's final wording for
            // each transaction step (user.go:436-448).
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to add user to project: " + ex.Message,
                ex);
        }

        // The reference invalidates its user cache here (user.go:458); the
        // port's management read path has no cache (C173).
        return created;
    }

    /// <summary>Port of UserService.RemoveUserFromProject (user.go:463-500).</summary>
    public async Task RemoveUserFromProjectAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        // UserProject.Delete runs the UserProject mutation policy first
        // (user.go:468-473); its WhereP(project_id = ctx) narrows the delete.
        long? mutationScope;
        try
        {
            mutationScope = await userProjectPrivacy
                .EvaluateMutationAsync(
                    UserProjectMutationOp.Delete,
                    projectId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to remove user from project: " + ex.Message,
                ex);
        }

        try
        {
            await users.RemoveProjectUserAsync(
                    userId,
                    projectId,
                    cancellationToken,
                    mutationProjectScope: mutationScope,
                    // Role.Query().IDs() runs the Role query policy
                    // (user.go:480-485); User.UpdateOneID runs the User
                    // mutation policy (user.go:489-492).
                    queryRoleScope: rolePrivacy.EvaluateQueryAsync,
                    beforeUserRoleRemoval: privacy.EvaluateMutationAsync)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's final wording for
            // each step (user.go:473-492).
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to remove user from project: " + ex.Message,
                ex);
        }

        // The reference invalidates its user cache here (user.go:497); the
        // port's management read path has no cache (C173).
    }

    /// <summary>Port of UserService.UpdateProjectUser (user.go:502-579).</summary>
    public async Task<UserProjectRecord> UpdateProjectUserAsync(
        UpdateProjectUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validate permissions before updating (user.go:505-523).
        try
        {
            await validator
                .CanEditUserPermissionsAsync(command.UserId, command.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw PermissionDenied(ex);
        }

        if (command.Scopes is not null)
        {
            try
            {
                await validator
                    .CanGrantScopesAsync(command.Scopes, command.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw PermissionDenied(ex);
            }
        }

        foreach (var roleId in command.AddRoleIds)
        {
            try
            {
                await validator
                    .CanEditRoleAsync(roleId, command.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw PermissionDenied(ex);
            }
        }

        // UserProject.Query().Where(user, project).Only runs the UserProject
        // query policy (user.go:540-547).
        UserProjectAccessFilter? queryScope;
        try
        {
            queryScope = await userProjectPrivacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to find user project relationship: " + ex.Message,
                ex);
        }

        UserProjectRecord? updated;
        try
        {
            updated = await users.UpdateProjectUserAsync(
                    command.UserId,
                    command.ProjectId,
                    command.IsOwner,
                    command.Scopes,
                    command.AddRoleIds,
                    command.RemoveRoleIds,
                    cancellationToken,
                    queryScope: queryScope,
                    // mut.Save runs the UserProject mutation policy
                    // (user.go:557), client.User.Get the User query policy
                    // (user.go:556), user.Update() the User mutation policy
                    // (user.go:562-572).
                    beforeUpdate: token => userProjectPrivacy.EvaluateMutationAsync(
                        UserProjectMutationOp.Update,
                        command.ProjectId,
                        token),
                    beforeUserRead: token => privacy.GetVisibleUserOrThrowAsync(
                        command.UserId,
                        token),
                    beforeUserRoleUpdate: privacy.EvaluateMutationAsync)
                .ConfigureAwait(false);
        }
        catch (StoreFailureException ex)
        {
            // The store already produced the reference's final wording for
            // each step (user.go:535-572).
            throw new InvalidOperationException(ex.Message, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to update project user: " + ex.Message,
                ex);
        }

        // The reference invalidates its user cache here (user.go:576); the
        // port's management read path has no cache (C173).
        return updated;
    }

    /// <summary>Port of UserService.GetUserByID without the cache layer.</summary>
    public Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => users.GetByIdAsync(id, cancellationToken);

    public async Task<ListPage<UserRecord>> ListAsync(
        UserListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The User query policy installs WhereHasProjectUsersWith(project_id)
        // for project owners and project members before the page runs.
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (projectScope is not null)
        {
            var scope = new UserListFilter
            {
                HasProjectUsersWithProjectId = projectScope.Value,
            };
            query = query with
            {
                Where = query.Where is null
                    ? scope
                    : new UserListFilter { And = [query.Where, scope] },
            };
        }

        return await users.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The reference wraps every validator failure in "permission denied: %w".</summary>
    private static InvalidOperationException PermissionDenied(Exception inner)
        => new("permission denied: " + inner.Message, inner);

    /// <summary>
    /// Parses a role's JSON scopes string for CanGrantRole; null, blank or
    /// malformed input yields no scopes (same rule as PermissionValidator).
    /// </summary>
    private static List<string> ParseScopes(string? json)
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
