using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the backup GraphQL surface the frontend sends from
/// frontend/src/features/system (backup-settings.tsx and system.ts): the
/// autoBackupSettings query and its pointer-field defaults, the
/// updateAutoBackupSettings merge, the owner gates of
/// internal/server/gql/backup.resolvers.go, the golden backup document and the
/// multipart Upload restore path (HotChocolate Upload scalar, C198).
/// </summary>
public sealed class AdminBackupEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string AutoBackupSettingsQuery =
        """
        query AutoBackupSettings {
          autoBackupSettings {
            includeSystemConfigs
            enabled
            frequency
            dataStorageID
            includeChannels
            includeModels
            includeAPIKeys
            includeModelPrices
            includeUsageStats
            includeRequestLogs
            retentionDays
            lastBackupAt
            lastBackupError
          }
        }
        """;

    private const string RestoreMutation =
        """
        mutation Restore($file: Upload!, $input: RestoreOptionsInput!) {
          restore(file: $file, input: $input) {
            success
            message
          }
        }
        """;

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeBackupBatchSource _batchSource = new();
    private readonly FakeBackupRestoreStore _restoreStore = new();
    private readonly FakeDataStorageStore _storages = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IBackupBatchSource>(_batchSource);
        builder.Services.AddSingleton<IBackupRestoreStore>(_restoreStore);
        builder.Services.AddSingleton<IDataStorageStore>(_storages);
        builder.Services.AddScoped<ISystemSettingsAdminService, SystemSettingsAdminService>();
        builder.Services.AddScoped<BackupService>();

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ------------------------------------------------------- autoBackupSettings

    [Fact]
    public async Task AutoBackupSettingsReturnsTheReferenceDefaults()
    {
        var data = await QueryDataAsync(AutoBackupSettingsQuery);
        var settings = data.GetProperty("autoBackupSettings");

        // defaultAutoBackupSettings (system_default.go:118-129).
        Assert.False(settings.GetProperty("includeSystemConfigs").GetBoolean());
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal("daily", settings.GetProperty("frequency").GetString());
        Assert.Equal(0, settings.GetProperty("dataStorageID").GetInt32());
        Assert.True(settings.GetProperty("includeChannels").GetBoolean());
        Assert.True(settings.GetProperty("includeModels").GetBoolean());
        Assert.False(settings.GetProperty("includeAPIKeys").GetBoolean());
        Assert.True(settings.GetProperty("includeModelPrices").GetBoolean());
        Assert.False(settings.GetProperty("includeUsageStats").GetBoolean());
        Assert.False(settings.GetProperty("includeRequestLogs").GetBoolean());
        Assert.Equal(30, settings.GetProperty("retentionDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("lastBackupAt").ValueKind);
        Assert.Equal(string.Empty, settings.GetProperty("lastBackupError").GetString());
    }

    [Fact]
    public async Task AutoBackupSettingsReadsTheStoredDocument()
    {
        _gate.Systems.Add(
            AutoBackupSettingsCodec.Key,
            """
            {"enabled":true,"frequency":"weekly","data_storage_id":4,
             "include_system_configs":true,"include_channels":false,
             "include_models":false,"include_api_keys":true,
             "include_model_prices":false,"include_usage_stats":true,
             "include_request_logs":true,"retention_days":7,
             "last_backup_at":"2026-03-04T05:06:07Z","last_backup_error":"boom"}
            """);

        var data = await QueryDataAsync(AutoBackupSettingsQuery);
        var settings = data.GetProperty("autoBackupSettings");

        Assert.True(settings.GetProperty("includeSystemConfigs").GetBoolean());
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal("weekly", settings.GetProperty("frequency").GetString());
        Assert.Equal(4, settings.GetProperty("dataStorageID").GetInt32());
        Assert.False(settings.GetProperty("includeChannels").GetBoolean());
        Assert.False(settings.GetProperty("includeModels").GetBoolean());
        Assert.True(settings.GetProperty("includeAPIKeys").GetBoolean());
        Assert.False(settings.GetProperty("includeModelPrices").GetBoolean());
        Assert.True(settings.GetProperty("includeUsageStats").GetBoolean());
        Assert.True(settings.GetProperty("includeRequestLogs").GetBoolean());
        Assert.Equal(7, settings.GetProperty("retentionDays").GetInt32());
        Assert.Equal("2026-03-04T05:06:07Z", settings.GetProperty("lastBackupAt").GetString());
        Assert.Equal("boom", settings.GetProperty("lastBackupError").GetString());
    }

    [Fact]
    public async Task UnknownStoredFrequencyFailsTheFieldInsteadOfEmittingRawText()
    {
        // C198: gqlgen marshals the stored Go string as-is; HotChocolate only
        // accepts declared enum values.
        _gate.Systems.Add(AutoBackupSettingsCodec.Key, """{"frequency":"hourly"}""");

        var errors = await QueryErrorsAsync(
            "query { autoBackupSettings { frequency } }");

        Assert.Contains(
            "Enum \"BackupFrequency\" cannot represent value: \"hourly\"",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateAutoBackupSettingsRejectsAnUndeclaredFrequency()
    {
        // C198: graphql.UnmarshalString coerces any string into
        // biz.BackupFrequency and the resolver stores it; HotChocolate
        // validates the input enum against the SDL before invoking the field.
        var errors = await QueryErrorsAsync(
            """
            mutation Update($input: UpdateAutoBackupSettingsInput!) {
              updateAutoBackupSettings(input: $input)
            }
            """,
            new { input = new { frequency = "hourly" } });

        var message = errors[0].GetProperty("message").GetString();
        Assert.Contains("hourly", message);
        Assert.Contains("BackupFrequency", message);
        Assert.DoesNotContain(
            _gate.Systems.Rows,
            row => row.Key == AutoBackupSettingsCodec.Key);
    }

    // ------------------------------------------------ updateAutoBackupSettings

    [Fact]
    public async Task UpdateAutoBackupSettingsMergesIntoTheStoredDocument()
    {
        var data = await QueryDataAsync(
            """
            mutation Update($input: UpdateAutoBackupSettingsInput!) {
              updateAutoBackupSettings(input: $input)
            }
            """,
            new
            {
                input = new
                {
                    enabled = true,
                    frequency = "weekly",
                    dataStorageID = 3,
                    retentionDays = 7,
                },
            });

        Assert.True(data.GetProperty("updateAutoBackupSettings").GetBoolean());

        var stored = await new SystemSettingsAdminService(_gate.Systems)
            .GetAutoBackupSettingsAsync();

        Assert.True(stored.Enabled);
        Assert.Equal(BackupFrequencies.Weekly, stored.Frequency);
        Assert.Equal(3, stored.DataStorageId);
        Assert.Equal(7, stored.RetentionDays);

        // Fields absent from the input keep the previous (default) values.
        Assert.False(stored.IncludeSystemConfigs);
        Assert.True(stored.IncludeChannels);
        Assert.True(stored.IncludeModels);
        Assert.False(stored.IncludeApiKeys);
        Assert.True(stored.IncludeModelPrices);
        Assert.False(stored.IncludeUsageStats);
        Assert.False(stored.IncludeRequestLogs);
        Assert.Null(stored.LastBackupAt);
        Assert.Equal(string.Empty, stored.LastBackupError);
    }

    [Fact]
    public async Task UpdateAutoBackupSettingsRequiresAnOwner()
    {
        var token = AddNonOwnerToken();

        var errors = await QueryErrorsAsync(
            """
            mutation Update($input: UpdateAutoBackupSettingsInput!) {
              updateAutoBackupSettings(input: $input)
            }
            """,
            new { input = new { enabled = true } },
            token);

        Assert.Equal(
            "permission denied: owner access required",
            errors[0].GetProperty("message").GetString());
        Assert.DoesNotContain(
            _gate.Systems.Rows,
            row => row.Key == AutoBackupSettingsCodec.Key);
    }

    // ------------------------------------------------------------------ backup

    [Fact]
    public async Task BackupStreamsTheGoldenDocument()
    {
        _batchSource.Channels.Add(new ChannelRecord
        {
            Id = 5,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Type = "openai",
            BaseUrl = null,
            Name = "gold",
            Status = "enabled",
        });
        _batchSource.Models.Add(new ModelRecord
        {
            Id = 7,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Developer = "openai",
            ModelId = "gpt-4o",
            Type = "chat",
            Name = "GPT-4o",
            Status = "enabled",
        });

        var data = await QueryDataAsync(
            """
            mutation Backup($input: BackupOptionsInput!) {
              backup(input: $input) {
                success
                data
                message
              }
            }
            """,
            new
            {
                input = new
                {
                    includeSystemConfigs = false,
                    includeChannels = true,
                    includeModelPrices = true,
                    includeModels = true,
                    includeAPIKeys = false,
                    includeUsageStats = true,
                    includeRequestLogs = false,
                },
            });

        var payload = data.GetProperty("backup");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Backup completed successfully", payload.GetProperty("message").GetString());

        using var document = JsonDocument.Parse(payload.GetProperty("data").GetString()!);
        var root = document.RootElement;

        // Fixed root member order; disabled optional arrays are omitted, while
        // channels/models stay present because they are pointers in Go.
        Assert.Equal(
            ["version", "timestamp", "channels", "models"],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.Equal(BackupVersions.Current, root.GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("timestamp").GetString()));

        var channel = Assert.Single(root.GetProperty("channels").EnumerateArray());
        Assert.Equal(5, channel.GetProperty("id").GetInt64());
        Assert.Equal("2026-01-02T03:04:05Z", channel.GetProperty("created_at").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", channel.GetProperty("updated_at").GetString());
        Assert.Equal("openai", channel.GetProperty("type").GetString());
        Assert.Equal("gold", channel.GetProperty("name").GetString());
        Assert.Equal("enabled", channel.GetProperty("status").GetString());

        // channels.base_url is optional in the DDL: NULL is omitted (omitempty).
        Assert.False(channel.TryGetProperty("base_url", out _));
        Assert.False(channel.TryGetProperty("default_test_model", out _));
        Assert.Equal("{}", channel.GetProperty("edges").GetRawText());

        var model = Assert.Single(root.GetProperty("models").EnumerateArray());
        Assert.Equal(7, model.GetProperty("id").GetInt64());
        Assert.Equal("openai", model.GetProperty("developer").GetString());
        Assert.Equal("gpt-4o", model.GetProperty("model_id").GetString());
        Assert.Equal("chat", model.GetProperty("type").GetString());
        Assert.Equal("GPT-4o", model.GetProperty("name").GetString());
        Assert.Equal("enabled", model.GetProperty("status").GetString());

        // includeModelPrices is true but no price row exists; omitempty drops
        // the empty array. API keys / usage are switched off and omitted too.
        Assert.False(root.TryGetProperty("channel_model_prices", out _));
        Assert.False(root.TryGetProperty("api_keys", out _));
        Assert.False(root.TryGetProperty("usage_logs", out _));
        Assert.False(root.TryGetProperty("usage_requests", out _));
        Assert.False(root.TryGetProperty("system_configs", out _));
        Assert.False(root.TryGetProperty("projects", out _));
    }

    [Fact]
    public async Task DisabledChannelAndModelSectionsAreEmittedAsNull()
    {
        var data = await QueryDataAsync(
            """
            mutation Backup($input: BackupOptionsInput!) {
              backup(input: $input) { data }
            }
            """,
            new
            {
                input = new
                {
                    includeSystemConfigs = false,
                    includeChannels = false,
                    includeModelPrices = false,
                    includeModels = false,
                    includeAPIKeys = false,
                    includeUsageStats = false,
                    includeRequestLogs = false,
                },
            });

        using var document = JsonDocument.Parse(
            data.GetProperty("backup").GetProperty("data").GetString()!);
        var root = document.RootElement;

        Assert.Equal(JsonValueKind.Null, root.GetProperty("channels").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("models").ValueKind);
        Assert.Equal(
            ["version", "timestamp", "channels", "models"],
            root.EnumerateObject().Select(static property => property.Name).ToArray());
    }

    [Fact]
    public async Task BackupRequiresAnOwner()
    {
        var token = AddNonOwnerToken();

        var errors = await QueryErrorsAsync(
            """
            mutation Backup($input: BackupOptionsInput!) {
              backup(input: $input) { success }
            }
            """,
            new
            {
                input = new
                {
                    includeSystemConfigs = false,
                    includeChannels = false,
                    includeModelPrices = false,
                    includeModels = false,
                    includeAPIKeys = false,
                    includeUsageStats = false,
                    includeRequestLogs = false,
                },
            },
            token);

        Assert.Equal(
            "only owners can perform backup operations",
            errors[0].GetProperty("message").GetString());
    }

    // ----------------------------------------------------------------- restore

    [Fact]
    public async Task RestoreReadsTheUploadAndCommitsTheTransaction()
    {
        var root = await ExecuteUploadAsync(
            RestoreMutation,
            RestoreVariables(),
            Encoding.UTF8.GetBytes("""{"version":"1.4","channels":[],"models":[]}"""));

        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        var payload = root.GetProperty("data").GetProperty("restore");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Restore completed successfully", payload.GetProperty("message").GetString());

        Assert.Equal(1, _restoreStore.BeginCalls);
        Assert.Equal(1, _restoreStore.CommitCalls);
        Assert.Equal(0, _restoreStore.RollbackCalls);
    }

    [Fact]
    public async Task RestoreRejectsAnUnsupportedVersionBeforeOpeningATransaction()
    {
        var root = await ExecuteUploadAsync(
            RestoreMutation,
            RestoreVariables(),
            Encoding.UTF8.GetBytes("""{"version":"9.9"}"""));

        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        Assert.Equal(
            "backup version mismatch: expected 1.4, got 9.9",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(0, _restoreStore.BeginCalls);
    }

    [Fact]
    public async Task RestoreRequiresAnOwner()
    {
        var root = await ExecuteUploadAsync(
            RestoreMutation,
            RestoreVariables(),
            Encoding.UTF8.GetBytes("""{"version":"1.4"}"""),
            AddNonOwnerToken());

        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        Assert.Equal(
            "only owners can perform restore operations",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(0, _restoreStore.BeginCalls);
    }

    // ------------------------------------------------------- triggerAutoBackup

    [Fact]
    public async Task TriggerAutoBackupReturnsSuccess()
    {
        var data = await QueryDataAsync(
            "mutation { triggerAutoBackup { success message } }");

        var payload = data.GetProperty("triggerAutoBackup");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("Backup completed successfully", payload.GetProperty("message").GetString());
    }

    [Fact]
    public async Task TriggerAutoBackupRequiresAnOwner()
    {
        var errors = await QueryErrorsAsync(
            "mutation { triggerAutoBackup { success } }",
            token: AddNonOwnerToken());

        Assert.Equal(
            "permission denied: owner access required",
            errors[0].GetProperty("message").GetString());
    }

    // --------------------------------------------------------------- helpers

    private string AddNonOwnerToken()
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = "member@example.com";
            user.Status = "activated";
            user.IsOwner = false;
        });

        return _gate.IssueJwt(member);
    }

    private static object RestoreVariables() => new
    {
        file = (object?)null,
        input = new
        {
            includeSystemConfigs = false,
            includeChannels = false,
            includeModelPrices = false,
            includeModels = false,
            includeAPIKeys = false,
            includeUsageStats = false,
            includeRequestLogs = false,
            channelConflictStrategy = "skip",
            modelConflictStrategy = "skip",
            modelPriceConflictStrategy = "skip",
            apiKeyConflictStrategy = "skip",
        },
    };

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token ?? _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// graphql-multipart-request-spec upload: the operations document carries a
    /// null for the Upload variable, map links part "0" to it, and the file part
    /// carries the backup bytes.
    /// </summary>
    private async Task<JsonElement> ExecuteUploadAsync(
        string query,
        object variables,
        byte[] file,
        string? token = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(
            new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
            "operations");
        form.Add(
            new StringContent("""{"0":["variables.file"]}""", Encoding.UTF8, "application/json"),
            "map");

        var fileContent = new ByteArrayContent(file);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(fileContent, "0", "backup.json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = form,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            token ?? _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
