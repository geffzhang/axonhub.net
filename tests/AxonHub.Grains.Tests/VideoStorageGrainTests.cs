using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the reminder-driven video-storage scan (C234, worker.go:54-95): the
/// registration arms the pending occurrence without running the scan, every
/// fire re-arms the following occurrence before running, a stale tick for an
/// already consumed occurrence is ignored, a settings change re-arms from the
/// call instant, a failed scan never drops the schedule, and Orleans' one-minute
/// floor also covers a non-positive stored interval.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class VideoStorageGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private IVideoStorageGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IVideoStorageGrain>(key);

    [Fact]
    public async Task RegistrationArmsTheNextOccurrenceWithoutRunningTheScan()
    {
        fixture.VideoStorageSchedule.Reset(Interval);
        fixture.VideoStorageScan.Reset();

        var scheduled = await Grain("video-storage:register").EnsureScheduledAsync(Now);

        Assert.Equal(Now + Interval, scheduled);
        Assert.Equal(1, fixture.VideoStorageSchedule.Calls);
        Assert.Equal(0, fixture.VideoStorageScan.Calls);
    }

    [Fact]
    public async Task EnsureKeepsThePendingOccurrenceAcrossCalls()
    {
        fixture.VideoStorageSchedule.Reset(Interval);
        fixture.VideoStorageScan.Reset();

        var grain = Grain("video-storage:ensure-twice");

        Assert.Equal(Now + Interval, await grain.EnsureScheduledAsync(Now));
        Assert.Equal(Now + Interval, await grain.EnsureScheduledAsync(Now.AddMinutes(5)));
        Assert.Equal(1, fixture.VideoStorageSchedule.Calls);
    }

    [Fact]
    public async Task TheFireArmsTheFollowingOccurrenceBeforeRunningTheScan()
    {
        fixture.VideoStorageSchedule.Reset(Interval);
        fixture.VideoStorageScan.Reset();

        var grain = Grain("video-storage:fire");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now + Interval;
        var next = await grain.FireScheduledAsync(firstOccurrence);

        Assert.Equal(firstOccurrence + Interval, next);
        Assert.Equal(2, fixture.VideoStorageSchedule.Calls);
        Assert.Equal(1, fixture.VideoStorageScan.Calls);
    }

    [Fact]
    public async Task AStaleTickForAConsumedOccurrenceDoesNotRunTheScanTwice()
    {
        fixture.VideoStorageSchedule.Reset(Interval);
        fixture.VideoStorageScan.Reset();

        var grain = Grain("video-storage:stale-tick");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now + Interval;
        Assert.Equal(
            firstOccurrence + Interval,
            await grain.FireScheduledAsync(firstOccurrence));

        // The stale delivery still re-arms from its own instant (the reminder
        // is overwritten either way) but must not run a second scan.
        var staleAt = firstOccurrence.AddMinutes(1);
        Assert.Equal(staleAt + Interval, await grain.FireScheduledAsync(staleAt));
        Assert.Equal(1, fixture.VideoStorageScan.Calls);
    }

    [Fact]
    public async Task RescheduleDropsThePendingOccurrenceAndRearmsFromTheCall()
    {
        fixture.VideoStorageSchedule.Reset(Interval, TimeSpan.FromMinutes(20));
        fixture.VideoStorageScan.Reset();

        var grain = Grain("video-storage:reschedule");
        Assert.Equal(Now + Interval, await grain.EnsureScheduledAsync(Now));

        var rescheduledAt = Now.AddMinutes(1);
        Assert.Equal(
            rescheduledAt + TimeSpan.FromMinutes(20),
            await grain.RescheduleAsync(rescheduledAt));
        Assert.Equal(2, fixture.VideoStorageSchedule.Calls);

        // The occurrence armed by the old interval is no longer pending.
        await grain.FireScheduledAsync(Now + Interval);
        Assert.Equal(0, fixture.VideoStorageScan.Calls);
    }

    [Fact]
    public async Task AFailedScanKeepsTheScheduleArmed()
    {
        fixture.VideoStorageSchedule.Reset(Interval);
        fixture.VideoStorageScan.Reset();
        fixture.VideoStorageScan.Failure = new InvalidOperationException("scan failed");

        var grain = Grain("video-storage:scan-failure");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now + Interval;
        var next = await grain.FireScheduledAsync(firstOccurrence);

        Assert.Equal(firstOccurrence + Interval, next);
        Assert.Equal(1, fixture.VideoStorageScan.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public async Task AnIntervalBelowOneMinuteIsFlooredToOneMinute(int seconds)
    {
        fixture.VideoStorageSchedule.Reset(TimeSpan.FromSeconds(seconds));
        fixture.VideoStorageScan.Reset();

        var scheduled = await Grain("video-storage:floor-" + seconds)
            .EnsureScheduledAsync(Now);

        Assert.Equal(Now.AddMinutes(1), scheduled);
    }
}

/// <summary>
/// Scripted IVideoStorageScheduleSource: a queue of intervals, one per read.
/// The reference reads the stored setting at registration and reschedule time.
/// </summary>
public sealed class FakeVideoStorageScheduleSource : IVideoStorageScheduleSource
{
    private readonly Queue<TimeSpan> _intervals = new();

    public int Calls { get; private set; }

    private TimeSpan Fallback { get; set; } = TimeSpan.FromMinutes(1);

    public void Reset(params TimeSpan[] intervals)
    {
        Calls = 0;
        _intervals.Clear();
        foreach (var interval in intervals)
        {
            _intervals.Enqueue(interval);
        }

        Fallback = intervals.Length == 0 ? TimeSpan.FromMinutes(1) : intervals[^1];
    }

    public Task<TimeSpan> GetIntervalAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.FromResult(_intervals.Count > 0 ? _intervals.Dequeue() : Fallback);
    }
}

public sealed class FakeVideoStorageScanRunner : IVideoStorageScanRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
