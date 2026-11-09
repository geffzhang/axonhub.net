using AxonHub.Api.Management;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the Apply table in biz/channel_settings_test.go (channel_settings.go:52-127):
/// a null patch keeps the base, a null base starts from the zero settings,
/// absent fields keep their stored values, present fields replace them, and a
/// list's nil-vs-empty distinction survives the patch boundary.
/// </summary>
public sealed class ChannelSettingsMergeTests
{
    [Fact]
    public void AnEmptyPatchKeepsTheBaseSettings()
    {
        var merged = new UpdateChannelSettingsCommand().Apply(BaseSettings());

        Assert.Equal("deepseek", merged.ExtraModelPrefix);
        Assert.Equal(new[] { "openai" }, merged.AutoTrimedModelPrefixes);
        Assert.True(merged.HideOriginalModels);
        Assert.True(merged.LowercaseModelId);
        Assert.Equal("http", merged.Proxy!.Type);
        Assert.Equal("http://proxy.example.com:8080", merged.Proxy.Url);
        Assert.Equal(60L, merged.RateLimit!.Rpm);
        Assert.Equal(new[] { 429 }, merged.RetryableStatusCodes);
    }

    [Fact]
    public void ANullBaseStartsFromTheZeroSettings()
    {
        var merged = new UpdateChannelSettingsCommand
        {
            ExtraModelPrefix = "anthropic",
        }.Apply(null);

        Assert.Equal("anthropic", merged.ExtraModelPrefix);
        Assert.Null(merged.Proxy);
        Assert.Null(merged.RateLimit);
        Assert.Null(merged.ModelMappings);
        Assert.Null(merged.AutoTrimedModelPrefixes);
        Assert.False(merged.HideOriginalModels);
        Assert.NotNull(merged.TransformOptions);
    }

    [Fact]
    public void AbsentFieldsKeepTheirStoredValues()
    {
        var merged = new UpdateChannelSettingsCommand
        {
            ExtraModelPrefix = "anthropic",
        }.Apply(BaseSettings());

        Assert.Equal("anthropic", merged.ExtraModelPrefix);
        Assert.True(merged.HideOriginalModels);
        Assert.True(merged.LowercaseModelId);
        Assert.Equal(new[] { "openai" }, merged.AutoTrimedModelPrefixes);
        Assert.Equal("http", merged.Proxy!.Type);
        Assert.Equal(60L, merged.RateLimit!.Rpm);
        Assert.Equal(new[] { 429 }, merged.RetryableStatusCodes);
    }

    [Fact]
    public void AnAbsentApiKeyPolicyKeepsTheStoredValues()
    {
        var stored = new ChannelSettingsDraft
        {
            ApiKeyStrategy = "round_robin",
            ApiKeyRoundRobinSwitchAfter = 3,
        };

        var merged = new UpdateChannelSettingsCommand
        {
            ExtraModelPrefix = "anthropic",
        }.Apply(stored);

        Assert.Equal("round_robin", merged.ApiKeyStrategy);
        Assert.Equal(3, merged.ApiKeyRoundRobinSwitchAfter);
    }

    [Fact]
    public void APresentApiKeyPolicyReplacesTheStoredValues()
    {
        var stored = new ChannelSettingsDraft
        {
            ApiKeyStrategy = "round_robin",
            ApiKeyRoundRobinSwitchAfter = 3,
        };

        var merged = new UpdateChannelSettingsCommand
        {
            ApiKeyStrategy = "priority",
            ApiKeyRoundRobinSwitchAfter = 1,
        }.Apply(stored);

        Assert.Equal("priority", merged.ApiKeyStrategy);
        Assert.Equal(1, merged.ApiKeyRoundRobinSwitchAfter);
    }

    [Fact]
    public void PresentFieldsReplaceTheStoredValues()
    {
        var patch = new UpdateChannelSettingsCommand
        {
            ExtraModelPrefix = string.Empty,
            HideOriginalModels = false,
            Proxy = new ProxyConfigDraft
            {
                Type = "socks5",
                Url = "socks5://other.example.com:1080",
            },
            RateLimit = new ChannelRateLimitDraft { Tpm = 1000 },
            QuotaRoutingMode = string.Empty,
        };

        var merged = patch.Apply(BaseSettings());

        Assert.Equal(string.Empty, merged.ExtraModelPrefix);
        Assert.False(merged.HideOriginalModels);
        Assert.Equal("socks5", merged.Proxy!.Type);
        Assert.Equal(1000L, merged.RateLimit!.Tpm);
        Assert.Null(merged.RateLimit.Rpm);
        Assert.Equal(string.Empty, merged.QuotaRoutingMode);

        // Untouched fields survive.
        Assert.True(merged.LowercaseModelId);
    }

    [Fact]
    public void AnExplicitlyEmptyListClearsAndAnAbsentListKeeps()
    {
        var patch = new UpdateChannelSettingsCommand
        {
            AutoTrimedModelPrefixes = [],
            RetryableStatusCodes = null,
        };

        var merged = patch.Apply(BaseSettings());

        Assert.NotNull(merged.AutoTrimedModelPrefixes);
        Assert.Empty(merged.AutoTrimedModelPrefixes!);
        Assert.Equal(new[] { 429 }, merged.RetryableStatusCodes);
    }

    [Fact]
    public void ApplyReturnsAFreshDraftEachCall()
    {
        var command = new UpdateChannelSettingsCommand { HideMappedModels = true };
        var baseSettings = BaseSettings();

        var first = command.Apply(baseSettings);
        var second = command.Apply(baseSettings);

        Assert.NotSame(first, second);
        Assert.NotSame(baseSettings, first);
        Assert.True(first.HideMappedModels);
        Assert.True(second.HideMappedModels);
        Assert.False(baseSettings.HideMappedModels);
    }

    private static ChannelSettingsDraft BaseSettings() => new()
    {
        ExtraModelPrefix = "deepseek",
        AutoTrimedModelPrefixes = ["openai"],
        HideOriginalModels = true,
        LowercaseModelId = true,
        Proxy = new ProxyConfigDraft
        {
            Type = "http",
            Url = "http://proxy.example.com:8080",
        },
        RateLimit = new ChannelRateLimitDraft { Rpm = 60 },
        RetryableStatusCodes = [429],
    };
}
