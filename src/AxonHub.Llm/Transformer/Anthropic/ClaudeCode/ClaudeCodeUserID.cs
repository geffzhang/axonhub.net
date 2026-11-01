using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

/// <summary>
/// Port of llm/transformer/anthropic/claudecode/userid.go's <c>UserID</c>.
/// The reference also generates user ids for outbound requests
/// (<c>BuildUserID</c>/<c>GenerateUserID</c>); the port only needs the parsing
/// half until the claudecode outbound is ported.
/// </summary>
public sealed class ClaudeCodeUserID
{
    /// <summary>Matches the old Claude Code format: user_&lt;64hex&gt;_account__session_&lt;uuid-v4&gt;.</summary>
    private static readonly Regex LegacyPattern = new(
        "^user_([a-fA-F0-9]{64})_account__session_([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$",
        RegexOptions.CultureInvariant);

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("account_uuid")]
    public string AccountUuid { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Port of claudecode.ParseUserID: supports the legacy
    /// <c>user_&lt;hex&gt;_account__session_&lt;uuid&gt;</c> form and the v2 JSON
    /// form. Whitespace is trimmed first; anything else returns null.
    /// </summary>
    public static ClaudeCodeUserID? ParseUserId(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        raw = raw.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        // Try the v2 JSON format first.
        if (raw.StartsWith('{'))
        {
            ClaudeCodeUserID? uid;
            try
            {
                uid = LlmJson.Deserialize<ClaudeCodeUserID>(raw);
            }
            catch (JsonException)
            {
                return null;
            }

            return string.IsNullOrEmpty(uid?.SessionId) ? null : uid;
        }

        var matches = LegacyPattern.Match(raw);
        if (!matches.Success)
        {
            return null;
        }

        return new ClaudeCodeUserID
        {
            DeviceId = matches.Groups[1].Value,
            AccountUuid = string.Empty,
            SessionId = matches.Groups[2].Value,
        };
    }
}
