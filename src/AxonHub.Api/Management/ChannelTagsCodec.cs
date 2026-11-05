using System.Text.Json;

namespace AxonHub.Api.Management;

/// <summary>
/// Decoder for the <c>channels.tags</c> jsonb column and the profile tag
/// matcher (objects.MatchChannelTags, internal/objects/apikey.go:66-94).
/// </summary>
/// <remarks>
/// Ent decodes the column into <c>[]string</c> when the channel row is loaded;
/// the port keeps the raw text, so the decode happens where the tags are read.
/// A malformed column fails the reference's row scan entirely; the port
/// degrades to "no tags" exactly like the other port JSON readers (C141).
/// </remarks>
public static class ChannelTagsCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Decodes <c>channels.tags</c>; null and <c>null</c> are no tags.</summary>
    public static HashSet<string> Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var values = JsonSerializer.Deserialize<string?[]>(raw, JsonOptions);
            return values is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(
                    values.Select(static value => value ?? string.Empty),
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Port of objects.MatchChannelTags.</summary>
    public static bool Match(
        IReadOnlyList<string> allowedTags,
        string matchMode,
        IReadOnlyCollection<string> channelTags)
    {
        // OrDefault: only "all" and "none" are distinct; anything else (empty,
        // "any", an unknown stored value) is the any-overlap rule.
        if (string.Equals(matchMode, "all", StringComparison.Ordinal))
        {
            foreach (var allowedTag in allowedTags)
            {
                if (!channelTags.Contains(allowedTag))
                {
                    return false;
                }
            }

            return true;
        }

        if (string.Equals(matchMode, "none", StringComparison.Ordinal))
        {
            foreach (var tag in channelTags)
            {
                if (allowedTags.Contains(tag, StringComparer.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var tag in channelTags)
        {
            if (allowedTags.Contains(tag, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
