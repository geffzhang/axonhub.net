namespace AxonHub.Api.Management;

/// <summary>
/// The authenticated management user the reference reads from the Gin context
/// (contexts.GetUser / scopes.getUserFromContext). <see cref="IsOwner"/>
/// mirrors the ent <c>is_owner</c> flag the privacy rules key on.
/// </summary>
public sealed record ManagementUser(long Id, bool IsOwner);

/// <summary>
/// Current-user seam for the management resolvers. The admin GraphQL gate
/// (middleware.WithAdminGraphqlAuth) populates the request user; the
/// production implementation (<see cref="HttpContextManagementIdentity"/>)
/// reads it back, and service-level tests substitute their own (C161).
/// </summary>
public interface IManagementIdentity
{
    /// <summary>The current management user, or null when there is none.</summary>
    Task<ManagementUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
