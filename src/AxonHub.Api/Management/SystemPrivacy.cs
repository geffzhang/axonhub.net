using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the <c>System</c> and <c>OIDCIdentity</c> schema ent privacy
/// policies (internal/ent/schema/system.go:52-64, oidc_identity.go:83-95).
/// The reference evaluates them inside every ent query; the port applies the
/// same decisions explicitly before touching the store, like the other
/// management privacy evaluators.
/// </summary>
/// <remarks>
/// <para>
/// System query policy: <c>OwnerRule()</c> →
/// <c>UserReadScopeRule(read_settings)</c> → default deny. OIDCIdentity query
/// policy: <c>OwnerRule()</c> → <c>UserReadScopeRule(read_users)</c> →
/// <c>UserOwnedQueryRule()</c>, which filters <c>user_id = me</c> instead of
/// denying.
/// </para>
/// <para>
/// A caller with no user in context (a service-account API key) hits the
/// reference's <c>getUserFromContext</c> error (<c>privacy.Denyf(ErrNoUser)</c>)
/// on the first scope rule; the deny surfaces as <c>permission denied</c> with
/// <c>extensions.code = FORBIDDEN</c>. The test principal carries the
/// reference's <c>authz.WithTestBypass</c> decision context (the gql fixtures
/// set it), so every rule is bypassed.
/// </para>
/// </remarks>
public sealed class SystemPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    /// <summary>Port of the System Query policy. Throws when the caller may not list systems.</summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateSystemQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // authz.WithTestBypass installs privacy.DecisionContext(Allow).
        if (current?.IsTest == true)
        {
            return;
        }

        if (current is null)
        {
            throw new PrivacyDenyException(NoUserInContext);
        }

        if (current.IsOwner)
        {
            return;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!PermissionValidator.HasSystemScope(loaded, AuthorizationScope.ReadSettings))
        {
            throw new PrivacyDenyException(DefaultDeny);
        }
    }

    /// <summary>
    /// Port of the OIDCIdentity Query policy. Returns the owner predicate the
    /// UserOwnedQueryRule installs, or null when the query is unfiltered.
    /// </summary>
    /// <exception cref="PrivacyDenyException">A caller with no user in context.</exception>
    public async Task<long?> EvaluateOidcIdentityQueryAsync(
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        if (current?.IsTest == true)
        {
            return null;
        }

        if (current is null)
        {
            throw new PrivacyDenyException(NoUserInContext);
        }

        if (current.IsOwner)
        {
            return null;
        }

        var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
            .ConfigureAwait(false);
        if (PermissionValidator.HasSystemScope(loaded, AuthorizationScope.ReadUsers))
        {
            return null;
        }

        return current.Id;
    }

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:11).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:53).</summary>
    private const string DefaultDeny = "default deny";
}
