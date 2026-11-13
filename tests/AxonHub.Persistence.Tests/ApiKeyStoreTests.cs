using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ApiKeyStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey();
        apiKey.Key = "sk-ported-key";
        var id = await store.CreateAsync(apiKey);

        Assert.True(id > 0);

        apiKey.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(apiKey, actual);
    }

    [Fact]
    public async Task GetByKeyReturnsTheLiveRow()
    {
        // APIKeyService.GetAPIKey: APIKey.Query().Where(KeyEQ(key)).Only(),
        // which applies the soft-delete predicate.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey();
        apiKey.Key = "sk-ported-key";
        var id = await store.CreateAsync(apiKey);

        var actual = await store.GetByKeyAsync(apiKey.Key);

        Assert.NotNull(actual);
        Assert.Equal(id, actual.Id);
        Assert.Equal(1, actual.ProjectId);
    }

    [Fact]
    public async Task GetByKeyIgnoresSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey();
        var id = await store.CreateAsync(apiKey);

        await SoftDeleteAsync(db, id);

        Assert.Null(await store.GetByIdAsync(id));
        Assert.Null(await store.GetByKeyAsync(apiKey.Key));
    }

    [Fact]
    public async Task KeysAreUniqueAcrossAllRows()
    {
        // api_keys_by_key is unique on key alone (Ent's APIKeysTable), unlike the
        // soft-deleted-name indexes on channels/projects/users.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var duplicate = NewApiKey();
        duplicate.Key = "sk-unique-key";
        var firstId = await store.CreateAsync(duplicate);

        var sameKey = NewApiKey();
        sameKey.Key = "sk-unique-key";
        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(sameKey));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await SoftDeleteAsync(db, firstId);

        // Soft deleting an api key does not free its key value.
        var archivedDuplicate = NewApiKey();
        archivedDuplicate.Key = "sk-unique-key";
        var stillDuplicate = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(archivedDuplicate));
        var stillUnique = Assert.IsType<PostgresException>(stillDuplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, stillUnique.SqlState);
    }

    [Fact]
    public async Task ArchiveChangesStatusWithoutSoftDeletingAndListKeepsArchivedRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var firstId = await store.CreateAsync(NewApiKey("first", projectId: 1));
        var secondId = await store.CreateAsync(NewApiKey("second", projectId: 1));
        await store.CreateAsync(NewApiKey("other-project", projectId: 2));
        var archivedId = await store.CreateAsync(NewApiKey("archived", projectId: 1));

        // bulkArchiveAPIKeys: APIKey.Update().SetStatus("archived").Save() — an
        // aggregate update with no soft-delete predicate.
        Assert.Equal(
            1,
            await store.BulkUpdateStatusAsync([archivedId], "archived", projectWritePredicate: null));

        var archived = await store.GetByIdAsync(archivedId);
        Assert.NotNull(archived);
        Assert.Equal("archived", archived.Status);
        Assert.Equal(0, archived.DeletedAt);

        var page = await store.ListAsync(new ApiKeyListQuery
        {
            ProjectId = 1,
            SortField = ApiKeySortField.Id,
            Direction = SortDirection.Asc,
            First = 1,
        });

        Assert.Equal([firstId], page.Nodes.Select(static key => key.Id));
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(3, page.TotalCount);

        var next = await store.ListAsync(new ApiKeyListQuery
        {
            ProjectId = 1,
            SortField = ApiKeySortField.Id,
            Direction = SortDirection.Asc,
            After = new ListCursor(firstId, null),
            First = 1,
        });

        Assert.Equal([secondId], next.Nodes.Select(static key => key.Id));
        Assert.True(next.HasNextPage);
        Assert.True(next.HasPreviousPage);
    }

    [Fact]
    public async Task CountAndExistsApplyTheUniquenessAndKeyFilters()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var alpha = NewApiKey("shared", projectId: 1);
        alpha.Key = "sk-alpha-one";
        var alphaId = await store.CreateAsync(alpha);

        var beta = NewApiKey("shared", projectId: 2);
        beta.Key = "sk-beta-two";
        await store.CreateAsync(beta);

        var gamma = NewApiKey("other", projectId: 1);
        gamma.Key = "sk-beta-three";
        await store.CreateAsync(gamma);

        // The duplicate-name query CreateAPIKey runs after the insert:
        // NameEQ + ProjectIDEQ + (TypeNEQ(personal) OR UserIDEQ(current)).
        var duplicateFilter = new ApiKeyListFilter
        {
            Name = "shared",
            ProjectId = 1,
            Or =
            [
                new ApiKeyListFilter { TypeNotIn = ["personal"] },
                new ApiKeyListFilter { UserId = 42 },
            ],
        };

        Assert.Equal(1, await store.CountAsync(duplicateFilter, access: null));
        Assert.Equal(
            0,
            await store.CountAsync(
                new ApiKeyListFilter { Name = "shared", ProjectId = 3 },
                access: null));

        // Key / KeyContainsFold / NameContainsFold predicates.
        Assert.Equal(
            1,
            await store.CountAsync(new ApiKeyListFilter { Key = "sk-beta-three" }, access: null));
        Assert.Equal(
            2,
            await store.CountAsync(
                new ApiKeyListFilter { KeyContainsFold = "BETA" },
                access: null));
        Assert.Equal(
            2,
            await store.CountAsync(
                new ApiKeyListFilter { NameContainsFold = "SHAR" },
                access: null));
        Assert.True(
            await store.ExistsAsync(
                new ApiKeyListFilter { KeyContainsFold = "ALPHA" },
                access: null));
        Assert.False(
            await store.ExistsAsync(new ApiKeyListFilter { Key = "missing" }, access: null));

        // The soft-delete predicate applies unless includeDeleted is set.
        await SoftDeleteAsync(db, alphaId);
        Assert.Equal(0, await store.CountAsync(duplicateFilter, access: null));
        Assert.Equal(
            1,
            await store.CountAsync(duplicateFilter, access: null, includeDeleted: true));
        Assert.False(
            await store.ExistsAsync(new ApiKeyListFilter { Key = "sk-alpha-one" }, access: null));
    }

    [Fact]
    public async Task UpdateAppliesClearReplaceAppendAndKeyChanges()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var apiKey = NewApiKey("before");
        apiKey.Type = "service_account";
        apiKey.Scopes = """["read_channels"]""";
        apiKey.AllowedIps = """["127.0.0.1"]""";
        var id = await store.CreateAsync(apiKey);

        var updated = await store.UpdateAsync(new ApiKeyUpdate
        {
            Id = id,
            Key = "sk-rotated",
            Name = "after",
            Status = "disabled",
            Scopes = [],
            AppendScopes = ["write_requests"],
            Profiles = """{"activeProfile":"x","profiles":null}""",
            SetProfiles = true,
        });

        Assert.NotNull(updated);
        Assert.Equal("sk-rotated", updated.Key);
        Assert.Equal("after", updated.Name);
        Assert.Equal("disabled", updated.Status);

        // ent's json Append overwrites the plain Set on the same column and
        // concatenates onto the pre-update value: ["read_channels"] || ["write_requests"].
        Assert.Equal("""["read_channels", "write_requests"]""", updated.Scopes);
        Assert.Equal("""["127.0.0.1"]""", updated.AllowedIps);

        using var profiles = JsonDocument.Parse(updated.Profiles!);
        Assert.Equal(
            "x",
            profiles.RootElement.GetProperty("activeProfile").GetString());

        // ClearAllowedIps emits SetNull; a later append rebuilds the column
        // through the JSON-null branch of the CASE expression.
        var cleared = await store.UpdateAsync(new ApiKeyUpdate
        {
            Id = id,
            ClearAllowedIps = true,
        });
        Assert.NotNull(cleared);
        Assert.Null(cleared.AllowedIps);

        var appended = await store.UpdateAsync(new ApiKeyUpdate
        {
            Id = id,
            AppendAllowedIps = ["10.0.0.0/8"],
        });
        Assert.NotNull(appended);
        Assert.Equal("""["10.0.0.0/8"]""", appended.AllowedIps);

        // UpdateOneID re-reads the full row, so a later read sees the same values.
        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("sk-rotated", actual.Key);
        Assert.Equal("""["read_channels", "write_requests"]""", actual.Scopes);
    }

    [Fact]
    public async Task UpdateWithoutMatchingProjectPredicateReturnsNull()
    {
        // ent's WhereP mutation policy (UserProjectScopeWriteRule) renders a
        // project_id predicate on the update; a mismatch is a NotFound.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var id = await store.CreateAsync(NewApiKey("guarded", projectId: 1));

        var updated = await store.UpdateAsync(new ApiKeyUpdate
        {
            Id = id,
            Name = "renamed",
            ProjectWritePredicate = 2,
        });

        Assert.Null(updated);
        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("guarded", actual.Name);
    }

    [Fact]
    public async Task BulkUpdateStatusOnlyTouchesRequestedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var archivedId = await store.CreateAsync(NewApiKey("archive", projectId: 1));
        var untouchedId = await store.CreateAsync(NewApiKey("keep", projectId: 2));

        Assert.Equal(
            0,
            await store.BulkUpdateStatusAsync([], "archived", projectWritePredicate: null));

        // The project write predicate restricts the aggregate update to the
        // context project (project-scoped writer).
        Assert.Equal(
            1,
            await store.BulkUpdateStatusAsync(
                [archivedId, untouchedId],
                "archived",
                projectWritePredicate: 1));

        var archived = await store.GetByIdAsync(archivedId);
        Assert.NotNull(archived);
        Assert.Equal("archived", archived.Status);
        Assert.Equal(0, archived.DeletedAt);

        var untouched = await store.GetByIdAsync(untouchedId);
        Assert.NotNull(untouched);
        Assert.Equal("enabled", untouched.Status);
    }

    [Fact]
    public async Task ListAppliesEveryScalarPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ApiKeyStore(context);

        var alpha = NewApiKey("Alpha", projectId: 1);
        alpha.Key = "sk-alpha";
        alpha.UserId = 1;
        alpha.CreatedAt = CreatedAt;
        alpha.UpdatedAt = CreatedAt.AddHours(1);
        var alphaId = await store.CreateAsync(alpha);

        var beta = NewApiKey("Beta", projectId: 2);
        beta.Key = "sk-beta";
        beta.Type = "service_account";
        beta.Status = "disabled";
        beta.UserId = 2;
        beta.CreatedAt = CreatedAt.AddMinutes(1);
        beta.UpdatedAt = CreatedAt.AddHours(2);
        var betaId = await store.CreateAsync(beta);

        var gamma = NewApiKey("Gamma", projectId: 1);
        gamma.Key = "sk-gamma";
        gamma.Type = "noauth";
        gamma.Status = "archived";
        gamma.UserId = null;
        gamma.CreatedAt = CreatedAt.AddMinutes(2);
        gamma.UpdatedAt = CreatedAt.AddHours(3);
        var gammaId = await store.CreateAsync(gamma);

        async Task<string[]> Names(ApiKeyListFilter where)
        {
            var page = await store.ListAsync(new ApiKeyListQuery { Where = where, First = 10 });
            return page.Nodes.Select(static row => row.Name).ToArray();
        }

        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { Id = alphaId }));
        Assert.Equal(["Beta", "Gamma"], await Names(new ApiKeyListFilter { IdNEQ = alphaId }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { IdIn = [alphaId, gammaId] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { IdNotIn = [alphaId] }));
        Assert.Equal(["Beta", "Gamma"], await Names(new ApiKeyListFilter { IdGT = alphaId }));
        Assert.Equal(["Beta", "Gamma"], await Names(new ApiKeyListFilter { IdGTE = betaId }));
        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { IdLT = betaId }));
        Assert.Equal(["Alpha", "Beta"], await Names(new ApiKeyListFilter { IdLTE = betaId }));

        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { CreatedAt = CreatedAt }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { CreatedAtNEQ = CreatedAt }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter
            {
                CreatedAtIn = [CreatedAt, CreatedAt.AddMinutes(2)],
            }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { CreatedAtNotIn = [CreatedAt] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { CreatedAtGT = CreatedAt }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { CreatedAtGTE = CreatedAt.AddMinutes(1) }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ApiKeyListFilter { CreatedAtLT = CreatedAt.AddMinutes(1) }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter { CreatedAtLTE = CreatedAt.AddMinutes(1) }));

        Assert.Equal(
            ["Beta"],
            await Names(new ApiKeyListFilter { UpdatedAt = CreatedAt.AddHours(2) }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { UpdatedAtNEQ = CreatedAt.AddHours(2) }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter
            {
                UpdatedAtIn = [CreatedAt.AddHours(1), CreatedAt.AddHours(3)],
            }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { UpdatedAtNotIn = [CreatedAt.AddHours(1)] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { UpdatedAtGT = CreatedAt.AddHours(1) }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { UpdatedAtGTE = CreatedAt.AddHours(2) }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ApiKeyListFilter { UpdatedAtLT = CreatedAt.AddHours(2) }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter { UpdatedAtLTE = CreatedAt.AddHours(2) }));

        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { UserId = 1 }));
        // "user_id <> 1" excludes the NULL row, like SQL and ent.
        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { UserIdNEQ = 1 }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter { UserIdIn = [1, 2] }));
        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { UserIdNotIn = [1] }));
        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { UserIdIsNil = true }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter { UserIdNotNil = true }));
        // Ent reads these as plain bools: false is ignored, not inverted.
        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            await Names(new ApiKeyListFilter { UserIdIsNil = false }));

        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { ProjectId = 1 }));
        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { ProjectIdNEQ = 1 }));
        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { ProjectIdIn = [2, 3] }));
        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { ProjectIdNotIn = [1] }));

        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { Key = "sk-beta" }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { KeyNEQ = "sk-beta" }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { KeyIn = ["sk-alpha", "sk-gamma"] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { KeyNotIn = ["sk-alpha"] }));
        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { KeyGT = "sk-beta" }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { KeyGTE = "sk-beta" }));
        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { KeyLT = "sk-beta" }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter { KeyLTE = "sk-beta" }));
        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { KeyContains = "gamma" }));
        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { KeyHasPrefix = "sk-a" }));
        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { KeyHasSuffix = "ma" }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ApiKeyListFilter { KeyEqualFold = "SK-ALPHA" }));
        Assert.Equal(
            ["Beta"],
            await Names(new ApiKeyListFilter { KeyContainsFold = "BETA" }));

        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { Name = "Alpha" }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { NameNEQ = "Alpha" }));
        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { NameIn = ["Alpha", "Gamma"] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { NameNotIn = ["Alpha"] }));
        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { NameGT = "Beta" }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { NameGTE = "Beta" }));
        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { NameLT = "Beta" }));
        Assert.Equal(["Alpha", "Beta"], await Names(new ApiKeyListFilter { NameLTE = "Beta" }));
        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { NameContains = "amm" }));
        Assert.Equal(["Alpha"], await Names(new ApiKeyListFilter { NameHasPrefix = "Al" }));
        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { NameHasSuffix = "ta" }));
        Assert.Equal(
            ["Alpha"],
            await Names(new ApiKeyListFilter { NameEqualFold = "ALPHA" }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ApiKeyListFilter { NameContainsFold = "AMM" }));

        Assert.Equal(["Gamma"], await Names(new ApiKeyListFilter { Type = "noauth" }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { TypeNEQ = "user" }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter { TypeIn = ["user", "service_account"] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { TypeNotIn = ["user"] }));

        Assert.Equal(["Beta"], await Names(new ApiKeyListFilter { Status = "disabled" }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { StatusNEQ = "enabled" }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { StatusIn = ["disabled", "archived"] }));
        Assert.Equal(
            ["Beta", "Gamma"],
            await Names(new ApiKeyListFilter { StatusNotIn = ["enabled"] }));
        Assert.Empty(await Names(new ApiKeyListFilter { StatusIn = [] }));
        Assert.Equal(
            ["Alpha", "Beta", "Gamma"],
            await Names(new ApiKeyListFilter { StatusNotIn = [] }));

        Assert.Equal(
            ["Alpha", "Gamma"],
            await Names(new ApiKeyListFilter { Not = new() { Status = "disabled" } }));
        Assert.Equal(
            ["Gamma"],
            await Names(new ApiKeyListFilter
            {
                And =
                [
                    new ApiKeyListFilter { Type = "noauth" },
                    new ApiKeyListFilter { NameContainsFold = "GAM" },
                ],
            }));
        Assert.Equal(
            ["Alpha", "Beta"],
            await Names(new ApiKeyListFilter
            {
                Or =
                [
                    new ApiKeyListFilter { Name = "Alpha" },
                    new ApiKeyListFilter { Name = "Beta" },
                ],
            }));
        Assert.Empty(await Names(new ApiKeyListFilter { Or = [] }));
    }

    private static async Task SoftDeleteAsync(TestDatabase db, long id)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE \"api_keys\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
            new { DeletedAt = 1_700_000_000L, Id = id });
    }

    private static ApiKeyRecord NewApiKey(string name = "default", long projectId = 1) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Key = $"sk-ported-key-{Guid.NewGuid():N}",
        Name = name,
        Type = "user",
        Status = "enabled",
        Scopes = """["write_requests","read_channels"]""",
        Profiles = """{"activeProfile":"","profiles":null}""",
        AllowedIps = """["127.0.0.1","10.0.0.0/8"]""",
        ProjectId = projectId,
        UserId = null,
    };
}
