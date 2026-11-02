using System.Text.Json;
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
/// the port reads PostgreSQL on demand (C029). The JSON codec lives in
/// <see cref="SecuritySettingsCodec"/> so the management GraphQL surface shares it.
/// </summary>
public sealed class SystemSettingsService(ISystemStore systems) : ISecuritySettingsProvider
{
    /// <summary>biz.SystemKeySecuritySettings.</summary>
    public const string SecuritySettingsKey = SecuritySettingsCodec.SecuritySettingsKey;

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

        if (row is null)
        {
            return SecuritySettings.Default;
        }

        try
        {
            return SecuritySettingsCodec.Deserialize(row.Value);
        }
        catch (JsonException)
        {
            // SecuritySettings returns the unmarshal error; the OrDefault caller
            // then falls back to the defaults.
            return SecuritySettings.Default;
        }
    }
}
