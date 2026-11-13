using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of the fields CreateTemplate reads from ent.CreateChannelOverrideTemplateInput.</summary>
public sealed record CreateChannelOverrideTemplateCommand
{
    public required string Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<OverrideOperationDraft>? HeaderOverrideOperations { get; init; }

    public IReadOnlyList<OverrideOperationDraft>? BodyOverrideOperations { get; init; }
}

/// <summary>Port of the fields UpdateTemplate applies from ent.UpdateChannelOverrideTemplateInput.</summary>
public sealed record UpdateChannelOverrideTemplateCommand
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public bool ClearDescription { get; init; }

    public IReadOnlyList<OverrideOperationDraft>? HeaderOverrideOperations { get; init; }

    public IReadOnlyList<OverrideOperationDraft>? AppendHeaderOverrideOperations { get; init; }

    public IReadOnlyList<OverrideOperationDraft>? BodyOverrideOperations { get; init; }

    public IReadOnlyList<OverrideOperationDraft>? AppendBodyOverrideOperations { get; init; }
}

/// <summary>Port of biz.ApplyTemplateMode.</summary>
public enum ChannelOverrideApplyMode
{
    Merge,
    Replace,
}

/// <summary>Port of gql's ApplyChannelOverrideTemplateInput.</summary>
public sealed record ApplyChannelOverrideTemplateCommand
{
    public required long TemplateId { get; init; }

    public required IReadOnlyList<long> ChannelIds { get; init; }

    public ChannelOverrideApplyMode Mode { get; init; } = ChannelOverrideApplyMode.Merge;
}

/// <summary>The channels ApplyTemplate/ClearTemplates rewrote, in query order.</summary>
public sealed record ChannelOverrideTemplateApplyResult(
    IReadOnlyList<ChannelRecord> Channels)
{
    public bool Success => true;

    public int Updated => Channels.Count;
}

/// <summary>
/// Port of biz.ChannelOverrideTemplateService
/// (internal/server/biz/channel_override_template.go:15-367) plus the resolvers'
/// current-user lookup (axonhub.resolvers.go:644-708).
/// </summary>
/// <remarks>
/// The reference's privacy rules run inside ent. The port applies them in the
/// service: a missing user denies query/mutation, and a non-owner is scoped to
/// their own rows (C161). The channel query of Apply/Clear is ordered by id;
/// the reference emits no ORDER BY (C163).
/// </remarks>
public sealed class ChannelOverrideTemplateService(
    IChannelOverrideTemplateStore store,
    IManagementIdentity identity,
    IChannelModelSyncReloadHook reloadHook)
{
    /// <summary>Port of mutationResolver.CreateChannelOverrideTemplate + CreateTemplate.</summary>
    public async Task<ChannelOverrideTemplateRecord> CreateAsync(
        CreateChannelOverrideTemplateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The resolver looks the user up before calling the service.
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        if (command.HeaderOverrideOperations is not null)
        {
            var error = ChannelValidation.ValidateHeaderOverrideOperations(
                command.HeaderOverrideOperations);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    "invalid header override operations: " + error);
            }
        }

        if (command.BodyOverrideOperations is not null)
        {
            var error = ChannelValidation.ValidateBodyOverrideOperations(
                command.BodyOverrideOperations);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    "invalid body override operations: " + error);
            }
        }

        var now = DateTime.UtcNow;
        var template = new ChannelOverrideTemplateRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = 0,
            Name = command.Name,
            Description = command.Description,
            OverrideParameters = "{}",
            OverrideHeaders = "[]",
            // The ent builder always sets both operation fields, so an absent
            // input stores a JSON null instead of the schema default.
            HeaderOverrideOperations = OperationsJson(command.HeaderOverrideOperations),
            BodyOverrideOperations = OperationsJson(command.BodyOverrideOperations),
            UserId = current.Id,
        };

        try
        {
            return await store.CreateAsync(template, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelOverrideTemplateNameConflictException)
        {
            throw DuplicateName(command.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create channel override template: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of mutationResolver.UpdateChannelOverrideTemplate + UpdateTemplate.</summary>
    public async Task<ChannelOverrideTemplateRecord> UpdateAsync(
        UpdateChannelOverrideTemplateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.HeaderOverrideOperations is not null)
        {
            var error = ChannelValidation.ValidateHeaderOverrideOperations(
                command.HeaderOverrideOperations);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    "invalid header override operations: " + error);
            }
        }

        if (command.AppendHeaderOverrideOperations is not null)
        {
            var error = ChannelValidation.ValidateHeaderOverrideOperations(
                command.AppendHeaderOverrideOperations);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    "invalid header override operations to append: " + error);
            }
        }

        if (command.BodyOverrideOperations is not null)
        {
            var error = ChannelValidation.ValidateBodyOverrideOperations(
                command.BodyOverrideOperations);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    "invalid body override operations: " + error);
            }
        }

        if (command.AppendBodyOverrideOperations is not null)
        {
            var error = ChannelValidation.ValidateBodyOverrideOperations(
                command.AppendBodyOverrideOperations);
            if (error is not null)
            {
                throw new InvalidOperationException(
                    "invalid body override operations to append: " + error);
            }
        }

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                "failed to update channel override template: "
                + "no user in context: ent/privacy: deny rule");
        }

        var update = new ChannelOverrideTemplateUpdate
        {
            Id = command.Id,
            Name = command.Name,
            ClearDescription = command.ClearDescription,
            Description = command.Description,
            HeaderOverrideOperations = OperationsJsonOrNull(command.HeaderOverrideOperations),
            // ent ignores an empty Append (Appended... ok=false), so it never
            // touches the column.
            AppendHeaderOverrideOperations = AppendJson(command.AppendHeaderOverrideOperations),
            BodyOverrideOperations = OperationsJsonOrNull(command.BodyOverrideOperations),
            AppendBodyOverrideOperations = AppendJson(command.AppendBodyOverrideOperations),
            ScopeUserId = ScopeOf(current),
        };

        ChannelOverrideTemplateRecord? updated;
        try
        {
            updated = await store.UpdateAsync(update, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelOverrideTemplateNameConflictException)
            when (command.Name is not null)
        {
            throw DuplicateName(command.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to update channel override template: " + ex.Message,
                ex);
        }

        if (updated is null)
        {
            // UserOwnedMutationRule adds user_id = current; a missing row is
            // reported by ent as not found.
            throw new InvalidOperationException(
                "failed to update channel override template: "
                + "ent: channel_override_template not found");
        }

        return updated;
    }

    /// <summary>Port of mutationResolver.DeleteChannelOverrideTemplate + DeleteTemplate.</summary>
    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                "failed to delete channel override template: "
                + "no user in context: ent/privacy: deny rule");
        }

        bool deleted;
        try
        {
            deleted = await store.DeleteAsync(id, ScopeOf(current), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to delete channel override template: " + ex.Message,
                ex);
        }

        if (!deleted)
        {
            throw new InvalidOperationException(
                "failed to delete channel override template: "
                + "ent: channel_override_template not found");
        }
    }

    /// <summary>Port of biz.ChannelOverrideTemplateService.GetTemplate.</summary>
    public async Task<ChannelOverrideTemplateRecord> GetAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                "failed to get channel override template: "
                + "no user in context: ent/privacy: deny rule");
        }

        ChannelOverrideTemplateRecord? template;
        try
        {
            template = await store.GetAsync(id, ScopeOf(current), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get channel override template: " + ex.Message,
                ex);
        }

        if (template is null)
        {
            throw new InvalidOperationException(
                "failed to get channel override template: "
                + "ent: channel_override_template not found");
        }

        return template;
    }

    /// <summary>
    /// Port of the entgql Node lookup for ChannelOverrideTemplate
    /// (<c>client.ChannelOverrideTemplate.Get(ctx, id)</c>): the query policy
    /// denies a caller with no user in context, an owner sees every row and a
    /// non-owner is scoped to their own; a missing or filtered-out row is
    /// <c>ent: channel_override_template not found</c>. Unlike
    /// <see cref="GetAsync"/> the Noder path carries no service prefix.
    /// </summary>
    /// <exception cref="PrivacyDenyException">No user in context.</exception>
    public async Task<ChannelOverrideTemplateRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new PrivacyDenyException(
                "no user in context: " + PrivacyDenyException.DenyMessage);
        }

        var template = await store.GetAsync(id, ScopeOf(current), cancellationToken)
            .ConfigureAwait(false);
        return template
            ?? throw new InvalidOperationException("ent: channel_override_template not found");
    }

    /// <summary>
    /// Port of the paginated channelOverrideTemplates query (ent.resolvers.go:383-397).
    /// </summary>
    public async Task<ListPage<ChannelOverrideTemplateRecord>> ListAsync(
        ChannelOverrideTemplateListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                "no user in context: ent/privacy: deny rule");
        }

        return await store.ListAsync(
                query with { ScopeUserId = ScopeOf(current) },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of mutationResolver.ApplyChannelOverrideTemplate + ApplyTemplate
    /// (axonhub.resolvers.go:669-692; channel_override_template.go:152-243).
    /// The resolver adds the "failed to apply template: " prefix.
    /// </summary>
    public async Task<ChannelOverrideTemplateApplyResult> ApplyAsync(
        ApplyChannelOverrideTemplateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var current = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            throw new InvalidOperationException(
                "failed to fetch template: "
                + "no user in context: ent/privacy: deny rule");
        }

        ChannelOverrideTemplateRecord? template;
        IReadOnlyList<OverrideOperationDraft> templateHeaderOps;
        IReadOnlyList<OverrideOperationDraft> templateBodyOps;
        try
        {
            template = await store.GetAsync(
                    command.TemplateId,
                    ScopeOf(current),
                    cancellationToken)
                .ConfigureAwait(false);

            // Ent scans the JSON columns while fetching the template, so a
            // malformed value fails before the channel query.
            templateHeaderOps = ChannelOverrideOperations.ParseStoredOperations(
                template?.HeaderOverrideOperations);
            templateBodyOps = ChannelOverrideOperations.ParseStoredOperations(
                template?.BodyOverrideOperations);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to fetch template: " + ex.Message,
                ex);
        }

        if (template is null)
        {
            throw new InvalidOperationException(
                "failed to fetch template: ent: channel_override_template not found");
        }

        var channels = await QueryChannelsAsync(command.ChannelIds, cancellationToken)
            .ConfigureAwait(false);
        if (channels.Count != command.ChannelIds.Count)
        {
            throw new InvalidOperationException("some channels not found for provided IDs");
        }

        var writes = new List<ChannelSettingsWrite>(channels.Count);
        foreach (var channel in channels)
        {
            var settings = ParseChannelSettings(channel);
            IReadOnlyList<OverrideOperationDraft> newHeaderOps;
            IReadOnlyList<OverrideOperationDraft> newBodyOps;

            if (command.Mode == ChannelOverrideApplyMode.Replace)
            {
                newHeaderOps = templateHeaderOps;
                newBodyOps = templateBodyOps;
            }
            else
            {
                newHeaderOps = ChannelOverrideOperations.MergeHeaders(
                    ChannelOverrideOperations.GetHeaderOverrideOperations(settings),
                    templateHeaderOps);
                newBodyOps = ChannelOverrideOperations.MergeBody(
                    ChannelOverrideOperations.GetBodyOverrideOperations(settings),
                    templateBodyOps);
            }

            // Go's omitempty drops an empty operation array; the write nulls the
            // legacy fields unconditionally.
            settings.HeaderOverrideOperations = newHeaderOps.Count > 0 ? newHeaderOps : null;
            settings.OverrideHeaders = null;
            settings.BodyOverrideOperations = newBodyOps.Count > 0 ? newBodyOps : null;
            settings.OverrideParameters = string.Empty;

            writes.Add(new ChannelSettingsWrite(
                channel.Id,
                channel.Name,
                GoJson.MarshalToString(settings)));
        }

        var updated = await store.UpdateChannelSettingsAsync(writes, cancellationToken)
            .ConfigureAwait(false);

        reloadHook.ReloadAfterModelSync();

        return new ChannelOverrideTemplateApplyResult(updated);
    }

    /// <summary>
    /// Port of mutationResolver.ClearChannelOverrideTemplates + ClearTemplates
    /// (axonhub.resolvers.go:694-708; channel_override_template.go:245-313).
    /// The resolver adds the "failed to clear templates: " prefix.
    /// </summary>
    public async Task<ChannelOverrideTemplateApplyResult> ClearAsync(
        IReadOnlyList<long> channelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        var channels = await QueryChannelsAsync(channelIds, cancellationToken)
            .ConfigureAwait(false);
        if (channels.Count != channelIds.Count)
        {
            throw new InvalidOperationException("some channels not found for provided IDs");
        }

        var writes = new List<ChannelSettingsWrite>(channels.Count);
        foreach (var channel in channels)
        {
            var settings = ParseChannelSettings(channel);
            settings.HeaderOverrideOperations = null;
            settings.OverrideHeaders = null;
            settings.BodyOverrideOperations = null;
            settings.OverrideParameters = string.Empty;

            writes.Add(new ChannelSettingsWrite(
                channel.Id,
                channel.Name,
                GoJson.MarshalToString(settings)));
        }

        var updated = await store.UpdateChannelSettingsAsync(writes, cancellationToken)
            .ConfigureAwait(false);

        reloadHook.ReloadAfterModelSync();

        return new ChannelOverrideTemplateApplyResult(updated);
    }

    private async Task<IReadOnlyList<ChannelRecord>> QueryChannelsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        try
        {
            return await store.ListChannelsAsync(ids, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to query channels: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Ent scans the settings column while reading the channel rows, so a
    /// malformed value fails the whole query before the transaction starts.
    /// </summary>
    private static ChannelSettingsDraft ParseChannelSettings(ChannelRecord channel)
    {
        try
        {
            return ChannelWriteSerialization.ParseSettingsForOverrideApply(channel.Settings)
                ?? new ChannelSettingsDraft();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to query channels: " + ex.Message,
                ex);
        }
    }

    private static long? ScopeOf(ManagementUser? user)
        => user is null || user.IsOwner ? null : user.Id;

    /// <summary>An explicitly set operation list stores JSON; absent stores JSON null.</summary>
    private static string OperationsJson(IReadOnlyList<OverrideOperationDraft>? operations)
        => operations is null ? "null" : GoJson.MarshalToString(operations);

    /// <summary>A null C# value means the field was not supplied and is not written.</summary>
    private static string? OperationsJsonOrNull(
        IReadOnlyList<OverrideOperationDraft>? operations)
        => operations is null ? null : GoJson.MarshalToString(operations);

    /// <summary>Ent's Append is a no-op for an empty list, so it writes nothing.</summary>
    private static string? AppendJson(IReadOnlyList<OverrideOperationDraft>? operations)
        => operations is { Count: > 0 } ? GoJson.MarshalToString(operations) : null;

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
