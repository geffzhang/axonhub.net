using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the Dapper/EF rendering of the reference ent Model store
/// (internal/ent/model.go, model_update.go): schema defaults, the soft-delete
/// predicate, the SET order with ClearField("remark"), aggregate updates and
/// the entgql keyset pagination.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ModelStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string DefaultModelCardJson =
        """{"reasoning":{"supported":false,"default":false},"reasoningEfforts":null,"toolCall":false,"temperature":false,"modalities":{"input":null,"output":null},"vision":false,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"limit":{"context":0,"output":0},"knowledge":"","releaseDate":"","lastUpdated":""}""";

    private const string DefaultSettingsJson =
        """{"disableDeveloperSettingsInheritance":false,"associations":null,"loadBalancerStrategy":"default","traceStickyMode":"default"}""";

    [Fact]
    public async Task CreateAppliesTheEntSchemaDefaults()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var model = NewModel("gpt-4o");
        // The record's CLR initializers hide the schema defaults, so clear the
        // columns ent would fill in.
        model.Type = string.Empty;
        model.Status = string.Empty;
        model.ModelCard = string.Empty;
        model.Settings = string.Empty;
        model.CreatedAt = default;
        model.UpdatedAt = default;

        var id = await store.CreateAsync(model);
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        Assert.Equal("chat", actual.Type);
        Assert.Equal("disabled", actual.Status);
        RecordAssert.Json(DefaultModelCardJson, actual.ModelCard);
        RecordAssert.Json(DefaultSettingsJson, actual.Settings);
        Assert.NotEqual(default, actual.CreatedAt);
        Assert.Equal(actual.CreatedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task CreateAndGetRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var model = NewModel("gpt-4o");
        model.ModelCard =
            """{"reasoning":{"supported":true,"default":false},"reasoningEfforts":["high"],"toolCall":true,"temperature":true,"modalities":{"input":["text"],"output":["text"]},"vision":true,"cost":{"input":1.5,"output":2.5,"cacheRead":0.5,"cacheWrite":0.25},"limit":{"context":128000,"output":32000},"knowledge":"2025-01","releaseDate":"2025-01-01","lastUpdated":"2025-02-01"}""";
        model.Settings =
            """{"disableDeveloperSettingsInheritance":true,"associations":[],"loadBalancerStrategy":"default","traceStickyMode":"default"}""";
        model.Remark = "ported";

        var id = await store.CreateAsync(model);

        Assert.True(id > 0);

        model.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(model, actual);
    }

    [Fact]
    public async Task LiveRowQueriesIgnoreSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var deleted = NewModel("gpt-4o");
        var deletedId = await store.CreateAsync(deleted);
        await store.DeleteAsync(deletedId);

        var live = NewModel("gpt-4o");
        live.Name = "live";
        var liveId = await store.CreateAsync(live);

        Assert.Null(await store.GetByIdAsync(deletedId));

        // models_by_model_id is unique on (model_id, deleted_at), so the live
        // row with the same model id is a separate row.
        var first = await store.GetFirstByModelIdAsync("gpt-4o");
        Assert.NotNull(first);
        Assert.Equal(liveId, first.Id);

        var byPairs = await store.ListByDeveloperModelIdPairsAsync(
            [new ModelDeveloperModelId("openai", "gpt-4o")]);
        Assert.Equal([liveId], byPairs.Select(model => model.Id));

        var all = await store.ListAllAsync(new ModelListFilter());
        Assert.Equal([liveId], all.Select(model => model.Id));

        // The OR-of-AND predicate over zero pairs is always false.
        Assert.Empty(await store.ListByDeveloperModelIdPairsAsync([]));
    }

    [Fact]
    public async Task ListAppliesThePortedPredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var alpha = await SeedAsync(store, "openai", "gpt-4o", "Alpha", "enabled", "chat", remark: null);
        var beta = await SeedAsync(store, "openai", "gpt-4o-mini", "Beta", "disabled", "embedding", remark: "note");
        var gamma = await SeedAsync(store, "anthropic", "claude", "Gamma", "enabled", "chat", remark: null);
        var delta = await SeedAsync(store, "anthropic", "claude-3", "Delta", "archived", "chat");
        await SoftDeleteAsync(db, gamma);

        async Task<long[]> IdsAsync(ModelListFilter filter)
            => [.. (await store.ListAllAsync(filter)).Select(model => model.Id)];

        Assert.Equal([alpha, beta], await IdsAsync(new ModelListFilter { DeveloperContainsFold = "OPEN" }));
        Assert.Equal([beta], await IdsAsync(new ModelListFilter { ModelIdContainsFold = "MINI" }));
        Assert.Equal([alpha], await IdsAsync(new ModelListFilter { StatusIn = ["enabled"] }));
        Assert.Equal(
            [alpha, delta],
            await IdsAsync(new ModelListFilter { ModelIdIn = ["gpt-4o", "claude-3"] }));
        // The models endpoint's full-data lookup pairs the id set with the
        // enabled status (openai.go:877-883).
        Assert.Equal(
            [alpha],
            await IdsAsync(new ModelListFilter
            {
                Status = "enabled",
                ModelIdIn = ["gpt-4o", "claude-3"],
            }));
        // entgql ignores an empty In-list (gamma is soft-deleted).
        Assert.Equal([alpha, beta, delta], await IdsAsync(new ModelListFilter { ModelIdIn = [] }));
        Assert.Equal([beta], await IdsAsync(new ModelListFilter { TypeNotIn = ["chat"] }));
        Assert.Equal([alpha, delta], await IdsAsync(new ModelListFilter { RemarkIsNil = true }));
        Assert.Equal([beta], await IdsAsync(new ModelListFilter { RemarkNotNil = true }));
        Assert.Equal(
            [alpha, beta],
            await IdsAsync(new ModelListFilter
            {
                Or =
                [
                    new ModelListFilter { NameContainsFold = "ALP" },
                    new ModelListFilter { Status = "disabled" },
                ],
            }));
        Assert.Equal([beta, delta], await IdsAsync(new ModelListFilter
        {
            Not = new ModelListFilter { Status = "enabled" },
        }));
        Assert.Equal([alpha], await IdsAsync(new ModelListFilter
        {
            And =
            [
                new ModelListFilter { Developer = "openai" },
                new ModelListFilter { Status = "enabled" },
            ],
        }));
        Assert.Equal([alpha], await IdsAsync(new ModelListFilter { Id = alpha }));
        Assert.Equal([alpha], await IdsAsync(new ModelListFilter { IdIn = [alpha, gamma] }));
        // The port renders an empty or-list as an always-false predicate (C177).
        Assert.Empty(await IdsAsync(new ModelListFilter { Or = [] }));
    }

    [Fact]
    public async Task ListPaginatesByTheRequestedSortField()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var alpha = await SeedAsync(store, "openai", "gpt-4o", "Alpha", "enabled");
        var beta = await SeedAsync(store, "openai", "gpt-4o-mini", "Beta", "disabled");
        var gamma = await SeedAsync(store, "anthropic", "claude", "Gamma", "enabled");

        var first = await store.ListAsync(new ModelListQuery
        {
            SortField = ModelSortField.Name,
            Direction = SortDirection.Asc,
            First = 2,
        });

        Assert.Equal([alpha, beta], first.Nodes.Select(model => model.Id));
        Assert.True(first.HasNextPage);
        Assert.False(first.HasPreviousPage);
        Assert.Equal(3, first.TotalCount);

        var next = await store.ListAsync(new ModelListQuery
        {
            SortField = ModelSortField.Name,
            Direction = SortDirection.Asc,
            After = new ListCursor(beta, "Beta"),
            First = 2,
        });

        Assert.Equal([gamma], next.Nodes.Select(model => model.Id));
        Assert.False(next.HasNextPage);
        Assert.True(next.HasPreviousPage);

        var descending = await store.ListAsync(new ModelListQuery
        {
            SortField = ModelSortField.Name,
            Direction = SortDirection.Desc,
            First = 2,
        });

        Assert.Equal([gamma, beta], descending.Nodes.Select(model => model.Id));

        // Last walks backwards from the end but returns rows in the requested
        // (ascending id) order.
        var last = await store.ListAsync(new ModelListQuery
        {
            Direction = SortDirection.Asc,
            Last = 2,
        });

        Assert.Equal([beta, gamma], last.Nodes.Select(model => model.Id));
        Assert.True(last.HasPreviousPage);
        Assert.False(last.HasNextPage);
        Assert.Equal(3, last.TotalCount);
    }

    [Fact]
    public async Task UpdateSetsFieldsInEntOrderAndClearsRemark()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var id = await SeedAsync(store, "openai", "gpt-4o", "Alpha", "enabled", remark: "before");

        var updated = await store.UpdateAsync(new ModelUpdate
        {
            Id = id,
            Name = "Renamed",
            Type = "embedding",
            ModelCard = """{"limit":{"context":1,"output":2}}""",
            Settings = """{"loadBalancerStrategy":"default","traceStickyMode":"default"}""",
            Status = "disabled",
            Remark = "after",
        });

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("embedding", updated.Type);
        Assert.Equal("disabled", updated.Status);
        Assert.Equal("after", updated.Remark);
        RecordAssert.Json("""{"limit":{"context":1,"output":2}}""", updated.ModelCard);

        var cleared = await store.UpdateAsync(new ModelUpdate
        {
            Id = id,
            ClearRemark = true,
        });

        Assert.NotNull(cleared);
        Assert.Null(cleared.Remark);
        Assert.Equal("Renamed", cleared.Name);

        Assert.Null(await store.UpdateAsync(new ModelUpdate { Id = 404, Name = "x" }));
    }

    [Fact]
    public async Task AggregateUpdatesAndSoftDeletesTouchOnlyTheRequestedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ModelStore(context);

        var first = await SeedAsync(store, "openai", "gpt-4o", "Alpha", "enabled");
        var second = await SeedAsync(store, "openai", "gpt-4o-mini", "Beta", "disabled");
        var third = await SeedAsync(store, "anthropic", "claude", "Gamma", "enabled");

        Assert.Equal(0, await store.BulkUpdateStatusAsync([], "archived"));
        Assert.Equal(2, await store.BulkUpdateStatusAsync([first, second], "archived"));

        var archived = await store.GetByIdAsync(first);
        Assert.NotNull(archived);
        Assert.Equal("archived", archived.Status);
        Assert.Equal(0, archived.DeletedAt);

        var untouched = await store.GetByIdAsync(third);
        Assert.NotNull(untouched);
        Assert.Equal("enabled", untouched.Status);

        Assert.Equal(1, await store.DeleteAsync(second));
        Assert.Equal(0, await store.DeleteAsync(second));
        Assert.Null(await store.GetByIdAsync(second));

        Assert.Equal(2, await store.BulkDeleteAsync([first, third]));
        Assert.Equal(0, await store.BulkDeleteAsync([first, third]));
        Assert.Empty(await store.ListAllAsync(new ModelListFilter()));
    }

    private static async Task<long> SeedAsync(
        ModelStore store,
        string developer,
        string modelId,
        string name,
        string status,
        string type = "chat",
        string? remark = null)
    {
        var model = NewModel(modelId);
        model.Developer = developer;
        model.Name = name;
        model.Status = status;
        model.Type = type;
        model.Remark = remark;
        return await store.CreateAsync(model);
    }

    private static ModelRecord NewModel(string modelId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Developer = "openai",
        ModelId = modelId,
        Type = "chat",
        Name = modelId,
        Icon = "openai",
        Group = "default",
        ModelCard = DefaultModelCardJson,
        Settings = DefaultSettingsJson,
        Status = "disabled",
        Remark = null,
    };

    private static async Task SoftDeleteAsync(TestDatabase db, long id)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE \"models\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
            new { DeletedAt = 1_700_000_000L, Id = id });
    }
}
