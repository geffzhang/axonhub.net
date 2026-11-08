using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Wire JSON settings shared by every transformer.
/// </summary>
/// <remarks>
/// Known divergence (difference ledger C002): Go's encoding/json HTML-escapes
/// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> while leaving other non-ASCII raw;
/// this encoder leaves both raw. The bytes differ when payloads contain those
/// characters, the parsed JSON does not.
/// </remarks>
public static class LlmJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
        // Go's json decoder matches field names case-insensitively.
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        // Go sorts map keys on marshal; see GoSortedMapConverterFactory.
        Converters = { new GoSortedMapConverterFactory(), new GoDoubleJsonConverter() },
    };

    /// <summary>Compact-serialize with the shared settings (Go json.Marshal equivalent).</summary>
    public static byte[] SerializeToUtf8Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Decodes a UTF-8 body the way Go's <c>json.Unmarshal</c> does: ill-formed
    /// bytes and lone surrogate escapes inside string literals are repaired to
    /// U+FFFD first (see <see cref="GoJsonUtf8"/>).
    /// </summary>
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Json)
    {
        var sanitized = GoJsonUtf8.SanitizeStringLiterals(utf8Json);
        return sanitized is null
            ? JsonSerializer.Deserialize<T>(utf8Json, Options)
            : JsonSerializer.Deserialize<T>(sanitized, Options);
    }

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
