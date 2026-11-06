using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Service-level coverage of internal/server/backup at b8bb3421: the owner
/// gates, the streamed document (backup_ops.go), the restore conflict
/// strategies and ordering (restore.go) and the auto-backup rules
/// (autobackup.go). The SQL rendering of the page/restore stores is covered by
/// the AxonHub.Persistence.Tests suites.
/// </summary>
public sealed class BackupServiceTests
{
    private const string ValidPriceJson =
        """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"0.5"}}]}""";

    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly FakeBackupBatchSource _batchSource = new();
    private readonly FakeBackupRestoreStore _restoreStore = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeDataStorageStore _dataStorages = new();
    private readonly FakeDataStorageBackends _backends = new();
    private readonly SystemSettingsAdminService _settings;
    private readonly FakeManagementIdentity _identity;
    private readonly BackupService _service;

    public BackupServiceTests()
    {
        _settings = new SystemSettingsAdminService(_systems);
        _identity = new FakeManagementIdentity(new ManagementUser(9, IsOwner: true));
        _service = new BackupService(
            _batchSource,
            _restoreStore,
            _systems,
            _settings,
            _dataStorages,
            _identity,
            _backends);
    }

    // ---------------------------------------------------------- owner gates

    [Fact]
    public async Task BackupWithoutAUserFails()
    {
        _identity.User = null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.BackupAsync(AllOptionsOff()));

        Assert.Equal("user not found in context", ex.Message);
    }

    [Fact]
    public async Task BackupRequiresAnOwner()
    {
        _identity.User = new ManagementUser(9, IsOwner: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.BackupAsync(AllOptionsOff()));

        Assert.Equal("only owners can perform backup operations", ex.Message);
    }

    [Fact]
    public async Task RestoreWithoutAUserFails()
    {
        _identity.User = null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(Document("""{"version":"1.4"}"""), AllRestoreOff()));

        Assert.Equal("user not found in context", ex.Message);
    }

    [Fact]
    public async Task RestoreRequiresAnOwner()
    {
        _identity.User = new ManagementUser(9, IsOwner: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(Document("""{"version":"1.4"}"""), AllRestoreOff()));

        Assert.Equal("only owners can perform restore operations", ex.Message);
    }

    [Fact]
    public async Task RestoreRejectsAnUnsupportedVersionBeforeOpeningATransaction()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(Document("""{"version":"9.9"}"""), AllRestoreOff()));

        Assert.Equal("backup version mismatch: expected 1.4, got 9.9", ex.Message);
        Assert.Equal(0, _restoreStore.BeginCalls);
    }

    [Fact]
    public async Task RestoreAcceptsEveryPublishedBackupVersion()
    {
        foreach (var version in BackupVersions.Accepted)
        {
            var beginCalls = _restoreStore.BeginCalls;
            await _service.RestoreAsync(
                Document("""{"version":""" + JsonSerializer.Serialize(version) + "}"),
                AllRestoreOff());
            Assert.Equal(beginCalls + 1, _restoreStore.BeginCalls);
        }
    }

    // ------------------------------------------------------- backup document

    [Fact]
    public async Task BackupDocumentOrdersSectionsAndFiltersRows()
    {
        SeedFullDataset();

        var data = await _service.BackupAsync(new BackupOptions
        {
            IncludeSystemConfigs = true,
            IncludeProjects = true,
            IncludeChannels = true,
            IncludeModels = true,
            IncludeApiKeys = true,
            IncludeModelPrices = true,
            IncludeUsageStats = true,
            IncludeRequestLogs = true,
        });

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        // Fixed member order of backup.doBackupToWriter.
        Assert.Equal(
            [
                "version",
                "timestamp",
                "system_configs",
                "projects",
                "channels",
                "models",
                "channel_model_prices",
                "api_keys",
                "usage_requests",
                "usage_logs",
            ],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(BackupVersions.Current, root.GetProperty("version").GetString());

        // Only whitelisted, live system rows are streamed.
        var system = Assert.Single(root.GetProperty("system_configs").EnumerateArray());
        Assert.Equal(SystemConfigBackupKeys.Title, system.GetProperty("key").GetString());
        Assert.Equal("Axon", system.GetProperty("value").GetString());

        // Soft-deleted rows are invisible to the ent soft-delete predicate.
        var project = Assert.Single(root.GetProperty("projects").EnumerateArray());
        Assert.Equal("Default", project.GetProperty("name").GetString());
        Assert.Equal("{}", project.GetProperty("edges").GetRawText());

        var channel = Assert.Single(root.GetProperty("channels").EnumerateArray());
        Assert.Equal("gold", channel.GetProperty("name").GetString());

        var model = Assert.Single(root.GetProperty("models").EnumerateArray());
        Assert.Equal("gpt-4o", model.GetProperty("model_id").GetString());

        // The price whose channel edge is soft-deleted is skipped.
        var price = Assert.Single(root.GetProperty("channel_model_prices").EnumerateArray());
        Assert.Equal("gold", price.GetProperty("channel_name").GetString());
        Assert.Equal("gpt-4o", price.GetProperty("model_id").GetString());
        Assert.Equal("ref-1", price.GetProperty("reference_id").GetString());

        var apiKey = Assert.Single(root.GetProperty("api_keys").EnumerateArray());
        Assert.Equal("sk-1", apiKey.GetProperty("key").GetString());
        Assert.Equal("Default", apiKey.GetProperty("project_name").GetString());
        Assert.Equal(
            "Default",
            apiKey.GetProperty("edges").GetProperty("project").GetProperty("name").GetString());

        var request = Assert.Single(root.GetProperty("usage_requests").EnumerateArray());
        Assert.Equal(14, request.GetProperty("id").GetInt64());
        Assert.Equal("Default", request.GetProperty("project_name").GetString());
        Assert.Equal("gold", request.GetProperty("channel_name").GetString());
        Assert.Equal("sk-1", request.GetProperty("api_key_key").GetString());

        var log = Assert.Single(root.GetProperty("usage_logs").EnumerateArray());
        Assert.Equal(14, log.GetProperty("request_id").GetInt64());
        Assert.Equal("Default", log.GetProperty("project_name").GetString());
        Assert.Equal("gold", log.GetProperty("channel_name").GetString());
        Assert.Equal("sk-1", log.GetProperty("api_key_key").GetString());
    }

    [Fact]
    public async Task BackupOmitsApiKeyKeyWhenIncludeApiKeysIsOff()
    {
        SeedFullDataset();

        var data = await _service.BackupAsync(new BackupOptions
        {
            IncludeApiKeys = false,
            IncludeModelPrices = true,
            IncludeRequestLogs = true,
            IncludeUsageStats = true,
        });

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        Assert.False(root.TryGetProperty("api_keys", out _));

        var request = Assert.Single(root.GetProperty("usage_requests").EnumerateArray());
        Assert.False(request.TryGetProperty("api_key_key", out _));

        // Without IncludeAPIKeys the key map is never loaded, so the log loses
        // its api_key_key as well.
        var log = Assert.Single(root.GetProperty("usage_logs").EnumerateArray());
        Assert.False(log.TryGetProperty("api_key_key", out _));
    }

    [Fact]
    public async Task BackupWithoutStreamingOptionsOnlyCarriesTheVersionAndTimestamp()
    {
        SeedFullDataset();

        var data = await _service.BackupAsync(AllOptionsOff());

        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        Assert.Equal(
            ["version", "timestamp", "channels", "models"],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("channels").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("models").ValueKind);
        Assert.False(root.TryGetProperty("system_configs", out _));
        Assert.False(root.TryGetProperty("projects", out _));
        Assert.False(root.TryGetProperty("channel_model_prices", out _));
        Assert.False(root.TryGetProperty("api_keys", out _));
        Assert.False(root.TryGetProperty("usage_requests", out _));
        Assert.False(root.TryGetProperty("usage_logs", out _));
    }

    // ------------------------------------------------------- restore: configs

    [Fact]
    public async Task RestoreSystemConfigsWritesWhitelistedKeysAndIgnoresOthers()
    {
        var data = Document(
            """
            {"version":"1.4",
             "system_configs":[
               {"key":"system_title","value":"Restored"},
               {"key":"system_initialized","value":"true"},
               {"key":"system_brand_name","value":null}
             ]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeSystemConfigs = true });

        Assert.Equal("Restored", _systems.Rows.Single(row => row.Key == "system_title").Value);

        // JSON null decodes into the Go string zero value.
        Assert.Equal(string.Empty, _systems.Rows.Single(row => row.Key == "system_brand_name").Value);
        Assert.DoesNotContain(_systems.Rows, row => row.Key == "system_initialized");
        Assert.Equal(1, _restoreStore.CommitCalls);
    }

    // ------------------------------------------------------- restore: channels

    [Fact]
    public async Task RestoreChannelSkipStrategyLeavesTheExistingRowUntouched()
    {
        _restoreStore.Channels.Add(new ChannelRecord
        {
            Id = 42,
            Name = "gold",
            Status = "disabled",
            BaseUrl = "https://old",
        });

        await _service.RestoreAsync(
            ChannelDocument(
                """
                {"id":1,"name":"gold","type":"openai","status":"enabled",
                 "credentials":{"apiKey":"sk-new"}}
                """),
            AllRestoreOff() with
            {
                IncludeChannels = true,
                ChannelConflictStrategy = BackupConflictStrategy.Skip,
            });

        Assert.Empty(_restoreStore.Calls);
        Assert.Equal("disabled", Assert.Single(_restoreStore.Channels).Status);
        Assert.Equal(1, _restoreStore.CommitCalls);
    }

    [Fact]
    public async Task RestoreChannelErrorStrategyReportsTheDuplicateName()
    {
        _restoreStore.Channels.Add(new ChannelRecord { Id = 42, Name = "gold" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(
                ChannelDocument(
                    """
                    {"id":1,"name":"gold","credentials":{"apiKey":"sk-new"}}
                    """),
                AllRestoreOff() with
                {
                    IncludeChannels = true,
                    ChannelConflictStrategy = BackupConflictStrategy.Error,
                }));

        Assert.Equal("channel gold already exists", ex.Message);
        Assert.Empty(_restoreStore.Calls);

        // The deferred rollback runs because the transaction never committed.
        Assert.Equal(0, _restoreStore.CommitCalls);
        Assert.Equal(1, _restoreStore.RollbackCalls);
    }

    [Fact]
    public async Task RestoreChannelOverwriteKeepsStoredBaseUrlWhenTheBackupHasNone()
    {
        _restoreStore.Channels.Add(new ChannelRecord
        {
            Id = 42,
            Name = "gold",
            Status = "disabled",
            BaseUrl = "https://old",
            Remark = "old remark",
            Credentials = "{}",
        });

        await _service.RestoreAsync(
            ChannelDocument(
                """
                {"id":1,"name":"gold","type":"openai","status":"enabled",
                 "base_url":"","credentials":{"apiKey":"sk-new"},
                 "auto_sync_model_pattern":null,"default_test_model":null,
                 "supported_models":null,"manual_models":null,"tags":null,
                 "remark":null,"settings":null}
                """),
            AllRestoreOff() with
            {
                IncludeChannels = true,
                ChannelConflictStrategy = BackupConflictStrategy.Overwrite,
            });

        var row = Assert.Single(_restoreStore.Channels);
        Assert.Equal("https://old", row.BaseUrl);
        Assert.Equal("enabled", row.Status);
        Assert.Contains("sk-new", row.Credentials);
        Assert.Null(row.Remark);
        Assert.Equal(string.Empty, row.AutoSyncModelPattern);
        Assert.Equal(string.Empty, row.DefaultTestModel);
        Assert.Contains("channel.update:42", _restoreStore.Calls);
    }

    [Fact]
    public async Task RestoreChannelWithoutCredentialsIsSkipped()
    {
        await _service.RestoreAsync(
            ChannelDocument("""{"id":1,"name":"gold","credentials":{}}"""),
            AllRestoreOff() with { IncludeChannels = true });

        Assert.Empty(_restoreStore.Calls);
        Assert.Empty(_restoreStore.Channels);
    }

    [Fact]
    public async Task RestoreChannelCreatesRowsWithSchemaDefaults()
    {
        await _service.RestoreAsync(
            ChannelDocument(
                """
                {"id":1,"name":"gold","type":"openai","status":"enabled",
                 "credentials":{"apiKey":"sk-new"}}
                """),
            AllRestoreOff() with { IncludeChannels = true });

        var row = Assert.Single(_restoreStore.Channels);
        Assert.Equal(1, row.Id);
        Assert.Equal("gold", row.Name);
        Assert.Equal("openai", row.Type);
        Assert.Equal("enabled", row.Status);
        Assert.Null(row.BaseUrl);
        Assert.Equal("[]", row.DisabledApiKeys);
        Assert.Equal("null", row.SupportedModels);
        Assert.Equal("null", row.ManualModels);
        Assert.Equal("null", row.Tags);
        Assert.Equal("null", row.Settings);
        Assert.Equal("[]", row.Endpoints);
        Assert.NotEqual("null", row.Policies);
        Assert.Contains("sk-new", row.Credentials);
    }

    // --------------------------------------------------------- restore: models

    [Fact]
    public async Task RestoreModelStrategiesMatchTheReference()
    {
        _restoreStore.Models.Add(new ModelRecord
        {
            Id = 7,
            Developer = "openai",
            ModelId = "gpt-4o",
            Name = "Old",
            Status = "disabled",
        });

        var skip = RestoreModels(BackupConflictStrategy.Skip);
        await _service.RestoreAsync(ModelDocument(), skip);
        Assert.Empty(_restoreStore.Calls);
        Assert.Equal("Old", Assert.Single(_restoreStore.Models).Name);

        var error = RestoreModels(BackupConflictStrategy.Error);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(ModelDocument(), error));
        Assert.Equal("model gpt-4o already exists", ex.Message);

        var overwrite = RestoreModels(BackupConflictStrategy.Overwrite);
        await _service.RestoreAsync(ModelDocument(), overwrite);
        var row = Assert.Single(_restoreStore.Models);
        Assert.Equal("GPT-4o", row.Name);
        Assert.Equal("enabled", row.Status);
        Assert.Equal("openai", row.Developer);
        Assert.Contains("model.update:7", _restoreStore.Calls);
    }

    [Fact]
    public async Task RestoreModelCreatesRowsAndIgnoresNullSettings()
    {
        await _service.RestoreAsync(ModelDocument(), RestoreModels(BackupConflictStrategy.Skip));

        var row = Assert.Single(_restoreStore.Models);
        Assert.Equal("openai", row.Developer);
        Assert.Equal("gpt-4o", row.ModelId);
        Assert.Equal("chat", row.Type);
        Assert.Equal("GPT-4o", row.Name);
        Assert.Equal("enabled", row.Status);
        Assert.Equal("null", row.Settings);
        Assert.Equal("null", row.ModelCard);
    }

    // ------------------------------------------------------- restore: projects

    [Fact]
    public async Task RestoreProjectStrategiesMatchTheReference()
    {
        _restoreStore.Projects.Add(new ProjectRecord
        {
            Id = 3,
            Name = "Default",
            Description = "old",
        });

        var data = Document(
            """
            {"version":"1.4","projects":[{"id":1,"name":"Default","description":"new","status":"disabled"}]}
            """);

        var error = AllRestoreOff() with
        {
            IncludeProjects = true,
            ProjectConflictStrategy = BackupConflictStrategy.Error,
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(data, error));
        Assert.Equal("project Default already exists", ex.Message);
        Assert.Equal("old", Assert.Single(_restoreStore.Projects).Description);

        var overwrite = error with { ProjectConflictStrategy = BackupConflictStrategy.Overwrite };
        await _service.RestoreAsync(data, overwrite);
        var row = Assert.Single(_restoreStore.Projects);
        Assert.Equal("new", row.Description);
        Assert.Equal("disabled", row.Status);
        Assert.Equal("null", row.Profiles);
        Assert.Contains("project.update:3", _restoreStore.Calls);
    }

    [Fact]
    public async Task RestoreProjectCreatesMissingRows()
    {
        await _service.RestoreAsync(
            Document("""{"version":"1.4","projects":[{"id":1,"name":"Fresh"}]}"""),
            AllRestoreOff() with { IncludeProjects = true });

        var row = Assert.Single(_restoreStore.Projects);
        Assert.Equal("Fresh", row.Name);
        Assert.Equal(string.Empty, row.Description);
        Assert.Equal(string.Empty, row.Status);
        Assert.Equal("null", row.Profiles);
    }

    // ------------------------------------------------------- restore: api keys

    [Fact]
    public async Task RestoreApiKeySkipsWhenTheProjectIsMissing()
    {
        var data = Document(
            """
            {"version":"1.4",
             "api_keys":[{"id":1,"key":"sk-1","name":"key","type":"user",
                          "status":"enabled","project_name":"Missing"}]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeApiKeys = true });

        Assert.Empty(_restoreStore.ApiKeys);
        Assert.Empty(_restoreStore.Calls);
        Assert.Equal(1, _restoreStore.CommitCalls);
    }

    [Fact]
    public async Task RestoreApiKeyCreatesIntoTheDefaultProject()
    {
        _restoreStore.Projects.Add(new ProjectRecord { Id = 3, Name = "Default" });

        var data = Document(
            """
            {"version":"1.4",
             "api_keys":[{"id":1,"key":"sk-1","name":"key","type":"user",
                          "status":"enabled","project_name":""}]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeApiKeys = true });

        var row = Assert.Single(_restoreStore.ApiKeys);
        Assert.Equal("sk-1", row.Key);
        Assert.Equal("key", row.Name);
        Assert.Equal(3, row.ProjectId);
        Assert.Equal(9, row.UserId);
        Assert.Equal("[]", row.AllowedIps);
        Assert.Equal("null", row.Scopes);
        Assert.Equal("null", row.Profiles);
    }

    // -------------------------------------------------- restore: model prices

    [Fact]
    public async Task RestoreChannelModelPriceCreatesTheRowAndActiveVersion()
    {
        _restoreStore.Channels.Add(new ChannelRecord { Id = 5, Name = "gold" });

        await _service.RestoreAsync(
            PriceDocument(
                """
                {"channel_name":"gold","model_id":"gpt-4o",
                 "price":{"items":[{"itemCode":"prompt_tokens",
                                    "pricing":{"mode":"flat_fee","flatFee":"0.5"}}]},
                 "reference_id":"ref-1"}
                """),
            AllRestoreOff() with
            {
                IncludeModelPrices = true,
                ModelPriceConflictStrategy = BackupConflictStrategy.Skip,
            });

        var price = Assert.Single(_restoreStore.Prices);
        Assert.Equal(5, price.ChannelId);
        Assert.Equal("gpt-4o", price.ModelId);
        Assert.Equal("ref-1", price.ReferenceId);

        var version = Assert.Single(_restoreStore.Versions);
        Assert.Equal(price.Id, version.ChannelModelPriceId);
        Assert.Equal("active", version.Status);
        Assert.Equal("ref-1", version.ReferenceId);
        Assert.Null(version.EffectiveEndAt);

        Assert.Contains("channel.updated_at:5", _restoreStore.Calls);
    }

    [Fact]
    public async Task RestoreChannelModelPriceRejectsAnEmptyReferenceId()
    {
        _restoreStore.Channels.Add(new ChannelRecord { Id = 5, Name = "gold" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(
                PriceDocument(
                    """
                    {"channel_name":"gold","model_id":"gpt-4o",
                     "price":{"items":[]},"reference_id":""}
                    """),
                AllRestoreOff() with
                {
                    IncludeModelPrices = true,
                    ModelPriceConflictStrategy = BackupConflictStrategy.Skip,
                }));

        Assert.Equal(
            "channel model price reference ID is empty: channel=gold model_id=gpt-4o",
            ex.Message);
    }

    [Fact]
    public async Task RestoreChannelModelPriceWrapsValidationFailures()
    {
        _restoreStore.Channels.Add(new ChannelRecord { Id = 5, Name = "gold" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RestoreAsync(
                PriceDocument(
                    """
                    {"channel_name":"gold","model_id":"gpt-4o",
                     "price":{"items":[{"itemCode":"prompt_tokens",
                                        "pricing":{"mode":"flat_fee"}}]},
                     "reference_id":"ref-1"}
                    """),
                AllRestoreOff() with
                {
                    IncludeModelPrices = true,
                    ModelPriceConflictStrategy = BackupConflictStrategy.Skip,
                }));

        Assert.Equal(
            "invalid channel model price: channel=gold model_id=gpt-4o: "
            + "items[0]: pricing: flatFee is required",
            ex.Message);
    }

    [Fact]
    public async Task RestoreChannelModelPriceOverwriteArchivesTheOldVersion()
    {
        _restoreStore.Channels.Add(new ChannelRecord { Id = 5, Name = "gold" });
        var existing = new ChannelModelPriceRecord
        {
            Id = 11,
            ChannelId = 5,
            ModelId = "gpt-4o",
            Price = ValidPriceJson,
            ReferenceId = "ref-1",
        };
        _restoreStore.Prices.Add(existing);
        _restoreStore.Versions.Add(new ChannelModelPriceVersionRecord
        {
            Id = 12,
            ChannelId = 5,
            ModelId = "gpt-4o",
            ChannelModelPriceId = 11,
            Price = ValidPriceJson,
            Status = "active",
            ReferenceId = "ref-1",
        });

        await _service.RestoreAsync(
            PriceDocument(
                """
                {"channel_name":"gold","model_id":"gpt-4o",
                 "price":{"items":[{"itemCode":"prompt_tokens",
                                    "pricing":{"mode":"flat_fee","flatFee":"0.9"}}]},
                 "reference_id":"ref-2"}
                """),
            AllRestoreOff() with
            {
                IncludeModelPrices = true,
                ModelPriceConflictStrategy = BackupConflictStrategy.Overwrite,
            });

        Assert.Single(_restoreStore.Prices);
        Assert.Equal("ref-2", _restoreStore.Prices[0].ReferenceId);
        Assert.Contains("price.archive:11", _restoreStore.Calls);
        Assert.Contains("price.update:11", _restoreStore.Calls);
        Assert.Contains("price.version:11", _restoreStore.Calls);

        var archived = _restoreStore.Versions.Single(version => version.Id == 12);
        Assert.Equal("archived", archived.Status);
        Assert.NotNull(archived.EffectiveEndAt);
        Assert.Equal(2, _restoreStore.Versions.Count);
    }

    // ---------------------------------------------------- restore: usage data

    [Fact]
    public async Task RestoreUsageRequestDeduplicatesByFingerprint()
    {
        SeedUsageRelations();
        _restoreStore.Requests.Add(new RequestRecord
        {
            Id = 100,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ProjectId = 3,
            ChannelId = 5,
            ApiKeyId = 7,
            Source = "api",
            ModelId = "gpt-4o",
            Format = "openai/chat_completions",
            Status = "completed",
            ClientIp = "127.0.0.1",
        });

        var data = Document(
            """
            {"version":"1.4",
             "usage_requests":[{"id":55,"created_at":"2026-01-02T03:04:05Z",
                                "updated_at":"2026-02-03T04:05:06Z","project_id":3,
                                "source":"api","model_id":"gpt-4o",
                                "format":"openai/chat_completions","channel_id":5,
                                "status":"completed","stream":false,
                                "client_ip":"127.0.0.1",
                                "project_name":"Default","channel_name":"gold",
                                "api_key_key":"sk-1"}]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeRequestLogs = true });

        Assert.Single(_restoreStore.Requests);
        Assert.DoesNotContain(
            _restoreStore.Calls,
            call => call.StartsWith("request.create", StringComparison.Ordinal));
        Assert.Equal(1, _restoreStore.CommitCalls);
    }

    [Fact]
    public async Task RestoreUsageRequestRecognizesTheSameId()
    {
        SeedUsageRelations();
        _restoreStore.Requests.Add(new RequestRecord
        {
            Id = 55,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ProjectId = 3,
            ChannelId = 5,
            ApiKeyId = 7,
            Source = "api",
            ModelId = "gpt-4o",
            Format = "openai/chat_completions",
            Status = "completed",
            ClientIp = "127.0.0.1",
        });

        var data = Document(
            """
            {"version":"1.4",
             "usage_requests":[{"id":55,"created_at":"2026-01-02T03:04:05Z",
                                "project_id":3,"source":"api","model_id":"gpt-4o",
                                "format":"openai/chat_completions","channel_id":5,
                                "status":"completed","client_ip":"127.0.0.1"}]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeRequestLogs = true });

        Assert.Single(_restoreStore.Requests);
        Assert.DoesNotContain(
            _restoreStore.Calls,
            call => call.StartsWith("request.create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreUsageLogCreatesAMissingRequestShell()
    {
        SeedUsageRelations();

        var data = Document(
            """
            {"version":"1.4",
             "usage_logs":[{"id":1,"created_at":"2026-01-02T03:04:05Z",
                            "request_id":55,"project_id":3,"channel_id":5,
                            "model_id":"gpt-4o","prompt_tokens":10,
                            "completion_tokens":20,"total_tokens":30,
                            "source":"api","format":"openai/chat_completions",
                            "api_key_key":"sk-1"}]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeUsageStats = true });

        var shell = Assert.Single(_restoreStore.Requests);
        Assert.Equal(3, shell.ProjectId);
        Assert.Equal(5, shell.ChannelId);
        Assert.Equal(7, shell.ApiKeyId);
        Assert.Equal("api", shell.Source);
        Assert.Equal("gpt-4o", shell.ModelId);
        Assert.Equal("openai/chat_completions", shell.Format);
        Assert.Equal("{}", shell.RequestBody);
        Assert.Equal("completed", shell.Status);
        Assert.False(shell.Stream);
        Assert.Equal(string.Empty, shell.ClientIp);

        var log = Assert.Single(_restoreStore.UsageLogs);
        Assert.Equal(shell.Id, log.RequestId);
        Assert.Equal(3, log.ProjectId);
        Assert.Equal(5, log.ChannelId);
        Assert.Equal(7, log.ApiKeyId);
        Assert.Equal(10, log.PromptTokens);
        Assert.Equal(20, log.CompletionTokens);
        Assert.Equal(30, log.TotalTokens);
        Assert.Equal("null", log.CostItems);
        Assert.Null(log.CostPriceReferenceId);
    }

    [Fact]
    public async Task RestoreUsageLogSkipsRequestsWhoseProjectIsMissing()
    {
        var data = Document(
            """
            {"version":"1.4",
             "usage_logs":[{"id":1,"created_at":"2026-01-02T03:04:05Z",
                            "request_id":55,"project_id":99,"model_id":"gpt-4o"}]}
            """);

        await _service.RestoreAsync(
            data,
            AllRestoreOff() with { IncludeUsageStats = true });

        Assert.Empty(_restoreStore.Requests);
        Assert.Empty(_restoreStore.UsageLogs);
    }

    // ---------------------------------------------------------- auto backup

    [Theory]
    [InlineData(BackupFrequencies.Daily, 2026, 11, 6, true)]
    [InlineData(BackupFrequencies.Weekly, 2026, 11, 8, true)]
    [InlineData(BackupFrequencies.Weekly, 2026, 11, 9, false)]
    [InlineData(BackupFrequencies.Monthly, 2026, 12, 1, true)]
    [InlineData(BackupFrequencies.Monthly, 2026, 12, 2, false)]
    [InlineData("unknown", 2026, 11, 6, true)]
    public void ShouldRunBackupMatchesTheFrequencyRules(
        string frequency,
        int year,
        int month,
        int day,
        bool expected)
    {
        var settings = AutoBackupSettingsCodec.Default with { Frequency = frequency };

        Assert.Equal(
            expected,
            BackupService.ShouldRunBackup(new DateTime(year, month, day), settings));
    }

    [Fact]
    public async Task RunBackupNowRequiresAConfiguredDataStorage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunBackupNowAsync());

        Assert.Equal("data storage not configured for backup", ex.Message);
        Assert.Null((await _settings.GetAutoBackupSettingsAsync()).LastBackupAt);
    }

    [Fact]
    public async Task RunBackupNowRecordsTheLastRunOnSuccess()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 2 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 2;
            storage.Name = "db";
            storage.Type = "database";
        });

        await _service.RunBackupNowAsync();

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.NotNull(stored.LastBackupAt);
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    [Fact]
    public async Task RunBackupNowRecordsAndRethrowsStorageFailures()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 3 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 3;
            storage.Name = "s3";
            storage.Type = "s3";
        });
        _backends.Failure = new InvalidOperationException(
            "failed to create s3 filesystem: failed to load AWS config: no region");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunBackupNowAsync());

        // SaveDataFromReader reaches objectStoreFor before GetFileSystem, and
        // objectStoreFor wraps client construction failures with
        // "failed to create s3 object store" (objectstore.go:75-78).
        Assert.Equal(
            "failed to write backup file: failed to create s3 object store: "
            + "failed to load AWS config: no region",
            ex.Message);

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.NotNull(stored.LastBackupAt);
        Assert.Equal(
            "failed to write backup file: failed to create s3 object store: "
            + "failed to load AWS config: no region",
            stored.LastBackupError);
    }

    [Fact]
    public async Task RunBackupNowReportsAMissingDataStorage()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 9 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunBackupNowAsync());

        Assert.Equal(
            "failed to get data storage: failed to get data storage by ID 9: "
            + "ent: data storage not found",
            ex.Message);
    }

    [Fact]
    public async Task TriggerAutoBackupIgnoresSettingsFailures()
    {
        _systems.GetFailure = new InvalidOperationException("boom");

        await _service.TriggerAutoBackupAsync();

        // The getter fails while the hook is installed, so clear it before
        // inspecting the (never written) settings row.
        _systems.GetFailure = null;
        Assert.Null((await _settings.GetAutoBackupSettingsAsync()).LastBackupAt);
    }

    [Fact]
    public async Task TriggerAutoBackupSkipsWhenDisabled()
    {
        await _settings.SetAutoBackupSettingsAsync(AutoBackupSettingsCodec.Default);

        await _service.TriggerAutoBackupAsync();

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.Null(stored.LastBackupAt);
    }

    [Fact]
    public async Task TriggerAutoBackupRecordsTheRunError()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with
            {
                Enabled = true,
                Frequency = BackupFrequencies.Daily,
                DataStorageId = 0,
            });

        await _service.TriggerAutoBackupAsync();

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.NotNull(stored.LastBackupAt);
        Assert.Equal(
            "failed to get data storage: failed to get data storage by ID 0: "
            + "ent: data storage not found",
            stored.LastBackupError);
    }

    [Fact]
    public async Task TriggerAutoBackupRunsWhenTheScheduleMatches()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with
            {
                Enabled = true,
                Frequency = BackupFrequencies.Daily,
                DataStorageId = 2,
            });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 2;
            storage.Name = "db";
            storage.Type = "database";
        });

        await _service.TriggerAutoBackupAsync();

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.NotNull(stored.LastBackupAt);
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    // ------------------------------------------------- non-database storage

    [Fact]
    public async Task RunBackupNowStreamsTheDocumentToANonDatabaseStorage()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with
            {
                DataStorageId = 4,
                IncludeSystemConfigs = true,
            });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 4;
            storage.Name = "disk";
            storage.Type = "fs";
            storage.Settings = "{}";
        });

        await _service.RunBackupNowAsync();

        var key = Assert.Single(_backends.Backend.StreamWrites);
        Assert.StartsWith("axonhub-backup-", key);
        Assert.EndsWith(".json", key);

        using var document = JsonDocument.Parse(_backends.Backend.Objects[key]);
        Assert.Equal(BackupVersions.Current, document.RootElement.GetProperty("version").GetString());

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.NotNull(stored.LastBackupAt);
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    [Fact]
    public async Task RunBackupNowWrapsFileSystemConstructionFailures()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 4 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 4;
            storage.Name = "disk";
            storage.Type = "fs";
        });
        _backends.Failure = new InvalidOperationException(
            "directory not configured for fs storage");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunBackupNowAsync());

        Assert.Equal(
            "failed to write backup file: failed to get file system: "
            + "directory not configured for fs storage",
            ex.Message);
    }

    [Fact]
    public async Task RunBackupNowReportsUnknownStorageTypesWithoutTheFileSystemWrapper()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 4 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 4;
            storage.Name = "mystery";
            storage.Type = "mystery";
        });
        _backends.Failure = new InvalidOperationException(
            "unsupported storage type: mystery");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunBackupNowAsync());

        Assert.Equal(
            "failed to write backup file: unsupported storage type: mystery",
            ex.Message);
    }

    [Fact]
    public async Task RunBackupNowWrapsStreamingSaveFailures()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 4 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 4;
            storage.Name = "disk";
            storage.Type = "fs";
        });
        _backends.Backend.WriteStreamFailure = new InvalidOperationException(
            "failed to write file: disk full, key: axonhub-backup-x.json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RunBackupNowAsync());

        Assert.Equal(
            "failed to write backup file: failed to write file: disk full, "
            + "key: axonhub-backup-x.json",
            ex.Message);
    }

    [Fact]
    public async Task RetentionDeletesOnlyOldBackupFiles()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 5, RetentionDays = 30 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 5;
            storage.Name = "disk";
            storage.Type = "fs";
        });

        var now = DateTimeOffset.UtcNow;
        _backends.Backend.Add("axonhub-backup-old.json", [], now.AddDays(-31));
        _backends.Backend.Add("axonhub-backup-recent.json", [], now.AddDays(-29));
        _backends.Backend.Add("notes.json", [], now.AddDays(-60));
        _backends.Backend.Add("axonhub-backup-old.txt", [], now.AddDays(-60));

        await _service.RunBackupNowAsync();

        Assert.Equal(["axonhub-backup-old.json"], _backends.Backend.Deletes);
        Assert.True(_backends.Backend.Objects.ContainsKey("axonhub-backup-recent.json"));
        Assert.True(_backends.Backend.Objects.ContainsKey("notes.json"));
    }

    [Fact]
    public async Task RetentionCleanupFailuresAreSwallowed()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 5, RetentionDays = 30 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 5;
            storage.Name = "disk";
            storage.Type = "fs";
        });
        _backends.Backend.ListFailure = new InvalidOperationException(
            "failed to list backups: boom");

        await _service.RunBackupNowAsync();

        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    [Fact]
    public async Task RetentionRunsForDatabaseStorageAndSwallowsTheFilesystemError()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 2, RetentionDays = 30 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 2;
            storage.Name = "db";
            storage.Type = "database";
        });
        _backends.Failure = new InvalidOperationException(
            "database storage does not support file system operations");

        await _service.RunBackupNowAsync();

        // cleanupOldBackups runs for every storage type and the caller only
        // logs the GetFileSystem failure (autobackup.go:146-150).
        Assert.Empty(_backends.Backend.Deletes);
        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    [Fact]
    public async Task RetentionKeepsFilesWhoseDeleteFails()
    {
        await _settings.SetAutoBackupSettingsAsync(
            AutoBackupSettingsCodec.Default with { DataStorageId = 5, RetentionDays = 30 });
        _dataStorages.Seed(storage =>
        {
            storage.Id = 5;
            storage.Name = "disk";
            storage.Type = "fs";
        });

        _backends.Backend.Add(
            "axonhub-backup-old.json",
            [],
            DateTimeOffset.UtcNow.AddDays(-31));
        _backends.Backend.DeleteFailureFor = key => key == "axonhub-backup-old.json"
            ? new InvalidOperationException("failed to remove file: denied")
            : null;

        await _service.RunBackupNowAsync();

        Assert.True(_backends.Backend.Objects.ContainsKey("axonhub-backup-old.json"));
        var stored = await _settings.GetAutoBackupSettingsAsync();
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    // --------------------------------------------------------------- helpers

    private static BackupOptions AllOptionsOff() => new();

    private static RestoreOptions AllRestoreOff() => new()
    {
        ProjectConflictStrategy = BackupConflictStrategy.Skip,
        ChannelConflictStrategy = BackupConflictStrategy.Skip,
        ModelConflictStrategy = BackupConflictStrategy.Skip,
        ModelPriceConflictStrategy = BackupConflictStrategy.Skip,
        ApiKeyConflictStrategy = BackupConflictStrategy.Skip,
    };

    private static RestoreOptions RestoreModels(string strategy)
        => AllRestoreOff() with
        {
            IncludeModels = true,
            ModelConflictStrategy = strategy,
        };

    private static byte[] Document(string json) => Encoding.UTF8.GetBytes(json);

    private static byte[] ChannelDocument(string channel)
        => Document("""{"version":"1.4","channels":[""" + channel + "]}");

    private static byte[] ModelDocument()
        => Document(
            """
            {"version":"1.4",
             "models":[{"id":1,"developer":"openai","model_id":"gpt-4o",
                        "type":"chat","name":"GPT-4o","status":"enabled"}]}
            """);

    private static byte[] PriceDocument(string price)
        => Document("""{"version":"1.4","channel_model_prices":[""" + price + "]}");

    private void SeedUsageRelations()
    {
        _restoreStore.Projects.Add(new ProjectRecord { Id = 3, Name = "Default" });
        _restoreStore.Channels.Add(new ChannelRecord { Id = 5, Name = "gold" });
        _restoreStore.ApiKeys.Add(new ApiKeyRecord { Id = 7, Key = "sk-1" });
    }

    private void SeedFullDataset()
    {
        _batchSource.Systems.Add(new SystemRecord
        {
            Id = 1,
            Key = SystemConfigBackupKeys.Title,
            Value = "Axon",
        });
        _batchSource.Systems.Add(new SystemRecord
        {
            Id = 2,
            Key = "system_initialized",
            Value = "true",
        });
        _batchSource.Systems.Add(new SystemRecord
        {
            Id = 3,
            Key = SystemConfigBackupKeys.BrandName,
            Value = "deleted",
            DeletedAt = 10,
        });

        _batchSource.Projects.Add(new ProjectRecord
        {
            Id = 1,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "Default",
            Status = "active",
        });
        _batchSource.Projects.Add(new ProjectRecord
        {
            Id = 2,
            Name = "gone",
            DeletedAt = 10,
        });

        _batchSource.Channels.Add(new ChannelRecord
        {
            Id = 7,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "gold",
            Type = "openai",
            Status = "enabled",
        });
        _batchSource.Channels.Add(new ChannelRecord
        {
            Id = 8,
            Name = "gone",
            DeletedAt = 10,
        });

        _batchSource.Models.Add(new ModelRecord
        {
            Id = 9,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Developer = "openai",
            ModelId = "gpt-4o",
            Type = "chat",
            Name = "GPT-4o",
            Status = "enabled",
        });

        _batchSource.Prices.Add(new ChannelModelPriceRecord
        {
            Id = 11,
            ChannelId = 7,
            ModelId = "gpt-4o",
            Price = ValidPriceJson,
            ReferenceId = "ref-1",
        });
        _batchSource.Prices.Add(new ChannelModelPriceRecord
        {
            Id = 12,
            ChannelId = 8,
            ModelId = "gone",
            Price = ValidPriceJson,
            ReferenceId = "ref-1",
        });

        _batchSource.ApiKeys.Add(new ApiKeyRecord
        {
            Id = 13,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Key = "sk-1",
            Name = "key",
            Type = "user",
            Status = "enabled",
            ProjectId = 1,
        });

        _batchSource.Requests.Add(new RequestRecord
        {
            Id = 14,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ProjectId = 1,
            ChannelId = 7,
            ApiKeyId = 13,
            Source = "api",
            ModelId = "gpt-4o",
            Format = "openai/chat_completions",
            Status = "completed",
            ClientIp = "127.0.0.1",
        });
        _batchSource.UsageLogs.Add(new UsageLogRecord
        {
            Id = 15,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            RequestId = 14,
            ProjectId = 1,
            ChannelId = 7,
            ApiKeyId = 13,
            ModelId = "gpt-4o",
            PromptTokens = 1,
            CompletionTokens = 2,
            TotalTokens = 3,
            Source = "api",
            Format = "openai/chat_completions",
        });
    }
}
