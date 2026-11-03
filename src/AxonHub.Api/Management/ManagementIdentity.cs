using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// The authenticated management user the reference reads from the Gin context
/// (contexts.GetUser / scopes.getUserFromContext). <see cref="IsOwner"/>
/// mirrors the ent <c>is_owner</c> flag the privacy rules key on.
/// </summary>
public sealed record ManagementUser(long Id, bool IsOwner);

/// <summary>
/// Current-user seam for the management resolvers. The reference's auth
/// middleware populates the request user; this build has no management login
/// yet, so the default implementation resolves the owner user and tests can
/// substitute another user (C161).
/// </summary>
public interface IManagementIdentity
{
    /// <summary>The current management user, or null when there is none.</summary>
    Task<ManagementUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default identity: the owner account. Template ownership is enforced through
/// <see cref="ManagementUser.IsOwner"/>, so with the owner identity every
/// template is visible and writable, which is the reference's behaviour for
/// the owner. A deployment with no owner has no management user.
/// </summary>
public sealed class OwnerManagementIdentity(IUserStore users) : IManagementIdentity
{
    public async Task<ManagementUser?> GetCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var owner = await users.FindOwnerAsync(cancellationToken).ConfigureAwait(false);
        return owner is null ? null : new ManagementUser(owner.Id, owner.IsOwner);
    }
}
