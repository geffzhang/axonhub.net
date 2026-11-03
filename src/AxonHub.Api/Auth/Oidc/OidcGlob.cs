namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of Go's <c>filepath.Match</c> (path/filepath/match.go) with Unix
/// separators, as used by <c>applyRoleMappings</c> to match group names. The
/// reference ignores the match error and falls back to a literal string
/// comparison; the port reproduces both outcomes. Windows escaping/separator
/// rules are not reproduced (C171).
/// </summary>
public static class OidcGlob
{
    private const char Separator = '/';

    /// <summary>Returns whether <paramref name="name"/> matches the glob pattern.</summary>
    public static bool Match(string pattern, string name)
        => MatchCore(pattern, name, out _);

    private static bool MatchCore(string pattern, string name, out bool badPattern)
    {
        badPattern = false;

        var patternIndex = 0;
        var nameIndex = 0;

        while (patternIndex < pattern.Length)
        {
            var star = false;
            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                star = true;
                patternIndex++;
            }

            var chunkStart = patternIndex;
            var inRange = false;
            while (patternIndex < pattern.Length)
            {
                var current = pattern[patternIndex];
                if (current == '\\')
                {
                    if (patternIndex + 1 < pattern.Length)
                    {
                        patternIndex++;
                    }
                }
                else if (current == '[')
                {
                    inRange = true;
                }
                else if (current == ']')
                {
                    inRange = false;
                }
                else if (current == '*' && !inRange)
                {
                    break;
                }

                patternIndex++;
            }

            var chunk = pattern[chunkStart..patternIndex];
            if (star && chunk.Length == 0)
            {
                // Trailing * matches the rest unless it contains a separator.
                return name.AsSpan(nameIndex).IndexOf(Separator) < 0;
            }

            var rest = MatchChunk(chunk, name[nameIndex..], out var ok, out badPattern);
            if (ok && (rest.Length == 0 || patternIndex < pattern.Length))
            {
                nameIndex = name.Length - rest.Length;
                continue;
            }

            if (badPattern)
            {
                return false;
            }

            if (star)
            {
                // Go retries the chunk at every later position that the star
                // may consume; a star cannot skip a separator.
                var limit = nameIndex;
                while (limit < name.Length && name[limit] != Separator)
                {
                    limit++;
                }

                for (var start = nameIndex + 1; start <= limit && start <= name.Length; start++)
                {
                    rest = MatchChunk(chunk, name[start..], out ok, out badPattern);
                    if (ok)
                    {
                        if (patternIndex >= pattern.Length && rest.Length > 0)
                        {
                            // The final chunk did not exhaust the name.
                            continue;
                        }

                        nameIndex = name.Length - rest.Length;
                        goto NextChunk;
                    }

                    if (badPattern)
                    {
                        return false;
                    }
                }
            }

            return false;

        NextChunk:
            continue;
        }

        return nameIndex == name.Length;
    }

    private static string MatchChunk(string chunk, string name, out bool ok, out bool badPattern)
    {
        ok = true;
        badPattern = false;

        var chunkIndex = 0;
        var nameIndex = 0;
        var failed = false;

        while (chunkIndex < chunk.Length)
        {
            if (!failed && nameIndex >= name.Length)
            {
                failed = true;
            }

            var current = chunk[chunkIndex];
            switch (current)
            {
                case '[':
                {
                    var matched = false;
                    chunkIndex++;
                    var negated = chunkIndex < chunk.Length && chunk[chunkIndex] == '^';
                    if (negated)
                    {
                        chunkIndex++;
                    }

                    var sample = failed ? '\0' : name[nameIndex];
                    var ranges = 0;
                    while (true)
                    {
                        if (chunkIndex < chunk.Length && chunk[chunkIndex] == ']' && ranges > 0)
                        {
                            chunkIndex++;
                            break;
                        }

                        if (!GetEsc(chunk, ref chunkIndex, out var low, out badPattern))
                        {
                            ok = false;
                            return string.Empty;
                        }

                        var high = low;
                        if (chunkIndex < chunk.Length && chunk[chunkIndex] == '-')
                        {
                            chunkIndex++;
                            if (!GetEsc(chunk, ref chunkIndex, out high, out badPattern))
                            {
                                ok = false;
                                return string.Empty;
                            }
                        }

                        if (!failed && low <= sample && sample <= high)
                        {
                            matched = true;
                        }

                        ranges++;
                    }

                    if (!failed)
                    {
                        nameIndex++;
                    }

                    if (matched == negated)
                    {
                        failed = true;
                    }

                    break;
                }

                case '?':
                    if (!failed)
                    {
                        if (name[nameIndex] == Separator)
                        {
                            failed = true;
                        }

                        nameIndex++;
                    }

                    chunkIndex++;
                    break;

                case '\\':
                    if (chunkIndex + 1 >= chunk.Length)
                    {
                        badPattern = true;
                        ok = false;
                        return string.Empty;
                    }

                    chunkIndex++;
                    if (!failed)
                    {
                        if (chunk[chunkIndex] != name[nameIndex])
                        {
                            failed = true;
                        }

                        nameIndex++;
                    }

                    chunkIndex++;
                    break;

                default:
                    if (!failed)
                    {
                        if (current != name[nameIndex])
                        {
                            failed = true;
                        }

                        nameIndex++;
                    }

                    chunkIndex++;
                    break;
            }
        }

        if (failed)
        {
            ok = false;
            return string.Empty;
        }

        ok = true;
        return name[nameIndex..];
    }

    private static bool GetEsc(string chunk, ref int index, out char value, out bool badPattern)
    {
        badPattern = false;
        value = '\0';

        if (index >= chunk.Length || chunk[index] == '-' || chunk[index] == ']')
        {
            badPattern = true;
            return false;
        }

        var current = chunk[index];
        if (current == '\\')
        {
            index++;
            if (index >= chunk.Length)
            {
                badPattern = true;
                return false;
            }

            current = chunk[index];
        }

        index++;
        value = current;
        if (index >= chunk.Length)
        {
            badPattern = true;
            return false;
        }

        return true;
    }
}
