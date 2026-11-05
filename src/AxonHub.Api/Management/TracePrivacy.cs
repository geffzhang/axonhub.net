using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>The ent mutation operation whose policy is being evaluated.</summary>
public enum TraceMutationOp
{
    Create,
    UpdateOne,
    Update,
    DeleteOne,
    Delete,
}

/// <summary>
/// The predicate pair installed by the Request query policy for a non-owner
/// project member: the project filter plus the request-visibility filter.
/// </summary>
public sealed record RequestAccessFilter(long ProjectId, long? VisibilityUserId);

/// <summary>
/// Emulation of the Trace and Thread schemas' ent privacy policies
/// (internal/ent/schema/trace.go:80-100, thread.go:61-79,
/// internal/scopes/rule_*.go). Both schemas share the same rules, so one
/// evaluator serves both. The reference evaluates them inside every ent
/// query/mutation; the port applies the same decisions explicitly before
/// touching the store (C189).
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>APIKeyProjectScopeReadRule(write_requests)</c> →
/// <c>UserProjectScopeReadRule(read_requests)</c> → <c>OwnerRule()</c> →
/// <c>UserReadScopeRule(read_requests)</c>, then <c>default deny</c> when every
/// rule skipped (internal/scopes/policy.go:53). The API-key rule denies with
/// the key's id; the user project rule installs <c>WhereP(project_id = ctx
/// project)</c> even for a system-scoped user.
/// </para>
/// <para>
/// Mutation policy: <c>APIKeyProjectScopeWriteRule(write_requests)</c> →
/// <c>UserProjectScopeWriteRule(write_requests)</c> → <c>OwnerRule()</c> →
/// <c>UserWriteScopeRule(write_requests)</c> → default deny. The API-key rule's
/// deny message omits the key id; a system-scoped user is allowed without a
/// project predicate.
/// </para>
/// <para>
/// The Request query policy (<c>UserProjectScopeReadRequestsRule</c>) adds the
/// per-row visibility predicate the reference installs on
/// <c>*ent.RequestFilter</c>: playground rows of the caller and API requests
/// whose key is missing or not another user's personal key.
/// </para>
/// </remarks>
public sealed class TracePrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    /// <summary>The trace/thread policies require the request scopes.</summary>
    public const string ReadRequests = "read_requests";

    public const string WriteRequests = "write_requests";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:11).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:53).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>
    /// Port of the Trace/Thread Query policy. Returns the project predicate
    /// the policy installed, or null when the query is unfiltered.
    /// </summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task<long?> EvaluateQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule.
        if (current?.IsTest == true)
        {
            return null;
        }

        // Rule 1: APIKeyProjectScopeReadRule — no API key skips; a missing
        // scope denies with the key's id; otherwise filter to the key project.
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (!HasScope(apiKey.Scopes, ReadRequests))
            {
                throw Deny(
                    "API key " + apiKey.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " does not have required scope: " + ReadRequests);
            }

            return apiKey.ProjectId;
        }

        // Rule 2: UserProjectScopeReadRule — needs both a user and a project
        // in context; a system or project scope filters to that project.
        var contextProjectId = identity.GetCurrentProjectId();
        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (current is not null && contextProjectId is not null
            && (PermissionValidator.HasSystemScope(loaded, ReadRequests)
                || PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadRequests)))
        {
            return contextProjectId;
        }

        // Rule 3: OwnerRule — owner users may read everything.
        if (current?.IsOwner == true)
        {
            return null;
        }

        // Rule 4: UserReadScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, ReadRequests))
        {
            return null;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the Trace/Thread Mutation policy. Returns the ent
    /// <c>WhereP(project_id = ...)</c> predicate, or null when the mutation was
    /// allowed without one.
    /// </summary>
    /// <param name="operation">The ent operation the service is about to run.</param>
    /// <param name="mutationProjectId">
    /// The project an API-key/user create targets; ignored by other operations.
    /// </param>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task<long?> EvaluateMutationAsync(
        TraceMutationOp operation,
        long? mutationProjectId,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule.
        if (current?.IsTest == true)
        {
            return null;
        }

        // Rule 1: APIKeyProjectScopeWriteRule.
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (!HasScope(apiKey.Scopes, WriteRequests))
            {
                throw Deny("API key does not have required scope: " + WriteRequests);
            }

            if (operation == TraceMutationOp.Create)
            {
                if (mutationProjectId is null)
                {
                    throw Deny("Project ID not found");
                }

                if (mutationProjectId.Value != apiKey.ProjectId)
                {
                    throw Deny(
                        "API key "
                        + apiKey.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " can not create resources in project "
                        + mutationProjectId.Value.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                }

                return null;
            }

            // UpdateOne/Update/DeleteOne/Delete install the key project.
            return apiKey.ProjectId;
        }

        // Rule 2: UserProjectScopeWriteRule — a system scope allows without a
        // predicate; a project scope installs the context project.
        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            if (PermissionValidator.HasSystemScope(loaded, WriteRequests))
            {
                return null;
            }

            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    WriteRequests))
            {
                if (operation == TraceMutationOp.Create)
                {
                    if (mutationProjectId == contextProjectId)
                    {
                        return null;
                    }
                }
                else
                {
                    return contextProjectId;
                }
            }
        }

        // Rule 3: OwnerRule — owner users may mutate everything.
        if (current?.IsOwner == true)
        {
            return null;
        }

        // Rule 4: UserWriteScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, WriteRequests))
        {
            return null;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the Request Query policy
    /// (internal/ent/schema/request.go:193-220): API-key project rule →
    /// <c>UserProjectScopeReadRequestsRule(read_requests)</c> → OwnerRule →
    /// UserReadScopeRule. Returns the project filter plus the optional
    /// visibility user id, or null when unfiltered.
    /// </summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task<RequestAccessFilter?> EvaluateRequestQueryAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return null;
        }

        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (!HasScope(apiKey.Scopes, ReadRequests))
            {
                throw Deny(
                    "API key " + apiKey.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " does not have required scope: " + ReadRequests);
            }

            return new RequestAccessFilter(apiKey.ProjectId, null);
        }

        var contextProjectId = identity.GetCurrentProjectId();
        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        if (current is not null && contextProjectId is not null
            && (PermissionValidator.HasSystemScope(loaded, ReadRequests)
                || PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadRequests)))
        {
            // System-scoped users and project owners audit everything in the
            // project; other members only see permitted requests.
            var fullVisibility = PermissionValidator.HasSystemScope(loaded, ReadRequests)
                || PermissionValidator.UserIsProjectOwner(loaded, contextProjectId.Value);
            return new RequestAccessFilter(
                contextProjectId.Value,
                fullVisibility ? null : current.Id);
        }

        if (current?.IsOwner == true)
        {
            return null;
        }

        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, ReadRequests))
        {
            return null;
        }

        throw Deny(DefaultDeny);
    }

    private static bool HasScope(string? scopesJson, string scope)
    {
        var scopes = ManagementMapping.ParseScopes(scopesJson);
        return scopes is not null
            && scopes.Contains(scope, StringComparer.Ordinal);
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
