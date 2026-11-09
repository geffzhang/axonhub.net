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
    /// <summary>
    /// Port of objects.OAuthCredentialRef (internal/objects/channel.go:390-396):
    /// the stable identity the auto-disable bookkeeping uses for an OAuth
    /// channel, whose access token is replaced on every refresh. The sentinel
    /// must never reach outbound requests or credential-management consumers.
    /// </summary>
    public const string OAuthCredentialRef = "__oauth__";

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

    /// <summary>
    /// Port of <c>len(channel.DisabledAPIKeys)</c>: the number of stored
    /// disable entries, including elapsed ones and entries whose key is empty
    /// (<c>filterDisabled</c> only decides which entries are active). A
    /// malformed document counts zero, matching the lenient credential reads
    /// (C013).
    /// </summary>
    public static int CountDisabledApiKeys(string? disabledApiKeysJson)
    {
        if (string.IsNullOrWhiteSpace(disabledApiKeysJson))
        {
            return 0;
        }

        try
        {
            using var document = JsonDocument.Parse(disabledApiKeysJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>Port of GetAllAPIKeys: legacy apiKey (unless OAuth) first, then apiKeys.</summary>
    public static IReadOnlyList<string> GetAllApiKeys(string? credentialsJson)
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

    /// <summary>
    /// Port of GetAllCredentialRefs (internal/objects/channel.go:398-414): the
    /// identities every credential the channel can be disabled on. OAuth
    /// channels collapse to the single <see cref="OAuthCredentialRef"/>; every
    /// other channel uses its API keys.
    /// </summary>
    public static IReadOnlyList<string> GetAllCredentialRefs(string? credentialsJson)
    {
        if (IsOAuth(credentialsJson))
        {
            return [OAuthCredentialRef];
        }

        return GetAllApiKeys(credentialsJson);
    }

    /// <summary>
    /// Port of GetEnabledCredentialRefs: <see cref="GetAllCredentialRefs"/>
    /// minus the active disable records (empty keys and elapsed temporary
    /// disables do not hide a credential).
    /// </summary>
    public static IReadOnlyList<string> GetEnabledCredentialRefs(
        string? credentialsJson,
        IReadOnlyList<DisabledApiKey> disabledKeys,
        DateTimeOffset now)
    {
        var candidates = GetAllCredentialRefs(credentialsJson);
        if (disabledKeys.Count == 0)
        {
            return candidates;
        }

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var disabledKey in disabledKeys)
        {
            if (disabledKey.Key.Length == 0 || disabledKey.IsExpired(now))
            {
                continue;
            }

            disabled.Add(disabledKey.Key);
        }

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

    /// <summary>
    /// Port of objects.ChannelCredentials.IsOAuth (internal/objects/channel.go:446-458):
    /// an <c>oauth</c> object with a non-empty access token, or the legacy
    /// <c>apiKey</c> field holding OAuth JSON.
    /// </summary>
    public static bool IsOAuth(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(credentialsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var legacyApiKey = string.Empty;
            var oauthPresent = false;

            foreach (var property in root.EnumerateObject())
            {
                switch (FoldAscii(property.Name))
                {
                    case "apikey":
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            legacyApiKey = property.Value.GetString() ?? string.Empty;
                        }

                        break;
                    case "oauth":
                        oauthPresent = property.Value.ValueKind == JsonValueKind.Object
                            && HasNonEmptyAccessToken(property.Value);
                        break;
                }
            }

            return oauthPresent
                || (legacyApiKey.Length > 0 && LooksLikeOAuthJson(legacyApiKey));
        }
    }

    /// <summary>
    /// The raw <c>oauth</c> object of the credentials document, or null when the
    /// document has none. This is the value <c>buildCodexOutbound</c> re-marshals
    /// into OAuth credentials; a non-object value means Ent would have failed to
    /// decode the channel, so it is treated as absent here (C013).
    /// </summary>
    public static string? GetOAuthObjectJson(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(credentialsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? oauthJson = null;
            foreach (var property in root.EnumerateObject())
            {
                if (FoldAscii(property.Name) == "oauth")
                {
                    // Go's decoder lets the last duplicate key win.
                    oauthJson = property.Value.ValueKind == JsonValueKind.Object
                        ? property.Value.GetRawText()
                        : null;
                }
            }

            return oauthJson;
        }
    }

    /// <summary>
    /// The <c>apiKeys</c> array of the credentials document, in order. The
    /// reference reads the Ent-decoded field directly; this port decodes it
    /// leniently and treats a non-array value or non-string elements as
    /// unusable (C013).
    /// </summary>
    public static IReadOnlyList<string> GetApiKeys(string? credentialsJson)
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

            foreach (var property in root.EnumerateObject())
            {
                if (FoldAscii(property.Name) == "apikeys")
                {
                    // Go's decoder lets the last duplicate key win.
                    keys = ReadStringArray(property.Value);
                }
            }

            return keys;
        }
    }

    /// <summary>The legacy <c>apiKey</c> string of the credentials document (may hold OAuth JSON).</summary>
    public static string GetLegacyApiKey(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(credentialsJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var apiKey = string.Empty;
            foreach (var property in root.EnumerateObject())
            {
                if (FoldAscii(property.Name) == "apikey"
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    // Go's decoder lets the last duplicate key win.
                    apiKey = property.Value.GetString() ?? string.Empty;
                }
            }

            return apiKey;
        }
    }

    /// <summary>
    /// The channel's <c>gcp</c> object (objects.GCPCredential,
    /// internal/objects/channel.go:483-487), or null when the document has
    /// none. A non-object value means Ent would have failed to decode the
    /// channel; the port treats it as absent (C013).
    /// </summary>
    public static GcpCredential? GetGcpCredential(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(credentialsJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            GcpCredential? credential = null;
            foreach (var property in root.EnumerateObject())
            {
                if (FoldAscii(property.Name) != "gcp")
                {
                    continue;
                }

                // Go's decoder lets the last duplicate key win.
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    credential = null;
                    continue;
                }

                var region = string.Empty;
                var projectId = string.Empty;
                var jsonData = string.Empty;
                foreach (var field in property.Value.EnumerateObject())
                {
                    switch (FoldAscii(field.Name))
                    {
                        case "region":
                            region = StringValue(field.Value, region);
                            break;
                        case "projectid":
                            projectId = StringValue(field.Value, projectId);
                            break;
                        case "jsondata":
                            jsonData = StringValue(field.Value, jsonData);
                            break;
                    }
                }

                credential = new GcpCredential
                {
                    Region = region,
                    ProjectId = projectId,
                    JsonData = jsonData,
                };
            }

            return credential;
        }
    }

    /// <summary>
    /// The optional <c>managementApiKey</c> string of the credentials document
    /// (objects.ChannelCredentials.ManagementAPIKey, internal/objects/channel.go:493-494,
    /// used only for server-side quota checks such as ZenMux). A non-string
    /// value means Ent would have failed to decode the channel; the port treats
    /// it as absent (C013).
    /// </summary>
    public static string GetManagementApiKey(string? credentialsJson)
    {
        if (string.IsNullOrWhiteSpace(credentialsJson))
        {
            return string.Empty;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(credentialsJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var managementApiKey = string.Empty;
            foreach (var property in root.EnumerateObject())
            {
                // Go binds both the field name and the json tag; folding makes
                // "ManagementAPIKey" and "managementApiKey" the same key.
                if (FoldAscii(property.Name) == "managementapikey"
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    // Go's decoder lets the last duplicate key win; null into a
                    // string value is a no-op.
                    managementApiKey = property.Value.GetString() ?? string.Empty;
                }
            }

            return managementApiKey;
        }
    }

    private static string StringValue(JsonElement value, string current)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : current;

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
            // Go's decoder binds a key to either the struct field name or its
            // JSON tag, case-insensitively: both "AccessToken" (folded to
            // "accesstoken") and the tag "access_token" reach the field.
            var name = FoldAscii(property.Name);
            if ((name == "access_token" || name == "accesstoken")
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

/// <summary>Port of objects.GCPCredential (internal/objects/channel.go:483-487).</summary>
public sealed class GcpCredential
{
    public string Region { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    public string JsonData { get; init; } = string.Empty;
}
