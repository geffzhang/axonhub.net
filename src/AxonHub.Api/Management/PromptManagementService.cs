using AxonHub.Api.GraphQL;
using AxonHub.Api.Models;
using AxonHub.Api.Prompts;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of ent.CreatePromptInput (ent.graphql:1779-1805).</summary>
public sealed record CreatePromptCommand
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Role { get; init; }

    public required string Content { get; init; }

    /// <summary>PromptStatus; null leaves the schema default (disabled).</summary>
    public string? Status { get; init; }

    public long? Order { get; init; }

    public required PromptSettings Settings { get; init; }
}

/// <summary>Port of ent.UpdatePromptInput (ent.graphql:6370-6395).</summary>
public sealed record UpdatePromptCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Role { get; init; }

    public string? Content { get; init; }

    public string? Status { get; init; }

    public long? Order { get; init; }

    public PromptSettings? Settings { get; init; }
}

/// <summary>
/// Port of biz.PromptService's management surface
/// (internal/server/biz/prompt.go): create/update/delete/status/bulk plus the
/// settings validator. Every ent call in the reference goes through a
/// privacy-filtered client; the port evaluates the same decisions explicitly
/// before touching the store (C188).
/// </summary>
/// <remarks>
/// The reference's <c>asyncReloadPrompts</c> only refreshes the per-project
/// cache this port does not keep (C187), so the service writes and returns.
/// </remarks>
public sealed class PromptManagementService(
    IPromptStore prompts,
    PromptPrivacy privacy,
    IManagementIdentity identity)
{
    /// <summary>Ent's NotFoundError text for the Prompt label.</summary>
    public const string PromptNotFound = "ent: prompt not found";

    public const string ProjectIdMissing = "project id not found in context";

    /// <summary>The reference's aggregate-update miss message.</summary>
    public const string PromptNotFoundOrNotInProject = "prompt not found or not in project";

    /// <summary>Port of PromptService.CreatePrompt (prompt.go:135-181).</summary>
    public async Task<PromptRecord> CreateAsync(
        CreatePromptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var projectId = RequireProjectId();

        ValidateSettings(command.Settings);

        // The duplicate check runs through the privacy-filtered client, so a
        // query deny is wrapped by the reference's error text.
        await EvaluateQueryAsync("failed to check prompt name uniqueness: ", cancellationToken)
            .ConfigureAwait(false);

        bool exists;
        try
        {
            exists = await prompts.ExistsByNameAsync(projectId, command.Name, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to check prompt name uniqueness: ", ex);
        }

        if (exists)
        {
            throw DuplicateName(command.Name);
        }

        // Ent evaluates the mutation policy inside Save.
        await EvaluateMutationAsync(
                PromptMutationOp.Create,
                projectId,
                "failed to create prompt: ",
                cancellationToken)
            .ConfigureAwait(false);

        var record = new PromptRecord
        {
            ProjectId = projectId,
            Name = command.Name,
            Description = command.Description ?? string.Empty,
            Role = command.Role,
            Content = command.Content,
            Status = command.Status ?? PromptStatuses.Disabled,
            Order = command.Order ?? 0,
            Settings = PromptSettingsCodec.Serialize(command.Settings),
        };

        try
        {
            await prompts.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to create prompt: ", ex);
        }

        return record;
    }

    /// <summary>Port of PromptService.UpdatePrompt (prompt.go:183-260).</summary>
    public async Task<PromptRecord> UpdateAsync(
        UpdatePromptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var projectId = RequireProjectId();

        if (command.Settings is not null)
        {
            ValidateSettings(command.Settings);
        }

        if (command.Name is not null)
        {
            await EvaluateQueryAsync("failed to check prompt name uniqueness: ", cancellationToken)
                .ConfigureAwait(false);

            bool exists;
            try
            {
                exists = await prompts.ExistsByNameAsync(
                        projectId,
                        command.Name,
                        command.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Wrap("failed to check prompt name uniqueness: ", ex);
            }

            if (exists)
            {
                throw DuplicateName(command.Name);
            }
        }

        // Ent evaluates the mutation policy inside Save.
        await EvaluateMutationAsync(
                PromptMutationOp.Update,
                projectId,
                "failed to update prompt: ",
                cancellationToken)
            .ConfigureAwait(false);

        int affected;
        try
        {
            affected = await prompts.UpdateAsync(
                    new PromptUpdate
                    {
                        Id = command.Id,
                        ProjectId = projectId,
                        Name = command.Name,
                        Description = command.Description,
                        Role = command.Role,
                        Content = command.Content,
                        Status = command.Status,
                        Order = command.Order,
                        Settings = command.Settings is null
                            ? null
                            : PromptSettingsCodec.Serialize(command.Settings),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to update prompt: ", ex);
        }

        if (affected == 0)
        {
            throw new InvalidOperationException(PromptNotFoundOrNotInProject);
        }

        return await QueryUpdatedAsync(command.Id, projectId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of PromptService.DeletePrompt (prompt.go:262-288).</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var projectId = RequireProjectId();

        await EvaluateMutationAsync(
                PromptMutationOp.Delete,
                projectId,
                "failed to delete prompt: ",
                cancellationToken)
            .ConfigureAwait(false);

        int affected;
        try
        {
            affected = await prompts.DeleteAsync(id, projectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to delete prompt: ", ex);
        }

        if (affected == 0)
        {
            throw new InvalidOperationException(PromptNotFoundOrNotInProject);
        }
    }

    /// <summary>Port of PromptService.UpdatePromptStatus (prompt.go:290-321).</summary>
    public async Task<PromptRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        var projectId = RequireProjectId();

        await EvaluateMutationAsync(
                PromptMutationOp.Update,
                projectId,
                "failed to update prompt status: ",
                cancellationToken)
            .ConfigureAwait(false);

        int affected;
        try
        {
            affected = await prompts.UpdateStatusAsync(id, projectId, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to update prompt status: ", ex);
        }

        if (affected == 0)
        {
            throw new InvalidOperationException(PromptNotFoundOrNotInProject);
        }

        return await QueryUpdatedAsync(id, projectId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of PromptService.BulkDeletePrompts (prompt.go:323-342).</summary>
    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var projectId = RequireProjectId();

        // The mutation hook runs even when IDIn() renders an always-false
        // predicate, so privacy is evaluated for empty id lists too.
        await EvaluateMutationAsync(
                PromptMutationOp.Delete,
                projectId,
                "failed to bulk delete prompts: ",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await prompts.BulkDeleteAsync(ids, projectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to bulk delete prompts: ", ex);
        }
    }

    /// <summary>Port of PromptService.BulkEnablePrompts (prompt.go:344-363).</summary>
    public Task BulkEnableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(
            ids,
            PromptStatuses.Enabled,
            "failed to bulk enable prompts: ",
            cancellationToken);

    /// <summary>Port of PromptService.BulkDisablePrompts (prompt.go:365-384).</summary>
    public Task BulkDisableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(
            ids,
            PromptStatuses.Disabled,
            "failed to bulk disable prompts: ",
            cancellationToken);

    /// <summary>Port of the entgql Prompt connection (ent.resolvers.go:465-478).</summary>
    public async Task<ListPage<PromptRecord>> ListAsync(
        PromptListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return await prompts.ListAsync(
                query with { ProjectScope = projectScope },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of the entgql Node lookup for Prompt (privacy applies).</summary>
    public async Task<PromptRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await prompts.GetByIdAsync(id, projectScope, cancellationToken)
            .ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(PromptNotFound);
    }

    /// <summary>Port of PromptService.ValidatePromptSettings (prompt.go:96-133).</summary>
    public static void ValidateSettings(PromptSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Conditions is not { Count: > 0 })
        {
            return;
        }

        foreach (var composite in settings.Conditions)
        {
            if (composite?.Conditions is not { Count: > 0 })
            {
                continue;
            }

            foreach (var condition in composite.Conditions)
            {
                // A JSON null element decodes to Go's zero condition, whose
                // type matches none of the branches.
                var type = condition?.Type;
                if (string.Equals(
                        type,
                        PromptActivationConditionTypes.ModelPattern,
                        StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(condition!.ModelPattern))
                    {
                        throw new InvalidOperationException(
                            "model_pattern is required when type is model_pattern");
                    }

                    var error = XRegexp.ValidateRegex(condition.ModelPattern);
                    if (error is not null)
                    {
                        throw new InvalidOperationException(
                            "invalid regex pattern in model_pattern: " + error);
                    }
                }

                if (string.Equals(
                        type,
                        PromptActivationConditionTypes.ModelId,
                        StringComparison.Ordinal)
                    && string.IsNullOrEmpty(condition!.ModelId))
                {
                    throw new InvalidOperationException(
                        "model_id is required when type is model_id");
                }

                if (string.Equals(
                        type,
                        PromptActivationConditionTypes.ApiKey,
                        StringComparison.Ordinal))
                {
                    if (condition!.ApiKeyId is null)
                    {
                        throw new InvalidOperationException(
                            "api_key_id is required when type is api_key");
                    }

                    if (condition.ApiKeyId <= 0)
                    {
                        throw new InvalidOperationException(
                            "api_key_id must be greater than 0");
                    }
                }
            }
        }
    }

    private async Task<PromptRecord> QueryUpdatedAsync(
        long id,
        long projectId,
        CancellationToken cancellationToken)
    {
        // The re-query is privacy-filtered; the policy may have allowed the
        // write but deny the read (a write-only project user), which the
        // reference reports with this prefix.
        await EvaluateQueryAsync("failed to query updated prompt: ", cancellationToken)
            .ConfigureAwait(false);

        // The re-query's explicit project predicate equals the privacy
        // predicate whenever the policy installed one.
        PromptRecord? record;
        try
        {
            record = await prompts.GetByIdAsync(id, projectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to query updated prompt: ", ex);
        }

        return record ?? throw new InvalidOperationException(
            "failed to query updated prompt: " + PromptNotFound);
    }

    private async Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var projectId = RequireProjectId();

        await EvaluateMutationAsync(
                PromptMutationOp.Update,
                projectId,
                errorPrefix,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await prompts.BulkUpdateStatusAsync(ids, projectId, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(errorPrefix, ex);
        }
    }

    private long RequireProjectId()
        => identity.GetCurrentProjectId()
            ?? throw new InvalidOperationException(ProjectIdMissing);

    private async Task<long?> EvaluateQueryAsync(
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            return await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(errorPrefix, ex);
        }
    }

    private async Task EvaluateMutationAsync(
        PromptMutationOp operation,
        long? mutationProjectId,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await privacy.EvaluateMutationAsync(operation, mutationProjectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(errorPrefix, ex);
        }
    }

    private static InvalidOperationException Wrap(string prefix, Exception exception)
        => new(prefix + exception.Message, exception);

    private static CodedException DuplicateName(string name)
        => new(
            CodedException.DuplicateName,
            "prompt name '" + name + "' already exists",
            new Dictionary<string, object?>
            {
                ["resource"] = "prompt",
                ["field"] = "name",
                ["value"] = name,
            });
}
