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
    public async Task ListAppliesIdAndTimePredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var alpha = NewRole("Alpha", "system", 0);
        alpha.CreatedAt = CreatedAt;
        alpha.UpdatedAt = CreatedAt.AddHours(3);
        var alphaId = await store.CreateAsync(alpha);

        var beta = NewRole("Beta", "project", 7);
        beta.CreatedAt = CreatedAt.AddMinutes(1);
        beta.UpdatedAt = CreatedAt.AddHours(1);
        var betaId = await store.CreateAsync(beta);

        var gamma = NewRole("gamma", "system", null);
        gamma.CreatedAt = CreatedAt.AddMinutes(2);
        gamma.UpdatedAt = CreatedAt.AddHours(2);
        var gammaId = await store.CreateAsync(gamma);

        async Task<string[]> Names(RoleListFilter where)
        {
            var page = await store.ListAsync(new RoleListQuery { Where = where });
            return page.Nodes.Select(static row => row.Name).ToArray();
        }

        Assert.Equal(Expected("Beta", "gamma"), await Names(new RoleListFilter { IdNEQ = alphaId }));
        Assert.Equal(
            Expected("Alpha", "gamma"),
            await Names(new RoleListFilter { IdIn = [alphaId, gammaId] }));
        // entgql ignores an empty In-list.
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { IdIn = [] }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { IdNotIn = [] }));
        Assert.Equal(Expected("Beta", "gamma"), await Names(new RoleListFilter { IdGT = alphaId }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { IdGTE = alphaId }));
        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { IdLT = betaId }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { IdLTE = betaId }));

        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { CreatedAt = CreatedAt }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { CreatedAtNEQ = CreatedAt }));
        Assert.Equal(
            Expected("Alpha", "gamma"),
            await Names(new RoleListFilter { CreatedAtIn = [CreatedAt, CreatedAt.AddMinutes(2)] }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { CreatedAtNotIn = [CreatedAt] }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { CreatedAtGT = CreatedAt }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { CreatedAtGTE = CreatedAt.AddMinutes(1) }));
        Assert.Equal(
            Expected("Alpha"),
            await Names(new RoleListFilter { CreatedAtLT = CreatedAt.AddMinutes(1) }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { CreatedAtLTE = CreatedAt.AddMinutes(1) }));

        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { UpdatedAt = CreatedAt.AddHours(2) }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { UpdatedAtNEQ = CreatedAt.AddHours(2) }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter
            {
                UpdatedAtIn = [CreatedAt.AddHours(1), CreatedAt.AddHours(3)],
            }));
        Assert.Equal(
            Expected("Alpha", "gamma"),
            await Names(new RoleListFilter { UpdatedAtNotIn = [CreatedAt.AddHours(1)] }));
        Assert.Equal(
            Expected("Alpha", "gamma"),
            await Names(new RoleListFilter { UpdatedAtGT = CreatedAt.AddHours(1) }));
        Assert.Equal(
            Expected("Alpha", "gamma"),
            await Names(new RoleListFilter { UpdatedAtGTE = CreatedAt.AddHours(2) }));
        Assert.Equal(
            Expected("Beta"),
            await Names(new RoleListFilter { UpdatedAtLT = CreatedAt.AddHours(2) }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { UpdatedAtLTE = CreatedAt.AddHours(2) }));
    }

    [Fact]
    public async Task ListAppliesNameLevelAndProjectPredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        await store.CreateAsync(NewRole("Alpha", "system", 0));
        await store.CreateAsync(NewRole("Beta", "project", 7));
        var gammaId = await store.CreateAsync(NewRole("gamma", "system", null));

        async Task<string[]> Names(RoleListFilter where)
        {
            var page = await store.ListAsync(new RoleListQuery { Where = where });
            return page.Nodes.Select(static row => row.Name).ToArray();
        }

        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { Name = "Alpha" }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { NameNEQ = "Alpha" }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { NameIn = ["Alpha", "Beta"] }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { NameNotIn = ["Alpha"] }));
        Assert.Equal(Expected("gamma"), await Names(new RoleListFilter { NameGT = "Beta" }));
        Assert.Equal(
            Expected("Beta", "gamma"),
            await Names(new RoleListFilter { NameGTE = "Beta" }));
        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { NameLT = "Beta" }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { NameLTE = "Beta" }));
        Assert.Equal(Expected("gamma"), await Names(new RoleListFilter { NameContains = "amm" }));
        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { NameHasPrefix = "Al" }));
        Assert.Equal(Expected("Beta"), await Names(new RoleListFilter { NameHasSuffix = "ta" }));
        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { NameEqualFold = "ALPHA" }));
        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { NameContainsFold = "AMM" }));

        Assert.Equal(
            Expected("Alpha", "gamma"),
            await Names(new RoleListFilter { Level = "system" }));
        Assert.Equal(Expected("Beta"), await Names(new RoleListFilter { LevelNEQ = "system" }));
        Assert.Equal(Expected("Beta"), await Names(new RoleListFilter { LevelIn = ["project"] }));
        Assert.Equal(
            Expected("Beta"),
            await Names(new RoleListFilter { LevelNotIn = ["system"] }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { LevelIn = [] }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { LevelNotIn = [] }));

        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { ProjectId = 0 }));
        Assert.Equal(Expected("Beta"), await Names(new RoleListFilter { ProjectId = 7 }));
        // "project_id <> 7" and "NOT IN" exclude the NULL row, like SQL and ent.
        Assert.Equal(Expected("Alpha"), await Names(new RoleListFilter { ProjectIdNEQ = 7 }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { ProjectIdIn = [0, 7] }));
        Assert.Equal(
            Expected("Beta"),
            await Names(new RoleListFilter { ProjectIdNotIn = [0] }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { ProjectIdIn = [] }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { ProjectIdNotIn = [] }));

        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { ProjectIdIsNil = true }));
        Assert.Equal(
            Expected("Alpha", "Beta"),
            await Names(new RoleListFilter { ProjectIdNotNil = true }));
        // Ent reads these as plain bools: false is ignored, not inverted.
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { ProjectIdIsNil = false }));
        Assert.Equal(
            Expected("Alpha", "Beta", "gamma"),
            await Names(new RoleListFilter { ProjectIdNotNil = false }));

        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { Id = gammaId }));
    }

    [Fact]
    public async Task ListAppliesUserEdgePredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var alice = NewUser("alice@example.com");
        var bob = NewUser("bob@example.com");
        var carol = NewUser("carol@example.com");
        context.Users.AddRange(alice, bob, carol);
        await context.SaveChangesAsync();

        var alphaId = await store.CreateAsync(NewRole("alpha", "system", 0));
        var betaId = await store.CreateAsync(NewRole("beta", "system", 0));
        var gammaId = await store.CreateAsync(NewRole("gamma", "system", 0));

        var alphaLink = new UserRoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            UserId = alice.Id,
            RoleId = alphaId,
        };
        var betaLink = new UserRoleRecord
        {
            // The reference declares both timestamps nillable on user_roles.
            CreatedAt = null,
            UpdatedAt = null,
            UserId = bob.Id,
            RoleId = betaId,
        };
        context.UserRoles.AddRange(alphaLink, betaLink);
        await context.SaveChangesAsync();

        async Task<string[]> Names(RoleListFilter where)
        {
            var page = await store.ListAsync(new RoleListQuery { Where = where });
            return page.Nodes.Select(static row => row.Name).ToArray();
        }

        Assert.Equal(
            Expected("alpha", "beta"),
            await Names(new RoleListFilter { HasUsers = true }));
        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { HasUsers = false }));
        Assert.Equal(
            Expected("alpha", "beta", "gamma"),
            await Names(new RoleListFilter { HasUsersWith = [] }));
        Assert.Equal(
            Expected("alpha"),
            await Names(new RoleListFilter
            {
                HasUsersWith = [new UserListFilter { Email = "alice@example.com" }],
            }));
        Assert.Equal(
            Expected("beta"),
            await Names(new RoleListFilter
            {
                HasUsersWith = [new UserListFilter { EmailContainsFold = "BOB@" }],
            }));
        // Several entries AND into the same EXISTS subquery: bob is activated
        // enough for the status predicate, but only alice carries that email.
        Assert.Equal(
            Expected("alpha"),
            await Names(new RoleListFilter
            {
                HasUsersWith =
                [
                    new UserListFilter { Email = "alice@example.com" },
                    new UserListFilter { Status = "activated" },
                ],
            }));

        Assert.Equal(
            Expected("alpha", "beta"),
            await Names(new RoleListFilter { HasUserRoles = true }));
        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { HasUserRoles = false }));
        Assert.Equal(
            Expected("beta"),
            await Names(new RoleListFilter
            {
                HasUserRolesWith = [new UserRoleListFilter { Id = betaLink.Id }],
            }));
        Assert.Equal(
            Expected("beta"),
            await Names(new RoleListFilter
            {
                HasUserRolesWith = [new UserRoleListFilter { CreatedAtIsNil = true }],
            }));
        // Ent reads IsNil as a plain bool: false is ignored, so the empty
        // predicate still requires the user_roles row to exist.
        Assert.Equal(
            Expected("alpha", "beta"),
            await Names(new RoleListFilter
            {
                HasUserRolesWith = [new UserRoleListFilter { CreatedAtIsNil = false }],
            }));

        Assert.Equal(
            Expected("gamma"),
            await Names(new RoleListFilter { Not = new RoleListFilter { HasUsers = true } }));
    }

    [Fact]
    public async Task ListAppliesProjectEdgePredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var alphaProject = NewProject("alpha");
        var betaProject = NewProject("beta", status: "archived");
        betaProject.DeletedAt = 1_700_000_000L;
        context.Projects.AddRange(alphaProject, betaProject);
        await context.SaveChangesAsync();

        await store.CreateAsync(NewRole("alpha", "project", alphaProject.Id));
        await store.CreateAsync(NewRole("beta", "project", betaProject.Id));
        await store.CreateAsync(NewRole("gamma", "system", null));
        // project_id has no foreign key, so a dangling id exists in the wild.
        await store.CreateAsync(NewRole("delta", "project", 999_999));

        async Task<string[]> Names(RoleListFilter where)
        {
            var page = await store.ListAsync(new RoleListQuery { Where = where });
            return page.Nodes.Select(static row => row.Name).ToArray();
        }

        Assert.Equal(Expected("alpha", "beta"), await Names(new RoleListFilter { HasProject = true }));
        Assert.Equal(
            Expected("gamma", "delta"),
            await Names(new RoleListFilter { HasProject = false }));
        Assert.Equal(
            Expected("alpha", "beta", "gamma", "delta"),
            await Names(new RoleListFilter { HasProjectWith = [] }));
        Assert.Equal(
            Expected("alpha", "beta"),
            await Names(new RoleListFilter { HasProjectWith = [new ProjectListFilter()] }));
        Assert.Equal(
            Expected("alpha"),
            await Names(new RoleListFilter
            {
                HasProjectWith = [new ProjectListFilter { Name = "alpha" }],
            }));
        // Soft-deleted projects still match, like the generated ent predicate.
        Assert.Equal(
            Expected("beta"),
            await Names(new RoleListFilter
            {
                HasProjectWith = [new ProjectListFilter { Name = "beta" }],
            }));
        Assert.Equal(
            Expected("beta"),
            await Names(new RoleListFilter
            {
                HasProjectWith = [new ProjectListFilter { Status = "archived" }],
            }));
        Assert.Empty(await Names(new RoleListFilter
        {
            HasProjectWith = [new ProjectListFilter { Name = "missing" }],
        }));

        Assert.Equal(
            Expected("gamma", "delta"),
            await Names(new RoleListFilter { Not = new RoleListFilter { HasProject = true } }));
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

    [Fact]
    public async Task ListUserRolesForRoleReturnsOnlyThatRolesLinksInIdOrder()
    {
        // Role.userRoles is the entgql list form of the user_roles edge
        // (ent.resolvers.go:832-834 runs All(ctx)); the port reads the join
        // table by role_id (C164).
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RoleStore(context);

        var alice = NewUser("alice@example.com");
        var bob = NewUser("bob@example.com");
        context.Users.AddRange(alice, bob);
        await context.SaveChangesAsync();

        var alphaId = await store.CreateAsync(NewRole("alpha", "system", 0));
        var betaId = await store.CreateAsync(NewRole("beta", "system", 0));

        context.UserRoles.AddRange(
            new UserRoleRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                UserId = alice.Id,
                RoleId = alphaId,
            },
            new UserRoleRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                UserId = bob.Id,
                RoleId = betaId,
            },
            new UserRoleRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                UserId = bob.Id,
                RoleId = alphaId,
            });
        await context.SaveChangesAsync();

        var links = await store.ListUserRolesForRoleAsync(alphaId);

        Assert.Equal([alice.Id, bob.Id], links.Select(static link => link.UserId).ToArray());
        Assert.All(links, link => Assert.Equal(alphaId, link.RoleId));
        Assert.Empty(await store.ListUserRolesForRoleAsync(betaId + 1));
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
    };

    private static ProjectRecord NewProject(string name, string status = "active") => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = name,
        Description = name + " project",
        Status = status,
    };

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
