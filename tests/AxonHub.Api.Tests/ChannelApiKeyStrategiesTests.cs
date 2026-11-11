using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/biz/channel_apikey_strategies_test.go and the
/// provider-level state tests: the multi-key strategy names, dispatch, the
/// sticky/round-robin/shared-state rules, the fixed and round_robin_success
/// cursors across channel rebuilds, the success-report wiring, and the factory's
/// decision to activate the configured policy.
/// </summary>
/// <remarks>
/// The reference tests drive real providers whose <c>Get</c> runs the selection.
/// The port resolves one key per attempt at provider-build time, so one
/// <see cref="Get"/> call here stands for one provider built from the given
/// channel generation, exactly like the reference's <c>Get</c>.
/// </remarks>
public sealed class ChannelApiKeyStrategiesTests
{
    private const long ChannelId = 1;

    private static readonly string[] ThreeKeys = ["k1", "k2", "k3"];

    private static ChannelSettingsDraft Settings(string? strategy, int? switchAfter = null)
        => new()
        {
            ApiKeyStrategy = strategy,
            ApiKeyRoundRobinSwitchAfter = switchAfter,
        };

    /// <summary>
    /// One provider build plus its single <c>Get</c> (channel_llm.go:169-184):
    /// the enabled set comes from the credentials minus the disabled keys, the
    /// configured list stays the full <c>apiKeys</c> array.
    /// </summary>
    private static string Get(
        ChannelApiKeySelectionRegistry registry,
        ChannelSettingsDraft? settings,
        IReadOnlyList<string> allKeys,
        IReadOnlyList<string>? disabledKeys = null,
        string? traceId = null,
        long channelId = ChannelId)
    {
        var disabled = (disabledKeys ?? []).ToHashSet(StringComparer.Ordinal);
        var enabled = allKeys.Where(key => !disabled.Contains(key)).ToArray();
        var state = registry.StateFor(
            channelId,
            ChannelApiKeyStrategies.StrategyOf(settings),
            ChannelApiKeyStrategies.SwitchAfterOf(settings));

        return ChannelApiKeyStrategies.Select(
            settings,
            state,
            enabled,
            allKeys,
            allKeys,
            disabled,
            traceId);
    }

    /// <summary>Port of publishAPIKeySelectionSnapshot for the test's generation.</summary>
    private static void Publish(
        ChannelApiKeySelectionRegistry registry,
        ChannelSettingsDraft? settings,
        IReadOnlyList<string> allKeys,
        IReadOnlyList<string>? disabledKeys = null,
        long channelId = ChannelId)
        => registry.Publish(
            channelId,
            ChannelApiKeyStrategies.StrategyOf(settings),
            ChannelApiKeyStrategies.SwitchAfterOf(settings),
            ChannelApiKeyStrategies.SnapshotOf(
                allKeys,
                (disabledKeys ?? []).ToHashSet(StringComparer.Ordinal)));

    /// <summary>
    /// Port of the reference test's setFixedCursor: walk the cursor onto
    /// <paramref name="key"/> by serving a generation whose preceding keys are
    /// disabled, then restore the real generation as the served one.
    /// </summary>
    private static void PlaceFixedCursor(
        ChannelApiKeySelectionRegistry registry,
        ChannelSettingsDraft settings,
        string[] allKeys,
        string key)
    {
        var index = Array.IndexOf(allKeys, key);
        Assert.True(index >= 0, "the cursor key must be one of the channel's keys");

        if (index > 0)
        {
            var blockers = allKeys[..index];
            Publish(registry, settings, allKeys, blockers);
            Assert.Equal(key, Get(registry, settings, allKeys, blockers));
        }

        Publish(registry, settings, allKeys);
        Assert.Equal(key, Get(registry, settings, allKeys));
    }

    /// <summary>
    /// Port of setRoundRobinSuccessCursor: place the cursor, then count
    /// <paramref name="successes"/> successes on it (the caller keeps the
    /// threshold above that count so the cursor does not move).
    /// </summary>
    private static void PlaceRoundRobinSuccessCursor(
        ChannelApiKeySelectionRegistry registry,
        ChannelSettingsDraft settings,
        string[] allKeys,
        string key,
        int successes = 0)
    {
        var index = Array.IndexOf(allKeys, key);
        Assert.True(index >= 0, "the cursor key must be one of the channel's keys");

        if (index > 0)
        {
            var blockers = allKeys[..index];
            Publish(registry, settings, allKeys, blockers);
            Assert.Equal(key, Get(registry, settings, allKeys, blockers));
        }

        Publish(registry, settings, allKeys);
        Assert.Equal(key, Get(registry, settings, allKeys));

        for (var count = 0; count < successes; count++)
        {
            registry.OnApiKeySuccess(ChannelId, key);
        }

        Assert.Equal(key, Get(registry, settings, allKeys));
    }

    // --- A. dispatch ---

    [Fact]
    public void MissingEmptyAndUnknownStrategiesFallBackToSticky()
    {
        var expected = ChannelApiKeyProvider.RendezvousSelect(ThreeKeys, "trace-dispatch");

        ChannelSettingsDraft?[] settings =
        [
            null,
            Settings(""),
            Settings(ChannelApiKeyStrategies.Sticky),
            Settings("unknown_value"),
        ];

        foreach (var candidate in settings)
        {
            var registry = new ChannelApiKeySelectionRegistry();

            Assert.Equal(expected, Get(registry, candidate, ThreeKeys, traceId: "trace-dispatch"));
        }
    }

    [Fact]
    public void DispatchUsesTheConfiguredStrategy()
    {
        // random: every pick is selectable, and the picks are spread (never all
        // one key over 60 draws: that would fail with probability 2 * 2^-60).
        var randomRegistry = new ChannelApiKeySelectionRegistry();
        var randomPicks = Enumerable.Range(0, 60)
            .Select(_ => Get(randomRegistry, Settings(ChannelApiKeyStrategies.Random), ThreeKeys))
            .ToArray();
        Assert.All(randomPicks, key => Assert.Contains(key, ThreeKeys));
        Assert.True(randomPicks.Distinct(StringComparer.Ordinal).Count() > 1);

        // round_robin: one request per key, then wrap.
        var roundRobinRegistry = new ChannelApiKeySelectionRegistry();
        var roundRobin = Settings(ChannelApiKeyStrategies.RoundRobin, 1);
        Assert.Equal("k1", Get(roundRobinRegistry, roundRobin, ThreeKeys));
        Assert.Equal("k2", Get(roundRobinRegistry, roundRobin, ThreeKeys));
        Assert.Equal("k3", Get(roundRobinRegistry, roundRobin, ThreeKeys));
        Assert.Equal("k1", Get(roundRobinRegistry, roundRobin, ThreeKeys));

        // round_robin_success: Get alone never moves the cursor.
        var successRegistry = new ChannelApiKeySelectionRegistry();
        var roundRobinSuccess = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        Publish(successRegistry, roundRobinSuccess, ThreeKeys);
        for (var call = 0; call < 10; call++)
        {
            Assert.Equal("k1", Get(successRegistry, roundRobinSuccess, ThreeKeys));
        }

        // priority: always the first selectable key.
        Assert.Equal("k1", Get(new ChannelApiKeySelectionRegistry(), Settings(ChannelApiKeyStrategies.Priority), ThreeKeys));

        // fixed: starts at the first selectable key and stays there.
        var fixedRegistry = new ChannelApiKeySelectionRegistry();
        var fixedSettings = Settings(ChannelApiKeyStrategies.Fixed);
        Publish(fixedRegistry, fixedSettings, ThreeKeys);
        Assert.Equal("k1", Get(fixedRegistry, fixedSettings, ThreeKeys));
        Assert.Equal("k1", Get(fixedRegistry, fixedSettings, ThreeKeys));
    }

    [Theory]
    [InlineData("")]
    [InlineData(ChannelApiKeyStrategies.Sticky)]
    [InlineData(ChannelApiKeyStrategies.RoundRobin)]
    [InlineData(ChannelApiKeyStrategies.RoundRobinSuccess)]
    [InlineData(ChannelApiKeyStrategies.Fixed)]
    public void RememberingStrategiesKeepSelectionState(string strategy)
        => Assert.True(ChannelApiKeyStrategies.KeepsSelectionState(strategy));

    [Theory]
    [InlineData(ChannelApiKeyStrategies.Random)]
    [InlineData(ChannelApiKeyStrategies.Priority)]
    public void StatelessStrategiesDoNotKeepSelectionState(string strategy)
        => Assert.False(ChannelApiKeyStrategies.KeepsSelectionState(strategy));

    [Fact]
    public void RegistryRegistersOnlyRememberingStrategies()
    {
        var registry = new ChannelApiKeySelectionRegistry();

        string[] remembering =
        [
            "",
            ChannelApiKeyStrategies.Sticky,
            ChannelApiKeyStrategies.RoundRobin,
            ChannelApiKeyStrategies.RoundRobinSuccess,
            ChannelApiKeyStrategies.Fixed,
        ];

        for (var index = 0; index < remembering.Length; index++)
        {
            Assert.NotNull(registry.StateFor(index + 1, remembering[index], 1));
        }

        string[] stateless =
        [
            ChannelApiKeyStrategies.Random,
            ChannelApiKeyStrategies.Priority,
        ];

        for (var index = 0; index < stateless.Length; index++)
        {
            Assert.Null(registry.StateFor(100 + index, stateless[index], 1));
        }
    }

    // --- B. priority ---

    [Fact]
    public void PriorityAlwaysReturnsTheFirstSelectableKey()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Priority);
        var keys = new[] { "first", "second", "third" };

        for (var call = 0; call < 10; call++)
        {
            Assert.Equal("first", Get(registry, settings, keys));
        }
    }

    [Fact]
    public void PrioritySkipsDisabledLeadingKeys()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Priority);
        var keys = new[] { "first", "second", "third" };

        Assert.Equal("third", Get(registry, settings, keys, ["first", "second"]));
    }

    [Fact]
    public void PriorityAllDisabledFallsBackToTheFirstKey()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Priority);
        var keys = new[] { "first", "second" };

        // Every key is disabled, so the enabled snapshot is empty and the
        // provider falls back to the channel's first key. A channel in this
        // state is out of service, so the selection is never actually used.
        Assert.Equal("first", Get(registry, settings, keys, ["first", "second"]));
    }

    // --- C. fixed ---

    [Fact]
    public void FixedStartsAtTheFirstSelectableKey()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3" };
        Publish(registry, settings, keys);

        Assert.Equal("k1", Get(registry, settings, keys));
    }

    [Fact]
    public void FixedStartsAtFirstSelectableSkippingDisabled()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3" };
        Publish(registry, settings, keys, ["k1"]);

        Assert.Equal("k2", Get(registry, settings, keys, ["k1"]));
    }

    [Fact]
    public void FixedKeepsTheCurrentKeyWhileSelectable()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3" };
        PlaceFixedCursor(registry, settings, keys, "k2");

        for (var call = 0; call < 20; call++)
        {
            Assert.Equal("k2", Get(registry, settings, keys));
        }
    }

    [Fact]
    public void FixedMovesForwardWhenTheCurrentKeyIsDisabled()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3", "k4" };
        PlaceFixedCursor(registry, settings, keys, "k2");

        // k2 gets disabled and the channel is rebuilt: the walk continues from
        // k2's position, never back at k1.
        Assert.Equal("k3", Get(registry, settings, keys, ["k2"]));
    }

    [Fact]
    public void FixedSkipsSeveralDisabledKeysOnTheWay()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3", "k4", "k5" };
        PlaceFixedCursor(registry, settings, keys, "k2");

        Assert.Equal("k5", Get(registry, settings, keys, ["k2", "k3", "k4"]));
    }

    [Fact]
    public void FixedWrapsAroundToTheFirstSelectable()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3" };
        PlaceFixedCursor(registry, settings, keys, "k3");

        // The tail is disabled, so the walk wraps around and lands on k1.
        Assert.Equal("k1", Get(registry, settings, keys, ["k3"]));
    }

    [Fact]
    public void FixedRemovedKeyContinuesForward()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var original = new[] { "k1", "k2", "k3", "k4" };
        PlaceFixedCursor(registry, settings, original, "k3");

        // k3 is deleted: the walk continues from where it used to sit, so k4 is
        // next rather than restarting at k1.
        Assert.Equal("k4", Get(registry, settings, ["k1", "k2", "k4"]));
    }

    [Fact]
    public void FixedRemovedKeyOutOfRangeWrapsAround()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var original = new[] { "k1", "k2", "k3", "k4" };
        PlaceFixedCursor(registry, settings, original, "k4");

        // The array shrank below the remembered position, so the walk restarts
        // at the head of the array instead of reading past the end.
        Assert.Equal("k1", Get(registry, settings, ["k1", "k2"]));
    }

    [Fact]
    public void FixedRemovedKeyThenSkipsDisabledTail()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var original = new[] { "k1", "k2", "k3", "k4" };
        PlaceFixedCursor(registry, settings, original, "k3");

        // k3 was removed and the key that took its place is disabled: the walk
        // keeps going and wraps around to k1.
        Assert.Equal("k1", Get(registry, settings, ["k1", "k2", "k4"], ["k4"]));
    }

    [Fact]
    public void FixedAllDisabledFallsBackToTheFirstKey()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2" };
        PlaceFixedCursor(registry, settings, keys, "k2");

        Assert.Equal("k1", Get(registry, settings, keys, ["k1", "k2"]));
    }

    [Fact]
    public void FixedUsesItsOwnSnapshot()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2", "k3" };
        Publish(registry, settings, keys);
        Assert.Equal("k1", Get(registry, settings, keys));

        // A rebuild must not make an in-flight provider pick a key that belongs
        // to a different channel generation than the endpoint it calls.
        Publish(registry, settings, keys, ["k1"]);

        Assert.Equal("k1", Get(registry, settings, keys));
        Assert.Equal("k2", Get(registry, settings, keys, ["k1"]));
    }

    [Fact]
    public void FixedStaleProviderDoesNotRewindCursor()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var original = new[] { "k1", "k2" };
        PlaceFixedCursor(registry, settings, original, "k2");

        // Rebuilt with k3 appended and k2 disabled, and served from now on.
        var rebuiltKeys = new[] { "k1", "k2", "k3" };
        Publish(registry, settings, rebuiltKeys, ["k2"]);

        Assert.Equal("k3", Get(registry, settings, rebuiltKeys, ["k2"]));
        Assert.Equal("k1", Get(registry, settings, original));
        Assert.Equal("k3", Get(registry, settings, rebuiltKeys, ["k2"]));
    }

    [Fact]
    public void FixedKeepsKeyWhenTheArrayShifts()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        PlaceFixedCursor(registry, settings, ["k1", "k2", "k3"], "k2");

        // A new key is prepended: positions shift, but the remembered key is
        // still selectable, so it keeps being used.
        Assert.Equal("k2", Get(registry, settings, ["k0", "k1", "k2", "k3"]));
    }

    [Fact]
    public void FixedForgetsStateOnChannelDeletion()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        var keys = new[] { "k1", "k2" };
        PlaceFixedCursor(registry, settings, keys, "k2");
        Assert.Equal("k2", Get(registry, settings, keys));

        registry.Forget(ChannelId);

        // A fresh state has no cursor: the deleted channel left nothing behind.
        Assert.Equal("k1", Get(registry, settings, keys));
    }

    // --- D. round_robin_success ---

    [Fact]
    public void RoundRobinSuccessGetDoesNotAdvance()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 2);
        Publish(registry, settings, ThreeKeys);

        for (var call = 0; call < 10; call++)
        {
            Assert.Equal("k1", Get(registry, settings, ThreeKeys));
        }
    }

    [Fact]
    public void RoundRobinSuccessAdvancesAfterSuccessfulCalls()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 2);
        Publish(registry, settings, ThreeKeys);

        Assert.Equal("k1", Get(registry, settings, ThreeKeys));

        // One success is not enough for switchAfter=2.
        registry.OnApiKeySuccess(ChannelId, "k1");
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));

        // The second success moves the cursor on.
        registry.OnApiKeySuccess(ChannelId, "k1");
        Assert.Equal("k2", Get(registry, settings, ThreeKeys));

        registry.OnApiKeySuccess(ChannelId, "k2");
        Assert.Equal("k2", Get(registry, settings, ThreeKeys));
        registry.OnApiKeySuccess(ChannelId, "k2");
        Assert.Equal("k3", Get(registry, settings, ThreeKeys));
    }

    [Fact]
    public void RoundRobinSuccessSwitchAfterOne()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        var keys = new[] { "k1", "k2" };
        Publish(registry, settings, keys);

        Assert.Equal("k1", Get(registry, settings, keys));
        registry.OnApiKeySuccess(ChannelId, "k1");
        Assert.Equal("k2", Get(registry, settings, keys));
        registry.OnApiKeySuccess(ChannelId, "k2");
        Assert.Equal("k1", Get(registry, settings, keys));
    }

    [Fact]
    public void RoundRobinSuccessSwitchAfterBelowOneIsTreatedAsOne()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 0);
        var keys = new[] { "k1", "k2" };
        Publish(registry, settings, keys);

        Assert.Equal("k1", Get(registry, settings, keys));
        registry.OnApiKeySuccess(ChannelId, "k1");
        Assert.Equal("k2", Get(registry, settings, keys));
    }

    [Fact]
    public void RoundRobinSuccessIgnoresReportsForOtherKeys()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        var keys = new[] { "k1", "k2" };
        Publish(registry, settings, keys);
        Assert.Equal("k1", Get(registry, settings, keys));

        // A late report for a key the cursor never pointed at must not advance
        // it, and the empty key is ignored outright.
        registry.OnApiKeySuccess(ChannelId, "k2");
        registry.OnApiKeySuccess(ChannelId, "");

        Assert.Equal("k1", Get(registry, settings, keys));
    }

    [Fact]
    public void RoundRobinSuccessIgnoresOtherStrategies()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 5);
        Publish(registry, settings, ThreeKeys);
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));

        // Flip the channel to a strategy that does not count successes.
        var fixedSettings = Settings(ChannelApiKeyStrategies.Fixed, 5);
        Assert.NotNull(registry.StateFor(
            ChannelId,
            ChannelApiKeyStrategies.StrategyOf(fixedSettings),
            ChannelApiKeyStrategies.SwitchAfterOf(fixedSettings)));

        registry.OnApiKeySuccess(ChannelId, "k1");

        // Flip back: the ignored report must not have counted. Four more
        // successes stay below the threshold; a fifth advances.
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));
        for (var count = 0; count < 4; count++)
        {
            registry.OnApiKeySuccess(ChannelId, "k1");
        }

        Assert.Equal("k1", Get(registry, settings, ThreeKeys));

        registry.OnApiKeySuccess(ChannelId, "k1");
        Assert.Equal("k2", Get(registry, settings, ThreeKeys));
    }

    [Fact]
    public void RoundRobinSuccessUnknownChannelIsIgnored()
    {
        var registry = new ChannelApiKeySelectionRegistry();

        // No state registered for this channel: the report is a silent no-op.
        var exception = Record.Exception(() => registry.OnApiKeySuccess(42, "k1"));

        Assert.Null(exception);
    }

    [Fact]
    public void RoundRobinSuccessResetsWhenCursorKeyIsDisabled()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 5);
        PlaceRoundRobinSuccessCursor(registry, settings, ThreeKeys, "k1", successes: 3);

        // k1 is disabled: the cursor moves on and the successes counted for k1
        // are discarded. The rebuilt generation is the served one, so its
        // provider owns the cursor.
        Publish(registry, settings, ThreeKeys, ["k1"]);
        Assert.Equal("k2", Get(registry, settings, ThreeKeys, ["k1"]));

        // Two successes on k2 stay below the threshold only if the old count
        // was discarded; 3 + 2 would have reached five and moved on to k3.
        registry.OnApiKeySuccess(ChannelId, "k2");
        registry.OnApiKeySuccess(ChannelId, "k2");

        Assert.Equal("k2", Get(registry, settings, ThreeKeys, ["k1"]));
    }

    [Fact]
    public void RoundRobinSuccessRemovedCursorKeyContinuesForward()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        PlaceRoundRobinSuccessCursor(registry, settings, ["k1", "k2", "k3", "k4"], "k3");

        Assert.Equal("k4", Get(registry, settings, ["k1", "k2", "k4"]));
    }

    [Fact]
    public void RoundRobinSuccessUsesItsOwnSnapshot()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        Publish(registry, settings, ThreeKeys);
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));

        // Same as the fixed strategy: an in-flight provider must not be steered
        // onto another channel generation by a rebuild.
        Publish(registry, settings, ThreeKeys, ["k1"]);

        Assert.Equal("k1", Get(registry, settings, ThreeKeys));
        Assert.Equal("k2", Get(registry, settings, ThreeKeys, ["k1"]));
    }

    [Fact]
    public void RoundRobinSuccessStaleProviderDoesNotRewindCursor()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        PlaceRoundRobinSuccessCursor(registry, settings, ["k1", "k2"], "k2");

        // Rebuilt with k3 appended and k2 disabled; the rebuild is served.
        var rebuiltKeys = new[] { "k1", "k2", "k3" };
        Publish(registry, settings, rebuiltKeys, ["k2"]);

        Assert.Equal("k3", Get(registry, settings, rebuiltKeys, ["k2"]));

        // The stale generation knows nothing about k3. It answers from its own
        // snapshot without dragging the shared cursor back.
        Assert.Equal("k1", Get(registry, settings, ["k1", "k2"]));
        Assert.Equal("k3", Get(registry, settings, rebuiltKeys, ["k2"]));
    }

    [Fact]
    public void RoundRobinSuccessAdvancesPastDisabledKeys()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        var keys = new[] { "k1", "k2", "k3", "k4" };
        Publish(registry, settings, keys);
        Assert.Equal("k1", Get(registry, settings, keys));

        // k2 and k3 are disabled before k1 finishes its turn: the cursor must
        // land on k4 rather than on a disabled key. The success report reads
        // the published snapshot, so the rebuild has to be published.
        Publish(registry, settings, keys, ["k2", "k3"]);
        registry.OnApiKeySuccess(ChannelId, "k1");

        Assert.Equal("k4", Get(registry, settings, keys, ["k2", "k3"]));
    }

    // --- E. RecordPerformance wiring ---

    private static ChannelMetricsMiddleware MetricsMiddleware(
        ChannelApiKeySelectionRegistry registry,
        ChannelPerformanceRecord record)
        => new(
            RateLimitTestData.EmptyOutbound(),
            new RecordingMetricsRecorder(),
            new ChannelPerformanceState { Current = record },
            TimeProvider.System,
            registry);

    private static ChannelPerformanceRecord PerformanceRecord(string apiKey, long channelId = ChannelId)
        => new()
        {
            ChannelId = channelId,
            StartTime = DateTimeOffset.UnixEpoch,
            ApiKey = apiKey,
        };

    [Fact]
    public void SuccessReportAdvancesRoundRobinSuccessCursor()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        var keys = new[] { "k1", "k2" };
        Publish(registry, settings, keys);
        Assert.Equal("k1", Get(registry, settings, keys));

        // A successful request reported through the normal metrics path is the
        // only thing that advances the cursor.
        MetricsMiddleware(registry, PerformanceRecord("k1"))
            .TransformOutboundLlmResponse(new LlmResponse());

        Assert.Equal("k2", Get(registry, settings, keys));
    }

    [Fact]
    public void FailedReportDoesNotAdvanceRoundRobinSuccess()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        var keys = new[] { "k1", "k2" };
        Publish(registry, settings, keys);
        Assert.Equal("k1", Get(registry, settings, keys));

        MetricsMiddleware(registry, PerformanceRecord("k1"))
            .OnOutboundRawError(new InvalidOperationException("upstream failed"));

        Assert.Equal("k1", Get(registry, settings, keys));
    }

    [Fact]
    public void SuccessReportDoesNotTouchOtherStrategies()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed, 1);
        var keys = new[] { "k1", "k2" };
        Publish(registry, settings, keys);
        Assert.Equal("k1", Get(registry, settings, keys));

        // A fixed channel has no success counter; the report is a no-op.
        var exception = Record.Exception(() => MetricsMiddleware(registry, PerformanceRecord("k1"))
            .TransformOutboundLlmResponse(new LlmResponse()));

        Assert.Null(exception);
        Assert.Equal("k1", Get(registry, settings, keys));
    }

    // --- F. concurrency ---

    [Fact]
    public void FixedConcurrentGetsAreConsistent()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Fixed);
        Publish(registry, settings, ThreeKeys);
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));

        var picks = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Parallel.For(0, 32, _ =>
        {
            for (var call = 0; call < 50; call++)
            {
                picks.Enqueue(Get(registry, settings, ThreeKeys));
            }
        });

        Assert.All(picks, key => Assert.Equal("k1", key));
    }

    [Fact]
    public void RoundRobinSuccessConcurrentGetAndSuccessStayConsistent()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 2);
        Publish(registry, settings, ThreeKeys);

        Parallel.For(0, 16, _ =>
        {
            for (var call = 0; call < 50; call++)
            {
                registry.OnApiKeySuccess(ChannelId, Get(registry, settings, ThreeKeys));
            }
        });

        // The cursor must still point at a selectable key of the channel.
        Assert.Contains(Get(registry, settings, ThreeKeys), ThreeKeys);
    }

    // --- G. round_robin & sticky keep their state across snapshot rebuilds ---

    [Fact]
    public void RoundRobinRotatesAcrossRequests()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobin, 1);

        Assert.Equal("k1", Get(registry, settings, ThreeKeys));
        Assert.Equal("k2", Get(registry, settings, ThreeKeys));
        Assert.Equal("k3", Get(registry, settings, ThreeKeys));
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));
    }

    [Fact]
    public void RoundRobinSwitchAfterN()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobin, 2);
        var keys = new[] { "k1", "k2" };

        Assert.Equal("k1", Get(registry, settings, keys));
        Assert.Equal("k1", Get(registry, settings, keys));
        Assert.Equal("k2", Get(registry, settings, keys));
        Assert.Equal("k2", Get(registry, settings, keys));
        Assert.Equal("k1", Get(registry, settings, keys));
    }

    [Fact]
    public void RoundRobinKeepsRotatingAcrossSnapshotRebuild()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.RoundRobin, 1);

        Assert.Equal("k1", Get(registry, settings, ThreeKeys));
        Assert.Equal("k2", Get(registry, settings, ThreeKeys));

        // A rebuild (any channel change does this) hands out a fresh provider:
        // the rotation must continue with k3, not restart at k1.
        Assert.Equal("k3", Get(registry, settings, ThreeKeys));
        Assert.Equal("k1", Get(registry, settings, ThreeKeys));
    }

    [Fact]
    public void StickyKeepsSessionAcrossSnapshotRebuild()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Sticky);

        var first = Get(registry, settings, ThreeKeys, traceId: "trace-1");
        Assert.Equal(ChannelApiKeyProvider.RendezvousSelect(ThreeKeys, "trace-1"), first);

        // A rebuild must not move an ongoing session onto another key.
        Assert.Equal(first, Get(registry, settings, ThreeKeys, traceId: "trace-1"));
    }

    [Fact]
    public void StickyMovesOffDisabledKeyAfterRebuild()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Sticky);

        var first = Get(registry, settings, ThreeKeys, traceId: "trace-pinned");

        // Rebuild with the key the trace is pinned to now disabled, as the
        // auto-disable rules would.
        var after = Get(registry, settings, ThreeKeys, [first], traceId: "trace-pinned");

        Assert.NotEqual(first, after);
        Assert.Contains(after, ThreeKeys);
    }

    [Fact]
    public void StickySameTraceStaysOnItsKey()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var settings = Settings(ChannelApiKeyStrategies.Sticky);

        var first = Get(registry, settings, ThreeKeys, traceId: "trace-a");

        for (var call = 0; call < 10; call++)
        {
            Assert.Equal(first, Get(registry, settings, ThreeKeys, traceId: "trace-a"));
        }
    }

    // --- H. enabled-channel publication & factory dispatch ---

    [Fact]
    public void PublishingTheEnabledGenerationFeedsTheSuccessReport()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        ChannelApiKeyStrategies.Publish(
            registry,
            TestChannels.OpenAi(31, "strategy-model") with
            {
                Credentials = """{"apiKeys":["k1","k2","k3"]}""",
                DisabledApiKeys = """[{"key":"k2"}]""",
                Settings = """{"apiKeyStrategy":"round_robin_success","apiKeyRoundRobinSwitchAfter":1}""",
            });

        var settings = Settings(ChannelApiKeyStrategies.RoundRobinSuccess, 1);
        var keys = new[] { "k1", "k2", "k3" };
        var disabled = new[] { "k2" };

        Assert.Equal("k1", Get(registry, settings, keys, disabled, channelId: 31));

        // The report walks the published generation, skipping k2.
        registry.OnApiKeySuccess(31, "k1");

        Assert.Equal("k3", Get(registry, settings, keys, disabled, channelId: 31));
    }

    private static LlmRequest ChatRequest(string? sessionId = null)
        => new()
        {
            Model = "strategy-model",
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            RawRequest = sessionId is null ? null : new HttpRequestData { SessionId = sessionId },
        };

    private static ChannelCandidate Candidate(
        long id,
        string credentials,
        string? settings = null,
        string? overrideApiKey = null,
        string? disabledApiKeys = null,
        string type = ChannelOutboundFactory.OpenAiChannelType,
        string baseUrl = "https://upstream.test/v1")
        => new()
        {
            ChannelId = id,
            ChannelName = "strategy-" + id,
            ActualModel = "strategy-model",
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials,
            DisabledApiKeys = disabledApiKeys,
            Settings = settings,
            ApiKeyOverride = overrideApiKey,
        };

    /// <summary>Build an outbound and read the key its request actually carries.</summary>
    private static (string Key, int? Index) SentKey(
        ChannelCandidate candidate,
        LlmRequest request,
        ChannelApiKeySelectionRegistry? registry = null)
    {
        var outbound = ChannelOutboundFactory.Create(
            candidate,
            request,
            DateTimeOffset.UnixEpoch,
            apiKeySelection: registry);
        outbound.TransformRequest(request);

        var provenance = Assert.IsAssignableFrom<IApiKeyProvenance>(outbound);
        Assert.NotNull(provenance.SelectedApiKey);

        return (provenance.SelectedApiKey!, provenance.SelectedApiKeyIndex);
    }

    [Fact]
    public void FactoryDispatchesMultiKeyChannelsOnTheStrategy()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var credentials = """{"apiKeys":["sk-a","sk-b"]}""";
        var settings = """{"apiKeyStrategy":"round_robin"}""";

        var first = SentKey(Candidate(7201, credentials, settings), ChatRequest(), registry);
        var second = SentKey(Candidate(7201, credentials, settings), ChatRequest(), registry);
        var third = SentKey(Candidate(7201, credentials, settings), ChatRequest(), registry);

        Assert.Equal("sk-a", first.Key);
        Assert.Equal(1, first.Index);
        Assert.Equal("sk-b", second.Key);
        Assert.Equal(2, second.Index);
        Assert.Equal("sk-a", third.Key);
    }

    [Fact]
    public void FactoryKeepsSingleKeyChannelsStatic()
    {
        // One key: no strategy dispatch, even when the channel configures one.
        var registry = new ChannelApiKeySelectionRegistry();
        var credentials = """{"apiKey":"sk-solo"}""";
        var settings = """{"apiKeyStrategy":"round_robin"}""";

        for (var call = 0; call < 3; call++)
        {
            Assert.Equal(
                "sk-solo",
                SentKey(Candidate(7202, credentials, settings), ChatRequest(), registry).Key);
        }
    }

    [Fact]
    public void FactoryUsesTheStrategyWhenOneOfSeveralKeysIsEnabled()
    {
        // The policy stays active whenever the channel holds more than one
        // configured key, even when only one is selectable right now: the
        // cursor state must survive a key being disabled.
        var registry = new ChannelApiKeySelectionRegistry();
        var credentials = """{"apiKeys":["sk-a","sk-b"]}""";
        var settings = """{"apiKeyStrategy":"round_robin"}""";

        var candidate = Candidate(
            7203,
            credentials,
            settings,
            disabledApiKeys: """[{"key":"sk-a"}]""");

        Assert.Equal("sk-b", SentKey(candidate, ChatRequest(), registry).Key);
        Assert.Equal("sk-b", SentKey(candidate, ChatRequest(), registry).Key);
    }

    [Fact]
    public void FactoryOverrideWinsOverTheStrategy()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var credentials = """{"apiKeys":["sk-a","sk-b"]}""";
        var settings = """{"apiKeyStrategy":"round_robin"}""";
        var candidate = Candidate(7204, credentials, settings, overrideApiKey: "sk-forced");

        Assert.Equal("sk-forced", SentKey(candidate, ChatRequest(), registry).Key);
        Assert.Equal("sk-forced", SentKey(candidate, ChatRequest(), registry).Key);
    }

    [Fact]
    public void FactoryStickyTraceSurvivesRebuild()
    {
        var registry = new ChannelApiKeySelectionRegistry();
        var credentials = """{"apiKeys":["sk-a","sk-b","sk-c"]}""";
        var settings = """{"apiKeyStrategy":"sticky"}""";
        var expected = ChannelApiKeyProvider.RendezvousSelect(
            ["sk-a", "sk-b", "sk-c"],
            "trace-factory");

        var first = SentKey(
            Candidate(7205, credentials, settings),
            ChatRequest(sessionId: "trace-factory"),
            registry);
        var second = SentKey(
            Candidate(7205, credentials, settings),
            ChatRequest(sessionId: "trace-factory"),
            registry);

        Assert.Equal(expected, first.Key);
        Assert.Equal(first.Key, second.Key);
    }

    [Fact]
    public void GeminiOutboundReportsTheSelectedKeyAndIndex()
    {
        // The audit seam (IApiKeyProvenance) is what feeds the success report;
        // a transformer that does not publish the key would make the
        // round_robin_success counter miss every Gemini success.
        var registry = new ChannelApiKeySelectionRegistry();
        var candidate = Candidate(
            7206,
            """{"apiKeys":["g1","g2"]}""",
            """{"apiKeyStrategy":"round_robin"}""",
            type: ChannelOutboundFactory.GeminiChannelType,
            baseUrl: "https://gemini.test");
        static LlmRequest GeminiRequest() => new()
        {
            Model = "gemini-model",
            ApiFormat = ApiFormats.GeminiContents,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

        var first = SentKey(candidate, GeminiRequest(), registry);
        var second = SentKey(candidate, GeminiRequest(), registry);

        Assert.Equal("g1", first.Key);
        Assert.Equal(1, first.Index);
        Assert.Equal("g2", second.Key);
        Assert.Equal(2, second.Index);
    }
}
