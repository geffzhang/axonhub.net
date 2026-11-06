using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory IBackupBatchSource with the reference's ID-cursor paging and
/// live-edge resolution (backup_ops.go:127-356). The SQL rendering itself is
/// covered by BackupStoreTests in AxonHub.Persistence.Tests.
/// </summary>
internal sealed class FakeBackupBatchSource : IBackupBatchSource
{
    public List<SystemRecord> Systems { get; } = [];

    public List<ProjectRecord> Projects { get; } = [];

    public List<ChannelRecord> Channels { get; } = [];

    public List<ModelRecord> Models { get; } = [];

    public List<ChannelModelPriceRecord> Prices { get; } = [];

    public List<ApiKeyRecord> ApiKeys { get; } = [];

    public List<RequestRecord> Requests { get; } = [];

    public List<UsageLogRecord> UsageLogs { get; } = [];

    /// <summary>Overrides <see cref="ApiKeys"/> for the api-key key map lookup.</summary>
    public Dictionary<long, string> ApiKeyKeys { get; } = [];

    public int FetchUsageRequestCalls { get; private set; }

    public Exception? Failure { get; set; }

    public Task<(IReadOnlyList<SystemRecord> Rows, long NextId)> FetchSystemBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken)
        => Page(
            Systems.Where(row => row.DeletedAt == 0
                && SystemConfigBackupKeys.All.Contains(row.Key)),
            lastId,
            limit,
            static row => row.Id);

    public Task<(IReadOnlyList<ProjectRecord> Rows, long NextId)> FetchProjectBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken)
        => Page(
            Projects.Where(row => row.DeletedAt == 0),
            lastId,
            limit,
            static row => row.Id);

    public Task<(IReadOnlyList<ChannelRecord> Rows, long NextId)> FetchChannelBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken)
        => Page(
            Channels.Where(row => row.DeletedAt == 0),
            lastId,
            limit,
            static row => row.Id);

    public Task<(IReadOnlyList<ModelRecord> Rows, long NextId)> FetchModelBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken)
        => Page(
            Models.Where(row => row.DeletedAt == 0),
            lastId,
            limit,
            static row => row.Id);

    public Task<(IReadOnlyList<BackupChannelModelPriceRow> Rows, long NextId)>
        FetchChannelModelPriceBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var page = Slice(Prices.Where(row => row.DeletedAt == 0), lastId, limit);
        var rows = page
            .Select(price => new BackupChannelModelPriceRow(
                price,
                LiveChannelName(price.ChannelId)))
            .ToList();
        return Task.FromResult<(
            IReadOnlyList<BackupChannelModelPriceRow> Rows,
            long NextId)>((rows, NextId(rows, static row => row.Price.Id)));
    }

    public Task<(IReadOnlyList<BackupApiKeyRow> Rows, long NextId)> FetchApiKeyBatchAsync(
        long lastId,
        int limit,
        CancellationToken cancellationToken)
    {
        var page = Slice(ApiKeys.Where(row => row.DeletedAt == 0), lastId, limit);
        var rows = page
            .Select(key => new BackupApiKeyRow(
                key,
                Projects.SingleOrDefault(project =>
                    project.Id == key.ProjectId && project.DeletedAt == 0)))
            .ToList();
        return Task.FromResult<(
            IReadOnlyList<BackupApiKeyRow> Rows,
            long NextId)>((rows, NextId(rows, static row => row.ApiKey.Id)));
    }

    public Task<(IReadOnlyList<BackupUsageRequestRow> Rows, long NextId)>
        FetchUsageRequestBatchAsync(
            long lastId,
            int limit,
            bool includeApiKeys,
            CancellationToken cancellationToken)
    {
        FetchUsageRequestCalls++;
        var page = Slice(Requests, lastId, limit);
        var rows = page
            .Select(request => new BackupUsageRequestRow(
                request,
                Projects.SingleOrDefault(project => project.Id == request.ProjectId)?.Name,
                request.ChannelId is { } channelId
                    ? LiveChannelName(channelId)
                    : null,
                includeApiKeys && request.ApiKeyId is { } apiKeyId
                    ? LiveApiKeyKey(apiKeyId)
                    : null))
            .ToList();
        return Task.FromResult<(
            IReadOnlyList<BackupUsageRequestRow> Rows,
            long NextId)>((rows, NextId(rows, static row => row.Request.Id)));
    }

    public Task<(IReadOnlyList<BackupUsageLogRow> Rows, long NextId)>
        FetchUsageLogBatchAsync(
            long lastId,
            int limit,
            CancellationToken cancellationToken)
    {
        var page = Slice(UsageLogs, lastId, limit);
        var rows = page
            .Select(log => new BackupUsageLogRow(
                log,
                Projects.SingleOrDefault(project => project.Id == log.ProjectId)?.Name,
                log.ChannelId is { } channelId
                    ? LiveChannelName(channelId)
                    : null))
            .ToList();
        return Task.FromResult<(
            IReadOnlyList<BackupUsageLogRow> Rows,
            long NextId)>((rows, NextId(rows, static row => row.Log.Id)));
    }

    public Task<IReadOnlyDictionary<long, string>> FetchApiKeyKeyMapAsync(
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, string>();
        foreach (var key in ApiKeys.Where(row => row.DeletedAt == 0))
        {
            map[key.Id] = key.Key;
        }

        foreach (var pair in ApiKeyKeys)
        {
            map[pair.Key] = pair.Value;
        }

        return Task.FromResult<IReadOnlyDictionary<long, string>>(map);
    }

    private Task<(IReadOnlyList<T> Rows, long NextId)> Page<T>(
        IEnumerable<T> source,
        long lastId,
        int limit,
        Func<T, long> id)
    {
        if (Failure is not null)
        {
            return Task.FromException<(IReadOnlyList<T> Rows, long NextId)>(
                Failure);
        }

        var rows = Slice(source, lastId, limit);
        return Task.FromResult<(IReadOnlyList<T> Rows, long NextId)>(
            (rows, NextId(rows, id)));
    }

    private static List<T> Slice<T>(IEnumerable<T> source, long lastId, int limit)
        => source
            .OrderBy(IdOf<T>())
            .Where(row => IdOf<T>()(row) > lastId)
            .Take(limit)
            .ToList();

    private static Func<T, long> IdOf<T>()
    {
        if (typeof(T) == typeof(SystemRecord))
        {
            return row => ((SystemRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(ProjectRecord))
        {
            return row => ((ProjectRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(ChannelRecord))
        {
            return row => ((ChannelRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(ModelRecord))
        {
            return row => ((ModelRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(ChannelModelPriceRecord))
        {
            return row => ((ChannelModelPriceRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(ApiKeyRecord))
        {
            return row => ((ApiKeyRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(RequestRecord))
        {
            return row => ((RequestRecord)(object)row!).Id;
        }

        if (typeof(T) == typeof(UsageLogRecord))
        {
            return row => ((UsageLogRecord)(object)row!).Id;
        }

        throw new InvalidOperationException("unsupported backup record " + typeof(T));
    }

    private static long NextId<T>(IReadOnlyList<T> rows, Func<T, long> id)
        => rows.Count == 0 ? 0 : id(rows[^1]);

    private string? LiveChannelName(long channelId)
        => Channels.SingleOrDefault(row =>
            row.Id == channelId && row.DeletedAt == 0)?.Name;

    private string? LiveApiKeyKey(long apiKeyId)
        => ApiKeys.SingleOrDefault(row =>
            row.Id == apiKeyId && row.DeletedAt == 0)?.Key;
}

/// <summary>
/// In-memory IBackupRestoreStore. Reads carry the reference's live-row
/// predicates; writes mutate the seeded rows and record the call so the
/// restore-order and conflict-strategy tests can observe them.
/// </summary>
internal sealed class FakeBackupRestoreStore : IBackupRestoreStore
{
    public List<ChannelRecord> Channels { get; } = [];

    public List<ProjectRecord> Projects { get; } = [];

    public List<ModelRecord> Models { get; } = [];

    public List<ApiKeyRecord> ApiKeys { get; } = [];

    public List<ChannelModelPriceRecord> Prices { get; } = [];

    public List<ChannelModelPriceVersionRecord> Versions { get; } = [];

    public List<RequestRecord> Requests { get; } = [];

    public List<UsageLogRecord> UsageLogs { get; } = [];

    public List<string> Calls { get; } = [];

    public int BeginCalls { get; private set; }

    public int CommitCalls { get; private set; }

    public int RollbackCalls { get; private set; }

    public Exception? BeginFailure { get; set; }

    public Exception? CommitFailure { get; set; }

    public Exception? CreateChannelFailure { get; set; }

    public Exception? CreateUsageLogFailure { get; set; }

    public Func<ChannelRecord, ChannelRecord?>? FindChannelByNameOverride { get; set; }

    public Task<IBackupRestoreTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        BeginCalls++;
        if (BeginFailure is not null)
        {
            return Task.FromException<IBackupRestoreTransaction>(BeginFailure);
        }

        return Task.FromResult<IBackupRestoreTransaction>(new FakeTransaction(this));
    }

    public Task<ChannelRecord?> FindLiveChannelByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        if (FindChannelByNameOverride is not null)
        {
            return Task.FromResult(FindChannelByNameOverride(
                new ChannelRecord { Name = name }));
        }

        return Task.FromResult(Channels
            .Where(row => row.DeletedAt == 0 && row.Name == name)
            .OrderBy(row => row.Id)
            .FirstOrDefault());
    }

    public Task<ChannelRecord?> FindLiveChannelByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Channels.SingleOrDefault(row =>
            row.Id == id && row.DeletedAt == 0));

    public Task<IReadOnlyList<LiveIdName>> ListLiveChannelIdNamesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LiveIdName>>(Channels
            .Where(row => row.DeletedAt == 0)
            .OrderBy(row => row.Id)
            .Select(row => new LiveIdName(row.Id, row.Name))
            .ToList());

    public Task<IReadOnlyList<LiveIdName>> ListLiveChannelIdNamesByNamesAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LiveIdName>>(Channels
            .Where(row => row.DeletedAt == 0 && names.Contains(row.Name))
            .OrderBy(row => row.Id)
            .Select(row => new LiveIdName(row.Id, row.Name))
            .ToList());

    public Task<ChannelRecord> CreateChannelAsync(
        ChannelRecord channel,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("channel.create:" + channel.Name);
        if (CreateChannelFailure is not null)
        {
            return Task.FromException<ChannelRecord>(CreateChannelFailure);
        }

        channel.Id = NextId(Channels);
        Channels.Add(channel);
        return Task.FromResult(channel);
    }

    public Task UpdateChannelAsync(
        long id,
        ChannelRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("channel.update:" + id);
        var row = Channels.SingleOrDefault(channel => channel.Id == id);
        if (row is null)
        {
            return Task.FromException(new InvalidOperationException("ent: channel not found"));
        }

        if (update.BaseUrl is not null)
        {
            row.BaseUrl = update.BaseUrl;
        }

        row.Status = update.Status;
        row.Credentials = update.CredentialsJson;
        row.SupportedModels = update.SupportedModelsJson;
        row.AutoSyncSupportedModels = update.AutoSyncSupportedModels;
        row.AutoSyncModelPattern = update.AutoSyncModelPattern;
        row.ManualModels = update.ManualModelsJson;
        row.Tags = update.TagsJson;
        row.DefaultTestModel = update.DefaultTestModel;
        row.Settings = update.SettingsJson;
        row.OrderingWeight = update.OrderingWeight;
        row.Remark = update.Remark;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task SetChannelSettingsAsync(
        long id,
        string settingsJson,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("channel.settings:" + id);
        var row = Channels.SingleOrDefault(channel => channel.Id == id);
        if (row is null)
        {
            return Task.FromException(new InvalidOperationException("ent: channel not found"));
        }

        row.Settings = settingsJson;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task SetChannelUpdatedAtAsync(
        long id,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("channel.updated_at:" + id);
        var row = Channels.SingleOrDefault(channel => channel.Id == id);
        if (row is not null)
        {
            row.UpdatedAt = updatedAt;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LiveIdName>> ListLiveProjectIdNamesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LiveIdName>>(Projects
            .Where(row => row.DeletedAt == 0)
            .OrderBy(row => row.Id)
            .Select(row => new LiveIdName(row.Id, row.Name))
            .ToList());

    public Task<ProjectRecord?> FindLiveProjectByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Projects
            .Where(row => row.DeletedAt == 0 && row.Name == name)
            .OrderBy(row => row.Id)
            .FirstOrDefault());

    public Task<ProjectRecord> CreateProjectAsync(
        ProjectRecord project,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("project.create:" + project.Name);
        project.Id = NextId(Projects);
        Projects.Add(project);
        return Task.FromResult(project);
    }

    public Task UpdateProjectAsync(
        long id,
        ProjectRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("project.update:" + id);
        var row = Projects.SingleOrDefault(project => project.Id == id);
        if (row is null)
        {
            return Task.FromException(new InvalidOperationException("ent: project not found"));
        }

        row.Name = update.Name;
        row.Description = update.Description;
        row.Status = update.Status;
        row.Profiles = update.ProfilesJson;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task<ModelRecord?> FindLiveModelAsync(
        string developer,
        string modelId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Models
            .Where(row => row.DeletedAt == 0
                && row.Developer == developer
                && row.ModelId == modelId)
            .OrderBy(row => row.Id)
            .FirstOrDefault());

    public Task<ModelRecord> CreateModelAsync(
        ModelRecord model,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("model.create:" + model.ModelId);
        model.Id = NextId(Models);
        Models.Add(model);
        return Task.FromResult(model);
    }

    public Task UpdateModelAsync(
        long id,
        ModelRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("model.update:" + id);
        var row = Models.SingleOrDefault(model => model.Id == id);
        if (row is null)
        {
            return Task.FromException(new InvalidOperationException("ent: model not found"));
        }

        row.Name = update.Name;
        row.Icon = update.Icon;
        row.Group = update.Group;
        row.ModelCard = update.ModelCardJson;
        row.Settings = update.SettingsJson;
        row.Status = update.Status;
        row.Remark = update.Remark;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<LiveIdKey>> ListLiveApiKeyIdKeysAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LiveIdKey>>(ApiKeys
            .Where(row => row.DeletedAt == 0)
            .OrderBy(row => row.Id)
            .Select(row => new LiveIdKey(row.Id, row.Key))
            .ToList());

    public Task<ApiKeyRecord?> FindLiveApiKeyByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
        => Task.FromResult(ApiKeys
            .Where(row => row.DeletedAt == 0 && row.Key == key)
            .OrderBy(row => row.Id)
            .FirstOrDefault());

    public Task<ApiKeyRecord> CreateApiKeyAsync(
        ApiKeyRecord apiKey,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("api_key.create:" + apiKey.Key);
        apiKey.Id = NextId(ApiKeys);
        ApiKeys.Add(apiKey);
        return Task.FromResult(apiKey);
    }

    public Task UpdateApiKeyAsync(
        long id,
        ApiKeyRestoreUpdate update,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("api_key.update:" + id);
        var row = ApiKeys.SingleOrDefault(key => key.Id == id);
        if (row is null)
        {
            return Task.FromException(new InvalidOperationException("ent: api_key not found"));
        }

        row.Name = update.Name;
        row.Type = update.Type;
        row.Status = update.Status;
        row.Scopes = update.ScopesJson;
        row.Profiles = update.ProfilesJson;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    public Task<ChannelModelPriceRecord?> FindLiveChannelModelPriceAsync(
        long channelId,
        string modelId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Prices
            .Where(row => row.DeletedAt == 0
                && row.ChannelId == channelId
                && row.ModelId == modelId)
            .OrderBy(row => row.Id)
            .FirstOrDefault());

    public Task ArchiveActiveChannelModelPriceVersionsAsync(
        long channelModelPriceId,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("price.archive:" + channelModelPriceId);
        foreach (var version in Versions.Where(row =>
            row.ChannelModelPriceId == channelModelPriceId && row.Status == "active"))
        {
            version.Status = "archived";
            version.EffectiveEndAt = now;
        }

        return Task.CompletedTask;
    }

    public Task UpdateChannelModelPriceAsync(
        long id,
        string priceJson,
        string referenceId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("price.update:" + id);
        var row = Prices.SingleOrDefault(price => price.Id == id);
        if (row is null)
        {
            return Task.FromException(new InvalidOperationException("ent: channel_model_price not found"));
        }

        row.Price = priceJson;
        row.ReferenceId = referenceId;
        return Task.CompletedTask;
    }

    public Task<ChannelModelPriceRecord> CreateChannelModelPriceAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("price.create:" + price.ModelId);
        price.Id = NextId(Prices);
        Prices.Add(price);
        return Task.FromResult(price);
    }

    public Task CreateChannelModelPriceVersionAsync(
        ChannelModelPriceVersionRecord version,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("price.version:" + version.ChannelModelPriceId);
        version.Id = NextId(Versions);
        Versions.Add(version);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BackupUsageRequestRow>> FindUsageRequestsByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BackupUsageRequestRow>>(Requests
            .Where(row => ids.Contains(row.Id))
            .OrderBy(row => row.Id)
            .Select(ToUsageRow)
            .ToList());

    public Task<IReadOnlyList<BackupUsageRequestRow>> FindUsageRequestsByCreatedAtAsync(
        IReadOnlyList<DateTime> createdAt,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<BackupUsageRequestRow>>(Requests
            .Where(row => createdAt.Contains(row.CreatedAt))
            .OrderBy(row => row.Id)
            .Select(ToUsageRow)
            .ToList());

    public Task<RequestRecord> CreateUsageRequestAsync(
        RequestRecord request,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("request.create:" + request.ModelId);
        request.Id = NextId(Requests);
        Requests.Add(request);
        return Task.FromResult(request);
    }

    public Task<HashSet<long>> ListUsageLogRequestIdsAsync(
        IReadOnlyList<long> requestIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult(UsageLogs
            .Where(row => requestIds.Contains(row.RequestId))
            .Select(row => row.RequestId)
            .ToHashSet());

    public Task CreateUsageLogsAsync(
        IReadOnlyList<UsageLogRecord> logs,
        CancellationToken cancellationToken = default)
    {
        Calls.Add("usage_log.create:" + logs.Count);
        if (CreateUsageLogFailure is not null)
        {
            return Task.FromException(CreateUsageLogFailure);
        }

        foreach (var log in logs)
        {
            log.Id = NextId(UsageLogs);
            UsageLogs.Add(log);
        }

        return Task.CompletedTask;
    }

    private BackupUsageRequestRow ToUsageRow(RequestRecord request)
        => new(
            request,
            Projects.SingleOrDefault(row => row.Id == request.ProjectId)?.Name,
            request.ChannelId is { } channelId
                ? Channels.SingleOrDefault(row =>
                    row.Id == channelId && row.DeletedAt == 0)?.Name
                : null,
            request.ApiKeyId is { } apiKeyId
                ? ApiKeys.SingleOrDefault(row =>
                    row.Id == apiKeyId && row.DeletedAt == 0)?.Key
                : null);

    private static long NextId<T>(IReadOnlyList<T> rows)
    {
        var max = 0L;
        foreach (var row in rows)
        {
            max = Math.Max(max, row switch
            {
                ChannelRecord channel => channel.Id,
                ProjectRecord project => project.Id,
                ModelRecord model => model.Id,
                ApiKeyRecord apiKey => apiKey.Id,
                ChannelModelPriceRecord price => price.Id,
                ChannelModelPriceVersionRecord version => version.Id,
                RequestRecord request => request.Id,
                UsageLogRecord log => log.Id,
                _ => throw new InvalidOperationException(
                    "unsupported restore record " + typeof(T)),
            });
        }

        return max + 1;
    }

    private sealed class FakeTransaction(FakeBackupRestoreStore store)
        : IBackupRestoreTransaction
    {
        private bool _completed;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (store.CommitFailure is not null)
            {
                return Task.FromException(store.CommitFailure);
            }

            store.CommitCalls++;
            _completed = true;
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            store.RollbackCalls++;
            _completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                store.RollbackCalls++;
            }

            return ValueTask.CompletedTask;
        }
    }
}
