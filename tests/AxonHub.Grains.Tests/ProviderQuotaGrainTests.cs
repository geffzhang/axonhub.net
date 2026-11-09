using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the per-channel quota actor (§150): the leader channel's grain runs
/// the group check, keeps only the leader's resulting cache entry, treats an
/// unexpected runner failure like the reference's group goroutine (log, keep
/// the previous entry), and serializes concurrent checks on one channel.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ProviderQuotaGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private IProviderQuotaGrain Grain(long channelId)
        => fixture.Cluster.GrainFactory.GetGrain<IProviderQuotaGrain>(channelId);

    private static ProviderQuotaCheckGroup Group(params long[] channelIds)
    {
        var group = new ProviderQuotaCheckGroup { AccountKey = "account-a" };
        foreach (var channelId in channelIds)
        {
            group.Members.Add(new ProviderQuotaChannelSnapshot
            {
                Id = channelId,
                Name = "channel-" + channelId,
                Type = "claudecode",
            });
        }

        return group;
    }

    private static ProviderQuotaStatusSnapshot Status(
        long channelId,
        string status,
        bool ready) => new()
    {
        ChannelId = channelId,
        ProviderType = "claudecode",
        Status = status,
        Ready = ready,
        Limits =
        [
            new ProviderQuotaLimitSnapshot
            {
                Type = "session",
                Status = status,
                UsageRatio = 0.25,
                Ready = ready,
                Window = "5h",
            },
        ],
    };

    private static ProviderQuotaGroupCheckResult Result(
        params (long ChannelId, ProviderQuotaStatusSnapshot? Status)[] members)
    {
        var result = new ProviderQuotaGroupCheckResult();
        foreach (var (channelId, status) in members)
        {
            result.Members.Add(new ProviderQuotaMemberStatus
            {
                ChannelId = channelId,
                Status = status,
            });
        }

        return result;
    }

    [Fact]
    public async Task TheLeaderGrainRunsTheGroupAndKeepsOnlyItsOwnEntry()
    {
        fixture.QuotaRunner.Reset();
        var leader = Status(11, "available", ready: true);
        var member = Status(12, "warning", ready: true);
        fixture.QuotaRunner.OnRun = _ => Result((11, leader), (12, member));

        var grain = Grain(11);
        var returned = await grain.CheckGroupAsync(Group(11, 12), Now);

        Assert.Equal(2, returned.Members.Count);
        Assert.Equal([11L, 12L], returned.Members.Select(static entry => entry.ChannelId));
        Assert.Equal("warning", returned.Members[1].Status!.Status);
        Assert.Single(fixture.QuotaRunner.Groups);
        Assert.Equal(Now, fixture.QuotaRunner.Instants[0]);

        var stored = await grain.GetStatusAsync();
        Assert.NotNull(stored);
        Assert.Equal(11, stored.ChannelId);
        Assert.Equal("available", stored.Status);
        Assert.True(stored.Ready);
        Assert.Equal("session", Assert.Single(stored.Limits).Type);
    }

    [Fact]
    public async Task AFailedCheckReturnsNoMembersAndKeepsThePreviousEntry()
    {
        fixture.QuotaRunner.Reset();
        var previous = Status(21, "available", ready: true);

        var grain = Grain(21);
        await grain.ApplyStatusAsync(previous);
        fixture.QuotaRunner.Failure = new InvalidOperationException("database is down");

        var returned = await grain.CheckGroupAsync(Group(21), Now);

        Assert.Empty(returned.Members);
        var stored = await grain.GetStatusAsync();
        Assert.NotNull(stored);
        Assert.Equal("available", stored.Status);
    }

    [Fact]
    public async Task ApplyStatusStoresTheMirroredEntryAndClearStatusRemovesIt()
    {
        var grain = Grain(31);

        await grain.ApplyStatusAsync(Status(31, "warning", ready: true));

        var stored = await grain.GetStatusAsync();
        Assert.NotNull(stored);
        Assert.Equal(31, stored.ChannelId);
        Assert.Equal("warning", stored.Status);

        await grain.ClearStatusAsync();
        Assert.Null(await grain.GetStatusAsync());
    }

    [Fact]
    public async Task AGroupWithoutMembersDoesNotReplaceTheStoredEntry()
    {
        fixture.QuotaRunner.Reset();

        var grain = Grain(41);
        await grain.ApplyStatusAsync(Status(41, "available", ready: true));

        await grain.CheckGroupAsync(Group(), Now);

        var stored = await grain.GetStatusAsync();
        Assert.NotNull(stored);
        Assert.Equal("available", stored.Status);
    }

    [Fact]
    public async Task ConcurrentChecksOnOneChannelAreSerializedByTheGrainTurn()
    {
        fixture.QuotaRunner.Reset();
        fixture.QuotaRunner.Gate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.QuotaRunner.OnRun = _ => Result((51, Status(51, "available", ready: true)));

        var grain = Grain(51);
        var first = grain.CheckGroupAsync(Group(51), Now);
        await fixture.QuotaRunner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = grain.CheckGroupAsync(Group(51), Now.AddMinutes(1));
        await Task.Delay(50);

        // The second call is still queued behind the first turn.
        Assert.Single(fixture.QuotaRunner.Groups);

        fixture.QuotaRunner.Gate.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, fixture.QuotaRunner.Groups.Count);
        Assert.Equal(1, fixture.QuotaRunner.MaxConcurrentCalls);
    }
}

/// <summary>
/// Locks the provider quota coordinator (§150, provider_quota.go:401-409,
/// 595-623, 665-717): the reminder arms without running, every fire re-arms the
/// following occurrence before the sweep, stale ticks are ignored, manual runs
/// force the sweep without touching the schedule, and failures never drop the
/// schedule or surface to the caller (invalidation is the one path that
/// propagates, like the reference's channel hook).
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ProviderQuotaCoordinatorGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstOccurrence = new(2024, 1, 1, 10, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SecondOccurrence = new(2024, 1, 1, 10, 10, 0, TimeSpan.Zero);

    private IProviderQuotaCoordinatorGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IProviderQuotaCoordinatorGrain>(key);

    [Fact]
    public async Task RegistrationArmsTheNextOccurrenceWithoutRunningTheSweep()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence);
        fixture.QuotaOperations.Reset();

        var scheduled = await Grain("provider-quota:register").EnsureScheduledAsync(Now);

        Assert.Equal(FirstOccurrence, scheduled);
        Assert.Equal([Now], fixture.QuotaSchedule.Calls);
        Assert.Empty(fixture.QuotaOperations.Forces);
    }

    [Fact]
    public async Task EnsureKeepsThePendingOccurrenceAcrossCalls()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.QuotaOperations.Reset();

        var grain = Grain("provider-quota:ensure-twice");

        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now));
        Assert.Equal(FirstOccurrence, await grain.EnsureScheduledAsync(Now.AddMinutes(1)));
        Assert.Single(fixture.QuotaSchedule.Calls);
    }

    [Fact]
    public async Task TheFireArmsTheFollowingOccurrenceBeforeRunningTheSweep()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.QuotaOperations.Reset();

        var grain = Grain("provider-quota:fire");
        await grain.EnsureScheduledAsync(Now);

        var next = await grain.FireScheduledAsync(FirstOccurrence);

        Assert.Equal(SecondOccurrence, next);
        Assert.Equal([Now, FirstOccurrence], fixture.QuotaSchedule.Calls);
        Assert.Equal([false], fixture.QuotaOperations.Forces);
    }

    [Fact]
    public async Task AStaleTickForAConsumedOccurrenceDoesNotRunTheSweepTwice()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence, SecondOccurrence, SecondOccurrence);
        fixture.QuotaOperations.Reset();

        var grain = Grain("provider-quota:stale-tick");
        await grain.EnsureScheduledAsync(Now);

        Assert.Equal(SecondOccurrence, await grain.FireScheduledAsync(FirstOccurrence));
        Assert.Equal(
            SecondOccurrence,
            await grain.FireScheduledAsync(FirstOccurrence.AddMinutes(1)));
        Assert.Single(fixture.QuotaOperations.Forces);
    }

    [Fact]
    public async Task ManualRunForcesTheSweepWithoutTouchingTheSchedule()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence);
        fixture.QuotaOperations.Reset();

        await Grain("provider-quota:manual").RunManualAsync();

        Assert.Equal([true], fixture.QuotaOperations.Forces);
        Assert.Empty(fixture.QuotaSchedule.Calls);
    }

    [Fact]
    public async Task AFailedManualRunIsSwallowedLikeTheReference()
    {
        fixture.QuotaOperations.Reset();
        fixture.QuotaOperations.RunFailure = new InvalidOperationException("sweep failed");

        await Grain("provider-quota:manual-failure").RunManualAsync();

        Assert.Equal([true], fixture.QuotaOperations.Forces);
    }

    [Fact]
    public async Task AFailedScheduledRunKeepsTheScheduleArmed()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence, SecondOccurrence);
        fixture.QuotaOperations.Reset();
        fixture.QuotaOperations.RunFailure = new InvalidOperationException("sweep failed");

        var grain = Grain("provider-quota:run-failure");
        await grain.EnsureScheduledAsync(Now);

        var next = await grain.FireScheduledAsync(FirstOccurrence);

        Assert.Equal(SecondOccurrence, next);
        Assert.Single(fixture.QuotaOperations.Forces);
    }

    [Fact]
    public async Task RefreshForwardsTheRequestWithoutArmingTheSchedule()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence);
        fixture.QuotaOperations.Reset();

        var request = new ProviderQuotaRefreshRequest
        {
            AccountKey = "account-a",
            Channel = new ProviderQuotaChannelSnapshot
            {
                Id = 61,
                Name = "channel-61",
                Type = "claudecode",
            },
        };

        await Grain("provider-quota:refresh").RefreshChannelAsync(request);

        var forwarded = Assert.Single(fixture.QuotaOperations.Refreshes);
        Assert.Equal("account-a", forwarded.AccountKey);
        Assert.Equal(61, forwarded.Channel.Id);
        Assert.Empty(fixture.QuotaSchedule.Calls);
    }

    [Fact]
    public async Task AFailedRefreshIsSwallowedLikeTheReference()
    {
        fixture.QuotaOperations.Reset();
        fixture.QuotaOperations.RefreshFailure = new InvalidOperationException("refresh failed");

        await Grain("provider-quota:refresh-failure").RefreshChannelAsync(
            new ProviderQuotaRefreshRequest { Channel = new ProviderQuotaChannelSnapshot() });

        Assert.Single(fixture.QuotaOperations.Refreshes);
    }

    [Fact]
    public async Task InvalidateForwardsAndPropagatesTheDeleteError()
    {
        fixture.QuotaSchedule.Reset(FirstOccurrence);
        fixture.QuotaOperations.Reset();
        fixture.QuotaOperations.InvalidateFailure = new InvalidOperationException("delete failed");

        var grain = Grain("provider-quota:invalidate");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.InvalidateAsync(71));

        Assert.Equal("delete failed", error.Message);
        Assert.Equal([71L], fixture.QuotaOperations.Invalidations);
        Assert.Empty(fixture.QuotaSchedule.Calls);
    }
}

/// <summary>Scripted IProviderQuotaScheduleSource: a queue of instants, one per arm.</summary>
public sealed class FakeProviderQuotaScheduleSource : IProviderQuotaScheduleSource
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

/// <summary>
/// Records the coordinator's operations and can fail each one on demand. The
/// concurrent-check test uses <see cref="Gate"/> to hold a run inside the
/// runner while another call queues.
/// </summary>
public sealed class FakeProviderQuotaGroupRunner : IProviderQuotaGroupRunner
{
    private int _concurrent;

    public List<ProviderQuotaCheckGroup> Groups { get; } = [];

    public List<DateTimeOffset> Instants { get; } = [];

    public Func<ProviderQuotaCheckGroup, ProviderQuotaGroupCheckResult>? OnRun { get; set; }

    public Exception? Failure { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public TaskCompletionSource Entered { get; private set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int MaxConcurrentCalls { get; private set; }

    public void Reset()
    {
        Groups.Clear();
        Instants.Clear();
        OnRun = null;
        Failure = null;
        Gate = null;
        Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MaxConcurrentCalls = 0;
    }

    public async Task<ProviderQuotaGroupCheckResult> RunGroupAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Groups.Add(group);
        Instants.Add(now);
        _concurrent++;
        MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, _concurrent);
        Entered.TrySetResult();

        try
        {
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            return OnRun?.Invoke(group) ?? new ProviderQuotaGroupCheckResult();
        }
        finally
        {
            _concurrent--;
        }
    }
}

public sealed class FakeProviderQuotaCoordinatorOperations : IProviderQuotaCoordinatorOperations
{
    public List<bool> Forces { get; } = [];

    public List<ProviderQuotaRefreshRequest> Refreshes { get; } = [];

    public List<long> Invalidations { get; } = [];

    public Exception? RunFailure { get; set; }

    public Exception? RefreshFailure { get; set; }

    public Exception? InvalidateFailure { get; set; }

    public void Reset()
    {
        Forces.Clear();
        Refreshes.Clear();
        Invalidations.Clear();
        RunFailure = null;
        RefreshFailure = null;
        InvalidateFailure = null;
    }

    public Task RunAsync(bool force, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Forces.Add(force);
        return RunFailure is null ? Task.CompletedTask : Task.FromException(RunFailure);
    }

    public Task RefreshChannelAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Refreshes.Add(request);
        return RefreshFailure is null ? Task.CompletedTask : Task.FromException(RefreshFailure);
    }

    public Task InvalidateAsync(long channelId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Invalidations.Add(channelId);
        return InvalidateFailure is null
            ? Task.CompletedTask
            : Task.FromException(InvalidateFailure);
    }
}
