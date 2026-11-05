using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the DataStorage schema's ent privacy policies
/// (internal/ent/schema/data_storage.go:88-104 at f272549f,
/// internal/scopes/rule_*.go). The reference evaluates them inside every ent
/// query/mutation; the port applies the same decisions explicitly before
/// touching the store (C196).
/// </summary>
/// <remarks>
/// <para>
/// Query policy: <c>APIKeyScopeQueryRule(read_data_storages)</c> →
/// <c>UserReadScopeRule(read_data_storages)</c> → <c>OwnerRule()</c>, then
/// <c>default deny</c> when every rule skipped (internal/scopes/policy.go:51).
/// Mutation policy: <c>UserWriteScopeRule(write_data_storages)</c> →
/// <c>OwnerRule()</c> → default deny.
/// </para>
/// <para>
/// The baseline b8bb3421 gated the API-key query rule on
/// <c>write_requests</c>; e5c904ef ("fix(api-keys): enforce scope grants for
/// service account keys") moved data storages to the dedicated
/// <c>read_data_storages</c> scope. This slice follows f272549f (C196).
/// </para>
/// <para>
/// A deny throws <see cref="PrivacyDenyException"/>, which the GraphQL error
/// filter reports as <c>permission denied</c> / FORBIDDEN. Service-account API
/// keys never see a user in context, so the user rules skip and the API-key
/// rule (or the default deny) decides.
/// </para>
/// </remarks>
public sealed class DataStoragePrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadDataStorages = "read_data_storages";

    public const string WriteDataStorages = "write_data_storages";

    /// <summary>The reference's <c>scopes.ErrNoUser</c> (internal/scopes/rule.go:18).</summary>
    private const string NoUserInContext = "no user in context";

    /// <summary>The reference's <c>scopes.QueryPolicy</c> default (policy.go:51).</summary>
    private const string DefaultDeny = "default deny";

    /// <summary>Port of the DataStorage Query policy.</summary>
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
            if (HasScope(apiKey.Scopes, ReadDataStorages))
            {
                return;
            }

            throw Deny($"API key does not have required scope: {ReadDataStorages}");
        }

        // Rule 2: UserReadScopeRule — no user skips; a user without the system
        // scope skips.
        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (PermissionValidator.HasSystemScope(loaded, ReadDataStorages))
            {
                return;
            }
        }

        // Rule 3: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        // QueryPolicy denies by default once every rule skipped. With a user in
        // context the reference never reaches this branch unless the user has
        // neither the scope nor ownership, which is still "default deny".
        throw Deny(current is null ? NoUserInContext : DefaultDeny);
    }

    /// <summary>Port of the DataStorage Mutation policy.</summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateMutationAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // The test principal's decision context bypasses every rule.
        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 1: UserWriteScopeRule — no user denies; a scope-less user skips.
        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (PermissionValidator.HasSystemScope(loaded, WriteDataStorages))
            {
                return;
            }
        }

        // Rule 2: OwnerRule — no user skips, non-owner skips.
        if (current?.IsOwner == true)
        {
            return;
        }

        throw Deny(current is null ? NoUserInContext : DefaultDeny);
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
