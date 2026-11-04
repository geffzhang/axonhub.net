using System.Security.Cryptography;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of ent.CreateAPIKeyInput (ent.graphql:…).</summary>
public sealed record CreateApiKeyCommand
{
    public required string Name { get; init; }

    public required long ProjectId { get; init; }

    /// <summary>user, service_account, noauth, or personal; nil means "user".</summary>
    public string? Type { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<string>? AllowedIps { get; init; }
}

/// <summary>Port of ent.UpdateAPIKeyInput (ent.graphql:…).</summary>
public sealed record UpdateApiKeyCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<string>? AppendScopes { get; init; }

    public bool ClearScopes { get; init; }

    public IReadOnlyList<string>? AllowedIps { get; init; }

    public IReadOnlyList<string>? AppendAllowedIps { get; init; }

    public bool ClearAllowedIps { get; init; }
}

/// <summary>
/// Port of biz.APIKeyService (internal/server/biz/api_key.go). The reference's
/// GraphQL Transactioner wraps every mutation operation in one transaction; the
/// port starts a transaction per write method instead (C176). API-key cache
/// invalidation and the deferred surfaces (quota usage, token stats, profile
/// templates, requests edge, UpdateAPIKeyScopes, CreateLLMAPIKey, GetForRead)
/// are not ported in this slice.
/// </summary>
public sealed class ApiKeyManagementService(
    IApiKeyStore apiKeys,
    IManagementIdentity identity,
    PermissionValidator permissions,
    ApiKeyPrivacy privacy,
    ApiKeyAuthOptions authOptions)
{
    public const string ApiKeyNotFound = "ent: api_key not found";

    private const string UserType = "user";
    private const string PersonalType = "personal";
    private const string ServiceAccountType = "service_account";
    private const string NoauthType = "noauth";

    /// <summary>Port of APIKeyService.CreateAPIKey (biz/api_key.go:291-354).</summary>
    public async Task<ApiKeyRecord> CreateAsync(
        CreateApiKeyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);

        var apiKeyType = UserType;
        if (command.Type is not null)
        {
            if (command.Type == NoauthType)
            {
                throw new InvalidOperationException("noauth type API key is reserved");
            }

            apiKeyType = command.Type;
        }

        if (apiKeyType == UserType)
        {
            await RequireProjectAdminAsync(current.Id, command.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        var generatedKey = GenerateApiKey();

        return await apiKeys.RunInTransactionAsync(
                async token =>
                {
                    await apiKeys.LockProjectForNameAsync(command.ProjectId, token)
                        .ConfigureAwait(false);

                    try
                    {
                        await privacy.EvaluateMutationAsync(
                                ApiKeyMutationOp.Create,
                                command.ProjectId,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InvalidOperationException(
                            "failed to create API key: " + ex.Message,
                            ex);
                    }

                    string? scopes = null;
                    if (apiKeyType == ServiceAccountType)
                    {
                        // Service account uses the provided scopes or an empty
                        // array; user type keeps the schema default.
                        scopes = JsonSerializer.Serialize(
                            command.Scopes ?? Array.Empty<string>());
                    }

                    string? allowedIps = null;
                    if (command.AllowedIps is { Count: > 0 })
                    {
                        ApiKeyProfilesCodec.ValidateAllowedIps(command.AllowedIps);
                        allowedIps = JsonSerializer.Serialize(command.AllowedIps);
                    }

                    var row = new ApiKeyRecord
                    {
                        Name = command.Name,
                        Key = generatedKey,
                        UserId = current.Id,
                        ProjectId = command.ProjectId,
                        Type = apiKeyType,
                        Scopes = scopes,
                        AllowedIps = allowedIps,
                    };

                    long id;
                    try
                    {
                        id = await apiKeys.CreateAsync(row, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InvalidOperationException(
                            "failed to create API key: " + ex.Message,
                            ex);
                    }

                    // Save runs the mutation policy before this bypassed
                    // uniqueness query; an error rolls the insert back with
                    // this transaction.
                    int duplicateCount;
                    try
                    {
                        duplicateCount = await apiKeys.CountAsync(
                                new ApiKeyListFilter
                                {
                                    Name = command.Name,
                                    ProjectId = command.ProjectId,
                                    Or =
                                    [
                                        new ApiKeyListFilter { TypeNotIn = [PersonalType] },
                                        new ApiKeyListFilter { UserId = current.Id },
                                    ],
                                },
                                access: null,
                                includeDeleted: false,
                                token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InvalidOperationException(
                            "failed to check API key name uniqueness: " + ex.Message,
                            ex);
                    }

                    if (duplicateCount > 1)
                    {
                        throw DuplicateName(command.Name);
                    }

                    return await apiKeys.GetByIdAsync(id, access: null, token)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "failed to create API key: " + ApiKeyNotFound);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of the privacy-filtered entgql APIKey connection query.</summary>
    public async Task<ListPage<ApiKeyRecord>> ListAsync(
        ApiKeyListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var access = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return await apiKeys.ListAsync(query with { Access = access }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of the entgql Node lookup for APIKey (privacy applies).</summary>
    public async Task<ApiKeyRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var access = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await apiKeys.GetByIdAsync(id, access, cancellationToken)
            .ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(ApiKeyNotFound);
    }

    /// <summary>Port of APIKeyService.UpdateAPIKey (biz/api_key.go:413-517).</summary>
    public Task<ApiKeyRecord> UpdateAsync(
        UpdateApiKeyCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return apiKeys.RunInTransactionAsync(
            async token =>
            {
                var existing = await GetForMutationAsync(command.Id, token)
                    .ConfigureAwait(false);

                if (existing.Type is UserType or PersonalType
                    && (command.Scopes is { Count: > 0 }
                        || command.AppendScopes is { Count: > 0 }
                        || command.ClearScopes))
                {
                    throw new InvalidOperationException(
                        existing.Type + " type API key cannot update scopes");
                }

                if (existing.Type == NoauthType)
                {
                    throw new InvalidOperationException("noauth type API key cannot be updated");
                }

                ManagementUser? current = null;
                if (existing.Type == PersonalType)
                {
                    current = await GetCurrentUserAsync(token).ConfigureAwait(false);
                    if ((existing.UserId ?? 0) != current.Id && !current.IsOwner)
                    {
                        throw new InvalidOperationException(
                            "personal API key can only be modified by its creator or a system owner");
                    }
                }

                if (command.Name is not null && command.Name != existing.Name)
                {
                    await apiKeys.LockProjectForNameAsync(existing.ProjectId, token)
                        .ConfigureAwait(false);
                }

                long? projectWritePredicate;
                try
                {
                    projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.UpdateOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update API key: " + ex.Message,
                        ex);
                }

                IReadOnlyList<string>? scopes = null;
                IReadOnlyList<string>? appendScopes = null;
                var clearScopes = false;
                IReadOnlyList<string>? allowedIps = null;
                IReadOnlyList<string>? appendAllowedIps = null;

                if (existing.Type == ServiceAccountType)
                {
                    if (command.Scopes is { Count: > 0 })
                    {
                        scopes = command.Scopes;
                    }

                    if (command.AppendScopes is { Count: > 0 })
                    {
                        appendScopes = command.AppendScopes;
                    }

                    clearScopes = command.ClearScopes;
                }

                if (command.AllowedIps is { Count: > 0 })
                {
                    ApiKeyProfilesCodec.ValidateAllowedIps(command.AllowedIps);
                    allowedIps = command.AllowedIps;
                }

                if (command.AppendAllowedIps is { Count: > 0 })
                {
                    ApiKeyProfilesCodec.ValidateAllowedIps(command.AppendAllowedIps);
                    appendAllowedIps = command.AppendAllowedIps;
                }

                ApiKeyRecord? updated;
                try
                {
                    updated = await apiKeys.UpdateAsync(
                            new ApiKeyUpdate
                            {
                                Id = command.Id,
                                Name = command.Name,
                                Scopes = scopes,
                                AppendScopes = appendScopes,
                                ClearScopes = clearScopes,
                                AllowedIps = allowedIps,
                                AppendAllowedIps = appendAllowedIps,
                                ClearAllowedIps = command.ClearAllowedIps,
                                ProjectWritePredicate = projectWritePredicate,
                            },
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update API key: " + ex.Message,
                        ex);
                }

                if (updated is null)
                {
                    throw new InvalidOperationException(
                        "failed to update API key: " + ApiKeyNotFound);
                }

                if (command.Name is not null && command.Name != existing.Name)
                {
                    await CheckRenamedApiKeyUniquenessAsync(
                            command.Name,
                            existing,
                            current,
                            token)
                        .ConfigureAwait(false);
                }

                return updated;
            },
            cancellationToken);
    }

    /// <summary>
    /// Port of APIKeyService.UpdateAPIKeyStatus (biz/api_key.go:519-546). The
    /// reference has no explicit transaction here; the GraphQL Transactioner
    /// still supplies one for the operation.
    /// </summary>
    public Task<ApiKeyRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
        => apiKeys.RunInTransactionAsync(
            async token =>
            {
                var existing = await GetForMutationAsync(id, token).ConfigureAwait(false);

                if (existing.Type == NoauthType)
                {
                    throw new InvalidOperationException(
                        "noauth type API key status cannot be updated");
                }

                if (existing.Type == PersonalType)
                {
                    var user = await GetCurrentUserAsync(token).ConfigureAwait(false);
                    if ((existing.UserId ?? 0) != user.Id && !user.IsOwner)
                    {
                        throw new InvalidOperationException(
                            "personal API key can only be modified by its creator or a system owner");
                    }
                }

                long? projectWritePredicate;
                try
                {
                    projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.UpdateOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update API key status: " + ex.Message,
                        ex);
                }

                ApiKeyRecord? updated;
                try
                {
                    updated = await apiKeys.UpdateAsync(
                            new ApiKeyUpdate
                            {
                                Id = id,
                                Status = status,
                                ProjectWritePredicate = projectWritePredicate,
                            },
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update API key status: " + ex.Message,
                        ex);
                }

                return updated ?? throw new InvalidOperationException(
                    "failed to update API key status: " + ApiKeyNotFound);
            },
            cancellationToken);

    /// <summary>
    /// Port of APIKeyService.UpdateAPIKeyProfiles (biz/api_key.go:620-679).
    /// </summary>
    public Task<ApiKeyRecord> UpdateProfilesAsync(
        long id,
        ApiKeyProfilesModel profiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        return apiKeys.RunInTransactionAsync(
            async token =>
            {
                var existing = await GetForMutationAsync(id, token).ConfigureAwait(false);

                if (existing.Type == NoauthType)
                {
                    throw new InvalidOperationException(
                        "noauth type API key profiles cannot be updated");
                }

                if (existing.Type == PersonalType)
                {
                    var user = await GetCurrentUserAsync(token).ConfigureAwait(false);
                    if ((existing.UserId ?? 0) != user.Id && !user.IsOwner)
                    {
                        throw new InvalidOperationException(
                            "personal API key can only be modified by its creator or a system owner");
                    }
                }

                // Validation order matches the reference: names, active
                // profile, filters, routing policies (normalized), quota.
                var normalized = ApiKeyProfilesCodec.ValidateAndNormalize(profiles);
                normalized = ApiKeyProfilesCodec.DetachModifiedTemplateProfiles(
                    ApiKeyProfilesCodec.Deserialize(existing.Profiles),
                    normalized);

                long? projectWritePredicate;
                try
                {
                    projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.UpdateOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update API key profiles: " + ex.Message,
                        ex);
                }

                ApiKeyRecord? updated;
                try
                {
                    updated = await apiKeys.UpdateAsync(
                            new ApiKeyUpdate
                            {
                                Id = id,
                                Profiles = ApiKeyProfilesCodec.Serialize(normalized),
                                SetProfiles = true,
                                ProjectWritePredicate = projectWritePredicate,
                            },
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update API key profiles: " + ex.Message,
                        ex);
                }

                return updated ?? throw new InvalidOperationException(
                    "failed to update API key profiles: " + ApiKeyNotFound);
            },
            cancellationToken);
    }

    /// <summary>Port of APIKeyService.RotateAPIKey (biz/api_key.go:1109-1147).</summary>
    public Task<ApiKeyRecord> RotateAsync(
        long id,
        CancellationToken cancellationToken = default)
        => apiKeys.RunInTransactionAsync(
            async token =>
            {
                var existing = await GetForMutationAsync(id, token).ConfigureAwait(false);

                if (existing.Type == NoauthType)
                {
                    throw new InvalidOperationException(
                        "noauth type API key cannot be rotated");
                }

                if (existing.Type == PersonalType)
                {
                    var user = await GetCurrentUserAsync(token).ConfigureAwait(false);
                    if ((existing.UserId ?? 0) != user.Id && !user.IsOwner)
                    {
                        throw new InvalidOperationException(
                            "personal API key can only be rotated by its creator or a system owner");
                    }
                }

                var newKey = GenerateApiKey();

                long? projectWritePredicate;
                try
                {
                    projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.UpdateOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to rotate API key: " + ex.Message,
                        ex);
                }

                ApiKeyRecord? rotated;
                try
                {
                    rotated = await apiKeys.UpdateAsync(
                            new ApiKeyUpdate
                            {
                                Id = id,
                                Key = newKey,
                                ProjectWritePredicate = projectWritePredicate,
                            },
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to rotate API key: " + ex.Message,
                        ex);
                }

                return rotated ?? throw new InvalidOperationException(
                    "failed to rotate API key: " + ApiKeyNotFound);
            },
            cancellationToken);

    public Task BulkDisableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "disabled", "disable", cancellationToken);

    public Task BulkEnableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "enabled", "enable", cancellationToken);

    public Task BulkArchiveAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(ids, "archived", "archive", cancellationToken);

    /// <summary>
    /// Port of APIKeyService.bulkUpdateAPIKeyStatus (biz/api_key.go:1022-1091).
    /// The reference's final cache-invalidation query is omitted (no cache in
    /// the port), so its "failed to query API keys for cache invalidation"
    /// error path is unreachable here (C176).
    /// </summary>
    private Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return apiKeys.RunInTransactionAsync(
            async token =>
            {
                ApiKeyAccessFilter? access;
                try
                {
                    access = await privacy.EvaluateQueryAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to query API keys: " + ex.Message,
                        ex);
                }

                int count;
                try
                {
                    count = await apiKeys.CountAsync(
                            new ApiKeyListFilter { IdIn = ids },
                            access,
                            includeDeleted: false,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to query API keys: " + ex.Message,
                        ex);
                }

                if (count != ids.Count)
                {
                    throw new InvalidOperationException(
                        $"expected to find {ids.Count} API keys, but found {count}");
                }

                bool noAuthExists;
                try
                {
                    noAuthExists = await apiKeys.ExistsAsync(
                            new ApiKeyListFilter { IdIn = ids, Type = NoauthType },
                            access,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to validate API keys for bulk " + action + ": " + ex.Message,
                        ex);
                }

                if (noAuthExists)
                {
                    throw new InvalidOperationException(
                        "noauth type API key cannot be bulk " + action + "d");
                }

                IReadOnlyList<ApiKeyRecord> personalKeys;
                try
                {
                    personalKeys = await apiKeys.ListAllAsync(
                            new ApiKeyListFilter { IdIn = ids, Type = PersonalType },
                            access,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to query personal API keys: " + ex.Message,
                        ex);
                }

                if (personalKeys.Count > 0)
                {
                    var user = await GetCurrentUserAsync(token).ConfigureAwait(false);
                    foreach (var key in personalKeys)
                    {
                        if ((key.UserId ?? 0) != user.Id && !user.IsOwner)
                        {
                            throw new InvalidOperationException(
                                "personal API key " + GoCast.GoQuote(key.Name)
                                + " can only be " + action
                                + "d by its creator or a system owner");
                        }
                    }
                }

                long? projectWritePredicate;
                try
                {
                    projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.Update,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to " + action + " API keys: " + ex.Message,
                        ex);
                }

                try
                {
                    await apiKeys.BulkUpdateStatusAsync(
                            ids,
                            status,
                            projectWritePredicate,
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to " + action + " API keys: " + ex.Message,
                        ex);
                }
            },
            cancellationToken);
    }

    private async Task<ApiKeyRecord> GetForMutationAsync(
        long id,
        CancellationToken cancellationToken)
    {
        ApiKeyAccessFilter? access;
        try
        {
            access = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            var record = await apiKeys.GetByIdAsync(id, access, cancellationToken)
                .ConfigureAwait(false);
            if (record is not null)
            {
                return record;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get API key: " + ex.Message,
                ex);
        }

        throw new InvalidOperationException("failed to get API key: " + ApiKeyNotFound);
    }

    /// <summary>
    /// Post-rename uniqueness check (biz/api_key.go:479-500). Personal keys are
    /// compared against their creator, everything else against the requesting
    /// user when present; keys of other users' personal scope never collide.
    /// </summary>
    private async Task CheckRenamedApiKeyUniquenessAsync(
        string name,
        ApiKeyRecord existing,
        ManagementUser? current,
        CancellationToken cancellationToken)
    {
        var nameScope = new List<ApiKeyListFilter>
        {
            new() { TypeNotIn = [PersonalType] },
        };

        if (existing.Type == PersonalType)
        {
            nameScope.Add(new ApiKeyListFilter { UserId = existing.UserId ?? 0 });
        }
        else if (current is not null)
        {
            nameScope.Add(new ApiKeyListFilter { UserId = current.Id });
        }

        int duplicateCount;
        try
        {
            duplicateCount = await apiKeys.CountAsync(
                    new ApiKeyListFilter
                    {
                        Name = name,
                        ProjectId = existing.ProjectId,
                        Or = nameScope,
                    },
                    access: null,
                    includeDeleted: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to check API key name uniqueness: " + ex.Message,
                ex);
        }

        if (duplicateCount > 1)
        {
            throw DuplicateName(name);
        }
    }

    /// <summary>Port of APIKeyService.requireProjectAdmin (biz/api_key.go:396-411).</summary>
    private async Task RequireProjectAdminAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken)
    {
        PermissionUser? loaded;
        try
        {
            loaded = await permissions.GetPermissionUserAsync(userId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to load API key creator permissions: " + ex.Message,
                ex);
        }

        if (loaded is null)
        {
            throw new InvalidOperationException(
                "failed to load API key creator permissions: " + PermissionValidator.UserNotFound);
        }

        try
        {
            PermissionValidator.CanGrantScopesForUser(
                loaded,
                ["write_users", "write_roles"],
                projectId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The reference discards the underlying error and reports only
            // this message (biz/api_key.go:407-409).
            throw new InvalidOperationException(
                "permission denied: project API keys require project admin permissions");
        }
    }

    private async Task<ManagementUser> GetCurrentUserAsync(CancellationToken cancellationToken)
        => await identity.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(PermissionValidator.UserNotFoundInContext);

    /// <summary>Port of GenerateAPIKey (biz/api_key.go:170-185).</summary>
    private string GenerateApiKey()
    {
        var prefix = authOptions.KeyPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new InvalidOperationException("api key prefix must not be empty");
        }

        return prefix + "-"
            + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    }

    private static CodedException DuplicateName(string name) => new(
        CodedException.DuplicateName,
        $"API Key name '{name}' already exists",
        new Dictionary<string, object?>
        {
            ["resource"] = "API Key",
            ["field"] = "name",
            ["value"] = name,
        });
}
