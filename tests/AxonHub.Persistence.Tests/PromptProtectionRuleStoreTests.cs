using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The prompt_protection_rules table as the reference's Ent client sees it
/// (internal/ent/schema/prompt_protection_rule.go + schema/migrate): EF Core
/// writes, Dapper reads, the SoftDeleteMixin predicate only on queries and the
/// (name, deleted_at) unique index behave like Ent's generated schema. The
/// update/delete methods mirror promptprotectionrule_update.sqlSave and the
/// soft-delete hook.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PromptProtectionRuleStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string SettingsJson =
        """{"action":"mask","replacement":"[MASKED]","scopes":["user","assistant"]}""";

    private static PromptProtectionRuleRecord NewRule(
        string name = "rule",
        string status = "enabled",
        string description = "a rule",
        string settings = SettingsJson)
        => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = name,
            Description = description,
            Pattern = "secret",
            Status = status,
            Settings = settings,
        };

    [Fact]
    public async Task CreateAndListEnabledRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var rule = NewRule();
        var id = await store.CreateAsync(rule);

        Assert.True(id > 0);

        rule.Id = id;
        var row = Assert.Single(await store.ListEnabledAsync());
        RecordAssert.Equivalent(rule, row);
        Assert.Equal(0, row.DeletedAt);
        RecordAssert.Json(SettingsJson, row.Settings);
    }

    [Fact]
    public async Task CreateDefaultsStatusToDisabledAndStampsTimestamps()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var rule = new PromptProtectionRuleRecord
        {
            Name = "defaults",
            Pattern = "secret",
            Settings = """{"action":"reject"}""",
            Status = "",
        };

        var id = await store.CreateAsync(rule);

        await using var verify = await db.Factory.OpenAsync();
        var row = await verify.QuerySingleAsync<PromptProtectionRuleRecord>(
            """
            SELECT "id", "created_at" AS "CreatedAt", "updated_at" AS "UpdatedAt",
                   "deleted_at" AS "DeletedAt", "name", "description", "pattern",
                   "status", "settings"
            FROM "prompt_protection_rules" WHERE "id" = @Id;
            """,
            new { Id = id });

        // Ent schema default: status=disabled; TimeMixin stamps both times.
        Assert.Equal("disabled", row.Status);
        Assert.NotEqual(default, row.CreatedAt);
        Assert.Equal(row.CreatedAt, row.UpdatedAt);
        Assert.Equal(0, row.DeletedAt);
    }

    [Fact]
    public async Task ListEnabledFiltersStatusAndSoftDelete()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var enabled = NewRule(name: "enabled");
        await store.CreateAsync(enabled);
        await store.CreateAsync(NewRule(name: "disabled", status: "disabled"));
        await store.CreateAsync(NewRule(name: "archived", status: "archived"));

        var deleted = NewRule(name: "deleted");
        var deletedId = await store.CreateAsync(deleted);
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompt_protection_rules\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        Assert.Equal(enabled.Id, Assert.Single(await store.ListEnabledAsync()).Id);
    }

    [Fact]
    public async Task ListEnabledReturnsAnEmptyListWithoutRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        Assert.Empty(await store.ListEnabledAsync());
    }

    [Fact]
    public async Task GetByIdAndExistsByNameApplyTheSoftDeletePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var rule = NewRule(name: "shared");
        var id = await store.CreateAsync(rule);

        Assert.NotNull(await store.GetByIdAsync(id));
        Assert.Null(await store.GetByIdAsync(id + 1));
        Assert.True(await store.ExistsByNameAsync("shared"));
        Assert.False(await store.ExistsByNameAsync("other"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompt_protection_rules\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Null(await store.GetByIdAsync(id));
        Assert.False(await store.ExistsByNameAsync("shared"));
    }

    [Fact]
    public async Task ListAppliesFiltersAndNameOrderKeysetPaging()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var alpha = NewRule(name: "Alpha");
        await store.CreateAsync(alpha);
        var beta = NewRule(name: "Beta");
        await store.CreateAsync(beta);
        await store.CreateAsync(NewRule(name: "Gamma", status: "disabled"));

        var filter = new PromptProtectionRuleListFilter { NameContainsFold = "a" };
        var page = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = filter,
            SortField = PromptProtectionRuleSortField.Name,
            Direction = SortDirection.Asc,
            First = 2,
        });

        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(
            [alpha.Id, beta.Id],
            page.Nodes.Select(static node => node.Id).ToArray());

        var next = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = filter,
            SortField = PromptProtectionRuleSortField.Name,
            Direction = SortDirection.Asc,
            After = new ListCursor(page.Nodes[^1].Id, page.Nodes[^1].Name),
            First = 2,
        });

        Assert.Equal(3, next.TotalCount);
        Assert.False(next.HasNextPage);
        Assert.True(next.HasPreviousPage);
        Assert.Equal("Gamma", Assert.Single(next.Nodes).Name);
    }

    [Fact]
    public async Task ListAppliesStatusFiltersAndEmptyNotInMatchesEverything()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        await store.CreateAsync(NewRule(name: "enabled"));
        await store.CreateAsync(NewRule(name: "disabled", status: "disabled"));
        await store.CreateAsync(NewRule(name: "archived", status: "archived"));

        var inPage = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = new PromptProtectionRuleListFilter
            {
                StatusIn = ["enabled", "archived"],
            },
        });
        Assert.Equal(
            ["enabled", "archived"],
            inPage.Nodes.Select(static node => node.Name).ToArray());

        var notInPage = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = new PromptProtectionRuleListFilter
            {
                StatusNotIn = ["disabled"],
            },
        });
        Assert.Equal(
            ["enabled", "archived"],
            notInPage.Nodes.Select(static node => node.Name).ToArray());

        // entgql renders an empty NOT IN list as an always-true predicate.
        var emptyNotInPage = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = new PromptProtectionRuleListFilter { StatusNotIn = [] },
        });
        Assert.Equal(3, emptyNotInPage.TotalCount);

        var notPage = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = new PromptProtectionRuleListFilter
            {
                Not = new PromptProtectionRuleListFilter { Status = "disabled" },
            },
        });
        Assert.Equal(
            ["enabled", "archived"],
            notPage.Nodes.Select(static node => node.Name).ToArray());
    }

    [Fact]
    public async Task ListOrdersByUpdatedAtDescendingWithValueCursors()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var older = NewRule(name: "older");
        await store.CreateAsync(older);
        var newer = NewRule(name: "newer");
        newer.UpdatedAt = UpdatedAt.AddDays(1);
        await store.CreateAsync(newer);

        var page = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            SortField = PromptProtectionRuleSortField.UpdatedAt,
            Direction = SortDirection.Desc,
        });

        Assert.Equal(
            ["newer", "older"],
            page.Nodes.Select(static node => node.Name).ToArray());

        var next = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            SortField = PromptProtectionRuleSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(page.Nodes[0].Id, page.Nodes[0].UpdatedAt),
        });

        Assert.Equal("older", Assert.Single(next.Nodes).Name);
        Assert.True(next.HasPreviousPage);
    }

    [Fact]
    public async Task ListDefaultsToIdOrderAndAcceptsCursorlessValueCursors()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var first = NewRule(name: "first");
        await store.CreateAsync(first);
        var second = NewRule(name: "second");
        await store.CreateAsync(second);

        var page = await store.ListAsync(new PromptProtectionRuleListQuery { First = 10 });

        Assert.Equal(
            [first.Id, second.Id],
            page.Nodes.Select(static node => node.Id).ToArray());

        // CREATED_AT folds into the default order, whose cursor carries no value.
        var next = await store.ListAsync(new PromptProtectionRuleListQuery
        {
            SortField = PromptProtectionRuleSortField.Id,
            After = new ListCursor(first.Id, null),
        });

        Assert.Equal(second.Id, Assert.Single(next.Nodes).Id);
    }

    [Fact]
    public async Task UpdateWritesOnlyProvidedFieldsAndReturnsTheRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var rule = NewRule(name: "before", description: "kept", status: "disabled");
        var id = await store.CreateAsync(rule);

        var updated = await store.UpdateAsync(new PromptProtectionRuleUpdate
        {
            Id = id,
            Name = "after",
            Settings = """{"action":"reject"}""",
        });

        Assert.NotNull(updated);
        Assert.Equal(id, updated.Id);
        Assert.Equal("after", updated.Name);
        Assert.Equal("kept", updated.Description);
        Assert.Equal("secret", updated.Pattern);
        Assert.Equal("disabled", updated.Status);
        RecordAssert.Json("""{"action":"reject"}""", updated.Settings);
        Assert.True(updated.UpdatedAt > UpdatedAt);

        var full = await store.UpdateAsync(new PromptProtectionRuleUpdate
        {
            Id = id,
            Name = "final",
            Description = "new description",
            Pattern = "token",
            Status = "enabled",
            Settings = SettingsJson,
        });

        Assert.NotNull(full);
        Assert.Equal("final", full.Name);
        Assert.Equal("new description", full.Description);
        Assert.Equal("token", full.Pattern);
        Assert.Equal("enabled", full.Status);
        RecordAssert.Json(SettingsJson, full.Settings);

        Assert.Null(await store.UpdateAsync(new PromptProtectionRuleUpdate
        {
            Id = id + 1,
            Name = "missing",
        }));
    }

    [Fact]
    public async Task UpdateAndStatusUpdatesHaveNoSoftDeletePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var rule = NewRule(name: "deleted");
        var id = await store.CreateAsync(rule);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompt_protection_rules\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        // UpdateOneID has no deleted_at predicate: a soft-deleted row is still
        // written (the aggregate update reports not-found only when no row
        // matched at all).
        var updated = await store.UpdateAsync(new PromptProtectionRuleUpdate
        {
            Id = id,
            Name = "renamed",
        });
        Assert.NotNull(updated);
        Assert.Equal("renamed", updated.Name);

        var status = await store.UpdateStatusAsync(id, "archived");
        Assert.NotNull(status);
        Assert.Equal("archived", status.Status);

        Assert.Null(await store.UpdateStatusAsync(id + 1, "archived"));
    }

    [Fact]
    public async Task DeleteSoftDeletesOnceAndStampsUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var rule = NewRule();
        var id = await store.CreateAsync(rule);

        Assert.Equal(1, await store.DeleteAsync(id));
        Assert.Equal(0, await store.DeleteAsync(id));

        await using var connection = await db.Factory.OpenAsync();
        var row = await connection.QuerySingleAsync<RawDeletedRow>(
            """
            SELECT "deleted_at" AS "DeletedAt", "updated_at" AS "UpdatedAt"
            FROM "prompt_protection_rules" WHERE "id" = @Id;
            """,
            new { Id = id });
        Assert.NotEqual(0, row.DeletedAt);
        Assert.True(row.UpdatedAt > UpdatedAt);
    }

    [Fact]
    public async Task BulkMutationsKeepAggregateSemantics()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var live = NewRule(name: "live");
        var liveId = await store.CreateAsync(live);
        var deleted = NewRule(name: "deleted");
        var deletedId = await store.CreateAsync(deleted);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"prompt_protection_rules\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        // Status updates include soft-deleted rows.
        Assert.Equal(
            2,
            await store.BulkUpdateStatusAsync([liveId, deletedId, 9_999], "enabled"));

        // Bulk delete is a soft-delete update over live rows only.
        Assert.Equal(1, await store.BulkDeleteAsync([liveId, deletedId, 9_999]));

        Assert.Equal(0, await store.BulkDeleteAsync([]));
        Assert.Equal(0, await store.BulkUpdateStatusAsync([], "disabled"));

        await using var verify = await db.Factory.OpenAsync();
        // Both rows are soft-deleted now: one before the bulk call, one by it.
        Assert.Equal(0, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"prompt_protection_rules\" WHERE \"deleted_at\" = 0;"));
        Assert.Equal(2, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"prompt_protection_rules\" WHERE \"status\" = 'enabled';"));
    }

    [Fact]
    public async Task NamesAreUniqueAmongLiveRows()
    {
        // prompt_protection_rules_by_name covers (name, deleted_at).
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new PromptProtectionRuleStore(context);

        var first = NewRule(name: "shared");
        var firstId = await store.CreateAsync(first);

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewRule(name: "shared")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        // Ent's unique index includes deleted_at, so a soft-deleted row does not
        // block a new row with the same name.
        Assert.Equal(1, await store.DeleteAsync(firstId));
        await store.CreateAsync(NewRule(name: "shared"));

        Assert.Equal(1, (await store.ListAsync(new PromptProtectionRuleListQuery
        {
            Where = new PromptProtectionRuleListFilter { Name = "shared" },
        })).TotalCount);
    }

    private sealed class RawDeletedRow
    {
        public long DeletedAt { get; init; }

        public DateTime UpdatedAt { get; init; }
    }
}
