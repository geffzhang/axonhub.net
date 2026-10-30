using System.Text;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.TraceStickyKeyProvider's selection rules
/// (internal/server/biz/channel_apikey_provider.go): a single enabled key is
/// used as is, several enabled keys are picked at random when the request has no
/// trace, and the trace-sticky path uses rendezvous hashing over
/// <c>traceID|key</c>.
/// </summary>
/// <remarks>
/// One instance serves one attempt. The reference provider picks inside
/// <c>Get</c> and the outbound transformer calls it exactly once per attempt, so
/// picking in the constructor is observationally identical for the untraced
/// path. The trace-seeded branch (and its 1024-entry LRU) is unreachable until
/// the trace subsystem is ported; <see cref="RendezvousSelect"/> is public so
/// the deterministic part is still covered by tests (C013).
/// </remarks>
public sealed class ChannelApiKeyProvider : IApiKeyProvider
{
    private readonly string _key;

    /// <summary>
    /// Selects from the enabled keys of one channel. <paramref name="allKeys"/>
    /// is the unfiltered list (<see cref="AxonHub.Abstractions.ChannelCredentials.GetAllApiKeys"/>),
    /// used only to derive the execution audit index.
    /// </summary>
    public ChannelApiKeyProvider(IReadOnlyList<string> enabledKeys, IReadOnlyList<string> allKeys)
    {
        ArgumentNullException.ThrowIfNull(enabledKeys);
        ArgumentNullException.ThrowIfNull(allKeys);

        if (enabledKeys.Count == 0)
        {
            // Reference fallback when every key is disabled: the first configured
            // key. ChannelOutboundFactory rejects this case before building a
            // transformer, so the branch only matters to direct callers.
            _key = allKeys.Count > 0 ? allKeys[0] : string.Empty;
        }
        else if (enabledKeys.Count == 1)
        {
            _key = enabledKeys[0];
        }
        else
        {
            // rand.IntN(len(enabled)): uniform over the enabled keys.
            _key = enabledKeys[Random.Shared.Next(enabledKeys.Count)];
        }

        KeyIndex = IndexOf(allKeys, _key);
    }

    /// <summary>The channel key this attempt sends upstream.</summary>
    public string Key => _key;

    /// <summary>
    /// 1-based position in the channel's full key list, or <see langword="null"/>
    /// for single-key channels and keys that are not in the list, matching the
    /// reference's channel_api_key_index rule.
    /// </summary>
    public int? KeyIndex { get; }

    public string Get() => _key;

    /// <summary>
    /// Port of rendezvousSelect: highest HRW score wins, ties keep the earlier
    /// key. Scores hash the UTF-8 bytes of <c>seed + "|" + key</c>.
    /// </summary>
    public static string RendezvousSelect(IReadOnlyList<string> keys, string seed)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(seed);

        if (keys.Count == 0)
        {
            throw new ArgumentException("no keys to select from", nameof(keys));
        }

        var bestKey = keys[0];
        var bestScore = HashApiKey(seed + "|" + bestKey);

        for (var index = 1; index < keys.Count; index++)
        {
            var key = keys[index];
            var score = HashApiKey(seed + "|" + key);
            if (score > bestScore)
            {
                bestScore = score;
                bestKey = key;
            }
        }

        return bestKey;
    }

    /// <summary>Port of hashAPIKey: FNV-1a 64 over the UTF-8 bytes.</summary>
    public static ulong HashApiKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;

        var hash = offsetBasis;
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            hash ^= valueByte;
            hash *= prime;
        }

        return hash;
    }

    private static int? IndexOf(IReadOnlyList<string> allKeys, string key)
    {
        if (allKeys.Count <= 1)
        {
            return null;
        }

        for (var index = 0; index < allKeys.Count; index++)
        {
            if (string.Equals(allKeys[index], key, StringComparison.Ordinal))
            {
                return index + 1;
            }
        }

        return null;
    }
}
