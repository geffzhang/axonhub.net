using System.Buffers;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AxonHub.Api.Models;

/// <summary>
/// Port of internal/pkg/xregexp.MatchString. A pattern without any regex
/// metacharacter is an exact, case-sensitive comparison; <c>*</c> matches
/// everything; anything else is compiled as an anchored regex. The reference
/// uses regexp2 (a .NET-compatible engine, already anchored with
/// <c>^(?:...)$</c>), so the port compiles the same anchored pattern with
/// System.Text.RegularExpressions. Compile errors and their cache are kept in a
/// process-wide dictionary, exactly like the reference's globalCache.
/// </summary>
public static class XRegexp
{
    private static readonly SearchValues<char> MetaCharacters =
        SearchValues.Create("*?+[]{}()^$.|\\");

    /// <summary>Characters that make a leading "(?…)" group something other than a modifier.</summary>
    private static readonly SearchValues<char> GroupIntroducers = SearchValues.Create(":=!<");

    private static readonly ConcurrentDictionary<string, Regex?> Cache = new(StringComparer.Ordinal);

    /// <summary>Port of xregexp.MatchString.</summary>
    public static bool MatchString(string pattern, string value)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(value);

        if (pattern == "*")
        {
            return true;
        }

        // No metacharacter means plain string equality (not a substring test).
        if (pattern.AsSpan().IndexOfAny(MetaCharacters) < 0)
        {
            return string.Equals(pattern, value, StringComparison.Ordinal);
        }

        var regex = Cache.GetOrAdd(pattern, static cached => Compile(cached));
        return regex is not null && regex.IsMatch(value);
    }

    /// <summary>
    /// Port of xregexp.Filter: an empty or uncompilable pattern matches nothing,
    /// <c>*</c> matches everything, a metacharacter-free pattern is exact
    /// equality, and anything else is an anchored regex. First-seen order is
    /// preserved.
    /// </summary>
    public static List<string> Filter(IReadOnlyList<string> items, string pattern)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(pattern);

        var matched = new List<string>(items.Count);
        if (pattern.Length == 0)
        {
            return matched;
        }

        if (pattern == "*")
        {
            matched.AddRange(items);
            return matched;
        }

        if (pattern.AsSpan().IndexOfAny(MetaCharacters) < 0)
        {
            foreach (var item in items)
            {
                if (string.Equals(pattern, item, StringComparison.Ordinal))
                {
                    matched.Add(item);
                }
            }

            return matched;
        }

        var regex = Cache.GetOrAdd(pattern, static cached => Compile(cached));
        if (regex is null)
        {
            return matched;
        }

        foreach (var item in items)
        {
            if (regex.IsMatch(item))
            {
                matched.Add(item);
            }
        }

        return matched;
    }

    /// <summary>
    /// Port of xregexp.ValidateRegex: an empty pattern is valid, and a pattern
    /// that failed to compile reports the raw pattern unquoted.
    /// </summary>
    public static string? ValidateRegex(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
        {
            return null;
        }

        var regex = Cache.GetOrAdd(pattern, static cached => Compile(cached));
        return regex is null ? "invalid regex pattern: " + pattern : null;
    }

    private static Regex? Compile(string pattern)
    {
        try
        {
            // regexp2 folds case with Unicode simple folding, independent of the
            // current culture; CultureInvariant reproduces that for (?i).
            return new Regex(
                EnsureAnchored(pattern),
                RegexOptions.CultureInvariant,
                Regex.InfiniteMatchTimeout);
        }
        catch (ArgumentException)
        {
            // getOrCreatePattern marks the entry as a compile error and
            // MatchString returns false forever after.
            return null;
        }
    }

    /// <summary>Port of ensureAnchored: keep an inline modifier, wrap the body.</summary>
    private static string EnsureAnchored(string pattern)
    {
        var (modifier, body) = SplitInlineModifier(pattern);
        if (body.StartsWith('^'))
        {
            body = body[1..];
        }

        if (body.EndsWith('$'))
        {
            body = body[..^1];
        }

        return modifier + "^(?:" + body + ")$";
    }

    /// <summary>
    /// Port of splitInlineModifier: a leading <c>(?flags)</c> group stays a
    /// modifier, while groups such as <c>(?:...)</c>, <c>(?=...)</c>,
    /// <c>(?!...)</c> and <c>(?&lt;...&gt;)</c> are part of the body.
    /// </summary>
    private static (string Modifier, string Body) SplitInlineModifier(string pattern)
    {
        if (!pattern.StartsWith("(?", StringComparison.Ordinal))
        {
            return (string.Empty, pattern);
        }

        var end = pattern.IndexOf(')');
        if (end <= 2)
        {
            return (string.Empty, pattern);
        }

        var modifier = pattern[..(end + 1)];
        var body = pattern[(end + 1)..];

        if (modifier.AsSpan(2, end - 2).IndexOfAny(GroupIntroducers) >= 0)
        {
            return (string.Empty, pattern);
        }

        return (modifier, body);
    }
}
