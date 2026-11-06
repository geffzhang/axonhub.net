using AxonHub.Api.Management;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// The GC task is registered as <c>{CronExpr: gc.cron, Timezone: "UTC"}</c>
/// (gc.go:77-84); the default <c>0 2 * * *</c> fires at 02:00 UTC. These tests
/// pin the standard five-field grammar and the "strictly after" next-occurrence
/// rule the reference's executor applies.
/// </summary>
public sealed class GcCronScheduleTests
{
    [Fact]
    public void DefaultExpressionFiresAtTwoUtc()
    {
        var schedule = new GcCronSchedule(GcOptions.DefaultCron);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 1, 59, 0, TimeSpan.Zero)));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 2, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void NextOccurrenceConvertsTheInputToUtc()
    {
        var schedule = new GcCronSchedule("0 2 * * *");

        // 10:00 +08:00 is 02:00Z: the next fire is the following day.
        var next = schedule.NextOccurrence(
            new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(8)));

        Assert.Equal(new DateTimeOffset(2026, 11, 7, 2, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void StepFieldsFireOnTheSteppedMinutes()
    {
        var schedule = new GcCronSchedule("*/15 * * * *");

        Assert.Equal(
            new DateTimeOffset(2026, 11, 6, 10, 15, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 10, 7, 0, TimeSpan.Zero)));
        Assert.Equal(
            new DateTimeOffset(2026, 11, 6, 10, 30, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 10, 15, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void RangedStartWithStepCountsFromTheRangeStart()
    {
        var schedule = new GcCronSchedule("5/20 * * * *");

        Assert.Equal(
            new DateTimeOffset(2026, 11, 6, 10, 25, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 10, 6, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void WeekdayNamesAndRangesSelectTheWeekday()
    {
        var schedule = new GcCronSchedule("0 9 * * mon-fri");

        // Friday 10:00 -> the weekend is skipped, next is Monday 09:00.
        Assert.Equal(
            new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void SundayCanBeWrittenAsSeven()
    {
        var schedule = new GcCronSchedule("0 0 * * 7");

        Assert.Equal(
            new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void MonthNamesSelectTheMonth()
    {
        var schedule = new GcCronSchedule("0 0 1 jan *");

        Assert.Equal(
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void RestrictedDayOfMonthAndWeekdayUseVixieOrSemantics()
    {
        // The 1st OR a Monday: Sunday 2026-11-01 12:00 -> Monday 2026-11-02.
        var schedule = new GcCronSchedule("0 0 1 * 1");

        Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void UnrestrictedDayOfMonthLetsTheWeekdayDecide()
    {
        var schedule = new GcCronSchedule("0 0 * * 0");

        Assert.Equal(
            new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero),
            schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("0 0 2 * * *", "expected 5 fields, found 6")]
    [InlineData("0 0 * *", "expected 5 fields, found 4")]
    [InlineData("60 * * * *", "invalid minute field \"60\": end of range (60) above maximum (59)")]
    [InlineData("*/0 * * * *", "invalid minute field \"*/0\": invalid step \"0\"")]
    [InlineData("0 24 * * *", "invalid hour field \"24\": end of range (24) above maximum (23)")]
    [InlineData("0 0 0 * *", "invalid day-of-month field \"0\": beginning of range (0) below minimum (1)")]
    [InlineData("0 0 * 13 *", "invalid month field \"13\": end of range (13) above maximum (12)")]
    [InlineData("0 0 * * 8", "invalid day-of-week field \"8\": end of range (8) above maximum (7)")]
    [InlineData("5-1 * * * *", "invalid minute field \"5-1\": beginning of range (5) beyond end of range (1)")]
    [InlineData("nope * * * *", "invalid minute field \"nope\": \"nope\" is not a value")]
    public void InvalidExpressionsAreRejectedWithTheParserMessage(string expression, string message)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new GcCronSchedule(expression));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void NeverMatchingExpressionsThrowInsteadOfSpinning()
    {
        var schedule = new GcCronSchedule("0 0 30 feb *");

        var error = Assert.Throws<InvalidOperationException>(
            () => schedule.NextOccurrence(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)));

        Assert.Equal("cron expression \"0 0 30 feb *\" never matches", error.Message);
    }

    [Fact]
    public void OptionsDefaultsMatchTheReferenceConfig()
    {
        var options = GcOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal("0 2 * * *", options.Cron);
        Assert.True(options.VacuumEnabled);
        Assert.False(options.VacuumFull);
    }

    [Fact]
    public void OptionsReadTheConfiguredValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gc:Cron"] = "30 4 * * *",
                ["Gc:VacuumEnabled"] = "false",
                ["Gc:VacuumFull"] = "true",
            })
            .Build();

        var options = GcOptions.FromConfiguration(configuration);

        Assert.Equal("30 4 * * *", options.Cron);
        Assert.False(options.VacuumEnabled);
        Assert.True(options.VacuumFull);
    }

    [Fact]
    public void OptionsPreferTheSectionOverTheEnvironmentStyleKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gc:Cron"] = "30 4 * * *",
                ["AXONHUB_GC_CRON"] = "0 0 * * *",
            })
            .Build();

        Assert.Equal("30 4 * * *", GcOptions.FromConfiguration(configuration).Cron);
    }

    [Fact]
    public void OptionsFallBackToTheEnvironmentStyleKey()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AXONHUB_GC_CRON"] = "15 1 * * *",
                ["AXONHUB_GC_VACUUM_ENABLED"] = "false",
                ["AXONHUB_GC_VACUUM_FULL"] = "true",
            })
            .Build();

        var options = GcOptions.FromConfiguration(configuration);

        Assert.Equal("15 1 * * *", options.Cron);
        Assert.False(options.VacuumEnabled);
        Assert.True(options.VacuumFull);
    }
}
