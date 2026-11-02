using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RoleStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var role = NewRole("admin", "system", 0);
        var id = await store.CreateAsync(role);

        role.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(role, actual);
    }

    [Fact]
    public async Task NullProjectIdRoundTripsAsNull()
    {
        // Ent declares project_id nullable with a default of 0, so both states
        // exist in the reference database.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var role = NewRole("legacy", "project", null);
        var id = await store.CreateAsync(role);

        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        Assert.Null(actual.ProjectId);
    }

    [Fact]
    public async Task NameExistsScopesToProjectAndIgnoresSoftDeletedRoles()
    {
        // RoleService.RoleNameExists queries
        // Role.Query().Where(NameEQ(name), ProjectIDEQ(projectID)).Exist.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var role = NewRole("admin", "system", 0);
        var id = await store.CreateAsync(role);

        Assert.True(await store.NameExistsAsync("admin", 0));
        Assert.False(await store.NameExistsAsync("admin", 7));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"roles\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.False(await store.NameExistsAsync("admin", 0));
    }

    [Fact]
    public async Task LiveNamesAreUniquePerProjectEvenAfterSoftDelete()
    {
        // roles_by_project_id_name is unique on (project_id, name) and, unlike
        // user_email_deleted_at, does not include deleted_at.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var firstId = await store.CreateAsync(NewRole("admin", "system", 0));

        var duplicate = NewRole("admin", "system", 0);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(duplicate));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"roles\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(duplicate));
    }

    [Fact]
    public async Task UpdateTouchesOnlySuppliedFieldsAndRefreshesUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var role = NewRole("admin", "system", 0);
        var id = await store.CreateAsync(role);
        // EF tracks the same instance, so capture the timestamp before the update.
        var before = role.UpdatedAt;

        var actual = await store.UpdateAsync(id, "operator", ["read_channels"]);

        Assert.NotNull(actual);
        Assert.Equal("operator", actual.Name);
        Assert.Equal("system", actual.Level);
        Assert.Equal(0L, actual.ProjectId);
        RecordAssert.Json("""["read_channels"]""", actual.Scopes);
        Assert.True(actual.UpdatedAt > before);
        Assert.Null(await store.UpdateAsync(999_999, "x", null));
    }

    [Fact]
    public async Task ListAppliesFiltersOrderAndKeysetPagination()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var alpha = NewRole("Alpha", "system", 0);
        alpha.CreatedAt = CreatedAt;
        alpha.UpdatedAt = CreatedAt.AddHours(3);
        await store.CreateAsync(alpha);

        var beta = NewRole("Beta", "project", 7);
        beta.CreatedAt = CreatedAt.AddMinutes(1);
        beta.UpdatedAt = CreatedAt.AddHours(1);
        await store.CreateAsync(beta);

        var gamma = NewRole("gamma", "system", 0);
        gamma.CreatedAt = CreatedAt.AddMinutes(2);
        gamma.UpdatedAt = CreatedAt.AddHours(2);
        await store.CreateAsync(gamma);

        var page = await store.ListAsync(new RoleListQuery
        {
            Where = new RoleListFilter { Level = "system" },
            First = 1,
        });

        Assert.Equal(Expected("Alpha"), page.Nodes.Select(row => row.Name));
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(2, page.TotalCount);

        var next = await store.ListAsync(new RoleListQuery
        {
            Where = new RoleListFilter { Level = "system" },
            After = new ListCursor(page.Nodes[0].Id, null),
            First = 1,
        });

        Assert.Equal(Expected("gamma"), next.Nodes.Select(row => row.Name));
        Assert.False(next.HasNextPage);
        Assert.True(next.HasPreviousPage);

        var folded = await store.ListAsync(new RoleListQuery
        {
            Where = new RoleListFilter { NameContainsFold = "AMM" },
        });
        Assert.Equal(Expected("gamma"), folded.Nodes.Select(row => row.Name));

        var byProject = await store.ListAsync(new RoleListQuery
        {
            Where = new RoleListFilter { ProjectId = 7 },
        });
        Assert.Equal(Expected("Beta"), byProject.Nodes.Select(row => row.Name));

        var disjunction = await store.ListAsync(new RoleListQuery
        {
            Where = new RoleListFilter
            {
                Or =
                [
                    new RoleListFilter { NameContainsFold = "alpha" },
                    new RoleListFilter { NameContainsFold = "BET" },
                ],
            },
        });
        Assert.Equal(Expected("Alpha", "Beta"), disjunction.Nodes.Select(row => row.Name));

        var updated = await store.ListAsync(new RoleListQuery
        {
            SortField = RoleSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            First = 3,
        });
        Assert.Equal(Expected("Alpha", "gamma", "Beta"), updated.Nodes.Select(row => row.Name));
    }

    [Fact]
    public async Task LastReturnsTheTailInAscendingOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        await store.CreateAsync(NewRole("a", "system", 0));
        await store.CreateAsync(NewRole("b", "system", 0));
        await store.CreateAsync(NewRole("c", "system", 0));

        var page = await store.ListAsync(new RoleListQuery { Last = 2 });

        Assert.Equal(Expected("b", "c"), page.Nodes.Select(row => row.Name));
        Assert.True(page.HasPreviousPage);
        Assert.False(page.HasNextPage);
    }

    [Fact]
    public async Task UserRolesFilterJoinsTheEdgeTable()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var user = new UserRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Email = "user@example.com",
            Status = "activated",
            PreferLanguage = "en",
            Password = "$2a$10$hashed",
            FirstName = "A",
            LastName = "B",
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var granted = await store.CreateAsync(NewRole("granted", "system", 0));
        await store.CreateAsync(NewRole("other", "system", 0));
        context.UserRoles.Add(new UserRoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            UserId = user.Id,
            RoleId = granted,
        });
        await context.SaveChangesAsync();

        var page = await store.ListAsync(new RoleListQuery { UserId = user.Id });

        Assert.Equal(Expected("granted"), page.Nodes.Select(row => row.Name));
    }

    [Fact]
    public async Task DeleteRemovesUserRolesAndSoftDeletesTheRole()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var user = new UserRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Email = "user@example.com",
            Status = "activated",
            PreferLanguage = "en",
            Password = "$2a$10$hashed",
            FirstName = "A",
            LastName = "B",
        };
        context.Users.Add(user);
        var roleId = await store.CreateAsync(NewRole("admin", "system", 0));
        context.UserRoles.Add(new UserRoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            UserId = user.Id,
            RoleId = roleId,
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.True(await store.DeleteAsync(roleId));
        Assert.Null(await store.GetByIdAsync(roleId));
        Assert.False(await store.DeleteAsync(roleId));

        await using var connection = await db.Factory.OpenAsync();
        var userRoles = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"user_roles\" WHERE \"role_id\" = @RoleId;",
            new { RoleId = roleId });
        Assert.Equal(0, userRoles);

        var deletedAt = await connection.ExecuteScalarAsync<long>(
            "SELECT \"deleted_at\" FROM \"roles\" WHERE \"id\" = @Id;",
            new { Id = roleId });
        Assert.True(deletedAt > 0);
    }

    [Fact]
    public async Task DeleteManySoftDeletesEveryListedRole()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var first = await store.CreateAsync(NewRole("a", "system", 0));
        var second = await store.CreateAsync(NewRole("b", "system", 0));
        var third = await store.CreateAsync(NewRole("c", "system", 0));

        await store.DeleteManyAsync([first, third]);

        Assert.Null(await store.GetByIdAsync(first));
        Assert.NotNull(await store.GetByIdAsync(second));
        Assert.Null(await store.GetByIdAsync(third));

        var remaining = await store.ListByIdsAsync([first, second, third]);
        Assert.Equal(Expected(second), remaining.Select(row => row.Id));
    }

    private static T[] Expected<T>(params T[] values) => values;

    private static RoleRecord NewRole(string name, string level, long? projectId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = name,
        Level = level,
        Scopes = """["read_channels","write_requests"]""",
        ProjectId = projectId,
    };
}
