using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the API-side halves of the provider quota actors (§150,
/// provider_quota.go:401-409, 595-623, 665-717, 719-790): the group executor
/// dispatches a due group to the leader channel's grain and mirrors every
/// member's cache entry, the coordinator client forwards the three serialized
/// entries, and the operations adapter clears the channel grain even when the
/// hard delete fails.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ProviderQuotaGrainBridgeTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private GrainQuotaGroupExecutor Executor() => new(
        fixture.Cluster.GrainFactory,
        NullLogger<GrainQuotaGroupExecutor>.Instance);

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

    private static ProviderQuotaStatusSnapshot Status(long channelId, string status) => new()
    {
        ChannelId = channelId,
        ProviderType = "claudecode",
        Status = status,
        Ready = true,
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

    private IProviderQuotaGrain ChannelGrain(long channelId)
        => fixture.Cluster.GrainFactory.GetGrain<IProviderQuotaGrain>(channelId);

    [Fact]
    public async Task ExecuteDispatchesTheGroupToTheLeaderAndMirrorsEveryMember()
    {
        fixture.QuotaRunner.Reset();
        fixture.QuotaRunner.OnRun = _ => Result(
            (81, Status(81, "available")),
            (82, Status(82, "warning")));

        await Executor().ExecuteAsync(Group(81, 82), Now);

        var dispatched = Assert.Single(fixture.QuotaRunner.Groups);
        Assert.Equal([81L, 82L], dispatched.Members.Select(static member => member.Id));

        var leader = await ChannelGrain(81).GetStatusAsync();
        Assert.NotNull(leader);
        Assert.Equal("available", leader.Status);

        var member = await ChannelGrain(82).GetStatusAsync();
        Assert.NotNull(member);
        Assert.Equal("warning", member.Status);
    }

    [Fact]
    public async Task ANullMemberStatusClearsTheMirroredEntry()
    {
        fixture.QuotaRunner.Reset();
        fixture.QuotaRunner.OnRun = _ => Result(
            (84, Status(84, "available")),
            (83, null));

        await ChannelGrain(83).ApplyStatusAsync(Status(83, "available"));

        await Executor().ExecuteAsync(Group(84, 83), Now);

        Assert.Null(await ChannelGrain(83).GetStatusAsync());
    }

    [Fact]
    public async Task AFailedLeaderCheckLeavesEveryEntryUntouched()
    {
        fixture.QuotaRunner.Reset();
        fixture.QuotaRunner.Failure = new InvalidOperationException("quota endpoint is down");

        await ChannelGrain(85).ApplyStatusAsync(Status(85, "available"));
        await ChannelGrain(86).ApplyStatusAsync(Status(86, "warning"));

        await Executor().ExecuteAsync(Group(85, 86), Now);

        Assert.Single(fixture.QuotaRunner.Groups);
        var leader = await ChannelGrain(85).GetStatusAsync();
        Assert.NotNull(leader);
        Assert.Equal("available", leader.Status);
        var member = await ChannelGrain(86).GetStatusAsync();
        Assert.NotNull(member);
        Assert.Equal("warning", member.Status);
    }

    [Fact]
    public async Task TheCoordinatorClientForwardsManualRunRefreshAndInvalidate()
    {
        fixture.QuotaOperations.Reset();

        var client = new GrainProviderQuotaCoordinator(fixture.Cluster.GrainFactory);
        await client.RunManualAsync();
        await client.RefreshChannelAsync(new ProviderQuotaRefreshRequest
        {
            AccountKey = "account-z",
            Channel = new ProviderQuotaChannelSnapshot
            {
                Id = 87,
                Name = "channel-87",
                Type = "claudecode",
            },
        });
        await client.InvalidateAsync(87);

        Assert.Equal([true], fixture.QuotaOperations.Forces);
        Assert.Equal(
            "account-z",
            Assert.Single(fixture.QuotaOperations.Refreshes).AccountKey);
        Assert.Equal([87L], fixture.QuotaOperations.Invalidations);
    }

    [Fact]
    public async Task TheCoordinatorClientPropagatesInvalidationErrors()
    {
        fixture.QuotaOperations.Reset();
        fixture.QuotaOperations.InvalidateFailure = new InvalidOperationException("delete failed");

        var client = new GrainProviderQuotaCoordinator(fixture.Cluster.GrainFactory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.InvalidateAsync(88));

        Assert.Equal("delete failed", error.Message);
    }

    [Fact]
    public async Task OperationsClearTheChannelGrainAfterInvalidating()
    {
        var statuses = new FakeProviderQuotaStatusStore();
        var operations = Operations(statuses);
        var grain = ChannelGrain(91);
        await grain.ApplyStatusAsync(Status(91, "available"));

        await operations.InvalidateAsync(91);

        Assert.Equal([91L], statuses.HardDeletes);
        Assert.Null(await grain.GetStatusAsync());
    }

    [Fact]
    public async Task OperationsStillClearTheChannelGrainWhenTheDeleteFails()
    {
        var statuses = new FakeProviderQuotaStatusStore
        {
            HardDeleteFailure = new InvalidOperationException("delete failed"),
        };
        var operations = Operations(statuses);
        var grain = ChannelGrain(92);
        await grain.ApplyStatusAsync(Status(92, "available"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operations.InvalidateAsync(92));

        Assert.Equal("failed to invalidate provider quota status: delete failed", error.Message);
        Assert.Null(await grain.GetStatusAsync());
    }

    [Fact]
    public void TheScheduleSourceFiresEveryFiveMinutesUtc()
    {
        var source = new GrainProviderQuotaScheduleSource();

        Assert.Equal(
            new DateTimeOffset(2024, 1, 1, 10, 5, 0, TimeSpan.Zero),
            source.NextOccurrence(Now));
    }

    private GrainProviderQuotaOperations Operations(FakeProviderQuotaStatusStore statuses) => new(
        BuildInvalidationService(statuses),
        fixture.Cluster.GrainFactory,
        NullLogger<GrainProviderQuotaOperations>.Instance);

    private static ProviderQuotaService BuildInvalidationService(
        FakeProviderQuotaStatusStore statuses)
    {
        var registry = new ProviderQuotaCheckerRegistry([]);
        return new ProviderQuotaService(
            new SingleStoreScopeFactory(statuses),
            new ProviderQuotaSettingsCache(),
            registry,
            TimeProvider.System,
            NullLogger<ProviderQuotaService>.Instance);
    }

    /// <summary>
    /// The invalidation core resolves only the status store; this shim serves
    /// it and behaves as both the root provider and its own scope.
    /// </summary>
    private sealed class SingleStoreScopeFactory(FakeProviderQuotaStatusStore statuses)
        : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IProviderQuotaStatusStore) ? statuses : null;

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public void Dispose()
        {
        }
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
/// The silo's quota group runner: answers from <see cref="OnRun"/> and records
/// the dispatched groups, so the executor bridge can be asserted without the
/// provider HTTP clients or the database.
/// </summary>
public sealed class FakeProviderQuotaGroupRunner : IProviderQuotaGroupRunner
{
    public List<ProviderQuotaCheckGroup> Groups { get; } = [];

    public Func<ProviderQuotaCheckGroup, ProviderQuotaGroupCheckResult>? OnRun { get; set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Groups.Clear();
        OnRun = null;
        Failure = null;
    }

    public Task<ProviderQuotaGroupCheckResult> RunGroupAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        _ = now;
        _ = cancellationToken;
        Groups.Add(group);

        if (Failure is not null)
        {
            return Task.FromException<ProviderQuotaGroupCheckResult>(Failure);
        }

        return Task.FromResult(OnRun?.Invoke(group) ?? new ProviderQuotaGroupCheckResult());
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
