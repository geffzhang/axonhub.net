using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// One row of <c>channel_model_prices</c> with its eagerly loaded channel edge
/// (backup.streamChannelModelPrices' <c>WithChannel()</c>). A null
/// <see cref="ChannelName"/> means the edge is missing — the channel is absent
/// or soft-deleted — and the reference skips the element.
/// </summary>
public sealed record BackupChannelModelPriceRow(ChannelModelPriceRecord Price, string? ChannelName);

/// <summary>
/// One row of <c>api_keys</c> with its eagerly loaded project edge
/// (backup.streamAPIKeys' <c>WithProject()</c>). The edge may be missing when
/// the project was soft-deleted; the reference then emits <c>edges: {}</c> and
/// an empty project_name.
/// </summary>
public sealed record BackupApiKeyRow(ApiKeyRecord ApiKey, ProjectRecord? Project);

/// <summary>
/// One row of <c>requests</c> with the names backup.streamUsageRequests reads
/// from its eager loads. Project/channel are only populated for live rows; the
/// API key key is only loaded when IncludeAPIKeys is on.
/// </summary>
public sealed record BackupUsageRequestRow(
    RequestRecord Request,
    string? ProjectName,
    string? ChannelName,
    string? ApiKeyKey);

/// <summary>One row of <c>usage_logs</c> with its eager-loaded project/channel names.</summary>
public sealed record BackupUsageLogRow(UsageLogRecord Log, string? ProjectName, string? ChannelName);

/// <summary>
/// Page source for <see cref="Management.BackupJson"/>. Every method is one
/// ent-style ID-cursor page (<c>id &gt; lastId ORDER BY id ASC LIMIT n</c>) and
/// returns the last row's id as the next cursor (0 for an empty page), exactly
/// like the reference stream functions (backup_ops.go:127-356).
/// </summary>
public interface IBackupBatchSource
{
    Task<(IReadOnlyList<SystemRecord> Rows, long NextId)> FetchSystemBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<ProjectRecord> Rows, long NextId)> FetchProjectBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<ChannelRecord> Rows, long NextId)> FetchChannelBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<ModelRecord> Rows, long NextId)> FetchModelBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<BackupChannelModelPriceRow> Rows, long NextId)>
        FetchChannelModelPriceBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken);

    Task<(IReadOnlyList<BackupApiKeyRow> Rows, long NextId)> FetchApiKeyBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken);

    Task<(IReadOnlyList<BackupUsageRequestRow> Rows, long NextId)>
        FetchUsageRequestBatchAsync(
            long lastId,
            int limit,
            bool includeApiKeys,
            CancellationToken cancellationToken);

    Task<(IReadOnlyList<BackupUsageLogRow> Rows, long NextId)> FetchUsageLogBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// backup_ops.go:321-332: every live API key's id→key map, loaded once
    /// before streaming usage logs when IncludeUsageStats and IncludeAPIKeys
    /// are both on.
    /// </summary>
    Task<IReadOnlyDictionary<long, string>> FetchApiKeyKeyMapAsync(
        CancellationToken cancellationToken);
}

/// <summary>
/// Dapper implementation of the backup page source. Ent queries always carry
/// the SoftDeleteMixin predicate for tables that have <c>deleted_at</c>, and
/// eager-loaded edges (project/channel/API key) only resolve live rows, so the
/// lookups below filter on <c>deleted_at = 0</c> as well.
/// </summary>
public sealed class BackupStore(AxonHubDbContext db) : IBackupBatchSource
{
    private const string SystemColumns = """
        "id", "created_at", "updated_at", "deleted_at", "key", "value"
        """;

    private const string ProjectColumns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "status", "profiles"
        """;

    private const string ChannelColumns = """
        "id", "created_at", "updated_at", "deleted_at", "type", "base_url",
        "name", "status", "credentials", "disabled_api_keys", "supported_models",
        "manual_models", "auto_sync_supported_models", "auto_sync_model_pattern",
        "tags", "default_test_model", "policies", "settings", "ordering_weight",
        "error_message", "auto_disabled_at", "auto_disable_expires_at", "remark",
        "endpoints"
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

    private const string UsageLogColumns = """
        "id", "created_at", "updated_at", "api_key_id", "model_id",
        "prompt_tokens", "completion_tokens", "total_tokens",
        "prompt_audio_tokens", "prompt_cached_tokens",
        "prompt_write_cached_tokens", "prompt_write_cached_tokens_5m",
        "prompt_write_cached_tokens_1h", "completion_audio_tokens",
        "completion_reasoning_tokens", "completion_accepted_prediction_tokens",
        "completion_rejected_prediction_tokens", "source", "format", "total_cost",
        "cost_items", "cost_price_reference_id", "channel_id", "project_id",
        "request_id"
        """;

    public async Task<(IReadOnlyList<SystemRecord> Rows, long NextId)>
        FetchSystemBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var arguments = new[] { args.Add(lastId), args.Add(limit) };
        var keys = new string[SystemConfigBackupKeys.All.Count];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = args.Add(SystemConfigBackupKeys.All[i]);
        }

        var sql = "SELECT " + SystemColumns
            + " FROM \"systems\""
            + " WHERE \"deleted_at\" = 0 AND \"id\" > " + arguments[0]
            + " AND \"key\" IN (" + string.Join(", ", keys) + ")"
            + " ORDER BY \"id\" ASC LIMIT " + arguments[1] + ";";

        var rows = await QueryAsync<SystemRecord>(sql, args, cancellationToken).ConfigureAwait(false);
        return (rows, NextId(rows, static row => row.Id));
    }

    public async Task<(IReadOnlyList<ProjectRecord> Rows, long NextId)>
        FetchProjectBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ProjectColumns
            + " FROM \"projects\""
            + " WHERE \"deleted_at\" = 0 AND \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var rows = await QueryAsync<ProjectRecord>(sql, args, cancellationToken).ConfigureAwait(false);
        return (rows, NextId(rows, static row => row.Id));
    }

    public async Task<(IReadOnlyList<ChannelRecord> Rows, long NextId)>
        FetchChannelBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ChannelColumns
            + " FROM \"channels\""
            + " WHERE \"deleted_at\" = 0 AND \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var rows = await QueryAsync<ChannelRecord>(sql, args, cancellationToken).ConfigureAwait(false);
        return (rows, NextId(rows, static row => row.Id));
    }

    public async Task<(IReadOnlyList<ModelRecord> Rows, long NextId)>
        FetchModelBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ModelColumns
            + " FROM \"models\""
            + " WHERE \"deleted_at\" = 0 AND \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var rows = await QueryAsync<ModelRecord>(sql, args, cancellationToken).ConfigureAwait(false);
        return (rows, NextId(rows, static row => row.Id));
    }

    public async Task<(IReadOnlyList<BackupChannelModelPriceRow> Rows, long NextId)>
        FetchChannelModelPriceBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ChannelModelPriceColumns
            + " FROM \"channel_model_prices\""
            + " WHERE \"deleted_at\" = 0 AND \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var prices = await QueryAsync<ChannelModelPriceRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        // ent's WithChannel() eager load resolves the edge through the channel
        // query, so a soft-deleted channel leaves Edges.Channel nil.
        var names = await LoadLiveNameMapAsync(
                "channels",
                prices.Select(static price => price.ChannelId),
                cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<BackupChannelModelPriceRow>(prices.Count);
        foreach (var price in prices)
        {
            rows.Add(new BackupChannelModelPriceRow(
                price,
                names.GetValueOrDefault(price.ChannelId)));
        }

        return (rows, NextId(rows, static row => row.Price.Id));
    }

    public async Task<(IReadOnlyList<BackupApiKeyRow> Rows, long NextId)>
        FetchApiKeyBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + ApiKeyColumns
            + " FROM \"api_keys\""
            + " WHERE \"deleted_at\" = 0 AND \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var apiKeys = await QueryAsync<ApiKeyRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var projectIds = apiKeys
            .Select(static key => key.ProjectId)
            .Distinct()
            .ToArray();
        var projects = await LoadLiveProjectsAsync(projectIds, cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<BackupApiKeyRow>(apiKeys.Count);
        foreach (var apiKey in apiKeys)
        {
            rows.Add(new BackupApiKeyRow(apiKey, projects.GetValueOrDefault(apiKey.ProjectId)));
        }

        return (rows, NextId(rows, static row => row.ApiKey.Id));
    }

    public async Task<(IReadOnlyList<BackupUsageRequestRow> Rows, long NextId)>
        FetchUsageRequestBatchAsync(
            long lastId,
            int limit,
            bool includeApiKeys,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + RequestColumns
            + " FROM \"requests\""
            + " WHERE \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var requests = await QueryAsync<RequestRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var names = new UsageEdgeNames(
            await LoadLiveNameMapAsync(
                    "projects",
                    requests.Select(static request => request.ProjectId),
                    cancellationToken)
                .ConfigureAwait(false),
            await LoadLiveNameMapAsync(
                    "channels",
                    requests
                        .Where(static request => request.ChannelId is not null)
                        .Select(static request => request.ChannelId!.Value),
                    cancellationToken)
                .ConfigureAwait(false),
            includeApiKeys
                ? await LoadLiveKeyMapAsync(
                        requests
                            .Where(static request => request.ApiKeyId is not null)
                            .Select(static request => request.ApiKeyId!.Value),
                        cancellationToken)
                    .ConfigureAwait(false)
                : EmptyKeys);

        var rows = new List<BackupUsageRequestRow>(requests.Count);
        foreach (var request in requests)
        {
            rows.Add(new BackupUsageRequestRow(
                request,
                names.Projects.GetValueOrDefault(request.ProjectId),
                request.ChannelId is { } channelId
                    ? names.Channels.GetValueOrDefault(channelId)
                    : null,
                includeApiKeys && request.ApiKeyId is { } apiKeyId
                    ? names.ApiKeys.GetValueOrDefault(apiKeyId)
                    : null));
        }

        return (rows, NextId(rows, static row => row.Request.Id));
    }

    public async Task<(IReadOnlyList<BackupUsageLogRow> Rows, long NextId)>
        FetchUsageLogBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + UsageLogColumns
            + " FROM \"usage_logs\""
            + " WHERE \"id\" > " + args.Add(lastId)
            + " ORDER BY \"id\" ASC LIMIT " + args.Add(limit) + ";";

        var logs = await QueryAsync<UsageLogRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var projects = await LoadLiveNameMapAsync(
                "projects",
                logs.Select(static log => log.ProjectId),
                cancellationToken)
            .ConfigureAwait(false);
        var channels = await LoadLiveNameMapAsync(
                "channels",
                logs
                    .Where(static log => log.ChannelId is not null)
                    .Select(static log => log.ChannelId!.Value),
                cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<BackupUsageLogRow>(logs.Count);
        foreach (var log in logs)
        {
            rows.Add(new BackupUsageLogRow(
                log,
                projects.GetValueOrDefault(log.ProjectId),
                log.ChannelId is { } channelId
                    ? channels.GetValueOrDefault(channelId)
                    : null));
        }

        return (rows, NextId(rows, static row => row.Log.Id));
    }

    public async Task<IReadOnlyDictionary<long, string>> FetchApiKeyKeyMapAsync(
        CancellationToken cancellationToken)
    {
        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<IdKeyRow>(
                new CommandDefinition(
                    "SELECT \"id\", \"key\" FROM \"api_keys\" WHERE \"deleted_at\" = 0;",
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
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

    /// <summary>
    /// Ent eager-load equivalent for a name edge: <c>SELECT id, name FROM
    /// &lt;table&gt; WHERE id IN (...) AND deleted_at = 0</c>. The table name is
    /// always a constant supplied by this class.
    /// </summary>
    private async Task<IReadOnlyDictionary<long, string>> LoadLiveNameMapAsync(
        string table,
        IEnumerable<long> ids,
        CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return EmptyNames;
        }

        var args = new SqlArguments();
        var parameters = distinct.Select(id => args.Add(id)).ToArray();
        var sql = "SELECT \"id\", \"name\" FROM \"" + table + "\""
            + " WHERE \"id\" IN (" + string.Join(", ", parameters) + ")"
            + " AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<IdNameRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var map = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            map[row.Id] = row.Name;
        }

        return map;
    }

    private async Task<IReadOnlyDictionary<long, ProjectRecord>> LoadLiveProjectsAsync(
        IReadOnlyCollection<long> ids,
        CancellationToken cancellationToken)
    {
        var distinct = ids.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return new Dictionary<long, ProjectRecord>();
        }

        var args = new SqlArguments();
        var parameters = distinct.Select(id => args.Add(id)).ToArray();
        var sql = "SELECT " + ProjectColumns + " FROM \"projects\""
            + " WHERE \"id\" IN (" + string.Join(", ", parameters) + ")"
            + " AND \"deleted_at\" = 0;";

        var rows = await QueryAsync<ProjectRecord>(sql, args, cancellationToken)
            .ConfigureAwait(false);

        var map = new Dictionary<long, ProjectRecord>();
        foreach (var row in rows)
        {
            map[row.Id] = row;
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
            return EmptyKeys;
        }

        var args = new SqlArguments();
        var parameters = distinct.Select(id => args.Add(id)).ToArray();
        var sql = "SELECT \"id\", \"key\" FROM \"api_keys\""
            + " WHERE \"id\" IN (" + string.Join(", ", parameters) + ")"
            + " AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<IdKeyRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var map = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            map[row.Id] = row.Key;
        }

        return map;
    }

    private static long NextId<T>(IReadOnlyList<T> rows, Func<T, long> id)
        => rows.Count == 0 ? 0 : id(rows[^1]);

    private static readonly IReadOnlyDictionary<long, string> EmptyNames =
        new Dictionary<long, string>();

    private static readonly IReadOnlyDictionary<long, string> EmptyKeys =
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

    /// <summary>Resolved backup edge names for one usage page.</summary>
    private sealed record UsageEdgeNames(
        IReadOnlyDictionary<long, string> Projects,
        IReadOnlyDictionary<long, string> Channels,
        IReadOnlyDictionary<long, string> ApiKeys);
}
