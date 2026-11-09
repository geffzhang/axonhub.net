using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the reminder-driven backup cron (§149, autobackup.go:22-32): the
/// registration arms the pending 02:00 without running, every fire re-arms the
/// following occurrence before triggering the backup, a stale tick for an
/// already consumed occurrence is ignored, and Reschedule drops the pending
/// occurrence.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class AutoBackupGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstOccurrence = new(2024, 1, 2, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondOccurrence = new(2024, 1, 3, 2, 0, 0, TimeSpan.Zero);

    private IAutoBackupGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IAutoBackupGrain>(key);

    [Fact]
    public async Task RegistrationArmsTheNextOccurrenceWithoutRunningTheBackup()
    {
        fixture.AutoBackupSchedule.Reset(FirstOccurrence);
        fixture.AutoBackupRunner.Reset();

        var grain = Grain("auto-backup:register");
        var scheduled = await grain.EnsureScheduledAsync(Now);

        Assert.Equal(FirstOccurrence, scheduled);
        Assert.Equal([Now], fixture.AutoBackupSchedule.Calls);
        Assert.Equal(0, fixture.AutoBackupRunner.Calls);
    }

    [Fact]
    public async Task EnsureKeepsThePendingOccurrenceAcrossCalls()
    {
        fixture.AutoBackupSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.AutoBackupRunner.Reset();

        var grain = Grain("auto-backup:ensure-twice");

        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now));
        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now.AddHours(1)));
        Assert.Single(fixture.AutoBackupSchedule.Calls);
    }

    [Fact]
    public async Task TheFireArmsTheFollowingOccurrenceBeforeRunningTheBackup()
    {
        fixture.AutoBackupSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.AutoBackupRunner.Reset();

        var grain = Grain("auto-backup:fire");
        await grain.EnsureScheduledAsync(Now);

        var next = await grain.FireScheduledAsync(FirstOccurrence);

        Assert.Equal(SecondOccurrence, next);
        Assert.Equal([Now, FirstOccurrence], fixture.AutoBackupSchedule.Calls);
        Assert.Equal(1, fixture.AutoBackupRunner.Calls);
    }

    [Fact]
    public async Task AStaleTickForAConsumedOccurrenceDoesNotTriggerASecondBackup()
    {
        fixture.AutoBackupSchedule.Reset(FirstOccurrence, SecondOccurrence, SecondOccurrence);
        fixture.AutoBackupRunner.Reset();

        var grain = Grain("auto-backup:stale-tick");
        await grain.EnsureScheduledAsync(Now);

        Assert.Equal(SecondOccurrence, await grain.FireScheduledAsync(FirstOccurrence));
        Assert.Equal(
            SecondOccurrence,
            await grain.FireScheduledAsync(FirstOccurrence.AddMinutes(1)));
        Assert.Equal(1, fixture.AutoBackupRunner.Calls);
    }

    [Fact]
    public async Task RescheduleDropsThePendingOccurrenceAndArmsFromTheCallingInstant()
    {
        var rescheduled = Now.AddHours(3);
        var rearmed = rescheduled.AddHours(5);
        fixture.AutoBackupSchedule.Reset(FirstOccurrence, rearmed);
        fixture.AutoBackupRunner.Reset();

        var grain = Grain("auto-backup:reschedule");
        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now));

        // The source is asked from the calling instant, not from the dropped
        // pending occurrence.
        Assert.Equal(rearmed, await grain.RescheduleAsync(rescheduled));
        Assert.Equal(rescheduled, fixture.AutoBackupSchedule.Calls[1]);

        // The re-armed occurrence is now the pending one.
        Assert.Equal(rearmed, await grain.EnsureScheduledAsync(Now.AddHours(2)));
        Assert.Equal(2, fixture.AutoBackupSchedule.Calls.Count);
    }

    [Fact]
    public async Task AFailedBackupKeepsTheScheduleArmed()
    {
        fixture.AutoBackupSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.AutoBackupRunner.Reset();
        fixture.AutoBackupRunner.Failure = new InvalidOperationException("storage unavailable");

        var grain = Grain("auto-backup:runner-failure");
        await grain.EnsureScheduledAsync(Now);

        var next = await grain.FireScheduledAsync(FirstOccurrence);

        Assert.Equal(SecondOccurrence, next);
        Assert.Equal(1, fixture.AutoBackupRunner.Calls);
    }
}

/// <summary>Scripted AutoBackupScheduleSource: a queue of instants, one per arm.</summary>
public sealed class FakeAutoBackupScheduleSource : IAutoBackupScheduleSource
{
    private readonly Queue<DateTimeOffset> _results = new();

    public List<DateTimeOffset> Calls { get; } = [];

    private DateTimeOffset Fallback { get; set; }

    public void Reset(params DateTimeOffset[] results)
    {
        Calls.Clear();
        _results.Clear();
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }

        Fallback = results.Length == 0 ? default : results[^1];
    }

    public Task<DateTimeOffset> GetNextOccurrenceAsync(
        DateTimeOffset after,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls.Add(after);
        return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : Fallback);
    }
}

public sealed class FakeAutoBackupRunner : IAutoBackupRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
