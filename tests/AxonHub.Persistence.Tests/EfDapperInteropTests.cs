using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The CQRS split must not split the transaction. EF Core's context exposes the
/// same <see cref="System.Data.Common.DbConnection"/> and transaction Dapper
/// needs, so a write and a read can participate in one atomic unit of work.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EfDapperInteropTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EfWriteAndDapperReadShareConnectionAndTransaction()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using var context = db.Factory.CreateDbContext();
        await context.Database.OpenConnectionAsync();
        await using var transaction = await context.Database.BeginTransactionAsync();

        var channel = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Type = "openai",
            Name = "in-transaction",
            Status = "enabled",
            Credentials = """{"api_key":"sk-test"}""",
            SupportedModels = """["gpt-4o"]""",
            DefaultTestModel = "gpt-4o",
        };

        context.Channels.Add(channel);
        await context.SaveChangesAsync();

        var connection = context.Database.GetDbConnection();
        var dbTransaction = context.Database.CurrentTransaction!.GetDbTransaction();

        var visible = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """SELECT count(*) FROM "channels" WHERE "id" = @Id;""",
            new { Id = channel.Id },
            transaction: dbTransaction));

        Assert.Equal(1L, visible);

        await transaction.RollbackAsync();

        // The Dapper read happened inside the EF transaction; the rollback must
        // also roll it back.
        var afterRollback = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """SELECT count(*) FROM "channels" WHERE "id" = @Id;""",
            new { Id = channel.Id }));

        Assert.Equal(0L, afterRollback);
    }

    [Fact]
    public async Task StoreWriteJoinsCallerTransactionAndItsDapperReadSeesIt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        long channelId;

        await using (var context = db.Factory.CreateDbContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var store = new ChannelStore(context);

            channelId = await store.CreateAsync(NewChannel("inside-transaction"));

            // The store's read path is Dapper. It must run on the context's
            // connection and transaction, so it sees the uncommitted write.
            var visible = await store.GetByIdAsync(channelId);
            Assert.NotNull(visible);
            Assert.Equal("inside-transaction", visible.Name);

            await transaction.RollbackAsync();
        }

        await using (var context = db.Factory.CreateDbContext())
        {
            var store = new ChannelStore(context);
            Assert.Null(await store.GetByIdAsync(channelId));
        }
    }

    private static ChannelRecord NewChannel(string name) => new()
    {
        CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        UpdatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
        Type = "openai",
        Name = name,
        Status = "enabled",
        Credentials = """{"api_key":"sk-test"}""",
        SupportedModels = """["gpt-4o"]""",
        DefaultTestModel = "gpt-4o",
    };
}
