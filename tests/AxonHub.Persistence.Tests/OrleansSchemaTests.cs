using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the opt-in Orleans AdoNet schema: membership, reminders and the
/// OrleansQuery rows must be created idempotently before the first silo joins.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OrleansSchemaTests(PostgresFixture fixture)
{
    private static readonly string[] ExpectedTables =
    [
        "orleansmembershiptable",
        "orleansmembershipversiontable",
        "orleansquery",
        "orleansreminderstable",
    ];

    [Fact]
    public async Task CreatesTheAdoNetSchemaIdempotently()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using (var connection = await db.Factory.OpenAsync())
        {
            await OrleansSchema.EnsureCreatedAsync(connection);
            await OrleansSchema.EnsureCreatedAsync(connection);
        }

        await using var verify = await db.Factory.OpenAsync();

        var tables = (await verify.QueryAsync<string>("""
            SELECT table_name
            FROM information_schema.tables
            WHERE table_schema = current_schema()
              AND table_name IN (
                'orleansquery',
                'orleansmembershiptable',
                'orleansmembershipversiontable',
                'orleansreminderstable');
            """))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedTables, tables);

        var queryCount = await verify.ExecuteScalarAsync<int>(
            "SELECT COUNT(*)::int FROM \"orleansquery\";");
        Assert.True(queryCount >= 17);
        Assert.Equal(
            1,
            await verify.ExecuteScalarAsync<int>(
                """SELECT COUNT(*)::int FROM "orleansquery" WHERE "querykey" = 'UpsertReminderRowKey';"""));
    }
}
