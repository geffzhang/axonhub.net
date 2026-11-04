using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the Model schema's ent privacy policies
/// (internal/ent/schema/model.go:79-90). The reference evaluates them inside
/// every ent query/mutation; the port applies the same decisions explicitly
/// before touching the store (C177).
/// </summary>
/// <remarks>
/// Query policy: <c>APIKeyScopeQueryRule(read_channels)</c> →
/// <c>OwnerRule()</c> → <c>UserReadScopeRule(read_channels)</c>. Mutation
/// policy: <c>OwnerRule()</c> → <c>UserWriteScopeRule(write_channels)</c> — an
/// API-key principal has no user, so mutations from API keys are denied with
/// <c>no user in context</c> regardless of scopes, exactly like the reference.
/// A deny throws <see cref="PrivacyDenyException"/>, which the GraphQL error
/// filter reports as <c>permission denied</c> / FORBIDDEN.
/// </remarks>
public sealed class ModelPrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadChannels = AuthorizationScope.ReadChannels;

    public const string WriteChannels = AuthorizationScope.WriteChannels;

    /// <summary>Port of the Model Query policy.</summary>
    /// <exception cref="PrivacyDenyException">Every rule skipped or denied.</exception>
    public async Task EvaluateQueryAsync(CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);

        // Rule 1: APIKeyScopeQueryRule — only applies to an API-key principal.
        var apiKey = identity.GetCurrentApiKey();
        if (apiKey is not null)
        {
            if (HasScope(apiKey.Scopes, ReadChannels))
            {
                return;
            }

            throw new PrivacyDenyException();
        }

        // The test principal's decision context bypasses every rule.
        if (current?.IsTest == true)
        {
            return;
        }

        // Rule 2: OwnerRule.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 3: UserReadScopeRule — system scopes only, no project scopes.
        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (PermissionValidator.HasSystemScope(loaded, ReadChannels))
            {
                return;
            }
        }

        // QueryPolicy denies by default.
        throw new PrivacyDenyException();
    }

    /// <summary>Port of the Model Mutation policy.</summary>
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

        // Rule 1: OwnerRule.
        if (current?.IsOwner == true)
        {
            return;
        }

        // Rule 2: UserWriteScopeRule — system scopes only.
        if (current is not null)
        {
            var loaded = await users.GetPermissionUserAsync(current.Id, cancellationToken)
                .ConfigureAwait(false);
            if (PermissionValidator.HasSystemScope(loaded, WriteChannels))
            {
                return;
            }
        }

        // MutationPolicy denies by default.
        throw new PrivacyDenyException();
    }

    private static bool HasScope(string? scopesJson, string requiredScope)
        => PermissionValidator.ParseScopes(scopesJson)
            .Contains(requiredScope, StringComparer.Ordinal);
}
