using System.Globalization;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of <c>internal/authz</c> (HasScope / RequireScope) for the management
/// services. The reference derives the principal from the Gin context; the port
/// derives it from <see cref="IManagementIdentity"/>: a management user
/// short-circuits on owner/test, an API key carries its own scopes, and no
/// principal at all is <c>unknown</c>.
/// </summary>
/// <remarks>
/// <c>userHasScope</c> only consults project membership and project-role scopes
/// when the request carries a project id; those checks reuse
/// <see cref="PermissionValidator.UserHasProjectScope"/>, whose first-matching
/// membership semantics are the reference's <c>break</c> (C161).
/// </remarks>
public sealed class AuthorizationScope(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadChannels = "read_channels";

    public const string WriteChannels = "write_channels";

    public const string WriteApiKeys = "write_api_keys";

    public const string WriteRequests = "write_requests";

    public const string ReadSettings = "read_settings";

    public const string WriteSettings = "write_settings";

    public const string ReadUsers = "read_users";

    /// <summary>
    /// Port of <c>scopes.UserHasScope</c> (rule_user_scope.go:66-77): only a
    /// management user principal is consulted — API keys never qualify and
    /// the test principal gets no bypass — and the user must pass
    /// <c>HasSystemScope</c> (owner, direct scope, or system-role scope).
    /// </summary>
    public async Task<bool> HasUserScopeAsync(
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return false;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        return PermissionValidator.HasSystemScope(loaded, requiredScope);
    }

    /// <summary>Port of authz.HasScope.</summary>
    public async Task<bool> HasScopeAsync(
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            // PrincipalTypeTest bypasses every policy in the reference.
            if (current.IsTest || current.IsOwner)
            {
                return true;
            }

            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            // Direct scopes and system-role scopes.
            if (PermissionValidator.HasSystemScope(loaded, requiredScope))
            {
                return true;
            }

            var projectId = identity.GetCurrentProjectId();
            return projectId is not null
                && PermissionValidator.UserHasProjectScope(
                    loaded,
                    projectId.Value,
                    requiredScope);
        }

        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            return PermissionValidator.ParseScopes(apiKey.Scopes)
                .Contains(requiredScope, StringComparer.Ordinal);
        }

        return false;
    }

    /// <summary>Port of authz.RequireScope.</summary>
    /// <exception cref="InvalidOperationException">
    /// The principal lacks the scope; the message matches the reference.
    /// </exception>
    public async Task RequireScopeAsync(
        string requiredScope,
        CancellationToken cancellationToken = default)
    {
        if (!await HasScopeAsync(requiredScope, cancellationToken).ConfigureAwait(false))
        {
            var principal = await GetPrincipalStringAsync(cancellationToken)
                .ConfigureAwait(false);
            throw new InvalidOperationException(
                "authz: principal " + principal
                + " does not have required scope " + requiredScope);
        }
    }

    /// <summary>Port of authz.Principal.String.</summary>
    public async Task<string> GetPrincipalStringAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            if (current.IsTest)
            {
                return "test";
            }

            return "user:" + current.Id.ToString(CultureInfo.InvariantCulture);
        }

        var apiKey = identity.GetCurrentApiKey();
        return apiKey is not null
            ? "apikey:" + apiKey.Id.ToString(CultureInfo.InvariantCulture)
            : "unknown";
    }
}
