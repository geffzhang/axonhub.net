using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>The ent mutation operation whose policy is being evaluated.</summary>
public enum UserProjectMutationOp
{
    Create,
    Update,
    Delete,
}

/// <summary>
/// Emulation of the UserProject schema's ent privacy policies
/// (internal/ent/schema/user_project.go:67-79, internal/scopes/rule_*.go).
/// The reference evaluates them inside every ent query/mutation; the port
/// applies the same decisions explicitly before touching the store.
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>OwnerRule()</c> → <c>UserProjectScopeReadRule(read_users)</c>
/// → <c>UserOwnedQueryRule()</c>. Unlike the User schema, the owned rule does
/// apply — <c>*ent.UserProjectFilter</c> has <c>WhereUserID</c> — so a caller
/// without any scope sees only their own memberships instead of being denied.
/// A caller with no user in context is denied with <c>no user in context</c>
/// by the owned rule.
/// </para>
/// <para>
/// Mutation policy: <c>OwnerRule()</c> → <c>UserProjectScopeWriteRule(write_users)</c>
/// → default deny. A caller with no user in context skips both rules and ends
/// at <c>default deny</c> (not <c>no user in context</c>).
/// </para>
/// </remarks>
public sealed class UserProjectPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadUsers = "read_users";

    public const string WriteUsers = "write_users";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.Policy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>
    /// Port of the UserProject Query policy. Returns the predicate ent
    /// installed, or null when the query is unfiltered.
    /// </summary>
    /// <exception cref="PrivacyDenyException">A caller with no user in context.</exception>
    public async Task<UserProjectAccessFilter?> EvaluateQueryAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return null;
        }

        // Rule 1: OwnerRule.
        if (current?.IsOwner == true)
        {
            return null;
        }

        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            // Rule 2: UserProjectScopeReadRule(read_users) — a system scope
            // keeps the query on the request's project too; a project scope
            // does the same.
            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && (PermissionValidator.HasSystemScope(loaded, ReadUsers)
                    || PermissionValidator.UserHasProjectScope(
                        loaded,
                        contextProjectId.Value,
                        ReadUsers)))
            {
                return new UserProjectAccessFilter { ProjectId = contextProjectId };
            }
        }

        // Rule 3: UserOwnedQueryRule — no user denies; otherwise the filter
        // narrows to the caller's own memberships.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        return new UserProjectAccessFilter { UserId = current.Id };
    }

    /// <summary>
    /// Port of the UserProject Mutation policy. Returns the project predicate
    /// ent installed on the mutation (<c>project_id = ctx</c>), or null when
    /// the mutation was allowed without one.
    /// </summary>
    /// <param name="operation">The ent operation the service is about to run.</param>
    /// <param name="mutationProjectId">The project the mutation targets.</param>
    /// <exception cref="PrivacyDenyException">
    /// Every rule skipped or denied — including a caller with no user in
    /// context, which the policy reports as <c>default deny</c>.
    /// </exception>
    public async Task<long?> EvaluateMutationAsync(
        UserProjectMutationOp operation,
        long mutationProjectId,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return null;
        }

        // Rule 1: OwnerRule.
        if (current?.IsOwner == true)
        {
            return null;
        }

        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            // Rule 2: UserProjectScopeWriteRule(write_users) — a system scope
            // allows without a predicate.
            if (PermissionValidator.HasSystemScope(loaded, WriteUsers))
            {
                return null;
            }

            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    WriteUsers))
            {
                if (operation == UserProjectMutationOp.Create)
                {
                    // A project mismatch skips this rule and ends at default deny.
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

        // Both rules skipped: MutationPolicy denies by default.
        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
