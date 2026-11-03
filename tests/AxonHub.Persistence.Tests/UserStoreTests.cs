using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UserStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserStore(context);

        var user = NewUser();
        var id = await store.CreateAsync(user);

        Assert.True(id > 0);

        user.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(user, actual);
    }

    [Fact]
    public async Task FindOwnerReturnsTheLowestLiveOwner()
    {
        // api_key.EnsureNoAuthAPIKey resolves the owner with
        // User.Query().Where(IsOwnerEQ(true)).First.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserStore(context);

        var member = NewUser();
        member.Email = "member@example.com";
        await store.CreateAsync(member);

        var owner = NewUser();
        owner.Email = "owner@example.com";
        owner.IsOwner = true;
        var ownerId = await store.CreateAsync(owner);

        var secondOwner = NewUser();
        secondOwner.Email = "second-owner@example.com";
        secondOwner.IsOwner = true;
        await store.CreateAsync(secondOwner);

        var actual = await store.FindOwnerAsync();

        Assert.NotNull(actual);
        Assert.Equal(ownerId, actual.Id);
        Assert.True(actual.IsOwner);
    }

    [Fact]
    public async Task SoftDeletedOwnersAreInvisible()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserStore(context);

        var owner = NewUser();
        owner.IsOwner = true;
        var ownerId = await store.CreateAsync(owner);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"users\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = ownerId });
        }

        Assert.Null(await store.GetByIdAsync(ownerId));
        Assert.Null(await store.FindOwnerAsync());
    }

    [Fact]
    public async Task LiveEmailsAreUniqueAndSoftDeletedEmailsCanBeReused()
    {
        // user_email_deleted_at is unique on (email, deleted_at), mirroring Ent.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserStore(context);

        var user = NewUser();
        var firstId = await store.CreateAsync(user);

        var duplicate = NewUser();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(duplicate));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"users\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        var reusedId = await store.CreateAsync(duplicate);
        Assert.True(reusedId > firstId);
    }

    [Fact]
    public async Task GetActivatedByEmailMatchesTheExactLiveActivatedRow()
    {
        // AuthService.AuthenticateUser queries EmailEQ(email) AND
        // StatusEQ(activated); Both predicates are part of the lookup.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UserStore(context);

        var active = NewUser();
        active.Email = "active@example.com";
        var activeId = await store.CreateAsync(active);

        var deactivated = NewUser();
        deactivated.Email = "inactive@example.com";
        deactivated.Status = "deactivated";
        await store.CreateAsync(deactivated);

        var deleted = NewUser();
        deleted.Email = "deleted@example.com";
        deleted.DeletedAt = 1_700_000_000;
        await store.CreateAsync(deleted);

        Assert.Equal(activeId, (await store.GetActivatedByEmailAsync("active@example.com"))?.Id);
        Assert.Null(await store.GetActivatedByEmailAsync("inactive@example.com"));
        Assert.Null(await store.GetActivatedByEmailAsync("deleted@example.com"));
        Assert.Null(await store.GetActivatedByEmailAsync("Active@example.com"));
        Assert.Null(await store.GetActivatedByEmailAsync("missing@example.com"));
    }

    private static UserRecord NewUser() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Email = "owner@example.com",
        Status = "activated",
        PreferLanguage = "zh",
        Password = "$2a$10$hashed",
        FirstName = "Geff",
        LastName = "Zhang",
        Avatar = "https://example.com/avatar.png",
        IsOwner = false,
        Scopes = """["read_channels","write_requests"]""",
    };
}
