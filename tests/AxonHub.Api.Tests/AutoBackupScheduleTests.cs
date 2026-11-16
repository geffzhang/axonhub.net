using AxonHub.Api.Management;

namespace AxonHub.Api.Tests;

/// <summary>
/// The backup cron entry is <c>0 2 * * *</c> in the system time zone
/// (autobackup.go:22-32); robfig/cron computes the next 02:00 strictly after
/// the current instant and never backfills a missed occurrence.
/// </summary>
public sealed class AutoBackupScheduleTests
{
    [Fact]
    public void NextOccurrenceIsTodayWhenTheHourHasNotPassed()
    {
        var utc = TimeZoneInfo.Utc;
        var after = new DateTimeOffset(2026, 11, 6, 1, 59, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero),
            AutoBackupSchedule.NextOccurrence(after, utc));
    }

    [Fact]
    public void NextOccurrenceIsTomorrowAtTheExactHour()
    {
        var utc = TimeZoneInfo.Utc;
        var after = new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero),
            AutoBackupSchedule.NextOccurrence(after, utc));
    }

    [Fact]
    public void NextOccurrenceUsesTheZoneWallClock()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        // 2026-11-06 18:30Z is 2026-11-07 02:30 in Shanghai (+08:00).
        var after = new DateTimeOffset(2026, 11, 6, 18, 30, 0, TimeSpan.Zero);

        var next = AutoBackupSchedule.NextOccurrence(after, zone);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 8, 2, 0, 0, TimeSpan.FromHours(8)),
            next);
    }

    [Fact]
    public void SpringForwardMovesTheMissingHourPastTheGap()
    {
        // US DST starts 2026-03-08; 02:00 does not exist in New York.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var after = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5));

        var next = AutoBackupSchedule.NextOccurrence(after, zone);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 3, 0, 0, TimeSpan.FromHours(-4)),
            next);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), next.ToUniversalTime());
    }

    [Fact]
    public void AmbiguousFallBackTimeTakesTheFirstInstant()
    {
        // Europe/Berlin ends DST on 2026-10-25: 02:00 happens at +02:00 and
        // again at +01:00. Go's time.Date / cron pick the first.
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var after = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(2));

        var next = AutoBackupSchedule.NextOccurrence(after, zone);

        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 2, 0, 0, TimeSpan.FromHours(2)),
            next);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero), next.ToUniversalTime());
    }
}
