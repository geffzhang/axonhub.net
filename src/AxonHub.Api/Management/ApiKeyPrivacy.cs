using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the ent privacy deny sentinel (entgo.io/ent/privacy.Deny). The
/// reference GraphQL presenter maps any error that wraps the sentinel to
/// <c>permission denied</c> with <c>extensions.code = FORBIDDEN</c>
/// (internal/server/gql/graphql.go:181-190); the port's error filter walks the
/// exception chain for this type.
/// </summary>
public sealed class PrivacyDenyException : Exception
{
    /// <summary>The sentinel's Error() text (ent/privacy/policy.go).</summary>
    public const string DenyMessage = "ent/privacy: deny rule";

    public PrivacyDenyException()
        : base(DenyMessage)
    {
    }

    /// <summary>
    /// A wrapped sentinel: Go's <c>fmt.Errorf("...: %w", privacy.Deny)</c> keeps
    /// the outer message while <c>errors.Is(err, privacy.Deny)</c> still
    /// matches. The type carries the identity; the message stays the caller's.
    /// </summary>
    public PrivacyDenyException(string message)
        : base(message)
    {
    }

    /// <summary>Port of <c>errors.Is(err, entprivacy.Deny)</c>, walking the chain.</summary>
    public static bool IsDeny(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PrivacyDenyException)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>The ent mutation operation whose policy is being evaluated.</summary>
public enum ApiKeyMutationOp
{
    Create,
    UpdateOne,
    Update,
    DeleteOne,
    Delete,
}

/// <summary>
/// Emulation of the APIKey schema's ent privacy policies
/// (internal/ent/schema/api_key.go:119-132, internal/scopes/rule_*.go). The
/// reference runs these rules inside every ent query/mutation; the port applies
/// the same decisions explicitly before touching the store.
/// </summary>
/// <remarks>
/// A query rule evaluation produces an <see cref="ApiKeyAccessFilter"/> (null
/// means "no predicate"); a mutation rule produces the <c>WhereP</c> project
/// predicate (null for an unconditional allow). Denying throws
/// <see cref="PrivacyDenyException"/>.<para />
/// The project/user read helpers emulate the Project and User schema policies
/// for the two edges the APIKey GraphQL type exposes,
/// <c>APIKey.project</c> and <c>APIKey.user</c>.
/// </remarks>
public sealed class ApiKeyPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users,
    IProjectStore projects)
{
    public const string ReadApiKeys = "read_api_keys";
    public const string WriteApiKeys = "write_api_keys";
    public const string ReadProjects = "read_projects";
    public const string ReadUsers = "read_users";

    /// <summary>
    /// Port of the APIKey Query policy:
    /// 1. UserPersonalAPIKeyReadRule(read_api_keys),
    /// 2. APIKeyProjectScopeReadRule(read_api_keys),
    /// 3. OwnerRule().
    /// </summary>
    public async Task<ApiKeyAccessFilter?> EvaluateQueryAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        var contextProjectId = identity.GetCurrentProjectId();

        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            // Rule 1: a user principal scoped to the request's project.
            if (contextProjectId is not null
                && (PermissionValidator.HasSystemScope(loaded, ReadApiKeys)
                    || PermissionValidator.UserHasProjectScope(
                        loaded,
                        contextProjectId.Value,
                        ReadApiKeys)))
            {
                return new ApiKeyAccessFilter
                {
                    ProjectId = contextProjectId,
                    PersonalUserId = PermissionValidator.UserIsProjectOwner(
                        loaded,
                        contextProjectId.Value)
                        ? null
                        : current.Id,
                };
            }
        }

        // Rule 2: an API key principal is confined to its own project.
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (!HasScope(apiKey.Scopes, ReadApiKeys))
            {
                throw new PrivacyDenyException();
            }

            return ApiKeyReadFilter(apiKey);
        }

        // Rule 3: system owners may read every API key.
        if (current?.IsOwner == true)
        {
            return null;
        }

        throw new PrivacyDenyException();
    }

    /// <summary>
    /// Port of the APIKey Mutation policy:
    /// 1. UserProjectScopeWriteRule(write_api_keys),
    /// 2. APIKeyProjectScopeWriteRule(write_api_keys),
    /// 3. OwnerRule().
    /// Returns the ent <c>WhereP(project_id = ...)</c> predicate, or null when
    /// the mutation was allowed without one.
    /// </summary>
    public async Task<long?> EvaluateMutationAsync(
        ApiKeyMutationOp operation,
        long? mutationProjectId,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            if (PermissionValidator.HasSystemScope(loaded, WriteApiKeys))
            {
                return null;
            }

            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    WriteApiKeys))
            {
                switch (operation)
                {
                    case ApiKeyMutationOp.Create:
                        // A project mismatch skips this rule and falls through
                        // to the API-key and owner rules.
                        if (mutationProjectId == contextProjectId)
                        {
                            return null;
                        }

                        break;
                    case ApiKeyMutationOp.UpdateOne:
                    case ApiKeyMutationOp.Update:
                    case ApiKeyMutationOp.DeleteOne:
                    case ApiKeyMutationOp.Delete:
                        return contextProjectId;
                }
            }
        }

        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (!HasScope(apiKey.Scopes, WriteApiKeys))
            {
                throw new PrivacyDenyException();
            }

            switch (operation)
            {
                case ApiKeyMutationOp.Create:
                    if (mutationProjectId is null
                        || mutationProjectId.Value != apiKey.ProjectId)
                    {
                        throw new PrivacyDenyException();
                    }

                    return null;
                case ApiKeyMutationOp.UpdateOne:
                case ApiKeyMutationOp.Update:
                case ApiKeyMutationOp.DeleteOne:
                case ApiKeyMutationOp.Delete:
                    return apiKey.ProjectId;
            }
        }

        if (current?.IsOwner == true)
        {
            return null;
        }

        throw new PrivacyDenyException();
    }

    /// <summary>
    /// Port of <c>getNilableUser</c> (internal/server/gql/graphql.go:249-267):
    /// the User query policy decides visibility; NotFound and deny both yield
    /// null. <paramref name="userId"/> 0 (ent's zero value when the edge is
    /// unset) yields null before any query.
    /// </summary>
    public async Task<UserRecord?> GetVisibleUserOrNullAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        if (userId == 0)
        {
            return null;
        }

        var target = await users.GetByIdAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            // OwnerRule skips, both project rules skip, UserReadScopeRule
            // denies (no user in context) — getNilableUser drops the error.
            return null;
        }

        // OwnerRule().
        if (current.IsOwner)
        {
            return target;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        var contextProjectId = identity.GetCurrentProjectId();
        if (contextProjectId is not null)
        {
            // ProjectOwnerReadUsersRule().
            if (PermissionValidator.UserIsProjectOwner(loaded, contextProjectId.Value))
            {
                return await IsProjectMemberAsync(
                        target.Id,
                        contextProjectId.Value,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ? target
                    : null;
            }

            // ProjectMemberReadUsersRule(read_users): a system scope keeps the
            // unrestricted view; a project scope narrows to members.
            if (PermissionValidator.HasSystemScope(loaded, ReadUsers))
            {
                return target;
            }

            if (PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadUsers))
            {
                return await IsProjectMemberAsync(
                        target.Id,
                        contextProjectId.Value,
                        cancellationToken)
                    .ConfigureAwait(false)
                    ? target
                    : null;
            }
        }

        // UserReadScopeRule(read_users).
        if (PermissionValidator.HasSystemScope(loaded, ReadUsers))
        {
            return target;
        }

        // UserOwnedQueryRule(): the last rule may only see the caller.
        return target.Id == current.Id ? target : null;
    }

    /// <summary>
    /// Emulation of the Project Query policy
    /// (internal/ent/schema/project.go:122-134): returns null when the read is
    /// filtered out (ent NotFound), throws when the policy denies.
    /// </summary>
    public async Task<ProjectRecord?> GetVisibleProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        var contextProjectId = identity.GetCurrentProjectId();
        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        // Rule 1: UserProjectScopeReadRule(read_projects) filters the query to
        // the request's project (a system scope included).
        if (contextProjectId is not null && current is not null
            && (PermissionValidator.HasSystemScope(loaded, ReadProjects)
                || PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadProjects)))
        {
            return project is not null && projectId == contextProjectId.Value
                ? project
                : null;
        }

        // Rule 2: OwnerRule().
        if (current?.IsOwner == true)
        {
            return project;
        }

        // Rule 3: UserReadScopeRule(read_projects).
        if (current is not null
            && PermissionValidator.HasSystemScope(loaded, ReadProjects))
        {
            return project;
        }

        throw new PrivacyDenyException();
    }

    private static ApiKeyAccessFilter ApiKeyReadFilter(ApiKeyRecord apiKey) =>
        string.Equals(apiKey.Type, "personal", StringComparison.Ordinal)
            ? new ApiKeyAccessFilter
            {
                ProjectId = apiKey.ProjectId,
                // Ent's APIKey.UserID is a non-nullable int; an unset edge
                // compares user_id = 0, which matches no personal key.
                PersonalUserId = apiKey.UserId ?? 0,
            }
            : new ApiKeyAccessFilter
            {
                ProjectId = apiKey.ProjectId,
                ExcludePersonal = true,
            };

    private async Task<bool> IsProjectMemberAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken)
    {
        var memberships = await users.ListProjectUsersForUserAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        return memberships.Any(membership => membership.ProjectId == projectId);
    }

    private static bool HasScope(string? scopesJson, string scope)
    {
        var scopes = ManagementMapping.ParseScopes(scopesJson);
        return scopes is not null
            && scopes.Contains(scope, StringComparer.Ordinal);
    }
}
