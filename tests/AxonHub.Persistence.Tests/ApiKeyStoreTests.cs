using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiKeyStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey();
        var id = await store.CreateAsync(apiKey);

        Assert.True(id > 0);

        apiKey.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(apiKey, actual);
    }

    [Fact]
    public async Task GetByKeyReturnsTheLiveRow()
    {
        // APIKeyService.GetAPIKey: APIKey.Query().Where(KeyEQ(key)).Only(),
        // which applies the soft-delete predicate.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey();
        var id = await store.CreateAsync(apiKey);

        var actual = await store.GetByKeyAsync("sk-ported-key");

        Assert.NotNull(actual);
        Assert.Equal(id, actual.Id);
        Assert.Equal(1, actual.ProjectId);
    }

    [Fact]
    public async Task GetByKeyIgnoresSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey();
        var id = await store.CreateAsync(apiKey);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"api_keys\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Null(await store.GetByIdAsync(id));
        Assert.Null(await store.GetByKeyAsync("sk-ported-key"));
    }

    [Fact]
    public async Task KeysAreUniqueAcrossAllRows()
    {
        // api_keys_by_key is unique on key alone (Ent's APIKeysTable), unlike the
        // soft-deleted-name indexes on channels/projects/users.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var firstId = await store.CreateAsync(NewApiKey());

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewApiKey()));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"api_keys\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        // Soft deleting an api key does not free its key value.
        var stillDuplicate = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewApiKey()));
        var stillUnique = Assert.IsType<PostgresException>(stillDuplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, stillUnique.SqlState);
    }

    private static ApiKeyRecord NewApiKey() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Key = "sk-ported-key",
        Name = "default",
        Type = "user",
        Status = "enabled",
        Scopes = """["write_requests","read_channels"]""",
        Profiles = """{"activeProfile":"","profiles":null}""",
        AllowedIps = """["127.0.0.1","10.0.0.0/8"]""",
        ProjectId = 1,
        UserId = null,
    };
}
