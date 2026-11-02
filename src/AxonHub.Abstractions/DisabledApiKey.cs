using System.Text.Json.Serialization;

namespace AxonHub.Abstractions;

/// <summary>
/// Port of objects.DisabledAPIKey (internal/objects/channel.go:322-329): one
/// auto-disable bookkeeping record on the channel's <c>disabled_api_keys</c>
/// column. The key is the credential reference (plaintext API key, or the
/// <see cref="ChannelCredentials.OAuthCredentialRef"/> sentinel for OAuth
/// channels), never a hash.
/// </summary>
public sealed class DisabledApiKey
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("disabledAt")]
    public DateTime DisabledAt { get; set; }

    [JsonPropertyName("errorCode")]
    public int ErrorCode { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("expiresAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Port of IsExpired: a temporary disable whose deadline has passed.</summary>
    public bool IsExpired(DateTimeOffset now)
        => ExpiresAt is { } expires && now > new DateTimeOffset(ToUtc(expires));

    private static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // Values decoded from the JSON column are UTC; test doubles may omit
            // the kind.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
