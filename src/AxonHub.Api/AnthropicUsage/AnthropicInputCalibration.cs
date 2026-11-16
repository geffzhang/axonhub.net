using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Api.Orchestration;

namespace AxonHub.Api.AnthropicUsage;

/// <summary>
/// Port of internal/server/api/anthropic_input_calibration.go: a bounded,
/// process-local cache of prompt-prefix hashes and measured input counts. It
/// stores no prompt text; restarting or evicting entries only restores
/// uncalibrated estimation.
/// </summary>
internal sealed class InputUsageCache
{
    internal static readonly TimeSpan CalibrationTtl = TimeSpan.FromHours(1);
    internal const int MaxEntries = 1024;
    internal const int MaxPrefixes = 65536;
    internal const int MaxMessages = 4096;

    private readonly object _sync = new();
    private List<InputUsageSample> _samples = [];
    private int _prefixes;

    /// <summary>Process-wide cache used by the Anthropic streaming boundary.</summary>
    public static InputUsageCache Shared { get; } = new();

    internal int PrefixCount
    {
        get
        {
            lock (_sync)
            {
                return _prefixes;
            }
        }
    }

    internal IReadOnlyList<InputUsageSample> Samples
    {
        get
        {
            lock (_sync)
            {
                return [.. _samples];
            }
        }
    }

    private void ExpireLocked(DateTimeOffset now)
    {
        while (_samples.Count > 0
               && now >= _samples[0].Completed + CalibrationTtl)
        {
            RemoveOldestLocked();
        }
    }

    private void RemoveOldestLocked()
    {
        _prefixes -= _samples[0].Prefixes.Count;
        _samples[0] = null!;
        _samples.RemoveAt(0);
    }

    /// <summary>
    /// Go: <c>calibrationPrefix</c>. Cumulative hashes make a prefix match cover
    /// every preceding message, not just the last message or a session id.
    /// </summary>
    internal static bool CalibrationPrefix(
        IReadOnlyList<byte[]> prefix,
        IReadOnlyList<byte[]> full)
        => prefix.Count > 0
            && prefix.Count <= full.Count
            && prefix[^1].AsSpan().SequenceEqual(full[prefix.Count - 1]);

    /// <summary>Go: <c>predict</c>; returns the count and the calibration mode.</summary>
    internal (long Count, string Mode) Predict(
        InputUsageFingerprint fingerprint,
        long raw,
        DateTimeOffset started,
        DateTimeOffset now)
    {
        lock (_sync)
        {
            ExpireLocked(now);

            var anchor = -1;
            for (var i = 0; i < _samples.Count; i++)
            {
                var sample = _samples[i];
                if (!sample.Scope.AsSpan().SequenceEqual(fingerprint.Scope)
                    || sample.Completed > started
                    || sample.Prefixes.Count >= fingerprint.Prefixes.Count
                    || !CalibrationPrefix(sample.Prefixes, fingerprint.Prefixes))
                {
                    continue;
                }

                if (anchor < 0
                    || sample.Prefixes.Count >= _samples[anchor].Prefixes.Count)
                {
                    anchor = i;
                }
            }

            if (anchor < 0)
            {
                return (raw, "no_match");
            }

            var ancestor = _samples[anchor];
            foreach (var sample in _samples)
            {
                if (sample.Scope.AsSpan().SequenceEqual(ancestor.Scope)
                    && sample.Completed > ancestor.Completed
                    && sample.Completed <= started
                    && sample.Prefixes.Count > ancestor.Prefixes.Count
                    && CalibrationPrefix(ancestor.Prefixes, sample.Prefixes)
                    && !CalibrationPrefix(sample.Prefixes, fingerprint.Prefixes))
                {
                    return (raw, "stale_prefix");
                }
            }

            var correction = ancestor.Actual - ancestor.Raw;
            if (correction > 0 && raw > long.MaxValue - correction)
            {
                return (raw, "overflow");
            }

            return (Math.Max(0, raw + correction), "calibrated");
        }
    }

    /// <summary>Go: <c>record</c>; only cleanly completed streams may train.</summary>
    internal void Record(
        InputUsageFingerprint fingerprint,
        long raw,
        long actual,
        DateTimeOffset now)
    {
        if (raw <= 0
            || actual <= 0
            || fingerprint.Prefixes.Count == 0
            || fingerprint.Prefixes.Count > MaxMessages)
        {
            return;
        }

        lock (_sync)
        {
            ExpireLocked(now);

            while (_samples.Count > 0
                   && (_samples.Count >= MaxEntries
                       || _prefixes + fingerprint.Prefixes.Count > MaxPrefixes))
            {
                RemoveOldestLocked();
            }

            var sample = new InputUsageSample
            {
                Scope = (byte[])fingerprint.Scope.Clone(),
                Prefixes = fingerprint.Prefixes
                    .Select(static prefix => (byte[])prefix.Clone())
                    .ToList(),
                Raw = raw,
                Actual = actual,
                Completed = now,
            };
            _samples.Add(sample);
            _samples = _samples.OrderBy(static item => item.Completed).ToList();
            _prefixes += sample.Prefixes.Count;
        }
    }
}

/// <summary>Go: <c>inputUsageFingerprint</c>.</summary>
internal class InputUsageFingerprint
{
    public byte[] Scope { get; init; } = [];

    public IReadOnlyList<byte[]> Prefixes { get; init; } = [];
}

/// <summary>Go: <c>inputUsageSample</c>.</summary>
internal sealed class InputUsageSample : InputUsageFingerprint
{
    public long Raw { get; init; }

    public long Actual { get; init; }

    public DateTimeOffset Completed { get; init; }
}

/// <summary>Port of the fingerprinting half of anthropic_input_calibration.go.</summary>
internal static class InputUsageFingerprintBuilder
{
    /// <summary>Go: <c>fingerprintInputUsage</c>.</summary>
    internal static bool TryBuild(
        byte[] body,
        JsonNode? identity,
        out InputUsageFingerprint fingerprint)
    {
        fingerprint = new InputUsageFingerprint();

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is not JsonObject request)
        {
            return false;
        }

        if (!request.TryGetPropertyValue("messages", out var messagesNode)
            || messagesNode is not JsonArray messages
            || messages.Count == 0
            || messages.Count > InputUsageCache.MaxMessages)
        {
            return false;
        }

        request.Remove("messages");
        request.Remove("metadata");
        request.Remove("stream");

        if (request.TryGetPropertyValue("system", out var system))
        {
            request["system"] = NormalizeContent(system);
        }

        if (request.TryGetPropertyValue("tools", out var toolsNode)
            && toolsNode is JsonArray tools)
        {
            foreach (var tool in tools)
            {
                if (tool is JsonObject toolObject)
                {
                    toolObject.Remove("cache_control");
                }
            }
        }

        var scopeNode = new JsonArray(identity?.DeepClone(), request);
        var scope = SHA256.HashData(CanonicalJson(scopeNode));
        var prefixes = new List<byte[]>(messages.Count);

        byte[] previous = new byte[32];
        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                return false;
            }

            message.Remove("cache_control");
            message["content"] = NormalizeContent(message["content"]);

            var encoded = CanonicalJson(message);
            var hashInput = new byte[previous.Length + encoded.Length];
            previous.CopyTo(hashInput, 0);
            encoded.CopyTo(hashInput, previous.Length);
            previous = SHA256.HashData(hashInput);
            prefixes.Add((byte[])previous.Clone());
        }

        fingerprint = new InputUsageFingerprint
        {
            Scope = scope,
            Prefixes = prefixes,
        };
        return true;
    }

    /// <summary>
    /// Go: <c>normalizeCalibrationContent</c>. Removes transport cache
    /// annotations only at content-block boundaries; tool arguments and JSON
    /// schemas remain significant.
    /// </summary>
    private static JsonNode? NormalizeContent(JsonNode? value)
    {
        if (value is JsonValue textValue && textValue.TryGetValue<string>(out _))
        {
            return new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = value.DeepClone(),
                });
        }

        if (value is not JsonArray blocks)
        {
            return value;
        }

        foreach (var item in blocks)
        {
            if (item is not JsonObject block)
            {
                continue;
            }

            block.Remove("cache_control");
            var kind = GjsonString(block, "type");
            if (kind is "tool_result" or "mcp_tool_result")
            {
                block["content"] = NormalizeContent(block["content"]);
            }
        }

        return blocks;
    }

    private static string GjsonString(JsonObject value, string property)
        => value.TryGetPropertyValue(property, out var found)
            && found is JsonValue jsonValue
            && jsonValue.TryGetValue<string>(out var text)
                ? text
                : string.Empty;

    /// <summary>Go's json.Marshal sorts map keys; hash inputs must too.</summary>
    private static byte[] CanonicalJson(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, node);
        }

        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var key in obj.Select(static pair => pair.Key)
                             .OrderBy(static key => key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteCanonical(writer, obj[key]);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }
}

/// <summary>Port of <c>inputUsageCalibration</c>.</summary>
internal sealed class AnthropicInputUsageCalibration
{
    private readonly CancellationToken _cancellationToken;
    private readonly InputUsageCache _cache;
    private readonly JsonNode _identity;
    private readonly DateTimeOffset _started;
    private InputUsageFingerprint? _fingerprint;
    private long _raw;

    private AnthropicInputUsageCalibration(
        CancellationToken cancellationToken,
        InputUsageCache cache,
        JsonNode identity,
        DateTimeOffset started)
    {
        _cancellationToken = cancellationToken;
        _cache = cache;
        _identity = identity;
        _started = started;
    }

    /// <summary>
    /// Go: <c>newInputUsageCalibration</c>. Requires an authenticated scope and
    /// the committed route; unidentified requests keep the stateless estimate.
    /// </summary>
    internal static AnthropicInputUsageCalibration? Create(
        CancellationToken cancellationToken,
        InputUsageCache? cache,
        ResponseRoute route,
        DateTimeOffset started,
        long projectId,
        long? apiKeyId)
    {
        if (cache is null
            || apiKeyId is not > 0
            || projectId <= 0
            || route.ChannelId <= 0
            || string.IsNullOrEmpty(route.Model))
        {
            return null;
        }

        var identity = new JsonObject
        {
            ["Project"] = projectId,
            ["Key"] = apiKeyId.Value,
            ["Route"] = new JsonObject
            {
                ["ChannelID"] = route.ChannelId,
                ["Model"] = route.Model,
                ["APIFormat"] = route.ApiFormat,
            },
        };
        return new AnthropicInputUsageCalibration(cancellationToken, cache, identity, started);
    }

    /// <summary>Go: <c>estimate</c>.</summary>
    internal long Estimate(byte[] body, string reportedModel, long raw)
    {
        var identity = new JsonArray(_identity.DeepClone(), reportedModel);
        if (!InputUsageFingerprintBuilder.TryBuild(body, identity, out var fingerprint))
        {
            return raw;
        }

        _fingerprint = fingerprint;
        _raw = raw;
        var (count, _) = _cache.Predict(fingerprint, raw, _started, DateTimeOffset.UtcNow);
        return count;
    }

    /// <summary>Go: <c>complete</c>; trains only from a cleanly completed stream.</summary>
    internal void Complete(long actual)
    {
        if (_raw <= 0
            || actual <= 0
            || _cancellationToken.IsCancellationRequested
            || _fingerprint is null)
        {
            return;
        }

        _cache.Record(_fingerprint, _raw, actual, DateTimeOffset.UtcNow);
    }
}
