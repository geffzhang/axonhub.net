using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/channel_model_sync_test.go: the transactional
/// merge/price-ensure/reload sequence, the provider-visible failure modes and
/// the pure merge tables. The real price materialization is covered by
/// ChannelModelPriceStoreTests; these tests assert the sync's delegation and
/// its <c>changed</c> bookkeeping through FakeChannelModelPriceStore.
/// </summary>
public sealed class ChannelModelSyncTests
{
    [Fact]
    public async Task SyncChannelModelsAutoConfiguresMissingPrices()
    {
        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Sync auto price",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["existing-model"]""",
            ManualModels = "null",
            DefaultTestModel = "existing-model",
        });

        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            Assert.Equal("https://provider.test/v1/models", request.Url);
            Assert.Equal("Bearer test-key", HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
            return ModelFetchTestResponses.Json(
                """
                {"data":[{"id":"existing-model"},{"id":"priced-model"},{"id":"no-cost-model"},{"id":"archived-model"},{"id":"missing-model"}]}
                """);
        };

        var prices = new FakeChannelModelPriceStore();
        var hook = new RecordingModelSyncReloadHook();
        var service = NewService(channels, prices, executor, hook);

        var updated = await service.SyncChannelModelsAsync(1, null, CancellationToken.None);

        string[] expected =
        [
            "existing-model",
            "priced-model",
            "no-cost-model",
            "archived-model",
            "missing-model",
        ];
        Assert.Equal(expected, updated.SupportedModels);
        Assert.Empty(updated.ManualModels);
        Assert.Equal(1, hook.Count);

        var ensured = Assert.Single(prices.EnsureCalls);
        Assert.Equal(1, ensured.ChannelId);
        Assert.Equal(expected, ensured.ModelIds);
        Assert.Equal(expected, ParseModelList(channels.Row(1)!.SupportedModels));

        // A later ensure that reports a price change marks the sync as changed
        // even when the provider list is identical.
        prices.EnsureResult = true;
        var second = await service.SyncChannelModelsAsync(1, null, CancellationToken.None);

        Assert.Equal(expected, second.SupportedModels);
        Assert.Equal(2, hook.Count);
        Assert.Equal(2, prices.EnsureCalls.Count);
    }

    [Fact]
    public async Task ClineModelSyncPreservesModelsOnDiscoveryFallback()
    {
        var existingModels = new[]
        {
            "zai/glm-5.2",
            "cline-free/glm-5.2",
            "cline-pass/deepseek-v4-flash",
        };
        var existingJson = GoJson.MarshalToString(existingModels);

        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Cline degraded sync",
            Type = "cline",
            BaseUrl = "https://api.cline.bot/api/v1",
            Status = "enabled",
            Credentials = """{"apiKeys":["test-key"]}""",
            SupportedModels = existingJson,
            ManualModels = "[]",
            DefaultTestModel = "cline-pass/deepseek-v4-flash",
        });

        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"error":"temporary outage"}""",
            502,
            "Bad Gateway");

        var prices = new FakeChannelModelPriceStore();
        var hook = new RecordingModelSyncReloadHook();
        var service = NewService(channels, prices, executor, hook);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SyncChannelModelsAsync(1, null, CancellationToken.None));

        Assert.Contains("fallback", error.Message, StringComparison.Ordinal);
        Assert.Equal(existingJson, channels.Row(1)!.SupportedModels);
        Assert.Empty(channels.SyncCalls);
        Assert.Empty(prices.EnsureCalls);
        Assert.Equal(0, hook.Count);
    }

    [Fact]
    public async Task SyncChannelModelsReportsMalformedModelColumnsLikeAReloadFailure()
    {
        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Malformed manual models",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["old-model"]""",
            ManualModels = "{not-json",
            DefaultTestModel = "old-model",
        });

        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"fetched-model"}]}""");

        var prices = new FakeChannelModelPriceStore();
        var service = NewService(
            channels,
            prices,
            executor,
            new RecordingModelSyncReloadHook());

        // Ent would reject the malformed column during the in-transaction
        // re-read; the port's decoder reports the same outer prefix (C160).
        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => service.SyncChannelModelsAsync(1, null, CancellationToken.None));

        Assert.StartsWith(
            "failed to reload channel for model sync: ",
            error.Message,
            StringComparison.Ordinal);
        Assert.Empty(prices.EnsureCalls);
        Assert.Equal("""["old-model"]""", channels.Row(1)!.SupportedModels);
    }

    [Fact]
    public async Task SyncChannelModelsAppliesThePatternOverride()
    {
        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Pattern override",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["old-model"]""",
            ManualModels = "[]",
            AutoSyncModelPattern = "^keep-.*",
            DefaultTestModel = "old-model",
        });

        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"keep-1"},{"id":"override-1"},{"id":"other"}]}""");

        var service = NewService(
            channels,
            new FakeChannelModelPriceStore(),
            executor,
            new RecordingModelSyncReloadHook());

        // xregexp.Filter anchors the whole pattern (^(?:…)$), so a bare prefix
        // needs an explicit .* suffix.
        var updated = await service.SyncChannelModelsAsync(1, "^override-.*", CancellationToken.None);

        Assert.Equal(["override-1"], updated.SupportedModels);
    }

    [Fact]
    public async Task ModelSyncIgnoresProviderOnlyOrderChanges()
    {
        var requestCount = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            ++requestCount == 1
                ? """{"data":[{"id":"model-a"},{"id":"model-b"}]}"""
                : """{"data":[{"id":"model-b"},{"id":"model-a"}]}""");

        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Order-stable sync",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["old-model"]""",
            ManualModels = "[]",
            DefaultTestModel = "old-model",
        });

        var prices = new FakeChannelModelPriceStore();
        var hook = new RecordingModelSyncReloadHook();
        var service = NewService(channels, prices, executor, hook);

        var first = await service.SyncChannelModelsAsync(1, null, CancellationToken.None);
        Assert.Equal(["model-a", "model-b"], first.SupportedModels);
        Assert.Equal(1, hook.Count);

        var second = await service.SyncChannelModelsAsync(1, null, CancellationToken.None);
        Assert.Equal(["model-a", "model-b"], second.SupportedModels);
        Assert.Equal(1, hook.Count);
        Assert.Equal(2, prices.EnsureCalls.Count);
    }

    [Fact]
    public async Task ModelSyncRemovesProtocolsForRemovedModels()
    {
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"current-model"}]}""");

        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Remove stale model protocol",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["removed-model","current-model"]""",
            ManualModels = "[]",
            DefaultTestModel = "current-model",
            Settings = """
                {"modelProtocols":[{"model":"removed-model","apiFormats":["openai/chat_completions"]},{"model":"current-model","apiFormats":["openai/chat_completions"]}]}
                """,
        });

        var service = NewService(
            channels,
            new FakeChannelModelPriceStore(),
            executor,
            new RecordingModelSyncReloadHook());

        var updated = await service.SyncChannelModelsAsync(1, null, CancellationToken.None);

        Assert.Equal(["current-model"], updated.SupportedModels);
        var updatedSettings = ChannelWriteSerialization.ParseSettings(updated.Channel.Settings);
        Assert.NotNull(updatedSettings);
        Assert.Equal("current-model", Assert.Single(updatedSettings.ModelProtocols!).Model);

        var persistedSettings = ChannelWriteSerialization.ParseSettings(channels.Row(1)!.Settings);
        Assert.NotNull(persistedSettings);
        Assert.Equal("current-model", Assert.Single(persistedSettings.ModelProtocols!).Model);
    }

    [Fact]
    public async Task PeriodicModelSyncNotifiesOnceForChangedBatch()
    {
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"batch-model"}]}""");

        var channels = new FakeChannelModelSyncStore();
        for (var i = 1; i <= 2; i++)
        {
            channels.Seed(new ChannelRecord
            {
                Id = i,
                Name = "Periodic sync " + i,
                Type = "openai",
                BaseUrl = "https://provider.test/v1",
                Status = "enabled",
                Credentials = """{"apiKey":"test-key"}""",
                SupportedModels = """["old-model"]""",
                ManualModels = "[]",
                DefaultTestModel = "old-model",
                AutoSyncSupportedModels = true,
            });
        }

        var prices = new FakeChannelModelPriceStore();
        var hook = new RecordingModelSyncReloadHook();
        var service = NewService(channels, prices, executor, hook);

        var summary = await service.SyncAllAsync(CancellationToken.None);

        Assert.Equal(new ChannelModelSyncSummary(2, 0, 2), summary);
        Assert.Equal(1, hook.Count);
        Assert.Equal([1, 2], prices.EnsureCalls.Select(call => call.ChannelId));
        Assert.All(prices.EnsureCalls, call => Assert.Equal(["batch-model"], call.ModelIds));
    }

    [Fact]
    public async Task PeriodicRunAsksTheGuardBeforeSyncing()
    {
        var channels = new FakeChannelModelSyncStore();
        var guard = new FakeChannelModelSyncGuard(result: false);
        var service = NewService(channels, new FakeChannelModelPriceStore(), guard: guard);

        await service.RunSyncChannelModelsPeriodicallyAsync(CancellationToken.None);

        // runSyncChannelModelsPeriodically reads the settings first, then asks
        // shouldRunModelSync; a closed window never reaches the sync pass.
        var call = Assert.Single(guard.Calls);
        Assert.Equal(AutoSyncFrequency.OneHour, call.Frequency);
        Assert.Empty(channels.SyncCalls);
    }

    [Fact]
    public async Task PeriodicRunSyncsWhenTheGuardAllows()
    {
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"guard-model"}]}""");

        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Guarded periodic sync",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["old-model"]""",
            ManualModels = "[]",
            DefaultTestModel = "old-model",
            AutoSyncSupportedModels = true,
        });

        var guard = new FakeChannelModelSyncGuard(result: true);
        var service = NewService(
            channels,
            new FakeChannelModelPriceStore(),
            executor,
            guard: guard);

        await service.RunSyncChannelModelsPeriodicallyAsync(CancellationToken.None);

        Assert.Single(guard.Calls);
        Assert.Equal([1], channels.SyncCalls);
    }

    [Fact]
    public async Task PeriodicRunWithoutAGuardUsesTheLocalWindow()
    {
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"local-model"}]}""");

        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Local window sync",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["old-model"]""",
            ManualModels = "[]",
            DefaultTestModel = "old-model",
            AutoSyncSupportedModels = true,
        });

        var time = new ManualTimeProvider(new DateTimeOffset(2024, 1, 1, 10, 2, 0, TimeSpan.Zero));
        var service = NewService(
            channels,
            new FakeChannelModelPriceStore(),
            executor,
            time: time);

        await service.RunSyncChannelModelsPeriodicallyAsync(CancellationToken.None);
        time.UtcNow = time.UtcNow.AddMinutes(2);
        await service.RunSyncChannelModelsPeriodicallyAsync(CancellationToken.None);

        // The direct-construction service keeps the reference's in-process
        // window: the second tick inside the hour does not sync again.
        Assert.Equal([1], channels.SyncCalls);
    }

    [Fact]
    public async Task SyncChannelModelsKeepsManualModelsSavedDuringFetch()
    {
        var channels = new FakeChannelModelSyncStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Concurrent manual model sync",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["auto-a","auto-b"]""",
            ManualModels = "[]",
            DefaultTestModel = "auto-a",
        });

        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            Assert.Equal("https://provider.test/v1/models", request.Url);

            // Simulate a user saving a manual model while the provider fetch is
            // in flight; the sync must re-read manual_models instead of
            // overwriting it with the pre-fetch snapshot.
            channels.UpdateRow(1, row =>
            {
                row.ManualModels = """["concurrent-manual"]""";
                row.SupportedModels = """["auto-a","auto-b","concurrent-manual"]""";
            });

            return ModelFetchTestResponses.Json(
                """{"data":[{"id":"auto-a"},{"id":"auto-c"}]}""");
        };

        var service = NewService(
            channels,
            new FakeChannelModelPriceStore(),
            executor,
            new RecordingModelSyncReloadHook());

        var updated = await service.SyncChannelModelsAsync(1, null, CancellationToken.None);

        Assert.Equal(3, updated.SupportedModels.Count);
        Assert.Contains("auto-a", updated.SupportedModels);
        Assert.Contains("auto-c", updated.SupportedModels);
        Assert.Contains("concurrent-manual", updated.SupportedModels);
        Assert.Equal(["concurrent-manual"], updated.ManualModels);

        var persisted = channels.Row(1)!;
        var persistedModels = ParseModelList(persisted.SupportedModels);
        Assert.Equal(3, persistedModels.Count);
        Assert.Contains("auto-a", persistedModels);
        Assert.Contains("auto-c", persistedModels);
        Assert.Contains("concurrent-manual", persistedModels);
        Assert.Equal(["concurrent-manual"], ParseModelList(persisted.ManualModels));
    }

    [Fact]
    public void MergePlanMatchesTheReference()
    {
        // The five merge tables from channel_model_sync_test.go collapse to one
        // table; BuildPlan is the port of lo.Uniq(append(manual, fetched...)).
        var cases = new (string Name, string[] Manual, string[] Fetched, string[] Expected)[]
        {
            ("manual models preserved when fetched is different", ["custom-model-1", "custom-model-2"], ["gpt-4", "gpt-3.5-turbo"], ["custom-model-1", "custom-model-2", "gpt-4", "gpt-3.5-turbo"]),
            ("manual models preserved when no overlap", ["my-custom-model"], ["claude-3-opus"], ["my-custom-model", "claude-3-opus"]),
            ("union of manual and fetched models", ["manual-model-a", "manual-model-b"], ["fetched-model-x", "fetched-model-y"], ["manual-model-a", "manual-model-b", "fetched-model-x", "fetched-model-y"]),
            ("empty manual models only fetched", [], ["gpt-4", "claude-3"], ["gpt-4", "claude-3"]),
            ("both lists have some models", ["model-1", "model-2"], ["model-3", "model-4", "model-5"], ["model-1", "model-2", "model-3", "model-4", "model-5"]),
            ("manual models remain when provider returns empty", ["important-custom-model", "another-manual-model"], [], ["important-custom-model", "another-manual-model"]),
            ("no models when both are empty", [], [], []),
            ("duplicates between manual and fetched are removed", ["gpt-4", "custom-model"], ["gpt-4", "claude-3"], ["gpt-4", "custom-model", "claude-3"]),
            ("duplicates within manual models are removed", ["model-a", "model-a", "model-b"], ["model-c"], ["model-a", "model-b", "model-c"]),
            ("duplicates within fetched models are removed", ["manual-model"], ["fetched-a", "fetched-a", "fetched-b"], ["manual-model", "fetched-a", "fetched-b"]),
            ("all unique no duplicates", ["model-1", "model-2"], ["model-3", "model-4"], ["model-1", "model-2", "model-3", "model-4"]),
            ("case sensitivity preserved - GPT-4 vs gpt-4", ["GPT-4"], ["gpt-4", "GPT-4"], ["GPT-4", "gpt-4"]),
            ("different cases are different models", ["Claude-3", "claude-3"], ["CLAUDE-3"], ["Claude-3", "claude-3", "CLAUDE-3"]),
            ("mixed case models preserved", ["MyCustomModel"], ["mycustommodel", "MYCUSTOMMODEL"], ["MyCustomModel", "mycustommodel", "MYCUSTOMMODEL"]),
        };

        foreach (var testCase in cases)
        {
            var current = new ChannelRecord
            {
                ManualModels = GoJson.MarshalToString(testCase.Manual),
                SupportedModels = "[]",
            };

            var plan = ChannelModelSyncService.BuildPlan(current, testCase.Fetched);

            if (testCase.Expected.Length == 0)
            {
                Assert.Null(plan);
                continue;
            }

            Assert.NotNull(plan);
            Assert.Equal(testCase.Expected.Length, plan.MergedModels.Count);
            Assert.Equal(
                testCase.Expected.ToHashSet(StringComparer.Ordinal),
                plan.MergedModels.ToHashSet(StringComparer.Ordinal));
            Assert.Equal(
                plan.MergedModels.Count,
                plan.MergedModels.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void ShouldRunModelSyncSkipsTheSameIntervalWindow()
    {
        var service = NewService(new FakeChannelModelSyncStore(), new FakeChannelModelPriceStore());
        var now = new DateTimeOffset(2024, 1, 1, 10, 2, 0, TimeSpan.Zero);

        Assert.True(service.ShouldRunModelSync(now, AutoSyncFrequency.OneHour));
        Assert.False(service.ShouldRunModelSync(now.AddMinutes(2), AutoSyncFrequency.OneHour));
    }

    [Fact]
    public void ShouldRunModelSyncRunsInTheNextIntervalWindow()
    {
        var service = NewService(new FakeChannelModelSyncStore(), new FakeChannelModelPriceStore());

        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 10, 2, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneHour));
        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 11, 0, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneHour));
    }

    [Fact]
    public void ShouldRunModelSyncDefaultsToHourly()
    {
        var service = NewService(new FakeChannelModelSyncStore(), new FakeChannelModelPriceStore());

        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 10, 30, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneHour));
        Assert.False(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 10, 59, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneHour));
        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 11, 0, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneHour));
    }

    [Fact]
    public void ShouldRunModelSyncUsesTheSixHourWindow()
    {
        var service = NewService(new FakeChannelModelSyncStore(), new FakeChannelModelPriceStore());

        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 10, 30, 0, TimeSpan.Zero),
            AutoSyncFrequency.SixHours));
        Assert.False(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 11, 59, 0, TimeSpan.Zero),
            AutoSyncFrequency.SixHours));
        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero),
            AutoSyncFrequency.SixHours));
    }

    [Fact]
    public void ShouldRunModelSyncUsesTheDailyWindow()
    {
        var service = NewService(new FakeChannelModelSyncStore(), new FakeChannelModelPriceStore());

        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 10, 30, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneDay));
        Assert.False(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 1, 23, 59, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneDay));
        Assert.True(service.ShouldRunModelSync(
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero),
            AutoSyncFrequency.OneDay));
    }

    private static ChannelModelSyncService NewService(
        FakeChannelModelSyncStore store,
        FakeChannelModelPriceStore prices,
        ScriptedExecutor? executor = null,
        RecordingModelSyncReloadHook? hook = null,
        TimeProvider? time = null,
        FakeChannelModelSyncGuard? guard = null)
        => new(
            store,
            prices,
            executor ?? new ScriptedExecutor(),
            time ?? TimeProvider.System,
            new FakeAutoSyncFrequencyProvider(AutoSyncFrequency.OneHour),
            hook ?? new RecordingModelSyncReloadHook(),
            guard);

    private static List<string> ParseModelList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? []
            : LlmJson.Deserialize<List<string>>(raw) ?? [];
}
