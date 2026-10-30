using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Persistence;

namespace AxonHub.Api.Security;

/// <summary>
/// Port of biz.SecuritySettings (internal/server/biz/system.go). Only the fields
/// that affect request admission are modelled; unknown JSON members are ignored
/// exactly like Go's encoding/json.
/// </summary>
public sealed record SecuritySettings(
    IReadOnlyList<string> BlockedIps,
    bool ShowRequestLogIpBanIcon)
{
    /// <summary>Port of biz.defaultSecuritySettings.</summary>
    public static SecuritySettings Default { get; } = new(Array.Empty<string>(), ShowRequestLogIpBanIcon: true);
}

/// <summary>Read side of the reference's SystemService settings lookup.</summary>
public interface ISecuritySettingsProvider
{
    /// <summary>
    /// Port of SystemService.SecuritySettingsOrDefault: a missing row, a lookup
    /// failure or malformed JSON falls back to <see cref="SecuritySettings.Default"/>.
    /// </summary>
    Task<SecuritySettings> GetSecuritySettingsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the <c>systems</c> key/value table. The reference caches system values;
/// the port reads PostgreSQL on demand (C029).
/// </summary>
public sealed class SystemSettingsService(ISystemStore systems) : ISecuritySettingsProvider
{
    /// <summary>biz.SystemKeySecuritySettings.</summary>
    public const string SecuritySettingsKey = "security_settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Go matches JSON keys to struct tags case-insensitively.
        PropertyNameCaseInsensitive = true,
    };

    public async Task<SecuritySettings> GetSecuritySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(SecuritySettingsKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // SecuritySettingsOrDefault swallows every lookup error.
            return SecuritySettings.Default;
        }

        return row is null ? SecuritySettings.Default : Parse(row.Value);
    }

    private static SecuritySettings Parse(string value)
    {
        SecuritySettingsJson? stored;
        try
        {
            stored = JsonSerializer.Deserialize<SecuritySettingsJson>(value, JsonOptions);
        }
        catch (JsonException)
        {
            // SecuritySettings returns the unmarshal error; the OrDefault caller
            // then falls back to the defaults.
            return SecuritySettings.Default;
        }

        if (stored is null)
        {
            // Go unmarshals the literal "null" into the zero struct, which keeps
            // both the default blocked list and the default icon flag.
            return SecuritySettings.Default;
        }

        return new SecuritySettings(
            NormalizeBlockedIps(stored.BlockedIps),
            stored.ShowRequestLogIpBanIcon ?? true);
    }

    /// <summary>Port of normalizeSecuritySettings: trim, drop blanks, dedupe, keep order.</summary>
    private static string[] NormalizeBlockedIps(string[]? values)
    {
        if (values is null)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>(values.Length);
        foreach (var raw in values)
        {
            var value = raw.Trim();
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }

            normalized.Add(value);
        }

        return [.. normalized];
    }

    private sealed class SecuritySettingsJson
    {
        [JsonPropertyName("blocked_ips")]
        public string[]? BlockedIps { get; set; }

        [JsonPropertyName("show_request_log_ip_ban_icon")]
        public bool? ShowRequestLogIpBanIcon { get; set; }
    }
}
