using System.Security.Cryptography;
using System.Text;
using Dapper;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresOidcStateStoreTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReplicasCanInitializeSharedStateConcurrently()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = 0;

        async Task InitializeAsync()
        {
            await using var connection = await db.Factory.OpenAsync(timeout.Token);
            if (Interlocked.Increment(ref connected) == 8)
                ready.SetResult();
            await ready.Task.WaitAsync(timeout.Token);
            await PostgresOidcStateStore.EnsureCreatedAsync(connection, timeout.Token);
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => InitializeAsync()));
        var store = new PostgresOidcStateStore(db.Factory);
        await store.SetAsync("ready", [1], TimeSpan.FromMinutes(1));
        Assert.Equal(new byte[] { 1 }, await store.ConsumeAsync("ready"));
    }

    [Fact]
    public async Task IndependentStoresShareAndAtomicallyConsumeOneValue()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        await PostgresOidcStateStore.EnsureCreatedAsync(connection);
        await PostgresOidcStateStore.EnsureCreatedAsync(connection);
        var first = new PostgresOidcStateStore(db.Factory);
        var second = new PostgresOidcStateStore(db.Factory);
        var key = "oidc_exchange:raw-bearer-code";
        var value = "42"u8.ToArray();
        await first.SetAsync(key, value, TimeSpan.FromMinutes(5));

        Assert.Equal(value, await second.GetAsync(key));
        var storedKey = await connection.QuerySingleAsync<byte[]>(
            "SELECT lookup_key FROM oidc_state_entries;");
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(key)), storedKey);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => (index % 2 == 0 ? first : second).ConsumeAsync(key)));

        Assert.Equal(value, Assert.Single(results, result => result is not null));
        Assert.Null(await first.GetAsync(key));
        Assert.Null(await second.ConsumeAsync(key));
    }

    [Fact]
    public async Task DatabaseExpiryRejectsReadsAndConsumesAndWritesCleanOnlyABoundedBatch()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        await PostgresOidcStateStore.EnsureCreatedAsync(connection);
        var store = new PostgresOidcStateStore(db.Factory);
        await store.SetAsync("expired", "secret"u8.ToArray(), TimeSpan.FromMinutes(-1));
        Assert.Null(await store.GetAsync("expired"));
        Assert.Null(await store.ConsumeAsync("expired"));

        await connection.ExecuteAsync("""
            INSERT INTO oidc_state_entries (lookup_key, value, expires_at)
            SELECT sha256(convert_to(n::text, 'UTF8')), '\x01'::bytea,
                   statement_timestamp() - interval '1 minute'
            FROM generate_series(1, 150) AS n;
            """);
        await store.SetAsync("live", "live"u8.ToArray(), TimeSpan.FromMinutes(5));

        Assert.Equal(50, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM oidc_state_entries WHERE expires_at < statement_timestamp();"));
        Assert.Equal("live"u8.ToArray(), await store.ConsumeAsync("live"));
    }

    [Fact]
    public async Task SetReplacesValuesAndDeleteIsIdempotent()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        await PostgresOidcStateStore.EnsureCreatedAsync(connection);
        var store = new PostgresOidcStateStore(db.Factory);
        await store.SetAsync("state", "old"u8.ToArray(), TimeSpan.FromMinutes(1));
        await store.SetAsync("state", "new"u8.ToArray(), TimeSpan.FromMinutes(10));
        Assert.Equal("new"u8.ToArray(), await store.GetAsync("state"));

        var remaining = await connection.ExecuteScalarAsync<double>(
            "SELECT extract(epoch FROM expires_at - statement_timestamp()) FROM oidc_state_entries;");
        Assert.InRange(remaining, 590, 600);
        await store.DeleteAsync("state");
        await store.DeleteAsync("state");
        Assert.Null(await store.ConsumeAsync("state"));
    }

    [Fact]
    public async Task InfrastructureFailuresAndCancellationAreNotCacheMisses()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        var store = new PostgresOidcStateStore(db.Factory);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.ConsumeAsync("missing-table"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => store.DeleteAsync("missing-table"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SetAsync("key", [], TimeSpan.FromMinutes(1), cancellation.Token));
    }
}
