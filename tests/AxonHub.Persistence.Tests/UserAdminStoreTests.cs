using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UserAdminStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateWithRolesAndGetById()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var roleIds = await SeedRolesAsync(context, 2);
        var user = NewUser("admin@example.com");

        var id = await store.CreateAsync(user, [roleIds[0], roleIds[0], roleIds[1]]);

        user.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(user, actual);

        var roles = await context.UserRoles
            .Where(row => row.UserId == id)
            .Select(row => row.RoleId)
            .ToListAsync();
        Assert.Equal(Expected(roleIds[0], roleIds[1]), roles.Order());
    }

    [Fact]
    public async Task ListAppliesFiltersAndKeysetPagination()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var first = NewUser("alice@example.com");
        first.FirstName = "Alice";
        first.Status = "activated";
        await store.CreateAsync(first, null);

        var second = NewUser("bob@example.com");
        second.FirstName = "Bob";
        second.Status = "deactivated";
        await store.CreateAsync(second, null);

        var third = NewUser("carol@example.com");
        third.FirstName = "Carol";
        third.Status = "activated";
        await store.CreateAsync(third, null);

        var page = await store.ListAsync(new UserListQuery { First = 1 });

        Assert.Equal(Expected("alice@example.com"), page.Nodes.Select(row => row.Email));
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(3, page.TotalCount);

        var next = await store.ListAsync(new UserListQuery
        {
            After = new ListCursor(page.Nodes[0].Id, null),
            First = 1,
        });
        Assert.Equal(Expected("bob@example.com"), next.Nodes.Select(row => row.Email));
        Assert.True(next.HasPreviousPage);

        var activated = await store.ListAsync(new UserListQuery
        {
            Where = new UserListFilter { StatusIn = ["activated"] },
        });
        Assert.Equal(Expected("alice@example.com", "carol@example.com"),
            activated.Nodes.Select(row => row.Email));

        var folded = await store.ListAsync(new UserListQuery
        {
            Where = new UserListFilter { EmailContainsFold = "AROL" },
        });
        Assert.Equal(Expected("carol@example.com"), folded.Nodes.Select(row => row.Email));

        var disjunction = await store.ListAsync(new UserListQuery
        {
            Where = new UserListFilter
            {
                Or =
                [
                    new UserListFilter { FirstNameContainsFold = "ali" },
                    new UserListFilter { FirstNameContainsFold = "BOB" },
                ],
            },
        });
        Assert.Equal(Expected("alice@example.com", "bob@example.com"),
            disjunction.Nodes.Select(row => row.Email));

        var excluded = await store.ListAsync(new UserListQuery
        {
            Where = new UserListFilter { Not = new UserListFilter { StatusIn = ["activated"] } },
        });
        Assert.Equal(Expected("bob@example.com"), excluded.Nodes.Select(row => row.Email));

        var newest = await store.ListAsync(new UserListQuery
        {
            SortField = UserSortField.UpdatedAt,
            Direction = SortDirection.Desc,
        });
        Assert.Equal(
            Expected("carol@example.com", "bob@example.com", "alice@example.com"),
            newest.Nodes.Select(row => row.Email));
    }

    [Fact]
    public async Task ListAppliesIdAndTimePredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var alice = NewUser("alice@example.com");
        alice.CreatedAt = CreatedAt;
        alice.UpdatedAt = CreatedAt.AddHours(3);
        var aliceId = await store.CreateAsync(alice, null);

        var bob = NewUser("bob@example.com");
        bob.CreatedAt = CreatedAt.AddMinutes(1);
        bob.UpdatedAt = CreatedAt.AddHours(1);
        var bobId = await store.CreateAsync(bob, null);

        var carol = NewUser("carol@example.com");
        carol.CreatedAt = CreatedAt.AddMinutes(2);
        carol.UpdatedAt = CreatedAt.AddHours(2);
        var carolId = await store.CreateAsync(carol, null);

        async Task<string[]> Emails(UserListFilter where)
        {
            var page = await store.ListAsync(new UserListQuery { Where = where });
            return page.Nodes.Select(static row => row.Email).ToArray();
        }

        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IdNEQ = aliceId }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IdIn = [aliceId, carolId] }));
        Assert.Empty(await Emails(new UserListFilter { IdIn = [] }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IdNotIn = [] }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IdGT = aliceId }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IdGTE = aliceId }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { IdLT = bobId }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com"),
            await Emails(new UserListFilter { IdLTE = bobId }));

        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { CreatedAt = CreatedAt }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { CreatedAtNEQ = CreatedAt }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { CreatedAtIn = [CreatedAt, CreatedAt.AddMinutes(2)] }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { CreatedAtNotIn = [CreatedAt] }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { CreatedAtGT = CreatedAt }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { CreatedAtGTE = CreatedAt.AddMinutes(1) }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { CreatedAtLT = CreatedAt.AddMinutes(1) }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com"),
            await Emails(new UserListFilter { CreatedAtLTE = CreatedAt.AddMinutes(1) }));

        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { UpdatedAtLT = CreatedAt.AddHours(2) }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { UpdatedAtGTE = CreatedAt.AddHours(2) }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { UpdatedAtNEQ = CreatedAt.AddHours(1) }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { UpdatedAtNotIn = [CreatedAt.AddHours(1)] }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { UpdatedAtLTE = CreatedAt.AddHours(2) }));
    }

    [Fact]
    public async Task ListAppliesEveryStringAndBooleanPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var alice = NewUser("alice@example.com");
        alice.FirstName = "Alice";
        alice.LastName = "Anderson";
        alice.Status = "activated";
        alice.PreferLanguage = "en";
        alice.Avatar = "https://cdn/a.png";
        alice.IsOwner = false;
        await store.CreateAsync(alice, null);

        var bob = NewUser("bob@example.com");
        bob.FirstName = "Bob";
        bob.LastName = "Brown";
        bob.Status = "deactivated";
        bob.PreferLanguage = "zh";
        bob.Avatar = null;
        bob.IsOwner = true;
        await store.CreateAsync(bob, null);

        var carol = NewUser("carol@example.com");
        carol.FirstName = "Carol";
        carol.LastName = "Clark";
        carol.Status = "activated";
        carol.PreferLanguage = "en";
        carol.Avatar = "https://cdn/c.png";
        carol.IsOwner = false;
        await store.CreateAsync(carol, null);

        async Task<string[]> Emails(UserListFilter where)
        {
            var page = await store.ListAsync(new UserListQuery { Where = where });
            return page.Nodes.Select(static row => row.Email).ToArray();
        }

        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { Email = "alice@example.com" }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { EmailNEQ = "alice@example.com" }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { EmailIn = ["alice@example.com", "carol@example.com"] }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { EmailNotIn = ["alice@example.com"] }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { EmailGT = "bob@example.com" }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { EmailGTE = "bob@example.com" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { EmailLT = "bob@example.com" }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com"),
            await Emails(new UserListFilter { EmailLTE = "bob@example.com" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { EmailContains = "carol" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { EmailHasPrefix = "bob@" }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { EmailHasSuffix = "@example.com" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { EmailEqualFold = "BOB@EXAMPLE.COM" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { EmailContainsFold = "AROL" }));

        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { Status = "deactivated" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { StatusNEQ = "activated" }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { StatusIn = ["activated"] }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { StatusNotIn = ["activated"] }));
        Assert.Empty(await Emails(new UserListFilter { StatusIn = [] }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { StatusNotIn = [] }));

        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguage = "zh" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageNEQ = "en" }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { PreferLanguageIn = ["en"] }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageNotIn = ["en"] }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageGT = "en" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageGTE = "zh" }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { PreferLanguageLT = "zh" }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { PreferLanguageLTE = "en" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageContains = "h" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageHasPrefix = "z" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageHasSuffix = "h" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageEqualFold = "ZH" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { PreferLanguageContainsFold = "ZH" }));

        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { FirstName = "Alice" }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { FirstNameNEQ = "Alice" }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com"),
            await Emails(new UserListFilter { FirstNameIn = ["Alice", "Bob"] }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { FirstNameNotIn = ["Alice"] }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { FirstNameGT = "Bob" }));
        Assert.Equal(
            Expected("bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { FirstNameGTE = "Bob" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { FirstNameLT = "Bob" }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com"),
            await Emails(new UserListFilter { FirstNameLTE = "Bob" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { FirstNameContains = "aro" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { FirstNameHasPrefix = "Ali" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { FirstNameHasSuffix = "ob" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { FirstNameEqualFold = "ALICE" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { FirstNameContainsFold = "ARO" }));

        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { LastName = "Brown" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { LastNameContains = "ark" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { LastNameHasPrefix = "And" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { LastNameEqualFold = "CLARK" }));

        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { Avatar = "https://cdn/a.png" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { AvatarNEQ = "https://cdn/a.png" }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { AvatarIn = ["https://cdn/a.png", "https://cdn/c.png"] }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { AvatarNotIn = ["https://cdn/a.png"] }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { AvatarContains = "cdn" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { AvatarHasPrefix = "https://cdn/a" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { AvatarHasSuffix = "c.png" }));
        Assert.Equal(
            Expected("carol@example.com"),
            await Emails(new UserListFilter { AvatarGT = "https://cdn/a.png" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { AvatarLT = "https://cdn/c.png" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { AvatarEqualFold = "HTTPS://CDN/A.PNG" }));
        Assert.Equal(
            Expected("alice@example.com"),
            await Emails(new UserListFilter { AvatarContainsFold = "CDN/A" }));
        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { AvatarIsNil = true }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { AvatarIsNil = false }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { AvatarNotNil = true }));
        Assert.Equal(
            Expected("alice@example.com", "bob@example.com", "carol@example.com"),
            await Emails(new UserListFilter { AvatarNotNil = false }));

        Assert.Equal(
            Expected("bob@example.com"),
            await Emails(new UserListFilter { IsOwner = true }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IsOwner = false }));
        Assert.Equal(
            Expected("alice@example.com", "carol@example.com"),
            await Emails(new UserListFilter { IsOwnerNEQ = true }));
    }

    [Fact]
    public async Task UpdateAppliesFieldsScopesAndRoleEdges()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var roleIds = await SeedRolesAsync(context, 3);
        var userId = await store.CreateAsync(NewUser("alice@example.com"), [roleIds[0]]);

        var actual = await store.UpdateAsync(new UserUpdate
        {
            Id = userId,
            FirstName = "Alice",
            LastName = "Zhang",
            Email = "alice.zhang@example.com",
            IsOwner = true,
            Avatar = "https://example.com/a.png",
            Password = "$2a$10$new",
            Scopes = ["read_channels"],
            AppendScopes = ["write_requests"],
            AddRoleIds = [roleIds[0], roleIds[1]],
            RemoveRoleIds = [roleIds[0]],
        });

        Assert.NotNull(actual);
        Assert.Equal("Alice", actual.FirstName);
        Assert.Equal("Zhang", actual.LastName);
        Assert.Equal("alice.zhang@example.com", actual.Email);
        Assert.True(actual.IsOwner);
        Assert.Equal("https://example.com/a.png", actual.Avatar);
        Assert.Equal("$2a$10$new", actual.Password);
        RecordAssert.Json("""["read_channels","write_requests"]""", actual.Scopes);

        var roles = await context.UserRoles
            .Where(row => row.UserId == userId)
            .Select(row => row.RoleId)
            .ToListAsync();
        Assert.Equal(Expected(roleIds[1]), roles.Order());

        var cleared = await store.UpdateAsync(new UserUpdate
        {
            Id = userId,
            ClearAvatar = true,
            ClearScopes = true,
            ClearRoles = true,
            AddRoleIds = [roleIds[2]],
        });

        Assert.NotNull(cleared);
        Assert.Null(cleared.Avatar);
        Assert.Null(cleared.Scopes);
        Assert.Empty(await context.UserRoles
            .Where(row => row.UserId == userId)
            .Select(row => row.RoleId)
            .ToListAsync());

        Assert.Null(await store.UpdateAsync(new UserUpdate { Id = 999_999, FirstName = "x" }));
    }

    [Fact]
    public async Task UpdateStatusDeactivatesOnlyEnabledPersonalKeys()
    {
        // Reference UserService.UpdateUserStatus: deactivating a user disables
        // the personal API keys that are still enabled.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        await SeedApiKeyAsync(context, userId, "personal", "enabled", "personal-enabled");
        await SeedApiKeyAsync(context, userId, "personal", "disabled", "personal-disabled");
        await SeedApiKeyAsync(context, userId, "user", "enabled", "user-enabled");

        var actual = await store.UpdateStatusAsync(userId, "deactivated");

        Assert.NotNull(actual);
        Assert.Equal("deactivated", actual.Status);

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(
            Expected("disabled", "disabled", "enabled"),
            await verify.ApiKeys.OrderBy(row => row.Id).Select(row => row.Status).ToListAsync());

        Assert.Null(await store.UpdateStatusAsync(999_999, "deactivated"));
    }

    [Fact]
    public async Task DeleteArchivesPersonalKeysRemovesRelationsAndSoftDeletes()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var roleIds = await SeedRolesAsync(context, 1);
        var userId = await store.CreateAsync(NewUser("alice@example.com"), [roleIds[0]]);
        context.UserProjects.Add(new UserProjectRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            IsOwner = true,
            UserId = userId,
            ProjectId = 7,
        });
        await context.SaveChangesAsync();
        await SeedApiKeyAsync(context, userId, "personal", "enabled", "personal-enabled");
        await SeedApiKeyAsync(context, userId, "personal", "disabled", "personal-disabled");

        Assert.True(await store.DeleteAsync(userId));

        Assert.Null(await store.GetByIdAsync(userId));
        Assert.False(await store.DeleteAsync(userId));

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"user_roles\" WHERE \"user_id\" = @UserId;",
            new { UserId = userId }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"user_projects\" WHERE \"user_id\" = @UserId;",
            new { UserId = userId }));
        Assert.Equal(Expected("archived", "archived"), (await connection.QueryAsync<string>(
            "SELECT \"status\" FROM \"api_keys\" WHERE \"user_id\" = @UserId ORDER BY \"id\";",
            new { UserId = userId })).ToArray());
    }

    [Fact]
    public async Task UpdateStatusReportsDisableFailuresWithTheReferencePrefix()
    {
        // Reference UpdateUserStatus: "failed to disable user personal API keys: %w",
        // returned by RunInTransaction without an extra status prefix.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        await BreakApiKeysTableAsync(db);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateStatusAsync(userId, "deactivated"));

        Assert.StartsWith("failed to disable user personal API keys: ", error.Message);

        // The status update was part of the same transaction, so it rolled back.
        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal("activated", await verify.Users
            .Where(row => row.Id == userId)
            .Select(row => row.Status)
            .SingleAsync());
    }

    [Fact]
    public async Task DeleteReportsArchiveFailuresWithTheReferencePrefix()
    {
        // Reference DeleteUser: "failed to archive user personal API keys: %w".
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        await BreakApiKeysTableAsync(db);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.DeleteAsync(userId));

        Assert.StartsWith("failed to archive user personal API keys: ", error.Message);
    }

    [Fact]
    public async Task AddProjectUserCreatesTheRelationshipAndRoleLinks()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var roleIds = await SeedRolesAsync(context, 2);

        var row = await store.AddProjectUserAsync(
            userId,
            7,
            isOwner: true,
            scopes: ["read_project"],
            [roleIds[0], roleIds[0], roleIds[1]]);

        Assert.Equal(userId, row.UserId);
        Assert.Equal(7, row.ProjectId);
        Assert.True(row.IsOwner);
        RecordAssert.Json("""["read_project"]""", row.Scopes);

        var roles = await context.UserRoles
            .Where(link => link.UserId == userId)
            .Select(link => link.RoleId)
            .ToListAsync();
        Assert.Equal(Expected(roleIds[0], roleIds[1]), roles.Order());
    }

    [Fact]
    public async Task AddProjectUserDefaultsIsOwnerToFalseAndLeavesScopesNull()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);

        // Ent's create default for is_owner is false (user.go:426-428).
        var row = await store.AddProjectUserAsync(userId, 7, null, null, []);

        Assert.False(row.IsOwner);
        Assert.Null(row.Scopes);
    }

    [Fact]
    public async Task AddProjectUserReportsTheDuplicateRelationshipWithTheReferencePrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        await store.AddProjectUserAsync(userId, 7, null, null, []);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AddProjectUserAsync(userId, 7, null, null, []));

        Assert.StartsWith("failed to add user to project: ", error.Message);
    }

    [Fact]
    public async Task AddProjectUserReportsTheMissingUserAndRollsTheInsertBack()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var roleIds = await SeedRolesAsync(context, 1);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AddProjectUserAsync(999_999, 7, null, null, [roleIds[0]]));

        // Reference AddUserToProject: client.User.Get → "failed to get user: %w"
        // inside the transaction (user.go:443-446).
        Assert.Equal("failed to get user: ent: user not found", error.Message);

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(0, await verify.UserProjects.CountAsync());
    }

    [Fact]
    public async Task AddProjectUserRunsTheCreatePolicyBeforeTheInsert()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var roleIds = await SeedRolesAsync(context, 1);

        // A deny at mut.Save rolls the transaction back before the insert
        // (user.go:434-437).
        var denied = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AddProjectUserAsync(
                userId,
                7,
                null,
                null,
                [roleIds[0]],
                beforeCreate: _ => throw new InvalidOperationException("create denied")));
        Assert.Equal("failed to add user to project: create denied", denied.Message);
        Assert.Empty(await store.ListProjectUsersForUserAsync(userId));

        // The insert's unique-index failure precedes client.User.Get, so the
        // User read policy never runs (user.go:434-443).
        await store.AddProjectUserAsync(userId, 7, null, null, []);
        var duplicate = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AddProjectUserAsync(
                userId,
                7,
                null,
                null,
                [roleIds[0]],
                beforeCreate: _ => Task.CompletedTask,
                beforeUserRead: _ => throw new InvalidOperationException("read denied")));
        Assert.StartsWith("failed to add user to project: ", duplicate.Message);
        Assert.DoesNotContain("read denied", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddProjectUserRunsTheUserReadPolicyBeforeTheRead()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var roleIds = await SeedRolesAsync(context, 1);

        // client.User.Get runs the User query policy (user.go:443-446).
        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AddProjectUserAsync(
                userId,
                7,
                null,
                null,
                [roleIds[0]],
                beforeUserRead: _ => throw new InvalidOperationException("read denied")));
        Assert.Equal("failed to get user: read denied", error.Message);

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(0, await verify.UserProjects.CountAsync());
        Assert.Equal(0, await verify.UserRoles.CountAsync());
    }

    [Fact]
    public async Task AddProjectUserRunsTheUserMutationPolicyBeforeTheRoleLinks()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var roleIds = await SeedRolesAsync(context, 1);

        // user.Update().AddRoleIDs runs the User mutation policy
        // (user.go:447-449).
        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AddProjectUserAsync(
                userId,
                7,
                null,
                null,
                [roleIds[0]],
                beforeUserRoleUpdate: _ => throw new InvalidOperationException(
                    "role update denied")));
        Assert.Equal("failed to add roles to user: role update denied", error.Message);

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(0, await verify.UserProjects.CountAsync());
        Assert.Equal(0, await verify.UserRoles.CountAsync());
    }

    [Fact]
    public async Task RemoveProjectUserDeletesTheRelationshipAndProjectRoleLinksOnly()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var projectRole = await SeedRoleAsync(context, "Project Admin", "project", 7);
        var otherProjectRole = await SeedRoleAsync(context, "Other Admin", "project", 8);
        var systemRole = await SeedRoleAsync(context, "Global Viewer", "system", 0);
        await store.AddProjectUserAsync(
            userId, 7, null, null, [projectRole, otherProjectRole, systemRole]);

        await store.RemoveProjectUserAsync(userId, 7);

        Assert.Empty(await store.ListProjectUsersForUserAsync(userId));

        var remaining = await context.UserRoles
            .Where(link => link.UserId == userId)
            .Select(link => link.RoleId)
            .ToListAsync();
        Assert.Equal(Expected(otherProjectRole, systemRole), remaining.Order());
    }

    [Fact]
    public async Task RemoveProjectUserWithNoRelationshipLeavesRoleLinksUntouched()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var systemRole = await SeedRoleAsync(context, "Global Viewer", "system", 0);
        await store.AddProjectUserAsync(userId, 9, null, null, [systemRole]);

        // rowsAffected == 0 returns before the role cleanup (user.go:476-478).
        await store.RemoveProjectUserAsync(userId, 7);

        Assert.Equal(
            Expected(systemRole),
            await context.UserRoles
                .Where(link => link.UserId == userId)
                .Select(link => link.RoleId)
                .ToListAsync());
    }

    [Fact]
    public async Task UpdateProjectUserAppliesFieldsAndAddsBeforeRemovingRoles()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var userId = await store.CreateAsync(NewUser("alice@example.com"), null);
        var role1 = await SeedRoleAsync(context, "Role 1", "system", 0);
        var role2 = await SeedRoleAsync(context, "Role 2", "system", 0);
        await store.AddProjectUserAsync(userId, 7, null, ["read_project"], [role1]);

        // role1 appears in both lists: ent applies the adds before the removes,
        // so it ends up removed (user.go:560-570).
        var row = await store.UpdateProjectUserAsync(
            userId,
            7,
            isOwner: true,
            scopes: ["read_project", "write_project"],
            [role1, role2],
            [role1]);

        Assert.True(row.IsOwner);
        RecordAssert.Json("""["read_project","write_project"]""", row.Scopes);

        var remaining = await context.UserRoles
            .Where(link => link.UserId == userId)
            .Select(link => link.RoleId)
            .ToListAsync();
        Assert.Equal(Expected(role2), remaining);
    }

    [Fact]
    public async Task UpdateProjectUserReportsTheMissingRelationshipWithTheReferencePrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateProjectUserAsync(999_999, 7, null, null, [], []));

        Assert.Equal(
            "failed to find user project relationship: ent: user_project not found",
            error.Message);
    }

    [Fact]
    public async Task ListProjectUsersReturnsEveryRowWithoutOrdering()
    {
        // Project.QueryProjectUsers().All() / User.QueryProjectUsers().All()
        // have no order clause, so the port asserts set equality (C164).
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserAdminStore(context);

        var alice = await store.CreateAsync(NewUser("alice@example.com"), null);
        var bob = await store.CreateAsync(NewUser("bob@example.com"), null);
        await store.AddProjectUserAsync(alice, 7, null, null, []);
        await store.AddProjectUserAsync(bob, 7, null, null, []);
        await store.AddProjectUserAsync(bob, 8, null, null, []);

        var projectUsers = await store.ListProjectUsersAsync(7);
        Assert.Equal(
            Expected((alice, 7L), (bob, 7L)),
            projectUsers.Select(row => (row.UserId, row.ProjectId)).Order());

        var bobs = await store.ListProjectUsersForUserAsync(bob);
        Assert.Equal(Expected(7L, 8L), bobs.Select(row => row.ProjectId).Order());
    }

    private static async Task BreakApiKeysTableAsync(TestDatabase db)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync("ALTER TABLE \"api_keys\" RENAME TO \"api_keys_broken\";");
    }

    private static T[] Expected<T>(params T[] values) => values;

    private static async Task<long> SeedRoleAsync(
        AxonHubDbContext context,
        string name,
        string level,
        long projectId)
    {
        var role = new RoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = name,
            Level = level,
            ProjectId = projectId,
        };
        context.Roles.Add(role);
        await context.SaveChangesAsync();
        return role.Id;
    }

    private static async Task<long[]> SeedRolesAsync(AxonHubDbContext context, int count)
    {
        var ids = new long[count];
        for (var i = 0; i < count; i++)
        {
            var role = new RoleRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                Name = "role-" + i,
                Level = "system",
                ProjectId = 0,
            };
            context.Roles.Add(role);
            await context.SaveChangesAsync();
            ids[i] = role.Id;
        }

        return ids;
    }

    private static async Task SeedApiKeyAsync(
        AxonHubDbContext context,
        long userId,
        string type,
        string status,
        string key)
    {
        context.ApiKeys.Add(new ApiKeyRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Key = key,
            Name = key,
            Type = type,
            Status = status,
            ProjectId = 1,
            UserId = userId,
        });
        await context.SaveChangesAsync();
    }

    private static UserRecord NewUser(string email) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Email = email,
        Status = "activated",
        PreferLanguage = "en",
        Password = "$2a$10$hashed",
        FirstName = "First",
        LastName = "Last",
        Scopes = """["read_channels"]""",
    };
}
