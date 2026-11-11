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

    [Fact]
    public async Task CreateIfAbsentInsertsOnlyWhenTheKeyIsMissing()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        await store.SetAsync("existing", "first");
        await store.CreateIfAbsentAsync("existing", "second");

        Assert.Equal("first", (await store.GetByKeyAsync("existing"))!.Value);

        await store.CreateIfAbsentAsync("fresh", "claim");
        Assert.Equal("claim", (await store.GetByKeyAsync("fresh"))!.Value);

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"systems\";"));
    }

    [Fact]
    public async Task CreateIfAbsentDoesNothingForASoftDeletedRow()
    {
        // The unique index covers key alone, so a soft-deleted marker still
        // makes ON CONFLICT DO NOTHING suppress the claim insert.
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

        await store.CreateIfAbsentAsync(system.Key, "claim");

        Assert.Null(await store.GetByKeyAsync(system.Key));

        await using var verify = await db.Factory.OpenAsync();
        Assert.Equal(1, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"systems\";"));
    }

    [Fact]
    public async Task RunInTransactionCommitsTheCallbackWrites()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        await store.RunInTransactionAsync(
            cancellationToken => store.SetAsync("committed", "1", cancellationToken));

        Assert.Equal("1", (await store.GetByKeyAsync("committed"))!.Value);
    }

    [Fact]
    public async Task RunInTransactionRollsBackTheCallbackWrites()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RunInTransactionAsync(async cancellationToken =>
            {
                await store.CreateIfAbsentAsync("claim", "mine", cancellationToken);
                await store.SetAsync("settings", "written", cancellationToken);
                throw new InvalidOperationException("boom");
            }));

        Assert.Null(await store.GetByKeyAsync("claim"));
        Assert.Null(await store.GetByKeyAsync("settings"));
    }

    [Fact]
    public async Task RunInTransactionJoinsAnAmbientTransaction()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            // The callback must not commit the ambient transaction.
            await store.RunInTransactionAsync(
                cancellationToken => store.SetAsync("joined", "1", cancellationToken));
            await transaction.RollbackAsync();
        }

        await using var verify = db.Factory.CreateDbContext();
        var verifyStore = new SystemStore(verify);
        Assert.Null(await verifyStore.GetByKeyAsync("joined"));
    }

    [Fact]
    public async Task ConcurrentClaimsElectExactlyOneOwner()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        async Task<(bool Owns, string Claim)> ClaimAsync()
        {
            await using var context = db.Factory.CreateDbContext();
            var store = new SystemStore(context);
            var claim = Guid.NewGuid().ToString();
            var owns = false;

            await store.RunInTransactionAsync(async cancellationToken =>
            {
                await store.CreateIfAbsentAsync(
                    "quota_routing_migration_done",
                    claim,
                    cancellationToken);
                var marker = await store.GetByKeyAsync(
                    "quota_routing_migration_done",
                    cancellationToken);
                owns = marker?.Value == claim;
            });

            return (owns, claim);
        }

        // Four concurrent starts, like the reference's real-Postgres test.
        var claims = Enumerable.Range(0, 4).Select(_ => ClaimAsync()).ToArray();
        var results = await Task.WhenAll(claims);

        // The unique index (key alone) plus the transaction make exactly one
        // start the migration; the losers read the committed claim and skip.
        var winner = Assert.Single(results.Where(result => result.Owns).ToArray());

        await using var verify = db.Factory.CreateDbContext();
        var marker = await new SystemStore(verify).GetByKeyAsync(
            "quota_routing_migration_done");
        Assert.Equal(winner.Claim, marker?.Value);
    }

    [Fact]
    public async Task UpdateValueIfUnchangedRequiresTheExactRowState()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        var id = await store.CreateAsync(system);
        var stored = (await store.GetByIdAsync(id))!;

        // A stale timestamp or a stale value loses the compare-and-set.
        Assert.Equal(
            0,
            await store.UpdateValueIfUnchangedAsync(
                id,
                stored.UpdatedAt.AddSeconds(1),
                stored.Value,
                "stale-timestamp"));
        Assert.Equal(
            0,
            await store.UpdateValueIfUnchangedAsync(
                id,
                stored.UpdatedAt,
                stored.Value + "x",
                "stale-value"));

        var value = """{"blocked_ips":[],"show_request_log_ip_ban_icon":true}""";
        Assert.Equal(
            1,
            await store.UpdateValueIfUnchangedAsync(
                id,
                stored.UpdatedAt,
                stored.Value,
                value));

        var updated = (await store.GetByIdAsync(id))!;
        Assert.Equal(value, updated.Value);
        // ent's UpdateDefault(xtime.UTCNow) advances updated_at.
        Assert.True(updated.UpdatedAt > stored.UpdatedAt);
    }

    [Fact]
    public async Task UpdateValueIfUnchangedIgnoresSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        var id = await store.CreateAsync(system);
        var stored = (await store.GetByIdAsync(id))!;

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"systems\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.Equal(
            0,
            await store.UpdateValueIfUnchangedAsync(
                id,
                stored.UpdatedAt,
                stored.Value,
                "resurrect"));
    }

    [Fact]
    public async Task TryCreateReportsAKeyConflictWithoutThrowing()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var system = NewSystem();
        Assert.True(await store.TryCreateAsync(system));

        // systems.key is unique on its own: a second live row conflicts.
        Assert.False(await store.TryCreateAsync(NewSystem()));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"systems\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = system.Id });
        }

        // A soft-deleted row still occupies the unique key.
        Assert.False(await store.TryCreateAsync(NewSystem()));

        await using var verify = await db.Factory.OpenAsync();
        Assert.Equal(1, await verify.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"systems\";"));
    }

    [Fact]
    public async Task ListPaginatesLiveRowsInIdOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var alpha = NewSystem("alpha", "1", CreatedAt, UpdatedAt);
        var beta = NewSystem("beta", "2", CreatedAt.AddMinutes(1), UpdatedAt.AddMinutes(1));
        var gamma = NewSystem("gamma", "3", CreatedAt.AddMinutes(2), UpdatedAt.AddMinutes(2));
        await store.CreateAsync(alpha);
        await store.CreateAsync(beta);
        await store.CreateAsync(gamma);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"systems\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = gamma.Id });
        }

        // The connection counts and pages over live rows only.
        var first = await store.ListAsync(new SystemListQuery { First = 1 });

        Assert.Equal(Expected("alpha"), first.Nodes.Select(row => row.Key));
        Assert.True(first.HasNextPage);
        Assert.False(first.HasPreviousPage);
        Assert.Equal(2, first.TotalCount);

        var second = await store.ListAsync(new SystemListQuery
        {
            After = new ListCursor(first.Nodes[0].Id, null),
            First = 1,
        });

        Assert.Equal(Expected("beta"), second.Nodes.Select(row => row.Key));
        Assert.False(second.HasNextPage);
        Assert.True(second.HasPreviousPage);

        // `last` flips the scan direction and build() restores the id order.
        var last = await store.ListAsync(new SystemListQuery { Last = 1 });

        Assert.Equal(Expected("beta"), last.Nodes.Select(row => row.Key));
        Assert.True(last.HasPreviousPage);
        Assert.False(last.HasNextPage);
    }

    [Fact]
    public async Task ListOrdersByEntgqlFieldsAndPushesFiltersDown()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new SystemStore(context);

        var security = NewSystem(
            "security_settings",
            """{"blocked_ips":[]}""",
            CreatedAt,
            UpdatedAt.AddMinutes(2));
        var retry = NewSystem(
            "retry_policy",
            """{"enabled":true}""",
            CreatedAt.AddMinutes(1),
            UpdatedAt.AddMinutes(1));
        var model = NewSystem(
            "model_settings",
            """{"default_model":"gpt"}""",
            CreatedAt.AddMinutes(2),
            UpdatedAt);
        await store.CreateAsync(security);
        await store.CreateAsync(retry);
        await store.CreateAsync(model);

        var newest = await store.ListAsync(new SystemListQuery
        {
            SortField = SystemSortField.UpdatedAt,
            Direction = SortDirection.Desc,
        });

        Assert.Equal(
            Expected("security_settings", "retry_policy", "model_settings"),
            newest.Nodes.Select(row => row.Key));

        // The cursor is the keyset (updated_at, id) pair of the last edge.
        var after = await store.ListAsync(new SystemListQuery
        {
            SortField = SystemSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(newest.Nodes[0].Id, newest.Nodes[0].UpdatedAt),
            First = 1,
        });

        Assert.Equal(Expected("retry_policy"), after.Nodes.Select(row => row.Key));

        var byCreated = await store.ListAsync(new SystemListQuery
        {
            SortField = SystemSortField.CreatedAt,
        });

        Assert.Equal(
            Expected("security_settings", "retry_policy", "model_settings"),
            byCreated.Nodes.Select(row => row.Key));

        var prefixed = await store.ListAsync(new SystemListQuery
        {
            Where = new SystemListFilter { KeyHasPrefix = "model_" },
        });

        Assert.Equal(Expected("model_settings"), prefixed.Nodes.Select(row => row.Key));
        Assert.Equal(1, prefixed.TotalCount);

        var folded = await store.ListAsync(new SystemListQuery
        {
            Where = new SystemListFilter { ValueContainsFold = "ENABLED" },
        });

        Assert.Equal(Expected("retry_policy"), folded.Nodes.Select(row => row.Key));

        var excluded = await store.ListAsync(new SystemListQuery
        {
            Where = new SystemListFilter
            {
                Not = new SystemListFilter
                {
                    KeyIn = ["security_settings", "model_settings"],
                },
            },
        });

        Assert.Equal(Expected("retry_policy"), excluded.Nodes.Select(row => row.Key));
    }

    private static T[] Expected<T>(params T[] values) => values;

    private static SystemRecord NewSystem() => NewSystem(
        "security_settings",
        """{"blocked_ips":["203.0.113.0/24"]}""",
        CreatedAt,
        UpdatedAt);

    private static SystemRecord NewSystem(
        string key,
        string value,
        DateTime createdAt,
        DateTime updatedAt) => new()
        {
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Key = key,
            Value = value,
        };
}
