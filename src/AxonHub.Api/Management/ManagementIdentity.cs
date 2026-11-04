namespace AxonHub.Api.Management;

/// <summary>
/// The authenticated management user the reference reads from the Gin context
/// (contexts.GetUser / scopes.getUserFromContext). <see cref="IsOwner"/>
/// mirrors the ent <c>is_owner</c> flag the privacy rules key on.
/// <see cref="IsTest"/> mirrors the principal type the reference sets via
/// <c>authz.WithTestBypass</c>; when true, AddUserToProject skips its
/// permission checks exactly like the reference (user.go:395-401).
/// </summary>
public sealed record ManagementUser(long Id, bool IsOwner, bool IsTest = false);

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

    /// <summary>
    /// The API key principal, or null when the caller is not an API key
    /// (contexts.GetAPIKey). API-key callers have no user.
    /// </summary>
    AxonHub.Persistence.ApiKeyRecord? GetCurrentApiKey() => null;

    /// <summary>
    /// The project selected by X-Project-ID, or null when the request has no
    /// project context (contexts.GetProjectID).
    /// </summary>
    long? GetCurrentProjectId() => null;
}
