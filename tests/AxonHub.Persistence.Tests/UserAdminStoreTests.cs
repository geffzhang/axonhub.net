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

    private static async Task BreakApiKeysTableAsync(TestDatabase db)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync("ALTER TABLE \"api_keys\" RENAME TO \"api_keys_broken\";");
    }

    private static T[] Expected<T>(params T[] values) => values;

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
