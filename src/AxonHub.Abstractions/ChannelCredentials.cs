using System.Text.Json;

namespace AxonHub.Abstractions;

/// <summary>
/// Port of the credential half of objects.ChannelCredentials
/// (internal/objects/channel.go: GetAllAPIKeys, IsOAuth, GetEnabledAPIKeys,
/// filterDisabled).
/// </summary>
/// <remarks>
/// The reference decodes the channel row with Ent before calling these helpers,
/// so a malformed document would have failed the whole channel load. This port
/// reads the two JSON documents leniently and treats anything it cannot decode
/// as "no usable key"; the difference is registered as C013.
/// </remarks>
public static class ChannelCredentials
{
    /// <summary>Port of GetEnabledAPIKeys: legacy apiKey first, then apiKeys, minus active disables.</summary>
    public static IReadOnlyList<string> GetEnabledApiKeys(
        string? credentialsJson,
        string? disabledApiKeysJson,
        DateTimeOffset now)
    {
        var candidates = GetAllApiKeys(credentialsJson);
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var disabled = ActiveDisabledKeys(disabledApiKeysJson, now);
        if (disabled.Count == 0)
        {
            return candidates;
        }

        var enabled = new List<string>(candidates.Count);
        foreach (var key in candidates)
        {
            if (!disabled.Contains(key))
            {
                enabled.Add(key);
            }
        }

        return enabled;
    }

    /// <summary>Port of GetAllAPIKeys.</summary>
    private static List<string> GetAllApiKeys(string? credentialsJson)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return keys;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(credentialsJson);
        }
        catch (JsonException)
        {
            return keys;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return keys;
            }

            var oauthPresent = false;
            var apiKey = string.Empty;
            List<string>? apiKeys = null;

            foreach (var property in root.EnumerateObject())
            {
                switch (FoldAscii(property.Name))
                {
                    case "apikey":
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            apiKey = property.Value.GetString() ?? string.Empty;
                        }

                        break;
                    case "apikeys":
                        apiKeys = ReadStringArray(property.Value);
                        break;
                    case "oauth":
                        oauthPresent = property.Value.ValueKind == JsonValueKind.Object
                            && HasNonEmptyAccessToken(property.Value);
                        break;
                }
            }

            // IsOAuth also recognises OAuth credentials smuggled in the legacy
            // apiKey field: a JSON object containing "access_token".
            if (apiKey.Length > 0 && !oauthPresent && LooksLikeOAuthJson(apiKey))
            {
                oauthPresent = true;
            }

            if (apiKey.Length > 0 && !oauthPresent)
            {
                keys.Add(apiKey);
            }

            if (apiKeys is not null)
            {
                keys.AddRange(apiKeys);
            }
        }

        return keys;
    }

    private static List<string> ActiveDisabledKeys(string? disabledJson, DateTimeOffset now)
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(disabledJson))
        {
            return [];
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(disabledJson);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var key = string.Empty;
                DateTimeOffset? expiresAt = null;

                foreach (var property in entry.EnumerateObject())
                {
                    switch (FoldAscii(property.Name))
                    {
                        case "key":
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                key = property.Value.GetString() ?? string.Empty;
                            }

                            break;
                        case "expiresat":
                            if (property.Value.ValueKind == JsonValueKind.String
                                && DateTimeOffset.TryParse(
                                    property.Value.GetString(),
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    System.Globalization.DateTimeStyles.RoundtripKind,
                                    out var parsed))
                            {
                                expiresAt = parsed;
                            }

                            break;
                    }
                }

                // filterDisabled: an empty key or an elapsed disable is ignored.
                if (key.Length == 0)
                {
                    continue;
                }

                if (expiresAt is { } expiry && now > expiry)
                {
                    continue;
                }

                disabled.Add(key);
            }
        }

        return [.. disabled];
    }

    private static List<string> ReadStringArray(JsonElement value)
    {
        var result = new List<string>();
        if (value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                result.Add(element.GetString() ?? string.Empty);
            }
        }

        return result;
    }

    private static bool HasNonEmptyAccessToken(JsonElement oauth)
    {
        foreach (var property in oauth.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "accesstoken"
                && property.Value.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(property.Value.GetString()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeOAuthJson(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith('{') && trimmed.Contains("access_token", StringComparison.Ordinal);
    }

    /// <summary>Go's encoding/json field lookup folds ASCII letters only.</summary>
    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }
}
