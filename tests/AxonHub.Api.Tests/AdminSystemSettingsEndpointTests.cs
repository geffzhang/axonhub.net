using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the system-settings GraphQL surface over /admin/graphql: the four
/// queries, the three update mutations, the coded validation envelope and the
/// <c>Any</c> scalar binding used by when-condition values.
/// </summary>
public sealed class AdminSystemSettingsEndpointTests : IAsyncLifetime
{
    private readonly FakeSystemStore _store = new();
    private readonly FakeDataStorageStore _dataStorages = new();
    private readonly FakeProviderQuotaStatusStore _quotaStatuses = new();
    private readonly FakeChannelStore _quotaChannels = new();
    private readonly ProviderQuotaSettingsCache _quotaCache = new();
    private readonly RecordingAutoBackupRescheduleSignal _reschedule = new();
    private readonly RecordingVideoStorageRescheduleSignal _videoReschedule = new();
    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddScoped<ISystemSettingsAdminService>(
            _ => new SystemSettingsAdminService(_store, _dataStorages));
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddSingleton<IProviderQuotaStatusStore>(_quotaStatuses);
        builder.Services.AddSingleton(_quotaCache);
        builder.Services.AddScoped(
            _ => new ProviderQuotaCollectionSettingsService(_store, _quotaCache));
        builder.Services.AddScoped<IProviderQuotaCollectionSettingsService>(
            static services => services
                .GetRequiredService<ProviderQuotaCollectionSettingsService>());
        builder.Services.AddScoped(
            _ => new SystemQuotaRoutingSettingsService(_store));
        builder.Services.AddScoped<IQuotaRoutingSettingsProvider>(
            static services => services.GetRequiredService<SystemQuotaRoutingSettingsService>());
        builder.Services.AddSingleton<IChannelStore>(_quotaChannels);
        builder.Services.AddSingleton<IProviderQuotaCheckerRegistry, ProviderQuotaCheckerRegistry>();
        builder.Services.AddSingleton<ProviderQuotaService>();
        builder.Services.AddSingleton<IAutoBackupRescheduleSignal>(_reschedule);
        builder.Services.AddSingleton<IVideoStorageRescheduleSignal>(_videoReschedule);

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

    [Fact]
    public async Task QueriesReturnTheReferenceDefaultsAndStoredValues()
    {
        _store.Add(SystemSettingsAdminService.InitializedKey, "TRUE");

        var data = await QueryAsync(
            """
            {
              systemStatus { isInitialized }
              retryPolicy {
                maxChannelRetries maxSingleChannelRetries retryDelayMs
                streamFirstEventTimeoutSeconds nonStreamResponseTimeoutSeconds
                loadBalancerStrategy traceStickyMode enabled
                autoDisableChannel { enabled rules { statusCodes keywordPatterns times action } }
                emptyResponseDetection
                upstreamErrorPolicy { mode customMessage }
              }
              systemModelSettings {
                fallbackToChannelsOnModelNotFound queryAllChannelModels
                defaultModelAPIIncludeAll autoReasoningEffort
                modelBlacklistRegex hideUnroutableModelsInList
                developerSettings { developer associations { type } }
              }
              securitySettings { blockedIPs showRequestLogIPBanIcon }
            }
            """);

        Assert.True(data.GetProperty("systemStatus").GetProperty("isInitialized").GetBoolean());

        var retry = data.GetProperty("retryPolicy");
        Assert.Equal(3, retry.GetProperty("maxChannelRetries").GetInt32());
        Assert.Equal(2, retry.GetProperty("maxSingleChannelRetries").GetInt32());
        Assert.Equal(1000, retry.GetProperty("retryDelayMs").GetInt32());
        Assert.Equal("adaptive", retry.GetProperty("loadBalancerStrategy").GetString());
        Assert.Equal("PREFER_PREVIOUS_CHANNEL", retry.GetProperty("traceStickyMode").GetString());
        Assert.True(retry.GetProperty("enabled").GetBoolean());
        Assert.False(retry.GetProperty("autoDisableChannel").GetProperty("enabled").GetBoolean());
        Assert.Empty(retry.GetProperty("autoDisableChannel").GetProperty("rules").EnumerateArray());
        Assert.Equal("passthrough", retry.GetProperty("upstreamErrorPolicy").GetProperty("mode").GetString());
        Assert.Equal("", retry.GetProperty("upstreamErrorPolicy").GetProperty("customMessage").GetString());

        var modelSettings = data.GetProperty("systemModelSettings");
        Assert.True(modelSettings.GetProperty("fallbackToChannelsOnModelNotFound").GetBoolean());
        Assert.True(modelSettings.GetProperty("queryAllChannelModels").GetBoolean());
        Assert.False(modelSettings.GetProperty("defaultModelAPIIncludeAll").GetBoolean());
        Assert.Equal("", modelSettings.GetProperty("modelBlacklistRegex").GetString());
        Assert.Empty(modelSettings.GetProperty("developerSettings").EnumerateArray());

        var security = data.GetProperty("securitySettings");
        Assert.Empty(security.GetProperty("blockedIPs").EnumerateArray());
        Assert.True(security.GetProperty("showRequestLogIPBanIcon").GetBoolean());
    }

    [Fact]
    public async Task RetryPolicyKeepsNilRuleListsNullOnTheWire()
    {
        _store.Add(
            RetryPolicyCodec.RetryPolicyKey,
            """
            {"auto_disable_channel":{"enabled":true,"rules":[
              {"times":2,"action":"permanent_disable"},
              {"statusCodes":[429],"times":1,"action":"permanent_disable","disableDurationMinutes":null}
            ]}}
            """);

        var data = await QueryAsync(
            """
            {
              retryPolicy {
                autoDisableChannel { rules { statusCodes keywordPatterns disableDurationMinutes } }
              }
            }
            """);

        var rules = data.GetProperty("retryPolicy")
            .GetProperty("autoDisableChannel")
            .GetProperty("rules")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, rules.Length);
        Assert.Equal(JsonValueKind.Null, rules[0].GetProperty("statusCodes").ValueKind);
        Assert.Equal(JsonValueKind.Null, rules[0].GetProperty("keywordPatterns").ValueKind);
        Assert.Equal(JsonValueKind.Null, rules[0].GetProperty("disableDurationMinutes").ValueKind);
        Assert.Equal(429, rules[1].GetProperty("statusCodes")[0].GetInt32());
    }

    [Fact]
    public async Task UpdateRetryPolicyNormalizesBeforeWriting()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateRetryPolicy(input: {
                enabled: true
                loadBalancerStrategy: "weighted"
                traceStickyMode: DISABLED
                autoDisableChannel: {
                  enabled: true
                  rules: [{
                    statusCodes: [429, 401, 429]
                    keywordPatterns: [" quota ", "", "quota"]
                    times: 2
                    action: temporary_disable
                    disableDurationMinutes: 5
                  }]
                }
              })
            }
            """);

        Assert.True(data.GetProperty("updateRetryPolicy").GetBoolean());

        var policy = RetryPolicyCodec.Deserialize(Assert.Single(_store.Rows).Value);
        Assert.Equal(RetryPolicy.FailoverStrategy, policy.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyDisabled, policy.TraceStickyMode);
        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([401, 429], rule.StatusCodes);
        Assert.Equal(["quota"], rule.KeywordPatterns);
    }

    [Fact]
    public async Task UpdateRetryPolicyReportsCodedValidationErrors()
    {
        var response = await PostAsync(
            """
            mutation {
              updateRetryPolicy(input: {
                autoDisableChannel: {
                  enabled: true
                  rules: [{ times: 0, action: permanent_disable }]
                }
              })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "API key rule 1 consecutive error count must be at least 1",
            error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal(CodedException.ValidationFailed, extensions.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, extensions.GetProperty("resource").ValueKind);
        Assert.Equal(JsonValueKind.Null, extensions.GetProperty("field").ValueKind);
        Assert.Equal(JsonValueKind.Null, extensions.GetProperty("value").ValueKind);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task ResolverFailuresSurfaceTheReferenceErrorMessage()
    {
        _store.SetFailure = new InvalidOperationException("write rejected");

        var response = await PostAsync(
            """
            mutation {
              updateRetryPolicy(input: { enabled: true })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update retry policy: failed to create system setting: write rejected",
            error.GetProperty("message").GetString());
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task UpdateSystemModelSettingsPreservesStoredDevelopersWhenOmitted()
    {
        _store.Add(
            ModelSettingsCodec.ModelSettingsKey,
            """
            {"query_all_channel_models":true,"developer_settings":[
              {"developer":"openai","associations":[{"type":"channel_model","channelModel":{"channelId":7,"modelId":""}}]}
            ]}
            """);

        var data = await QueryAsync(
            """
            mutation {
              updateSystemModelSettings(input: {
                queryAllChannelModels: false
                autoReasoningEffort: true
              })
            }
            """);

        Assert.True(data.GetProperty("updateSystemModelSettings").GetBoolean());

        var settings = ModelSettingsCodec.Deserialize(Assert.Single(_store.Rows).Value);
        Assert.False(settings.QueryAllChannelModels);
        Assert.True(settings.AutoReasoningEffort);
        var developer = Assert.Single(settings.DeveloperSettings);
        Assert.NotNull(developer);
        Assert.Equal("openai", developer.Developer);
        Assert.Single(developer.Associations);
    }

    [Fact]
    public async Task WhenConditionValuesBindThroughTheAnyScalar()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateSystemModelSettings(input: {
                developerSettings: [{
                  developer: "openai"
                  associations: [{
                    type: "channel_model"
                    channelModel: { channelId: 7, modelId: "pinned" }
                    when: {
                      enabled: true
                      condition: {
                        type: group
                        logic: "and"
                        conditions: [
                          { type: condition, field: "stream", operator: "eq", value: false },
                          { type: condition, field: "prompt_tokens", operator: "gte", value: 200 }
                        ]
                      }
                    }
                  }]
                }]
              })
            }
            """);

        Assert.True(data.GetProperty("updateSystemModelSettings").GetBoolean());

        var stored = Assert.Single(_store.Rows).Value;
        Assert.Contains("\"value\":false", stored, StringComparison.Ordinal);
        Assert.Contains("\"value\":200", stored, StringComparison.Ordinal);
        Assert.Contains("\"modelId\":\"\"", stored, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateSecuritySettingsMergesWithTheStoredSettings()
    {
        _store.Add(
            SecuritySettingsCodec.SecuritySettingsKey,
            """{"blocked_ips":["1.1.1.1"],"show_request_log_ip_ban_icon":false}""");

        var data = await QueryAsync(
            """
            mutation {
              updateSecuritySettings(input: {
                blockedIPs: [" 2.2.2.2 ", "2.2.2.2"]
              })
            }
            """);

        Assert.True(data.GetProperty("updateSecuritySettings").GetBoolean());

        Assert.Equal(
            """{"blocked_ips":["2.2.2.2"],"show_request_log_ip_ban_icon":false}""",
            Assert.Single(_store.Rows).Value);
    }

    [Fact]
    public async Task TheSchemaExposesTheReferenceTypeNames()
    {
        var data = await QueryAsync(
            """
            {
              retry: __type(name: "RetryPolicy") { kind }
              rule: __type(name: "APIKeyAutoDisableRule") { kind }
              action: __type(name: "APIKeyAutoDisableAction") { enumValues { name } }
              filter: __type(name: "FilterCondition") { fields { name } }
              any: __type(name: "Any") { kind }
            }
            """);

        Assert.Equal("OBJECT", data.GetProperty("retry").GetProperty("kind").GetString());
        Assert.Equal("OBJECT", data.GetProperty("rule").GetProperty("kind").GetString());
        Assert.Equal(
            [
                "temporary_disable",
                "disable_until_cron",
                "permanent_disable",
                "permanent_disable_delete",
            ],
            data.GetProperty("action").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()!)
                .ToArray());
        Assert.Equal(
            ["type", "logic", "conditions", "field", "operator", "value"],
            data.GetProperty("filter").GetProperty("fields").EnumerateArray()
                .Select(static field => field.GetProperty("name").GetString()!)
                .ToArray());
        Assert.Equal("SCALAR", data.GetProperty("any").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ProviderQuotaCollectionSettingsQueryFollowsTheSupportedProviderOrder()
    {
        _store.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{"minimax":false}}""");

        var data = await QueryAsync(
            """
            {
              providerQuotaCollectionSettings {
                enabled
                providers { provider enabled }
              }
            }
            """);

        var settings = data.GetProperty("providerQuotaCollectionSettings");
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        var providers = settings.GetProperty("providers").EnumerateArray().ToArray();
        Assert.Equal(
            ProviderQuotaCollectionSettingsService.SupportedProviderQuotaTypes.ToArray(),
            providers.Select(static provider => provider.GetProperty("provider").GetString()!)
                .ToArray());
        Assert.False(ProviderEntry(providers, "minimax"));
        Assert.True(ProviderEntry(providers, "codex"));
    }

    [Fact]
    public async Task UpdateProviderQuotaCollectionSettingsMergesIntoTheStoredRow()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateProviderQuotaCollectionSettings(input: {
                enabled: false
                providers: [
                  { provider: "minimax", enabled: false }
                  { provider: "codex", enabled: true }
                ]
              })
            }
            """);

        Assert.True(data.GetProperty("updateProviderQuotaCollectionSettings").GetBoolean());

        using var document = JsonDocument.Parse(Assert.Single(_store.Rows).Value);
        var root = document.RootElement;
        Assert.False(root.GetProperty("enabled").GetBoolean());
        var providerMap = root.GetProperty("providers");
        Assert.Equal(
            ProviderQuotaCollectionSettingsService.SupportedProviderQuotaTypes
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray(),
            providerMap.EnumerateObject().Select(static property => property.Name).ToArray());
        Assert.False(providerMap.GetProperty("minimax").GetBoolean());
        Assert.True(providerMap.GetProperty("codex").GetBoolean());
    }

    [Fact]
    public async Task UpdateProviderQuotaCollectionSettingsSurfacesTheReferenceError()
    {
        _store.SetFailure = new InvalidOperationException("boom");

        var response = await PostAsync(
            """
            mutation {
              updateProviderQuotaCollectionSettings(input: { enabled: true })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update provider quota collection settings: "
            + "failed to create system setting: boom",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task QuotaRoutingSettingsUpdatePersistsTheDomainMode()
    {
        var initial = await QueryAsync("{ quotaRoutingSettings { defaultMode } }");
        Assert.Equal(
            "REMOVE_ON_EXHAUSTED",
            initial.GetProperty("quotaRoutingSettings").GetProperty("defaultMode").GetString());

        var data = await QueryAsync(
            """
            mutation {
              updateQuotaRoutingSettings(input: { defaultMode: BACKPRESSURE })
            }
            """);
        Assert.True(data.GetProperty("updateQuotaRoutingSettings").GetBoolean());

        var updated = await QueryAsync("{ quotaRoutingSettings { defaultMode } }");
        Assert.Equal(
            "BACKPRESSURE",
            updated.GetProperty("quotaRoutingSettings").GetProperty("defaultMode").GetString());

        using var document = JsonDocument.Parse(Assert.Single(_store.Rows).Value);
        Assert.Equal(
            "backpressure",
            document.RootElement.GetProperty("defaultMode").GetString());
    }

    [Fact]
    public async Task CheckProviderQuotasRunsTheCollectionPass()
    {
        var data = await QueryAsync("mutation { checkProviderQuotas }");

        Assert.True(data.GetProperty("checkProviderQuotas").GetBoolean());
        // The collection pass reached the channel query even though nothing is
        // configured to check.
        Assert.Equal(1, _quotaChannels.ListEnabledByTypesCalls);
    }

    [Fact]
    public async Task ResetChannelQuotaNowResolvesTheGidAndSurfacesTheServiceError()
    {
        _quotaChannels.Add(new ChannelRecord
        {
            Id = 42,
            Type = "claudecode",
            Name = "claudecode-channel",
            Status = "enabled",
            Credentials = """{"oauth":{"access_token":"token"}}""",
        });

        var response = await PostAsync(
            """
            mutation {
              resetChannelQuotaNow(channelID: "gid://axonhub/Channel/42")
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "provider quota reset is not supported for provider \"claudecode\"",
            error.GetProperty("message").GetString());
    }

    // ------------------------------------------------- general / channel / brand

    [Fact]
    public async Task GeneralSettingsQueryReturnsDefaultsAndStoredValues()
    {
        var defaults = await QueryAsync(
            "{ systemGeneralSettings { currencyCode timezone } }");
        var settings = defaults.GetProperty("systemGeneralSettings");
        Assert.Equal("USD", settings.GetProperty("currencyCode").GetString());
        Assert.Equal("UTC", settings.GetProperty("timezone").GetString());

        _store.Add(SystemConfigBackupKeys.GeneralSettings, """{"currency_code":"CNY"}""");

        var stored = await QueryAsync(
            "{ systemGeneralSettings { currencyCode timezone } }");
        settings = stored.GetProperty("systemGeneralSettings");
        // The missing timezone decodes to "" and is normalized back to UTC.
        Assert.Equal("CNY", settings.GetProperty("currencyCode").GetString());
        Assert.Equal("UTC", settings.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task GeneralSettingsQueryNormalizesEmptyStrings()
    {
        _store.Add(
            SystemConfigBackupKeys.GeneralSettings,
            """{"currency_code":"","timezone":""}""");

        var data = await QueryAsync(
            "{ systemGeneralSettings { currencyCode timezone } }");

        var settings = data.GetProperty("systemGeneralSettings");
        Assert.Equal("USD", settings.GetProperty("currencyCode").GetString());
        Assert.Equal("UTC", settings.GetProperty("timezone").GetString());
    }

    [Fact]
    public async Task GeneralSettingsQuerySurfacesLookupAndUnmarshalErrors()
    {
        _store.GetFailuresByKey[SystemConfigBackupKeys.GeneralSettings] =
            new InvalidOperationException("boom");

        var response = await PostAsync("{ systemGeneralSettings { currencyCode } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get general settings: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.Rows.Clear();
        _store.Add(SystemConfigBackupKeys.GeneralSettings, "not json");

        response = await PostAsync("{ systemGeneralSettings { currencyCode } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to unmarshal general settings: ",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateSystemGeneralSettingsWritesTheDocumentAndReschedulesBackup()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateSystemGeneralSettings(input: {
                currencyCode: "CNY"
                timezone: "Asia/Shanghai"
              })
            }
            """);

        Assert.True(data.GetProperty("updateSystemGeneralSettings").GetBoolean());
        var row = Assert.Single(_store.Rows);
        Assert.Equal(
            """{"currency_code":"CNY","timezone":"Asia/Shanghai"}""",
            row.Value);
        Assert.Equal(1, _reschedule.Calls);

        // Absent input strings decode to the Go zero value and are stored as "".
        await QueryAsync(
            """
            mutation {
              updateSystemGeneralSettings(input: { timezone: "UTC" })
            }
            """);

        Assert.Equal("""{"currency_code":"","timezone":"UTC"}""", row.Value);
        Assert.Equal(2, _reschedule.Calls);
    }

    [Fact]
    public async Task ChannelSettingsQueryReturnsTheReferenceDefaults()
    {
        var data = await QueryAsync(
            """
            {
              systemChannelSettings {
                probe { enabled frequency }
                autoSync { frequency }
                testSystemPrompt
                testUserPrompt
              }
            }
            """);

        var settings = data.GetProperty("systemChannelSettings");
        Assert.True(settings.GetProperty("probe").GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "FIVE_MINUTES",
            settings.GetProperty("probe").GetProperty("frequency").GetString());
        Assert.Equal(
            "ONE_HOUR",
            settings.GetProperty("autoSync").GetProperty("frequency").GetString());
        Assert.Equal(
            "You are a helpful assistant.",
            settings.GetProperty("testSystemPrompt").GetString());
        Assert.Equal(
            "Hello world, I'm AxonHub.\nPlease tell me who you are?",
            settings.GetProperty("testUserPrompt").GetString());
    }

    [Fact]
    public async Task ChannelSettingsQueryNormalizesStoredValues()
    {
        _store.Add(
            SystemConfigBackupKeys.ChannelSettings,
            """
            {"probe":{"enabled":true,"frequency":"7x"},
             "auto_sync":{"frequency":"2h"},
             "test_system_prompt":"  ",
             "test_user_prompt":"keep me"}
            """);

        var data = await QueryAsync(
            """
            {
              systemChannelSettings {
                probe { enabled frequency }
                autoSync { frequency }
                testSystemPrompt
                testUserPrompt
              }
            }
            """);

        var settings = data.GetProperty("systemChannelSettings");
        // An unknown stored probe interval renders as ONE_MINUTE and is not
        // rewritten by normalization.
        Assert.Equal(
            "ONE_MINUTE",
            settings.GetProperty("probe").GetProperty("frequency").GetString());
        // AutoSyncFrequency.UnmarshalJSON collapses every unknown value to 1h.
        Assert.Equal(
            "ONE_HOUR",
            settings.GetProperty("autoSync").GetProperty("frequency").GetString());
        Assert.Equal(
            "You are a helpful assistant.",
            settings.GetProperty("testSystemPrompt").GetString());
        Assert.Equal("keep me", settings.GetProperty("testUserPrompt").GetString());
    }

    [Fact]
    public async Task ChannelSettingsQuerySurfacesUnmarshalErrors()
    {
        _store.Add(SystemConfigBackupKeys.ChannelSettings, "not json");

        var response = await PostAsync(
            "{ systemChannelSettings { testSystemPrompt } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to get channel setting: failed to unmarshal channel setting: ",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsCreatesTheDefaultDocumentWithThePatch()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateSystemChannelSettings(input: {
                probe: { enabled: false, frequency: THIRTY_MINUTES }
                testSystemPrompt: "Custom"
              })
            }
            """);

        Assert.True(data.GetProperty("updateSystemChannelSettings").GetBoolean());
        var row = Assert.Single(_store.Rows);
        Assert.Equal(
            """{"probe":{"enabled":false,"frequency":"30m"},"auto_sync":{"frequency":"1h"},"test_system_prompt":"Custom","test_user_prompt":"Hello world, I'm AxonHub.\nPlease tell me who you are?"}""",
            row.Value);
        Assert.True(row.CreatedAt > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(row.UpdatedAt > new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsAppliesAPartialPatch()
    {
        var row = _store.Add(
            SystemConfigBackupKeys.ChannelSettings,
            """{"probe":{"enabled":false,"frequency":"30m"},"auto_sync":{"frequency":"1h"},"test_system_prompt":"sys","test_user_prompt":"usr"}""");

        var data = await QueryAsync(
            """
            mutation {
              updateSystemChannelSettings(input: {
                autoSync: { frequency: SIX_HOURS }
              })
            }
            """);

        Assert.True(data.GetProperty("updateSystemChannelSettings").GetBoolean());
        Assert.Equal(
            """{"probe":{"enabled":false,"frequency":"30m"},"auto_sync":{"frequency":"6h"},"test_system_prompt":"sys","test_user_prompt":"usr"}""",
            row.Value);
        Assert.Equal(1, _store.UpdateValueIfUnchangedCalls);
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsRestoresDefaultsForBlankPrompts()
    {
        var row = _store.Add(
            SystemConfigBackupKeys.ChannelSettings,
            """{"probe":{"enabled":false,"frequency":"30m"},"auto_sync":{"frequency":"1h"},"test_system_prompt":"sys","test_user_prompt":"usr"}""");

        await QueryAsync(
            """
            mutation {
              updateSystemChannelSettings(input: { testSystemPrompt: "   " })
            }
            """);

        Assert.Equal(
            """{"probe":{"enabled":false,"frequency":"30m"},"auto_sync":{"frequency":"1h"},"test_system_prompt":"You are a helpful assistant.","test_user_prompt":"usr"}""",
            row.Value);
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsValidatesPromptRuneLimits()
    {
        var oversized = new string('a', 4097);

        var response = await PostAsync(
            $$"""
            mutation {
              updateSystemChannelSettings(input: { testSystemPrompt: "{{oversized}}" })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update channel setting: "
            + "test system prompt must not exceed 4096 characters",
            error.GetProperty("message").GetString());
        // Validation runs before the insert, so nothing is persisted.
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsRejectsUnknownEnumValues()
    {
        // Both implementations reject an unknown ProbeFrequency before the
        // resolver runs; only the validation message differs (C232).
        var response = await PostAsync(
            """
            mutation {
              updateSystemChannelSettings(input: {
                probe: { enabled: true, frequency: NOPE }
              })
            }
            """);

        Assert.True(response.TryGetProperty("errors", out var errors));
        Assert.NotEmpty(errors.EnumerateArray());
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsRetriesTheCompareAndSet()
    {
        var row = _store.Add(
            SystemConfigBackupKeys.ChannelSettings,
            DefaultChannelSettingsJson);
        _store.UpdateValueIfUnchangedOverride = static (_, _) => 0;

        var response = await PostAsync(
            """
            mutation {
              updateSystemChannelSettings(input: {
                autoSync: { frequency: SIX_HOURS }
              })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update channel setting: "
            + "failed to update channel setting after 5 concurrent attempts",
            error.GetProperty("message").GetString());
        Assert.Equal(5, _store.UpdateValueIfUnchangedCalls);
        Assert.Equal(DefaultChannelSettingsJson, row.Value);
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsReappliesThePatchAfterAConcurrentWrite()
    {
        const string competitor =
            """{"probe":{"enabled":false,"frequency":"1m"},"auto_sync":{"frequency":"1h"},"test_system_prompt":"theirs","test_user_prompt":"theirs"}""";
        var row = _store.Add(
            SystemConfigBackupKeys.ChannelSettings,
            DefaultChannelSettingsJson);
        var first = true;
        _store.UpdateValueIfUnchangedOverride = (current, value) =>
        {
            if (first)
            {
                // The competing writer commits first and invalidates the CAS.
                first = false;
                current.Value = competitor;
                return 0;
            }

            current.Value = value;
            current.UpdatedAt = DateTime.UtcNow;
            return 1;
        };

        await QueryAsync(
            """
            mutation {
              updateSystemChannelSettings(input: {
                autoSync: { frequency: ONE_DAY }
              })
            }
            """);

        // The retry re-read the competitor's document, then applied the patch
        // on top of it.
        Assert.Equal(
            """{"probe":{"enabled":false,"frequency":"1m"},"auto_sync":{"frequency":"1d"},"test_system_prompt":"theirs","test_user_prompt":"theirs"}""",
            row.Value);
        Assert.Equal(2, _store.UpdateValueIfUnchangedCalls);
    }

    [Fact]
    public async Task UpdateSystemChannelSettingsSkipsTheWriteWhenNothingChanges()
    {
        var row = _store.Add(
            SystemConfigBackupKeys.ChannelSettings,
            DefaultChannelSettingsJson);

        var data = await QueryAsync(
            "mutation { updateSystemChannelSettings(input: {}) }");

        Assert.True(data.GetProperty("updateSystemChannelSettings").GetBoolean());
        Assert.Equal(0, _store.UpdateValueIfUnchangedCalls);
        Assert.Equal(DefaultChannelSettingsJson, row.Value);
    }

    [Fact]
    public async Task BrandSettingsQueryReadsEachKeyAndDefaultsToEmpty()
    {
        var defaults = await QueryAsync(
            "{ brandSettings { brandName brandLogo title } }");
        var settings = defaults.GetProperty("brandSettings");
        Assert.Equal(string.Empty, settings.GetProperty("brandName").GetString());
        Assert.Equal(string.Empty, settings.GetProperty("brandLogo").GetString());
        Assert.Equal(string.Empty, settings.GetProperty("title").GetString());

        _store.Add(SystemConfigBackupKeys.BrandName, "AxonHub");
        _store.Add(SystemConfigBackupKeys.Title, "AxonHub Admin");

        var stored = await QueryAsync(
            "{ brandSettings { brandName brandLogo title } }");
        settings = stored.GetProperty("brandSettings");
        Assert.Equal("AxonHub", settings.GetProperty("brandName").GetString());
        Assert.Equal(string.Empty, settings.GetProperty("brandLogo").GetString());
        Assert.Equal("AxonHub Admin", settings.GetProperty("title").GetString());
    }

    [Fact]
    public async Task BrandSettingsQuerySurfacesEachReadPrefix()
    {
        _store.GetFailuresByKey[SystemConfigBackupKeys.BrandLogo] =
            new InvalidOperationException("boom");

        var response = await PostAsync("{ brandSettings { brandName } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get brand logo: failed to get brand logo: boom",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateBrandSettingsWritesOnlyProvidedFields()
    {
        var name = _store.Add(SystemConfigBackupKeys.BrandName, "AxonHub");
        var logo = _store.Add(SystemConfigBackupKeys.BrandLogo, "data:image/png;base64,AA==");

        var data = await QueryAsync(
            """
            mutation {
              updateBrandSettings(input: { title: "AxonHub Admin" })
            }
            """);

        Assert.True(data.GetProperty("updateBrandSettings").GetBoolean());
        Assert.Equal("AxonHub", name.Value);
        Assert.Equal("data:image/png;base64,AA==", logo.Value);
        var title = Assert.Single(
            _store.Rows, row => row.Key == SystemConfigBackupKeys.Title);
        Assert.Equal("AxonHub Admin", title.Value);
    }

    [Fact]
    public async Task UpdateBrandSettingsSurfacesTheWritePrefix()
    {
        _store.SetFailuresByKey[SystemConfigBackupKeys.BrandName] =
            new InvalidOperationException("boom");

        var response = await PostAsync(
            """
            mutation {
              updateBrandSettings(input: { brandName: "AxonHub" })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update brand name setting: failed to create system setting: boom",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PassThroughFlagsReadDefaultsAndOnlyTheExactTrueString()
    {
        var defaults = await QueryAsync(
            """
            {
              userAgentPassThroughSettings { enabled }
              passThroughSettings { enabled }
              usageCostInjectionSettings { enabled }
            }
            """);
        Assert.False(defaults.GetProperty("userAgentPassThroughSettings")
            .GetProperty("enabled").GetBoolean());
        Assert.False(defaults.GetProperty("passThroughSettings")
            .GetProperty("enabled").GetBoolean());
        Assert.False(defaults.GetProperty("usageCostInjectionSettings")
            .GetProperty("enabled").GetBoolean());

        _store.Add(SystemConfigBackupKeys.UserAgentPassThrough, "true");
        _store.Add(SystemConfigBackupKeys.PassThrough, "TRUE");
        _store.Add(SystemConfigBackupKeys.InjectUsageCost, " true ");

        var stored = await QueryAsync(
            """
            {
              userAgentPassThroughSettings { enabled }
              passThroughSettings { enabled }
              usageCostInjectionSettings { enabled }
            }
            """);
        Assert.True(stored.GetProperty("userAgentPassThroughSettings")
            .GetProperty("enabled").GetBoolean());
        // The reference compares the stored string with "true" exactly.
        Assert.False(stored.GetProperty("passThroughSettings")
            .GetProperty("enabled").GetBoolean());
        Assert.False(stored.GetProperty("usageCostInjectionSettings")
            .GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task PassThroughFlagMutationsStoreTheExactStrings()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateUserAgentPassThroughSettings(input: { enabled: true })
              updatePassThroughSettings(input: { enabled: true })
              updateUsageCostInjectionSettings(input: { enabled: true })
            }
            """);

        Assert.True(data.GetProperty("updateUserAgentPassThroughSettings").GetBoolean());
        Assert.True(data.GetProperty("updatePassThroughSettings").GetBoolean());
        Assert.True(data.GetProperty("updateUsageCostInjectionSettings").GetBoolean());
        Assert.Equal(
            "true",
            Assert.Single(_store.Rows,
                row => row.Key == SystemConfigBackupKeys.UserAgentPassThrough).Value);
        Assert.Equal(
            "true",
            Assert.Single(_store.Rows,
                row => row.Key == SystemConfigBackupKeys.PassThrough).Value);
        Assert.Equal(
            "true",
            Assert.Single(_store.Rows,
                row => row.Key == SystemConfigBackupKeys.InjectUsageCost).Value);

        await QueryAsync(
            """
            mutation {
              updatePassThroughSettings(input: { enabled: false })
            }
            """);

        Assert.Equal(
            "false",
            Assert.Single(_store.Rows,
                row => row.Key == SystemConfigBackupKeys.PassThrough).Value);
    }

    [Fact]
    public async Task PassThroughFlagErrorsCarryTheReferencePrefixes()
    {
        _store.GetFailuresByKey[SystemConfigBackupKeys.InjectUsageCost] =
            new InvalidOperationException("boom");

        var response = await PostAsync("{ usageCostInjectionSettings { enabled } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get usage cost injection settings: "
            + "failed to get inject usage cost: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.SetFailuresByKey[SystemConfigBackupKeys.UserAgentPassThrough] =
            new InvalidOperationException("boom");

        response = await PostAsync(
            """
            mutation {
              updateUserAgentPassThroughSettings(input: { enabled: false })
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update user-agent pass-through settings: "
            + "failed to create system setting: boom",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task VideoStorageSettingsQueryReturnsTheReferenceDefaults()
    {
        var data = await QueryAsync(
            "{ videoStorageSettings { enabled dataStorageID scanIntervalMinutes scanLimit } }");

        var settings = data.GetProperty("videoStorageSettings");
        Assert.False(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal(0, settings.GetProperty("dataStorageID").GetInt32());
        Assert.Equal(1, settings.GetProperty("scanIntervalMinutes").GetInt32());
        Assert.Equal(50, settings.GetProperty("scanLimit").GetInt32());
    }

    [Fact]
    public async Task VideoStorageSettingsQueryNormalizesNonPositiveScanValues()
    {
        _store.Add(
            SystemSettingsAdminService.VideoStorageSettingsKey,
            """{"enabled":true,"data_storage_id":7,"scan_interval_minutes":0,"scan_limit":-3}""");

        var data = await QueryAsync(
            "{ videoStorageSettings { enabled dataStorageID scanIntervalMinutes scanLimit } }");

        var settings = data.GetProperty("videoStorageSettings");
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, settings.GetProperty("dataStorageID").GetInt32());
        Assert.Equal(1, settings.GetProperty("scanIntervalMinutes").GetInt32());
        Assert.Equal(50, settings.GetProperty("scanLimit").GetInt32());
    }

    [Fact]
    public async Task VideoStorageSettingsQuerySurfacesLookupAndUnmarshalErrors()
    {
        _store.GetFailuresByKey[SystemSettingsAdminService.VideoStorageSettingsKey] =
            new InvalidOperationException("boom");

        var response = await PostAsync("{ videoStorageSettings { enabled } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get video storage settings: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.Rows.Clear();
        _store.Add(SystemSettingsAdminService.VideoStorageSettingsKey, "not json");

        response = await PostAsync("{ videoStorageSettings { enabled } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to unmarshal video storage settings: ",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateVideoStorageSettingsNormalizesAndWritesTheDocument()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateVideoStorageSettings(input: {
                enabled: false
                dataStorageID: 0
                scanIntervalMinutes: 0
                scanLimit: 0
              })
            }
            """);

        Assert.True(data.GetProperty("updateVideoStorageSettings").GetBoolean());
        var row = Assert.Single(_store.Rows);
        Assert.Equal(
            """{"enabled":false,"data_storage_id":0,"scan_interval_minutes":1,"scan_limit":50}""",
            row.Value);
        Assert.Equal(1, _videoReschedule.Calls);
    }

    [Fact]
    public async Task UpdateVideoStorageSettingsValidatesTheTargetStorage()
    {
        var response = await PostAsync(
            """
            mutation {
              updateVideoStorageSettings(input: { enabled: true })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update video storage settings: "
            + "data_storage_id is required when video storage is enabled",
            error.GetProperty("message").GetString());

        response = await PostAsync(
            """
            mutation {
              updateVideoStorageSettings(input: { enabled: true, dataStorageID: 99 })
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update video storage settings: "
            + "failed to get data storage: ent: data storage not found",
            error.GetProperty("message").GetString());

        _dataStorages.Seed(row =>
        {
            row.Id = 1;
            row.Primary = true;
            row.Type = "fs";
        });
        response = await PostAsync(
            """
            mutation {
              updateVideoStorageSettings(input: { enabled: true, dataStorageID: 1 })
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update video storage settings: "
            + "video storage must use a non-database data storage",
            error.GetProperty("message").GetString());

        _dataStorages.Seed(row =>
        {
            row.Id = 2;
            row.Type = "database";
        });
        response = await PostAsync(
            """
            mutation {
              updateVideoStorageSettings(input: { enabled: true, dataStorageID: 2 })
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update video storage settings: "
            + "video storage must use a non-database data storage",
            error.GetProperty("message").GetString());

        _dataStorages.Seed(row =>
        {
            row.Id = 3;
            row.Type = "fs";
        });
        var data = await QueryAsync(
            """
            mutation {
              updateVideoStorageSettings(input: { enabled: true, dataStorageID: 3 })
            }
            """);

        Assert.True(data.GetProperty("updateVideoStorageSettings").GetBoolean());
        Assert.Equal(
            """{"enabled":true,"data_storage_id":3,"scan_interval_minutes":1,"scan_limit":50}""",
            Assert.Single(_store.Rows).Value);
        Assert.Equal(1, _videoReschedule.Calls);
    }

    [Fact]
    public async Task UpdateVideoStorageSettingsSurfacesTheWritePrefixAndSkipsTheReschedule()
    {
        _store.SetFailuresByKey[SystemSettingsAdminService.VideoStorageSettingsKey] =
            new InvalidOperationException("boom");

        var response = await PostAsync(
            """
            mutation {
              updateVideoStorageSettings(input: { enabled: false })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update video storage settings: "
            + "failed to set video storage settings: "
            + "failed to create system setting: boom",
            error.GetProperty("message").GetString());
        Assert.Equal(0, _videoReschedule.Calls);
    }

    [Fact]
    public async Task ProxyPresetsQueryReturnsAnEmptyListWhenMissingOrStoredNull()
    {
        var data = await QueryAsync("{ proxyPresets { name url username password } }");
        Assert.Empty(data.GetProperty("proxyPresets").EnumerateArray());

        _store.Add(SystemConfigBackupKeys.ProxyPresets, "null");

        data = await QueryAsync("{ proxyPresets { name url username password } }");
        Assert.Empty(data.GetProperty("proxyPresets").EnumerateArray());
    }

    [Fact]
    public async Task ProxyPresetsQueryReadsTheStoredDocuments()
    {
        _store.Add(
            SystemConfigBackupKeys.ProxyPresets,
            """
            [{"name":"corp","url":"http://proxy-a:8080","username":"u","password":"p"},
             {"url":"socks5://proxy-b:1080"}]
            """);

        var data = await QueryAsync("{ proxyPresets { name url username password } }");

        var presets = data.GetProperty("proxyPresets").EnumerateArray().ToArray();
        Assert.Equal(2, presets.Length);
        Assert.Equal("corp", presets[0].GetProperty("name").GetString());
        Assert.Equal("http://proxy-a:8080", presets[0].GetProperty("url").GetString());
        Assert.Equal("u", presets[0].GetProperty("username").GetString());
        Assert.Equal("p", presets[0].GetProperty("password").GetString());
        Assert.Equal("", presets[1].GetProperty("name").GetString());
        Assert.Equal("socks5://proxy-b:1080", presets[1].GetProperty("url").GetString());
        Assert.Equal("", presets[1].GetProperty("username").GetString());
        Assert.Equal("", presets[1].GetProperty("password").GetString());
    }

    [Fact]
    public async Task ProxyPresetsQuerySurfacesLookupAndUnmarshalErrors()
    {
        _store.GetFailuresByKey[SystemConfigBackupKeys.ProxyPresets] =
            new InvalidOperationException("boom");

        var response = await PostAsync("{ proxyPresets { url } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get proxy presets: "
            + "failed to get proxy presets: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.Rows.Clear();
        _store.Add(SystemConfigBackupKeys.ProxyPresets, "not json");

        response = await PostAsync("{ proxyPresets { url } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to get proxy presets: failed to unmarshal proxy presets: ",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SaveProxyPresetAppendsThenReplacesByUrl()
    {
        await QueryAsync(
            """
            mutation {
              saveProxyPreset(input: { url: "http://a:1" })
            }
            """);
        var row = Assert.Single(_store.Rows);
        Assert.Equal("""[{"url":"http://a:1"}]""", row.Value);

        await QueryAsync(
            """
            mutation {
              saveProxyPreset(input: { name: "first", url: "http://b:2", username: "u" })
            }
            """);
        Assert.Equal(
            """[{"url":"http://a:1"},{"name":"first","url":"http://b:2","username":"u"}]""",
            row.Value);

        await QueryAsync(
            """
            mutation {
              saveProxyPreset(input: { name: "replaced", url: "http://a:1", password: "p" })
            }
            """);
        Assert.Equal(
            """[{"name":"replaced","url":"http://a:1","password":"p"},{"name":"first","url":"http://b:2","username":"u"}]""",
            row.Value);
    }

    [Fact]
    public async Task DeleteProxyPresetFiltersByUrlAndKeepsAnEmptyArray()
    {
        _store.Add(SystemConfigBackupKeys.ProxyPresets, "null");
        await QueryAsync(
            """
            mutation {
              deleteProxyPreset(url: "http://a:1")
            }
            """);

        var row = Assert.Single(_store.Rows);
        Assert.Equal("[]", row.Value);

        row.Value = """[{"url":"http://a:1"},{"name":"b","url":"http://b:2"}]""";
        await QueryAsync(
            """
            mutation {
              deleteProxyPreset(url: "http://a:1")
            }
            """);
        Assert.Equal("""[{"name":"b","url":"http://b:2"}]""", row.Value);

        // Deleting an absent URL rewrites the same document.
        await QueryAsync(
            """
            mutation {
              deleteProxyPreset(url: "http://missing:9")
            }
            """);
        Assert.Equal("""[{"name":"b","url":"http://b:2"}]""", row.Value);
    }

    [Fact]
    public async Task ProxyPresetMutationsSurfaceTheReferencePrefixes()
    {
        _store.GetFailuresByKey[SystemConfigBackupKeys.ProxyPresets] =
            new InvalidOperationException("boom");

        var response = await PostAsync(
            """
            mutation {
              saveProxyPreset(input: { url: "http://a:1" })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to save proxy preset: "
            + "failed to get proxy presets: failed to get system value: boom",
            error.GetProperty("message").GetString());

        response = await PostAsync(
            """
            mutation {
              deleteProxyPreset(url: "http://a:1")
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to delete proxy preset: "
            + "failed to get proxy presets: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.SetFailuresByKey[SystemConfigBackupKeys.ProxyPresets] =
            new InvalidOperationException("boom");

        response = await PostAsync(
            """
            mutation {
              saveProxyPreset(input: { url: "http://a:1" })
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to save proxy preset: failed to create system setting: boom",
            error.GetProperty("message").GetString());

        response = await PostAsync(
            """
            mutation {
              deleteProxyPreset(url: "http://a:1")
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to delete proxy preset: failed to create system setting: boom",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OnboardingInfoIsNullBeforeAnyCompletion()
    {
        var data = await QueryAsync("{ onboardingInfo { onboarded completedAt } }");

        Assert.Equal(JsonValueKind.Null, data.GetProperty("onboardingInfo").ValueKind);
    }

    [Fact]
    public async Task CompleteOnboardingCreatesTheRecordAndAutoCompletesTheChannelModule()
    {
        var data = await QueryAsync(
            """
            mutation {
              completeOnboarding(input: {})
            }
            """);

        Assert.True(data.GetProperty("completeOnboarding").GetBoolean());
        var row = Assert.Single(_store.Rows);
        Assert.Equal(SystemSettingsAdminService.OnboardedKey, row.Key);

        using var document = JsonDocument.Parse(row.Value);
        var root = document.RootElement;
        Assert.True(root.GetProperty("onboarded").GetBoolean());
        Assert.False(root.TryGetProperty("system_model_setting", out _));

        var module = root.GetProperty("auto_disable_channel");
        Assert.True(module.GetProperty("onboarded").GetBoolean());
        Assert.Equal(
            root.GetProperty("completed_at").GetString(),
            module.GetProperty("completed_at").GetString());

        data = await QueryAsync(
            """
            {
              onboardingInfo {
                onboarded completedAt
                systemModelSetting { onboarded completedAt }
                autoDisableChannel { onboarded completedAt }
              }
            }
            """);

        var info = data.GetProperty("onboardingInfo");
        Assert.True(info.GetProperty("onboarded").GetBoolean());
        Assert.EndsWith("Z", info.GetProperty("completedAt").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            info.GetProperty("systemModelSetting").ValueKind);
        Assert.True(
            info.GetProperty("autoDisableChannel").GetProperty("onboarded").GetBoolean());
        Assert.EndsWith(
            "Z",
            info.GetProperty("autoDisableChannel").GetProperty("completedAt").GetString());
    }

    [Fact]
    public async Task CompleteOnboardingPreservesAnExistingChannelModule()
    {
        _store.Add(
            SystemSettingsAdminService.OnboardedKey,
            """
            {"onboarded":false,
             "system_model_setting":{"onboarded":true,"completed_at":"2026-01-02T03:04:05Z"},
             "auto_disable_channel":{"onboarded":true,"completed_at":"2026-02-03T04:05:06Z"}}
            """);

        await QueryAsync(
            """
            mutation {
              completeOnboarding(input: {})
            }
            """);

        using var document = JsonDocument.Parse(Assert.Single(_store.Rows).Value);
        var root = document.RootElement;
        Assert.True(root.GetProperty("onboarded").GetBoolean());
        Assert.True(root.TryGetProperty("completed_at", out _));
        Assert.Equal(
            "2026-01-02T03:04:05Z",
            root.GetProperty("system_model_setting").GetProperty("completed_at").GetString());
        Assert.Equal(
            "2026-02-03T04:05:06Z",
            root.GetProperty("auto_disable_channel").GetProperty("completed_at").GetString());
    }

    [Fact]
    public async Task CompleteSystemModelSettingOnboardingWritesTheModuleOnly()
    {
        await QueryAsync(
            """
            mutation {
              completeSystemModelSettingOnboarding(input: {})
            }
            """);

        using var document = JsonDocument.Parse(Assert.Single(_store.Rows).Value);
        var root = document.RootElement;
        Assert.False(root.GetProperty("onboarded").GetBoolean());
        Assert.False(root.TryGetProperty("completed_at", out _));
        Assert.False(root.TryGetProperty("auto_disable_channel", out _));

        var module = root.GetProperty("system_model_setting");
        Assert.True(module.GetProperty("onboarded").GetBoolean());
        Assert.True(module.TryGetProperty("completed_at", out _));
    }

    [Fact]
    public async Task ModuleCompletionsPreserveEachOther()
    {
        await QueryAsync(
            """
            mutation {
              completeSystemModelSettingOnboarding(input: {})
            }
            """);

        var data = await QueryAsync(
            """
            {
              onboardingInfo {
                onboarded
                systemModelSetting { onboarded completedAt }
                autoDisableChannel { onboarded }
              }
            }
            """);

        var info = data.GetProperty("onboardingInfo");
        Assert.False(info.GetProperty("onboarded").GetBoolean());
        Assert.True(
            info.GetProperty("systemModelSetting").GetProperty("onboarded").GetBoolean());
        Assert.Equal(
            JsonValueKind.String,
            info.GetProperty("systemModelSetting").GetProperty("completedAt").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            info.GetProperty("autoDisableChannel").ValueKind);

        await QueryAsync(
            """
            mutation {
              completeOnboarding(input: {})
            }
            """);

        data = await QueryAsync(
            """
            {
              onboardingInfo {
                onboarded
                systemModelSetting { onboarded }
                autoDisableChannel { onboarded }
              }
            }
            """);

        info = data.GetProperty("onboardingInfo");
        Assert.True(info.GetProperty("onboarded").GetBoolean());
        Assert.True(
            info.GetProperty("systemModelSetting").GetProperty("onboarded").GetBoolean());
        Assert.True(
            info.GetProperty("autoDisableChannel").GetProperty("onboarded").GetBoolean());

        // An explicit module completion replaces its own timestamp.
        var stored = Assert.Single(_store.Rows);
        stored.Value =
            """
            {"onboarded":true,
             "auto_disable_channel":{"onboarded":true,"completed_at":"2020-01-01T00:00:00Z"}}
            """;

        await QueryAsync(
            """
            mutation {
              completeAutoDisableChannelOnboarding(input: {})
            }
            """);

        using var document = JsonDocument.Parse(stored.Value);
        var root = document.RootElement;
        Assert.True(root.GetProperty("onboarded").GetBoolean());
        Assert.NotEqual(
            "2020-01-01T00:00:00Z",
            root.GetProperty("auto_disable_channel").GetProperty("completed_at").GetString());
    }

    [Fact]
    public async Task OnboardingSurfacesTheReferencePrefixes()
    {
        _store.GetFailuresByKey[SystemSettingsAdminService.OnboardedKey] =
            new InvalidOperationException("boom");

        var response = await PostAsync("{ onboardingInfo { onboarded } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get onboarding info: "
            + "failed to get onboarding info: failed to get system value: boom",
            error.GetProperty("message").GetString());

        response = await PostAsync(
            """
            mutation {
              completeOnboarding(input: {})
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to complete onboarding: "
            + "failed to get existing onboarding info: "
            + "failed to get onboarding info: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.Rows.Clear();
        _store.Add(SystemSettingsAdminService.OnboardedKey, "not json");

        response = await PostAsync("{ onboardingInfo { onboarded } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to get onboarding info: failed to unmarshal onboarding info: ",
            error.GetProperty("message").GetString());

        response = await PostAsync(
            """
            mutation {
              completeSystemModelSettingOnboarding(input: {})
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to complete system model setting onboarding: "
            + "failed to get existing onboarding info: "
            + "failed to unmarshal onboarding info: ",
            error.GetProperty("message").GetString());

        _store.Rows.Clear();
        _store.SetFailuresByKey[SystemSettingsAdminService.OnboardedKey] =
            new InvalidOperationException("boom");

        response = await PostAsync(
            """
            mutation {
              completeSystemModelSettingOnboarding(input: {})
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to complete system model setting onboarding: "
            + "failed to create system setting: boom",
            error.GetProperty("message").GetString());

        response = await PostAsync(
            """
            mutation {
              completeAutoDisableChannelOnboarding(input: {})
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to complete auto disable channel onboarding: "
            + "failed to create system setting: boom",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WebhookNotifierConfigReturnsTheReferenceDefaultsAndStoredValues()
    {
        var data = await QueryAsync(
            """
            {
              webhookNotifierConfig {
                targets {
                  name enabled url
                  proxy { type url username password disableConnectionReuse }
                  timeoutMs headers { key value } body
                }
                subscriptions { event targetNames }
              }
            }
            """);

        var config = data.GetProperty("webhookNotifierConfig");
        Assert.Empty(config.GetProperty("targets").EnumerateArray());
        Assert.Empty(config.GetProperty("subscriptions").EnumerateArray());

        _store.Add(
            SystemConfigBackupKeys.WebhookNotifierConfig,
            """
            {
              "targets": [
                {
                  "name": "primary",
                  "enabled": true,
                  "url": "https://hooks.example.com/axon",
                  "proxy": {"type": "url", "url": "http://proxy:8080", "username": "u", "password": "p", "disableConnectionReuse": true},
                  "timeout_ms": 2500,
                  "headers": [{"key": "X-Sign", "value": "abc"}],
                  "body": "payload"
                },
                {"name": "bare"}
              ],
              "subscriptions": [
                {"event": "channel.auto_disabled", "target_names": ["primary"]},
                {"event": "bare"}
              ]
            }
            """);

        data = await QueryAsync(
            """
            {
              webhookNotifierConfig {
                targets {
                  name enabled url
                  proxy { type url username password disableConnectionReuse }
                  timeoutMs headers { key value } body
                }
                subscriptions { event targetNames }
              }
            }
            """);

        config = data.GetProperty("webhookNotifierConfig");
        var targets = config.GetProperty("targets").EnumerateArray().ToArray();
        Assert.Equal(2, targets.Length);

        var primary = targets[0];
        Assert.Equal("primary", primary.GetProperty("name").GetString());
        Assert.True(primary.GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "https://hooks.example.com/axon",
            primary.GetProperty("url").GetString());

        // ProxyType marshals the raw lowercase stored value (C139).
        var proxy = primary.GetProperty("proxy");
        Assert.Equal("url", proxy.GetProperty("type").GetString());
        Assert.Equal("http://proxy:8080", proxy.GetProperty("url").GetString());
        Assert.Equal("u", proxy.GetProperty("username").GetString());
        Assert.Equal("p", proxy.GetProperty("password").GetString());
        Assert.True(proxy.GetProperty("disableConnectionReuse").GetBoolean());

        Assert.Equal(2500, primary.GetProperty("timeoutMs").GetInt32());
        var header = Assert.Single(primary.GetProperty("headers").EnumerateArray());
        Assert.Equal("X-Sign", header.GetProperty("key").GetString());
        Assert.Equal("abc", header.GetProperty("value").GetString());
        Assert.Equal("payload", primary.GetProperty("body").GetString());

        // A minimal stored target keeps the Go zero values; a nil headers
        // slice renders as [] through gqlgen's list marshaler.
        var bare = targets[1];
        Assert.Equal("bare", bare.GetProperty("name").GetString());
        Assert.False(bare.GetProperty("enabled").GetBoolean());
        Assert.Equal("", bare.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("proxy").ValueKind);
        Assert.Equal(0, bare.GetProperty("timeoutMs").GetInt32());
        Assert.Empty(bare.GetProperty("headers").EnumerateArray());
        Assert.Equal("", bare.GetProperty("body").GetString());

        var subscriptions = config.GetProperty("subscriptions").EnumerateArray().ToArray();
        Assert.Equal(2, subscriptions.Length);
        Assert.Equal(
            "channel.auto_disabled",
            subscriptions[0].GetProperty("event").GetString());
        Assert.Equal(
            "primary",
            Assert.Single(
                subscriptions[0].GetProperty("targetNames").EnumerateArray()).GetString());
        Assert.Equal("bare", subscriptions[1].GetProperty("event").GetString());
        Assert.Empty(subscriptions[1].GetProperty("targetNames").EnumerateArray());

        // Explicit null lists normalize to empty slices, and a stored target
        // with "headers": null renders as [].
        var stored = Assert.Single(_store.Rows);
        stored.Value =
            """{"targets":[{"name":"x","headers":null}],"subscriptions":null}""";

        data = await QueryAsync(
            """
            {
              webhookNotifierConfig {
                targets { name headers { key value } }
                subscriptions { event targetNames }
              }
            }
            """);

        config = data.GetProperty("webhookNotifierConfig");
        var only = Assert.Single(config.GetProperty("targets").EnumerateArray());
        Assert.Equal("x", only.GetProperty("name").GetString());
        Assert.Empty(only.GetProperty("headers").EnumerateArray());
        Assert.Empty(config.GetProperty("subscriptions").EnumerateArray());
    }

    [Fact]
    public async Task UpdateWebhookNotifierConfigWritesTheReferenceDocument()
    {
        var data = await QueryAsync(
            """
            mutation {
              updateWebhookNotifierConfig(input: {
                targets: [
                  {
                    name: "primary"
                    enabled: true
                    url: "https://hooks.example.com/axon"
                    proxy: {
                      type: url
                      url: "http://proxy:8080"
                      username: "u"
                      password: "p"
                      disableConnectionReuse: true
                    }
                    timeoutMs: 2500
                    headers: [{ key: "X-Sign", value: "abc" }]
                    body: "payload"
                  },
                  {
                    name: "second"
                    enabled: false
                    url: ""
                    timeoutMs: 0
                    body: ""
                  }
                ]
                subscriptions: [{ event: "channel.auto_disabled", targetNames: ["primary"] }]
              })
            }
            """);

        Assert.True(
            data.GetProperty("updateWebhookNotifierConfig").GetBoolean());

        var stored = Assert.Single(_store.Rows);
        Assert.Equal(SystemConfigBackupKeys.WebhookNotifierConfig, stored.Key);

        // Field order and omitempty follow Go json.Marshal: proxy is omitted
        // when nil, headers is written as null when nil.
        Assert.Equal(
            """{"targets":[{"name":"primary","enabled":true,"url":"https://hooks.example.com/axon","proxy":{"type":"url","url":"http://proxy:8080","username":"u","password":"p","disableConnectionReuse":true},"timeout_ms":2500,"headers":[{"key":"X-Sign","value":"abc"}],"body":"payload"},{"name":"second","enabled":false,"url":"","timeout_ms":0,"headers":null,"body":""}],"subscriptions":[{"event":"channel.auto_disabled","target_names":["primary"]}]}""",
            stored.Value);

        // Absent input lists are normalized to empty slices before writing.
        data = await QueryAsync(
            """
            mutation {
              updateWebhookNotifierConfig(input: {})
            }
            """);

        Assert.True(
            data.GetProperty("updateWebhookNotifierConfig").GetBoolean());
        Assert.Equal("""{"targets":[],"subscriptions":[]}""", stored.Value);
    }

    [Fact]
    public async Task WebhookNotifierConfigSurfacesTheReferencePrefixes()
    {
        _store.GetFailuresByKey[SystemConfigBackupKeys.WebhookNotifierConfig] =
            new InvalidOperationException("boom");

        var response = await PostAsync(
            "{ webhookNotifierConfig { targets { name } } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to get webhook notifier config: failed to get system value: boom",
            error.GetProperty("message").GetString());

        _store.GetFailuresByKey.Clear();
        _store.Rows.Clear();
        _store.Add(SystemConfigBackupKeys.WebhookNotifierConfig, "not json");

        response = await PostAsync(
            "{ webhookNotifierConfig { targets { name } } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to unmarshal webhook notifier config: ",
            error.GetProperty("message").GetString());

        _store.Rows.Clear();
        _store.SetFailuresByKey[SystemConfigBackupKeys.WebhookNotifierConfig] =
            new InvalidOperationException("boom");

        response = await PostAsync(
            """
            mutation {
              updateWebhookNotifierConfig(input: {})
            }
            """);

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to update webhook notifier config: failed to create system setting: boom",
            error.GetProperty("message").GetString());
    }

    private const string DefaultChannelSettingsJson =
        """{"probe":{"enabled":true,"frequency":"5m"},"auto_sync":{"frequency":"1h"},"test_system_prompt":"You are a helpful assistant.","test_user_prompt":"Hello world, I'm AxonHub.\nPlease tell me who you are?"}""";

    private static bool ProviderEntry(JsonElement[] providers, string providerType)
        => providers.Single(
                provider => provider.GetProperty("provider").GetString() == providerType)
            .GetProperty("enabled").GetBoolean();

    private async Task<JsonElement> QueryAsync(string query)
    {
        var response = await PostAsync(query);
        Assert.False(response.TryGetProperty("errors", out _), response.ToString());
        return response.GetProperty("data");
    }

    private async Task<JsonElement> PostAsync(string query)
    {
        var payload = JsonSerializer.Serialize(new { query });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
