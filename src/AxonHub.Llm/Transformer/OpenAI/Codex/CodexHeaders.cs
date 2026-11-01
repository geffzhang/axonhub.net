using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Port of llm/transformer/openai/codex/headers.go.</summary>
public static class CodexHeaders
{
    public const string SessionHeader = "Session_id";

    public const string SessionHeaderHyphen = "Session-Id";

    public const string TurnMetadataHeader = "X-Codex-Turn-Metadata";

    public const string TurnStateHeader = "X-Codex-Turn-State";

    public const string WindowIdHeader = "X-Codex-Window-Id";

    public const string ClientRequestIdHeader = "X-Client-Request-Id";

    public const string BetaFeaturesHeader = "X-Codex-Beta-Features";

    public const string ThreadIdHeader = "Thread-Id";

    /// <summary>
    /// The canonical spelling is "Openai": Go's http.Header canonicalizes keys,
    /// so lookups match regardless of case, and the wire name is
    /// case-insensitive per RFC 9110.
    /// </summary>
    public const string ResponsesLiteHeader = "X-Openai-Internal-Codex-Responses-Lite";

    /// <summary>
    /// Client metadata that Codex-compatible upstreams use to select protocol
    /// behavior. Headers a Codex client actually sends are copied verbatim;
    /// headers missing from non-Codex inbound requests are fabricated in
    /// <c>CodexOutboundTransformer.TransformRequest</c> so the upstream always
    /// sees a complete Codex session shape.
    /// </summary>
    public static readonly string[] PassthroughHeaders =
    [
        TurnMetadataHeader,
        TurnStateHeader,
        WindowIdHeader,
        ClientRequestIdHeader,
        BetaFeaturesHeader,
        ThreadIdHeader,
        ResponsesLiteHeader,
    ];

    /// <summary>Port of <c>ExtractSessionIDFromTurnMetadata</c>.</summary>
    public static string ExtractSessionIdFromTurnMetadata(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        TurnMetadata? payload;
        try
        {
            payload = LlmJson.Deserialize<TurnMetadata>(raw);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return (payload?.SessionId ?? string.Empty).Trim();
    }

    /// <summary>
    /// Port of <c>turnStartedAtUnixMS</c>: a deterministic per-session timestamp
    /// inside the current 10-minute bucket — the bucket start plus an offset
    /// derived from the session id. Fabricated turn metadata is therefore stable
    /// across requests of the same session and rolls over roughly every 10
    /// minutes.
    /// </summary>
    public static long TurnStartedAtUnixMs(string sessionId)
    {
        const long bucketMs = 10 * 60 * 1000;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (now - (now % bucketMs)) + (long)(HashUint64(sessionId) % (ulong)bucketMs);
    }

    /// <summary>Port of <c>hashUint64</c>: the first 8 SHA-256 bytes, big-endian.</summary>
    public static ulong HashUint64(string value)
    {
        if (value.Length == 0)
        {
            return 0;
        }

        var sum = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(sum.AsSpan(0, 8));
    }

    /// <summary>Port of <c>GetSessionIDFromHeaders</c>.</summary>
    public static string GetSessionIdFromHeaders(Dictionary<string, List<string>>? headers)
    {
        if (headers is null)
        {
            return string.Empty;
        }

        var sessionId = HttpClientUtils.HeaderGet(headers, SessionHeader).Trim();
        if (sessionId.Length == 0)
        {
            sessionId = HttpClientUtils.HeaderGet(headers, SessionHeaderHyphen).Trim();
        }

        if (sessionId.Length > 0)
        {
            return sessionId;
        }

        return ExtractSessionIdFromTurnMetadata(
            HttpClientUtils.HeaderGet(headers, TurnMetadataHeader).Trim());
    }

    /// <summary>Go: <c>http.Header.Values</c> — every value stored under the canonical key.</summary>
    internal static List<string> HeaderValues(Dictionary<string, List<string>>? headers, string key)
        => headers is not null
            && headers.TryGetValue(HttpClientUtils.CanonicalHeaderKey(key), out var values)
            ? values
            : [];
}

/// <summary>
/// Port of codex.TurnMetadata. Field order and the <c>workspaces</c>
/// <c>omitempty</c> rule follow the Go struct so the header JSON matches.
/// </summary>
public sealed class TurnMetadata
{
    [JsonPropertyName("installation_id")]
    public string InstallationId { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("thread_id")]
    public string ThreadId { get; set; } = string.Empty;

    [JsonPropertyName("turn_id")]
    public string TurnId { get; set; } = string.Empty;

    [JsonPropertyName("window_id")]
    public string WindowId { get; set; } = string.Empty;

    [JsonPropertyName("request_kind")]
    public string RequestKind { get; set; } = string.Empty;

    [JsonPropertyName("thread_source")]
    public string ThreadSource { get; set; } = string.Empty;

    [JsonPropertyName("sandbox")]
    public string Sandbox { get; set; } = string.Empty;

    /// <summary>Always emitted, even when zero (no <c>omitempty</c> in the reference).</summary>
    [JsonPropertyName("turn_started_at_unix_ms")]
    public long TurnStartedAtUnixMs { get; set; }

    /// <summary>
    /// Go: <c>json:"workspaces,omitempty"</c> — absent when nil or empty. The
    /// port emits <c>{}</c> for an explicitly-set empty map (difference C094);
    /// the transformer never sets this field.
    /// </summary>
    [JsonPropertyName("workspaces")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? Workspaces { get; set; }
}
