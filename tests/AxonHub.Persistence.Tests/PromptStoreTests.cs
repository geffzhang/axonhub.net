using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The prompts table as the reference's Ent client sees it
/// (internal/ent/schema/prompt.go + migrate/schema.go:425-465): EF Core writes,
/// Dapper reads, the soft-delete predicate and the (project, name, deleted_at)
/// unique index behave like Ent's generated schema.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PromptStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private static PromptRecord NewPrompt(
        long projectId = 1,
        string name = "prompt",
        string status = "enabled",
        long order = 0,
        string settings = """{"action":{"type":"prepend"}}""")
        => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = name,
            Description = "a prompt",
            Role = "system",
            Content = "You are a helpful assistant.",
            Status = status,
            Order = order,
            Settings = settings,
            ProjectId = projectId,
        };

    [Fact]
    public async Task CreateAndListRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var prompt = NewPrompt(order: 7);
        var id = await store.CreateAsync(prompt);

        Assert.True(id > 0);

        prompt.Id = id;
        var row = Assert.Single(await store.ListEnabledByProjectAsync(1));
        RecordAssert.Equivalent(prompt, row);
        Assert.Equal(0, row.DeletedAt);
    }

    [Fact]
    public async Task ListEnabledByProjectFiltersProjectStatusAndSoftDelete()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var enabled = NewPrompt(name: "enabled");
        await store.CreateAsync(enabled);
        await store.CreateAsync(NewPrompt(name: "disabled", status: "disabled"));
        await store.CreateAsync(NewPrompt(name: "other-project", projectId: 2));

        var deleted = NewPrompt(name: "deleted");
        var deletedId = await store.CreateAsync(deleted);
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompts\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var rows = await store.ListEnabledByProjectAsync(1);

        Assert.Equal(enabled.Id, Assert.Single(rows).Id);
    }

    [Fact]
    public async Task ListEnabledByProjectReturnsAnEmptyListWithoutRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        Assert.Empty(await store.ListEnabledByProjectAsync(99));
    }

    [Fact]
    public async Task NamesAreUniquePerProjectButSharedAcrossProjects()
    {
        // prompts_by_project_id_name covers (project_id, name, deleted_at).
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        await store.CreateAsync(NewPrompt(name: "shared"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewPrompt(name: "shared")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        // A different project may reuse the name.
        await store.CreateAsync(NewPrompt(name: "shared", projectId: 2));
    }

    [Fact]
    public async Task SoftDeletedNamesCanBeReused()
    {
        // Ent's unique index includes deleted_at, so a soft-deleted row does not
        // block a new row with the same name.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var first = NewPrompt(name: "reused");
        var firstId = await store.CreateAsync(first);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompts\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        var second = NewPrompt(name: "reused");
        await store.CreateAsync(second);

        var row = Assert.Single(await store.ListEnabledByProjectAsync(1));
        Assert.Equal(second.Id, row.Id);
    }

    [Fact]
    public async Task SettingsKeepTheirJsonShape()
    {
        // jsonb normalizes formatting and key order; the values must survive.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        const string settings = """
            {
              "action": {"type": "append"},
              "conditions": [{"conditions": [{"type": "model_id", "model_id": "gpt-4"}]}]
            }
            """;
        await store.CreateAsync(NewPrompt(settings: settings));

        var row = Assert.Single(await store.ListEnabledByProjectAsync(1));
        RecordAssert.Json(settings, row.Settings);
    }
}
