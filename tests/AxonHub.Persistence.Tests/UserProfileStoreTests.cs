using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the self-service store against the reference eager-load shape:
/// user_roles joins roles with the role soft-delete predicate, myProjects
/// applies HasUsersWith + status = active, and unlinkOIDCIdentity is a soft
/// delete. Ent emits no ORDER BY for these collections; the port pins primary
/// key order (C164).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UserProfileStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task LoadsUserEdgesInPinnedOrderAndFiltersSoftDeletedRoles()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserProfileStore(context);

        var user = await SeedUserAsync(context, "me@example.com");
        var alpha = await SeedRoleAsync(context, "Alpha", level: "system", projectId: 0);
        var deleted = await SeedRoleAsync(
            context,
            "Deleted",
            level: "project",
            projectId: 7,
            deletedAt: 99);
        var beta = await SeedRoleAsync(context, "Beta", level: "project", projectId: 7);
        var gamma = await SeedRoleAsync(context, "Gamma", level: "project", projectId: 8);

        // The join rows are inserted out of id order on purpose: the store must
        // return the roles by role id, not by user_roles id.
        AddUserRole(context, user.Id, gamma.Id);
        AddUserRole(context, user.Id, alpha.Id);
        AddUserRole(context, user.Id, deleted.Id);
        AddUserRole(context, user.Id, beta.Id);

        var firstProject = await SeedUserProjectAsync(context, user.Id, 8, isOwner: true);
        var secondProject = await SeedUserProjectAsync(context, user.Id, 7, isOwner: false);

        var firstIdentity = await SeedOidcIdentityAsync(context, user.Id, "sub-1");
        await SeedOidcIdentityAsync(context, user.Id, "sub-deleted", deletedAt: 99);
        var secondIdentity = await SeedOidcIdentityAsync(context, user.Id, "sub-2");

        await context.SaveChangesAsync();

        var roles = await store.ListUserRolesAsync(user.Id);
        Assert.Equal(["Alpha", "Beta", "Gamma"], roles.Select(row => row.Name));

        var projects = await store.ListUserProjectsAsync(user.Id);
        Assert.Equal(
            [firstProject.Id, secondProject.Id],
            projects.Select(row => row.Id));

        var identities = await store.ListOidcIdentitiesAsync(user.Id);
        Assert.Equal(
            [firstIdentity.Id, secondIdentity.Id],
            identities.Select(row => row.Id));
        Assert.Equal("sub-1", identities[0].Subject);
    }

    [Fact]
    public async Task GetUserHidesSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserProfileStore(context);

        var live = await SeedUserAsync(context, "live@example.com");
        var deleted = await SeedUserAsync(context, "gone@example.com", deletedAt: 99);

        Assert.Equal(live.Id, (await store.GetUserAsync(live.Id))?.Id);
        Assert.Null(await store.GetUserAsync(deleted.Id));
        Assert.Null(await store.GetUserAsync(424242));
    }

    [Fact]
    public async Task ListActiveProjectsFiltersArchivedSoftDeletedAndNonMemberRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserProfileStore(context);

        var user = await SeedUserAsync(context, "member@example.com");
        var other = await SeedUserAsync(context, "other@example.com");

        var first = await SeedProjectAsync(context, "First", status: "active");
        var archived = await SeedProjectAsync(context, "Archived", status: "archived");
        var deleted = await SeedProjectAsync(context, "Deleted", status: "active", deletedAt: 99);
        var later = await SeedProjectAsync(context, "Later", status: "active");
        var notMember = await SeedProjectAsync(context, "NotMember", status: "active");

        await SeedUserProjectAsync(context, user.Id, first.Id);
        await SeedUserProjectAsync(context, user.Id, archived.Id);
        await SeedUserProjectAsync(context, user.Id, deleted.Id);
        await SeedUserProjectAsync(context, user.Id, later.Id);
        await SeedUserProjectAsync(context, other.Id, notMember.Id);
        await context.SaveChangesAsync();

        var projects = await store.ListActiveProjectsAsync(user.Id);

        Assert.Equal(["First", "Later"], projects.Select(row => row.Name));
    }

    [Fact]
    public async Task UpdateOwnProfileWritesOnlySuppliedFieldsAndReturnsTheFreshRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserProfileStore(context);

        var user = await SeedUserAsync(context, "me@example.com");
        var seededUpdatedAt = user.UpdatedAt;

        var updated = await store.UpdateOwnProfileAsync(new OwnProfileUpdate
        {
            Id = user.Id,
            FirstName = "Alice",
            Avatar = string.Empty,
        });

        Assert.NotNull(updated);
        Assert.Equal("Alice", updated.FirstName);
        Assert.Equal(string.Empty, updated.Avatar);
        Assert.Equal("Doe", updated.LastName);
        Assert.Equal("en", updated.PreferLanguage);
        Assert.Equal(user.Password, updated.Password);
        Assert.True(updated.UpdatedAt > seededUpdatedAt);

        var withPassword = await store.UpdateOwnProfileAsync(new OwnProfileUpdate
        {
            Id = user.Id,
            Password = "hashed",
        });
        Assert.NotNull(withPassword);
        Assert.Equal("hashed", withPassword.Password);
        Assert.Equal("Alice", withPassword.FirstName);

        Assert.Null(await store.UpdateOwnProfileAsync(new OwnProfileUpdate
        {
            Id = 424242,
            FirstName = "Ghost",
        }));
    }

    [Fact]
    public async Task UnlinkSoftDeletesTheIdentityAndHidesItFromReadsAndCounts()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserProfileStore(context);

        var user = await SeedUserAsync(context, "me@example.com");
        var identity = await SeedOidcIdentityAsync(context, user.Id, "sub-1");
        await context.SaveChangesAsync();

        Assert.Equal(1, await store.CountOidcIdentitiesAsync(user.Id));

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(await store.UnlinkOidcIdentityAsync(identity.Id));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        Assert.Null(await store.GetOidcIdentityAsync(identity.Id));
        Assert.Equal(0, await store.CountOidcIdentitiesAsync(user.Id));

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.OidcIdentities.SingleAsync(row => row.Id == identity.Id);
        Assert.InRange(reloaded.DeletedAt, before, after);
        Assert.True(reloaded.UpdatedAt > UpdatedAt);

        Assert.False(await store.UnlinkOidcIdentityAsync(identity.Id));
        Assert.False(await store.UnlinkOidcIdentityAsync(424242));
    }

    [Fact]
    public async Task IssuerSubjectIndexIsUniquePerLiveRowSet()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var connection = await db.Factory.OpenAsync();

        const string Insert =
            "INSERT INTO \"oidc_identities\" (\"issuer\", \"subject\", \"deleted_at\", \"user_id\")"
            + " VALUES (@Issuer, @Subject, @DeletedAt, @UserId);";

        Assert.Equal(1, await connection.ExecuteAsync(Insert, new
        {
            Issuer = "https://issuer.example.com",
            Subject = "sub-1",
            DeletedAt = 0L,
            UserId = 9L,
        }));

        await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
            Insert,
            new
            {
                Issuer = "https://issuer.example.com",
                Subject = "sub-1",
                DeletedAt = 0L,
                UserId = 9L,
            }));

        // The index includes deleted_at, so the soft-deleted pair is free.
        Assert.Equal(1, await connection.ExecuteAsync(
            "UPDATE \"oidc_identities\" SET \"deleted_at\" = 123"
            + " WHERE \"issuer\" = @Issuer AND \"subject\" = @Subject;",
            new { Issuer = "https://issuer.example.com", Subject = "sub-1" }));

        Assert.Equal(1, await connection.ExecuteAsync(Insert, new
        {
            Issuer = "https://issuer.example.com",
            Subject = "sub-1",
            DeletedAt = 0L,
            UserId = 9L,
        }));
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM \"oidc_identities\";"));
    }

    private static void AddUserRole(AxonHubDbContext context, long userId, long roleId)
        => context.UserRoles.Add(new UserRoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            UserId = userId,
            RoleId = roleId,
        });

    private static async Task<UserRecord> SeedUserAsync(
        AxonHubDbContext context,
        string email,
        long deletedAt = 0)
    {
        var user = new UserRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Email = email,
            Status = "activated",
            PreferLanguage = "en",
            Password = "hashed",
            FirstName = "John",
            LastName = "Doe",
            IsOwner = false,
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static async Task<RoleRecord> SeedRoleAsync(
        AxonHubDbContext context,
        string name,
        string level,
        long? projectId,
        long deletedAt = 0)
    {
        var role = new RoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Name = name,
            Level = level,
            Scopes = "[]",
            ProjectId = projectId,
        };

        context.Roles.Add(role);
        await context.SaveChangesAsync();
        return role;
    }

    private static async Task<UserProjectRecord> SeedUserProjectAsync(
        AxonHubDbContext context,
        long userId,
        long projectId,
        bool isOwner = false)
    {
        var userProject = new UserProjectRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            IsOwner = isOwner,
            Scopes = "[]",
            UserId = userId,
            ProjectId = projectId,
        };

        context.UserProjects.Add(userProject);
        await context.SaveChangesAsync();
        return userProject;
    }

    private static async Task<ProjectRecord> SeedProjectAsync(
        AxonHubDbContext context,
        string name,
        string status,
        long deletedAt = 0)
    {
        var project = new ProjectRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Name = name,
            Description = string.Empty,
            Status = status,
        };

        context.Projects.Add(project);
        await context.SaveChangesAsync();
        return project;
    }

    private static async Task<OidcIdentityRecord> SeedOidcIdentityAsync(
        AxonHubDbContext context,
        long userId,
        string subject,
        long deletedAt = 0)
    {
        var identity = NewOidcIdentity(userId, subject);
        identity.DeletedAt = deletedAt;

        context.OidcIdentities.Add(identity);
        await context.SaveChangesAsync();
        return identity;
    }

    private static OidcIdentityRecord NewOidcIdentity(long userId, string subject) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = 0,
        Issuer = "https://issuer.example.com",
        Subject = subject,
        Email = "me@example.com",
        IdpName = "Example",
        UserId = userId,
    };
}
