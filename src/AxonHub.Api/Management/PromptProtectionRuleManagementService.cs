using AxonHub.Api.GraphQL;
using AxonHub.Api.PromptProtection;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of ent.CreatePromptProtectionRuleInput (ent.graphql:1809-1826).</summary>
public sealed record CreatePromptProtectionRuleCommand
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Pattern { get; init; }

    public required PromptProtectionSettings Settings { get; init; }
}

/// <summary>Port of ent.UpdatePromptProtectionRuleInput (ent.graphql:6401-6419).</summary>
public sealed record UpdatePromptProtectionRuleCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Pattern { get; init; }

    public string? Status { get; init; }

    public PromptProtectionSettings? Settings { get; init; }
}

/// <summary>
/// Port of biz.PromptProtectionRuleService's management surface
/// (internal/server/biz/prompt_protection_rule.go): create/update/delete/
/// status/bulk plus the settings validator and the preview helper
/// (prompt_protection_preview.go).
/// </summary>
/// <remarks>
/// The reference refreshes its 30-second live cache after every mutation
/// (asyncReloadEnabledRules); the port reads the database per request, so the
/// mutation is the whole effect (C092). The rule schema's scope policy
/// (read/write channels) is not ported (C014).
/// </remarks>
public sealed class PromptProtectionRuleManagementService(IPromptProtectionRuleStore rules)
{
    /// <summary>Ent's NotFoundError text for the PromptProtectionRule label.</summary>
    public const string RuleNotFound = "ent: prompt_protection_rule not found";

    /// <summary>Port of PromptProtectionRuleService.CreateRule.</summary>
    public async Task<PromptProtectionRuleRecord> CreateAsync(
        CreatePromptProtectionRuleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Settings is null)
        {
            throw new InvalidOperationException("settings are required");
        }

        ValidateSettings(command.Pattern, command.Settings);

        bool exists;
        try
        {
            exists = await rules.ExistsByNameAsync(command.Name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to check existing prompt protection rule: ", ex);
        }

        if (exists)
        {
            throw DuplicateName(command.Name);
        }

        var record = new PromptProtectionRuleRecord
        {
            Name = command.Name,
            Description = command.Description ?? string.Empty,
            Pattern = command.Pattern,
            // The create input has no status; the schema default applies.
            Status = "disabled",
            Settings = command.Settings.ToJson(),
        };

        try
        {
            await rules.CreateAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to create prompt protection rule: ", ex);
        }

        return record;
    }

    /// <summary>Port of PromptProtectionRuleService.UpdateRule.</summary>
    public async Task<PromptProtectionRuleRecord> UpdateAsync(
        UpdatePromptProtectionRuleCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // UpdateRule re-reads the row first (through the soft-delete-aware
        // query) and validates the effective pattern/settings pair.
        PromptProtectionRuleRecord current;
        PromptProtectionSettings? currentSettings;
        try
        {
            current = await rules.GetByIdAsync(command.Id, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(RuleNotFound);
            currentSettings = DecodeSettings(current.Settings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to query prompt protection rule: ", ex);
        }

        var pattern = command.Pattern ?? current.Pattern;
        var settings = command.Settings ?? currentSettings;

        ValidateSettings(pattern, settings);

        PromptProtectionRuleRecord? updated;
        try
        {
            updated = await rules.UpdateAsync(
                    new PromptProtectionRuleUpdate
                    {
                        Id = command.Id,
                        Name = command.Name,
                        Description = command.Description,
                        Pattern = command.Pattern,
                        Status = command.Status,
                        Settings = command.Settings?.ToJson(),
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
            throw Wrap("failed to update prompt protection rule: ", ex);
        }

        // Ent's UpdateOne reports a missing row as NotFoundError.
        return updated ?? throw new InvalidOperationException(
            "failed to update prompt protection rule: " + RuleNotFound);
    }

    /// <summary>Port of PromptProtectionRuleService.DeleteRule.</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        int affected;
        try
        {
            affected = await rules.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to delete prompt protection rule: ", ex);
        }

        // DeleteOneID returns NotFoundError when the soft-delete update
        // matches no live row.
        if (affected == 0)
        {
            throw new InvalidOperationException(
                "failed to delete prompt protection rule: " + RuleNotFound);
        }
    }

    /// <summary>Port of PromptProtectionRuleService.UpdateRuleStatus.</summary>
    public async Task<PromptProtectionRuleRecord> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        PromptProtectionRuleRecord? updated;
        try
        {
            // No live-row check: the aggregate update has no deleted_at
            // predicate, so a soft-deleted row is still written.
            updated = await rules.UpdateStatusAsync(id, status, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to update prompt protection rule status: ", ex);
        }

        return updated ?? throw new InvalidOperationException(
            "failed to update prompt protection rule status: " + RuleNotFound);
    }

    /// <summary>Port of PromptProtectionRuleService.BulkDeleteRules.</summary>
    public async Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await rules.BulkDeleteAsync(ids, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to bulk delete prompt protection rules: ", ex);
        }
    }

    /// <summary>Port of PromptProtectionRuleService.BulkEnableRules.</summary>
    public Task BulkEnableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(
            ids,
            "enabled",
            "failed to bulk enable prompt protection rules: ",
            cancellationToken);

    /// <summary>Port of PromptProtectionRuleService.BulkDisableRules.</summary>
    public Task BulkDisableAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => BulkUpdateStatusAsync(
            ids,
            "disabled",
            "failed to bulk disable prompt protection rules: ",
            cancellationToken);

    /// <summary>Port of the entgql PromptProtectionRule connection.</summary>
    public async Task<ListPage<PromptProtectionRuleRecord>> ListAsync(
        PromptProtectionRuleListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return await rules.ListAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of PromptProtectionRuleService.ValidateSettings
    /// (prompt_protection_rule.go:124-166). The pattern compiles first, so an
    /// invalid pattern is reported before a missing settings object.
    /// </summary>
    public static void ValidateSettings(string pattern, PromptProtectionSettings? settings)
    {
        if (!PromptProtectionRegex.TryGetOrCompile(pattern, out _, out var compileError))
        {
            throw new InvalidOperationException(
                "invalid regex pattern: " + compileError?.Message);
        }

        if (settings is null)
        {
            throw new InvalidOperationException("settings are required");
        }

        if (settings.Action != PromptProtectionActions.Mask
            && settings.Action != PromptProtectionActions.Reject)
        {
            throw new InvalidOperationException("invalid action: " + settings.Action);
        }

        if (settings.Action == PromptProtectionActions.Mask && settings.Replacement.Length == 0)
        {
            throw new InvalidOperationException("replacement is required for mask action");
        }

        if (settings.Scopes is { } scopes)
        {
            foreach (var scope in scopes)
            {
                if (scope != PromptProtectionScopes.System
                    && scope != PromptProtectionScopes.Developer
                    && scope != PromptProtectionScopes.User
                    && scope != PromptProtectionScopes.Assistant
                    && scope != PromptProtectionScopes.Tool)
                {
                    throw new InvalidOperationException("invalid scope: " + scope);
                }
            }
        }
    }

    /// <summary>
    /// Port of PromptProtectionRuleService.Preview
    /// (internal/server/biz/prompt_protection_preview.go). Match and replace
    /// errors surface unwrapped; only the compile error gets the
    /// <c>invalid regex pattern:</c> prefix.
    /// </summary>
    public static PromptProtectionPreviewResult Preview(
        string pattern,
        string testText,
        PromptProtectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSettings(pattern, settings);

        if (!PromptProtectionRegex.TryGetOrCompile(pattern, out var regex, out var compileError))
        {
            throw new InvalidOperationException(
                "invalid regex pattern: " + compileError?.Message);
        }

        // The cache entry cannot be a failure after ValidateSettings succeeded,
        // but keep the reference's error branch for parity.
        if (regex is null)
        {
            throw new InvalidOperationException("invalid regex pattern: ");
        }

        var hasMatch = regex.IsMatch(testText);

        var result = testText;
        if (hasMatch && settings.Action == PromptProtectionActions.Mask)
        {
            result = regex.Replace(testText, settings.Replacement);
        }

        if (hasMatch && settings.Action == PromptProtectionActions.Reject)
        {
            result = PromptProtectionActions.Reject;
        }

        return new PromptProtectionPreviewResult
        {
            Result = result,
            HasMatch = hasMatch,
        };
    }

    /// <summary>
    /// Decodes the jsonb settings column the way Ent's scan does. A malformed
    /// column makes the reference's query fail, so the caller prefixes the
    /// exception with the query error text.
    /// </summary>
    public static PromptProtectionSettings? DecodeSettings(string json)
        => string.IsNullOrWhiteSpace(json)
            ? null
            : PromptProtectionSettings.FromJson(json);

    private async Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        string errorPrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            await rules.BulkUpdateStatusAsync(ids, status, cancellationToken)
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
            "prompt protection rule name '" + name + "' already exists",
            new Dictionary<string, object?>
            {
                ["resource"] = "prompt protection rule",
                ["field"] = "name",
                ["value"] = name,
            });
}
