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

        var none = await store.ListAsync(new ChannelOverrideTemplateListQuery
        {
            ScopeUserId = 1,
            Where = new ChannelOverrideTemplateListFilter { NameIn = [] },
        });
        Assert.Empty(none.Nodes);
        Assert.Equal(0, none.TotalCount);
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

    private static ChannelOverrideTemplateRecord NewTemplate(string name, long userId) => new()
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
