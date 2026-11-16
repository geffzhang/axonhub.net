using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of the fields RoleService.CreateRole reads from ent.CreateRoleInput.</summary>
public sealed record CreateRoleCommand
{
    public required string Name { get; init; }

    /// <summary>"system" or "project"; nil means system.</summary>
    public string? Level { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public long? ProjectId { get; init; }
}

/// <summary>Port of the fields RoleService.UpdateRole applies from ent.UpdateRoleInput.</summary>
public sealed record UpdateRoleCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }
}

/// <summary>
/// Port of the management methods of biz.RoleService
/// (internal/server/biz/role.go:46-312) over <see cref="IRoleStore"/>. The two
/// delete paths run in one transaction that also soft-deletes the role's
/// invitations (role.go:181-265).
/// </summary>
public sealed class RoleManagementService(
    IRoleStore roles,
    IInvitationStore invitations,
    PermissionValidator validator,
    RolePrivacy privacy)
{
    public const string LevelSystem = "system";

    public const string LevelProject = "project";

    public const string DefaultRoleName = "Developer";

    /// <summary>Port of RoleService.CreateRole (role.go:44-114).</summary>
    public async Task<RoleRecord> CreateAsync(
        CreateRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Validate that the current user can grant these scopes (role.go:45-53);
        // ent's CreateRoleInput.Scopes is a plain slice, nil means none.
        try
        {
            await validator
                .CanGrantScopesAsync(command.Scopes ?? [], command.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw PermissionDenied(ex);
        }

        string level;
        long projectIdValue;

        if (command.Level is null)
        {
            if (command.ProjectId is not null)
            {
                throw new InvalidOperationException("project ID is not allowed for system roles");
            }

            level = LevelSystem;
            projectIdValue = 0;
        }
        else
        {
            switch (command.Level)
            {
                case LevelSystem:
                    if (command.ProjectId is not null)
                    {
                        throw new InvalidOperationException(
                            "project ID is not allowed for system roles");
                    }

                    level = LevelSystem;
                    projectIdValue = 0;
                    break;
                case LevelProject:
                    if (command.ProjectId is null)
                    {
                        throw new InvalidOperationException(
                            "project ID is required for project roles");
                    }

                    level = LevelProject;
                    projectIdValue = command.ProjectId.Value;
                    break;
                default:
                    throw new InvalidOperationException("invalid role level");
            }
        }

        bool exists;
        try
        {
            exists = await RoleNameExistsAsync(
                    level,
                    command.Name,
                    projectIdValue,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to check role name uniqueness: " + ex.Message,
                ex);
        }

        if (exists)
        {
            throw DuplicateName(command.Name);
        }

        var now = DateTime.UtcNow;
        var role = new RoleRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Name = command.Name,
            Level = level,
            Scopes = command.Scopes is null ? null : JsonSerializer.Serialize(command.Scopes),
            ProjectId = projectIdValue,
        };

        long id;
        try
        {
            // client.Role.Create().Save runs the Role mutation policy
            // (role.go:103-111).
            await privacy
                .EvaluateMutationAsync(RoleMutationOp.Create, projectIdValue, cancellationToken)
                .ConfigureAwait(false);
            id = await roles.CreateAsync(role, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to create role: " + ex.Message, ex);
        }

        return await roles.GetByIdAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("failed to get created role: ent: role not found");
    }

    /// <summary>Port of RoleService.UpdateRole (role.go:117-177).</summary>
    public async Task<RoleRecord> UpdateAsync(
        UpdateRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // First check if the user can edit this role (role.go:118-121).
        try
        {
            await validator.CanEditRoleAsync(command.Id, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw PermissionDenied(ex);
        }

        // Validate new scopes if being updated (role.go:123-138).
        if (command.Scopes is not null)
        {
            RoleRecord role;
            try
            {
                // client.Role.Get runs the Role query policy (role.go:125).
                role = await privacy.GetVisibleRoleOrThrowAsync(command.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to get role: " + ex.Message, ex);
            }

            long? projectId = null;
            if (!role.IsSystemRole)
            {
                projectId = role.ProjectId;
            }

            try
            {
                await validator.CanGrantScopesAsync(command.Scopes, projectId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw PermissionDenied(ex);
            }
        }

        if (command.Name is not null)
        {
            RoleRecord current;
            try
            {
                // client.Role.Get runs the Role query policy (role.go:145).
                current = await privacy.GetVisibleRoleOrThrowAsync(command.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get current role: " + ex.Message,
                    ex);
            }

            if (command.Name != current.Name)
            {
                bool exists;
                try
                {
                    exists = await RoleNameExistsAsync(
                            current.Level,
                            command.Name,
                            current.ProjectId,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to check role name uniqueness: " + ex.Message,
                        ex);
                }

                if (exists)
                {
                    throw DuplicateName(command.Name);
                }
            }
        }

        RoleRecord? updated;
        try
        {
            // client.Role.UpdateOneID(id).Save runs the Role mutation policy
            // (role.go:162-172); a project-scoped caller gets
            // WhereP(project_id = ctx).
            var mutationScope = await privacy
                .EvaluateMutationAsync(RoleMutationOp.UpdateOne, null, cancellationToken)
                .ConfigureAwait(false);
            updated = await roles.UpdateAsync(
                    command.Id,
                    command.Name,
                    command.Scopes,
                    cancellationToken,
                    projectScope: mutationScope)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to update role: " + ex.Message, ex);
        }

        return updated
            ?? throw new InvalidOperationException("failed to update role: ent: role not found");
    }

    /// <summary>Port of RoleService.DeleteRole (role.go:181-226).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await roles.RunInTransactionAsync(
            async token =>
            {
                RoleRecord role;
                try
                {
                    // client.Role.Query().Where(IDEQ).Only runs the Role query
                    // policy (role.go:185-190).
                    role = await privacy.GetVisibleRoleOrThrowAsync(id, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to check role existence: " + ex.Message,
                        ex);
                }

                if (role.Name == DefaultRoleName)
                {
                    throw new InvalidOperationException("cannot delete the default Developer role");
                }

                await RevokeInvitationsAsync([id], token).ConfigureAwait(false);

                bool deleted;
                try
                {
                    // client.Role.DeleteOneID(id).Exec runs the Role mutation
                    // policy (role.go:201-203).
                    var mutationScope = await privacy
                        .EvaluateMutationAsync(RoleMutationOp.DeleteOne, null, token)
                        .ConfigureAwait(false);
                    deleted = await roles
                        .DeleteAsync(id, token, projectScope: mutationScope)
                        .ConfigureAwait(false);
                }
                catch (StoreFailureException ex)
                {
                    // The store already produced the reference's final wording.
                    throw new InvalidOperationException(ex.Message, ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("failed to delete role: " + ex.Message, ex);
                }

                if (!deleted)
                {
                    throw new InvalidOperationException("failed to delete role: ent: role not found");
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of RoleService.BulkDeleteRoles (role.go:228-256).</summary>
    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        await roles.RunInTransactionAsync(
            async token =>
            {
                IReadOnlyList<RoleRecord> found;
                try
                {
                    // client.Role.Query().Where(IDIn).All runs the Role query
                    // policy (role.go:224).
                    var queryScope = await privacy.EvaluateQueryAsync(token)
                        .ConfigureAwait(false);
                    found = await roles.ListByIdsAsync(ids, token, projectScope: queryScope)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("failed to query roles: " + ex.Message, ex);
                }

                if (found.Count != ids.Count)
                {
                    throw new InvalidOperationException(
                        $"expected to find {ids.Count} roles, but found {found.Count}");
                }

                foreach (var role in found)
                {
                    if (role.Name == DefaultRoleName)
                    {
                        throw new InvalidOperationException(
                            "cannot delete the default Developer role");
                    }
                }

                await RevokeInvitationsAsync(ids, token).ConfigureAwait(false);

                try
                {
                    // client.Role.Delete().Where(IDIn).Exec runs the Role
                    // mutation policy (role.go:243-245).
                    var mutationScope = await privacy
                        .EvaluateMutationAsync(RoleMutationOp.Delete, null, token)
                        .ConfigureAwait(false);
                    await roles.DeleteManyAsync(ids, token, projectScope: mutationScope)
                        .ConfigureAwait(false);
                }
                catch (StoreFailureException ex)
                {
                    // The store already produced the reference's final wording.
                    throw new InvalidOperationException(ex.Message, ex);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("failed to delete roles: " + ex.Message, ex);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of revokeRoleInvitations (role.go:258-265).</summary>
    private async Task RevokeInvitationsAsync(
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await invitations.DeleteByRoleIdsAsync(roleIds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to revoke role invitations: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of RoleService.RoleNameExists.</summary>
    public async Task<bool> RoleNameExistsAsync(
        string level,
        string name,
        long? projectId,
        CancellationToken cancellationToken = default)
    {
        if (level == LevelSystem)
        {
            // client.Role.Query().Exist runs the Role query policy
            // (role.go:274-279); its WhereProjectID(ctx) predicate cannot
            // match a system role (project_id 0).
            var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            if (projectScope is not null)
            {
                return false;
            }

            return await roles.NameExistsAsync(name, 0, cancellationToken).ConfigureAwait(false);
        }

        if (projectId is null)
        {
            // The reference returns this before running the query (role.go:282).
            throw new InvalidOperationException("project ID is required for project roles");
        }

        var scope = await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        if (scope is not null && scope.Value != projectId.Value)
        {
            return false;
        }

        return await roles.NameExistsAsync(name, projectId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ListPage<RoleRecord>> ListAsync(
        RoleListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The Role query policy installs WhereProjectID(ctx) for project owners
        // and project members before the page runs.
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (projectScope is not null)
        {
            var scope = new RoleListFilter { ProjectId = projectScope.Value };
            query = query with
            {
                Where = query.Where is null
                    ? scope
                    : new RoleListFilter { And = [query.Where, scope] },
            };
        }

        return await roles.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    public static bool IsValidLevel(string level)
        => level is LevelSystem or LevelProject;

    /// <summary>The reference wraps every validator failure in "permission denied: %w".</summary>
    private static InvalidOperationException PermissionDenied(Exception inner)
        => new("permission denied: " + inner.Message, inner);

    /// <summary>Port of xerrors.DuplicateNameError("role", name).</summary>
    private static CodedException DuplicateName(string name) => new(
        CodedException.DuplicateName,
        $"role name '{name}' already exists",
        new Dictionary<string, object?>
        {
            ["resource"] = "role",
            ["field"] = "name",
            ["value"] = name,
        });
}
