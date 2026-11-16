using System.Text;
using System.Text.RegularExpressions;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of <c>github.com/aptible/supercronic/cronexpr.Parse</c> v0.2.39, the
/// parser behind <c>normalizeAutoDisableRules</c>. Only validation is ported;
/// the reference also evaluates the expression when an auto-disable rule fires
/// (C017). Error strings match the library verbatim.
/// </summary>
/// <remarks>
/// The parser is intentionally structural: <c>Parse</c> accepts 5 to 7 fields
/// (minute-first for 5/6, second-first for 7), the <c>@alias</c> descriptors,
/// names, ranges, steps, <c>L</c>/<c>W</c> day modifiers and <c>dow#n</c>; the
/// non-strict entry point ignores fields after the seventh.
/// </remarks>
public static class CronExpression
{
    /// <summary>Returns the parser error, or <see langword="null"/> when the line is valid.</summary>
    public static string? Validate(string cronLine)
        => TryParse(cronLine, out var error) ? null : error;

    /// <summary>Port of <c>cronexpr.Parse</c> (non-strict).</summary>
    public static bool TryParse(string cronLine, out string? error)
    {
        ArgumentNullException.ThrowIfNull(cronLine);

        var cron = Normalize(cronLine);
        var fields = FindFields(cron);

        if (fields.Count < 5)
        {
            error = "missing field(s)";
            return false;
        }

        var fieldCount = Math.Min(fields.Count, 7);
        var field = 0;

        // A seven-field line starts with seconds; six or five starts with minutes.
        if (fieldCount == 7)
        {
            var secondError = ParseGenericField(FieldSlice(cron, fields[field]), SecondDescriptor);
            if (secondError is not null)
            {
                error = secondError;
                return false;
            }

            field++;
        }

        var minuteError = ParseGenericField(FieldSlice(cron, fields[field]), MinuteDescriptor);
        if (minuteError is not null)
        {
            error = minuteError;
            return false;
        }

        field++;
        var hourError = ParseGenericField(FieldSlice(cron, fields[field]), HourDescriptor);
        if (hourError is not null)
        {
            error = hourError;
            return false;
        }

        field++;
        var domError = ParseDomField(FieldSlice(cron, fields[field]));
        if (domError is not null)
        {
            error = domError;
            return false;
        }

        field++;
        var monthError = ParseGenericField(FieldSlice(cron, fields[field]), MonthDescriptor);
        if (monthError is not null)
        {
            error = monthError;
            return false;
        }

        field++;
        var dowError = ParseDowField(FieldSlice(cron, fields[field]));
        if (dowError is not null)
        {
            error = dowError;
            return false;
        }

        field++;
        if (field < fieldCount)
        {
            var yearError = ParseGenericField(FieldSlice(cron, fields[field]), YearDescriptor);
            if (yearError is not null)
            {
                error = yearError;
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string FieldSlice(string cron, (int Start, int End) field)
        => cron[field.Start..field.End];

    /// <summary>
    /// <c>\S+</c> from Go's regexp: the five ASCII whitespace characters only,
    /// so Unicode spaces are part of a field (unlike .NET's <c>\s</c>).
    /// </summary>
    private static List<(int Start, int End)> FindFields(string cron)
    {
        var fields = new List<(int, int)>();
        var index = 0;

        while (index < cron.Length)
        {
            if (IsGoSpace(cron[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < cron.Length && !IsGoSpace(cron[index]))
            {
                index++;
            }

            fields.Add((start, index));
        }

        return fields;
    }

    private static bool IsGoSpace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    /// <summary>
    /// Port of <c>strings.NewReplacer</c> with the cron aliases: a single
    /// left-to-right pass, earliest argument wins at the same position.
    /// </summary>
    private static string Normalize(string cronLine)
    {
        var result = new StringBuilder(cronLine.Length + 16);
        var index = 0;

        while (index < cronLine.Length)
        {
            var replaced = false;
            foreach (var (alias, replacement) in Aliases)
            {
                if (index + alias.Length <= cronLine.Length
                    && cronLine.AsSpan(index, alias.Length).SequenceEqual(alias))
                {
                    result.Append(replacement);
                    index += alias.Length;
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
            {
                result.Append(cronLine[index]);
                index++;
            }
        }

        return result.ToString();
    }

    private static readonly (string Alias, string Replacement)[] Aliases =
    [
        ("@yearly", "0 0 0 1 1 * *"),
        ("@annually", "0 0 0 1 1 * *"),
        ("@monthly", "0 0 0 1 * * *"),
        ("@weekly", "0 0 0 * * 0 *"),
        ("@daily", "0 0 0 * * * *"),
        ("@hourly", "0 0 * * * * *"),
    ];

    private static string? ParseGenericField(string value, FieldDescriptor descriptor)
    {
        var directives = ParseDirectives(value, descriptor, out var error);
        if (directives is null)
        {
            return error;
        }

        foreach (var directive in directives)
        {
            if (directive.Kind == DirectiveKind.None)
            {
                var raw = value[directive.Start..directive.End];
                return "syntax error in " + descriptor.Name + " field: '" + raw + "'";
            }
        }

        return null;
    }

    private static string? ParseDomField(string value)
    {
        var directives = ParseDirectives(value, DomDescriptor, out var error);
        if (directives is null)
        {
            return error;
        }

        foreach (var directive in directives)
        {
            if (directive.Kind != DirectiveKind.None)
            {
                continue;
            }

            var raw = value[directive.Start..directive.End];
            var normal = raw.ToLowerInvariant();

            if (LastDomRegex.IsMatch(normal) || LastWorkDomRegex.IsMatch(normal))
            {
                continue;
            }

            if (WorkDomRegex(DomDescriptor.ValuePattern).IsMatch(normal))
            {
                continue;
            }

            return "syntax error in day-of-month field: '" + raw + "'";
        }

        return null;
    }

    private static string? ParseDowField(string value)
    {
        var directives = ParseDirectives(value, DowDescriptor, out var error);
        if (directives is null)
        {
            return error;
        }

        foreach (var directive in directives)
        {
            if (directive.Kind != DirectiveKind.None)
            {
                continue;
            }

            var raw = value[directive.Start..directive.End];
            var normal = raw.ToLowerInvariant();

            if (DowOfLastWeekRegex(DowDescriptor.ValuePattern).IsMatch(normal)
                || DowOfSpecificWeekRegex(DowDescriptor.ValuePattern).IsMatch(normal))
            {
                continue;
            }

            return "syntax error in day-of-week field: '" + raw + "'";
        }

        return null;
    }

    private static List<CronDirective>? ParseDirectives(
        string value,
        FieldDescriptor descriptor,
        out string? error)
    {
        var matches = EntryRegex.Matches(value);
        if (matches.Count == 0)
        {
            error = descriptor.Name + " field: missing directive";
            return null;
        }

        var directives = new List<CronDirective>(matches.Count);

        foreach (Match match in matches)
        {
            var directive = new CronDirective { Start = match.Index, End = match.Index + match.Length };
            var normal = match.Value.ToLowerInvariant();

            if (WildcardRegex.IsMatch(normal))
            {
                directive.Kind = DirectiveKind.All;
                directive.First = descriptor.Min;
                directive.Last = descriptor.Max;
                directive.Step = 1;
                directives.Add(directive);
                continue;
            }

            if (ValueRegex(descriptor.ValuePattern).IsMatch(normal))
            {
                directive.Kind = DirectiveKind.One;
                directive.First = descriptor.Atoi(normal);
                directives.Add(directive);
                continue;
            }

            var rangeMatch = RangeRegex(descriptor.ValuePattern).Match(normal);
            if (rangeMatch.Success)
            {
                directive.Kind = DirectiveKind.Span;
                directive.First = descriptor.Atoi(rangeMatch.Groups[1].Value);
                directive.Last = descriptor.Atoi(rangeMatch.Groups[2].Value);
                directive.Step = 1;

                if (directive.Last < directive.First)
                {
                    error = "invalid interval " + normal
                        + " (normalized to " + directive.First + "-" + directive.Last + ")";
                    return null;
                }

                directives.Add(directive);
                continue;
            }

            var wildcardIntervalMatch = WildcardIntervalRegex.Match(normal);
            if (wildcardIntervalMatch.Success)
            {
                directive.Kind = DirectiveKind.Span;
                directive.First = descriptor.Min;
                directive.Last = descriptor.Max;
                directive.Step = Atoi(wildcardIntervalMatch.Groups[1].Value);

                if (directive.Step < 1 || directive.Step > descriptor.Max)
                {
                    error = "invalid interval " + normal;
                    return null;
                }

                directives.Add(directive);
                continue;
            }

            var valueIntervalMatch = ValueIntervalRegex(descriptor.ValuePattern).Match(normal);
            if (valueIntervalMatch.Success)
            {
                directive.Kind = DirectiveKind.Span;
                directive.First = descriptor.Atoi(valueIntervalMatch.Groups[1].Value);
                directive.Last = descriptor.Max;
                directive.Step = Atoi(valueIntervalMatch.Groups[2].Value);

                if (directive.Step < 1 || directive.Step > descriptor.Max)
                {
                    error = "invalid interval " + normal;
                    return null;
                }

                directives.Add(directive);
                continue;
            }

            var rangeIntervalMatch = RangeIntervalRegex(descriptor.ValuePattern).Match(normal);
            if (rangeIntervalMatch.Success)
            {
                directive.Kind = DirectiveKind.Span;
                directive.First = descriptor.Atoi(rangeIntervalMatch.Groups[1].Value);
                directive.Last = descriptor.Atoi(rangeIntervalMatch.Groups[2].Value);
                directive.Step = Atoi(rangeIntervalMatch.Groups[3].Value);

                if (directive.Step < 1 || directive.Step > descriptor.Max)
                {
                    error = "invalid interval " + normal;
                    return null;
                }

                directives.Add(directive);
                continue;
            }

            directive.Kind = DirectiveKind.None;
            directives.Add(directive);
        }

        error = null;
        return directives;
    }

    private enum DirectiveKind
    {
        None,
        One,
        Span,
        All,
    }

    private sealed class CronDirective
    {
        public DirectiveKind Kind { get; set; }
        public int First { get; set; }
        public int Last { get; set; }
        public int Step { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
    }

    private sealed record FieldDescriptor(
        string Name,
        int Min,
        int Max,
        int[] DefaultList,
        string ValuePattern,
        Func<string, int> Atoi);

    private static readonly int[] GenericDefaultList = Enumerable.Range(0, 60).ToArray();
    private static readonly int[] YearDefaultList = Enumerable.Range(1970, 130).ToArray();

    private static readonly Dictionary<string, int> NumberTokens = BuildNumberTokens();

    private static readonly Dictionary<string, int> MonthTokens = new(StringComparer.Ordinal)
    {
        ["01"] = 1,
        ["1"] = 1,
        ["jan"] = 1,
        ["january"] = 1,
        ["02"] = 2,
        ["2"] = 2,
        ["feb"] = 2,
        ["february"] = 2,
        ["03"] = 3,
        ["3"] = 3,
        ["mar"] = 3,
        ["march"] = 3,
        ["04"] = 4,
        ["4"] = 4,
        ["apr"] = 4,
        ["april"] = 4,
        ["05"] = 5,
        ["5"] = 5,
        ["may"] = 5,
        ["06"] = 6,
        ["6"] = 6,
        ["jun"] = 6,
        ["june"] = 6,
        ["07"] = 7,
        ["7"] = 7,
        ["jul"] = 7,
        ["july"] = 7,
        ["08"] = 8,
        ["8"] = 8,
        ["aug"] = 8,
        ["august"] = 8,
        ["09"] = 9,
        ["9"] = 9,
        ["sep"] = 9,
        ["september"] = 9,
        ["10"] = 10,
        ["oct"] = 10,
        ["october"] = 10,
        ["11"] = 11,
        ["nov"] = 11,
        ["november"] = 11,
        ["12"] = 12,
        ["dec"] = 12,
        ["december"] = 12,
    };

    private static readonly Dictionary<string, int> DowTokens = new(StringComparer.Ordinal)
    {
        ["0"] = 0,
        ["sun"] = 0,
        ["sunday"] = 0,
        ["1"] = 1,
        ["mon"] = 1,
        ["monday"] = 1,
        ["2"] = 2,
        ["tue"] = 2,
        ["tuesday"] = 2,
        ["3"] = 3,
        ["wed"] = 3,
        ["wednesday"] = 3,
        ["4"] = 4,
        ["thu"] = 4,
        ["thursday"] = 4,
        ["5"] = 5,
        ["fri"] = 5,
        ["friday"] = 5,
        ["6"] = 6,
        ["sat"] = 6,
        ["saturday"] = 6,
        ["7"] = 0,
    };

    private static Dictionary<string, int> BuildNumberTokens()
    {
        var tokens = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < 60; i++)
        {
            tokens[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = i;
            if (i < 10)
            {
                tokens["0" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = i;
            }
        }

        for (var year = 1970; year <= 2099; year++)
        {
            tokens[year.ToString(System.Globalization.CultureInfo.InvariantCulture)] = year;
        }

        return tokens;
    }

    private static int Atoi(string value) => NumberTokens.GetValueOrDefault(value);

    private static readonly FieldDescriptor SecondDescriptor = new(
        "second", 0, 59, GenericDefaultList,
        "0?[0-9]|[1-5][0-9]", Atoi);

    private static readonly FieldDescriptor MinuteDescriptor = new(
        "minute", 0, 59, GenericDefaultList,
        "0?[0-9]|[1-5][0-9]", Atoi);

    private static readonly FieldDescriptor HourDescriptor = new(
        "hour", 0, 23, GenericDefaultList[..24],
        "0?[0-9]|1[0-9]|2[0-3]", Atoi);

    private static readonly FieldDescriptor DomDescriptor = new(
        "day-of-month", 1, 31, GenericDefaultList[1..32],
        "0?[1-9]|[12][0-9]|3[01]", Atoi);

    private static readonly FieldDescriptor MonthDescriptor = new(
        "month", 1, 12, GenericDefaultList[1..13],
        "0?[1-9]|1[012]|jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec"
            + "|january|february|march|april|march|april|june|july|august"
            + "|september|october|november|december",
        static value => MonthTokens.GetValueOrDefault(value));

    private static readonly FieldDescriptor DowDescriptor = new(
        "day-of-week", 0, 6, GenericDefaultList[..7],
        "0?[0-7]|sun|mon|tue|wed|thu|fri|sat"
            + "|sunday|monday|tuesday|wednesday|thursday|friday|saturday",
        static value => DowTokens.GetValueOrDefault(value));

    private static readonly FieldDescriptor YearDescriptor = new(
        "year", 1970, 2099, YearDefaultList,
        "19[789][0-9]|20[0-9]{2}", Atoi);

    /// <summary>Port of <c>makeLayoutRegexp</c>: the reference caches compiled layouts.</summary>
    private static readonly Dictionary<string, Regex> LayoutCache = new(StringComparer.Ordinal);

    private static readonly Regex EntryRegex = new("[^,]+", RegexOptions.CultureInvariant);

    private static readonly Regex WildcardRegex = CompileLayout(@"^\*$|^\?$");
    private static readonly Regex LastDomRegex = CompileLayout("^l$");
    private static readonly Regex LastWorkDomRegex = CompileLayout("^lw$");
    private static readonly Regex WildcardIntervalRegex =
        CompileLayout(@"^\*/([0-9]+)$");

    private static Regex CompileLayout(string layout) => new(layout, RegexOptions.CultureInvariant);

    private static Regex ValueRegex(string valuePattern) => Layout("^(%value%)$", valuePattern);

    private static Regex RangeRegex(string valuePattern)
        => Layout("^(%value%)-(%value%)$", valuePattern);

    private static Regex ValueIntervalRegex(string valuePattern)
        => Layout("^(%value%)/([0-9]+)$", valuePattern);

    private static Regex RangeIntervalRegex(string valuePattern)
        => Layout("^(%value%)-(%value%)/([0-9]+)$", valuePattern);

    private static Regex WorkDomRegex(string valuePattern)
        => Layout("^(%value%)w$", valuePattern);

    private static Regex DowOfLastWeekRegex(string valuePattern)
        => Layout("^(%value%)l$", valuePattern);

    private static Regex DowOfSpecificWeekRegex(string valuePattern)
        => Layout("^(%value%)#([1-5])$", valuePattern);

    private static Regex Layout(string layout, string valuePattern)
    {
        var expanded = layout.Replace("%value%", valuePattern, StringComparison.Ordinal);

        lock (LayoutCache)
        {
            if (!LayoutCache.TryGetValue(expanded, out var regex))
            {
                regex = CompileLayout(expanded);
                LayoutCache[expanded] = regex;
            }

            return regex;
        }
    }
}
