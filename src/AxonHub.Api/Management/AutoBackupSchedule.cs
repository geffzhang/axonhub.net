namespace AxonHub.Api.Management;

/// <summary>
/// Port of the backup cron entry's timing. The reference registers
/// <c>{Name:"backup", CronExpr:"0 2 * * *", Timezone: system TimeLocation}</c>
/// (autobackup.go:22-32, scheduler.go) and robfig/cron fires the next 02:00 in
/// that zone; a missed occurrence while the process was down is never
/// backfilled.
/// </summary>
public static class AutoBackupSchedule
{
    /// <summary>The hour in the cron expression <c>0 2 * * *</c>.</summary>
    public const int Hour = 2;

    /// <summary>
    /// The first 02:00 in <paramref name="zone"/> strictly after
    /// <paramref name="after"/>.
    /// </summary>
    /// <remarks>
    /// On a spring-forward day 02:00 does not exist; Go's <c>time.Date</c>
    /// normalizes the nonexistent wall time forward across the gap, so the
    /// occurrence becomes the first valid instant after it (03:00 for the
    /// usual one-hour shift). On a fall-back day 02:00 may occur twice; the
    /// cron library takes the pre-transition offset, which is the first of the
    /// two instants.
    /// </remarks>
    public static DateTimeOffset NextOccurrence(DateTimeOffset after, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var localNow = TimeZoneInfo.ConvertTime(after, zone);
        var date = localNow.Date;

        while (true)
        {
            var candidate = AtLocalHour(date, zone);
            if (candidate > after)
            {
                return candidate;
            }

            date = date.AddDays(1);
        }
    }

    /// <summary>The 02:00 occurrence for one local calendar date.</summary>
    public static DateTimeOffset AtLocalHour(DateTime date, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = DateTime.SpecifyKind(date.Date.AddHours(Hour), DateTimeKind.Unspecified);

        // A DST gap makes the wall time invalid; time.Date moves past it.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        // For an ambiguous wall time pick the pre-transition (larger) offset,
        // i.e. the first of the two instants.
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset);
    }
}
