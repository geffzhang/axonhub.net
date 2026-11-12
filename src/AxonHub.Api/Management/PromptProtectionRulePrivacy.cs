using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the PromptProtectionRule schema's ent privacy policy
/// (internal/ent/schema/prompt_protection_rule.go:65-77). The reference
/// evaluates it inside every ent query/mutation; the port applies the same
/// decisions explicitly at the points where the service touches the store
/// (C014).
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>APIKeyScopeQueryRule(read_channels)</c> → <c>OwnerRule()</c>
/// → <c>UserReadScopeRule(read_channels)</c>, then <c>default deny</c> when
/// every rule skipped (internal/scopes/policy.go:51).
/// </para>
/// <para>
/// Mutation policy: <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_channels)</c>
/// → default deny. The reference evaluates it when the ent mutation executes,
/// so validation and the service's internal reads still come first
/// (prompt_protection_rule.go:221-286).
/// </para>
/// <para>
/// The deny texts mirror the reference exactly, including ent's
/// <c>fmt.Errorf("%s: %w", msg, privacy.Deny)</c> suffix; a deny throws
/// <see cref="PrivacyDenyException"/>, which the GraphQL error filter reports
/// as <c>permission denied</c> / FORBIDDEN.
/// </para>
/// </remarks>
public sealed class PromptProtectionRulePrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadChannels = AuthorizationScope.ReadChannels;

    public const string WriteChannels = AuthorizationScope.WriteChannels;

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>Port of the PromptProtectionRule query policy.</summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule
        // (authz.WithTestBypass → privacy.WithBypassPrivacy), including the
        // API-key rule, so it is checked before the policy sequence.
        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: APIKeyScopeQueryRule — only applies to an API-key principal;
        // a context without an API key skips on to the next rule.
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (HasScope(apiKey.Scopes, ReadChannels))
            {
                return;
            }

            throw Deny($"API key does not have required scope: {ReadChannels}");
        }

        // Rule 2: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 3: UserReadScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, ReadChannels))
        {
            return;
        }

        // QueryPolicy denies by default once every rule skipped.
        throw Deny(DefaultDeny);
    }

    /// <summary>
    /// Port of the PromptProtectionRule mutation policy:
    /// <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_channels)</c> → default
    /// deny. An API-key principal has no user, so the write rule denies it with
    /// "no user in context"; the admin route rejects service-account keys
    /// before the resolver anyway.
    /// </summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateMutationAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 2: UserWriteScopeRule — no user denies; a scope-less user skips.
        if (current is null)
        {
            throw Deny(NoUserInContext);
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, WriteChannels))
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

    private static bool HasScope(string? scopesJson, string requiredScope)
        => PermissionValidator.ParseScopes(scopesJson)
            .Contains(requiredScope, StringComparer.Ordinal);
}
