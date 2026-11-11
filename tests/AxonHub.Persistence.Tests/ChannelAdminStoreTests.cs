using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChannelAdminStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task ListAppliesFiltersKeysetPaginationAndOrdering()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var alpha = await SeedChannelAsync(context, "openai", "Alpha", "enabled", weight: 3, tags: """["prod"]""");
        var bravo = await SeedChannelAsync(context, "anthropic", "Bravo", "disabled", weight: 1, tags: """["dev"]""");
        var charlie = await SeedChannelAsync(context, "openai", "Charlie", "enabled", weight: 2);
        await SeedChannelAsync(context, "openai", "Deleted", "enabled", weight: 0, deletedAt: 1234);

        var page = await store.ListAsync(new ChannelListQuery { First = 2 });
        Assert.Equal([alpha.Id, bravo.Id], page.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(3, page.TotalCount);

        var next = await store.ListAsync(new ChannelListQuery
        {
            After = new ListCursor(bravo.Id, null),
            First = 2,
        });
        Assert.Equal([charlie.Id], next.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(next.HasPreviousPage);

        var activated = await store.ListAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { StatusIn = ["enabled"] },
        });
        Assert.Equal([alpha.Id, charlie.Id], activated.Nodes.Select(static row => row.Id).ToArray());
        Assert.Equal(2, activated.TotalCount);

        var folded = await store.ListAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { NameContainsFold = "ALPH" },
        });
        Assert.Equal([alpha.Id], folded.Nodes.Select(static row => row.Id).ToArray());

        var tagged = await store.ListAsync(new ChannelListQuery { HasTag = "prod" });
        Assert.Equal([alpha.Id], tagged.Nodes.Select(static row => row.Id).ToArray());

        var noError = await store.ListAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { ErrorMessageIsNil = true },
        });
        Assert.Equal(3, noError.TotalCount);

        // ORDERING_WEIGHT DESC issues an integer keyset cursor.
        var byWeight = await store.ListAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.OrderingWeight,
            Direction = SortDirection.Desc,
            First = 1,
        });
        Assert.Equal([alpha.Id], byWeight.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(byWeight.HasNextPage);

        var afterWeight = await store.ListAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.OrderingWeight,
            Direction = SortDirection.Desc,
            After = new ListCursor(alpha.Id, alpha.OrderingWeight),
            First = 2,
        });
        Assert.Equal([charlie.Id, bravo.Id], afterWeight.Nodes.Select(static row => row.Id).ToArray());

        // last walks backwards and still returns the tail in ascending order.
        var tail = await store.ListAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.Name,
            Direction = SortDirection.Asc,
            Last = 2,
        });
        Assert.Equal([bravo.Id, charlie.Id], tail.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(tail.HasPreviousPage);
        Assert.False(tail.HasNextPage);
    }

    [Fact]
    public async Task ListAllOrdersOnlyWhenTheCallerSuppliedAnOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var alpha = await SeedChannelAsync(context, "openai", "Alpha", "enabled", weight: 2);
        var bravo = await SeedChannelAsync(context, "openai", "Bravo", "enabled", weight: 2);
        var charlie = await SeedChannelAsync(context, "openai", "Charlie", "enabled", weight: 1);

        var unordered = await store.ListAllAsync(new ChannelListQuery());
        Assert.Equal(
            [alpha.Id, bravo.Id, charlie.Id],
            unordered.Select(static row => row.Id).ToArray());

        var ordered = await store.ListAllAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.OrderingWeight,
            Direction = SortDirection.Desc,
            Ordered = true,
        });

        // Equal weights fall back to id ASC; the orderBy field keeps its direction.
        Assert.Equal(
            [alpha.Id, bravo.Id, charlie.Id],
            ordered.Select(static row => row.Id).ToArray());

        var filtered = await store.ListAllAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { Name = "Bravo" },
            Ordered = true,
            SortField = ChannelSortField.Name,
        });
        Assert.Equal([bravo.Id], filtered.Select(static row => row.Id).ToArray());
    }

    [Fact]
    public async Task SummariesSelectEnabledAndDisabledOrderedByWeight()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var enabled = await SeedChannelAsync(context, "openai", "Enabled", "enabled", weight: 9);
        var disabled = await SeedChannelAsync(context, "anthropic", "Disabled", "disabled", weight: 1);
        var archived = await SeedChannelAsync(context, "openai", "Archived", "archived", weight: 100);
        await SeedChannelAsync(context, "openai", "Gone", "enabled", weight: 50, deletedAt: 42);

        var live = await store.ListSummariesAsync(includeArchived: false);
        Assert.Equal([enabled.Id, disabled.Id], live.Select(static row => row.Id).ToArray());

        var includingArchived = await store.ListSummariesAsync(includeArchived: true);
        Assert.Equal(
            [archived.Id, enabled.Id, disabled.Id],
            includingArchived.Select(static row => row.Id).ToArray());

        // The project branch's system-bypass query is enabled-only.
        var enabledOnly = await store.ListEnabledSummariesAsync();
        Assert.Equal([enabled.Id], enabledOnly.Select(static row => row.Id).ToArray());
    }

    [Fact]
    public async Task TagRowsSkipArchivedRowsAndCountsApplyTheStatusPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        await SeedChannelAsync(context, "openai", "One", "enabled", tags: """["prod","eu"]""");
        await SeedChannelAsync(context, "openai", "Two", "disabled", tags: """["dev"]""");
        await SeedChannelAsync(context, "anthropic", "Three", "enabled");
        await SeedChannelAsync(context, "openai", "Archived", "archived", tags: """["gone"]""");
        await SeedChannelAsync(context, "openai", "Deleted", "enabled", tags: """["gone"]""", deletedAt: 9);

        // jsonb storage normalizes whitespace inside the array text.
        var tagRows = await store.ListTagSourcesAsync();
        Assert.Equal(3, tagRows.Count);
        Assert.Equal("""["prod", "eu"]""", tagRows[0].Tags);
        Assert.Equal("""["dev"]""", tagRows[1].Tags);
        Assert.Null(tagRows[2].Tags);

        var allLive = await store.CountByTypeAsync(statusIn: null);
        Assert.Equal(
            [("anthropic", 1), ("openai", 2)],
            allLive.Select(static row => (row.Type, row.Count)).ToArray());

        var enabledOnly = await store.CountByTypeAsync(statusIn: ["enabled"]);
        Assert.Equal(
            [("anthropic", 1), ("openai", 1)],
            enabledOnly.Select(static row => (row.Type, row.Count)).ToArray());

        // An explicitly empty statusIn falls back to "not archived".
        var emptyStatuses = await store.CountByTypeAsync(statusIn: []);
        Assert.Equal(3, emptyStatuses.Sum(static row => row.Count));
    }

    private static async Task<ChannelRecord> SeedChannelAsync(
        AxonHubDbContext context,
        string type,
        string name,
        string status,
        long weight = 0,
        string? tags = null,
        long deletedAt = 0)
    {
        var channel = new ChannelRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Type = type,
            Name = name,
            Status = status,
            Credentials = "{}",
            SupportedModels = "null",
            AutoSyncModelPattern = "",
            DefaultTestModel = "",
            Tags = tags,
            OrderingWeight = weight,
        };

        context.Channels.Add(channel);
        await context.SaveChangesAsync();
        return channel;
    }
}
