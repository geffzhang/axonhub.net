using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SystemStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        var id = await store.CreateAsync(system);

        Assert.True(id > 0);

        system.Id = id;
        RecordAssert.Equivalent(system, (await store.GetByIdAsync(id))!);
        RecordAssert.Equivalent(system, (await store.GetByKeyAsync(system.Key))!);
    }

    [Fact]
    public async Task MissingKeyReturnsNull()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        Assert.Null(await store.GetByKeyAsync("missing"));
    }

    [Fact]
    public async Task SoftDeletedSettingsAreInvisible()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        var id = await store.CreateAsync(system);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"systems\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Null(await store.GetByIdAsync(id));
        Assert.Null(await store.GetByKeyAsync(system.Key));
    }

    [Fact]
    public async Task KeysStayUniqueEvenForSoftDeletedRows()
    {
        // Ent declares systems.key unique on its own (not (key, deleted_at)),
        // unlike the soft-delete-aware unique indexes on users/projects.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        var id = await store.CreateAsync(system);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewSystem()));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"systems\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewSystem()));
        postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    [Fact]
    public async Task SetAsyncInsertsANewRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        await store.SetAsync("retry_policy", """{"enabled":true}""");

        var row = await store.GetByKeyAsync("retry_policy");
        Assert.NotNull(row);
        Assert.Equal("""{"enabled":true}""", row.Value);
        Assert.Equal(0, row.DeletedAt);
    }

    [Fact]
    public async Task SetAsyncUpdatesTheConflictingRowInPlace()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var id = await store.CreateAsync(NewSystem());

        await store.SetAsync(NewSystem().Key, "new");

        var row = await store.GetByIdAsync(id);
        Assert.NotNull(row);
        Assert.Equal("new", row.Value);
        // OnConflict(key).UpdateNewValues() keeps the immutable creation time.
        Assert.Equal(CreatedAt, row.CreatedAt);

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"systems\";"));
    }

    [Fact]
    public async Task SetAsyncResurrectsASoftDeletedRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        var id = await store.CreateAsync(system);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"systems\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Null(await store.GetByKeyAsync(system.Key));

        await store.SetAsync(system.Key, "new");

        var row = await store.GetByKeyAsync(system.Key);
        Assert.NotNull(row);
        Assert.Equal(id, row.Id);
        Assert.Equal("new", row.Value);
        Assert.Equal(0, row.DeletedAt);
    }

    private static SystemRecord NewSystem() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Key = "security_settings",
        Value = """{"blocked_ips":["203.0.113.0/24"]}""",
    };
}
