namespace AxonHub.Api.Management;

/// <summary>
/// One half-open UTC period <c>[Start, End)</c>
/// (internal/pkg/xtime/time.go:27-32).
/// </summary>
public readonly record struct CalendarPeriod(DateTime Start, DateTime End);

/// <summary>
/// Port of <c>xtime.GetCalendarPeriods</c> (internal/pkg/xtime/time.go:49-100):
/// calendar-aligned dashboard periods for the configured timezone. Weeks start
/// on Monday (Sunday is treated as day 7); months start on the first.
/// </summary>
public readonly record struct DashboardCalendarPeriods(
    CalendarPeriod Today,
    CalendarPeriod ThisWeek,
    CalendarPeriod LastWeek,
    CalendarPeriod ThisMonth);

public static class CalendarPeriods
{
    /// <summary>
    /// Port of <c>GetCalendarPeriods</c>. Every boundary is a local wall-clock
    /// midnight converted to UTC, exactly like the reference's
    /// <c>time.Date(..., loc).UTC()</c>.
    /// </summary>
    public static DashboardCalendarPeriods Compute(DateTime utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(ToUtcKind(utcNow), zone);
        var todayLocal = nowLocal.Date;

        var todayStart = LocalToUtc(todayLocal, zone);
        var todayEnd = LocalToUtc(todayLocal.AddDays(1), zone);

        var weekday = (int)nowLocal.DayOfWeek;
        if (weekday == 0)
        {
            weekday = 7; // Go treats Sunday as 7 for the Monday-based week.
        }

        var thisWeekStartLocal = todayLocal.AddDays(-(weekday - 1));
        var thisWeekStart = LocalToUtc(thisWeekStartLocal, zone);
        var thisWeekEnd = LocalToUtc(thisWeekStartLocal.AddDays(7), zone);
        var lastWeekStart = LocalToUtc(thisWeekStartLocal.AddDays(-7), zone);

        var thisMonthStartLocal = new DateTime(nowLocal.Year, nowLocal.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var thisMonthStart = LocalToUtc(thisMonthStartLocal, zone);
        var thisMonthEnd = LocalToUtc(thisMonthStartLocal.AddMonths(1), zone);

        return new DashboardCalendarPeriods(
            new CalendarPeriod(todayStart, todayEnd),
            new CalendarPeriod(thisWeekStart, thisWeekEnd),
            new CalendarPeriod(lastWeekStart, thisWeekStart),
            new CalendarPeriod(thisMonthStart, thisMonthEnd));
    }

    /// <summary>
    /// Local midnight of the day containing <paramref name="utcNow"/>, as an
    /// unspecified-kind wall-clock value (the reference's local
    /// <c>time.Date(nowLocal.Year(), ..., loc)</c>).
    /// </summary>
    public static DateTime StartOfLocalDay(DateTime utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(ToUtcKind(utcNow), zone);
        return DateTime.SpecifyKind(nowLocal.Date, DateTimeKind.Unspecified);
    }

    /// <summary>
    /// Converts a local wall-clock value to UTC. Go's <c>time.Date</c> keeps
    /// the pre-transition offset for a wall time skipped by a DST change and
    /// leaves the ambiguous fall-back choice unspecified; .NET throws for both,
    /// so the port resolves them explicitly instead (C195).
    /// </summary>
    public static DateTime LocalToUtc(DateTime local, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            // Pre-transition offset, the offset Go's time.Date applies.
            var offset = zone.GetUtcOffset(local.AddDays(-1));
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        if (zone.IsAmbiguousTime(local))
        {
            // Go documents the choice as unspecified; use the larger
            // (pre-transition) offset so the boundary stays stable.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>Formats a local day the way Go's <c>Format("2006-01-02")</c> does.</summary>
    public static string FormatDate(DateTime localDate)
        => localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime ToUtcKind(DateTime value)
        => value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
