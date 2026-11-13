using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the channel_override_templates store against the reference ent
/// behaviour: the create builder writes JSON null for unpublished operation
/// lists, updates reproduce the sqljson append CASE, deletes are soft, the
/// (user_id, name, deleted_at) index surfaces as a name conflict, and the
/// apply/clear settings writes run in one transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelOverrideTemplateStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateStoresExactJsonAndReadsTheRowBack()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var template = NewTemplate("Headers", userId: 7);
        template.HeaderOverrideOperations = """[{"op":"set","path":"x-key","value":"v"}]""";
        template.BodyOverrideOperations = "null";

        var created = await store.CreateAsync(template);

        Assert.True(created.Id > 0);
        var actual = await store.GetAsync(created.Id, scopeUserId: null);
        Assert.NotNull(actual);
        RecordAssert.Equivalent(created, actual);

        // Ent stores an explicitly set nil slice as jsonb 'null', not SQL NULL.
        await using var verify = db.Factory.CreateDbContext();
        var raw = await verify.Database.GetDbConnection().QuerySingleAsync<RawRow>(
            """
            SELECT "override_parameters" AS "OverrideParameters",
                   "override_headers" AS "OverrideHeaders",
                   "header_override_operations"::text AS "HeaderOps",
                   "body_override_operations"::text AS "BodyOps",
                   "user_id" AS "UserId"
            FROM "channel_override_templates" WHERE "id" = @Id;
            """,
            new { created.Id });
        Assert.Equal("{}", raw.OverrideParameters);
        Assert.Equal("[]", raw.OverrideHeaders);
        Assert.Equal("""[{"op": "set", "path": "x-key", "value": "v"}]""", raw.HeaderOps);
        Assert.Equal("null", raw.BodyOps);
        Assert.Equal(7, raw.UserId);

        // The soft-delete predicate hides rows from Get.
        Assert.Null(await store.GetAsync(created.Id, scopeUserId: 8));
        Assert.NotNull(await store.GetAsync(created.Id, scopeUserId: 7));
    }

    [Fact]
    public async Task UpdateAppliesDescriptionSetClearAndAppendPrecedence()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var template = NewTemplate("Merge", userId: 1);
        template.HeaderOverrideOperations = """[{"op":"set","path":"a","value":"1"}]""";
        template.BodyOverrideOperations = "null";
        var created = await store.CreateAsync(template);

        var updated = await store.UpdateAsync(new ChannelOverrideTemplateUpdate
        {
            Id = created.Id,
            Name = "Renamed",
            Description = "desc",
            // A simultaneous set and append: the sqljson modifier replaces the
            // Set assignment, so the append is applied to the stored array and
            // the set value is ignored.
            HeaderOverrideOperations = """[{"op":"set","path":"ignored","value":"x"}]""",
            AppendHeaderOverrideOperations = """[{"op":"delete","path":"b"}]""",
            // Appending to a JSON null column replaces it with the appended array.
            AppendBodyOverrideOperations = """[{"op":"set","path":"c","value":"3"}]""",
            ScopeUserId = 1,
        });

        Assert.NotNull(updated);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("desc", updated.Description);
        Assert.Equal(
            """[{"op": "set", "path": "a", "value": "1"}, {"op": "delete", "path": "b"}]""",
            updated.HeaderOverrideOperations);
        Assert.Equal("""[{"op": "set", "path": "c", "value": "3"}]""", updated.BodyOverrideOperations);
        Assert.True(updated.UpdatedAt > UpdatedAt);

        var cleared = await store.UpdateAsync(new ChannelOverrideTemplateUpdate
        {
            Id = created.Id,
            ClearDescription = true,
            Description = "ignored",
            AppendHeaderOverrideOperations = """[{"op":"set","path":"d","value":"4"}]""",
        });
        Assert.NotNull(cleared);
        Assert.Null(cleared.Description);
        Assert.Equal(
            """[{"op": "set", "path": "a", "value": "1"}, {"op": "delete", "path": "b"}, {"op": "set", "path": "d", "value": "4"}]""",
            cleared.HeaderOverrideOperations);

        Assert.Null(await store.UpdateAsync(new ChannelOverrideTemplateUpdate
        {
            Id = 424242,
            Name = "missing",
        }));
    }

    [Fact]
    public async Task DuplicateNamesSurfaceAsConflictsPerUserAndRespectSoftDelete()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var first = NewTemplate("Shared", userId: 1);
        await store.CreateAsync(first);

        // Another user may reuse the name.
        var other = NewTemplate("Shared", userId: 2);
        await store.CreateAsync(other);

        await Assert.ThrowsAsync<ChannelOverrideTemplateNameConflictException>(
            () => store.CreateAsync(NewTemplate("Shared", userId: 1)));

        // A rename onto the same user's name conflicts too.
        var third = NewTemplate("Third", userId: 1);
        await store.CreateAsync(third);
        await Assert.ThrowsAsync<ChannelOverrideTemplateNameConflictException>(
            () => store.UpdateAsync(new ChannelOverrideTemplateUpdate
            {
                Id = third.Id,
                Name = "Shared",
                ScopeUserId = 1,
            }));

        // The unique index includes deleted_at, so a soft-deleted name is free.
        Assert.True(await store.DeleteAsync(first.Id, scopeUserId: null));
        var reused = NewTemplate("Shared", userId: 1);
        await store.CreateAsync(reused);
        Assert.NotEqual(first.Id, reused.Id);
    }

    [Fact]
    public async Task DeleteSoftDeletesAndHidesTheRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var template = NewTemplate("Delete", userId: 1);
        var created = await store.CreateAsync(template);

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(await store.DeleteAsync(created.Id, scopeUserId: 1));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.ChannelOverrideTemplates
            .SingleAsync(row => row.Id == created.Id);
        Assert.InRange(reloaded.DeletedAt, before, after);
        Assert.True(reloaded.UpdatedAt > UpdatedAt);

        Assert.Null(await store.GetAsync(created.Id, scopeUserId: null));
        Assert.False(await store.DeleteAsync(created.Id, scopeUserId: null));
        Assert.False(await store.DeleteAsync(created.Id, scopeUserId: 2));
    }

    [Fact]
    public async Task ListAppliesOwnerScopeFiltersAndKeysetPagination()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var alpha = NewTemplate("Alpha", userId: 1);
        alpha.UpdatedAt = UpdatedAt;
        await store.CreateAsync(alpha);
        var beta = NewTemplate("beta", userId: 1);
        beta.UpdatedAt = UpdatedAt.AddMinutes(1);
        await store.CreateAsync(beta);
        var other = NewTemplate("Alpha", userId: 2);
        other.UpdatedAt = UpdatedAt.AddMinutes(2);
        await store.CreateAsync(other);

        var scoped = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            First = 10,
        });
        Assert.Equal(Expected("Alpha", "beta"), scoped.Nodes.Select(row => row.Name));
        Assert.Equal(2, scoped.TotalCount);
        Assert.False(scoped.HasNextPage);

        var page = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            SortField = ChannelOverrideTemplateSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            First = 1,
        });
        Assert.Equal(Expected("beta"), page.Nodes.Select(row => row.Name));
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);

        var next = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            SortField = ChannelOverrideTemplateSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(page.Nodes[0].Id, page.Nodes[0].UpdatedAt),
            First = 1,
        });
        Assert.Equal(Expected("Alpha"), next.Nodes.Select(row => row.Name));
        Assert.True(next.HasPreviousPage);

        var folded = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            Where = new ChannelOverrideTemplateListFilter { NameContainsFold = "ALPH" },
        });
        Assert.Equal(Expected("Alpha"), folded.Nodes.Select(row => row.Name));

        var caseSensitive = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            Where = new ChannelOverrideTemplateListFilter { NameContains = "ALPH" },
        });
        Assert.Empty(caseSensitive.Nodes);

        var disjunction = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            Where = new ChannelOverrideTemplateListFilter
            {
                Or =
                [
                    new ChannelOverrideTemplateListFilter { Name = "Alpha" },
                    new ChannelOverrideTemplateListFilter { Name = "beta" },
                ],
            },
        });
        Assert.Equal(Expected("Alpha", "beta"), disjunction.Nodes.Select(row => row.Name));

        // entgql only installs In when the list is non-empty, so an empty
        // list is ignored.
        var none = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            Where = new ChannelOverrideTemplateListFilter { NameIn = [] },
        });
        Assert.Equal(Expected("Alpha", "beta"), none.Nodes.Select(row => row.Name));
        Assert.Equal(2, none.TotalCount);
    }

    [Fact]
    public async Task ListAppliesEveryScalarAndUserEdgePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        // Users 101 (live) and 102 (soft-deleted); the generated edge
        // predicates do not re-apply the User soft delete.
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO "users" ("id", "created_at", "updated_at", "email", "status", "password")
                VALUES (101, @CreatedAt, @UpdatedAt, 'alice@example.com', 'activated', 'x'),
                       (102, @CreatedAt, @UpdatedAt, 'bob@example.com', 'activated', 'x');
                UPDATE "users" SET "deleted_at" = 99 WHERE "id" = 102;
                """,
                new { CreatedAt, UpdatedAt });
        }

        var alpha = NewTemplate("Alpha", userId: 101);
        alpha.Description = "First";
        alpha.OverrideParameters = """{"a":1}""";
        alpha.CreatedAt = CreatedAt;
        alpha.UpdatedAt = CreatedAt.AddHours(1);
        var alphaId = (await store.CreateAsync(alpha)).Id;

        var beta = NewTemplate("beta", userId: 102);
        beta.Description = null;
        beta.OverrideParameters = """{"b":2}""";
        beta.CreatedAt = CreatedAt.AddMinutes(1);
        beta.UpdatedAt = CreatedAt.AddHours(2);
        var betaId = (await store.CreateAsync(beta)).Id;

        var gamma = NewTemplate("Gamma", userId: null);
        gamma.Description = "Third";
        gamma.OverrideParameters = """{"c":3}""";
        gamma.CreatedAt = CreatedAt.AddMinutes(2);
        gamma.UpdatedAt = CreatedAt.AddHours(3);
        await store.CreateAsync(gamma);

        async Task<string[]> Names(ChannelOverrideTemplateListFilter where)
        {
            var page = await store.ListAsync(new ChannelOverrideTemplateListQuery
            {
                Where = where,
                First = 10,
            });
            return page.Nodes.Select(static row => row.Name).ToArray();
        }

        Assert.Equal(["Alpha"], await Names(new ChannelOverrideTemplateListFilter { Id = alphaId }));
        Assert.Equal(
            ["beta", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { IdNEQ = alphaId }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { IdGT = betaId }));
        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter { IdLTE = betaId }));
        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter { IdIn = [alphaId, betaId] }));

        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { CreatedAt = CreatedAt }));
        Assert.Equal(
            ["beta", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { CreatedAtGT = CreatedAt }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                CreatedAtLT = CreatedAt.AddMinutes(1),
            }));
        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                CreatedAtIn = [CreatedAt, CreatedAt.AddMinutes(1)],
            }));

        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                UpdatedAt = CreatedAt.AddHours(2),
            }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                UpdatedAtNotIn = [CreatedAt.AddHours(2)],
            }));

        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { UserId = 101 }));
        // "user_id <> 101" excludes the NULL row, like SQL and ent.
        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter { UserIdNEQ = 101 }));
        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter { UserIdIn = [101, 102] }));
        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter { UserIdNotIn = [101] }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { UserIdIsNil = true }));
        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter { UserIdNotNil = true }));
        // Ent reads these as plain bools: false is ignored, not inverted.
        Assert.Equal(
            ["Alpha", "beta", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { UserIdIsNil = false }));

        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { Name = "Alpha" }));
        Assert.Equal(
            ["beta", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { NameGT = "Alpha" }));
        // Byte order: "Alpha" < "Gamma" < "beta".
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { NameLTE = "Gamma" }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { NameContains = "amm" }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { NameHasPrefix = "Al" }));
        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter { NameHasSuffix = "ta" }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { NameEqualFold = "ALPHA" }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { NameContainsFold = "AMM" }));

        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { Description = "First" }));
        // "description <> 'First'" excludes the NULL row.
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionNEQ = "First" }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                DescriptionIn = ["First", "Third"],
            }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionGT = "First" }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionLT = "Third" }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionHasSuffix = "ird" }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionEqualFold = "FIRST" }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionContainsFold = "THIRD" }));
        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionIsNil = true }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { DescriptionNotNil = true }));

        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParameters = """{"a":1}""",
            }));
        Assert.Equal(
            ["beta", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersNEQ = """{"a":1}""",
            }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersIn = ["""{"a":1}""", """{"c":3}"""],
            }));
        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersContains = "\"b\"",
            }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersHasPrefix = """{"a""",
            }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersHasSuffix = "3}",
            }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersEqualFold = """{"A":1}""",
            }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                OverrideParametersContainsFold = """{"C""",
            }));

        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter { HasUser = true }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { HasUser = false }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                HasUserWith = [new UserListFilter { Email = "alice@example.com" }],
            }));
        // The soft-deleted user still matches: sqlgraph does not re-apply the
        // User soft-delete predicate inside HasNeighborsWith.
        Assert.Equal(
            ["beta"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                HasUserWith = [new UserListFilter { Email = "bob@example.com" }],
            }));
        Assert.Equal(
            ["Alpha", "beta", "Gamma"],
            await Names(new ChannelOverrideTemplateListFilter { HasUserWith = [] }));

        Assert.Equal(
            ["Gamma"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                Not = new ChannelOverrideTemplateListFilter { HasUser = true },
            }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                And =
                [
                    new ChannelOverrideTemplateListFilter { HasUser = true },
                    new ChannelOverrideTemplateListFilter { NameContainsFold = "ALP" },
                ],
            }));
        Assert.Equal(
            ["Alpha", "beta"],
            await Names(new ChannelOverrideTemplateListFilter
            {
                Or =
                [
                    new ChannelOverrideTemplateListFilter { Name = "Alpha" },
                    new ChannelOverrideTemplateListFilter { Name = "beta" },
                ],
            }));
        Assert.Empty(await Names(new ChannelOverrideTemplateListFilter { Or = [] }));
    }

    [Fact]
    public async Task ListChannelsIgnoresMissingAndDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var live = await SeedChannelAsync(context, "Live");
        var deleted = await SeedChannelAsync(context, "Deleted", deletedAt: 99);
        var later = await SeedChannelAsync(context, "Later");

        // The reference emits no ORDER BY; the port pins id ascending (C163).
        var found = await store.ListChannelsAsync([later.Id, live.Id, deleted.Id, 424242]);
        Assert.Equal(["Live", "Later"], found.Select(row => row.Name));

        // ent's IDIn with no values matches nothing.
        Assert.Empty(await store.ListChannelsAsync([]));
    }

    [Fact]
    public async Task UpdateChannelSettingsCommitsOrRollsBackTheWholeBatch()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelOverrideTemplateStore(context);

        var alpha = await SeedChannelAsync(context, "Alpha", settings: """{"modelMappings":[]}""");
        var beta = await SeedChannelAsync(context, "Beta", settings: """{"modelMappings":[]}""");

        var updated = await store.UpdateChannelSettingsAsync(
        [
            new ChannelSettingsWrite(alpha.Id, alpha.Name, """{"modelMappings":[]}"""),
            new ChannelSettingsWrite(beta.Id, beta.Name, """{"modelMappings":[]}"""),
        ]);

        Assert.Equal(Expected("Alpha", "Beta"), updated.Select(row => row.Name));
        Assert.Equal("""{"modelMappings": []}""", updated[0].Settings);
        Assert.True(updated[0].UpdatedAt > UpdatedAt);

        var failure = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateChannelSettingsAsync(
            [
                new ChannelSettingsWrite(alpha.Id, alpha.Name, """{"modelMappings":[{"from":"a","to":"b"}]}"""),
                new ChannelSettingsWrite(424242, "Ghost", "{}"),
            ]));
        Assert.Equal("failed to update channel Ghost: ent: channel not found", failure.Message);

        // The first write was rolled back: the reference runs both in one
        // transaction and rethrows before commit.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal("""{"modelMappings": []}""", reloaded.Settings);
    }

    private static ChannelOverrideTemplateRecord NewTemplate(string name, long? userId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = 0,
        Name = name,
        OverrideParameters = "{}",
        OverrideHeaders = "[]",
        HeaderOverrideOperations = "null",
        BodyOverrideOperations = "null",
        UserId = userId,
    };

    private static IEnumerable<string> Expected(params string[] names)
        => names.Order(StringComparer.Ordinal);

    private static async Task<ChannelRecord> SeedChannelAsync(
        AxonHubDbContext context,
        string name,
        long deletedAt = 0,
        string? settings = null)
    {
        var channel = new ChannelRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Type = "openai",
            Name = name,
            Credentials = "{}",
            DisabledApiKeys = "[]",
            SupportedModels = "null",
            AutoSyncModelPattern = string.Empty,
            Policies = """{"stream":"unlimited"}""",
            Settings = settings,
            Endpoints = "[]",
        };

        context.Channels.Add(channel);
        await context.SaveChangesAsync();
        return channel;
    }

    private sealed class RawRow
    {
        public string OverrideParameters { get; init; } = string.Empty;

        public string OverrideHeaders { get; init; } = string.Empty;

        public string HeaderOps { get; init; } = string.Empty;

        public string BodyOps { get; init; } = string.Empty;

        public long? UserId { get; init; }
    }
}
