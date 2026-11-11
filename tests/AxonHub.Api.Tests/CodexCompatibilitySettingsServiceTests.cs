using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.SystemService's Codex compatibility settings surface
/// (internal/server/biz/system_codex.go, followed at f272549f): the zero-value
/// default, Go's JSON decode tolerances, the read/write scope split, the
/// validation errors and the Go-shaped persisted value (§160).
/// </summary>
public sealed class CodexCompatibilitySettingsServiceTests
{
    private readonly FakeSystemStore _store = new();
    private readonly FakeChannelStore _channels = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly FakeManagementIdentity _identity = new(null);

    private CodexCompatibilitySettingsService NewService()
        => new(_store, _channels, new AuthorizationScope(_identity, _adminUsers));

    private async Task<CodexCompatibilitySettingsService> SignInAsync(
        params string[] scopes)
    {
        var id = await _adminUsers.CreateAsync(
            new UserRecord
            {
                Email = "codex-settings@example.com",
                Status = "activated",
                Scopes = JsonSerializer.Serialize(scopes),
            },
            roleIds: null);
        _identity.User = new ManagementUser(id, IsOwner: false);
        return NewService();
    }

    private ChannelRecord AddCodexChannel(long id = 7) => _channels.Add(new ChannelRecord
    {
        Id = id,
        Type = "codex",
        Name = "codex-main",
        Status = "enabled",
    });

    [Fact]
    public async Task MissingRowIsTheZeroStruct()
    {
        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.False(settings.Enabled);
        Assert.Null(settings.ChannelID);
    }

    [Fact]
    public async Task AStoredNullIsAlsoTheZeroStruct()
    {
        _store.Add(CodexCompatibilitySettingsService.SystemKey, "null");

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.False(settings.Enabled);
        Assert.Null(settings.ChannelID);
    }

    [Theory]
    [InlineData("""{"enabled":true,"channelID":7}""")]
    [InlineData("""{"Enabled":true,"CHANNELID":7}""")]
    public async Task DecodingMatchesGoKeyMatching(string stored)
    {
        // encoding/json matches keys case-insensitively.
        _store.Add(CodexCompatibilitySettingsService.SystemKey, stored);

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.True(settings.Enabled);
        Assert.Equal(7, settings.ChannelID);
    }

    [Fact]
    public async Task DuplicateKeysLastWinAndANullChannelClearsIt()
    {
        // Go keeps the last value for a duplicate key; null into *int clears it
        // while null into the bool field is a no-op.
        _store.Add(
            CodexCompatibilitySettingsService.SystemKey,
            """{"enabled":true,"channelID":7,"enabled":null,"channelID":null,"channelID":9}""");

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.True(settings.Enabled);
        Assert.Equal(9, settings.ChannelID);
    }

    [Fact]
    public async Task ANullBoolFieldIsANoOp()
    {
        _store.Add(
            CodexCompatibilitySettingsService.SystemKey,
            """{"enabled":true,"channelID":null,"enabled":null}""");

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        // The trailing null does not reset the earlier true.
        Assert.True(settings.Enabled);
        Assert.Null(settings.ChannelID);
    }

    [Fact]
    public async Task ChannelIDIsStoredAsA64BitGoInt()
    {
        _store.Add(
            CodexCompatibilitySettingsService.SystemKey,
            """{"enabled":true,"channelID":9007199254740993}""");

        var settings = await (await SignInAsync("read_settings")).GetAsync();

        Assert.Equal(9_007_199_254_740_993, settings.ChannelID);
    }

    [Theory]
    [InlineData("[]", "json: cannot unmarshal array into Go value of type biz.CodexCompatibilitySettings")]
    [InlineData(
        """{"enabled":"yes"}""",
        "json: cannot unmarshal string into Go struct field CodexCompatibilitySettings.enabled of type bool")]
    [InlineData(
        """{"channelID":"7"}""",
        "json: cannot unmarshal string into Go struct field CodexCompatibilitySettings.channelID of type int")]
    [InlineData(
        """{"channelID":1.5}""",
        "json: cannot unmarshal number 1.5 into Go struct field CodexCompatibilitySettings.channelID of type int")]
    public async Task DecodeFailuresCarryTheDecodePrefix(string stored, string detail)
    {
        _store.Add(CodexCompatibilitySettingsService.SystemKey, stored);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (await SignInAsync("read_settings")).GetAsync());

        Assert.Equal("decode Codex compatibility settings: " + detail, error.Message);
    }

    [Fact]
    public async Task CorruptStoredValueFailsWithTheDecodePrefix()
    {
        _store.Add(CodexCompatibilitySettingsService.SystemKey, "{");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await (await SignInAsync("read_settings")).GetAsync());

        Assert.StartsWith("decode Codex compatibility settings: ", error.Message);
    }

    [Fact]
    public async Task ReadFailuresCarryTheOperationPrefixes()
    {
        _store.GetFailuresByKey[CodexCompatibilitySettingsService.SystemKey] =
            new InvalidOperationException("db down");

        var service = await SignInAsync("read_settings", "write_settings");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetAsync());
        Assert.Equal(
            "read Codex compatibility settings: failed to get system value: db down",
            error.Message);

        var enabledError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IsEnabledAsync());
        Assert.Equal(
            "read Codex compatibility enabled state: failed to get system value: db down",
            enabledError.Message);
    }

    [Fact]
    public async Task TheEnabledStateBypassesScopes()
    {
        // authz.RunWithSystemBypass("codex-compatibility-enabled"): the runtime
        // gate reads the flag without any principal.
        _store.Add(
            CodexCompatibilitySettingsService.SystemKey,
            """{"enabled":true,"channelID":7}""");

        _identity.User = null;
        Assert.True(await NewService().IsEnabledAsync());

        // The scoped read still fails for the same anonymous principal.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().GetAsync());
        Assert.Equal(
            "authz: principal unknown does not have required scope read_settings",
            error.Message);
    }

    [Fact]
    public async Task UpdatePersistsTheGoShapedValue()
    {
        AddCodexChannel();
        // A non-nil channelID also requires read_channels (system_codex.go:63).
        var service = await SignInAsync("read_settings", "write_settings", "read_channels");

        await service.UpdateAsync(new CodexCompatibilitySettings
        {
            Enabled = true,
            ChannelID = 7,
        });

        var row = Assert.Single(_store.Rows);
        Assert.Equal(CodexCompatibilitySettingsService.SystemKey, row.Key);
        Assert.Equal("""{"enabled":true,"channelID":7}""", row.Value);

        var settings = await NewService().GetAsync();
        Assert.True(settings.Enabled);
        Assert.Equal(7, settings.ChannelID);
    }

    [Fact]
    public async Task UpdateWithNoChannelEmitsTheNullChannelID()
    {
        // Disabling needs only write_settings; channelID has no omitempty.
        var service = await SignInAsync("write_settings");

        await service.UpdateAsync(new CodexCompatibilitySettings { Enabled = false });

        Assert.Equal(
            """{"enabled":false,"channelID":null}""",
            Assert.Single(_store.Rows).Value);
    }

    [Fact]
    public async Task EnabledWithoutAChannelIsAValidationError()
    {
        var service = await SignInAsync("write_settings");

        var error = await Assert.ThrowsAsync<CodedException>(
            () => service.UpdateAsync(new CodexCompatibilitySettings { Enabled = true }));

        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Equal("a Codex channel is required when enabled", error.Message);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task AMissingChannelIsAValidationError()
    {
        // The channel lookup itself needs read_channels before it can miss.
        var service = await SignInAsync("read_settings", "write_settings", "read_channels");

        var error = await Assert.ThrowsAsync<CodedException>(
            () => service.UpdateAsync(new CodexCompatibilitySettings
            {
                Enabled = true,
                ChannelID = 404,
            }));

        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Equal(
            "Codex catalog channel is unavailable or inaccessible",
            error.Message);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task ANonCodexChannelIsAValidationError()
    {
        _channels.Add(new ChannelRecord
        {
            Id = 3,
            Type = "openai",
            Name = "openai",
            Status = "enabled",
        });
        var service = await SignInAsync("read_settings", "write_settings", "read_channels");

        var error = await Assert.ThrowsAsync<CodedException>(
            () => service.UpdateAsync(new CodexCompatibilitySettings
            {
                Enabled = true,
                ChannelID = 3,
            }));

        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Equal("catalog channel must be Codex", error.Message);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task AChannelIDRequiresTheReadChannelsScope()
    {
        AddCodexChannel();
        var service = await SignInAsync("read_settings", "write_settings");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(new CodexCompatibilitySettings
            {
                Enabled = true,
                ChannelID = 7,
            }));

        Assert.Contains("does not have required scope read_channels", error.Message);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task WriteFailuresCarryTheCreateSettingPrefix()
    {
        _store.SetFailuresByKey[CodexCompatibilitySettingsService.SystemKey] =
            new InvalidOperationException("write rejected");
        var service = await SignInAsync("write_settings");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(new CodexCompatibilitySettings { Enabled = false }));

        Assert.Equal("failed to create system setting: write rejected", error.Message);
    }

    [Fact]
    public async Task ScopesMatchTheReference()
    {
        var cases = new (string[] Scopes, bool Read, bool Write)[]
        {
            ([], false, false),
            (["read_settings"], true, false),
            (["write_settings"], false, true),
        };

        foreach (var (scopes, canRead, canWrite) in cases)
        {
            var store = new FakeSystemStore();
            var channels = new FakeChannelStore();
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
            var service = new CodexCompatibilitySettingsService(
                store,
                channels,
                new AuthorizationScope(identity, users));

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
                await service.UpdateAsync(new CodexCompatibilitySettings { Enabled = false });
            }
            else
            {
                var updateError = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => service.UpdateAsync(
                        new CodexCompatibilitySettings { Enabled = false }));
                Assert.Contains(
                    "does not have required scope write_settings",
                    updateError.Message);
            }
        }
    }
}
