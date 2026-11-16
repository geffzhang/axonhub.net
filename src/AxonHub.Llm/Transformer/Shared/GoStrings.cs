namespace AxonHub.Llm.Transformer.Shared;

/// <summary>Port of the Go <c>strings</c> helpers transformers rely on.</summary>
public static class GoStrings
{
    /// <summary>
    /// Port of <c>strings.TrimSpace</c>: trims the Unicode White_Space set that
    /// <c>unicode.IsSpace</c> reports.
    /// </summary>
    public static string TrimSpace(string value)
    {
        var start = 0;
        while (start < value.Length && IsGoSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsGoSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsGoSpace(char c) => Array.IndexOf(GoWhitespace, c) >= 0;

    // Same set as Go's unicode.IsSpace: White_Space plus the ASCII controls.
    private static readonly char[] GoWhitespace =
    [
        (char)0x0009, (char)0x000A, (char)0x000B, (char)0x000C, (char)0x000D,
        (char)0x0020, (char)0x0085, (char)0x00A0, (char)0x1680,
        (char)0x2000, (char)0x2001, (char)0x2002, (char)0x2003, (char)0x2004,
        (char)0x2005, (char)0x2006, (char)0x2007, (char)0x2008, (char)0x2009,
        (char)0x200A, (char)0x2028, (char)0x2029, (char)0x202F, (char)0x205F,
        (char)0x3000
    ];
}
