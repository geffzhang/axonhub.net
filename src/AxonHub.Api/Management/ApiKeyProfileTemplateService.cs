using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of the fields CreateTemplate reads from ent.CreateAPIKeyProfileTemplateInput.</summary>
public sealed record CreateApiKeyProfileTemplateCommand
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required long ProjectId { get; init; }
}

/// <summary>Port of the fields UpdateTemplate applies from ent.UpdateAPIKeyProfileTemplateInput.</summary>
public sealed record UpdateApiKeyProfileTemplateCommand
{
    public string? Name { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// Port of biz.APIKeyProfileTemplateService
/// (internal/server/biz/api_key_profile_template.go:19-425).
/// </summary>
/// <remarks>
/// The reference's privacy rules run inside ent under the request-bound client.
/// The port evaluates them explicitly (see
/// <see cref="ApiKeyProfileTemplatePrivacy"/> and <see cref="ApiKeyPrivacy"/>)
/// before touching the stores, keeping the reference's wrapping messages so
/// callers see the same error chain. <see cref="ListForProjectAsync"/> orders
/// by id where the reference emits no ORDER BY (C163).
/// </remarks>
public sealed class ApiKeyProfileTemplateService(
    IApiKeyProfileTemplateStore templates,
    IApiKeyStore apiKeys,
    ApiKeyProfileTemplatePrivacy privacy,
    ApiKeyPrivacy apiKeyPrivacy,
    IManagementIdentity identity)
{
    public const string TemplateNotFound = "ent: api_key_profile_template not found";

    private const string ApiKeyNotFound = "ent: api_key not found";
    private const string PersonalKeyType = "personal";

    /// <summary>Port of APIKeyProfileTemplateService.CreateTemplate (:37-65).</summary>
    public async Task<ApiKeyProfileTemplateRecord> CreateAsync(
        CreateApiKeyProfileTemplateCommand command,
        ApiKeyProfileModel? profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // CreateTemplate overwrites the profile identity before validating the
        // routing policy, so a validation error reports the input name.
        var publishedProfile = profile;
        if (publishedProfile is not null)
        {
            publishedProfile = publishedProfile with
            {
                TemplateId = null,
                TemplateName = string.Empty,
                Name = command.Name,
            };
            publishedProfile = ApiKeyProfilesCodec.NormalizeAndValidateRoutingPolicy(
                publishedProfile);
        }

        try
        {
            await privacy.EvaluateMutationAsync(
                    ApiKeyMutationOp.Create,
                    command.ProjectId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create template: " + ex.Message,
                ex);
        }

        var now = DateTime.UtcNow;
        var template = new ApiKeyProfileTemplateRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = 0,
            Name = command.Name,
            Description = command.Description ?? string.Empty,
            // SetProfile(nil) writes SQL NULL and suppresses the schema default.
            Profile = publishedProfile is null
                ? null
                : ApiKeyProfilesCodec.SerializeProfile(publishedProfile),
            ProjectId = command.ProjectId,
        };

        try
        {
            return await templates.CreateAsync(template, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiKeyProfileTemplateNameConflictException)
        {
            throw DuplicateName(command.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create template: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of APIKeyProfileTemplateService.GetTemplate (:67-76).</summary>
    public async Task<ApiKeyProfileTemplateRecord> GetAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var access = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            var template = await templates.GetAsync(id, access, cancellationToken)
                .ConfigureAwait(false);
            if (template is not null)
            {
                return template;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get template: " + ex.Message, ex);
        }

        throw new InvalidOperationException("failed to get template: " + TemplateNotFound);
    }

    /// <summary>
    /// Port of the entgql Node lookup for APIKeyProfileTemplate
    /// (ent.resolvers.go:325-332): the query policy applies, and the raw ent
    /// errors are surfaced (no service-level wrapping).
    /// </summary>
    public async Task<ApiKeyProfileTemplateRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var access = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var template = await templates.GetAsync(id, access, cancellationToken)
            .ConfigureAwait(false);
        return template ?? throw new InvalidOperationException(TemplateNotFound);
    }

    /// <summary>
    /// Port of APIKeyProfileTemplateService.GetForRead (:86-107). Exactly one of
    /// id or name must be provided; raw ent errors are returned.
    /// </summary>
    public async Task<ApiKeyProfileTemplateRecord> GetForReadAsync(
        long? id,
        string? name,
        CancellationToken cancellationToken = default)
    {
        if ((id is null) == (name is null))
        {
            throw new InvalidOperationException(
                "exactly one of template id or name must be provided");
        }

        var access = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        if (id is not null)
        {
            var byId = await templates.GetAsync(id.Value, access, cancellationToken)
                .ConfigureAwait(false);
            return byId ?? throw new InvalidOperationException(TemplateNotFound);
        }

        var byName = await templates.GetByNameAsync(name!, access, cancellationToken)
            .ConfigureAwait(false);
        return byName ?? throw new InvalidOperationException(TemplateNotFound);
    }

    /// <summary>Port of APIKeyProfileTemplateService.ListTemplates (:109-120).</summary>
    public async Task<IReadOnlyList<ApiKeyProfileTemplateRecord>> ListForProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var access = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return await templates.ListForProjectAsync(projectId, access, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to list templates: " + ex.Message, ex);
        }
    }

    /// <summary>Port of the entgql apiKeyProfileTemplates connection query.</summary>
    public async Task<ListPage<ApiKeyProfileTemplateRecord>> ListAsync(
        ApiKeyProfileTemplateListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var access = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return await templates.ListAsync(query with { Access = access }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of APIKeyProfileTemplateService.UpdateTemplate (:122-179).</summary>
    public Task<ApiKeyProfileTemplateRecord> UpdateAsync(
        long id,
        UpdateApiKeyProfileTemplateCommand command,
        ApiKeyProfileModel? profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        return templates.RunInTransactionAsync(
            async token =>
            {
                var existing = await GetExistingForUpdateAsync(id, token)
                    .ConfigureAwait(false);

                // When only the name changes, republish the stored profile so
                // its embedded name stays in sync.
                var publishedProfile = profile;
                if (publishedProfile is null && command.Name is not null)
                {
                    publishedProfile = ApiKeyProfilesCodec.DeserializeProfile(
                        existing.Profile);
                }

                string? profileJson = null;
                if (publishedProfile is not null)
                {
                    publishedProfile = publishedProfile with
                    {
                        TemplateId = null,
                        TemplateName = string.Empty,
                    };

                    // The validation error reports the pre-overwrite name.
                    publishedProfile = ApiKeyProfilesCodec.NormalizeAndValidateRoutingPolicy(
                        publishedProfile);
                    publishedProfile = publishedProfile with
                    {
                        Name = command.Name ?? existing.Name,
                    };
                    profileJson = ApiKeyProfilesCodec.SerializeProfile(publishedProfile);
                }

                ApiKeyProfileTemplateRecord? updated;
                try
                {
                    var projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.UpdateOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);

                    updated = await templates.UpdateAsync(
                            new ApiKeyProfileTemplateUpdate
                            {
                                Id = id,
                                Name = command.Name,
                                Description = command.Description,
                                Profile = profileJson,
                                ProjectWritePredicate = projectWritePredicate,
                            },
                            token)
                        .ConfigureAwait(false);
                }
                catch (ApiKeyProfileTemplateNameConflictException)
                {
                    // lo.FromPtr(input.Name) reports "" when the input did not
                    // rename the template.
                    throw DuplicateName(command.Name ?? string.Empty);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to update template: " + ex.Message,
                        ex);
                }

                if (updated is null)
                {
                    throw new InvalidOperationException(
                        "failed to update template: " + TemplateNotFound);
                }

                if (publishedProfile is not null)
                {
                    await SyncLinkedProfilesAsync(
                            existing,
                            updated,
                            publishedProfile,
                            token)
                        .ConfigureAwait(false);
                }

                return updated;
            },
            cancellationToken);
    }

    /// <summary>Port of APIKeyProfileTemplateService.DeleteTemplate (:181-208).</summary>
    public Task<ApiKeyProfileTemplateRecord> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default)
        => templates.RunInTransactionAsync(
            async token =>
            {
                ApiKeyProfileTemplateRecord? template;
                try
                {
                    var access = await privacy.EvaluateQueryAsync(token)
                        .ConfigureAwait(false);
                    template = await templates.GetAsync(id, access, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to get template for deletion: " + ex.Message,
                        ex);
                }

                if (template is null)
                {
                    throw new InvalidOperationException(
                        "failed to get template for deletion: " + TemplateNotFound);
                }

                await DetachLinkedProfilesAsync(template, token).ConfigureAwait(false);

                bool deleted;
                try
                {
                    var projectWritePredicate = await privacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.DeleteOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);
                    deleted = await templates.DeleteAsync(
                            id,
                            projectWritePredicate is null
                                ? null
                                : new ApiKeyProfileTemplateAccessFilter
                                {
                                    ProjectId = projectWritePredicate,
                                },
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to delete template: " + ex.Message,
                        ex);
                }

                return deleted
                    ? template
                    : throw new InvalidOperationException(
                        "failed to delete template: " + TemplateNotFound);
            },
            cancellationToken);

    /// <summary>Port of APIKeyProfileTemplateService.LoadTemplate (:210-277).</summary>
    public Task<ApiKeyRecord> LoadAsync(
        long templateId,
        long apiKeyId,
        CancellationToken cancellationToken = default)
        => templates.RunInTransactionAsync(
            async token =>
            {
                ApiKeyProfileTemplateRecord? template;
                try
                {
                    var templateAccess = await privacy.EvaluateQueryAsync(token)
                        .ConfigureAwait(false);
                    template = await templates.GetAsync(templateId, templateAccess, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to get template: " + ex.Message,
                        ex);
                }

                if (template is null)
                {
                    throw new InvalidOperationException(
                        "failed to get template: " + TemplateNotFound);
                }

                ApiKeyRecord? apiKey;
                try
                {
                    var apiKeyAccess = await apiKeyPrivacy.EvaluateQueryAsync(token)
                        .ConfigureAwait(false);
                    apiKey = await apiKeys.GetByIdAsync(apiKeyId, apiKeyAccess, token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        "failed to get API key: " + ex.Message,
                        ex);
                }

                if (apiKey is null)
                {
                    throw new InvalidOperationException("failed to get API key: " + ApiKeyNotFound);
                }

                if (template.ProjectId != apiKey.ProjectId)
                {
                    throw new InvalidOperationException(
                        "template and API key must belong to the same project");
                }

                if (string.Equals(apiKey.Type, PersonalKeyType, StringComparison.Ordinal))
                {
                    var user = await identity.GetCurrentUserAsync(token)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException("user not found in context");
                    if ((apiKey.UserId ?? 0) != user.Id && !user.IsOwner)
                    {
                        throw new InvalidOperationException(
                            "personal API key can only be modified by its creator or a system owner");
                    }
                }

                var templateProfile = ApiKeyProfilesCodec.DeserializeProfile(template.Profile)
                    ?? throw new InvalidOperationException("template has no profile");
                templateProfile = ApiKeyProfilesCodec.NormalizeAndValidateRoutingPolicy(
                    templateProfile);

                var existingProfiles = ApiKeyProfilesCodec.Deserialize(apiKey.Profiles);
                var profileName = templateProfile.Name;
                if (profileName.Length == 0)
                {
                    profileName = template.Name;
                }

                var resolvedName = ResolveProfileNameConflict(
                    existingProfiles.Profiles,
                    profileName);

                var loadedProfile = templateProfile with
                {
                    Name = resolvedName,
                    TemplateId = template.Id,
                    TemplateName = template.Name,
                };

                var profiles = existingProfiles.Profiles?.ToList() ?? [];
                profiles.Add(loadedProfile);
                var nextProfiles = existingProfiles with { Profiles = profiles };

                ApiKeyRecord? updatedKey;
                try
                {
                    var projectWritePredicate = await apiKeyPrivacy.EvaluateMutationAsync(
                            ApiKeyMutationOp.UpdateOne,
                            mutationProjectId: null,
                            token)
                        .ConfigureAwait(false);

                    updatedKey = await apiKeys.UpdateAsync(
                            new ApiKeyUpdate
                            {
                                Id = apiKeyId,
                                Profiles = ApiKeyProfilesCodec.Serialize(nextProfiles),
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

                return updatedKey ?? throw new InvalidOperationException(
                    "failed to update API key profiles: " + ApiKeyNotFound);
            },
            cancellationToken);

    /// <summary>
    /// Port of APIKeyProfileTemplateService.CountLinkedProfiles (:283-306). The
    /// count is derived from the project's API keys; the embedded JSON is the
    /// single source of truth.
    /// </summary>
    public async Task<int> CountLinkedProfilesAsync(
        ApiKeyProfileTemplateRecord template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        IReadOnlyList<ApiKeyRecord> keys;
        ApiKeyProfileModel? templateProfile = null;
        try
        {
            var access = await apiKeyPrivacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            keys = await apiKeys.ListAllAsync(
                    new ApiKeyListFilter { ProjectId = template.ProjectId },
                    access,
                    cancellationToken)
                .ConfigureAwait(false);
            templateProfile = ApiKeyProfilesCodec.DeserializeProfile(template.Profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to list API keys linked to template: " + ex.Message,
                ex);
        }

        var count = 0;
        foreach (var key in keys)
        {
            if (key.Profiles is null)
            {
                continue;
            }

            var profiles = ApiKeyProfilesCodec.Deserialize(key.Profiles).Profiles;
            if (profiles is null)
            {
                continue;
            }

            foreach (var profile in profiles)
            {
                if ((profile.TemplateId is not null
                        && profile.TemplateId.Value == template.Id)
                    || IsLegacyTemplateMatch(profile, template, templateProfile))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private async Task<ApiKeyProfileTemplateRecord> GetExistingForUpdateAsync(
        long id,
        CancellationToken cancellationToken)
    {
        try
        {
            var access = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            var template = await templates.GetAsync(id, access, cancellationToken)
                .ConfigureAwait(false);
            if (template is not null)
            {
                return template;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get template: " + ex.Message, ex);
        }

        throw new InvalidOperationException("failed to get template: " + TemplateNotFound);
    }

    /// <summary>
    /// Port of syncLinkedProfiles (:308-350): republish the template profile to
    /// every linked profile, preserving each key's local name alias.
    /// </summary>
    private async Task SyncLinkedProfilesAsync(
        ApiKeyProfileTemplateRecord previousTemplate,
        ApiKeyProfileTemplateRecord template,
        ApiKeyProfileModel publishedProfile,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiKeyRecord> keys;
        ApiKeyProfileModel? previousProfile = null;
        try
        {
            var access = await apiKeyPrivacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            keys = await apiKeys.ListAllAsync(
                    new ApiKeyListFilter { ProjectId = template.ProjectId },
                    access,
                    cancellationToken)
                .ConfigureAwait(false);
            previousProfile = ApiKeyProfilesCodec.DeserializeProfile(previousTemplate.Profile);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to list API keys linked to template: " + ex.Message,
                ex);
        }

        foreach (var key in keys)
        {
            if (key.Profiles is null)
            {
                continue;
            }

            var profiles = ApiKeyProfilesCodec.Deserialize(key.Profiles);
            if (profiles.Profiles is null)
            {
                continue;
            }

            var changed = false;
            var updated = new List<ApiKeyProfileModel>(profiles.Profiles.Count);
            foreach (var current in profiles.Profiles)
            {
                var isLinked = current.TemplateId is not null
                    && current.TemplateId.Value == template.Id;
                if (!isLinked
                    && !IsLegacyTemplateMatch(current, previousTemplate, previousProfile))
                {
                    updated.Add(current);
                    continue;
                }

                // The profile name is an alias local to this key; keep it so a
                // rename never invalidates activeProfile or collides.
                updated.Add(publishedProfile with
                {
                    Name = current.Name,
                    TemplateId = template.Id,
                    TemplateName = template.Name,
                });
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            try
            {
                var projectWritePredicate = await apiKeyPrivacy.EvaluateMutationAsync(
                        ApiKeyMutationOp.UpdateOne,
                        mutationProjectId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                await apiKeys.UpdateAsync(
                        new ApiKeyUpdate
                        {
                            Id = key.Id,
                            Profiles = ApiKeyProfilesCodec.Serialize(
                                profiles with { Profiles = updated }),
                            SetProfiles = true,
                            ProjectWritePredicate = projectWritePredicate,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"failed to publish template to API key {key.Id}: {ex.Message}",
                    ex);
            }
        }
    }

    /// <summary>
    /// Port of detachLinkedProfiles (:375-407): only an explicit TemplateID
    /// match is detached; legacy name matches stay untouched.
    /// </summary>
    private async Task DetachLinkedProfilesAsync(
        ApiKeyProfileTemplateRecord template,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ApiKeyRecord> keys;
        try
        {
            var access = await apiKeyPrivacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            keys = await apiKeys.ListAllAsync(
                    new ApiKeyListFilter { ProjectId = template.ProjectId },
                    access,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to list API keys linked to template: " + ex.Message,
                ex);
        }

        foreach (var key in keys)
        {
            if (key.Profiles is null)
            {
                continue;
            }

            var profiles = ApiKeyProfilesCodec.Deserialize(key.Profiles);
            if (profiles.Profiles is null)
            {
                continue;
            }

            var changed = false;
            var updated = new List<ApiKeyProfileModel>(profiles.Profiles.Count);
            foreach (var profile in profiles.Profiles)
            {
                if (profile.TemplateId is not null
                    && profile.TemplateId.Value == template.Id)
                {
                    updated.Add(profile with
                    {
                        TemplateId = null,
                        TemplateName = string.Empty,
                    });
                    changed = true;
                }
                else
                {
                    updated.Add(profile);
                }
            }

            if (!changed)
            {
                continue;
            }

            try
            {
                var projectWritePredicate = await apiKeyPrivacy.EvaluateMutationAsync(
                        ApiKeyMutationOp.UpdateOne,
                        mutationProjectId: null,
                        cancellationToken)
                    .ConfigureAwait(false);

                await apiKeys.UpdateAsync(
                        new ApiKeyUpdate
                        {
                            Id = key.Id,
                            Profiles = ApiKeyProfilesCodec.Serialize(
                                profiles with { Profiles = updated }),
                            SetProfiles = true,
                            ProjectWritePredicate = projectWritePredicate,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"failed to detach template from API key {key.Id}: {ex.Message}",
                    ex);
            }
        }
    }

    /// <summary>
    /// Port of isLegacyTemplateMatch (:355-373): only an unchanged profile with
    /// the template's original name is adopted.
    /// </summary>
    private static bool IsLegacyTemplateMatch(
        ApiKeyProfileModel profile,
        ApiKeyProfileTemplateRecord template,
        ApiKeyProfileModel? templateProfile)
    {
        if (profile.TemplateId is not null || templateProfile is null)
        {
            return false;
        }

        if (!string.Equals(profile.Name, template.Name, StringComparison.Ordinal)
            && !string.Equals(profile.Name, templateProfile.Name, StringComparison.Ordinal))
        {
            return false;
        }

        return ApiKeyProfilesCodec.SameProfileContentsIgnoringNameAndTemplate(
            profile,
            templateProfile);
    }

    /// <summary>Port of resolveProfileNameConflict (:409-425).</summary>
    private static string ResolveProfileNameConflict(
        IReadOnlyList<ApiKeyProfileModel>? existingProfiles,
        string newName)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in existingProfiles ?? [])
        {
            names.Add(profile.Name);
        }

        if (!names.Contains(newName))
        {
            return newName;
        }

        for (var i = 1; ; i++)
        {
            var candidate = $"{newName} ({i})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Port of xerrors.DuplicateNameError("Template", name).</summary>
    private static CodedException DuplicateName(string name) => new(
        CodedException.DuplicateName,
        $"Template name '{name}' already exists",
        new Dictionary<string, object?>
        {
            ["resource"] = "Template",
            ["field"] = "name",
            ["value"] = name,
        });
}
