using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AxonHub.Persistence;

/// <summary>
/// Unit of work for <c>backup.BackupService.Restore</c>
/// (internal/server/backup/restore.go:52-75). The reference opens one ent
/// transaction, runs the whole restore inside it and commits once; every store
/// call below joins the ambient context transaction, so the handle only owns
/// the commit/rollback of the transaction this store opened itself.
/// </summary>
public interface IBackupRestoreTransaction : IAsyncDisposable
{
    /// <summary>Port of <c>tx.Commit()</c>.</summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of the deferred <c>tx.Rollback()</c>.</summary>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>One <c>id,name</c> row of a live (not soft-deleted) table.</summary>
public sealed record LiveIdName(long Id, string Name);

/// <summary>One <c>id,key</c> row of the live <c>api_keys</c> table.</summary>
public sealed record LiveIdKey(long Id, string Key);

/// <summary>
/// Port of the <c>Channel.UpdateOneID(existing.ID)</c> builder of
/// restoreChannels (restore.go:731-748). Every column listed here is written
/// unconditionally; only <see cref="BaseUrl"/> keeps the stored value when the
/// backup carries an empty string, exactly like <c>SetNillableBaseURL(nil)</c>.
/// The store stamps <c>updated_at</c> from the time mixin.
/// </summary>
public sealed class ChannelRestoreUpdate
{
    public string? BaseUrl { get; init; }

    public required string Status { get; init; }

    public required string CredentialsJson { get; init; }

    public required string SupportedModelsJson { get; init; }

    public bool AutoSyncSupportedModels { get; init; }

    public required string AutoSyncModelPattern { get; init; }

    public required string ManualModelsJson { get; init; }

    public required string TagsJson { get; init; }

    public required string DefaultTestModel { get; init; }

    public required string SettingsJson { get; init; }

    public long OrderingWeight { get; init; }

    /// <summary>Written verbatim; null clears the column (ClearRemark).</summary>
    public string? Remark { get; init; }
}

/// <summary>Port of the Project.UpdateOneID builder of restoreProjects (restore.go:508-513).</summary>
public sealed class ProjectRestoreUpdate
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string Status { get; init; }

    /// <summary>JSON text; "null" when the backup carried no profiles.</summary>
    public required string ProfilesJson { get; init; }
}

/// <summary>Port of the Model.UpdateOneID builder of restoreModels (restore.go:820-826).</summary>
public sealed class ModelRestoreUpdate
{
    public required string Name { get; init; }

    public required string Icon { get; init; }

    public required string Group { get; init; }

    /// <summary>JSON text; "null" when the backup carried no model card.</summary>
    public required string ModelCardJson { get; init; }

    /// <summary>JSON text; "null" when the backup carried no settings.</summary>
    public required string SettingsJson { get; init; }

    public required string Status { get; init; }

    /// <summary>Written verbatim; null clears the column (ClearRemark).</summary>
    public string? Remark { get; init; }
}

/// <summary>Port of the APIKey.UpdateOneID builder of restoreAPIKeys (restore.go:902-907).</summary>
public sealed class ApiKeyRestoreUpdate
{
    public required string Name { get; init; }

    public required string Type { get; init; }

    public required string Status { get; init; }

    /// <summary>JSON text; "null" when the backup carried no scopes.</summary>
    public required string ScopesJson { get; init; }

    /// <summary>JSON text; "null" when the backup carried no profiles.</summary>
    public required string ProfilesJson { get; init; }
}

/// <summary>
/// Persistence seam for the restore half of
/// <c>internal/server/backup/restore.go</c>. Reads resolve the reference's ent
/// queries (soft-delete predicates, eager-loaded edges restricted to live rows)
/// through Dapper; writes go through EF Core. All calls share the context's
/// connection and the transaction opened by <see cref="BeginTransactionAsync"/>,
/// so later reads observe earlier uncommitted writes like the reference's
/// <c>tx.Client()</c>.
/// </summary>
public interface IBackupRestoreStore
{
    /// <summary>
    /// Port of <c>svc.db.Tx(ctx)</c>. When the context already carries a
    /// transaction the returned handle is a no-op that leaves commit/rollback to
    /// the ambient owner.
    /// </summary>
    Task<IBackupRestoreTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default);

    // ------------------------------------------------------------- channels

    /// <summary>Port of <c>db.Channel.Query().Where(channel.Name(name)).First(ctx)</c>.</summary>
    Task<ChannelRecord?> FindLiveChannelByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Port of <c>db.Channel.Query().Where(channel.IDEQ(id)).Only(ctx)</c>.</summary>
    Task<ChannelRecord?> FindLiveChannelByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>Port of <c>Channel.Query().Select(id, name).All(ctx)</c>.</summary>
    Task<IReadOnlyList<LiveIdName>> ListLiveChannelIdNamesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of the <c>NameIn(names...)</c> channel projection used by buildChannelIDMap.</summary>
    Task<IReadOnlyList<LiveIdName>> ListLiveChannelIdNamesByNamesAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default);

    /// <summary>Port of the <c>Channel.Create()</c> of restoreChannels.</summary>
    Task<ChannelRecord> CreateChannelAsync(
        ChannelRecord channel,
        CancellationToken cancellationToken = default);

    /// <summary>Port of the overwrite builder of restoreChannels.</summary>
    Task UpdateChannelAsync(
        long id,
        ChannelRestoreUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>Channel.UpdateOneID(id).SetSettings(&amp;settings).Save(ctx)</c>
    /// (restoreLegacyQuotaRouting, restore.go:190).
    /// </summary>
    Task SetChannelSettingsAsync(
        long id,
        string settingsJson,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>Channel.UpdateOneID(id).SetUpdatedAt(now).Exec(ctx)</c>
    /// (restore.go:690-694). The explicit timestamp overrides the mixin default.
    /// </summary>
    Task SetChannelUpdatedAtAsync(
        long id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    // ------------------------------------------------------------- projects

    Task<IReadOnlyList<LiveIdName>> ListLiveProjectIdNamesAsync(
        CancellationToken cancellationToken = default);

    Task<ProjectRecord?> FindLiveProjectByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<ProjectRecord> CreateProjectAsync(
        ProjectRecord project,
        CancellationToken cancellationToken = default);

    Task UpdateProjectAsync(
        long id,
        ProjectRestoreUpdate update,
        CancellationToken cancellationToken = default);

    // --------------------------------------------------------------- models

    Task<ModelRecord?> FindLiveModelAsync(
        string developer,
        string modelId,
        CancellationToken cancellationToken = default);

    Task<ModelRecord> CreateModelAsync(
        ModelRecord model,
        CancellationToken cancellationToken = default);

    Task UpdateModelAsync(
        long id,
        ModelRestoreUpdate update,
        CancellationToken cancellationToken = default);

    // ------------------------------------------------------------- api keys

    Task<IReadOnlyList<LiveIdKey>> ListLiveApiKeyIdKeysAsync(
        CancellationToken cancellationToken = default);

    Task<ApiKeyRecord?> FindLiveApiKeyByKeyAsync(
        string key,
        CancellationToken cancellationToken = default);

    Task<ApiKeyRecord> CreateApiKeyAsync(
        ApiKeyRecord apiKey,
        CancellationToken cancellationToken = default);

    Task UpdateApiKeyAsync(
        long id,
        ApiKeyRestoreUpdate update,
        CancellationToken cancellationToken = default);

    // ------------------------------------------------- channel model prices

    Task<ChannelModelPriceRecord?> FindLiveChannelModelPriceAsync(
        long channelId,
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the archive statement of the overwrite branch
    /// (restore.go:622-632): active versions of the price become archived with
    /// <c>effective_end_at = now</c>.
    /// </summary>
    Task ArchiveActiveChannelModelPriceVersionsAsync(
        long channelModelPriceId,
        DateTime now,
        CancellationToken cancellationToken = default);

    Task UpdateChannelModelPriceAsync(
        long id,
        string priceJson,
        string referenceId,
        CancellationToken cancellationToken = default);

    Task<ChannelModelPriceRecord> CreateChannelModelPriceAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default);

    Task CreateChannelModelPriceVersionAsync(
        ChannelModelPriceVersionRecord version,
        CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- usage

    /// <summary>
    /// Port of <c>Request.Query().Where(request.IDIn(...)).WithProject().WithChannel().WithAPIKey().All(ctx)</c>.
    /// </summary>
    Task<IReadOnlyList<BackupUsageRequestRow>> FindUsageRequestsByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>Port of the <c>CreatedAtIn(...)</c> variant of existingUsageRequests.</summary>
    Task<IReadOnlyList<BackupUsageRequestRow>> FindUsageRequestsByCreatedAtAsync(
        IReadOnlyList<DateTime> createdAt,
        CancellationToken cancellationToken = default);

    Task<RequestRecord> CreateUsageRequestAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>UsageLog.Query().Where(usagelog.RequestIDIn(...)).Select(usagelog.FieldRequestID).All(ctx)</c>.
    /// </summary>
    Task<HashSet<long>> ListUsageLogRequestIdsAsync(
        IReadOnlyList<long> requestIds,
        CancellationToken cancellationToken = default);

    /// <summary>Port of <c>UsageLog.CreateBulk(builders...).Save(ctx)</c>.</summary>
    Task CreateUsageLogsAsync(
        IReadOnlyList<UsageLogRecord> logs,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dapper/EF implementation of the restore seam. Every read carries the ent
/// predicate the reference would apply (SoftDeleteMixin tables filter
/// <c>deleted_at = 0</c>); the added <c>ORDER BY id ASC</c> makes the
/// reference's unordered <c>First</c> deterministic (C198).
/// </summary>
public sealed class BackupRestoreStore(AxonHubDbContext db) : IBackupRestoreStore
{
    /// <summary>backup.backupBatchSize (backup_ops.go:23).</summary>
    public const int BatchSize = 500;

    private const string ChannelColumns = """
        "id", "created_at", "updated_at", "deleted_at", "type", "base_url",
        "name", "status", "credentials", "disabled_api_keys", "supported_models",
        "manual_models", "auto_sync_supported_models", "auto_sync_model_pattern",
        "tags", "default_test_model", "policies", "settings", "ordering_weight",
        "error_message", "auto_disabled_at", "auto_disable_expires_at", "remark",
        "endpoints"
        """;

    private const string ProjectColumns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "status", "profiles"
        """;

    private const string ModelColumns = """
        "id", "created_at", "updated_at", "deleted_at", "developer", "model_id",
        "type", "name", "icon", "group", "model_card", "settings", "status",
        "remark"
        """;

    private const string ChannelModelPriceColumns = """
        "id", "created_at", "updated_at", "deleted_at", "model_id", "price",
        "reference_id", "channel_id"
        """;

    private const string ApiKeyColumns = """
        "id", "created_at", "updated_at", "deleted_at", "key", "name", "type",
        "status", "scopes", "profiles", "allowed_ips", "project_id", "user_id"
        """;

    private const string RequestColumns = """
        "id", "created_at", "updated_at", "user_id", "source", "model_id",
        "reasoning_effort", "format", "request_headers", "request_body",
        "response_headers", "response_body", "response_chunks", "external_id",
        "status", "stream", "client_ip", "user_agent", "metrics_latency_ms",
        "metrics_first_token_latency_ms", "metrics_reasoning_duration_ms",
        "content_saved", "content_storage_id", "content_storage_key",
        "content_saved_at", "api_key_id", "channel_id", "data_storage_id",
        "project_id", "trace_id"
        """;

    // --------------------------------------------------------- transaction

    public async Task<IBackupRestoreTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return AmbientBackupRestoreTransaction.Instance;
        }

        IDbContextTransaction transaction;
        try
        {
            transaction = await db.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        return new OwnedBackupRestoreTransaction(transaction);
    }

    private sealed class OwnedBackupRestoreTransaction(IDbContextTransaction transaction)
        : IBackupRestoreTransaction
    {
        private bool _completed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                // The reference's deferred rollback ignores its own error
                // (restore.go:59-63).
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Ignored like the reference.
                }
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class AmbientBackupRestoreTransaction : IBackupRestoreTransaction
    {
        public static AmbientBackupRestoreTransaction Instance { get; } = new();

        public Task CommitAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ------------------------------------------------------------ channels

    public async Task<ChannelRecord?> FindLiveChannelByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ChannelColumns + " FROM \"channels\""
            + " WHERE \"name\" = " + args.Add(name) + " AND \"deleted_at\" = 0"
            + " ORDER BY \"id\" ASC LIMIT 1;";

        var rows = await QueryAsync<ChannelRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<ChannelRecord?> FindLiveChannelByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ChannelColumns + " FROM \"channels\""
            + " WHERE \"id\" = " + args.Add(id) + " AND \"deleted_at\" = 0"
            + " LIMIT 1;";

        var rows = await QueryAsync<ChannelRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<LiveIdName>> ListLiveChannelIdNamesAsync(
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT \"id\", \"name\" FROM \"channels\""
            + " WHERE \"deleted_at\" = 0 ORDER BY \"id\" ASC;";

        var rows = await QueryAsync<IdNameRow>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static row => new LiveIdName(row.Id, row.Name)).ToList();
    }

    public async Task<IReadOnlyList<LiveIdName>> ListLiveChannelIdNamesByNamesAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        var result = new List<LiveIdName>(names.Count);
        foreach (var batch in Batch(names, BatchSize))
        {
            var args = new SqlArguments();
            var parameters = batch.Select(name => args.Add(name)).ToArray();
            var sql = "SELECT \"id\", \"name\" FROM \"channels\""
                + " WHERE \"name\" IN (" + string.Join(", ", parameters) + ")"
                + " AND \"deleted_at\" = 0 ORDER BY \"id\" ASC;";

            var rows = await QueryAsync<IdNameRow>(sql, args, cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                result.Add(new LiveIdName(row.Id, row.Name));
            }
        }

        return result;
    }

    public async Task<ChannelRecord> CreateChannelAsync(
        ChannelRecord channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        try
        {
            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ent's Save returns the driver error verbatim; EF wraps it in a
            // DbUpdateException, so the reference-visible text is the base one.
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task UpdateChannelAsync(
        long id,
        ChannelRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.Channels
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(
                            row => row.BaseUrl,
                            row => update.BaseUrl ?? row.BaseUrl)
                        .SetProperty(row => row.Status, update.Status)
                        .SetProperty(row => row.Credentials, update.CredentialsJson)
                        .SetProperty(row => row.SupportedModels, update.SupportedModelsJson)
                        .SetProperty(
                            row => row.AutoSyncSupportedModels,
                            update.AutoSyncSupportedModels)
                        .SetProperty(
                            row => row.AutoSyncModelPattern,
                            update.AutoSyncModelPattern)
                        .SetProperty(row => row.ManualModels, update.ManualModelsJson)
                        .SetProperty(row => row.Tags, update.TagsJson)
                        .SetProperty(row => row.DefaultTestModel, update.DefaultTestModel)
                        .SetProperty(row => row.Settings, update.SettingsJson)
                        .SetProperty(row => row.OrderingWeight, update.OrderingWeight)
                        .SetProperty(row => row.Remark, update.Remark)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        if (affected == 0)
        {
            throw NotFound("channel");
        }
    }

    public async Task SetChannelSettingsAsync(
        long id,
        string settingsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsJson);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.Channels
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Settings, settingsJson)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        if (affected == 0)
        {
            throw NotFound("channel");
        }
    }

    public async Task SetChannelUpdatedAtAsync(
        long id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ent's Exec does not surface a missing row as an error.
            await db.Channels
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(row => row.UpdatedAt, updatedAt),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    // ------------------------------------------------------------ projects

    public async Task<IReadOnlyList<LiveIdName>> ListLiveProjectIdNamesAsync(
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT \"id\", \"name\" FROM \"projects\""
            + " WHERE \"deleted_at\" = 0 ORDER BY \"id\" ASC;";

        var rows = await QueryAsync<IdNameRow>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static row => new LiveIdName(row.Id, row.Name)).ToList();
    }

    public async Task<ProjectRecord?> FindLiveProjectByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ProjectColumns + " FROM \"projects\""
            + " WHERE \"name\" = " + args.Add(name) + " AND \"deleted_at\" = 0"
            + " ORDER BY \"id\" ASC LIMIT 1;";

        var rows = await QueryAsync<ProjectRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<ProjectRecord> CreateProjectAsync(
        ProjectRecord project,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        try
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return project;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task UpdateProjectAsync(
        long id,
        ProjectRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.Projects
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Name, update.Name)
                        .SetProperty(row => row.Description, update.Description)
                        .SetProperty(row => row.Status, update.Status)
                        .SetProperty(row => row.Profiles, update.ProfilesJson)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        if (affected == 0)
        {
            throw NotFound("project");
        }
    }

    // -------------------------------------------------------------- models

    public async Task<ModelRecord?> FindLiveModelAsync(
        string developer,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ModelColumns + " FROM \"models\""
            + " WHERE \"developer\" = " + args.Add(developer)
            + " AND \"model_id\" = " + args.Add(modelId)
            + " AND \"deleted_at\" = 0 ORDER BY \"id\" ASC LIMIT 1;";

        var rows = await QueryAsync<ModelRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<ModelRecord> CreateModelAsync(
        ModelRecord model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        try
        {
            db.Models.Add(model);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return model;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task UpdateModelAsync(
        long id,
        ModelRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.Models
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Name, update.Name)
                        .SetProperty(row => row.Icon, update.Icon)
                        .SetProperty(row => row.Group, update.Group)
                        .SetProperty(row => row.ModelCard, update.ModelCardJson)
                        .SetProperty(row => row.Settings, update.SettingsJson)
                        .SetProperty(row => row.Status, update.Status)
                        .SetProperty(row => row.Remark, update.Remark)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        if (affected == 0)
        {
            throw NotFound("model");
        }
    }

    // ------------------------------------------------------------ api keys

    public async Task<IReadOnlyList<LiveIdKey>> ListLiveApiKeyIdKeysAsync(
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT \"id\", \"key\" FROM \"api_keys\""
            + " WHERE \"deleted_at\" = 0 ORDER BY \"id\" ASC;";

        var rows = await QueryAsync<IdKeyRow>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(static row => new LiveIdKey(row.Id, row.Key)).ToList();
    }

    public async Task<ApiKeyRecord?> FindLiveApiKeyByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ApiKeyColumns + " FROM \"api_keys\""
            + " WHERE \"key\" = " + args.Add(key) + " AND \"deleted_at\" = 0"
            + " ORDER BY \"id\" ASC LIMIT 1;";

        var rows = await QueryAsync<ApiKeyRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<ApiKeyRecord> CreateApiKeyAsync(
        ApiKeyRecord apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        try
        {
            db.ApiKeys.Add(apiKey);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return apiKey;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task UpdateApiKeyAsync(
        long id,
        ApiKeyRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.ApiKeys
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Name, update.Name)
                        .SetProperty(row => row.Type, update.Type)
                        .SetProperty(row => row.Status, update.Status)
                        .SetProperty(row => row.Scopes, update.ScopesJson)
                        .SetProperty(row => row.Profiles, update.ProfilesJson)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        if (affected == 0)
        {
            throw NotFound("api_key");
        }
    }

    // ------------------------------------------------ channel model prices

    public async Task<ChannelModelPriceRecord?> FindLiveChannelModelPriceAsync(
        long channelId,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ChannelModelPriceColumns + " FROM \"channel_model_prices\""
            + " WHERE \"channel_id\" = " + args.Add(channelId)
            + " AND \"model_id\" = " + args.Add(modelId)
            + " AND \"deleted_at\" = 0 ORDER BY \"id\" ASC LIMIT 1;";

        var rows = await QueryAsync<ChannelModelPriceRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task ArchiveActiveChannelModelPriceVersionsAsync(
        long channelModelPriceId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await db.ChannelModelPriceVersions
                .Where(row => row.ChannelModelPriceId == channelModelPriceId
                    && row.Status == "active")
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Status, "archived")
                        .SetProperty(row => row.EffectiveEndAt, now)
                        .SetProperty(row => row.UpdatedAt, DateTime.UtcNow),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task UpdateChannelModelPriceAsync(
        long id,
        string priceJson,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(priceJson);
        ArgumentNullException.ThrowIfNull(referenceId);

        var now = DateTime.UtcNow;
        int affected;
        try
        {
            affected = await db.ChannelModelPrices
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Price, priceJson)
                        .SetProperty(row => row.ReferenceId, referenceId)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }

        if (affected == 0)
        {
            throw NotFound("channel_model_price");
        }
    }

    public async Task<ChannelModelPriceRecord> CreateChannelModelPriceAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(price);

        try
        {
            db.ChannelModelPrices.Add(price);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return price;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task CreateChannelModelPriceVersionAsync(
        ChannelModelPriceVersionRecord version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        try
        {
            db.ChannelModelPriceVersions.Add(version);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    // --------------------------------------------------------------- usage

    public async Task<IReadOnlyList<BackupUsageRequestRow>> FindUsageRequestsByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = new List<BackupUsageRequestRow>(ids.Count);
        foreach (var batch in Batch(ids, BatchSize))
        {
            var args = new SqlArguments();
            var parameters = batch.Select(id => args.Add(id)).ToArray();
            var sql = "SELECT " + RequestColumns + " FROM \"requests\""
                + " WHERE \"id\" IN (" + string.Join(", ", parameters) + ")"
                + " ORDER BY \"id\" ASC;";

            rows.AddRange(await LoadUsageRequestRowsAsync(sql, args, cancellationToken)
                .ConfigureAwait(false));
        }

        return rows;
    }

    public async Task<IReadOnlyList<BackupUsageRequestRow>> FindUsageRequestsByCreatedAtAsync(
        IReadOnlyList<DateTime> createdAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createdAt);
        if (createdAt.Count == 0)
        {
            return [];
        }

        var rows = new List<BackupUsageRequestRow>(createdAt.Count);
        foreach (var batch in Batch(createdAt, BatchSize))
        {
            var args = new SqlArguments();
            var parameters = batch.Select(value => args.Add(value)).ToArray();
            var sql = "SELECT " + RequestColumns + " FROM \"requests\""
                + " WHERE \"created_at\" IN (" + string.Join(", ", parameters) + ")"
                + " ORDER BY \"id\" ASC;";

            rows.AddRange(await LoadUsageRequestRowsAsync(sql, args, cancellationToken)
                .ConfigureAwait(false));
        }

        return rows;
    }

    public async Task<RequestRecord> CreateUsageRequestAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            db.Requests.Add(request);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return request;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task<HashSet<long>> ListUsageLogRequestIdsAsync(
        IReadOnlyList<long> requestIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestIds);

        var result = new HashSet<long>();
        foreach (var batch in Batch(requestIds, BatchSize))
        {
            var args = new SqlArguments();
            var parameters = batch.Select(id => args.Add(id)).ToArray();
            var sql = "SELECT DISTINCT \"request_id\" FROM \"usage_logs\""
                + " WHERE \"request_id\" IN (" + string.Join(", ", parameters) + ");";

            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = await connection.QueryAsync<long>(
                    new CommandDefinition(
                        sql,
                        args.Parameters,
                        transaction: db.CurrentDbTransaction,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            foreach (var requestId in rows)
            {
                result.Add(requestId);
            }
        }

        return result;
    }

    public async Task CreateUsageLogsAsync(
        IReadOnlyList<UsageLogRecord> logs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (logs.Count == 0)
        {
            return;
        }

        try
        {
            // ent's CreateBulk inserts the whole batch in one statement.
            db.UsageLogs.AddRange(logs);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>
    /// Runs one request page and resolves the eager-loaded project, channel
    /// and API key edges. Like ent's <c>WithProject/WithChannel/WithAPIKey</c>,
    /// only live edge rows resolve; a soft-deleted edge leaves the name null.
    /// </summary>
    private async Task<IReadOnlyList<BackupUsageRequestRow>> LoadUsageRequestRowsAsync(
        string sql,
        SqlArguments args,
        CancellationToken cancellationToken)
    {
        var requests = await QueryAsync<RequestRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var projects = await LoadLiveNameMapAsync(
                "projects",
                requests.Select(static request => request.ProjectId),
                cancellationToken)
            .ConfigureAwait(false);
        var channels = await LoadLiveNameMapAsync(
                "channels",
                requests
                    .Where(static request => request.ChannelId is not null)
                    .Select(static request => request.ChannelId!.Value),
                cancellationToken)
            .ConfigureAwait(false);
        var apiKeys = await LoadLiveKeyMapAsync(
                requests
                    .Where(static request => request.ApiKeyId is not null)
                    .Select(static request => request.ApiKeyId!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<BackupUsageRequestRow>(requests.Count);
        foreach (var request in requests)
        {
            rows.Add(new BackupUsageRequestRow(
                request,
                projects.GetValueOrDefault(request.ProjectId),
                request.ChannelId is { } channelId
                    ? channels.GetValueOrDefault(channelId)
                    : null,
                request.ApiKeyId is { } apiKeyId
                    ? apiKeys.GetValueOrDefault(apiKeyId)
                    : null));
        }

        return rows;
    }

    private async Task<IReadOnlyDictionary<long, string>> LoadLiveNameMapAsync(
        string table,
        IEnumerable<long> ids,
        CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return EmptyMap;
        }

        var args = new SqlArguments();
        var parameters = distinct.Select(id => args.Add(id)).ToArray();
        var sql = "SELECT \"id\", \"name\" FROM \"" + table + "\""
            + " WHERE \"id\" IN (" + string.Join(", ", parameters) + ")"
            + " AND \"deleted_at\" = 0;";

        var rows = await QueryAsync<IdNameRow>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            map[row.Id] = row.Name;
        }

        return map;
    }

    private async Task<IReadOnlyDictionary<long, string>> LoadLiveKeyMapAsync(
        IEnumerable<long> ids,
        CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return EmptyMap;
        }

        var args = new SqlArguments();
        var parameters = distinct.Select(id => args.Add(id)).ToArray();
        var sql = "SELECT \"id\", \"key\" FROM \"api_keys\""
            + " WHERE \"id\" IN (" + string.Join(", ", parameters) + ")"
            + " AND \"deleted_at\" = 0;";

        var rows = await QueryAsync<IdKeyRow>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            map[row.Id] = row.Key;
        }

        return map;
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        SqlArguments args,
        CancellationToken cancellationToken)
    {
        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<T>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.AsList();
    }

    private static StoreFailureException NotFound(string label)
        => new(
            "ent: " + label + " not found",
            new InvalidOperationException("ent: " + label + " not found"));

    private static IEnumerable<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items, int size)
    {
        for (var start = 0; start < items.Count; start += size)
        {
            var count = Math.Min(size, items.Count - start);
            var batch = new T[count];
            for (var i = 0; i < count; i++)
            {
                batch[i] = items[start + i];
            }

            yield return batch;
        }
    }

    private static readonly IReadOnlyDictionary<long, string> EmptyMap =
        new Dictionary<long, string>();

    private sealed class IdNameRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class IdKeyRow
    {
        public long Id { get; set; }

        public string Key { get; set; } = string.Empty;
    }
}
