using System.Text.Json;
using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Pass-through half of the reference <c>objects.ChannelSettings</c>
/// (internal/objects/channel.go:214-225): <c>passThroughUserAgent</c> and
/// <c>passThroughBody</c>. Both are tri-state — <see langword="null"/> inherits
/// the global system setting, an explicit value overrides it.
/// </summary>
[GenerateSerializer]
public sealed class ChannelPassThroughSettings
{
    /// <summary>Channel override for <c>system_user_agent_pass_through</c>.</summary>
    [Id(0)]
    public bool? PassThroughUserAgent { get; set; }

    /// <summary>Channel override for <c>system_pass_through</c>.</summary>
    [Id(1)]
    public bool? PassThroughBody { get; set; }

    /// <summary>
    /// Reads the pass-through fields from a stored <c>settings</c> jsonb value
    /// with Go's case-insensitive object key matching and last-duplicate-wins.
    /// The reference validates settings when the channel is written and fails
    /// the channel load on a malformed value; this build has no channel write
    /// API yet, so an unreadable value degrades to "inherit" instead (C037).
    /// </summary>
    public static ChannelPassThroughSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new ChannelPassThroughSettings();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return new ChannelPassThroughSettings();
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ChannelPassThroughSettings();
            }

            return new ChannelPassThroughSettings
            {
                PassThroughUserAgent = ReadBool(document.RootElement, "passThroughUserAgent"),
                PassThroughBody = ReadBool(document.RootElement, "passThroughBody"),
            };
        }
    }

    /// <summary>
    /// Reads a <c>*bool</c> field: a JSON bool yields its value, an explicit
    /// <c>null</c> or any other JSON kind yields <see langword="null"/> (inherit).
    /// </summary>
    private static bool? ReadBool(JsonElement settings, string name)
    {
        var found = false;
        var value = default(bool?);
        foreach (var property in settings.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;
            value = property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        return found ? value : null;
    }
}
