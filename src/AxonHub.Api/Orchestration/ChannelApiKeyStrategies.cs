using AxonHub.Abstractions;
using AxonHub.Api.Management;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of internal/server/biz/channel_apikey_strategies.go: the multi-key
/// strategy names, their helpers, and the per-strategy selection the reference's
/// providers perform inside <c>Get</c>.
/// </summary>
/// <remarks>
/// One provider serves one attempt and the reference calls <c>Get</c> exactly
/// once per attempt, so the port resolves the key when the provider is built;
/// the enabled set, the settings and the trace id cannot change during an
/// attempt. The only state that must outlive the provider is the per-channel
/// selection state in <see cref="ChannelApiKeySelectionRegistry"/>.
/// </remarks>
internal static class ChannelApiKeyStrategies
{
    /// <summary>objects.APIKeyStrategySticky (internal/objects/channel.go:284-303).</summary>
    public const string Sticky = "sticky";

    public const string Random = "random";

    public const string RoundRobin = "round_robin";

    public const string RoundRobinSuccess = "round_robin_success";

    public const string Priority = "priority";

    public const string Fixed = "fixed";

    /// <summary>
    /// Port of <c>channelAPIKeyStrategy</c>: an absent settings document or an
    /// absent field means sticky. A present empty value also means sticky.
    /// </summary>
    public static string StrategyOf(ChannelSettingsDraft? settings)
        => settings?.ApiKeyStrategy ?? string.Empty;

    /// <summary>Port of <c>channelAPIKeySwitchAfter</c>: absent means one; values below one mean one.</summary>
    public static int SwitchAfterOf(ChannelSettingsDraft? settings)
        => NormalizeSwitchAfter(settings?.ApiKeyRoundRobinSwitchAfter ?? 1);

    /// <summary>Normalizes the reuse threshold the way the provider constructors do.</summary>
    public static int NormalizeSwitchAfter(int value) => value >= 1 ? value : 1;

    /// <summary>
    /// Port of <c>strategyKeepsSelectionState</c>: the strategies that remember
    /// something between requests get the shared per-channel state. An empty
    /// strategy means sticky, the historical default.
    /// </summary>
    public static bool KeepsSelectionState(string strategy) => strategy switch
    {
        "" or Sticky or RoundRobin or RoundRobinSuccess or Fixed => true,
        _ => false,
    };

    /// <summary>
    /// Port of <c>newMultiKeyProvider</c>'s selection rules: unknown or empty
    /// strategies fall back to sticky.
    /// </summary>
    /// <param name="state">
    /// The shared state returned by <see cref="ChannelApiKeySelectionRegistry.StateFor"/>
    /// for this build, or null when the strategy is stateless or no registry is
    /// wired (direct callers and tests keep the historical provider behavior).
    /// </param>
    public static string Select(
        ChannelSettingsDraft? settings,
        ChannelApiKeyState? state,
        IReadOnlyList<string> enabledKeys,
        IReadOnlyList<string> configuredApiKeys,
        IReadOnlyList<string> allKeys,
        IReadOnlySet<string> disabledKeys,
        string? traceId)
    {
        ArgumentNullException.ThrowIfNull(enabledKeys);
        ArgumentNullException.ThrowIfNull(configuredApiKeys);
        ArgumentNullException.ThrowIfNull(allKeys);
        ArgumentNullException.ThrowIfNull(disabledKeys);

        var strategy = StrategyOf(settings);
        var per = SwitchAfterOf(settings);
        var selectable = SelectableKeys(enabledKeys, configuredApiKeys);

        switch (strategy)
        {
            case Random:
                // rand.IntN(len(enabled)): uniform over the selectable keys on
                // every call, traced or not.
                return selectable[Random.Shared.Next(selectable.Count)];
            case RoundRobin:
                // Without shared state the reference provider keeps a
                // provider-local counter; the port's provider serves one attempt,
                // so a fresh counter always yields the first key.
                return state is null
                    ? selectable[0]
                    : state.NextRoundRobinKey(selectable, per);
            case RoundRobinSuccess:
                // Without shared state there is nothing to advance: behave like
                // a static first-key selection.
                return state is null
                    ? selectable[0]
                    : state.SelectRoundRobinSuccessKey(SnapshotOf(allKeys, disabledKeys));
            case Priority:
                // Failover to the next key is delegated to the per-key
                // auto-disable rules, exactly like the reference provider.
                return selectable[0];
            case Fixed:
                // Without shared state there is nothing to remember: fall back
                // to the first selectable key.
                return state is null
                    ? selectable[0]
                    : state.SelectFixedKey(SnapshotOf(allKeys, disabledKeys));
            case Sticky:
            case "":
            default:
                return SelectSticky(state, enabledKeys, configuredApiKeys, traceId);
        }
    }

    /// <summary>
    /// Port of <c>selectableKeys</c>: the enabled keys, or the first configured
    /// <c>apiKeys</c> entry when every key is disabled (TraceStickyKeyProvider's
    /// edge handling; the channel itself is out of service in that case).
    /// </summary>
    public static IReadOnlyList<string> SelectableKeys(
        IReadOnlyList<string> enabledKeys,
        IReadOnlyList<string> configuredApiKeys)
    {
        if (enabledKeys.Count > 0)
        {
            return enabledKeys;
        }

        return configuredApiKeys.Count > 0 ? [configuredApiKeys[0]] : [string.Empty];
    }

    /// <summary>
    /// Port of <c>positionOf</c>: the index of the cursor key inside the full
    /// array, or its remembered index once the key has been removed, so a walk
    /// continues where the key used to sit.
    /// </summary>
    public static int PositionOf(IReadOnlyList<string> all, string cursorKey, int cursorIdx)
    {
        for (var index = 0; index < all.Count; index++)
        {
            if (string.Equals(all[index], cursorKey, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return cursorIdx;
    }

    /// <summary>
    /// Port of <c>firstSelectableFrom</c>: walks the full array forward from
    /// <paramref name="start"/> with wraparound, skipping disabled keys. When
    /// every key is disabled it returns the first key as a defensive fallback.
    /// </summary>
    public static (string Key, int Index) FirstSelectableFrom(
        IReadOnlyList<string> all,
        IReadOnlySet<string> disabled,
        int start)
    {
        var count = all.Count;
        if (count == 0)
        {
            return (string.Empty, -1);
        }

        if (start < 0 || start >= count)
        {
            start = 0;
        }

        for (var offset = 0; offset < count; offset++)
        {
            var index = (start + offset) % count;
            if (!disabled.Contains(all[index]))
            {
                return (all[index], index);
            }
        }

        return (all[0], 0);
    }

    /// <summary>
    /// Port of <c>publishAPIKeySelectionSnapshot</c> for the enabled-channel
    /// cache read: decodes the descriptor's strategy and key arrays and stores
    /// them as the served generation.
    /// </summary>
    public static void Publish(
        ChannelApiKeySelectionRegistry registry,
        ChannelDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(channel);

        var settings = ChannelTransformOptions.SettingsOfRaw(channel.Settings);

        registry.Publish(
            channel.Id,
            StrategyOf(settings),
            SwitchAfterOf(settings),
            SnapshotOf(
                ChannelCredentials.GetAllApiKeys(channel.Credentials),
                ChannelCredentials.GetDisabledApiKeySet(channel.DisabledApiKeys)));
    }

    /// <summary>The key-array half of a selection snapshot.</summary>
    public static ChannelKeySnapshot SnapshotOf(
        IReadOnlyList<string> allKeys,
        IReadOnlySet<string> disabledKeys)
        => new()
        {
            AllKeys = allKeys,
            DisabledKeys = disabledKeys,
        };

    /// <summary>
    /// The sticky branch shared by the <c>sticky</c>, empty and unknown
    /// strategies: with shared state it keeps a trace's key across rebuilds;
    /// without it the historical per-provider TraceStickyKeyProvider rules
    /// apply.
    /// </summary>
    private static string SelectSticky(
        ChannelApiKeyState? state,
        IReadOnlyList<string> enabledKeys,
        IReadOnlyList<string> configuredApiKeys,
        string? traceId)
    {
        if (state is null)
        {
            return ChannelApiKeyProvider.SelectTraceSticky(enabledKeys, configuredApiKeys, traceId);
        }

        var enabled = enabledKeys;
        if (enabled.Count == 0)
        {
            return configuredApiKeys.Count > 0 ? configuredApiKeys[0] : string.Empty;
        }

        if (enabled.Count == 1)
        {
            return enabled[0];
        }

        if (string.IsNullOrEmpty(traceId))
        {
            // Untraced requests are spread at random, same as the reference.
            return enabled[Random.Shared.Next(enabled.Count)];
        }

        return state.StickyKeyFor(enabled, traceId);
    }
}
