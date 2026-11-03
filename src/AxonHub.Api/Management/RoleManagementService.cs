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
/// (internal/server/biz/role.go:46-312) over <see cref="IRoleStore"/>.
/// Role-invitation revocation is a no-op until the invitations table is ported.
/// </summary>
public sealed class RoleManagementService(
    IRoleStore roles,
    PermissionValidator validator)
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
            RoleRecord? role;
            try
            {
                role = await roles.GetByIdAsync(command.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to get role: " + ex.Message, ex);
            }

            if (role is null)
            {
                throw new InvalidOperationException(
                    "failed to get role: " + PermissionValidator.RoleNotFound);
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
            RoleRecord? current;
            try
            {
                current = await roles.GetByIdAsync(command.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get current role: " + ex.Message,
                    ex);
            }

            if (current is null)
            {
                throw new InvalidOperationException(
                    "failed to get current role: ent: role not found");
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
            updated = await roles.UpdateAsync(
                    command.Id,
                    command.Name,
                    command.Scopes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to update role: " + ex.Message, ex);
        }

        return updated
            ?? throw new InvalidOperationException("failed to update role: ent: role not found");
    }

    /// <summary>Port of RoleService.DeleteRole (minus invitation revocation).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        RoleRecord? role;
        try
        {
            role = await roles.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to check role existence: " + ex.Message,
                ex);
        }

        if (role is null)
        {
            throw new InvalidOperationException(
                "failed to check role existence: ent: role not found");
        }

        if (role.Name == DefaultRoleName)
        {
            throw new InvalidOperationException("cannot delete the default Developer role");
        }

        bool deleted;
        try
        {
            deleted = await roles.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to delete role: " + ex.Message, ex);
        }

        if (!deleted)
        {
            throw new InvalidOperationException("failed to delete role: ent: role not found");
        }
    }

    /// <summary>Port of RoleService.BulkDeleteRoles (minus invitation revocation).</summary>
    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0)
        {
            return;
        }

        IReadOnlyList<RoleRecord> found;
        try
        {
            found = await roles.ListByIdsAsync(ids, cancellationToken).ConfigureAwait(false);
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
                throw new InvalidOperationException("cannot delete the default Developer role");
            }
        }

        try
        {
            await roles.DeleteManyAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to delete roles: " + ex.Message, ex);
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
            return await roles.NameExistsAsync(name, 0, cancellationToken).ConfigureAwait(false);
        }

        if (projectId is null)
        {
            throw new InvalidOperationException("project ID is required for project roles");
        }

        return await roles.NameExistsAsync(name, projectId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<ListPage<RoleRecord>> ListAsync(
        RoleListQuery query,
        CancellationToken cancellationToken = default)
        => roles.ListAsync(query, cancellationToken);

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
