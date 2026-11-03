using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The schema is owned by <see cref="DatabaseMigrator"/>'s idempotent DDL, while
/// EF Core owns the write-side model. These tests close the loop: every table,
/// column and index the EF model believes in must exist in the migrated database
/// exactly once and with the same shape.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EfCoreModelTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EfColumnsMatchMigratedSchema()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        await using var connection = await db.Factory.OpenAsync();

        var tables = 0;
        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            Assert.NotNull(table);
            tables++;

            var efColumns = entity.GetProperties()
                .Select(property => property.GetColumnName()!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var dbColumns = (await connection.QueryAsync<string>(new CommandDefinition(
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = current_schema() AND table_name = @Table;
                """,
                new { Table = table }))).OrderBy(name => name, StringComparer.Ordinal).ToArray();

            Assert.Equal(dbColumns, efColumns);
        }

        Assert.Equal(19, tables);
    }

    [Fact]
    public async Task EfIndexesMatchMigratedSchema()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        await using var connection = await db.Factory.OpenAsync();

        var indexes = 0;
        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            Assert.NotNull(table);

            foreach (var index in entity.GetIndexes())
            {
                indexes++;

                var name = index.GetDatabaseName();
                Assert.NotNull(name);

                var rows = (await connection.QueryAsync<IndexRow>(new CommandDefinition(
                    """
                    SELECT i.indisunique AS IsUnique,
                           array_agg(a.attname ORDER BY ord.ordinality) AS Columns
                    FROM pg_index i
                    JOIN pg_class t ON t.oid = i.indrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    JOIN LATERAL unnest(i.indkey::int2[]) WITH ORDINALITY AS ord(attnum, ordinality) ON true
                    JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ord.attnum
                    WHERE n.nspname = current_schema()
                      AND t.relname = @Table
                      AND i.indexrelid = to_regclass(format('%I.%I', current_schema(), @Index))
                    GROUP BY i.indisunique;
                    """,
                    new { Table = table, Index = name }))).AsList();

                var row = Assert.Single(rows);
                Assert.Equal(index.IsUnique, row.IsUnique);
                Assert.Equal(
                    index.Properties.Select(property => property.GetColumnName()),
                    row.Columns);
            }
        }

        Assert.Equal(45, indexes);
    }

    private sealed class IndexRow
    {
        public bool IsUnique { get; set; }

        public string[] Columns { get; set; } = [];
    }
}
