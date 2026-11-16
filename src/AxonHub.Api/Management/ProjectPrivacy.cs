using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the Project schema's ent privacy policies
/// (internal/ent/schema/project.go:122-134, internal/scopes/rule_*.go). The
/// reference evaluates them inside every ent query/mutation; the port applies
/// the same decisions explicitly before touching the store.
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>UserProjectScopeReadRule(read_projects)</c> →
/// <c>OwnerRule()</c> → <c>UserReadScopeRule(read_projects)</c> →
/// default deny. The first rule runs before the owner check and installs
/// <c>WhereID(ctx)</c> for a project header, so an owner who sends the header
/// likewise only sees that project.
/// </para>
/// <para>
/// Mutation policy: <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_projects)</c>
/// → default deny (project.go:129-132). Unlike Role and UserProject, the chain
/// does not start with <c>UserProjectScopeWriteRule</c>, so the
/// <c>*ent.ProjectMutation</c> branch of that rule is unreachable for the
/// Project schema: a non-owner project member — even the membership owner —
/// cannot update or delete the project without a <em>system</em> write_projects
/// scope. The policy is a decision only; no predicate is installed.
/// </para>
/// </remarks>
public sealed class ProjectPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users,
    IProjectStore projects)
{
    public const string ReadProjects = "read_projects";

    public const string WriteProjects = "write_projects";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.Policy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>
    /// Port of the Project Query policy. Returns the project the policy filtered
    /// the query to (<c>id = ctx</c>), or null when the query is unfiltered.
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

            // Rule 1: UserProjectScopeReadRule(read_projects) — *ent.ProjectFilter
            // is narrowed to the request's project for a system or project scope.
            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && (PermissionValidator.HasSystemScope(loaded, ReadProjects)
                    || PermissionValidator.UserHasProjectScope(
                        loaded,
                        contextProjectId.Value,
                        ReadProjects)))
            {
                return contextProjectId;
            }
        }

        // Rule 2: OwnerRule.
        if (current?.IsOwner == true)
        {
            return null;
        }

        // Rule 3: UserReadScopeRule(read_projects) — no user denies; a
        // scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, ReadProjects))
        {
            return null;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>Port of the Project Mutation policy (decision only).</summary>
    /// <remarks>
    /// <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_projects)</c>
    /// (project.go:129-132). The mutation itself is never inspected: a create
    /// and an update/delete-one follow the same decision.
    /// </remarks>
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

        // Rule 2: UserWriteScopeRule(write_projects) — no user denies; a
        // scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, WriteProjects))
        {
            return;
        }

        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the entgql Project query behind a node/edge read
    /// (<c>client.Project.Get(ctx, id)</c>): the query policy filters and
    /// denies, a filtered-out row is ent NotFound.
    /// </summary>
    /// <exception cref="PrivacyDenyException">The query policy denied.</exception>
    public async Task<ProjectRecord?> GetVisibleProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var project = await projects.GetByIdAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        // WhereID(ctx) filters every other project out (ent NotFound).
        return projectScope is not null && projectId != projectScope.Value
            ? null
            : project;
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
