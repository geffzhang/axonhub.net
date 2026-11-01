using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace AxonHub.Api.PromptProtection;

/// <summary>
/// Port of the pattern cache and the two matching helpers in
/// internal/server/biz/prompt_protection_rule.go. The reference compiles with
/// github.com/dlclark/regexp2 (a .NET-style backtracking engine), so .NET
/// <see cref="Regex"/> is the closest equivalent; differences are recorded in
/// the porting ledger (C092).
/// </summary>
internal static class PromptProtectionRegex
{
    private sealed record CachedPattern(Regex? Regex, Exception? Error);

    // xmap.New with no eviction: a compile failure is cached too, so an invalid
    // stored pattern does not re-compile on every message.
    private static readonly ConcurrentDictionary<string, CachedPattern> Cache =
        new(StringComparer.Ordinal);

    /// <summary>Port of biz.MatchPromptProtectionRule.</summary>
    public static bool Match(string pattern, string content)
    {
        var regex = GetOrCompile(pattern);
        if (regex is null)
        {
            return false;
        }

        try
        {
            return regex.IsMatch(content);
        }
        catch (Exception)
        {
            // regexp2 surfaces match-time errors (for example a backtracking
            // limit); the reference treats them as "no match".
            return false;
        }
    }

    /// <summary>Port of biz.ReplacePromptProtectionRule (replace every match).</summary>
    public static string Replace(string pattern, string content, string replacement)
    {
        var regex = GetOrCompile(pattern);
        if (regex is null)
        {
            return content;
        }

        try
        {
            return regex.Replace(content, replacement);
        }
        catch (Exception)
        {
            return content;
        }
    }

    private static Regex? GetOrCompile(string pattern)
    {
        var cached = Cache.GetOrAdd(pattern, static value =>
        {
            try
            {
                // regexp2.Compile(pattern, regexp2.None): no timeout, no
                // culture-sensitive options.
                return new CachedPattern(new Regex(value, RegexOptions.None), null);
            }
            catch (ArgumentException error)
            {
                return new CachedPattern(null, error);
            }
        });

        return cached.Regex;
    }
}
