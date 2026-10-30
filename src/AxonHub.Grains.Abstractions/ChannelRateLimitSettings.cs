using System.Text.Json;
using System.Text.Json.Serialization;
using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Port of <c>objects.ChannelRateLimit</c> (internal/objects/channel.go:302-318):
/// the rate-limit half of a channel's <c>settings</c> jsonb value.
/// </summary>
/// <remarks>
/// <para>
/// <c>MaxConcurrent</c> turns the channel into a hard-admission channel: at most
/// that many requests are in flight and excess requests wait in a FIFO queue.
/// <c>QueueSize</c> bounds that queue (<c>nil</c>/<c>0</c> = unbounded wait) and
/// <c>QueueTimeoutMs</c> bounds each wait (<c>nil</c>/<c>0</c> = no per-channel
/// timeout). RPM/TPM are fixed natural-minute budgets.
/// </para>
/// <para>
/// The reference validates these fields on the channel write path
/// (<c>biz.ValidateRateLimit</c>); this build has no channel write API, so
/// malformed values degrade to "no limits" the same way channel override
/// settings do (difference C037).
/// </para>
/// </remarks>
[GenerateSerializer]
public sealed class ChannelRateLimitSettings
{
    // encoding/json matches struct fields case-insensitively.
    private static readonly JsonSerializerOptions ParseOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Requests per minute; null = unlimited.</summary>
    [Id(0)]
    [JsonPropertyName("rpm")]
    public long? Rpm { get; set; }

    /// <summary>Tokens per minute; null = unlimited.</summary>
    [Id(1)]
    [JsonPropertyName("tpm")]
    public long? Tpm { get; set; }

    /// <summary>Maximum concurrent requests; null or &lt;= 0 = unlimited.</summary>
    [Id(2)]
    [JsonPropertyName("maxConcurrent")]
    public long? MaxConcurrent { get; set; }

    /// <summary>
    /// FIFO wait-queue capacity when <see cref="MaxConcurrent"/> is set:
    /// null/0 = unbounded queue, &gt; 0 = reject once full.
    /// </summary>
    [Id(3)]
    [JsonPropertyName("queueSize")]
    public long? QueueSize { get; set; }

    /// <summary>Per-channel queue wait timeout in milliseconds; null/0 = none.</summary>
    [Id(4)]
    [JsonPropertyName("queueTimeoutMs")]
    public long? QueueTimeoutMs { get; set; }

    /// <summary>
    /// Reads <c>settings.rateLimit</c> from a stored channel settings value.
    /// Returns <see langword="null"/> when the field is absent, null, or not a
    /// JSON object (the reference's typed decode would fail the whole channel
    /// load; see difference C037).
    /// </summary>
    public static ChannelRateLimitSettings? Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (!TryGetProperty(document.RootElement, "rateLimit", out var rateLimit)
                || rateLimit.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            try
            {
                return rateLimit.Deserialize<ChannelRateLimitSettings>(ParseOptions)
                    ?? new ChannelRateLimitSettings();
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Port of <c>encoding/json</c>'s case-insensitive field match.</summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }
}
