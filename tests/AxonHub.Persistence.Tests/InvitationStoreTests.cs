using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks InvitationStore against the reference ent behaviour: the live-row
/// predicates, the WithProject edge, the RegisterInvitation transaction
/// (conditional use-count update, user + membership + role link) and the
/// soft-delete revocation of revokeRoleInvitations (invitation.go:146-260,
/// role.go:258-265).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InvitationStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByTokenHashRoundTripsEveryColumnAndTheProjectEdge()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(
            projectId,
            roleId: 42,
            expiresAt: new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc));

        await store.CreateAsync(invitation);

        var actual = await store.GetByTokenHashAsync(invitation.TokenHash);

        Assert.NotNull(actual);
        Assert.Equal("project", actual.ProjectName);
        RecordAssert.Equivalent(invitation, actual.Invitation);
    }

    [Fact]
    public async Task CreateFillsDefaultTimestamps()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new InvitationStore(context);

        var invitation = new InvitationRecord
        {
            TokenHash = "default-timestamps",
            ProjectId = 1,
            RoleId = 2,
            MaxUses = 1,
        };

        await store.CreateAsync(invitation);

        Assert.True(invitation.Id > 0);
        Assert.True(invitation.CreatedAt > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(invitation.CreatedAt, invitation.UpdatedAt);
    }

    [Fact]
    public async Task GetByTokenHashReturnsNullForUnknownOrSoftDeletedInvitations()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId: 1, roleId: 2);
        await store.CreateAsync(invitation);

        Assert.Null(await store.GetByTokenHashAsync("unknown"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"invitations\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = invitation.Id });
        }

        Assert.Null(await store.GetByTokenHashAsync(invitation.TokenHash));
    }

    [Fact]
    public async Task SoftDeletedProjectsYieldNoEdgeButKeepTheInvitationRow()
    {
        // The reference eager-loads WithProject under the SoftDeleteMixin
        // predicate and then dereferences the missing edge (a panic); the port
        // reports a null name and the service turns it into "" (C201).
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "gone-project");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId: 2);
        await store.CreateAsync(invitation);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"projects\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = projectId });
        }

        var actual = await store.GetByTokenHashAsync(invitation.TokenHash);

        Assert.NotNull(actual);
        Assert.Null(actual.ProjectName);
    }

    [Fact]
    public async Task RegisterCreatesTheUserMembershipRoleLinkAndConsumesOneUse()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var roleId = await SeedRoleAsync(context, projectId, "write_requests");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId);
        await store.CreateAsync(invitation);

        var user = await store.RegisterAsync(new InvitationRegistration
        {
            TokenHash = invitation.TokenHash,
            Email = "member@example.com",
            PasswordHash = "hashed-password",
            FirstName = "Member",
            LastName = "User",
        });

        Assert.True(user.Id > 0);

        var stored = await new UserAdminStore(context).GetByIdAsync(user.Id);
        Assert.NotNull(stored);
        Assert.Equal("member@example.com", stored.Email);
        Assert.Equal("hashed-password", stored.Password);
        Assert.Equal("Member", stored.FirstName);
        Assert.Equal("User", stored.LastName);
        Assert.Equal("activated", stored.Status);
        Assert.Equal("en", stored.PreferLanguage);
        Assert.False(stored.IsOwner);

        await using var connection = await db.Factory.OpenAsync();
        Assert.True(await connection.ExecuteScalarAsync<bool>(
            """
            SELECT "is_owner" FROM "user_projects"
            WHERE "user_id" = @UserId AND "project_id" = @ProjectId;
            """,
            new { UserId = user.Id, ProjectId = projectId }));
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>(
            """
            SELECT count(*) FROM "user_roles"
            WHERE "user_id" = @UserId AND "role_id" = @RoleId;
            """,
            new { UserId = user.Id, RoleId = roleId }));
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>(
            "SELECT \"used_count\" FROM \"invitations\" WHERE \"id\" = @Id;",
            new { Id = invitation.Id }));
    }

    [Fact]
    public async Task RegisterRejectsTheSecondUseOfASingleUseInvitation()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var roleId = await SeedRoleAsync(context, projectId, "write_requests");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId);
        await store.CreateAsync(invitation);

        await store.RegisterAsync(Registration(invitation.TokenHash, "first@example.com"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAsync(Registration(invitation.TokenHash, "second@example.com")));

        Assert.Equal("invitation is no longer valid", error.Message);
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM \"users\" WHERE \"email\" = 'second@example.com';"));
    }

    [Fact]
    public async Task UnlimitedInvitationRegistersEveryTime()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var roleId = await SeedRoleAsync(context, projectId, "write_requests");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId, maxUses: 0);
        await store.CreateAsync(invitation);

        var first = await store.RegisterAsync(
            Registration(invitation.TokenHash, "first@example.com"));
        var second = await store.RegisterAsync(
            Registration(invitation.TokenHash, "second@example.com"));

        Assert.NotEqual(first.Id, second.Id);
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(2L, await connection.ExecuteScalarAsync<long>(
            "SELECT \"used_count\" FROM \"invitations\" WHERE \"id\" = @Id;",
            new { Id = invitation.Id }));
    }

    [Fact]
    public async Task RegisterRejectsAnExpiredInvitation()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new InvitationStore(context);
        var invitation = NewInvitation(
            projectId: 1,
            roleId: 2,
            expiresAt: DateTime.UtcNow.AddHours(-1));
        await store.CreateAsync(invitation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAsync(Registration(invitation.TokenHash, "member@example.com")));

        Assert.Equal("invitation is no longer valid", error.Message);
    }

    [Fact]
    public async Task RegisterRejectsALegacyInvitationWithoutARole()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId: 1, roleId: null);
        await store.CreateAsync(invitation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAsync(Registration(invitation.TokenHash, "member@example.com")));

        Assert.Equal("invitation role is required", error.Message);
    }

    [Fact]
    public async Task RegisterRejectsASoftDeletedRole()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var roleId = await SeedRoleAsync(context, projectId, "write_requests");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId);
        await store.CreateAsync(invitation);
        await new RoleStore(context).DeleteAsync(roleId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAsync(Registration(invitation.TokenHash, "member@example.com")));

        Assert.Equal("invitation role is no longer available", error.Message);
    }

    [Fact]
    public async Task RegisterRejectsARoleOfAnotherProject()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var otherProjectId = await SeedProjectAsync(context, "other-project");
        var otherRoleId = await SeedRoleAsync(context, otherProjectId, "write_requests");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, otherRoleId);
        await store.CreateAsync(invitation);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAsync(Registration(invitation.TokenHash, "member@example.com")));

        Assert.Equal("invitation role is no longer available", error.Message);
    }

    [Fact]
    public async Task RegisterRejectsAnExistingEmail()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var roleId = await SeedRoleAsync(context, projectId, "write_requests");
        var store = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId);
        await store.CreateAsync(invitation);
        await new UserAdminStore(context).CreateAsync(new UserRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Email = "taken@example.com",
            Status = "activated",
        }, null);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.RegisterAsync(Registration(invitation.TokenHash, "taken@example.com")));

        Assert.Equal("a user with this email already exists", error.Message);
    }

    [Fact]
    public async Task DeleteByRoleIdsSoftDeletesOnlyLiveInvitationsForThoseRoles()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var firstRoleId = await SeedRoleAsync(context, projectId, "write_requests");
        var secondRoleId = await SeedRoleAsync(context, projectId, "write_requests");
        var store = new InvitationStore(context);
        var first = NewInvitation(projectId, firstRoleId);
        var second = NewInvitation(projectId, secondRoleId);
        await store.CreateAsync(first);
        await store.CreateAsync(second);

        var affected = await store.DeleteByRoleIdsAsync([firstRoleId]);

        Assert.Equal(1, affected);
        await using var connection = await db.Factory.OpenAsync();
        Assert.NotEqual(0L, await connection.ExecuteScalarAsync<long>(
            "SELECT \"deleted_at\" FROM \"invitations\" WHERE \"id\" = @Id;",
            new { Id = first.Id }));
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>(
            "SELECT \"deleted_at\" FROM \"invitations\" WHERE \"id\" = @Id;",
            new { Id = second.Id }));

        Assert.Equal(0, await store.DeleteByRoleIdsAsync([firstRoleId]));
        Assert.Equal(0, await store.DeleteByRoleIdsAsync([]));
    }

    [Fact]
    public async Task DeleteByRoleIdsJoinsTheAmbientRoleDeletionTransaction()
    {
        // RoleService.DeleteRole wraps revokeRoleInvitations and the role soft
        // delete in one transaction; a later failure must roll the revocation
        // back (role.go:181-226).
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var projectId = await SeedProjectAsync(context, "project");
        var roleId = await SeedRoleAsync(context, projectId, "write_requests");
        var roles = new RoleStore(context);
        var invitations = new InvitationStore(context);
        var invitation = NewInvitation(projectId, roleId);
        await invitations.CreateAsync(invitation);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => roles.RunInTransactionAsync(
                async token =>
                {
                    await invitations.DeleteByRoleIdsAsync([roleId], token);
                    throw new InvalidOperationException("boom");
                },
                CancellationToken.None));

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>(
            "SELECT \"deleted_at\" FROM \"invitations\" WHERE \"id\" = @Id;",
            new { Id = invitation.Id }));
    }

    // ---------------- helpers ----------------

    private static InvitationRegistration Registration(string tokenHash, string email) => new()
    {
        TokenHash = tokenHash,
        Email = email,
        PasswordHash = "hashed-password",
        FirstName = "Member",
        LastName = "User",
    };

    private static InvitationRecord NewInvitation(
        long projectId,
        long? roleId,
        DateTime? expiresAt = null,
        long maxUses = 1) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        TokenHash = "hash-" + Guid.NewGuid().ToString("N"),
        ProjectId = projectId,
        RoleId = roleId,
        ExpiresAt = expiresAt,
        MaxUses = maxUses,
        UsedCount = 0,
    };

    private static Task<long> SeedProjectAsync(AxonHubDbContext context, string name)
        => new ProjectStore(context).CreateAsync(new ProjectRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = name,
            Status = "active",
        });

    private static Task<long> SeedRoleAsync(
        AxonHubDbContext context,
        long projectId,
        string scope)
        => new RoleStore(context).CreateAsync(new RoleRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "Developer",
            Level = "project",
            ProjectId = projectId,
            Scopes = System.Text.Json.JsonSerializer.Serialize(new[] { scope }),
        });
}
