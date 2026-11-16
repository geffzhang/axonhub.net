using System.Text;
using System.Text.Json.Nodes;
using AxonHub.Api.AnthropicUsage;
using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/api/anthropic_input_calibration_test.go: prefix
/// hashing, measured-error transfer, stale/scoped rejection, capacity and
/// clean-stream learning.
/// </summary>
public sealed class AnthropicInputCalibrationTests
{
    [Fact]
    public void PredictionTransfersMeasuredErrorAndRejectsStalePrefixes()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new InputUsageCache();
        var parent = Fingerprint("route-a", "opening");
        var next = Fingerprint("route-a", "opening", "next");
        cache.Record(parent, 100, 120, now);

        var (count, mode) = cache.Predict(next, 150, now.AddSeconds(1), now.AddSeconds(1));
        Assert.Equal(170, count);
        Assert.Equal("calibrated", mode);

        (count, mode) = cache.Predict(next, 150, now.AddSeconds(-1), now);
        Assert.Equal(150, count);
        Assert.Equal("no_match", mode);

        var other = Fingerprint("route-b", "opening", "next");
        (count, mode) = cache.Predict(other, 150, now.AddSeconds(1), now.AddSeconds(1));
        Assert.Equal(150, count);
        Assert.Equal("no_match", mode);

        cache.Record(next, 150, 160, now.AddSeconds(1));
        var fork = Fingerprint("route-a", "opening", "rewritten");
        (count, mode) = cache.Predict(fork, 180, now.AddSeconds(2), now.AddSeconds(2));
        Assert.Equal(180, count);
        Assert.Equal("stale_prefix", mode);

        var continuation = Fingerprint("route-a", "opening", "next", "continued");
        (count, mode) = cache.Predict(continuation, 180, now.AddSeconds(2), now.AddSeconds(2));
        Assert.Equal(190, count);
        Assert.Equal("calibrated", mode);

        (count, mode) = cache.Predict(continuation, 180, now.AddHours(2), now.AddHours(2));
        Assert.Equal(180, count);
        Assert.Equal("no_match", mode);
        Assert.Empty(cache.Samples);
    }

    [Fact]
    public void FingerprintNormalizesTransportAnnotations()
    {
        var a = FingerprintOf(
            """{"model":"m","system":"policy","messages":[{"role":"user","content":"hi"}],"metadata":{"user_id":"a"},"stream":true}""");
        var b = FingerprintOf(
            """{"stream":false,"metadata":{"user_id":"b"},"messages":[{"role":"user","content":[{"type":"text","text":"hi","cache_control":{"type":"ephemeral"}}]}],"system":[{"type":"text","text":"policy","cache_control":{"type":"ephemeral"}}],"model":"m"}""");

        Assert.Equal(a.Scope, b.Scope);
        Assert.Equal(a.Prefixes.Count, b.Prefixes.Count);
        for (var i = 0; i < a.Prefixes.Count; i++)
        {
            Assert.Equal(a.Prefixes[i], b.Prefixes[i]);
        }

        foreach (var body in new[] { "{", "null", """{"messages":[]} """, """{"messages":[{}]} {}""" })
        {
            Assert.False(InputUsageFingerprintBuilder.TryBuild(
                Encoding.UTF8.GetBytes(body), JsonValue.Create("scope"), out _));
        }

        foreach (var location in new[] { "input", "input_schema" })
        {
            var template =
                "{\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\""
                + location
                + "\":{\"cache_control\":\"%s\"}}]}]}";
            var first = FingerprintOf(template.Replace("%s", "a", StringComparison.Ordinal));
            var second = FingerprintOf(template.Replace("%s", "b", StringComparison.Ordinal));
            Assert.NotEqual(first.Prefixes[0], second.Prefixes[0]);
        }

        var toolsA = FingerprintOf(
            """{"tools":[{"name":"tool","input_schema":{"cache_control":"a"}}],"messages":[{"role":"user","content":"hi"}]}""");
        var toolsB = FingerprintOf(
            """{"tools":[{"name":"tool","input_schema":{"cache_control":"b"}}],"messages":[{"role":"user","content":"hi"}]}""");
        Assert.NotEqual(toolsA.Scope, toolsB.Scope);
    }

    [Fact]
    public void CapacityBoundsEntriesAndPrefixMemory()
    {
        var cache = new InputUsageCache();
        var now = DateTimeOffset.UtcNow;
        var fingerprint = Fingerprint("scope", "a");
        for (var i = 0; i <= InputUsageCache.MaxEntries; i++)
        {
            cache.Record(fingerprint, 100, 101, now.AddMilliseconds(i));
        }

        Assert.Equal(InputUsageCache.MaxEntries, cache.Samples.Count);
        Assert.Equal(InputUsageCache.MaxEntries, cache.PrefixCount);

        var large = new InputUsageFingerprint
        {
            Scope = fingerprint.Scope,
            Prefixes = Enumerable.Range(0, InputUsageCache.MaxMessages)
                .Select(static _ => new byte[32])
                .ToList(),
        };
        for (var i = 0; i < InputUsageCache.MaxEntries; i++)
        {
            cache.Record(large, 100, 101, now);
        }

        Assert.True(cache.PrefixCount <= InputUsageCache.MaxPrefixes);

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 25; i++)
            {
                cache.Record(large, 100, 101, DateTimeOffset.UtcNow);
                cache.Predict(fingerprint, 150, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            }
        });
        Assert.True(cache.PrefixCount <= InputUsageCache.MaxPrefixes);
    }

    [Fact]
    public void FinalAnthropicInputTokensRequiresExplicitProviderUsage()
    {
        var cases = new (string Body, long Want)[]
        {
            ("""{"input_tokens":10,"cache_read_input_tokens":20,"cache_creation_input_tokens":30}""", 60),
            ("""{"input_tokens":0,"cache_read_input_tokens":20}""", 20),
            ("""{"output_tokens":10}""", 0),
            ("""{"input_tokens":-1}""", 0),
            ("""{"input_tokens":1.5}""", 0),
            ("""{"input_tokens":"10"}""", 0),
            ("""{"input_tokens":9223372036854775807,"cache_read_input_tokens":1}""", 0),
        };
        foreach (var (body, want) in cases)
        {
            var usage = JsonNode.Parse(body)!.AsObject();
            Assert.Equal(want, AnthropicInputUsageStream.FinalAnthropicInputTokens(usage));
        }

        var cache = new InputUsageCache();
        var now = DateTimeOffset.UtcNow;
        cache.Record(Fingerprint("s", "a"), 1, long.MaxValue, now);
        var (count, mode) = cache.Predict(
            Fingerprint("s", "a", "b"), 10, now, now);
        Assert.Equal(10, count);
        Assert.Equal("overflow", mode);
    }

    private static InputUsageFingerprint Fingerprint(string scope, params string[] messages)
    {
        var body = new JsonObject
        {
            ["model"] = "alias",
            ["messages"] = new JsonArray(messages
                .Select(static message => (JsonNode)new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = message,
                })
                .ToArray()),
        };
        Assert.True(InputUsageFingerprintBuilder.TryBuild(
            Encoding.UTF8.GetBytes(body.ToJsonString()),
            JsonValue.Create(scope),
            out var fingerprint));
        return fingerprint;
    }

    private static InputUsageFingerprint FingerprintOf(string body)
    {
        Assert.True(InputUsageFingerprintBuilder.TryBuild(
            Encoding.UTF8.GetBytes(body),
            JsonValue.Create("scope"),
            out var fingerprint));
        return fingerprint;
    }
}
