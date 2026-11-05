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

    [Fact]
    public async Task GetByIdAppliesTheProjectScopeAndTheSoftDeletePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var prompt = NewPrompt();
        var id = await store.CreateAsync(prompt);

        Assert.NotNull(await store.GetByIdAsync(id, projectScope: null));
        Assert.NotNull(await store.GetByIdAsync(id, projectScope: 1));
        Assert.Null(await store.GetByIdAsync(id, projectScope: 2));
        Assert.Null(await store.GetByIdAsync(id + 1, projectScope: null));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompts\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Null(await store.GetByIdAsync(id, projectScope: null));
    }

    [Fact]
    public async Task ExistsByNameScopesProjectExcludesAndIgnoresDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var prompt = NewPrompt(name: "shared");
        var id = await store.CreateAsync(prompt);
        var otherProject = NewPrompt(name: "shared", projectId: 2);
        await store.CreateAsync(otherProject);

        Assert.True(await store.ExistsByNameAsync(1, "shared", excludeId: null));
        Assert.False(await store.ExistsByNameAsync(1, "shared", excludeId: id));
        Assert.False(await store.ExistsByNameAsync(3, "shared", excludeId: null));
        Assert.False(await store.ExistsByNameAsync(1, "other", excludeId: null));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompts\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.False(await store.ExistsByNameAsync(1, "shared", excludeId: null));
    }

    [Fact]
    public async Task ListAppliesFiltersProjectScopeAndOrderKeysetPaging()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var first = NewPrompt(name: "Alpha", order: 2);
        await store.CreateAsync(first);
        var second = NewPrompt(name: "Beta", order: 1);
        await store.CreateAsync(second);
        await store.CreateAsync(NewPrompt(name: "Gamma", order: 3, status: "disabled"));
        await store.CreateAsync(NewPrompt(name: "Outside", order: 0, projectId: 2));

        var page = await store.ListAsync(new PromptListQuery
        {
            Where = new PromptListFilter { NameContainsFold = "a" },
            ProjectScope = 1,
            SortField = PromptSortField.Order,
            Direction = SortDirection.Asc,
            First = 2,
        });

        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(
            [second.Id, first.Id],
            page.Nodes.Select(static node => node.Id).ToArray());

        var next = await store.ListAsync(new PromptListQuery
        {
            Where = new PromptListFilter { NameContainsFold = "a" },
            ProjectScope = 1,
            SortField = PromptSortField.Order,
            Direction = SortDirection.Asc,
            After = new ListCursor(page.Nodes[^1].Id, page.Nodes[^1].Order),
            First = 2,
        });

        Assert.Single(next.Nodes);
        Assert.False(next.HasNextPage);
        Assert.True(next.HasPreviousPage);
    }

    [Fact]
    public async Task ListOrdersByUpdateTimeDescending()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        await store.CreateAsync(NewPrompt(name: "older"));
        var newer = NewPrompt(name: "newer");
        newer.UpdatedAt = UpdatedAt.AddDays(1);
        await store.CreateAsync(newer);

        var page = await store.ListAsync(new PromptListQuery
        {
            SortField = PromptSortField.UpdatedAt,
            Direction = SortDirection.Desc,
        });

        Assert.Equal(
            ["newer", "older"],
            page.Nodes.Select(static node => node.Name).ToArray());
    }

    [Fact]
    public async Task UpdateWritesFieldsAndHasNoSoftDeletePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var prompt = NewPrompt(name: "before");
        var id = await store.CreateAsync(prompt);

        var affected = await store.UpdateAsync(new PromptUpdate
        {
            Id = id,
            ProjectId = 1,
            Name = "after",
            Description = "new description",
            Role = "assistant",
            Content = "new content",
            Status = "disabled",
            Order = 7,
            Settings = """{"action":{"type":"append"}}""",
        });

        Assert.Equal(1, affected);

        var row = await store.GetByIdAsync(id, projectScope: null);
        Assert.NotNull(row);
        Assert.Equal("after", row.Name);
        Assert.Equal("new description", row.Description);
        Assert.Equal("assistant", row.Role);
        Assert.Equal("new content", row.Content);
        Assert.Equal("disabled", row.Status);
        Assert.Equal(7, row.Order);
        RecordAssert.Json("""{"action":{"type":"append"}}""", row.Settings);
        Assert.True(row.UpdatedAt > UpdatedAt);

        // The predicate is (id, project_id) only: a foreign project misses.
        Assert.Equal(0, await store.UpdateAsync(new PromptUpdate
        {
            Id = id,
            ProjectId = 2,
            Name = "nope",
        }));

        // The aggregate update has no deleted_at predicate, so a soft-deleted
        // row is still written (the service's re-query then reports not found).
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompts\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Equal(1, await store.UpdateStatusAsync(id, 1, "enabled"));
        Assert.Equal(1, await store.BulkUpdateStatusAsync([id], 1, "disabled"));
    }

    [Fact]
    public async Task DeleteSoftDeletesOnceAndScopesTheProject()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var prompt = NewPrompt();
        var id = await store.CreateAsync(prompt);

        Assert.Equal(0, await store.DeleteAsync(id, projectId: 2));
        Assert.Equal(1, await store.DeleteAsync(id, projectId: 1));
        Assert.Equal(0, await store.DeleteAsync(id, projectId: 1));

        await using var connection = await db.Factory.OpenAsync();
        var row = await connection.QuerySingleAsync<RawDeletedRow>(
            """
            SELECT "deleted_at" AS "DeletedAt", "updated_at" AS "UpdatedAt"
            FROM "prompts" WHERE "id" = @Id;
            """,
            new { Id = id });
        Assert.NotEqual(0, row.DeletedAt);
        Assert.True(row.UpdatedAt > UpdatedAt);
    }

    [Fact]
    public async Task BulkMutationsScopeTheProjectAndKeepAggregateSemantics()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptStore(context);

        var live = NewPrompt(name: "live");
        var liveId = await store.CreateAsync(live);
        var deleted = NewPrompt(name: "deleted");
        var deletedId = await store.CreateAsync(deleted);
        var foreign = NewPrompt(name: "foreign", projectId: 2);
        var foreignId = await store.CreateAsync(foreign);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompts\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        // Status updates include soft-deleted rows but stay inside the project.
        var statusAffected = await store.BulkUpdateStatusAsync(
            [liveId, deletedId, foreignId],
            projectId: 1,
            status: "enabled");
        Assert.Equal(2, statusAffected);

        // Bulk delete is a soft-delete update over live rows only.
        var deleteAffected = await store.BulkDeleteAsync(
            [liveId, deletedId, foreignId],
            projectId: 1);
        Assert.Equal(1, deleteAffected);

        Assert.Equal(0, await store.BulkDeleteAsync([], projectId: 1));

        await using var verify = await db.Factory.OpenAsync();
        Assert.Equal(1, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"prompts\" WHERE \"project_id\" = 2 AND \"deleted_at\" = 0;"));
        Assert.Equal(0, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"prompts\" WHERE \"project_id\" = 1 AND \"deleted_at\" = 0;"));
    }

    private sealed class RawDeletedRow
    {
        public long DeletedAt { get; init; }

        public DateTime UpdatedAt { get; init; }
    }
}
