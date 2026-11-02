using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Security;

/// <summary>
/// Go-compatible JSON codec and normalization for the <c>security_settings</c>
/// system setting (internal/server/biz/system.go:176-186, 1895-1973).
/// </summary>
public static class SecuritySettingsCodec
{
    /// <summary>biz.SystemKeySecuritySettings.</summary>
    public const string SecuritySettingsKey = "security_settings";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // Go matches JSON keys to struct tags case-insensitively.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Port of SecuritySettings: unmarshal into <c>securitySettingsJSON</c>, start
    /// from the defaults, take blocked IPs unconditionally, then normalize.
    /// </summary>
    /// <exception cref="JsonException">The document cannot be decoded.</exception>
    public static SecuritySettings Deserialize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var root = value.AsSpan().TrimStart();
        if (root.IsEmpty)
        {
            throw new JsonException("unexpected end of JSON input");
        }

        if (root.SequenceEqual("null"))
        {
            // json.Unmarshal("null", &stored) leaves the zero struct, which
            // keeps the default blocked list and icon flag.
            return Normalize(SecuritySettings.Default);
        }

        if (root[0] != '{')
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(root[0])
                + " into Go value of type biz.securitySettingsJSON");
        }

        var stored = JsonSerializer.Deserialize<SecuritySettingsJson>(value, ReadOptions)
            ?? new SecuritySettingsJson();

        // settings := defaultSecuritySettings
        var settings = SecuritySettings.Default with
        {
            BlockedIps = stored.BlockedIps?
                .Select(static ip => ip ?? string.Empty)
                .ToList() ?? [],
        };

        if (stored.ShowRequestLogIpBanIcon is { } showIcon)
        {
            settings = settings with { ShowRequestLogIpBanIcon = showIcon };
        }

        return Normalize(settings);
    }

    /// <summary>Port of normalizeSecuritySettings: trim, drop blanks, dedupe, keep order.</summary>
    public static SecuritySettings Normalize(SecuritySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(settings.BlockedIps.Count);
        foreach (var raw in settings.BlockedIps)
        {
            var value = raw.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }

            normalized.Add(value);
        }

        return settings with { BlockedIps = normalized };
    }

    /// <summary>Port of json.Marshal(biz.SecuritySettings).</summary>
    public static string Serialize(SecuritySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var write = new SecuritySettingsJson
        {
            BlockedIps = settings.BlockedIps
                .Select(static ip => (string?)ip)
                .ToList(),
            ShowRequestLogIpBanIcon = settings.ShowRequestLogIpBanIcon,
        };

        return GoJson.MarshalToString(write);
    }

    private static string GoKindName(char first) => first switch
    {
        '[' => "array",
        '"' => "string",
        't' or 'f' => "bool",
        '-' or (>= '0' and <= '9') => "number",
        _ => "object",
    };

    private sealed class SecuritySettingsJson
    {
        [JsonPropertyName("blocked_ips")]
        public List<string?>? BlockedIps { get; set; }

        [JsonPropertyName("show_request_log_ip_ban_icon")]
        public bool? ShowRequestLogIpBanIcon { get; set; }
    }
}
