using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Safeguards;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.SystemService's Claude Code safeguards settings surface
/// (internal/server/biz/system_claude_code.go, followed at f272549f): the
/// server defaults, the stale-cache-proof direct read, validation, the
/// read_settings / write_settings guards and the Go-shaped persisted JSON.
/// </summary>
public sealed class ClaudeCodeSafeguardsSettingsServiceTests
{
    private readonly FakeSystemStore _store = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly FakeManagementIdentity _identity = new(null);

    private ClaudeCodeSafeguardsSettingsService NewService(
        ClaudeCodeSafeguardsServerDefaults? serverDefaults = null)
        => new(
            _store,
            serverDefaults ?? new ClaudeCodeSafeguardsServerDefaults(),
            new AuthorizationScope(_identity, _adminUsers));

    private async Task<ClaudeCodeSafeguardsSettingsService> SignInAsync(
        params string[] scopes)
    {
        var id = await _adminUsers.CreateAsync(
            new UserRecord
            {
                Email = "settings@example.com",
                Status = "activated",
                Scopes = JsonSerializer.Serialize(scopes),
            },
            roleIds: null);
        _identity.User = new ManagementUser(id, IsOwner: false);
        return NewService();
    }

    [Fact]
    public async Task MissingRowReturnsTheReferenceConfDefaults()
    {
        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.True(settings.UsingServerDefaults);
        Assert.Equal(SafeguardsDefaults.DefaultPolicy, settings.DefaultPolicy);
        Assert.Equal(string.Empty, settings.Config.ClassifierModel);
        Assert.Equal(string.Empty, settings.Config.Policy);
        Assert.Empty(settings.Config.FallbackModels);
        Assert.False(settings.Config.EscalateOnDenial);
        Assert.Equal(0, settings.Config.AttemptTimeoutSeconds);
        Assert.Equal(30, settings.Config.TimeoutSeconds);
        Assert.Equal(4096, settings.Config.MaxTokens);
        Assert.Equal(24000, settings.Config.TranscriptCharBudget);
    }

    [Fact]
    public async Task ZeroServerDefaultsFallBackToThePackageDefaults()
    {
        // system_claude_code.go:89-97: a zero Timeout/MaxTokens/TranscriptCharBudget
        // on the YAML config is replaced before the settings are exposed.
        var service = new ClaudeCodeSafeguardsSettingsService(
            _store,
            new ClaudeCodeSafeguardsServerDefaults
            {
                ClassifierModel = "yaml-model",
                Policy = "YAML policy",
                TimeoutNanoseconds = 0,
                MaxTokens = 0,
                TranscriptCharBudget = 0,
            },
            new AuthorizationScope(_identity, _adminUsers));
        _identity.User = null;
        var settings = await ReadWithScopeAsync(service);

        Assert.True(settings.UsingServerDefaults);
        Assert.Equal("yaml-model", settings.Config.ClassifierModel);
        Assert.Equal("YAML policy", settings.Config.Policy);
        Assert.Equal(30, settings.Config.TimeoutSeconds);
        Assert.Equal(4096, settings.Config.MaxTokens);
        Assert.Equal(24000, settings.Config.TranscriptCharBudget);
    }

    [Fact]
    public async Task ServerDefaultsAreReturnedVerbatimWhileUsingServerDefaults()
    {
        var service = new ClaudeCodeSafeguardsSettingsService(
            _store,
            new ClaudeCodeSafeguardsServerDefaults
            {
                ClassifierModel = "yaml-model",
                Policy = "YAML policy",
                FallbackModels = ["runner-up"],
                EscalateOnDenial = true,
                AttemptTimeoutNanoseconds = 1_500_000_000,
                TimeoutNanoseconds = 45_000_000_000,
                MaxTokens = 2048,
                TranscriptCharBudget = 12000,
            },
            new AuthorizationScope(_identity, _adminUsers));
        var settings = await ReadWithScopeAsync(service);

        Assert.True(settings.UsingServerDefaults);
        Assert.Equal("yaml-model", settings.Config.ClassifierModel);
        Assert.Equal("YAML policy", settings.Config.Policy);
        Assert.Equal(["runner-up"], settings.Config.FallbackModels);
        Assert.True(settings.Config.EscalateOnDenial);
        Assert.Equal(1.5, settings.Config.AttemptTimeoutSeconds);
        Assert.Equal(45, settings.Config.TimeoutSeconds);
        Assert.Equal(2048, settings.Config.MaxTokens);
        Assert.Equal(12000, settings.Config.TranscriptCharBudget);
    }

    [Fact]
    public async Task StoredOverrideIsNormalizedAndReturned()
    {
        // TestClaudeCodeSafeguardsPersistence (system_claude_code_test.go:28-49).
        _store.Add(
            ClaudeCodeSafeguardsSettingsService.SystemKey,
            """
            {"classifierModel":" ui-model ","policy":"Custom policy\nAllow feature branch updates.",
             "fallbackModels":[" backup "],"escalateOnDenial":true,"attemptTimeoutSeconds":0.5,
             "timeoutSeconds":12,"maxTokens":1024,"transcriptCharBudget":8000}
            """);

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.False(settings.UsingServerDefaults);
        Assert.Equal(SafeguardsDefaults.DefaultPolicy, settings.DefaultPolicy);
        Assert.Equal("ui-model", settings.Config.ClassifierModel);
        Assert.Equal("Custom policy\nAllow feature branch updates.", settings.Config.Policy);
        Assert.Equal(["backup"], settings.Config.FallbackModels);
        Assert.True(settings.Config.EscalateOnDenial);
        Assert.Equal(0.5, settings.Config.AttemptTimeoutSeconds);
        Assert.Equal(12, settings.Config.TimeoutSeconds);
        Assert.Equal(1024, settings.Config.MaxTokens);
        Assert.Equal(8000, settings.Config.TranscriptCharBudget);
    }

    [Fact]
    public async Task ANullStoredValueMeansServerDefaults()
    {
        _store.Add(ClaudeCodeSafeguardsSettingsService.SystemKey, "null");

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.True(settings.UsingServerDefaults);
        Assert.Equal(30, settings.Config.TimeoutSeconds);
    }

    [Fact]
    public async Task AnEmptyStoredObjectFailsValidationLikeGo()
    {
        // json.Unmarshal("{}") leaves the zero config; runtimeConfig then
        // rejects the zero timeout before any default is applied.
        _store.Add(ClaudeCodeSafeguardsSettingsService.SystemKey, "{}");

        var error = await Assert.ThrowsAsync<CodedException>(
            async () => await (await SignInAsync("read_settings")).GetAsync());

        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Equal(
            "overall timeout must be greater than zero and at most 120 seconds",
            error.Message);
    }

    [Fact]
    public async Task CorruptStoredValueFailsWithTheDecodePrefix()
    {
        // system_claude_code_test.go:61-64: corrupt settings must not silently
        // disable review.
        _store.Add(ClaudeCodeSafeguardsSettingsService.SystemKey, "{");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (await SignInAsync("read_settings")).GetAsync());

        Assert.StartsWith("decode Claude Code safeguards settings: ", error.Message);
    }

    [Fact]
    public async Task StoredValueWithInvalidLimitsFailsValidation()
    {
        _store.Add(
            ClaudeCodeSafeguardsSettingsService.SystemKey,
            """
            {"classifierModel":"reviewer","policy":"","fallbackModels":[],"escalateOnDenial":false,
             "attemptTimeoutSeconds":0,"timeoutSeconds":0,"maxTokens":4096,"transcriptCharBudget":24000}
            """);

        var error = await Assert.ThrowsAsync<CodedException>(
            async () => await (await SignInAsync("read_settings")).GetAsync());

        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Equal(
            "overall timeout must be greater than zero and at most 120 seconds",
            error.Message);
    }

    [Fact]
    public async Task UpdatePersistsTheGoShapedNormalizedJson()
    {
        var service = await SignInAsync("read_settings", "write_settings");

        await service.UpdateAsync(new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = " ui-model ",
            Policy = "Custom policy\nAllow feature branch updates.",
            FallbackModels = [" backup "],
            EscalateOnDenial = true,
            AttemptTimeoutSeconds = 0.5,
            TimeoutSeconds = 12,
            MaxTokens = 1024,
            TranscriptCharBudget = 8000,
        });

        var row = Assert.Single(_store.Rows);
        Assert.Equal(ClaudeCodeSafeguardsSettingsService.SystemKey, row.Key);
        Assert.Equal(
            """{"classifierModel":"ui-model","policy":"Custom policy\nAllow feature branch updates.","fallbackModels":["backup"],"escalateOnDenial":true,"attemptTimeoutSeconds":0.5,"timeoutSeconds":12,"maxTokens":1024,"transcriptCharBudget":8000}""",
            row.Value);

        // A fresh service reads the persisted override, not an in-process value.
        var settings = await NewService().GetAsync();
        Assert.False(settings.UsingServerDefaults);
        Assert.Equal("ui-model", settings.Config.ClassifierModel);
        Assert.Equal(["backup"], settings.Config.FallbackModels);
        Assert.Equal(0.5, settings.Config.AttemptTimeoutSeconds);
        Assert.Equal(12, settings.Config.TimeoutSeconds);
    }

    [Fact]
    public async Task UpdateStoresAnEmptyFallbackArrayNotNull()
    {
        // claudeCodeConfig builds append([]string{}, c.FallbackModels...), which
        // is an empty non-nil slice: Go marshals [] not null.
        var service = await SignInAsync("read_settings", "write_settings");

        await service.UpdateAsync(new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = "reviewer",
            TimeoutSeconds = 30,
            MaxTokens = 4096,
            TranscriptCharBudget = 24000,
        });

        Assert.Contains("\"fallbackModels\":[]", Assert.Single(_store.Rows).Value);
    }

    [Fact]
    public async Task UpdateTruncatesSecondsToWholeNanosecondsLikeGo()
    {
        var service = await SignInAsync("read_settings", "write_settings");

        await service.UpdateAsync(new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = "reviewer",
            AttemptTimeoutSeconds = 0.0000000009,
            TimeoutSeconds = 0.1234567891,
            MaxTokens = 4096,
            TranscriptCharBudget = 24000,
        });

        Assert.Contains("\"attemptTimeoutSeconds\":0,", Assert.Single(_store.Rows).Value);
        Assert.Contains("\"timeoutSeconds\":0.123456789,", _store.Rows[0].Value);

        var settings = await NewService().GetAsync();
        Assert.Equal(0, settings.Config.AttemptTimeoutSeconds);
        Assert.Equal(0.123456789, settings.Config.TimeoutSeconds);
    }

    [Fact]
    public async Task UpdateRejectsEveryReferenceValidationRule()
    {
        var service = await SignInAsync("read_settings", "write_settings");
        var baseline = new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = "reviewer",
            TimeoutSeconds = 30,
            MaxTokens = 4096,
            TranscriptCharBudget = 24000,
        };

        var cases = new (string Name, ClaudeCodeSafeguardsConfig Config, string Message)[]
        {
            (
                "zero timeout",
                baseline with { TimeoutSeconds = 0 },
                "overall timeout must be greater than zero and at most 120 seconds"),
            (
                "timeout above 120",
                baseline with { TimeoutSeconds = 121 },
                "overall timeout must be greater than zero and at most 120 seconds"),
            (
                "NaN timeout",
                baseline with { TimeoutSeconds = double.NaN },
                "overall timeout must be greater than zero and at most 120 seconds"),
            (
                "infinite timeout",
                baseline with { TimeoutSeconds = double.PositiveInfinity },
                "overall timeout must be greater than zero and at most 120 seconds"),
            (
                "sub-nanosecond timeout",
                baseline with { TimeoutSeconds = 1e-10 },
                "overall timeout must be at least one nanosecond"),
            (
                "negative attempt timeout",
                baseline with { AttemptTimeoutSeconds = -1 },
                "attempt timeout must be between zero and 120 seconds"),
            (
                "attempt timeout above 120",
                baseline with { AttemptTimeoutSeconds = 121 },
                "attempt timeout must be between zero and 120 seconds"),
            (
                "NaN attempt timeout",
                baseline with { AttemptTimeoutSeconds = double.NaN },
                "attempt timeout must be between zero and 120 seconds"),
            (
                "zero max tokens",
                baseline with { MaxTokens = 0 },
                "max tokens must be between 1 and 16384"),
            (
                "max tokens above 16384",
                baseline with { MaxTokens = 16385 },
                "max tokens must be between 1 and 16384"),
            (
                "zero transcript budget",
                baseline with { TranscriptCharBudget = 0 },
                "transcript budget must be between 1 and 1000000 UTF-8 bytes"),
            (
                "transcript budget above 1000000",
                baseline with { TranscriptCharBudget = 1000001 },
                "transcript budget must be between 1 and 1000000 UTF-8 bytes"),
            (
                "duplicate fallback after trimming",
                baseline with { FallbackModels = ["backup", " backup "] },
                "fallback models must be nonempty, distinct and different from the primary model"),
            (
                "fallback equal to the primary model",
                baseline with { ClassifierModel = "primary", FallbackModels = ["primary"] },
                "fallback models must be nonempty, distinct and different from the primary model"),
            (
                "blank fallback",
                baseline with { FallbackModels = [" "] },
                "fallback models must be nonempty, distinct and different from the primary model"),
        };

        foreach (var (name, config, message) in cases)
        {
            var error = await Assert.ThrowsAsync<CodedException>(
                () => service.UpdateAsync(config));
            Assert.Equal(CodedException.ValidationFailed, error.Code);
            Assert.Equal(message, error.Message);
            Assert.Empty(_store.Rows);
            _ = name;
        }
    }

    [Fact]
    public async Task AFailedValidationLeavesTheStoredValueUntouched()
    {
        _store.Add(ClaudeCodeSafeguardsSettingsService.SystemKey, "null");
        var service = await SignInAsync("read_settings", "write_settings");

        await Assert.ThrowsAsync<CodedException>(
            () => service.UpdateAsync(new ClaudeCodeSafeguardsConfig
            {
                TimeoutSeconds = 0,
                MaxTokens = 4096,
                TranscriptCharBudget = 24000,
            }));

        Assert.Equal("null", Assert.Single(_store.Rows).Value);
    }

    [Fact]
    public async Task ResetStoresTheNullLiteral()
    {
        var service = await SignInAsync("read_settings", "write_settings");

        await service.UpdateAsync(new ClaudeCodeSafeguardsConfig
        {
            ClassifierModel = "reviewer",
            TimeoutSeconds = 30,
            MaxTokens = 4096,
            TranscriptCharBudget = 24000,
        });
        await service.ResetAsync();

        Assert.Equal("null", Assert.Single(_store.Rows).Value);
        Assert.True((await service.GetAsync()).UsingServerDefaults);
    }

    [Fact]
    public async Task ScopeChecksMatchTheReference()
    {
        // TestClaudeCodeSafeguardsValidationAndPermissions
        // (system_claude_code_test.go:130-148).
        var cases = new (string[] Scopes, bool Read, bool Write)[]
        {
            ([], false, false),
            (["read_settings"], true, false),
            (["write_settings"], false, true),
        };

        foreach (var (scopes, canRead, canWrite) in cases)
        {
            var store = new FakeSystemStore();
            var users = new FakeUserAdminStore();
            var identity = new FakeManagementIdentity(null);
            var id = await users.CreateAsync(
                new UserRecord
                {
                    Email = "scoped@example.com",
                    Status = "activated",
                    Scopes = JsonSerializer.Serialize(scopes),
                },
                roleIds: null);
            identity.User = new ManagementUser(id, IsOwner: false);
            var service = new ClaudeCodeSafeguardsSettingsService(
                store,
                new ClaudeCodeSafeguardsServerDefaults(),
                new AuthorizationScope(identity, users));

            var input = new ClaudeCodeSafeguardsConfig
            {
                ClassifierModel = "reviewer",
                TimeoutSeconds = 30,
                MaxTokens = 4096,
                TranscriptCharBudget = 24000,
            };

            if (canRead)
            {
                _ = await service.GetAsync();
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.GetAsync());
                Assert.Contains("does not have required scope read_settings", error.Message);
            }

            if (canWrite)
            {
                await service.UpdateAsync(input);
                await service.ResetAsync();
            }
            else
            {
                var updateError = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.UpdateAsync(input));
                Assert.Contains(
                    "does not have required scope write_settings",
                    updateError.Message);

                var resetError = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.ResetAsync());
                Assert.Contains(
                    "does not have required scope write_settings",
                    resetError.Message);
            }
        }

        // No principal at all still fails with the authz message.
        var anonymous = new ClaudeCodeSafeguardsSettingsService(
            new FakeSystemStore(),
            new ClaudeCodeSafeguardsServerDefaults(),
            new AuthorizationScope(new FakeManagementIdentity(null), new FakeUserAdminStore()));
        var anonymousError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => anonymous.GetAsync());
        Assert.Equal(
            "authz: principal unknown does not have required scope read_settings",
            anonymousError.Message);
    }

    [Fact]
    public async Task RuntimeConfigIgnoresPermissionsAndReturnsTheValidatedConfig()
    {
        // TestClaudeCodeSafeguardsPersistence: ClaudeCodeSafeguardsConfig runs
        // without admin scopes.
        _store.Add(
            ClaudeCodeSafeguardsSettingsService.SystemKey,
            """
            {"classifierModel":" ui-model ","policy":"Custom policy","fallbackModels":[" backup "],
             "escalateOnDenial":true,"attemptTimeoutSeconds":0.5,"timeoutSeconds":12,
             "maxTokens":1024,"transcriptCharBudget":8000}
            """);
        var service = NewService();
        _identity.User = null;

        var runtime = await service.GetRuntimeConfigAsync();

        Assert.Equal("ui-model", runtime.ClassifierModel);
        Assert.Equal("Custom policy", runtime.Policy);
        Assert.Equal(["backup"], runtime.FallbackModels);
        Assert.True(runtime.EscalateOnDenial);
        Assert.Equal(500_000_000, runtime.AttemptTimeoutNanoseconds);
        Assert.Equal(12_000_000_000, runtime.TimeoutNanoseconds);
        Assert.Equal(1024, runtime.MaxTokens);
        Assert.Equal(8000, runtime.TranscriptCharBudget);
    }

    [Fact]
    public async Task RuntimeConfigSurfacesCorruptStoredValues()
    {
        _store.Add(ClaudeCodeSafeguardsSettingsService.SystemKey, "{");
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().GetRuntimeConfigAsync());

        Assert.StartsWith("decode Claude Code safeguards settings: ", error.Message);
    }

    [Fact]
    public async Task ReadFailuresCarryTheReadPrefix()
    {
        _store.GetFailuresByKey[ClaudeCodeSafeguardsSettingsService.SystemKey] =
            new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (await SignInAsync("read_settings")).GetAsync());

        Assert.Equal("read Claude Code safeguards settings: db down", error.Message);
    }

    [Fact]
    public async Task WriteFailuresCarryTheCreateSettingPrefix()
    {
        _store.SetFailuresByKey[ClaudeCodeSafeguardsSettingsService.SystemKey] =
            new InvalidOperationException("write rejected");
        var service = await SignInAsync("read_settings", "write_settings");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(new ClaudeCodeSafeguardsConfig
            {
                ClassifierModel = "reviewer",
                TimeoutSeconds = 30,
                MaxTokens = 4096,
                TranscriptCharBudget = 24000,
            }));

        Assert.Equal("failed to create system setting: write rejected", error.Message);
    }

    private async Task<ClaudeCodeSafeguardsSettings> ReadWithScopeAsync(
        ClaudeCodeSafeguardsSettingsService service)
    {
        var id = await _adminUsers.CreateAsync(
            new UserRecord
            {
                Email = "reader@example.com",
                Status = "activated",
                Scopes = """["read_settings"]""",
            },
            roleIds: null);
        _identity.User = new ManagementUser(id, IsOwner: false);
        return await service.GetAsync();
    }
}
