using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
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
    private readonly FakeProviderQuotaStatusStore _quotaStatuses = new();
    private readonly FakeChannelStore _quotaChannels = new();
    private readonly ProviderQuotaSettingsCache _quotaCache = new();
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
            _ => new SystemSettingsAdminService(_store));
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
