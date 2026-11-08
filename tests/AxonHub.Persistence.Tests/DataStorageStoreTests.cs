using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The data_storages table as the reference's Ent client sees it
/// (internal/ent/schema/data_storage.go + migrate/schema.go:262-288): EF Core
/// writes, Dapper reads, the SoftDeleteMixin predicate, and the unique name
/// index that deliberately has no deleted_at column.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DataStorageStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 4, 5, 6, 7, 8, DateTimeKind.Utc);

    private const string S3Settings = """
        {
          "dsn": null,
          "directory": null,
          "s3": {
            "bucketName": "bucket",
            "endpoint": "https://s3.example.com",
            "region": "us-east-1",
            "accessKey": "ak",
            "secretKey": "sk",
            "pathStyle": true
          },
          "gcs": null,
          "webdav": null
        }
        """;

    private static DataStorageRecord NewStorage(
        string name = "storage",
        string type = "s3",
        string status = "active",
        string settings = S3Settings)
        => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = name,
            Description = "a storage",
            Primary = false,
            Type = type,
            Settings = settings,
            Status = status,
        };

    [Fact]
    public async Task CreateAndGetRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var storage = NewStorage();
        var id = await store.CreateAsync(storage);

        Assert.True(id > 0);

        storage.Id = id;
        var row = await store.GetByIdAsync(id);
        Assert.NotNull(row);
        RecordAssert.Equivalent(storage, row);
        Assert.Equal(0, row.DeletedAt);
        Assert.Null(await store.GetByIdAsync(id + 1));
    }

    [Fact]
    public async Task SoftDeletedRowsStayHiddenButReserveTheirName()
    {
        // Ent's data_sources_by_name index covers "name" only, so unlike the
        // prompts index a soft-deleted row keeps the name reserved at the
        // database level even though every ent query filters it out.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var storage = NewStorage(name: "reserved");
        var id = await store.CreateAsync(storage);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"data_storages\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Null(await store.GetByIdAsync(id));
        Assert.False(await store.ExistsByNameAsync("reserved", excludeId: null));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewStorage(name: "reserved")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        // The failed insert was detached, so the same context can still write.
        await store.CreateAsync(NewStorage(name: "available"));
        Assert.True(await store.ExistsByNameAsync("available", excludeId: null));
    }

    [Fact]
    public async Task PrimaryLookupIgnoresSoftDeletedRowsAndKeepsIdOrder()
    {
        // ent's First() appends deleted_at = 0 and orders by id ASC.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var first = NewStorage(name: "primary-a");
        first.Primary = true;
        var firstId = await store.CreateAsync(first);

        var second = NewStorage(name: "primary-b");
        second.Primary = true;
        var secondId = await store.CreateAsync(second);

        var primary = await store.GetPrimaryAsync();
        Assert.NotNull(primary);
        Assert.Equal(firstId, primary.Id);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"data_storages\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" IN (@First, @Second);",
                new { DeletedAt = 1_700_000_000L, First = firstId, Second = secondId });
        }

        Assert.Null(await store.GetPrimaryAsync());

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"data_storages\" SET \"deleted_at\" = 0 WHERE \"id\" = @Second;",
                new { Second = secondId });
        }

        primary = await store.GetPrimaryAsync();
        Assert.NotNull(primary);
        Assert.Equal(secondId, primary.Id);
    }

    [Fact]
    public async Task ExistsByNameExcludesTheGivenId()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var storage = NewStorage(name: "shared");
        var id = await store.CreateAsync(storage);

        Assert.True(await store.ExistsByNameAsync("shared", excludeId: null));
        Assert.False(await store.ExistsByNameAsync("shared", excludeId: id));
        Assert.False(await store.ExistsByNameAsync("other", excludeId: null));
    }

    [Fact]
    public async Task ListAppliesNameFoldTypeAndStatusFilters()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var gamma = NewStorage(name: "Gamma", type: "s3");
        await store.CreateAsync(gamma);
        await store.CreateAsync(NewStorage(name: "delta", type: "fs"));
        await store.CreateAsync(NewStorage(name: "GAMMA-2", type: "s3", status: "archived"));

        // containsFold lowers both sides: "ga" matches Gamma and GAMMA-2.
        var page = await store.ListAsync(new DataStorageListQuery
        {
            Where = new DataStorageListFilter
            {
                NameContainsFold = "ga",
                TypeIn = ["s3"],
                StatusIn = ["active"],
            },
        });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(gamma.Id, Assert.Single(page.Nodes).Id);

        // An empty *In list installs no predicate (ent len() > 0 gates).
        var all = await store.ListAsync(new DataStorageListQuery
        {
            Where = new DataStorageListFilter { TypeIn = [], StatusNotIn = [] },
        });
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task ListPaginatesWithTheIdKeyset()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var first = NewStorage(name: "first");
        await store.CreateAsync(first);
        var second = NewStorage(name: "second");
        await store.CreateAsync(second);
        await store.CreateAsync(NewStorage(name: "third"));

        var page = await store.ListAsync(new DataStorageListQuery { First = 2 });

        Assert.Equal(3, page.TotalCount);
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal([first.Id, second.Id], page.Nodes.Select(static n => n.Id).ToArray());

        var next = await store.ListAsync(new DataStorageListQuery
        {
            After = new ListCursor(second.Id, null),
            First = 2,
        });

        Assert.Single(next.Nodes);
        Assert.False(next.HasNextPage);
        Assert.True(next.HasPreviousPage);
    }

    [Fact]
    public async Task ListOrdersByUpdatedAtWithANonNullCursorValue()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var older = NewStorage(name: "older");
        await store.CreateAsync(older);
        var newer = NewStorage(name: "newer");
        newer.UpdatedAt = UpdatedAt.AddDays(1);
        await store.CreateAsync(newer);

        var page = await store.ListAsync(new DataStorageListQuery
        {
            SortField = DataStorageSortField.UpdatedAt,
            Direction = SortDirection.Desc,
        });

        Assert.Equal(
            ["newer", "older"],
            page.Nodes.Select(static node => node.Name).ToArray());

        var next = await store.ListAsync(new DataStorageListQuery
        {
            SortField = DataStorageSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(newer.Id, newer.UpdatedAt),
        });

        Assert.Equal(older.Id, Assert.Single(next.Nodes).Id);
    }

    [Fact]
    public async Task NestedEmptyPredicatesThrowTheEntMessages()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        // The top-level empty input is swallowed by ent's Filter.
        Assert.Equal(
            0,
            (await store.ListAsync(new DataStorageListQuery
            {
                Where = new DataStorageListFilter(),
            })).TotalCount);

        var not = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ListAsync(new DataStorageListQuery
            {
                Where = new DataStorageListFilter { Not = new DataStorageListFilter() },
            }));
        Assert.Equal("ent: empty predicate DataStorageWhereInput: field 'not'", not.Message);

        var and = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ListAsync(new DataStorageListQuery
            {
                Where = new DataStorageListFilter { And = [new DataStorageListFilter()] },
            }));
        Assert.Equal("ent: empty predicate DataStorageWhereInput: field 'and'", and.Message);

        var or = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ListAsync(new DataStorageListQuery
            {
                Where = new DataStorageListFilter { Or = [new DataStorageListFilter()] },
            }));
        Assert.Equal("ent: empty predicate DataStorageWhereInput: field 'or'", or.Message);
    }

    [Fact]
    public async Task UpdateWritesOnlyProvidedFieldsAndReturnsTheRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        var storage = NewStorage(name: "before");
        var id = await store.CreateAsync(storage);

        var updated = await store.UpdateAsync(new DataStorageUpdate
        {
            Id = id,
            Status = "archived",
            Settings = """{"dsn":null,"directory":null,"s3":null,"gcs":null,"webdav":null}""",
        });

        Assert.NotNull(updated);
        Assert.Equal("before", updated.Name);
        Assert.Equal("a storage", updated.Description);
        Assert.Equal("archived", updated.Status);
        RecordAssert.Json(
            """{"dsn":null,"directory":null,"s3":null,"gcs":null,"webdav":null}""",
            updated.Settings);
        Assert.True(updated.UpdatedAt > UpdatedAt);

        var renamed = await store.UpdateAsync(new DataStorageUpdate
        {
            Id = id,
            Name = "after",
            Description = "new description",
        });
        Assert.NotNull(renamed);
        Assert.Equal("after", renamed.Name);
        Assert.Equal("new description", renamed.Description);

        Assert.Null(await store.UpdateAsync(new DataStorageUpdate { Id = id + 1 }));
    }
}
