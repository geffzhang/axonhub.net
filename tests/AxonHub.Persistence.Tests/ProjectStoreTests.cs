using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProjectStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);

        Assert.True(id > 0);

        project.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(project, actual);
    }

    [Fact]
    public async Task GetFirstReturnsTheLowestLiveProject()
    {
        // api_key.EnsureNoAuthAPIKey resolves the default project with
        // Project.Query().Order(Asc(id)).First: the lowest live id wins.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var first = NewProject();
        first.Name = "default";
        var firstId = await store.CreateAsync(first);

        var second = NewProject();
        second.Name = "second";
        await store.CreateAsync(second);

        var actual = await store.GetFirstAsync();

        Assert.NotNull(actual);
        Assert.Equal(firstId, actual.Id);
    }

    [Fact]
    public async Task SoftDeletedProjectsAreInvisible()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var deleted = NewProject();
        deleted.Name = "deleted";
        var deletedId = await store.CreateAsync(deleted);

        var live = NewProject();
        live.Name = "live";
        var liveId = await store.CreateAsync(live);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"projects\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        Assert.Null(await store.GetByIdAsync(deletedId));

        var first = await store.GetFirstAsync();
        Assert.NotNull(first);
        Assert.Equal(liveId, first.Id);
    }

    [Fact]
    public async Task LiveProjectNamesAreUniqueAndSoftDeletedNamesCanBeReused()
    {
        // projects_by_name is unique on (name, deleted_at), mirroring Ent.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var firstId = await store.CreateAsync(project);

        var duplicate = NewProject();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(duplicate));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"projects\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        var reusedId = await store.CreateAsync(duplicate);
        Assert.True(reusedId > firstId);
    }

    [Fact]
    public async Task NameExistsIgnoresSoftDeletedProjects()
    {
        // ProjectService.CreateProject's duplicate check queries live rows only.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);

        Assert.True(await store.NameExistsAsync("default"));
        Assert.False(await store.NameExistsAsync("Default"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"projects\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        Assert.False(await store.NameExistsAsync("default"));
    }

    [Fact]
    public async Task ListAppliesFiltersOrderAndKeysetPagination()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var alpha = NewProject();
        alpha.Name = "Alpha";
        alpha.CreatedAt = CreatedAt;
        alpha.UpdatedAt = CreatedAt.AddHours(3);
        await store.CreateAsync(alpha);

        var beta = NewProject();
        beta.Name = "Beta";
        beta.CreatedAt = CreatedAt.AddMinutes(1);
        beta.UpdatedAt = CreatedAt.AddHours(1);
        await store.CreateAsync(beta);

        var gamma = NewProject();
        gamma.Name = "Gamma";
        gamma.CreatedAt = CreatedAt.AddMinutes(2);
        gamma.UpdatedAt = CreatedAt.AddHours(2);
        await store.CreateAsync(gamma);

        var page = await store.ListAsync(new ProjectListQuery { First = 2 });
        Assert.Equal(["Alpha", "Beta"], page.Nodes.Select(static row => row.Name));
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(3, page.TotalCount);

        var next = await store.ListAsync(new ProjectListQuery
        {
            After = new ListCursor(page.Nodes[1].Id, null),
            First = 2,
        });
        Assert.Equal(["Gamma"], next.Nodes.Select(static row => row.Name));
        Assert.False(next.HasNextPage);
        Assert.True(next.HasPreviousPage);

        var last = await store.ListAsync(new ProjectListQuery { Last = 2 });
        Assert.Equal(["Beta", "Gamma"], last.Nodes.Select(static row => row.Name));
        Assert.True(last.HasPreviousPage);
        Assert.False(last.HasNextPage);

        var folded = await store.ListAsync(new ProjectListQuery
        {
            Where = new ProjectListFilter { NameContainsFold = "AMM" },
            First = 10,
        });
        Assert.Equal(["Gamma"], folded.Nodes.Select(static row => row.Name));

        var disjunction = await store.ListAsync(new ProjectListQuery
        {
            Where = new ProjectListFilter
            {
                Or =
                [
                    new ProjectListFilter { NameContainsFold = "alpha" },
                    new ProjectListFilter { NameContainsFold = "BET" },
                ],
            },
            First = 10,
        });
        Assert.Equal(["Alpha", "Beta"], disjunction.Nodes.Select(static row => row.Name));

        var updated = await store.ListAsync(new ProjectListQuery
        {
            SortField = ProjectSortField.UpdatedAt,
            Direction = SortDirection.Desc,
            First = 3,
        });
        Assert.Equal(["Alpha", "Gamma", "Beta"], updated.Nodes.Select(static row => row.Name));

        var status = await store.ListAsync(new ProjectListQuery
        {
            Where = new ProjectListFilter { Status = "archived" },
            First = 10,
        });
        Assert.Empty(status.Nodes);
    }

    [Fact]
    public async Task UpdateTouchesOnlySuppliedFieldsAndRefreshesUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);
        // EF tracks the same instance, so capture the timestamp before the update.
        var before = project.UpdatedAt;

        var renamed = await store.UpdateAsync(new ProjectUpdate { Id = id, Name = "Renamed" });

        Assert.NotNull(renamed);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal("Default project", renamed.Description);
        Assert.True(renamed.UpdatedAt > before);

        var described = await store.UpdateAsync(
            new ProjectUpdate { Id = id, Description = "Changed" });

        Assert.NotNull(described);
        Assert.Equal("Renamed", described.Name);
        Assert.Equal("Changed", described.Description);

        Assert.Null(await store.UpdateAsync(new ProjectUpdate { Id = 999_999, Name = "x" }));
    }

    [Fact]
    public async Task UpdateReplacesUserProjectEdgesWithJoinTableDefaults()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);
        await AddProjectUsersAsync(context, id, (1, false), (2, true));

        var updated = await store.UpdateAsync(new ProjectUpdate
        {
            Id = id,
            RemovedUserIds = [1],
            AddedUserIds = [3],
        });

        Assert.NotNull(updated);
        var rows = await ProjectUsersAsync(db, id);
        Assert.Equal([2L, 3L], rows.Select(static row => row.UserId));
        Assert.True(rows.Single(row => row.UserId == 2).IsOwner);
        // Join-table creates use UserProject's defaults, not the updated row's.
        Assert.False(rows.Single(row => row.UserId == 3).IsOwner);
        RecordAssert.Json("[]", rows.Single(row => row.UserId == 3).Scopes);

        await store.UpdateAsync(new ProjectUpdate
        {
            Id = id,
            ClearUsers = true,
            AddedUserIds = [4],
        });

        rows = await ProjectUsersAsync(db, id);
        var only = Assert.Single(rows);
        Assert.Equal(4, only.UserId);
        Assert.False(only.IsOwner);
        RecordAssert.Json("[]", only.Scopes);
    }

    [Fact]
    public async Task DuplicateUserAddRollsBackTheWholeUpdate()
    {
        // sqlgraph order is node update, edge clear, edge add; adding an
        // existing member is a unique violation that rolls back the node
        // update too.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);
        await AddProjectUsersAsync(context, id, (4, true));

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateAsync(new ProjectUpdate
            {
                Id = id,
                Name = "ShouldRollback",
                AddedUserIds = [4],
            }));

        Assert.StartsWith("add m2m edge for table user_projects: ", error.Message);
        Assert.Contains("user_projects_by_user_id_project_id", error.Message);

        var live = await store.GetByIdAsync(id);
        Assert.NotNull(live);
        Assert.Equal("default", live.Name);
        var rows = await ProjectUsersAsync(db, id);
        var only = Assert.Single(rows);
        Assert.Equal(4, only.UserId);
        Assert.True(only.IsOwner);
    }

    [Fact]
    public async Task UpdateProfilesAndStatusPersistAndMissingRowsReturnNull()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);
        var before = project.UpdatedAt;

        var profiled = await store.UpdateProfilesAsync(
            id,
            """{"activeProfile":"a","profiles":[]}""");

        Assert.NotNull(profiled);
        RecordAssert.Json("""{"activeProfile":"a","profiles":[]}""", profiled.Profiles);
        Assert.True(profiled.UpdatedAt > before);

        var status = await store.UpdateStatusAsync(id, "archived");

        Assert.NotNull(status);
        Assert.Equal("archived", status.Status);

        Assert.Null(await store.UpdateProfilesAsync(999_999, "{}"));
        Assert.Null(await store.UpdateStatusAsync(999_999, "active"));
    }

    [Fact]
    public async Task AssignOwnerWritesTheOwnerRowAndDuplicateFails()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);

        await store.AssignOwnerAsync(9, id);

        var rows = await ProjectUsersAsync(db, id);
        var owner = Assert.Single(rows);
        Assert.Equal(9, owner.UserId);
        Assert.True(owner.IsOwner);
        RecordAssert.Json("[]", owner.Scopes);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.AssignOwnerAsync(9, id));
        Assert.StartsWith("failed to assign user to project: ", error.Message);
        Assert.Contains("user_projects_by_user_id_project_id", error.Message);
    }

    [Fact]
    public async Task DeleteCascadesJoinRowsRolesAndApiKeysButLeavesUserRoles()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProjectStore(context);

        var project = NewProject();
        var id = await store.CreateAsync(project);
        var other = NewProject();
        other.Name = "other";
        var otherId = await store.CreateAsync(other);

        var role = new RoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "Admin",
            Level = "project",
            Scopes = """["read_users"]""",
            ProjectId = id,
        };
        var otherRole = new RoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "Admin",
            Level = "project",
            Scopes = """["read_users"]""",
            ProjectId = otherId,
        };
        context.Roles.AddRange(role, otherRole);
        context.ApiKeys.AddRange(
            NewApiKey("sk-project", id),
            NewApiKey("sk-other", otherId));
        await context.SaveChangesAsync();

        context.UserRoles.Add(new UserRoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            UserId = 1,
            RoleId = role.Id,
        });
        await AddProjectUsersAsync(context, id, (1, true));

        Assert.True(await store.DeleteAsync(id));
        Assert.False(await store.DeleteAsync(id));

        await using var connection = await db.Factory.OpenAsync();

        var projectKeyId = await connection.ExecuteScalarAsync<long>(
            "SELECT \"id\" FROM \"api_keys\" WHERE \"key\" = 'sk-project';");
        var otherKeyId = await connection.ExecuteScalarAsync<long>(
            "SELECT \"id\" FROM \"api_keys\" WHERE \"key\" = 'sk-other';");

        // Soft deletes stamp deleted_at with Unix seconds; live rows stay 0.
        Assert.True(await DeletedAtAsync(connection, "projects", id) > 0);
        Assert.Equal(0L, await DeletedAtAsync(connection, "projects", otherId));
        Assert.True(await DeletedAtAsync(connection, "roles", role.Id) > 0);
        Assert.Equal(0L, await DeletedAtAsync(connection, "roles", otherRole.Id));
        Assert.True(await DeletedAtAsync(connection, "api_keys", projectKeyId) > 0);
        Assert.Equal(0L, await DeletedAtAsync(connection, "api_keys", otherKeyId));

        // user_projects is hard-deleted; the role's user_roles links survive
        // because Role.Delete() only soft-deletes the role itself.
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM \"user_projects\" WHERE \"project_id\" = " + id + ";"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM \"user_roles\" WHERE \"role_id\" = " + role.Id + ";"));
    }

    private static ApiKeyRecord NewApiKey(string key, long projectId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Key = key,
        Name = key,
        Type = "user",
        Status = "enabled",
        Scopes = """["read_requests"]""",
        Profiles = """{"activeProfile":"","profiles":null}""",
        AllowedIps = """[]""",
        ProjectId = projectId,
    };

    private static async Task AddProjectUsersAsync(
        AxonHubDbContext context,
        long projectId,
        params (long UserId, bool IsOwner)[] users)
    {
        foreach (var (userId, isOwner) in users)
        {
            context.UserProjects.Add(new UserProjectRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                IsOwner = isOwner,
                Scopes = "[]",
                UserId = userId,
                ProjectId = projectId,
            });
        }

        await context.SaveChangesAsync();
    }

    private sealed class UserProjectRow
    {
        public long UserId { get; set; }

        public bool IsOwner { get; set; }

        public string? Scopes { get; set; }
    }

    private static async Task<List<UserProjectRow>> ProjectUsersAsync(
        TestDatabase db,
        long projectId)
    {
        await using var connection = await db.Factory.OpenAsync();
        var rows = await connection.QueryAsync<UserProjectRow>(
            "SELECT \"user_id\" AS \"UserId\", \"is_owner\" AS \"IsOwner\", "
            + "\"scopes\"::text AS \"Scopes\" FROM \"user_projects\" "
            + "WHERE \"project_id\" = @ProjectId ORDER BY \"user_id\";",
            new { ProjectId = projectId });
        return rows.AsList();
    }

    private static async Task<long> DeletedAtAsync(
        System.Data.Common.DbConnection connection,
        string table,
        long id)
    {
        var sql = "SELECT \"deleted_at\" FROM \"" + table + "\" WHERE \"id\" = @Id;";
        var deletedAt = await connection.ExecuteScalarAsync<long>(sql, new { Id = id });
        return deletedAt;
    }

    private static ProjectRecord NewProject() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = "default",
        Description = "Default project",
        Status = "active",
        Profiles = """{"activeProfile":"default","profiles":[]}""",
    };
}
