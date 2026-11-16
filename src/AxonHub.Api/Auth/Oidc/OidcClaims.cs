using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of biz.oidcClaims (internal/server/biz/oidc.go:787-801) and the
/// mapstructure/json decoding around it. Field lookups are case-insensitive,
/// like encoding/json and mapstructure; a value of the wrong type leaves the
/// field at its zero value.
/// </summary>
public sealed record OidcClaims
{
    public string Sub { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public bool EmailVerified { get; init; }

    public string Name { get; init; } = string.Empty;

    public string GivenName { get; init; } = string.Empty;

    public string FamilyName { get; init; } = string.Empty;

    public string PreferredUsername { get; init; } = string.Empty;

    public string Picture { get; init; } = string.Empty;

    /// <summary>Filled manually from the configured group claims.</summary>
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>Port of <c>mapstructure.Decode(raw, &amp;claims)</c>.</summary>
    public static OidcClaims FromRaw(IReadOnlyDictionary<string, object?> raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        return new OidcClaims
        {
            Sub = GetString(raw, "sub"),
            Email = GetString(raw, "email"),
            EmailVerified = GetBool(raw, "email_verified"),
            Name = GetString(raw, "name"),
            GivenName = GetString(raw, "given_name"),
            FamilyName = GetString(raw, "family_name"),
            PreferredUsername = GetString(raw, "preferred_username"),
            Picture = GetString(raw, "picture"),
        };
    }

    /// <summary>
    /// Port of <c>OIDCService.extractGroups</c> (oidc.go:868-905): read the
    /// configured claim names (default <c>groups</c>, <c>roles</c>), drop
    /// non-string array items, apply the optional regex replacement and
    /// lowercase unless the parser is case sensitive.
    /// </summary>
    public static IReadOnlyList<string>? ExtractGroups(
        IReadOnlyDictionary<string, object?> raw,
        OidcProviderConfig config,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        var claimNames = config.GroupClaims.Count > 0
            ? config.GroupClaims
            : DefaultGroupClaims;

        var allGroups = new List<string>();
        foreach (var claim in claimNames)
        {
            if (raw.TryGetValue(claim, out var value))
            {
                var groups = ParseGroups(value);
                if (groups.Count > 0)
                {
                    allGroups.AddRange(groups);
                }
            }
        }

        if (allGroups.Count == 0)
        {
            return null;
        }

        Regex? regex = null;
        if (config.GroupParser.RegexReplacePattern.Length > 0)
        {
            try
            {
                regex = new Regex(
                    config.GroupParser.RegexReplacePattern,
                    RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                LogInvalidGroupRegex(logger, ex);
            }
        }

        var results = new List<string>(allGroups.Count);
        foreach (var group in allGroups)
        {
            var value = group;
            if (regex is not null)
            {
                value = regex.Replace(value, config.GroupParser.RegexReplaceWith);
            }

            if (!config.GroupParser.CaseSensitive)
            {
                value = value.ToLowerInvariant();
            }

            results.Add(value);
        }

        return results;
    }

    /// <summary>Port of <c>parseGroups</c> (oidc.go:907-935).</summary>
    public static List<string> ParseGroups(object? value)
    {
        switch (value)
        {
            case string text:
                return [text];
            case List<object?> items:
                var result = new List<string>(items.Count);
                foreach (var item in items)
                {
                    if (item is string itemText)
                    {
                        result.Add(itemText);
                    }
                }

                return result;
            case string[] strings:
                return [.. strings];
            default:
                return [];
        }
    }

    private static readonly string[] DefaultGroupClaims = ["groups", "roles"];

    private static readonly Action<ILogger, Exception?> LogInvalidGroupRegex =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "OidcInvalidGroupParserRegex"),
            "Invalid GroupParser regex");

    private static string GetString(IReadOnlyDictionary<string, object?> raw, string name)
        => Lookup(raw, name) is string value ? value : string.Empty;

    private static bool GetBool(IReadOnlyDictionary<string, object?> raw, string name)
        => Lookup(raw, name) is bool value && value;

    private static object? Lookup(IReadOnlyDictionary<string, object?> raw, string name)
    {
        if (raw.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var pair in raw)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes a JSON object into Go's <c>any</c> shapes: string, bool,
    /// double, nil, <c>[]any</c> and <c>map[string]any</c>.
    /// </summary>
    public static Dictionary<string, object?> FromJson(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ToGoValue(property.Value);
        }

        return result;
    }

    /// <summary>Converts one JSON value to the Go <c>any</c> shape.</summary>
    public static object? ToGoValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetDouble(out var number) ? number : 0d,
        JsonValueKind.Array => ParseArray(element),
        JsonValueKind.Object => FromJson(element),
        _ => null,
    };

    private static List<object?> ParseArray(JsonElement element)
    {
        var items = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            items.Add(ToGoValue(item));
        }

        return items;
    }
}
