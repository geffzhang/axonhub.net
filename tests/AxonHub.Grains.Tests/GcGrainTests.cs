using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the reminder-driven GC cron (§149, gc.go:77-84): the registration
/// arms the pending occurrence without running, every fire re-arms the
/// following occurrence before running the cleanup, and a stale tick for an
/// already consumed occurrence is ignored.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class GcGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstOccurrence = new(2024, 1, 2, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondOccurrence = new(2024, 1, 3, 2, 0, 0, TimeSpan.Zero);

    private IGcGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IGcGrain>(key);

    [Fact]
    public async Task RegistrationArmsTheNextOccurrenceWithoutRunningTheCleanup()
    {
        fixture.GcSchedule.Reset(FirstOccurrence);
        fixture.GcCleanup.Reset();

        var grain = Grain("gc:register");
        var scheduled = await grain.EnsureScheduledAsync(Now);

        Assert.Equal(FirstOccurrence, scheduled);
        Assert.Equal([Now], fixture.GcSchedule.Calls);
        Assert.Equal(0, fixture.GcCleanup.Calls);
    }

    [Fact]
    public async Task EnsureKeepsThePendingOccurrenceAcrossCalls()
    {
        fixture.GcSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.GcCleanup.Reset();

        var grain = Grain("gc:ensure-twice");

        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now));
        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now.AddHours(1)));
        Assert.Single(fixture.GcSchedule.Calls);
    }

    [Fact]
    public async Task TheFireArmsTheFollowingOccurrenceBeforeRunningTheCleanup()
    {
        fixture.GcSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.GcCleanup.Reset();

        var grain = Grain("gc:fire");
        await grain.EnsureScheduledAsync(Now);

        var next = await grain.FireScheduledAsync(FirstOccurrence);

        Assert.Equal(SecondOccurrence, next);
        Assert.Equal([Now, FirstOccurrence], fixture.GcSchedule.Calls);
        Assert.Equal(1, fixture.GcCleanup.Calls);
    }

    [Fact]
    public async Task AStaleTickForAConsumedOccurrenceDoesNotRunTheCleanupTwice()
    {
        fixture.GcSchedule.Reset(FirstOccurrence, SecondOccurrence, SecondOccurrence);
        fixture.GcCleanup.Reset();

        var grain = Grain("gc:stale-tick");
        await grain.EnsureScheduledAsync(Now);

        Assert.Equal(SecondOccurrence, await grain.FireScheduledAsync(FirstOccurrence));
        Assert.Equal(
            SecondOccurrence,
            await grain.FireScheduledAsync(FirstOccurrence.AddMinutes(1)));
        Assert.Equal(1, fixture.GcCleanup.Calls);
    }
}

/// <summary>Scripted IGcScheduleSource: a queue of instants, one per arm.</summary>
public sealed class FakeGcScheduleSource : IGcScheduleSource
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

    public DateTimeOffset NextOccurrence(DateTimeOffset after)
    {
        Calls.Add(after);
        return _results.Count > 0 ? _results.Dequeue() : Fallback;
    }
}

public sealed class FakeGcCleanupRunner : IGcCleanupRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
