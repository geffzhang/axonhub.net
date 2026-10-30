using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);

        Assert.True(id > 0);

        project.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(project, actual);
    }

    [Fact]
    public async Task GetFirstReturnsTheLowestLiveProject()
    {
        // api_key.EnsureNoAuthAPIKey resolves the default project with
        // Project.Query().Order(Asc(id)).First: the lowest live id wins.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var first = NewProject();
        first.Name = "default";
        var firstId = await store.CreateAsync(first);

        var second = NewProject();
        second.Name = "second";
        await store.CreateAsync(second);

        var actual = await store.GetFirstAsync();

        Assert.NotNull(actual);
        Assert.Equal(firstId, actual.Id);
    }

    [Fact]
    public async Task SoftDeletedProjectsAreInvisible()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var deleted = NewProject();
        deleted.Name = "deleted";
        var deletedId = await store.CreateAsync(deleted);

        var live = NewProject();
        live.Name = "live";
        var liveId = await store.CreateAsync(live);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"projects\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        Assert.Null(await store.GetByIdAsync(deletedId));

        var first = await store.GetFirstAsync();
        Assert.NotNull(first);
        Assert.Equal(liveId, first.Id);
    }

    [Fact]
    public async Task LiveProjectNamesAreUniqueAndSoftDeletedNamesCanBeReused()
    {
        // projects_by_name is unique on (name, deleted_at), mirroring Ent.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var firstId = await store.CreateAsync(project);

        var duplicate = NewProject();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(duplicate));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"projects\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        var reusedId = await store.CreateAsync(duplicate);
        Assert.True(reusedId > firstId);
    }

    private static ProjectRecord NewProject() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = "default",
        Description = "Default project",
        Status = "active",
        Profiles = """{"activeProfile":"default","profiles":[]}""",
    };
}
