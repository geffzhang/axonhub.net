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

/// <summary>
/// Port of the management methods of biz.UserService
/// (internal/server/biz/user.go:62-642) over <see cref="IUserAdminStore"/>.
/// The GraphQL permission validator is not ported yet (C014), so the service
/// only carries the persistence behaviour and the reference error strings.
/// </summary>
public sealed class UserManagementService(
    IUserAdminStore users,
    IPasswordHasher passwords)
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

    /// <summary>Port of UserService.UpdateUser (minus the permission checks).</summary>
    public async Task<UserRecord> UpdateAsync(
        UpdateUserCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        string? hashedPassword = null;
        if (command.Password is not null)
        {
            // HashPassword failures are returned unwrapped by the reference.
            hashedPassword = passwords.Hash(command.Password);
        }

        UserRecord? updated;
        try
        {
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

    /// <summary>Port of UserService.DeleteUser (minus the permission check).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
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

    /// <summary>Port of UserService.GetUserByID without the cache layer.</summary>
    public Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => users.GetByIdAsync(id, cancellationToken);

    public Task<ListPage<UserRecord>> ListAsync(
        UserListQuery query,
        CancellationToken cancellationToken = default)
        => users.ListAsync(query, cancellationToken);
}
