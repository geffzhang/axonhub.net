using System.Globalization;
using Dapper;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The v0.4.0 data migration (internal/ent/migrate/datamigrate/v0.4.0.go): a
/// database without a primary storage gets the seeded "Primary" database
/// storage and the default_data_storage_id system row.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DataStorageBootstrapTests(PostgresFixture fixture)
{
    private const string StorageColumns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "primary", "type", "settings", "status"
        """;

    private static async Task<List<DataStorageRecord>> StoragesAsync(NpgsqlConnection connection)
        => (await connection.QueryAsync<DataStorageRecord>(
            "SELECT " + StorageColumns + " FROM \"data_storages\" ORDER BY \"id\";")).ToList();

    private static Task<string?> DefaultStorageIdAsync(NpgsqlConnection connection)
        => connection.ExecuteScalarAsync<string?>(
            """SELECT "value" FROM "systems" WHERE "key" = 'default_data_storage_id';""");

    [Fact]
    public async Task EmptyDatabaseGetsThePrimaryStorageAndDefaultSetting()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        await DataStorageBootstrap.EnsurePrimaryDataStorageAsync(connection);

        var storage = Assert.Single(await StoragesAsync(connection));
        Assert.True(storage.Id > 0);
        Assert.Equal("Primary", storage.Name);
        Assert.Equal("Primary database storage", storage.Description);
        Assert.True(storage.Primary);
        Assert.Equal("database", storage.Type);
        Assert.Equal("{}", storage.Settings);
        Assert.Equal("active", storage.Status);
        Assert.Equal(0, storage.DeletedAt);

        Assert.Equal(
            storage.Id.ToString(CultureInfo.InvariantCulture),
            await DefaultStorageIdAsync(connection));
    }

    [Fact]
    public async Task SecondRunIsANoOp()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        await DataStorageBootstrap.EnsurePrimaryDataStorageAsync(connection);
        var first = Assert.Single(await StoragesAsync(connection));

        await DataStorageBootstrap.EnsurePrimaryDataStorageAsync(connection);

        var again = Assert.Single(await StoragesAsync(connection));
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(
            first.Id.ToString(CultureInfo.InvariantCulture),
            await DefaultStorageIdAsync(connection));
    }

    [Fact]
    public async Task ExistingPrimaryStorageIsRespected()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO "data_storages"
                ("name", "description", "primary", "type", "settings", "status")
            VALUES ('Custom', 'custom storage', TRUE, 'fs', '{}', 'active');
            """);

        await DataStorageBootstrap.EnsurePrimaryDataStorageAsync(connection);

        var storage = Assert.Single(await StoragesAsync(connection));
        Assert.Equal("Custom", storage.Name);

        // The migration returns before touching the systems table.
        Assert.Null(await DefaultStorageIdAsync(connection));
    }

    [Fact]
    public async Task NonPrimaryRowsDoNotSuppressTheSeed()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO "data_storages"
                ("name", "description", "primary", "type", "settings", "status")
            VALUES ('Scratch', 'not primary', FALSE, 'fs', '{}', 'active');
            """);

        await DataStorageBootstrap.EnsurePrimaryDataStorageAsync(connection);

        var rows = await StoragesAsync(connection);
        Assert.Equal(2, rows.Count);
        var seeded = Assert.Single(rows.Where(static row => row.Primary));
        Assert.Equal("Primary", seeded.Name);
        Assert.Equal(
            seeded.Id.ToString(CultureInfo.InvariantCulture),
            await DefaultStorageIdAsync(connection));
    }
}
