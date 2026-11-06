using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Emulation of the APIKeyProfileTemplate schema's ent privacy policies
/// (internal/ent/schema/api_key_profile_template.go:78-90). A query evaluation
/// produces an <see cref="ApiKeyProfileTemplateAccessFilter"/> (null means "no
/// predicate"); a mutation evaluation produces the ent <c>WhereP(project_id =
/// ...)</c> predicate (null for an unconditional allow). Denying throws
/// <see cref="PrivacyDenyException"/>.
/// </summary>
/// <remarks>
/// Query rules: UserProjectScopeReadRule(read_api_keys),
/// APIKeyProjectScopeReadRule(read_api_keys), OwnerRule(). Mutation rules:
/// UserProjectScopeWriteRule(write_api_keys), OwnerRule() — there is
/// deliberately no API-key mutation rule for templates.
/// </remarks>
public sealed class ApiKeyProfileTemplatePrivacy(
    IManagementIdentity identity,
    IUserAdminStore users)
{
    public const string ReadApiKeys = ApiKeyPrivacy.ReadApiKeys;
    public const string WriteApiKeys = ApiKeyPrivacy.WriteApiKeys;

    /// <summary>Evaluates the query policy for the current request context.</summary>
    public async Task<ApiKeyProfileTemplateAccessFilter?> EvaluateQueryAsync(
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
                return new ApiKeyProfileTemplateAccessFilter
                {
                    ProjectId = contextProjectId,
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

            return new ApiKeyProfileTemplateAccessFilter
            {
                ProjectId = apiKey.ProjectId,
            };
        }

        // Rule 3: system owners may read every template.
        if (current?.IsOwner == true)
        {
            return null;
        }

        throw new PrivacyDenyException();
    }

    /// <summary>
    /// Evaluates the mutation policy for the current request context. Returns
    /// the project predicate to apply to the write, or null when the write was
    /// allowed without one.
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

            // Rule 1: UserProjectScopeWriteRule(write_api_keys).
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
                        // to the owner rule.
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

        // Rule 2: OwnerRule().
        if (current?.IsOwner == true)
        {
            return null;
        }

        throw new PrivacyDenyException();
    }

    private static bool HasScope(string? scopesJson, string scope)
    {
        var scopes = ManagementMapping.ParseScopes(scopesJson);
        return scopes is not null
            && scopes.Contains(scope, StringComparer.Ordinal);
    }
}
