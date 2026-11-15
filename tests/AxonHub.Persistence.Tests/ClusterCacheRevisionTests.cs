using Dapper;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ClusterCacheRevisionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReplicasCanMigrateAnEmptyDatabaseConcurrently()
    {
        await using var db = new TestDatabase(fixture);
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync("CREATE SCHEMA \"" + db.Schema + "\"");
        }

        async Task MigrateAsync()
        {
            await using var connection = await db.Factory.OpenAsync();
            await DatabaseMigrator.EnsureCreatedAsync(connection);
        }

        await Task.WhenAll(MigrateAsync(), MigrateAsync());
    }

    [Fact]
    public async Task RevisionChangesWithCommittedWritesButNotRollbacks()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var connection = await db.Factory.OpenAsync();
        await PostgresCacheRevisionSource.EnsureCreatedAsync(connection);
        await PostgresCacheRevisionSource.EnsureCreatedAsync(connection);
        var source = new PostgresCacheRevisionSource(db.Factory);
        var initial = await source.GetRevisionAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await connection.ExecuteAsync("UPDATE channels SET name = name", transaction: transaction);
            await transaction.RollbackAsync();
        }

        Assert.Equal(initial, await source.GetRevisionAsync());
        await connection.ExecuteAsync("UPDATE channels SET name = name");
        Assert.True(await source.GetRevisionAsync() > initial);
    }
}
