using System.Collections.Concurrent;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// The key arrays of one channel generation, used by the two strategies that
/// walk the full key array (<c>fixed</c> and <c>round_robin_success</c>) and as
/// the generation identity behind <c>isServing</c>.
/// </summary>
/// <remarks>
/// The reference compares the published <c>*Channel</c> pointer against the
/// provider's own channel, because both are the same object only when the
/// provider belongs to the newest cache load. The port rebuilds a descriptor per
/// read, so identity is carried by content: a provider whose key arrays match
/// the published generation would compute every cursor write from the same
/// arrays as the published provider, so allowing the write is observationally
/// identical to the reference's pointer rule.
/// </remarks>
internal sealed class ChannelKeySnapshot
{
    public required IReadOnlyList<string> AllKeys { get; init; }

    /// <summary>
    /// Every non-empty key of <c>disabled_api_keys</c>, expiry ignored
    /// (the reference's <c>cachedDisabledKeySet</c>).
    /// </summary>
    public required IReadOnlySet<string> DisabledKeys { get; init; }

    public bool SameKeys(ChannelKeySnapshot other)
        => AllKeys.SequenceEqual(other.AllKeys, StringComparer.Ordinal)
            && DisabledKeys.SetEquals(other.DisabledKeys);
}

/// <summary>
/// Port of biz.apiKeySelectionState (internal/server/biz/channel_apikey_state.go):
/// the per-channel selection state that has to outlive a channel snapshot.
/// </summary>
/// <remarks>
/// Any channel change rebuilds every enabled channel and with it every key
/// provider, so the strategies that remember something between requests keep it
/// here, keyed by channel id, instead of inside the provider.
/// </remarks>
internal sealed class ChannelApiKeyState
{
    /// <summary>Port of traceStickyLRUSize.</summary>
    private const int TraceStickyLruSize = 1024;

    private readonly object _gate = new();

    private readonly Dictionary<string, LinkedListNode<StickyEntry>> _stickyIndex =
        new(StringComparer.Ordinal);

    private readonly LinkedList<StickyEntry> _stickyOrder = new();

    private string _strategy = string.Empty;

    private ChannelKeySnapshot? _snapshot;

    private ulong _rrCounter;

    private string _fixedCursorKey = string.Empty;
    private int _fixedCursorIdx;

    private string _rrSuccessCursor = string.Empty;
    private int _rrSuccessIdx;
    private int _rrSuccessCount;
    private int _rrSuccessPer;

    /// <summary>
    /// Port of <c>apiKeySelectionStateFor</c>'s state half: record the strategy
    /// the channel was last built with and the round_robin_success reuse
    /// threshold; a changed threshold invalidates the successes counted under
    /// the previous one.
    /// </summary>
    internal void Configure(string strategy, int switchAfter)
    {
        lock (_gate)
        {
            _strategy = strategy;

            if (switchAfter != _rrSuccessPer)
            {
                _rrSuccessCount = 0;
            }

            _rrSuccessPer = switchAfter;
        }
    }

    /// <summary>
    /// Port of <c>publishAPIKeySelectionSnapshot</c>: stores the channel
    /// generation the success report reads its key array from. Serialized with
    /// the cursor updates so a reload cannot land between a provider's
    /// isServing check and its cursor write.
    /// </summary>
    internal void SetSnapshot(ChannelKeySnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
        }
    }

    /// <summary>
    /// Port of <c>nextRoundRobinKey</c>: every request advances the counter,
    /// whatever its outcome.
    /// </summary>
    internal string NextRoundRobinKey(IReadOnlyList<string> selectable, int per)
    {
        if (selectable.Count == 0)
        {
            return string.Empty;
        }

        if (per < 1)
        {
            per = 1;
        }

        lock (_gate)
        {
            var index = (int)(_rrCounter / (ulong)per % (ulong)selectable.Count);
            _rrCounter++;

            return selectable[index];
        }
    }

    /// <summary>
    /// Port of <c>stickyKeyFor</c>: a remembered trace keeps its key while that
    /// key stays enabled; otherwise rendezvous hashing picks one and the choice
    /// is remembered in a 1024-entry LRU.
    /// </summary>
    internal string StickyKeyFor(IReadOnlyList<string> enabled, string traceId)
    {
        if (enabled.Count == 1)
        {
            return enabled[0];
        }

        lock (_gate)
        {
            if (_stickyIndex.TryGetValue(traceId, out var cachedNode))
            {
                var cached = cachedNode.Value.Key;
                if (enabled.Contains(cached, StringComparer.Ordinal))
                {
                    TouchSticky(cachedNode);
                    return cached;
                }
            }

            var selected = ChannelApiKeyProvider.RendezvousSelect(enabled, traceId);
            RememberSticky(traceId, selected);

            return selected;
        }
    }

    /// <summary>
    /// Port of <c>selectFixedKey</c>: the key in use wins while selectable;
    /// otherwise the walk starts at that key's position (or its remembered
    /// position once removed) and moves forward through the full key array,
    /// wrapping around and skipping disabled keys. Only the serving generation
    /// moves the cursor.
    /// </summary>
    internal string SelectFixedKey(ChannelKeySnapshot snapshot)
    {
        var all = snapshot.AllKeys;
        if (all.Count == 0)
        {
            return string.Empty;
        }

        lock (_gate)
        {
            var serving = IsServing(snapshot);

            var idx = IndexOf(all, _fixedCursorKey);
            if (idx >= 0 && !snapshot.DisabledKeys.Contains(_fixedCursorKey))
            {
                if (serving)
                {
                    _fixedCursorIdx = idx;
                }

                return _fixedCursorKey;
            }

            var (selectedKey, selectedIdx) = ChannelApiKeyStrategies.FirstSelectableFrom(
                all,
                snapshot.DisabledKeys,
                ChannelApiKeyStrategies.PositionOf(all, _fixedCursorKey, _fixedCursorIdx));

            if (serving)
            {
                _fixedCursorKey = selectedKey;
                _fixedCursorIdx = selectedIdx;
            }

            return selectedKey;
        }
    }

    /// <summary>
    /// Port of <c>selectRoundRobinSuccessKey</c>: it counts nothing; the cursor
    /// is advanced by the success report. A cursor that stopped being selectable
    /// moves forward through the full key array and restarts the success count.
    /// </summary>
    internal string SelectRoundRobinSuccessKey(ChannelKeySnapshot snapshot)
    {
        var all = snapshot.AllKeys;
        if (all.Count == 0)
        {
            return string.Empty;
        }

        lock (_gate)
        {
            var serving = IsServing(snapshot);

            var idx = IndexOf(all, _rrSuccessCursor);
            if (idx >= 0 && !snapshot.DisabledKeys.Contains(_rrSuccessCursor))
            {
                if (serving)
                {
                    _rrSuccessIdx = idx;
                }

                return _rrSuccessCursor;
            }

            var (selectedKey, selectedIdx) = ChannelApiKeyStrategies.FirstSelectableFrom(
                all,
                snapshot.DisabledKeys,
                ChannelApiKeyStrategies.PositionOf(all, _rrSuccessCursor, _rrSuccessIdx));

            if (serving)
            {
                _rrSuccessCursor = selectedKey;
                _rrSuccessIdx = selectedIdx;
                _rrSuccessCount = 0;
            }

            return selectedKey;
        }
    }

    /// <summary>
    /// Port of <c>onAPIKeySuccess</c>: a successful request for the key the
    /// cursor is using counts towards its reuse threshold; once reached, the
    /// cursor moves past that key through the served generation's key array.
    /// Late reports for keys the cursor already left are ignored, as are
    /// reports while another strategy is in force.
    /// </summary>
    internal void ReportSuccess(string apiKey)
    {
        lock (_gate)
        {
            if (!string.Equals(_strategy, ChannelApiKeyStrategies.RoundRobinSuccess, StringComparison.Ordinal))
            {
                return;
            }

            if (_rrSuccessCursor.Length == 0
                || !string.Equals(_rrSuccessCursor, apiKey, StringComparison.Ordinal))
            {
                return;
            }

            _rrSuccessCount++;

            var per = Math.Max(_rrSuccessPer, 1);
            if (_rrSuccessCount < per)
            {
                return;
            }

            var snapshot = _snapshot;
            if (snapshot is null)
            {
                _rrSuccessCount = 0;
                return;
            }

            var all = snapshot.AllKeys;
            if (all.Count == 0)
            {
                _rrSuccessCount = 0;
                return;
            }

            var (selectedKey, selectedIdx) = ChannelApiKeyStrategies.FirstSelectableFrom(
                all,
                snapshot.DisabledKeys,
                ChannelApiKeyStrategies.PositionOf(all, _rrSuccessCursor, _rrSuccessIdx) + 1);

            _rrSuccessCursor = selectedKey;
            _rrSuccessIdx = selectedIdx;
            _rrSuccessCount = 0;
        }
    }

    /// <summary>Port of <c>isServing</c>; callers hold <see cref="_gate"/>.</summary>
    private bool IsServing(ChannelKeySnapshot snapshot)
        => _snapshot is null || _snapshot.SameKeys(snapshot);

    private static int IndexOf(IReadOnlyList<string> all, string key)
    {
        for (var index = 0; index < all.Count; index++)
        {
            if (string.Equals(all[index], key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void RememberSticky(string traceId, string key)
    {
        if (_stickyIndex.TryGetValue(traceId, out var existing))
        {
            existing.Value = new StickyEntry(traceId, key);
            TouchSticky(existing);
            return;
        }

        var node = _stickyOrder.AddLast(new StickyEntry(traceId, key));
        _stickyIndex[traceId] = node;

        if (_stickyIndex.Count > TraceStickyLruSize)
        {
            var oldest = _stickyOrder.First!;
            _stickyOrder.RemoveFirst();
            _stickyIndex.Remove(oldest.Value.TraceId);
        }
    }

    private void TouchSticky(LinkedListNode<StickyEntry> node)
    {
        if (node == _stickyOrder.Last)
        {
            return;
        }

        _stickyOrder.Remove(node);
        _stickyOrder.AddLast(node);
    }

    private readonly record struct StickyEntry(string TraceId, string Key);
}

/// <summary>
/// Process-wide home of the per-channel key selection state
/// (<c>ChannelService.apiKeyStates</c>, channel_apikey_state.go). It lives on the
/// API process because that is where the enabled-channel reads, the outbound
/// providers and the performance records all run.
/// </summary>
public sealed class ChannelApiKeySelectionRegistry
{
    private readonly ConcurrentDictionary<long, ChannelApiKeyState> _states = new();

    /// <summary>
    /// Port of <c>apiKeySelectionStateFor</c>: returns the shared state for the
    /// strategies that remember something between requests, creating it on
    /// first use, and null for the stateless ones (so they never allocate).
    /// </summary>
    internal ChannelApiKeyState? StateFor(long channelId, string strategy, int switchAfter)
    {
        if (!ChannelApiKeyStrategies.KeepsSelectionState(strategy))
        {
            return null;
        }

        var state = _states.GetOrAdd(channelId, static _ => new ChannelApiKeyState());
        state.Configure(strategy, ChannelApiKeyStrategies.NormalizeSwitchAfter(switchAfter));

        return state;
    }

    /// <summary>
    /// Port of <c>publishAPIKeySelectionSnapshot</c>: records the generation the
    /// success report should read its key array from. Only the enabled-channel
    /// cache path calls this; single lookups, key tests and endpoint detection
    /// must not overwrite it.
    /// </summary>
    internal void Publish(
        long channelId,
        string strategy,
        int switchAfter,
        ChannelKeySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        StateFor(channelId, strategy, switchAfter)?.SetSnapshot(snapshot);
    }

    /// <summary>
    /// Port of <c>forgetAPIKeySelectionState</c>: a deleted channel does not
    /// leave state behind for the lifetime of the process.
    /// </summary>
    internal void Forget(long channelId) => _states.TryRemove(channelId, out _);

    /// <summary>Port of <c>onAPIKeySuccess</c>.</summary>
    internal void OnApiKeySuccess(long channelId, string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return;
        }

        if (!_states.TryGetValue(channelId, out var state))
        {
            return;
        }

        state.ReportSuccess(apiKey);
    }
}
