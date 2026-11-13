using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the User schema's ent privacy policies
/// (internal/ent/schema/user.go:100-114, internal/scopes/rule_*.go). The
/// reference evaluates them inside every ent query/mutation; the port applies
/// the same decisions explicitly before touching the store, like the other
/// management privacy evaluators.
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>OwnerRule()</c> → <c>ProjectOwnerReadUsersRule()</c> →
/// <c>ProjectMemberReadUsersRule(read_users)</c> →
/// <c>UserReadScopeRule(read_users)</c> → <c>UserOwnedQueryRule()</c>, then
/// <c>default deny</c> when every rule skipped (policy.go:51). The two project
/// rules install <c>WhereHasProjectUsersWith(project_id = ctx)</c>, which the
/// caller passes to the store as
/// <see cref="UserListFilter.HasProjectUsersWithProjectId"/>.
/// </para>
/// <para>
/// <c>UserOwnedQueryRule</c> never applies to a <c>*ent.UserFilter</c> — the
/// generated filter has no <c>WhereUserID</c> — so a non-owner without any
/// read_users scope ends at the policy default deny rather than a self-only
/// filter.
/// </para>
/// <para>
/// Mutation policy: <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_users)</c>
/// → default deny. It is a decision only; the reference installs no predicate.
/// </para>
/// </remarks>
public sealed class UserPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadUsers = "read_users";

    public const string WriteUsers = "write_users";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>
    /// Port of the User Query policy. Returns the project the policy filtered
    /// the query to (its members), or null when the query is unfiltered.
    /// </summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task<long?> EvaluateQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule
        // (authz.WithTestBypass → privacy.WithBypassPrivacy).
        if (current?.IsTest == true)
        {
            return null;
        }

        // Rule 1: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return null;
        }

        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        var contextProjectId = identity.GetCurrentProjectId();
        if (current is not null && contextProjectId is not null)
        {
            // Rule 2: ProjectOwnerReadUsersRule — a project owner sees the
            // users of that project.
            if (PermissionValidator.UserIsProjectOwner(loaded, contextProjectId.Value))
            {
                return contextProjectId;
            }

            // Rule 3: ProjectMemberReadUsersRule(read_users) — a system scope
            // keeps the unrestricted view; a project scope narrows to members.
            if (PermissionValidator.HasSystemScope(loaded, ReadUsers))
            {
                return null;
            }

            if (PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadUsers))
            {
                return contextProjectId;
            }
        }

        // Rule 4: UserReadScopeRule(read_users) — no user denies; a scope-less
        // user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, ReadUsers))
        {
            return null;
        }

        // Rule 5: UserOwnedQueryRule skips for *ent.UserFilter (no
        // WhereUserID); QueryPolicy denies by default.
        throw Deny(DefaultDeny);
    }

    /// <summary>Port of the User Mutation policy (decision only).</summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateMutationAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: OwnerRule.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 2: UserWriteScopeRule(write_users) — no user denies, a
        // scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, WriteUsers))
        {
            return;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the entgql User query behind a required edge
    /// (<c>client.User.Get(ctx, id)</c>): the query policy filters and denies,
    /// a filtered-out or missing row is <c>ent: user not found</c>, and a deny
    /// propagates.
    /// </summary>
    /// <exception cref="PrivacyDenyException">The query policy denied.</exception>
    /// <exception cref="InvalidOperationException">The user is missing or filtered out.</exception>
    public async Task<UserRecord> GetVisibleUserOrThrowAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // The reference evaluates the query policy before the SQL runs.
        var projectScope = await EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var target = await users.GetByIdAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        if (target is null)
        {
            throw new InvalidOperationException("ent: user not found");
        }

        if (projectScope is not null
            && !await IsProjectMemberAsync(userId, projectScope.Value, cancellationToken)
                .ConfigureAwait(false))
        {
            // The policy's WhereHasProjectUsersWith filtered the row out.
            throw new InvalidOperationException("ent: user not found");
        }

        return target;
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

        try
        {
            var projectScope = await EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);

            var target = await users.GetByIdAsync(userId, cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                return null;
            }

            if (projectScope is not null
                && !await IsProjectMemberAsync(userId, projectScope.Value, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }

            return target;
        }
        catch (PrivacyDenyException)
        {
            // getNilableUser drops the deny sentinel.
            return null;
        }
    }

    private async Task<bool> IsProjectMemberAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken)
    {
        var memberships = await users.ListProjectUsersForUserAsync(userId, cancellationToken)
            .ConfigureAwait(false);
        return memberships.Any(membership => membership.ProjectId == projectId);
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
