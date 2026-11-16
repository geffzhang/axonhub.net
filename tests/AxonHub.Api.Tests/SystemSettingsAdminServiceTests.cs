using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the strict half of SystemService (internal/server/biz/system.go):
/// missing rows, wrapped lookup/unmarshal failures, the coded auto-disable
/// validation error and the normalized documents written back to the store.
/// </summary>
public sealed class SystemSettingsAdminServiceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("True", true)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public async Task IsInitializedComparesCaseInsensitively(string? stored, bool expected)
    {
        var store = new FakeSystemStore();
        if (stored is not null)
        {
            store.Add(SystemSettingsAdminService.InitializedKey, stored);
        }

        Assert.Equal(expected, await new SystemSettingsAdminService(store).IsInitializedAsync());
    }

    [Fact]
    public async Task MissingRowsReturnTheReferenceDefaults()
    {
        var service = new SystemSettingsAdminService(new FakeSystemStore());

        Assert.Equal(RetryPolicy.Default, await service.GetRetryPolicyAsync());
        Assert.Equal(ModelSettings.Default, await service.GetModelSettingsAsync());
        Assert.Equal(SecuritySettings.Default, await service.GetSecuritySettingsAsync());
    }

    [Theory]
    [InlineData("retry_policy")]
    [InlineData("model")]
    [InlineData("security")]
    public async Task LookupFailuresKeepTheReferenceWrapperChain(string kind)
    {
        var store = new FakeSystemStore
        {
            GetFailure = new InvalidOperationException("database is down"),
        };
        var service = new SystemSettingsAdminService(store);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => kind switch
            {
                "retry_policy" => service.GetRetryPolicyAsync(),
                "model" => service.GetModelSettingsAsync(),
                _ => service.GetSecuritySettingsAsync(),
            });

        var prefix = kind switch
        {
            "retry_policy" => "failed to get retry policy",
            "model" => "failed to get model settings",
            _ => "failed to get security settings",
        };
        Assert.Equal(prefix + ": failed to get system value: database is down", error.Message);
    }

    [Fact]
    public async Task MalformedRetryPolicyIsReportedWithoutFallingBack()
    {
        var store = new FakeSystemStore();
        store.Add(RetryPolicyCodec.RetryPolicyKey, """{"trace_sticky_mode":null}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).GetRetryPolicyAsync());

        Assert.Equal(
            "failed to unmarshal retry policy: invalid TraceStickyMode: \"\"",
            error.Message);
    }

    [Fact]
    public async Task MalformedModelSettingsAreReportedWithoutFallingBack()
    {
        var store = new FakeSystemStore();
        store.Add(ModelSettingsCodec.ModelSettingsKey, """{"query_all_channel_models":"true"}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).GetModelSettingsAsync());

        Assert.StartsWith(
            "failed to unmarshal model settings: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetRetryPolicyNormalizesAndPersistsTheDocument()
    {
        var store = new FakeSystemStore();
        var service = new SystemSettingsAdminService(store);

        await service.SetRetryPolicyAsync(new RetryPolicy
        {
            Enabled = true,
            LoadBalancerStrategy = "weighted",
            TraceStickyMode = "bogus",
            AutoDisableChannel = new AutoDisableChannel(
                true,
                [
                    new AutoDisableRule(
                        [429, 401, 429],
                        [" quota ", "", "quota"],
                        2,
                        RetryPolicy.AutoDisableActionTemporary,
                        5,
                        "0 0 * * *",
                        "UTC"),
                ]),
        });

        var stored = Assert.Single(store.Rows);
        Assert.Equal(RetryPolicyCodec.RetryPolicyKey, stored.Key);
        Assert.Contains("\"load_balancer_strategy\":\"failover\"", stored.Value, StringComparison.Ordinal);

        var policy = RetryPolicyCodec.Deserialize(stored.Value);
        Assert.Equal(RetryPolicy.FailoverStrategy, policy.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyPreferPreviousChannel, policy.TraceStickyMode);

        var rule = Assert.Single(policy.AutoDisableChannel.Rules);
        Assert.Equal([401, 429], rule.StatusCodes);
        Assert.Equal(["quota"], rule.KeywordPatterns);
        Assert.Equal(5, rule.DisableDurationMinutes);
        Assert.Equal(string.Empty, rule.DisableUntilCron);
        Assert.Equal(string.Empty, rule.DisableUntilTimezone);
    }

    [Fact]
    public async Task SetRetryPolicySurfacesInvalidRulesAsCodedErrors()
    {
        var store = new FakeSystemStore();
        var service = new SystemSettingsAdminService(store);

        var error = await Assert.ThrowsAsync<CodedException>(
            () => service.SetRetryPolicyAsync(new RetryPolicy
            {
                AutoDisableChannel = new AutoDisableChannel(
                    true,
                    [
                        new AutoDisableRule(
                            null,
                            null,
                            0,
                            RetryPolicy.AutoDisableActionPermanent,
                            null,
                            string.Empty,
                            string.Empty),
                    ]),
            }));

        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Equal(
            "API key rule 1 consecutive error count must be at least 1",
            error.Message);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task SetRetryPolicyWrapsStorageFailures()
    {
        var store = new FakeSystemStore
        {
            SetFailure = new InvalidOperationException("write rejected"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(store).SetRetryPolicyAsync(RetryPolicy.Default));

        Assert.Equal(
            "failed to create system setting: write rejected",
            error.Message);
    }

    [Fact]
    public async Task SetModelSettingsRejectsInvalidBlacklistRegex()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SystemSettingsAdminService(new FakeSystemStore())
                .SetModelSettingsAsync(ModelSettings.Default with { ModelBlacklistRegex = "(" }));

        Assert.StartsWith(
            "invalid model blacklist regex: invalid regex pattern: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetModelSettingsNormalizesDevelopersBeforeValidation()
    {
        var error = await Assert.ThrowsAsync<ModelSettingsValidationException>(
            () => new SystemSettingsAdminService(new FakeSystemStore())
                .SetModelSettingsAsync(new ModelSettings
                {
                    DeveloperSettings =
                    [
                        new DeveloperModelSettings(" ", []),
                    ],
                }));

        Assert.Equal("model developer is required", error.Message);
    }

    [Fact]
    public async Task SetModelSettingsPersistsTheNormalizedDocument()
    {
        var store = new FakeSystemStore();
        var service = new SystemSettingsAdminService(store);

        await service.SetModelSettingsAsync(new ModelSettings
        {
            DeveloperSettings =
            [
                new DeveloperModelSettings(
                    " openai ",
                    [
                        new ModelAssociation
                        {
                            Type = ModelAssociation.TypeChannelModel,
                            ChannelModel = new ChannelModelAssociation(7, "pinned"),
                        },
                    ]),
            ],
        });

        var stored = Assert.Single(store.Rows);
        Assert.Equal(ModelSettingsCodec.ModelSettingsKey, stored.Key);
        Assert.Contains("\"developer\":\"openai\"", stored.Value, StringComparison.Ordinal);
        Assert.Contains("\"channelId\":7,\"modelId\":\"\"", stored.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetSecuritySettingsNormalizesAndPersists()
    {
        var store = new FakeSystemStore();
        var service = new SystemSettingsAdminService(store);

        await service.SetSecuritySettingsAsync(
            new SecuritySettings([" 1.2.3.4 ", "", "1.2.3.4", "5.6.7.8"], ShowRequestLogIpBanIcon: false));

        var stored = Assert.Single(store.Rows);
        Assert.Equal(SecuritySettingsCodec.SecuritySettingsKey, stored.Key);
        Assert.Equal(
            """{"blocked_ips":["1.2.3.4","5.6.7.8"],"show_request_log_ip_ban_icon":false}""",
            stored.Value);
    }

    [Fact]
    public async Task SecuritySettingsDeserializeStartsFromTheDefaults()
    {
        var settings = SecuritySettingsCodec.Deserialize("{}");

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);

        var overridden = SecuritySettingsCodec.Deserialize(
            """{"blocked_ips":[" 1.1.1.1 ",""],"show_request_log_ip_ban_icon":false}""");

        Assert.Equal(["1.1.1.1"], overridden.BlockedIps);
        Assert.False(overridden.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public void SecuritySettingsNullDocumentKeepsTheDefaults()
    {
        var settings = SecuritySettingsCodec.Deserialize("null");

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public void SecuritySettingsRejectNonObjectDocumentsWithTheStoredTypeName()
    {
        var error = Assert.Throws<JsonException>(
            () => SecuritySettingsCodec.Deserialize("[]"));

        Assert.Equal(
            "json: cannot unmarshal array into Go value of type biz.securitySettingsJSON",
            error.Message);
    }
}
