using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/biz/channel_apikey_test.go for the reachable half of
/// TraceStickyKeyProvider: untraced random selection, the all-disabled fallback,
/// rendezvous hashing and the execution audit index. The trace-seeded branch
/// (and its LRU, which makes an addition exactly stable) waits for the trace
/// subsystem (C013); the deterministic selector it uses is covered here.
/// </summary>
public sealed class ChannelApiKeyProviderTests
{
    private static ChannelApiKeyProvider Provider(string[] enabled, string[]? all = null)
        => new(enabled, all ?? enabled);

    [Fact]
    public void SingleEnabledKeyIsSelected()
    {
        var provider = Provider(["legacy-key"]);

        Assert.Equal("legacy-key", provider.Key);
        Assert.Equal("legacy-key", provider.Get());
        Assert.Null(provider.KeyIndex);
    }

    [Fact]
    public void UntracedSelectionStaysWithinEnabledKeys()
    {
        string[] keys = ["key-1", "key-2", "key-3"];

        for (var attempt = 0; attempt < 50; attempt++)
        {
            Assert.Contains(Provider(keys).Key, keys);
        }
    }

    [Fact]
    public void EmptyEnabledKeysFallBackToTheFirstConfiguredKey()
    {
        var provider = Provider([], ["fallback-key"]);

        Assert.Equal("fallback-key", provider.Key);
    }

    [Fact]
    public void AllKeysDisabledFallsBackToTheFirstOfTwoKeys()
    {
        var provider = Provider([], ["key-1", "key-2"]);

        Assert.Equal("key-1", provider.Key);
        Assert.Equal(1, provider.KeyIndex);
    }

    [Fact]
    public void DisabledKeysAreNeverSelected()
    {
        string[] all = ["key-1", "key-2", "key-3"];
        string[] enabled = ["key-1", "key-3"];

        for (var attempt = 0; attempt < 100; attempt++)
        {
            var provider = Provider(enabled, all);

            Assert.Contains(provider.Key, enabled);
            Assert.NotEqual("key-2", provider.Key);
            Assert.Equal(provider.Key == "key-1" ? 1 : 3, provider.KeyIndex);
        }
    }

    [Fact]
    public void RendezvousSelectionIsStickyForTheSameTrace()
    {
        string[] keys = ["key-1", "key-2", "key-3"];

        var first = ChannelApiKeyProvider.RendezvousSelect(keys, "trace-abc-123");

        Assert.Contains(first, keys);
        Assert.Equal(first, ChannelApiKeyProvider.RendezvousSelect(keys, "trace-abc-123"));
        Assert.Equal(first, ChannelApiKeyProvider.RendezvousSelect(keys, "trace-abc-123"));
    }

    [Fact]
    public void DifferentTracesSelectDifferentKeys()
    {
        string[] keys = ["key-1", "key-2", "key-3", "key-4", "key-5"];

        var selected = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 100; index++)
        {
            selected.Add(ChannelApiKeyProvider.RendezvousSelect(
                keys,
                "trace-" + (char)('A' + (index % 26)) + "-" + (char)('0' + (index % 10))));
        }

        Assert.True(selected.Count > 1, "different traces should select different keys");
    }

    [Fact]
    public void AddingAKeyRemapsAtMostTwoThirdsOfTraces()
    {
        string[] originalKeys = ["key-1", "key-2", "key-3"];
        string[] newKeys = ["key-1", "key-2", "key-3", "key-4"];

        var traces = new string[20];
        for (var index = 0; index < traces.Length; index++)
        {
            traces[index] = "trace-" + (char)('A' + index);
        }

        var remapped = traces.Count(trace =>
            ChannelApiKeyProvider.RendezvousSelect(originalKeys, trace)
            != ChannelApiKeyProvider.RendezvousSelect(newKeys, trace));

        Assert.True(
            remapped <= traces.Length * 2 / 3,
            "adding a key should not remap most traces (rendezvous property), remapped " + remapped);
    }

    [Fact]
    public void RemovingAKeyLeavesEveryUnaffectedTraceStable()
    {
        string[] originalKeys = ["key-1", "key-2", "key-3", "key-4"];
        string[] newKeys = ["key-1", "key-2", "key-4"];

        var selections = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 40; index++)
        {
            var trace = "trace-" + (char)('A' + index);
            selections[trace] = ChannelApiKeyProvider.RendezvousSelect(originalKeys, trace);
        }

        foreach (var (trace, original) in selections)
        {
            var after = ChannelApiKeyProvider.RendezvousSelect(newKeys, trace);

            Assert.Contains(after, newKeys);
            if (original != "key-3")
            {
                Assert.Equal(original, after);
            }
        }

        Assert.Contains(selections.Values, key => key == "key-3");
    }

    [Fact]
    public void SelectionIsIndependentOfKeyOrder()
    {
        string[] keysA = ["key-a", "key-b", "key-c"];
        string[] keysB = ["key-c", "key-a", "key-b"];

        Assert.Equal(
            ChannelApiKeyProvider.RendezvousSelect(keysA, "trace-order-test"),
            ChannelApiKeyProvider.RendezvousSelect(keysB, "trace-order-test"));
    }

    [Fact]
    public void LegacyAndNewKeysShareTheOneBasedIndexOverTheFullList()
    {
        string[] all = ["legacy-key", "new-key-1", "new-key-2"];

        for (var attempt = 0; attempt < 50; attempt++)
        {
            var provider = Provider(all, all);
            var expected = Array.IndexOf(all, provider.Key) + 1;

            Assert.InRange(expected, 1, 3);
            Assert.Equal(expected, provider.KeyIndex);
        }
    }

    [Fact]
    public void IndexOfTheLastKeyCountsDisabledPrefixes()
    {
        // Disabling the first two keys leaves one static key, but the audit
        // index still counts them: the reference derives it from GetAllAPIKeys.
        var provider = Provider(["key-3"], ["key-1", "key-2", "key-3"]);

        Assert.Equal("key-3", provider.Key);
        Assert.Equal(3, provider.KeyIndex);
    }

    [Fact]
    public void KeysSharingTheirLastFourCharactersStillGetDistinctIndexes()
    {
        var provider = Provider(["sk-bravo-9999"], ["sk-alpha-9999", "sk-bravo-9999"]);

        Assert.Equal("sk-bravo-9999", provider.Key);
        Assert.Equal(2, provider.KeyIndex);
    }

    [Fact]
    public void NoConfiguredKeysYieldsAnEmptyKeyWithoutIndex()
    {
        var provider = Provider([], []);

        Assert.Equal(string.Empty, provider.Key);
        Assert.Null(provider.KeyIndex);
    }

    [Fact]
    public void HashMatchesTheReferenceFnv1aVectors()
    {
        // FNV-1a 64 is part of the cross-language contract: Go's fnv.New64a
        // produced these values for the same UTF-8 bytes.
        Assert.Equal(14695981039346656037UL, ChannelApiKeyProvider.HashApiKey(string.Empty));
        Assert.Equal(0xaf63dc4c8601ec8cUL, ChannelApiKeyProvider.HashApiKey("a"));
    }

    [Fact]
    public void HashIsDeterministicAndSeparatesInputs()
    {
        Assert.Equal(
            ChannelApiKeyProvider.HashApiKey("input-1"),
            ChannelApiKeyProvider.HashApiKey("input-1"));

        Assert.NotEqual(
            ChannelApiKeyProvider.HashApiKey("input-1"),
            ChannelApiKeyProvider.HashApiKey("input-2"));
        Assert.NotEqual(
            ChannelApiKeyProvider.HashApiKey("input-2"),
            ChannelApiKeyProvider.HashApiKey("input-3"));
        Assert.NotEqual(
            ChannelApiKeyProvider.HashApiKey("input-1"),
            ChannelApiKeyProvider.HashApiKey("input-3"));
    }
}
