using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>The ent mutation operation whose policy is being evaluated.</summary>
public enum PromptMutationOp
{
    Create,
    UpdateOne,
    Update,
    DeleteOne,
    Delete,
}

/// <summary>
/// Emulation of the Prompt schema's ent privacy policies
/// (internal/ent/schema/prompt.go:93-106, internal/scopes/rule_*.go). The
/// reference evaluates them inside every ent query/mutation; the port applies
/// the same decisions explicitly before touching the store (C188).
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>OwnerRule()</c> → <c>UserProjectScopeReadRule(read_prompts)</c>
/// → <c>UserReadScopeRule(read_prompts)</c>, then <c>default deny</c> when
/// every rule skipped (internal/scopes/policy.go:51). The project rule returns
/// the ent <c>WhereP(project_id = ctx project)</c> predicate, which the caller
/// passes to the store as <see cref="PromptListQuery.ProjectScope"/>.
/// </para>
/// <para>
/// Mutation policy: <c>OwnerRule()</c> → <c>UserProjectScopeWriteRule(write_prompts)</c>
/// → <c>UserWriteScopeRule(write_prompts)</c> → default deny. The service
/// always adds the same project predicate explicitly, so the mutation rule is
/// a decision only.
/// </para>
/// <para>
/// Prompt has no API-key rule: a service-account API key in the admin GraphQL
/// surface has no user in context, so both policies end at
/// <see cref="NoUserInContext"/> (unlike the Model and APIKey schemas).
/// </para>
/// </remarks>
public sealed class PromptPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadPrompts = "read_prompts";

    public const string WritePrompts = "write_prompts";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>
    /// Port of the Prompt Query policy. Returns the project predicate the
    /// policy installed, or null when the query is unfiltered.
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

        // Rule 2: UserProjectScopeReadRule — needs both a user and a project
        // in context; a system or project scope filters to that project.
        var contextProjectId = identity.GetCurrentProjectId();
        if (current is not null && contextProjectId is not null
            && (PermissionValidator.HasSystemScope(loaded, ReadPrompts)
                || PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    ReadPrompts)))
        {
            return contextProjectId;
        }

        // Rule 3: UserReadScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, ReadPrompts))
        {
            return null;
        }

        // QueryPolicy denies by default once every rule skipped.
        throw Deny(DefaultDeny);
    }

    /// <summary>Port of the Prompt Mutation policy (decision only).</summary>
    /// <param name="operation">The ent operation the service is about to run.</param>
    /// <param name="mutationProjectId">
    /// The project set on a create mutation; ignored by every other operation.
    /// </param>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateMutationAsync(
        PromptMutationOp operation,
        long? mutationProjectId,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule.
        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        PermissionUser? loaded = null;
        if (current is not null)
        {
            loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        // Rule 2: UserProjectScopeWriteRule — a system scope allows without a
        // predicate; a project scope allows only the request's project (a
        // create must target it), installing WhereP(project_id) otherwise.
        if (current is not null)
        {
            if (PermissionValidator.HasSystemScope(loaded, WritePrompts))
            {
                return;
            }

            var contextProjectId = identity.GetCurrentProjectId();
            if (contextProjectId is not null
                && PermissionValidator.UserHasProjectScope(
                    loaded,
                    contextProjectId.Value,
                    WritePrompts))
            {
                if (operation == PromptMutationOp.Create)
                {
                    // A project mismatch skips this rule and falls through to
                    // UserWriteScopeRule.
                    if (mutationProjectId == contextProjectId)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }
        }

        // Rule 3: UserWriteScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        if (PermissionValidator.HasSystemScope(loaded, WritePrompts))
        {
            return;
        }

        // MutationPolicy denies by default once every rule skipped.
        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Wraps a rule message the way ent reports a deny:
    /// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c>.
    /// </summary>
    private static PrivacyDenyException Deny(string message)
        => new(message + ": " + PrivacyDenyException.DenyMessage);
}
