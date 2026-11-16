using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>The ent mutation operation whose policy is being evaluated.</summary>
public enum RoleMutationOp
{
    Create,
    Update,
    UpdateOne,
    DeleteOne,
    Delete,
}

/// <summary>
/// Emulation of the Role schema's ent privacy policies
/// (internal/ent/schema/role.go:85-97, internal/scopes/rule_*.go). The
/// reference evaluates them inside every ent query/mutation; the port applies
/// the same decisions explicitly before touching the store.
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>UserProjectScopeReadRule(read_roles)</c> →
/// <c>OwnerRule()</c> → <c>UserReadScopeRule(read_roles)</c> →
/// default deny. The first rule runs before the owner check and returns
/// <c>WhereProjectID(ctx)</c> — an owner who sends a project header is filtered
/// exactly like any other caller (HasSystemScope is true for owners).
/// </para>
/// <para>
/// Mutation policy: <c>UserProjectScopeWriteRule(write_roles)</c> →
/// <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_roles)</c>. A system
/// write_roles scope (owner included) allows unconditionally; a project scope
/// allows a create only when the mutation targets that project and installs
/// <c>WhereP(project_id = ctx)</c> on updates/deletes. Everything else falls
/// through to default deny.
/// </para>
/// </remarks>
public sealed class RolePrivacy(
    IManagementIdentity identity,
    IUserAdminStore users,
    IRoleStore roles)
{
    public const string ReadRoles = "read_roles";

    public const string WriteRoles = "write_roles";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.Policy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>
    /// Port of the Role Query policy. Returns the project the policy filtered
    /// the query to, or null when the query is unfiltered.
    /// </summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task<long?> EvaluateQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return null;
        }

        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            // Rule 1: UserProjectScopeReadRule(read_roles) — *ent.RoleFilter
            // implements ProjectOwnedFilter, so a project header plus a system
            // or project scope narrows the query to that project.
            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && (PermissionValidator.HasSystemScope(loaded, ReadRoles)
                    || PermissionValidator.UserHasProjectScope(
                        loaded,
                        contextProjectId.Value,
                        ReadRoles)))
            {
                return contextProjectId;
            }
        }

        // Rule 2: OwnerRule.
        if (current?.IsOwner == true)
        {
            return null;
        }

        // Rule 3: UserReadScopeRule(read_roles) — no user denies; a scope-less
        // user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, ReadRoles))
        {
            return null;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the Role Mutation policy. Returns the project predicate ent
    /// installed on the mutation (<c>project_id = ctx</c>), or null when the
    /// mutation was allowed without one.
    /// </summary>
    /// <param name="operation">The ent operation the service is about to run.</param>
    /// <param name="mutationProjectId">
    /// The project set on a create mutation; ignored by the other operations.
    /// </param>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task<long?> EvaluateMutationAsync(
        RoleMutationOp operation,
        long? mutationProjectId,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return null;
        }

        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);

            // Rule 1: UserProjectScopeWriteRule(write_roles) — a system scope
            // allows without a predicate.
            if (PermissionValidator.HasSystemScope(loaded, WriteRoles))
            {
                return null;
            }

            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    WriteRoles))
            {
                if (operation == RoleMutationOp.Create)
                {
                    // A project mismatch skips this rule and falls through to
                    // UserWriteScopeRule.
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

        // Rule 2: OwnerRule.
        if (current?.IsOwner == true)
        {
            return null;
        }

        // Rule 3: UserWriteScopeRule(write_roles) — no user denies; a
        // scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, WriteRoles))
        {
            return null;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the entgql Role query behind a node/edge read
    /// (<c>client.Role.Get(ctx, id)</c>): the query policy filters and denies,
    /// a filtered-out or missing row is <c>ent: role not found</c>.
    /// </summary>
    /// <exception cref="PrivacyDenyException">The query policy denied.</exception>
    /// <exception cref="InvalidOperationException">The role is missing or filtered out.</exception>
    public async Task<RoleRecord> GetVisibleRoleOrThrowAsync(
        long roleId,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var role = await roles.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            throw new InvalidOperationException("ent: role not found");
        }

        if (projectScope is not null && (role.ProjectId ?? 0) != projectScope.Value)
        {
            // The policy's WhereProjectID filtered the row out.
            throw new InvalidOperationException("ent: role not found");
        }

        return role;
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
