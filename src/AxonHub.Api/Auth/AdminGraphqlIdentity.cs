using AxonHub.Api.Management;

namespace AxonHub.Api.Auth;

/// <summary>
/// The authenticated admin GraphQL caller established by the gate
/// (middleware.WithAdminGraphqlAuth): a JWT user or a service-account API key,
/// plus the project selected by X-Project-ID (middleware.WithProjectID). The
/// reference stores these facts on the Gin context; the port stores the same
/// three facts in <c>HttpContext.Items</c> where
/// <see cref="HttpContextManagementIdentity"/> reads them back.
/// </summary>
public sealed record AdminGraphqlIdentity(
    ManagementUser? User,
    long? ApiKeyId,
    long? ProjectId,
    bool IsApiKey,
    AxonHub.Persistence.ApiKeyRecord? ApiKey = null)
{
    /// <summary>The HttpContext.Items key written by the gate middleware.</summary>
    public static readonly object ContextKey = new();

    public AdminGraphqlIdentity WithProject(long projectId) => this with { ProjectId = projectId };
}
