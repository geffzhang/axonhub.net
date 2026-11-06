using System.Security.Cryptography;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of biz.InvitationInfo (internal/server/biz/invitation.go:50-57).</summary>
public sealed record InvitationInfo(
    string ProjectName,
    DateTime? ExpiresAt,
    long MaxUses,
    long UsedCount,
    long RemainingUses);

/// <summary>Port of biz.CreatedInvitation (invitation.go:59-63).</summary>
public sealed record CreatedInvitation(string Token, InvitationInfo Info);

/// <summary>
/// The registered user and the user-info document the reference builds from a
/// post-commit reload with roles, project users and OIDC identities loaded
/// (invitation.go:252-259, ConvertUserToUserInfo).
/// </summary>
public sealed record RegisteredInvitation(UserRecord User, UserProfileInfo Profile);

/// <summary>
/// Port of biz.InvitationService
/// (internal/server/biz/invitation.go:26-335): invitation creation, public
/// metadata lookup and invitation registration.
/// </summary>
/// <remarks>
/// The reference runs every query through authz.RunWithSystemBypass; the port
/// has no ent privacy layer, so the store reads are already unprivileged. The
/// caller's identity comes from <see cref="IManagementIdentity"/> (C161): the
/// admin JWT gate loads it via UserService.GetUserByID, whose Roles/ProjectUsers
/// edges the port fetches on demand (C201).
/// </remarks>
public sealed class InvitationService(
    IInvitationStore invitations,
    IProjectStore projects,
    IRoleStore roles,
    IUserAdminStore users,
    IUserProfileStore profiles,
    IManagementIdentity identity,
    PermissionValidator validator,
    IPasswordHasher passwords)
{
    /// <summary>biz.defaultInvitationLifetime.</summary>
    public static readonly TimeSpan DefaultInvitationLifetime = TimeSpan.FromDays(7);

    /// <summary>scopes.ScopeWriteUsers.</summary>
    public const string WriteUsersScope = "write_users";

    /// <summary>Port of InvitationService.CreateInvitation (invitation.go:65-124).</summary>
    public async Task<CreatedInvitation> CreateInvitationAsync(
        long projectId,
        long roleId,
        int? expiresInHours,
        int maxUses,
        CancellationToken cancellationToken = default)
    {
        await CanInviteAsync(projectId, cancellationToken).ConfigureAwait(false);

        if (roleId == 0)
        {
            throw new InvalidOperationException("project role is required");
        }

        if (maxUses != 0 && maxUses != 1)
        {
            throw new InvalidOperationException("max uses must be 1 or 0");
        }

        var expiresAt = InvitationExpiry(expiresInHours);
        var token = GenerateInvitationToken();

        ProjectRecord? project;
        try
        {
            project = await projects.GetByIdAsync(projectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get project: " + ex.Message, ex);
        }

        if (project is null)
        {
            throw new InvalidOperationException("failed to get project: ent: project not found");
        }

        // The reference folds every lookup error into this message
        // (invitation.go:95-102); the role must belong to the project.
        RoleRecord? role;
        try
        {
            role = await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("project role not found", ex);
        }

        if (role is null || role.ProjectId != projectId)
        {
            throw new InvalidOperationException("project role not found");
        }

        try
        {
            await validator.CanGrantRoleAsync(
                    PermissionValidator.ParseScopes(role.Scopes),
                    projectId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("permission denied: " + ex.Message, ex);
        }

        var invitation = new InvitationRecord
        {
            TokenHash = HashInvitationToken(token),
            ProjectId = projectId,
            RoleId = roleId,
            ExpiresAt = expiresAt,
            MaxUses = maxUses,
            UsedCount = 0,
        };

        try
        {
            invitation = await invitations.CreateAsync(invitation, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create invitation: " + ex.Message,
                ex);
        }

        return new CreatedInvitation(token, InvitationInfoFor(invitation, project.Name));
    }

    /// <summary>Port of InvitationService.GetInvitation (invitation.go:126-144).</summary>
    public async Task<InvitationInfo> GetInvitationAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        InvitationWithProject? row;
        try
        {
            row = await invitations.GetByTokenHashAsync(HashInvitationToken(token), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("invitation not found", ex);
        }

        if (row is null)
        {
            throw new InvalidOperationException("invitation not found");
        }

        if (InvitationIsExpired(row.Invitation, DateTime.UtcNow)
            || (row.Invitation.MaxUses > 0
                && row.Invitation.UsedCount >= row.Invitation.MaxUses))
        {
            throw new InvalidOperationException("invitation is no longer valid");
        }

        // The reference dereferences the project edge unconditionally and
        // panics when the project is soft-deleted (invitation.go:142); the port
        // reports an empty name instead (C201).
        return InvitationInfoFor(row.Invitation, row.ProjectName ?? string.Empty);
    }

    /// <summary>
    /// Port of InvitationService.RegisterInvitation (invitation.go:146-260).
    /// The validation messages are the reference's; store step failures carry
    /// its per-step wording.
    /// </summary>
    public async Task<RegisteredInvitation> RegisterInvitationAsync(
        string token,
        string email,
        string password,
        string firstName,
        string lastName,
        CancellationToken cancellationToken = default)
    {
        email = GoStrings.TrimSpace(email);
        if (email.Length == 0 || password.Length == 0)
        {
            throw new InvalidOperationException("email and password are required");
        }

        // Go's len(password) counts bytes; the reference compares against 7.
        if (Encoding.UTF8.GetByteCount(password) < 7)
        {
            throw new InvalidOperationException("password must be at least 7 characters");
        }

        // The reference hashes inside the registration transaction
        // (invitation.go:224-227); hashing before it is observationally
        // equivalent because a rollback discards nothing else (C201).
        var passwordHash = passwords.Hash(password);

        var created = await invitations
            .RegisterAsync(
                new InvitationRegistration
                {
                    TokenHash = HashInvitationToken(token),
                    Email = email,
                    PasswordHash = passwordHash,
                    FirstName = GoStrings.TrimSpace(firstName),
                    LastName = GoStrings.TrimSpace(lastName),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Post-commit reload with the roles, project-user and OIDC edges
        // (invitation.go:252-259); the reference returns these errors raw.
        var user = await users.GetByIdAsync(created.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ent: user not found");

        var roleRows = await profiles.ListUserRolesAsync(created.Id, cancellationToken)
            .ConfigureAwait(false);
        var projectUsers = await profiles.ListUserProjectsAsync(created.Id, cancellationToken)
            .ConfigureAwait(false);
        var oidcIdentities = await profiles.ListOidcIdentitiesAsync(created.Id, cancellationToken)
            .ConfigureAwait(false);

        return new RegisteredInvitation(
            user,
            UserProfileService.BuildUserInfo(user, roleRows, projectUsers, oidcIdentities));
    }

    /// <summary>Port of InvitationService.canInvite (invitation.go:262-286).</summary>
    public async Task CanInviteAsync(long projectId, CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        if (current.IsOwner)
        {
            return;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);

        // scopes.UserHasScope(ctx, ScopeWriteUsers).
        if (PermissionValidator.HasSystemScope(loaded, WriteUsersScope))
        {
            return;
        }

        if (loaded is not null)
        {
            foreach (var membership in loaded.ProjectUsers)
            {
                if (membership.ProjectId != projectId)
                {
                    continue;
                }

                if (membership.IsOwner
                    || ContainsWriteUsers(membership.Scopes))
                {
                    return;
                }

                // The reference only consults project roles inside the matching
                // membership branch (invitation.go:278-283).
                foreach (var role in loaded.Roles)
                {
                    if (role.ProjectId == projectId && ContainsWriteUsers(role.Scopes))
                    {
                        return;
                    }
                }
            }
        }

        throw new InvalidOperationException(
            "permission denied: project user management access required");
    }

    /// <summary>Port of biz.invitationInfo (invitation.go:306-318).</summary>
    public static InvitationInfo InvitationInfoFor(InvitationRecord row, string projectName)
    {
        ArgumentNullException.ThrowIfNull(row);

        var remaining = row.MaxUses > 0 ? Math.Max(row.MaxUses - row.UsedCount, 0) : 0;
        return new InvitationInfo(
            projectName,
            row.ExpiresAt,
            row.MaxUses,
            row.UsedCount,
            remaining);
    }

    /// <summary>Port of biz.invitationIsExpired (invitation.go:320-322).</summary>
    public static bool InvitationIsExpired(InvitationRecord row, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ExpiresAt is { } expiresAt && expiresAt <= now;
    }

    /// <summary>Port of biz.invitationExpiry (invitation.go:288-304).</summary>
    public static DateTime? InvitationExpiry(int? hours)
    {
        if (hours is null)
        {
            return DateTime.UtcNow.Add(DefaultInvitationLifetime);
        }

        if (hours < 0)
        {
            throw new InvalidOperationException("expiration must not be negative");
        }

        if (hours == 0)
        {
            return null;
        }

        if (hours > 100000)
        {
            throw new InvalidOperationException("expiration hours exceed maximum limit");
        }

        return DateTime.UtcNow.AddHours(hours.Value);
    }

    /// <summary>
    /// Port of biz.generateInvitationToken (invitation.go:324-330): six random
    /// bytes in unpadded base64url (eight characters).
    /// </summary>
    public static string GenerateInvitationToken()
    {
        var bytes = new byte[6];
        try
        {
            RandomNumberGenerator.Fill(bytes);
        }
        catch (CryptographicException ex)
        {
            // crypto/rand failure is effectively unreachable on a supported
            // platform; the reference wraps it (invitation.go:326-328).
            throw new InvalidOperationException(
                "failed to generate invitation token: " + ex.Message,
                ex);
        }

        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>
    /// Port of biz.hashInvitationToken (invitation.go:332-335): SHA-256 of the
    /// token as lowercase hex.
    /// </summary>
    public static string HashInvitationToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static bool ContainsWriteUsers(string? scopesJson)
        => PermissionValidator.ParseScopes(scopesJson)
            .Contains(WriteUsersScope, StringComparer.Ordinal);
}
