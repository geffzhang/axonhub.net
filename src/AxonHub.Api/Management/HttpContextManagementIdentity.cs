using AxonHub.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Management;

/// <summary>
/// Reads the management user the admin GraphQL gate established on the request
/// (middleware.WithAdminGraphqlAuth). A JWT caller yields its user; a
/// service-account API key has no user, so resolvers see "user not found in
/// context" exactly like the reference's <c>contexts.GetUser</c> on an
/// API-key request (C161).
/// </summary>
public sealed class HttpContextManagementIdentity(IHttpContextAccessor accessor) : IManagementIdentity
{
    public Task<ManagementUser?> GetCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Identity?.User);
    }

    public AxonHub.Persistence.ApiKeyRecord? GetCurrentApiKey() => Identity?.ApiKey;

    public long? GetCurrentProjectId() => Identity?.ProjectId;

    private AdminGraphqlIdentity? Identity =>
        accessor.HttpContext?.Items[AdminGraphqlIdentity.ContextKey] as AdminGraphqlIdentity;
}
