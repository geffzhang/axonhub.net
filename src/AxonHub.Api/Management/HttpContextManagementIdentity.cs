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

    /// <summary>
    /// The admin gate's API key, or the key published by the API-key
    /// middleware for the /v1 surface (contexts.GetAPIKey reads the same
    /// request context in the reference).
    /// </summary>
    public AxonHub.Persistence.ApiKeyRecord? GetCurrentApiKey()
        => Identity is not null ? Identity.ApiKey : ApiKeyIdentity?.ApiKey;

    public long? GetCurrentProjectId()
        => Identity is not null ? Identity.ProjectId : ApiKeyIdentity?.ProjectId;

    public AxonHub.Persistence.ProjectRecord? GetCurrentProject()
        => Identity is not null ? null : ApiKeyIdentity?.Project;

    private AdminGraphqlIdentity? Identity =>
        accessor.HttpContext?.Items[AdminGraphqlIdentity.ContextKey] as AdminGraphqlIdentity;

    /// <summary>
    /// The middleware-published identity of the /v1 surface. Read lazily and
    /// only when the admin gate never ran, so management requests keep their
    /// existing principal exactly.
    /// </summary>
    private Auth.RequestIdentity? ApiKeyIdentity
        => accessor.HttpContext is { } context
            ? Auth.RequestIdentity.FromHttpContext(context)
            : null;
}
