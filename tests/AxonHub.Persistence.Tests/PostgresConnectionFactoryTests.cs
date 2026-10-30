using Dapper;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresConnectionFactoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task OpenPinsTheTestSchemaAsCurrentSchema()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using var connection = await db.Factory.OpenAsync();

        var currentSchema = await connection.ExecuteScalarAsync<string>("SELECT current_schema();");
        Assert.Equal(db.Schema, currentSchema);
    }

    [Fact]
    public async Task FactoryKeepsDapperUnderscoreMappingEnabled()
    {
        // Every store maps snake_case columns onto PascalCase records through
        // Dapper, which needs the global underscore mapping switched on.
        _ = new PostgresConnectionFactory(fixture.ConnectionString);

        Assert.True(DefaultTypeMap.MatchNamesWithUnderscores);
    }
}
