using System.Globalization;

namespace AxonHub.Api.Management;

/// <summary>
/// Parsed five-field cron expression for the GC task
/// (<c>scheduler.TaskSpec{Name:"gc", CronExpr:gc.cron, Timezone:"UTC"}</c>).
/// The reference hands the expression to zhenzou/executors'
/// <c>CRONRule</c>; the port implements the standard/Vixie field grammar so
/// the default <c>0 2 * * *</c> fires at 02:00 UTC every day.
/// </summary>
/// <remarks>
/// Supported grammar: <c>*</c>, <c>n</c>, <c>n-m</c>, <c>*/s</c>, <c>n-m/s</c>,
/// <c>n/s</c> and comma lists of those; three-letter month and weekday names
/// are accepted case-insensitively. The weekday field accepts 0-7 (0 and 7
/// are Sunday). When both day-of-month and day-of-week are restricted, either
/// may match (Vixie semantics). Six-field (seconds) expressions are rejected;
/// the reference only ever registers the five-field default. Because the task
/// timezone is fixed to UTC there are no DST gaps to special-case.
/// </remarks>
public sealed class GcCronSchedule
{
    private static readonly string[] MonthNames =
    [
        "jan", "feb", "mar", "apr", "may", "jun",
        "jul", "aug", "sep", "oct", "nov", "dec",
    ];

    private static readonly string[] WeekdayNames =
    [
        "sun", "mon", "tue", "wed", "thu", "fri", "sat",
    ];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    /// <exception cref="InvalidOperationException">
    /// The expression is not a valid five-field cron expression.
    /// </exception>
    public GcCronSchedule(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        Expression = expression;

        var fields = expression.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            throw new InvalidOperationException(
                "expected 5 fields, found " + fields.Length.ToString(CultureInfo.InvariantCulture));
        }

        _minutes = ParseField(fields[0], 0, 59, names: null, "minute");
        _hours = ParseField(fields[1], 0, 23, names: null, "hour");
        _daysOfMonth = ParseField(fields[2], 1, 31, names: null, "day-of-month");
        _months = ParseField(fields[3], 1, 12, MonthNames, "month");
        _daysOfWeek = ParseField(fields[4], 0, 7, WeekdayNames, "day-of-week");

        // Normalise Sunday-as-7 onto 0 so the matcher only checks 0-6.
        if (_daysOfWeek[7])
        {
            _daysOfWeek[0] = true;
        }

        _dayOfMonthRestricted = fields[2] != "*";
        _dayOfWeekRestricted = fields[4] != "*";

        if (!_months.Any(static m => m))
        {
            throw new InvalidOperationException("month field matches no month");
        }

        if (!_daysOfMonth.Any(static d => d) && !_daysOfWeek.Any(static d => d))
        {
            throw new InvalidOperationException("day fields match no day");
        }
    }

    public string Expression { get; }

    /// <summary>
    /// The first instant strictly after <paramref name="after"/> whose UTC wall
    /// clock matches the expression.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The expression can never match (for example February 30).
    /// </exception>
    public DateTimeOffset NextOccurrence(DateTimeOffset after)
    {
        var utcAfter = after.ToUniversalTime();
        var start = utcAfter.UtcDateTime;

        // A four-year window covers the leap-day cases; a valid cron expression
        // always fires inside it except impossible combos (Feb 30), which the
        // reference's scheduler also never fires (the executor would error or
        // spin). Cap at eight years to cover a Feb 29 that lands just after a
        // non-leap century boundary.
        var limit = start.AddYears(8);
        var date = start.Date;

        while (date <= limit)
        {
            if (DateMatches(date))
            {
                foreach (var hour in MatchingIndices(_hours, 24))
                {
                    foreach (var minute in MatchingIndices(_minutes, 60))
                    {
                        var candidate = new DateTime(
                            date.Year, date.Month, date.Day,
                            hour, minute, 0, DateTimeKind.Utc);
                        if (candidate > start)
                        {
                            return new DateTimeOffset(candidate, TimeSpan.Zero);
                        }
                    }
                }
            }

            date = date.AddDays(1);
        }

        throw new InvalidOperationException(
            "cron expression \"" + Expression + "\" never matches");
    }

    private bool DateMatches(DateTime date)
    {
        if (!_months[date.Month])
        {
            return false;
        }

        var dom = _daysOfMonth[date.Day];
        var dow = _daysOfWeek[(int)date.DayOfWeek];

        // Vixie/robfig rule: if either day field is unrestricted the other one
        // decides; when both are restricted either match fires the job.
        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
        {
            return dom || dow;
        }

        if (_dayOfMonthRestricted)
        {
            return dom;
        }

        if (_dayOfWeekRestricted)
        {
            return dow;
        }

        return true;
    }

    private static IEnumerable<int> MatchingIndices(bool[] set, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (set[i])
            {
                yield return i;
            }
        }
    }

    private static bool[] ParseField(
        string field,
        int min,
        int max,
        string[]? names,
        string fieldName)
    {
        var set = new bool[max + 1];

        foreach (var part in field.Split(','))
        {
            if (part.Length == 0)
            {
                throw Invalid(fieldName, field, "empty list element");
            }

            var slash = part.IndexOf('/');
            string rangePart;
            var step = 1;
            if (slash >= 0)
            {
                rangePart = part[..slash];
                var stepText = part[(slash + 1)..];
                if (!TryParseInt(stepText, out step) || step <= 0)
                {
                    throw Invalid(fieldName, field, "invalid step \"" + stepText + "\"");
                }
            }
            else
            {
                rangePart = part;
            }

            int low;
            int high;
            if (rangePart == "*")
            {
                low = min;
                high = max;
            }
            else
            {
                var dash = rangePart.IndexOf('-');
                if (dash >= 0)
                {
                    if (!TryParseValue(rangePart[..dash], min, names, out low)
                        || !TryParseValue(rangePart[(dash + 1)..], min, names, out high))
                    {
                        throw Invalid(fieldName, field, "\"" + rangePart + "\" is not a range");
                    }
                }
                else
                {
                    if (!TryParseValue(rangePart, min, names, out low))
                    {
                        throw Invalid(fieldName, field, "\"" + rangePart + "\" is not a value");
                    }

                    // "n/step" means "n-max/step"; a bare "n" is just n.
                    high = slash >= 0 ? max : low;
                }
            }

            if (low > high)
            {
                throw Invalid(
                    fieldName,
                    field,
                    "beginning of range (" + low.ToString(CultureInfo.InvariantCulture)
                    + ") beyond end of range (" + high.ToString(CultureInfo.InvariantCulture) + ")");
            }

            if (low < min)
            {
                throw Invalid(
                    fieldName,
                    field,
                    "beginning of range (" + low.ToString(CultureInfo.InvariantCulture)
                    + ") below minimum (" + min.ToString(CultureInfo.InvariantCulture) + ")");
            }

            if (high > max)
            {
                throw Invalid(
                    fieldName,
                    field,
                    "end of range (" + high.ToString(CultureInfo.InvariantCulture)
                    + ") above maximum (" + max.ToString(CultureInfo.InvariantCulture) + ")");
            }

            for (var value = low; value <= high; value += step)
            {
                set[value] = true;
            }
        }

        return set;
    }

    private static bool TryParseValue(
        string text,
        int min,
        string[]? names,
        out int value)
    {
        if (TryParseInt(text, out value))
        {
            return true;
        }

        if (names is null)
        {
            return false;
        }

        var index = Array.FindIndex(
            names,
            name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        // Month names index from 1 (jan == 1); weekday names from 0 (sun == 0).
        value = min == 1 ? index + 1 : index;
        return true;
    }

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static InvalidOperationException Invalid(
        string fieldName,
        string field,
        string reason)
        => new("invalid " + fieldName + " field \"" + field + "\": " + reason);
}
