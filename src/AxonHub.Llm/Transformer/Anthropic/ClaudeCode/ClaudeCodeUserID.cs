using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

/// <summary>Port of llm/transformer/anthropic/claudecode/userid.go's <c>UserID</c>.</summary>
public sealed class ClaudeCodeUserID
{
    /// <summary>Go's <c>uuid.NameSpaceURL</c>.</summary>
    private static readonly Guid UrlNamespace = new("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

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

    /// <summary>Port of claudecode.BuildUserID: the v2 JSON representation.</summary>
    public static string BuildUserId(ClaudeCodeUserID uid)
    {
        ArgumentNullException.ThrowIfNull(uid);

        return GoJson.MarshalToString(uid);
    }

    /// <summary>
    /// Port of claudecode.GenerateUserID. A channel identity makes the device id
    /// and account UUID deterministic (SHA-256 hex and a name-based UUID v5 in
    /// the URL namespace); without one the device id is random. The session id
    /// comes from the shared request context — the port threads it on the raw
    /// request (C062) — and falls back to a fresh UUID.
    /// </summary>
    public static string GenerateUserId(string accountIdentity, string? sessionId)
    {
        string deviceId;
        var accountUuid = string.Empty;

        if (accountIdentity.Length > 0)
        {
            deviceId = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(accountIdentity)));
            accountUuid = UuidV5(UrlNamespace, Encoding.UTF8.GetBytes(accountIdentity))
                .ToString("D");
        }
        else
        {
            deviceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        }

        if (sessionId is null || sessionId.Trim().Length == 0)
        {
            sessionId = Guid.NewGuid().ToString("D");
        }

        return BuildUserId(new ClaudeCodeUserID
        {
            DeviceId = deviceId,
            AccountUuid = accountUuid,
            SessionId = sessionId,
        });
    }

    /// <summary>Port of <c>uuid.NewSHA1</c> (a name-based RFC 4122 version 5 UUID).</summary>
    private static Guid UuidV5(Guid space, ReadOnlySpan<byte> name)
    {
        Span<byte> input = stackalloc byte[16 + name.Length];
        space.TryWriteBytes(input, bigEndian: true, out _);
        name.CopyTo(input[16..]);

        Span<byte> hash = stackalloc byte[20];
#pragma warning disable CA5350 // google/uuid.NewSHA1 mandates SHA-1 for name-based UUIDs.
        SHA1.HashData(input, hash);
#pragma warning restore CA5350

        // Version 5 and the RFC 4122 variant.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }
}
