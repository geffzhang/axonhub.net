using Dapper;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChannelAdmissionStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task IndependentReplicasShareCapacityAndFifoAndReleaseIsIdempotent()
    {
        await using var db = await CreateDatabaseAsync();
        var first = new PostgresChannelAdmissionStore(db.Factory);
        var second = new PostgresChannelAdmissionStore(db.Factory);
        var active = Guid.NewGuid();
        var head = Guid.NewGuid();
        var tail = Guid.NewGuid();
        Assert.Equal(AdmissionState.Granted, await first.JoinAsync(active, 1, "a", 1, 2));
        Assert.Equal(AdmissionState.Waiting, await second.JoinAsync(head, 1, "b", 1, 2));
        Assert.Equal(AdmissionState.Waiting, await first.JoinAsync(tail, 1, "a", 1, 2));
        Assert.Equal(AdmissionState.Full, await second.JoinAsync(Guid.NewGuid(), 1, "b", 1, 2));
        var stats = Assert.Single(await second.GetStatsAsync(1));
        Assert.Equal(1, stats.InFlight);
        Assert.Equal(2, stats.Waiting);
        Assert.Equal(AdmissionState.Waiting, await first.PollAsync(tail, 1, 1));

        await first.ReleaseAsync(active, 1);
        await first.ReleaseAsync(active, 1);
        Assert.Equal(AdmissionState.Waiting, await first.PollAsync(tail, 1, 1));
        Assert.Equal(AdmissionState.Granted, await second.PollAsync(head, 1, 1));
        await second.ReleaseAsync(head, 1);
        Assert.Equal(AdmissionState.Granted, await first.PollAsync(tail, 1, 1));
    }

    [Fact]
    public async Task GrantedPermitsSurviveRestartAndDoNotExpireWithWaitingTickets()
    {
        await using var db = await CreateDatabaseAsync();
        var store = new PostgresChannelAdmissionStore(db.Factory);
        var active = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        await store.JoinAsync(active, 1, "dead-process", 1, 1);
        await store.JoinAsync(waiting, 1, "dead-process", 1, 1);
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE axonhub_channel_admissions SET touched_at = CURRENT_TIMESTAMP - interval '1 day'");

        var restarted = new PostgresChannelAdmissionStore(db.Factory);
        var rows = await restarted.ListAsync();
        Assert.Contains(rows, row => row.Id == active && row.Granted);
        Assert.Equal(AdmissionState.Waiting,
            await restarted.JoinAsync(Guid.NewGuid(), 1, "survivor", 1, 1));
        await restarted.CancelWaitingAsync(active, 1);
        Assert.Contains(await restarted.ListAsync(), row => row.Id == active);
        await restarted.ReleaseAsync(active, 1);
        Assert.DoesNotContain(await restarted.ListAsync(), row => row.Id == active);
    }

    [Fact]
    public async Task ConcurrentArrivalsCannotExceedCapacityAndCanceledHeadDoesNotBlock()
    {
        await using var db = await CreateDatabaseAsync();
        var store = new PostgresChannelAdmissionStore(db.Factory);
        var ids = Enumerable.Range(0, 12).Select(_ => Guid.NewGuid()).ToArray();
        var results = await Task.WhenAll(ids.Select(id => store.JoinAsync(id, 1, "node", 2, 20)));
        Assert.Equal(2, results.Count(result => result == AdmissionState.Granted));
        foreach (var id in ids)
        {
            await store.CancelWaitingAsync(id, 1);
        }

        Assert.Equal(2, (await store.ListAsync()).Count);
        foreach (var id in ids)
        {
            await store.ReleaseAsync(id, 1);
        }

        Assert.Equal(AdmissionState.Granted, await store.JoinAsync(Guid.NewGuid(), 1, "node", 2, 1));
    }

    [Fact]
    public async Task WaitingRequestsObserveCapacityChangesCommittedByAnotherReplica()
    {
        await using var db = await CreateDatabaseAsync();
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync("""
            INSERT INTO channels(id, type, name, credentials, supported_models, default_test_model, settings)
            VALUES (1, 'openai', 'limited', '{}', '[]', 'test', '{"rateLimit":{"maxConcurrent":2}}')
            """);
        var store = new PostgresChannelAdmissionStore(db.Factory);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        await store.JoinAsync(first, 1, "a", 2, 1);
        await store.JoinAsync(second, 1, "a", 2, 1);
        await store.JoinAsync(waiting, 1, "b", 2, 1);
        await connection.ExecuteAsync(
            """UPDATE channels SET settings = '{"rateLimit":{"maxConcurrent":1}}'""");
        await store.ReleaseAsync(first, 1);
        Assert.Equal(AdmissionState.Waiting, await store.PollAsync(waiting, 1, 2));
        await store.ReleaseAsync(second, 1);
        Assert.Equal(AdmissionState.Granted, await store.PollAsync(waiting, 1, 2));
    }

    [Fact]
    public async Task CancellationRacingWithGrantNeverReturnsAnUnaccountedPermit()
    {
        await using var db = await CreateDatabaseAsync();
        var store = new PostgresChannelAdmissionStore(db.Factory);
        var active = Guid.NewGuid();
        var waiting = Guid.NewGuid();
        await store.JoinAsync(active, 1, "a", 1, 1);
        await store.JoinAsync(waiting, 1, "b", 1, 1);
        await store.ReleaseAsync(active, 1);
        var poll = store.PollAsync(waiting, 1, 1);
        var cancel = store.CancelWaitingAsync(waiting, 1);
        await Task.WhenAll(poll, cancel);
        var rows = await store.ListAsync();
        if (await poll == AdmissionState.Granted)
        {
            Assert.Contains(rows, row => row.Id == waiting && row.Granted);
        }
        else
        {
            Assert.DoesNotContain(rows, row => row.Id == waiting);
        }
    }

    private async Task<TestDatabase> CreateDatabaseAsync()
    {
        var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var connection = await db.Factory.OpenAsync();
        await PostgresChannelAdmissionStore.EnsureCreatedAsync(connection);
        return db;
    }
}
