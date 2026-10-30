using AxonHub.Api.Security;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks SecuritySettingsOrDefault / normalizeSecuritySettings
/// (internal/server/biz/system.go). Any lookup or parse failure falls back to
/// the defaults, which disable the blocklist.
/// </summary>
public sealed class SystemSettingsServiceTests
{
    [Fact]
    public async Task MissingRowReturnsDefaults()
    {
        var settings = await ReadAsync(value: null);

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task StoredValueIsParsedAndNormalized()
    {
        var settings = await ReadAsync(
            value: """
                {"blocked_ips":[" 203.0.113.0/24 ","203.0.113.0/24","","   ","198.51.100.1"],
                 "show_request_log_ip_ban_icon":false}
                """);

        Assert.Equal<string>(["203.0.113.0/24", "198.51.100.1"], settings.BlockedIps);
        Assert.False(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task MissingIconFlagDefaultsToTrue()
    {
        var settings = await ReadAsync(value: """{"blocked_ips":["10.0.0.0/8"]}""");

        Assert.Equal<string>(["10.0.0.0/8"], settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task NullBlockedIpsNormalizesToEmpty()
    {
        var settings = await ReadAsync(
            value: """{"blocked_ips":null,"show_request_log_ip_ban_icon":false}""");

        Assert.Empty(settings.BlockedIps);
        Assert.False(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task NullDocumentReturnsDefaults()
    {
        var settings = await ReadAsync(value: "null");

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task EmptyObjectReturnsDefaults()
    {
        var settings = await ReadAsync(value: "{}");

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task KeysMatchCaseInsensitivelyLikeGo()
    {
        var settings = await ReadAsync(
            value: """{"Blocked_IPs":["10.0.0.0/8"],"SHOW_REQUEST_LOG_IP_BAN_ICON":false}""");

        Assert.Equal<string>(["10.0.0.0/8"], settings.BlockedIps);
        Assert.False(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task UnknownMembersAreIgnored()
    {
        var settings = await ReadAsync(
            value: """{"blocked_ips":["10.0.0.0/8"],"future_setting":123}""");

        Assert.Equal<string>(["10.0.0.0/8"], settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task DuplicateKeysUseTheLastValueLikeGo()
    {
        var settings = await ReadAsync(
            value: """{"blocked_ips":["10.0.0.0/8"],"blocked_ips":["192.0.2.0/24"]}""");

        Assert.Equal<string>(["192.0.2.0/24"], settings.BlockedIps);
    }

    [Theory]
    [InlineData("{\"blocked_ips\":")]
    [InlineData("{\"blocked_ips\":\"10.0.0.0/8\"}")]
    [InlineData("{\"blocked_ips\":[1,2]}")]
    [InlineData("{\"show_request_log_ip_ban_icon\":\"true\"}")]
    public async Task MalformedValueReturnsDefaults(string value)
    {
        var settings = await ReadAsync(value: value);

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task StoreFailureReturnsDefaults()
    {
        var store = new FakeSystemStore { GetFailure = new InvalidOperationException("down") };

        var settings = await new SystemSettingsService(store).GetSecuritySettingsAsync();

        Assert.Empty(settings.BlockedIps);
        Assert.True(settings.ShowRequestLogIpBanIcon);
    }

    [Fact]
    public async Task SoftDeletedRowIsTreatedAsMissing()
    {
        var settings = await ReadAsync(
            value: """{"blocked_ips":["10.0.0.0/8"]}""",
            deletedAt: 1_700_000_000L);

        Assert.Empty(settings.BlockedIps);
    }

    private static async Task<SecuritySettings> ReadAsync(
        string? value,
        long deletedAt = 0)
    {
        var store = new FakeSystemStore();
        if (value is not null)
        {
            store.Add(
                SystemSettingsService.SecuritySettingsKey,
                value,
                row => row.DeletedAt = deletedAt);
        }

        return await new SystemSettingsService(store).GetSecuritySettingsAsync();
    }
}
