using System.Text.Json;
using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.SystemService's provider quota collection settings surface
/// (internal/server/biz/provider_quota_settings.go): defaults, merge-on-update,
/// validation, the strict/lenient read pair and the synchronous routing-gate
/// snapshot refresh.
/// </summary>
public sealed class ProviderQuotaCollectionSettingsServiceTests
{
    private readonly FakeSystemStore _store = new();
    private readonly ProviderQuotaSettingsCache _cache = new();

    private ProviderQuotaCollectionSettingsService NewService() => new(_store, _cache);

    [Fact]
    public async Task MissingRowReturnsTheDefaultAllEnabledSettings()
    {
        // TestSystemService_ProviderQuotaCollectionSettings_DefaultsToEnabled.
        var settings = await NewService().GetAsync();

        Assert.True(settings.Enabled);
        Assert.True(settings.Providers["codex"]);
        Assert.True(settings.Providers["antigravity"]);
        Assert.True(settings.Providers["xai_subscription"]);
        Assert.True(settings.Providers["minimax"]);
        Assert.True(settings.Providers["zhipu"]);
        Assert.Same(settings, _cache.Current);
    }

    [Fact]
    public async Task StoredPartialProviderMapKeepsUpgradedDefaultsOn()
    {
        // TestSystemService_ProviderQuotaCollectionSettings_
        // DefaultsMissingProvidersToEnabled.
        _store.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{"codex":true,"minimax":false}}""");

        var settings = await NewService().GetAsync();

        Assert.True(settings.Enabled);
        Assert.False(settings.Providers["minimax"]);
        Assert.True(settings.Providers["zhipu"]);
        Assert.True(settings.Providers["codex"]);
    }

    [Fact]
    public async Task UpdateMergesProvidersWithoutResettingTheRest()
    {
        // TestSystemService_ProviderQuotaCollectionSettings_UpdateMergesProviders.
        var service = NewService();

        await service.UpdateAsync(
            enabled: null,
            [new ProviderQuotaCollectionProviderDraft { Provider = "minimax", Enabled = false }]);

        var settings = await service.GetAsync();
        Assert.True(settings.Enabled);
        Assert.False(settings.Providers["minimax"]);
        Assert.True(settings.Providers["codex"]);
        Assert.True(settings.Providers["zhipu"]);

        // The write refreshes the routing gate's snapshot.
        Assert.False(_cache.Current.Providers["minimax"]);
    }

    [Fact]
    public async Task UpdatePersistsTheReferenceJsonShapeWithSortedProviderKeys()
    {
        var settings = NewService();

        await settings.UpdateAsync(
            enabled: true,
            [
                new ProviderQuotaCollectionProviderDraft { Provider = "zai", Enabled = false },
                new ProviderQuotaCollectionProviderDraft { Provider = "codex", Enabled = true },
            ]);

        var row = Assert.Single(_store.Rows);
        Assert.Equal(ProviderQuotaCollectionSettingsService.SystemKey, row.Key);

        using var document = JsonDocument.Parse(row.Value);
        var root = document.RootElement;
        Assert.True(root.GetProperty("enabled").GetBoolean());

        var providers = root.GetProperty("providers").EnumerateObject()
            .Select(static property => property.Name)
            .ToArray();
        Assert.Equal(
            ProviderQuotaCollectionSettingsService.SupportedProviderQuotaTypes
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray(),
            providers);
    }

    [Fact]
    public async Task UpdateCanToggleTheGlobalSwitch()
    {
        var service = NewService();

        await service.UpdateAsync(
            enabled: false,
            [new ProviderQuotaCollectionProviderDraft { Provider = "codex", Enabled = true }]);

        var settings = await service.GetAsync();
        Assert.False(settings.Enabled);
        Assert.True(settings.Providers["codex"]);
        Assert.False(await service.IsEnabledAsync("codex"));
    }

    [Fact]
    public async Task UpdateRejectsUnsupportedAndDuplicateProviders()
    {
        // TestSystemService_ProviderQuotaCollectionSettings_
        // RejectsInvalidProviderUpdates.
        var service = NewService();

        var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(
                null,
                [new ProviderQuotaCollectionProviderDraft { Provider = "unsupported" }]));
        Assert.Contains("unsupported provider quota type", unsupported.Message);

        var duplicate = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateAsync(
                null,
                [
                    new ProviderQuotaCollectionProviderDraft { Provider = "minimax", Enabled = false },
                    new ProviderQuotaCollectionProviderDraft { Provider = "minimax", Enabled = true },
                ]));
        Assert.Contains("duplicate provider quota type", duplicate.Message);

        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task IsEnabledDistinguishesRetiredProviderTypes()
    {
        // TestSystemService_IsProviderQuotaCollectionEnabled.
        var service = NewService();

        Assert.True(await service.IsEnabledAsync("minimax"));
        Assert.False(ProviderQuotaCollectionSettingsService.IsSupportedProviderQuotaType("wafer"));
        Assert.False(ProviderQuotaCollectionSettingsService.IsSupportedProviderQuotaType("typo-wafer"));
        Assert.True(ProviderQuotaCollectionSettingsService.IsSupportedProviderQuotaType("minimax"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.IsEnabledAsync("wafer"));
        Assert.Contains("unsupported provider quota type", error.Message);
    }

    [Fact]
    public async Task StoredProviderSwitchDisablesOnlyThatProvider()
    {
        _store.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{"minimax":false}}""");

        var service = NewService();

        Assert.False(await service.IsEnabledAsync("minimax"));
        Assert.True(await service.IsEnabledAsync("zhipu"));
    }

    [Fact]
    public async Task MalformedStoredJsonSurfacesTheUnmarshalWrapper()
    {
        _store.Add(ProviderQuotaCollectionSettingsService.SystemKey, "{not-json");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().GetAsync());

        Assert.StartsWith("failed to unmarshal provider quota collection settings: ", error.Message);
    }

    [Fact]
    public async Task ReadFailureKeepsTheStrictWrapperAndLenientFallback()
    {
        _store.GetFailure = new InvalidOperationException("boom");

        var strict = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().GetAsync());
        Assert.Equal(
            "failed to get provider quota collection settings: failed to get system value: boom",
            strict.Message);

        // ProviderQuotaCollectionSettingsOrDefault logs and returns defaults.
        var service = NewService();
        var settings = await service.GetOrDefaultAsync();
        Assert.True(settings.Enabled);
        Assert.True(settings.Providers["codex"]);
        Assert.Same(settings, _cache.Current);
    }

    [Fact]
    public async Task WriteFailureSurfacesTheSetSystemValueWrapper()
    {
        _store.SetFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().UpdateAsync(
                enabled: true,
                [new ProviderQuotaCollectionProviderDraft { Provider = "codex" }]));

        Assert.Equal("failed to create system setting: boom", error.Message);
    }
}
