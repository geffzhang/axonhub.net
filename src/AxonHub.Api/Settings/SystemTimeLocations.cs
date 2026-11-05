using System.Text.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Settings;

/// <summary>
/// Port of <c>biz.SystemService.TimeLocation</c>
/// (internal/server/biz/system.go:1504-1546) plus the <c>system_general_settings</c>
/// JSON decoding it relies on. The name is kept next to the zone because the
/// reference passes <c>loc.String()</c> into the Postgres <c>AT TIME ZONE</c>
/// expression (analytics daily statistics).
/// </summary>
/// <remarks>
/// The reference caches the first resolved location process-wide; the port
/// re-reads the settings row on every call (C186). A read failure, a missing
/// row, malformed JSON, an empty timezone or an unknown zone all resolve to UTC.
/// </remarks>
public static class SystemTimeLocations
{
    /// <summary>biz.SystemKeyGeneralSettings (internal/server/biz/system.go:96).</summary>
    public const string GeneralSettingsKey = "system_general_settings";

    /// <summary>
    /// The resolved zone plus the name Go's <c>*time.Location.String()</c>
    /// returns: the configured string for a loadable zone, "UTC" for the
    /// fallback, and "Local" for the special local name.
    /// </summary>
    public readonly record struct Location(TimeZoneInfo Zone, string Name);

    private static readonly Location Utc = new(TimeZoneInfo.Utc, "UTC");

    public static async Task<Location> ResolveAsync(
        ISystemStore systems,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(systems);

        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(GeneralSettingsKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return Utc;
        }

        if (row is null)
        {
            // GeneralSettings returns defaultGeneralSettings {Timezone: "UTC"}.
            return Utc;
        }

        string timezone;
        try
        {
            timezone = GeneralSettingsTimezone(row.Value);
        }
        catch (JsonException)
        {
            return Utc;
        }

        if (timezone.Length == 0)
        {
            return Utc;
        }

        // time.LoadLocation("Local") returns the process's local location and
        // String() reports "Local".
        if (string.Equals(timezone, "Local", StringComparison.Ordinal))
        {
            return new Location(TimeZoneInfo.Local, "Local");
        }

        try
        {
            return new Location(TimeZoneInfo.FindSystemTimeZoneById(timezone), timezone);
        }
        catch (Exception)
        {
            return Utc;
        }
    }

    /// <summary>
    /// Port of <c>json.Unmarshal</c> into biz.SystemGeneralSettings
    /// (internal/server/biz/system.go:138-142): both fields carry string types,
    /// a JSON null document leaves the zero value, and a non-string member makes
    /// the whole document invalid.
    /// </summary>
    internal static string GeneralSettingsTimezone(string? json)
    {
        using var document = JsonDocument.Parse(json ?? string.Empty);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + root.ValueKind
                + " into Go value of type biz.SystemGeneralSettings");
        }

        // Decode both known members so a mistyped currency_code invalidates the
        // document exactly like the reference's struct unmarshal.
        _ = ReadStringMember(root, "currency_code");
        return ReadStringMember(root, "timezone");
    }

    private static string ReadStringMember(JsonElement root, string name)
    {
        JsonElement? matched = null;
        if (root.TryGetProperty(name, out var exact))
        {
            matched = exact;
        }
        else
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    matched = property.Value;
                    break;
                }
            }
        }

        if (matched is null)
        {
            return string.Empty;
        }

        return matched.Value.ValueKind switch
        {
            JsonValueKind.String => matched.Value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            _ => throw new JsonException(
                "json: cannot unmarshal " + matched.Value.ValueKind
                + " into Go struct field SystemGeneralSettings." + name + " of type string"),
        };
    }
}
