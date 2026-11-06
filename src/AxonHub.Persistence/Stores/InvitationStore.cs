using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>An invitation row plus the name of its project edge (ent WithProject).</summary>
public sealed record InvitationWithProject(InvitationRecord Invitation, string? ProjectName);

/// <summary>The inputs of the registration transaction (the password is already hashed).</summary>
public sealed record InvitationRegistration
{
    /// <summary>SHA-256 hex of the token the caller presented.</summary>
    public required string TokenHash { get; init; }

    /// <summary>Trimmed email (InvitationService trims before the transaction).</summary>
    public required string Email { get; init; }

    public required string PasswordHash { get; init; }

    public required string FirstName { get; init; }

    public required string LastName { get; init; }
}

/// <summary>
/// Persistence seam for the reference <c>invitations</c> table
/// (internal/ent/schema/invitation.go:13-61) and the transaction body of
/// InvitationService.RegisterInvitation
/// (internal/server/biz/invitation.go:117-214).
/// </summary>
public interface IInvitationStore
{
    /// <summary>Port of <c>Invitation.Create()...Save</c> (no unique-name rule).</summary>
    Task<InvitationRecord> CreateAsync(
        InvitationRecord invitation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>Invitation.Query().Where(TokenHashEQ).WithProject().Only()</c>:
    /// live rows only, and the project edge (also live) is loaded separately.
    /// Null when no live row matches.
    /// </summary>
    Task<InvitationWithProject?> GetByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the InvitationService.RegisterInvitation transaction body: reads
    /// and validates the invitation, consumes one use with the reference's
    /// conditional update, then creates the user, its project membership and
    /// the invitation role link. Validation messages are the reference's; step
    /// failures carry its per-step wording as <see cref="StoreFailureException"/>.
    /// </summary>
    Task<UserRecord> RegisterAsync(
        InvitationRegistration registration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>revokeRoleInvitations</c> (role.go:258-265):
    /// <c>Invitation.Delete().Where(RoleIDIn(ids...))</c> soft-deletes every
    /// live invitation bound to the roles. Returns the affected row count.
    /// </summary>
    Task<int> DeleteByRoleIdsAsync(
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>invitations</c> table. Writes go through EF Core
/// (Dapper for the reference's conditional <c>AddUsedCount</c> update); reads
/// use Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class InvitationStore(AxonHubDbContext db) : IInvitationStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "token_hash",
        "role_id", "expires_at", "max_uses", "used_count", "project_id"
        """;

    public async Task<InvitationRecord> CreateAsync(
        InvitationRecord invitation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invitation);

        var now = DateTime.UtcNow;
        if (invitation.CreatedAt == default)
        {
            invitation.CreatedAt = now;
        }

        if (invitation.UpdatedAt == default)
        {
            invitation.UpdatedAt = now;
        }

        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return invitation;
    }

    public async Task<InvitationWithProject?> GetByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        var invitation = await FindByTokenHashAsync(tokenHash, cancellationToken)
            .ConfigureAwait(false);
        if (invitation is null)
        {
            return null;
        }

        // The reference eager-loads the project edge under the same system
        // bypass; the SoftDeleteMixin predicate still applies to the edge
        // query, so a soft-deleted project yields no name. The reference then
        // dereferences the nil edge (a panic); the port keeps the row and lets
        // the service report an empty name (C201).
        const string projectSql = """
            SELECT "name" FROM "projects"
            WHERE "id" = @ProjectId AND "deleted_at" = 0;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var projectName = await connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition(
                projectSql,
                new { invitation.ProjectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new InvitationWithProject(invitation, projectName);
    }

    public async Task<UserRecord> RegisterAsync(
        InvitationRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Step order and messages mirror InvitationService.RegisterInvitation:
        // each failure rolls the whole transaction back (dispose without commit).
        var invitation = await FindByTokenHashAsync(registration.TokenHash, cancellationToken)
            .ConfigureAwait(false);
        if (invitation is null)
        {
            throw new InvalidOperationException("invitation not found");
        }

        var now = DateTime.UtcNow;
        if (InvitationIsExpired(invitation, now)
            || (invitation.MaxUses > 0 && invitation.UsedCount >= invitation.MaxUses))
        {
            throw new InvalidOperationException("invitation is no longer valid");
        }

        if (invitation.RoleId is not { } roleId)
        {
            throw new InvalidOperationException("invitation role is required");
        }

        if (!await RoleExistsAsync(roleId, invitation.ProjectId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException("invitation role is no longer available");
        }

        bool emailExists;
        try
        {
            emailExists = await EmailExistsAsync(registration.Email, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StoreFailureException("failed to check existing user: " + ex.Message, ex);
        }

        if (emailExists)
        {
            throw new InvalidOperationException("a user with this email already exists");
        }

        int consumed;
        try
        {
            consumed = await ConsumeUseAsync(invitation, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StoreFailureException("failed to use invitation: " + ex.Message, ex);
        }

        // The conditional update is the reference's concurrency guard: a
        // concurrent registration that consumed the last use wins here.
        if (consumed != 1)
        {
            throw new InvalidOperationException("invitation is no longer valid");
        }

        // Ent's User.Create defaults: status activated, prefer_language en,
        // first_name/last_name "" (already trimmed by the service), is_owner
        // false, nullable avatar/scopes.
        var user = new UserRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Email = registration.Email,
            Status = "activated",
            PreferLanguage = "en",
            Password = registration.PasswordHash,
            FirstName = registration.FirstName,
            LastName = registration.LastName,
        };

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StoreFailureException("failed to create user: " + ex.Message, ex);
        }

        db.UserProjects.Add(new UserProjectRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            IsOwner = false,
            UserId = user.Id,
            ProjectId = invitation.ProjectId,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StoreFailureException("failed to add user to project: " + ex.Message, ex);
        }

        // client.User.UpdateOneID(id).AddRoleIDs(*roleID): the edge add inserts
        // the user_roles link.
        db.UserRoles.Add(new UserRoleRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            UserId = user.Id,
            RoleId = roleId,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StoreFailureException("failed to assign invitation role: " + ex.Message, ex);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    public async Task<int> DeleteByRoleIdsAsync(
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        // RoleIDIn() with no ids is FALSE in ent; the callers never pass an
        // empty list, but keep the predicate valid.
        if (roleIds.Count == 0)
        {
            return 0;
        }

        var args = new SqlArguments();
        var parameters = new List<string>(roleIds.Count);
        foreach (var roleId in roleIds)
        {
            parameters.Add(args.Add(roleId));
        }

        // The SoftDeleteMixin delete hook rewrites OpDelete into an update of
        // deleted_at (+ updated_at from the TimeMixin UpdateDefault) and keeps
        // the deleted_at = 0 predicate.
        var sql = """
            UPDATE "invitations"
            SET "deleted_at" = @DeletedAt, "updated_at" = @UpdatedAt
            WHERE "deleted_at" = 0 AND "role_id" IN (
            """
            + string.Join(", ", parameters)
            + ");";

        args.Parameters.Add("DeletedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        args.Parameters.Add("UpdatedAt", DateTime.UtcNow);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Port of <c>Query().Where(TokenHashEQ(...)).Only()</c> without the edge.</summary>
    internal async Task<InvitationRecord?> FindByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"invitations\" WHERE \"token_hash\" = @TokenHash AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<InvitationRecord>(
            new CommandDefinition(
                sql,
                new { TokenHash = tokenHash },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<bool> RoleExistsAsync(
        long roleId,
        long projectId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM "roles"
                WHERE "id" = @RoleId AND "project_id" = @ProjectId
                  AND "deleted_at" = 0
            );
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { RoleId = roleId, ProjectId = projectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM "users"
                WHERE "email" = @Email AND "deleted_at" = 0
            );
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { Email = email },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of the conditional <c>AddUsedCount(1)</c> update: the expires_at
    /// predicate is always present, the used_count predicate only for limited
    /// invitations. Returns the affected row count (ent's Save count).
    /// </summary>
    private async Task<int> ConsumeUseAsync(
        InvitationRecord invitation,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var sql = """
            UPDATE "invitations"
            SET "used_count" = "used_count" + 1, "updated_at" = @UpdatedAt
            WHERE "id" = @Id AND "deleted_at" = 0
              AND ("expires_at" IS NULL OR "expires_at" > @Now)
            """
            + (invitation.MaxUses > 0 ? " AND \"used_count\" < @MaxUses" : string.Empty)
            + ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    UpdatedAt = now,
                    Now = now,
                    invitation.Id,
                    invitation.MaxUses,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static bool InvitationIsExpired(InvitationRecord invitation, DateTime now)
        => invitation.ExpiresAt is { } expiresAt && expiresAt <= now;
}
