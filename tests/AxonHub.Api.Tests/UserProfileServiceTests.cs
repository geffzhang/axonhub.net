using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the self-service service against me.resolvers.go and
/// ConvertUserToUserInfo (internal/server/biz/user.go:296-394): the exact
/// error prefixes, the OIDC-only password flow, the last-identity guard, and
/// the scope/role projection. Unordered reference collections are pinned
/// ordinally here (C164).
/// </summary>
public sealed class UserProfileServiceTests
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly FakeUserProfileStore _store = new();
    private readonly FakeManagementIdentity _identity = new(new ManagementUser(9, IsOwner: true));
    private readonly FakePasswordHasher _passwords = new();

    private UserProfileService Service => new(_store, _identity, _passwords);

    // ---------------------------------------------------------------------
    // me
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MeBuildsUserInfoWithGlobalRolesProjectsAndIdentities()
    {
        _store.User = NewUser(scopes: """["system:read"]""");
        _store.Roles.Add(NewRole(1, "Viewer", projectId: 0, scopes: """["system:read","shared"]"""));
        _store.Roles.Add(NewRole(2, "ProjectRole", projectId: 7, scopes: """["p:read","shared"]"""));
        _store.UserProjects.Add(NewUserProject(11, projectId: 7, scopes: """["p:read","p:write"]"""));
        _store.OidcIdentities.Add(NewIdentity(5, userId: 9));

        var me = await Service.GetMeAsync();

        Assert.Equal(9, me.Id);
        Assert.Equal("me@example.com", me.Email);
        Assert.Equal("John", me.FirstName);
        Assert.Equal("Doe", me.LastName);
        Assert.True(me.IsOwner);
        Assert.Equal("en", me.PreferLanguage);
        Assert.Equal("avatar.png", me.Avatar);
        Assert.True(me.HasPassword);

        // User scopes ∪ system-role scopes, sorted ordinally (C164).
        Assert.Equal(["shared", "system:read"], me.Scopes);

        // Only system roles are promoted to UserInfo.roles.
        Assert.Equal(["Viewer"], me.Roles.Select(role => role.Name));

        var project = Assert.Single(me.Projects);
        Assert.Equal(7, project.ProjectId);
        Assert.False(project.IsOwner);
        Assert.Equal(["p:read", "p:write"], project.Scopes);
        Assert.Equal(["p:read", "p:write", "shared"], project.EffectiveScopes);
        Assert.Equal(["ProjectRole"], project.Roles.Select(role => role.Name));

        var identity = Assert.Single(me.OidcIdentities);
        Assert.Equal(5, identity.Id);
        Assert.Equal("Example", identity.IdpName);
        Assert.Equal("https://issuer.example.com", identity.Issuer);
        Assert.Equal("subject-5", identity.Subject);
        Assert.Equal("oidc@example.com", identity.Email);
    }

    [Fact]
    public async Task MeTreatsProjectIdZeroOrNullAsSystemRoleRegardlessOfLevel()
    {
        // biz.Role.IsSystemRole keys on ProjectID, not on the level label.
        _store.User = NewUser();
        _store.Roles.Add(NewRole(1, "Zero", projectId: 0, level: "project"));
        _store.Roles.Add(NewRole(2, "Null", projectId: null, level: "project"));

        var me = await Service.GetMeAsync();

        Assert.Equal(["Zero", "Null"], me.Roles.Select(role => role.Name));
        Assert.Empty(me.Projects);
    }

    [Fact]
    public async Task MeGivesProjectsWithoutRolesOnlyTheirMembershipScopes()
    {
        _store.User = NewUser();
        _store.Roles.Add(NewRole(1, "OnlySeven", projectId: 7, scopes: """["role:scope"]"""));
        _store.UserProjects.Add(NewUserProject(11, projectId: 7, scopes: """["seven"]"""));
        _store.UserProjects.Add(NewUserProject(12, projectId: 8, scopes: """["eight"]"""));

        var me = await Service.GetMeAsync();

        Assert.Equal([7L, 8L], me.Projects.Select(project => project.ProjectId));

        Assert.Equal(["OnlySeven"], me.Projects[0].Roles.Select(role => role.Name));
        Assert.Equal(["role:scope", "seven"], me.Projects[0].EffectiveScopes);

        Assert.Empty(me.Projects[1].Roles);
        Assert.Equal(["eight"], me.Projects[1].Scopes);
        Assert.Equal(["eight"], me.Projects[1].EffectiveScopes);
    }

    [Fact]
    public async Task MeNeverReturnsANullAvatarAndDerivesHasPasswordFromThePlaceholder()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder, avatar: null);
        _store.OidcIdentities.Add(new OidcIdentityRecord
        {
            Id = 5,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Issuer = "https://issuer.example.com",
            Subject = "subject-5",
            Email = null,
            IdpName = null,
            UserId = 9,
        });

        var me = await Service.GetMeAsync();

        Assert.Equal(string.Empty, me.Avatar);
        Assert.False(me.HasPassword);
        Assert.Equal(string.Empty, me.OidcIdentities[0].IdpName);
        Assert.Equal(string.Empty, me.OidcIdentities[0].Email);
    }

    [Fact]
    public async Task MeRequiresAUserInContext()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetMeAsync());

        Assert.Equal("user not found in context", error.Message);
    }

    [Fact]
    public async Task MeWrapsEveryLoadFailureWithBothPrefixes()
    {
        _store.GetUserError = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetMeAsync());

        Assert.Equal("failed to get user details: failed to get user: boom", error.Message);
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task MeReportsAMissingUserAsEntNotFound()
    {
        _store.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetMeAsync());

        Assert.Equal(
            "failed to get user details: failed to get user: ent: user not found",
            error.Message);
    }

    [Fact]
    public async Task MeWrapsEdgeLoadFailuresTheSameWay()
    {
        _store.User = NewUser();
        _store.ListRolesError = new InvalidOperationException("roles down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetMeAsync());

        Assert.Equal(
            "failed to get user details: failed to get user: roles down",
            error.Message);
    }

    // ---------------------------------------------------------------------
    // myProjects
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MyProjectsReturnsTheActiveProjectsOfTheCurrentUser()
    {
        var first = NewProject(1, "First");
        var second = NewProject(2, "Second");
        _store.ActiveProjects.Add(first);
        _store.ActiveProjects.Add(second);

        var projects = await Service.GetMyProjectsAsync();

        Assert.Equal(["First", "Second"], projects.Select(project => project.Name));
        Assert.Equal(9, _store.LastProjectsUserId);
    }

    [Fact]
    public async Task MyProjectsRequiresAUserInContext()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.GetMyProjectsAsync());

        Assert.Equal("user not found in context", error.Message);
        Assert.Empty(_store.Calls);
    }

    // ---------------------------------------------------------------------
    // updateMe
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateMePassesOnlyTheSuppliedFields()
    {
        _store.User = NewUser();
        _store.UpdateResult = NewUser(email: "updated@example.com");

        var updated = await Service.UpdateMeAsync(new UpdateMeCommand
        {
            FirstName = "Alice",
            PreferLanguage = "zh",
            Avatar = string.Empty,
        });

        Assert.Equal("updated@example.com", updated.Email);
        var write = Assert.IsType<OwnProfileUpdate>(_store.LastUpdate);
        Assert.Equal(9, write.Id);
        Assert.Equal("Alice", write.FirstName);
        Assert.Null(write.LastName);
        Assert.Equal("zh", write.PreferLanguage);
        Assert.Equal(string.Empty, write.Avatar);
        Assert.Null(write.Password);
    }

    [Fact]
    public async Task UpdateMeRequiresAUserInContext()
    {
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMeAsync(new UpdateMeCommand { FirstName = "Alice" }));

        Assert.Equal("user not found in context", error.Message);
    }

    [Fact]
    public async Task UpdateMeWrapsStoreFailuresAndMissingRows()
    {
        _store.UpdateError = new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMeAsync(new UpdateMeCommand { FirstName = "Alice" }));

        Assert.Equal("failed to update user profile: db down", error.Message);

        _store.UpdateError = null;
        _store.UpdateResult = null;

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMeAsync(new UpdateMeCommand { FirstName = "Alice" }));

        Assert.Equal("failed to update user profile: ent: user not found", error.Message);
    }

    // ---------------------------------------------------------------------
    // updateMyPassword
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateMyPasswordLetsAnOidcOnlyUserSetTheFirstPassword()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _store.UpdateResult = _store.User;
        _passwords.HashResult = "hashed:new";

        var result = await Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
        {
            NewPassword = "new-secret",
        });

        Assert.True(result);
        Assert.Empty(_passwords.Verifications);
        Assert.Equal(["new-secret"], _passwords.HashedPasswords);
        var write = Assert.IsType<OwnProfileUpdate>(_store.LastUpdate);
        Assert.Equal("hashed:new", write.Password);
        Assert.Null(write.FirstName);
    }

    [Fact]
    public async Task UpdateMyPasswordRequiresTheOldPasswordForLocalUsers()
    {
        _store.User = NewUser(password: "stored-hash");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
            {
                NewPassword = "new-secret",
            }));

        Assert.Equal("current password is required", error.Message);
        Assert.Empty(_passwords.HashedPasswords);
        Assert.Null(_store.LastUpdate);
    }

    [Fact]
    public async Task UpdateMyPasswordFoldsVerifyFailuresIntoIncorrectOldPassword()
    {
        _store.User = NewUser(password: "stored-hash");
        _passwords.VerifyError = new InvalidOperationException(
            "crypto/bcrypt: hashedPassword is not the hash of the given password");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
            {
                OldPassword = "wrong",
                NewPassword = "new-secret",
            }));

        Assert.Equal("incorrect old password", error.Message);
        Assert.Empty(_passwords.HashedPasswords);
        Assert.Null(_store.LastUpdate);
    }

    [Fact]
    public async Task UpdateMyPasswordVerifiesTheStoredHashAndStoresTheNewOne()
    {
        _store.User = NewUser(password: "stored-hash");
        _store.UpdateResult = _store.User;
        _passwords.HashResult = "hashed:new";

        var result = await Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
        {
            OldPassword = "old-secret",
            NewPassword = "new-secret",
        });

        Assert.True(result);
        Assert.Equal(("stored-hash", "old-secret"), Assert.Single(_passwords.Verifications));
        Assert.Equal("hashed:new", _store.LastUpdate?.Password);
    }

    [Fact]
    public async Task UpdateMyPasswordPropagatesHasherFailuresUnwrapped()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _passwords.HashError = new InvalidOperationException(
            "failed to hash password: bcrypt: password length exceeds 72 bytes");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
            {
                NewPassword = new string('x', 73),
            }));

        Assert.Equal(
            "failed to hash password: bcrypt: password length exceeds 72 bytes",
            error.Message);
        Assert.Null(_store.LastUpdate);
    }

    [Fact]
    public async Task UpdateMyPasswordWrapsStoreFailuresAndMissingRows()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _store.UpdateError = new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
            {
                NewPassword = "new-secret",
            }));

        Assert.Equal("failed to update user profile: db down", error.Message);

        _store.UpdateError = null;
        _store.UpdateResult = null;

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
            {
                NewPassword = "new-secret",
            }));

        Assert.Equal("failed to update user profile: ent: user not found", error.Message);
    }

    // ---------------------------------------------------------------------
    // unlinkOIDCIdentity
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UnlinkRejectsAnIdentityOfAnotherUser()
    {
        _store.OidcIdentity = NewIdentity(5, userId: 999);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));

        Assert.Equal(
            "permission denied: this identity does not belong to you",
            error.Message);
        Assert.Null(_store.UnlinkedIdentityId);
    }

    [Fact]
    public async Task UnlinkProtectsTheLastIdentityOfAnOidcOnlyUser()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _store.OidcIdentity = NewIdentity(5, userId: 9);
        _store.OidcIdentityCount = 1;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));

        Assert.Equal(
            "please set a local password before unlinking your last OIDC identity",
            error.Message);
        Assert.Null(_store.UnlinkedIdentityId);
    }

    [Fact]
    public async Task UnlinkAllowsAnOidcOnlyUserWithAnotherIdentityLeft()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _store.OidcIdentity = NewIdentity(5, userId: 9);
        _store.OidcIdentityCount = 2;

        var result = await Service.UnlinkOidcIdentityAsync(5);

        Assert.True(result);
        Assert.Equal(5, _store.UnlinkedIdentityId);
    }

    [Fact]
    public async Task UnlinkIgnoresTheGuardForUsersWithALocalPassword()
    {
        _store.User = NewUser(password: "stored-hash");
        _store.OidcIdentity = NewIdentity(5, userId: 9);
        _store.OidcIdentityCount = 1;

        var result = await Service.UnlinkOidcIdentityAsync(5);

        Assert.True(result);
        Assert.Equal(5, _store.UnlinkedIdentityId);
        Assert.Equal(0, _store.CountCalls);
    }

    [Fact]
    public async Task UnlinkWrapsLookupCountAndDeleteFailures()
    {
        _store.GetOidcIdentityError = new InvalidOperationException("lookup down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));
        Assert.Equal("failed to get identity: lookup down", error.Message);

        _store.GetOidcIdentityError = null;
        _store.OidcIdentity = null;

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));
        Assert.Equal("failed to get identity: ent: oidc_identity not found", error.Message);

        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _store.OidcIdentity = NewIdentity(5, userId: 9);
        _store.CountError = new InvalidOperationException("count down");

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));
        Assert.Equal("failed to count identities: count down", error.Message);

        _store.CountError = null;
        _store.OidcIdentityCount = 2;
        _store.UnlinkError = new InvalidOperationException("delete down");

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));
        Assert.Equal("failed to unlink identity: delete down", error.Message);
    }

    [Fact]
    public async Task UnlinkReportsADeletedRowAsEntNotFound()
    {
        _store.User = NewUser(password: "stored-hash");
        _store.OidcIdentity = NewIdentity(5, userId: 9);
        _store.UnlinkResult = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service.UnlinkOidcIdentityAsync(5));

        Assert.Equal("failed to unlink identity: ent: oidc_identity not found", error.Message);
    }

    [Fact]
    public async Task EverySelfServiceEntryPointRequiresAUserInContext()
    {
        _identity.User = null;
        _store.User = NewUser();
        _store.OidcIdentity = NewIdentity(5, userId: 9);

        foreach (var call in new Func<Task>[]
                 {
                     () => Service.GetMeAsync(),
                     () => Service.GetMyProjectsAsync(),
                     () => Service.UpdateMeAsync(new UpdateMeCommand()),
                     () => Service.UpdateMyPasswordAsync(new UpdateMyPasswordCommand
                     {
                         NewPassword = "new-secret",
                     }),
                     () => Service.UnlinkOidcIdentityAsync(5),
                 })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Equal("user not found in context", error.Message);
        }
    }

    private static UserRecord NewUser(
        string email = "me@example.com",
        string password = "stored-hash",
        string? scopes = null,
        string? avatar = "avatar.png") => new()
    {
        Id = 9,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Email = email,
        Status = "activated",
        PreferLanguage = "en",
        Password = password,
        FirstName = "John",
        LastName = "Doe",
        Avatar = avatar,
        IsOwner = true,
        Scopes = scopes ?? "[]",
    };

    private static RoleRecord NewRole(
        long id,
        string name,
        long? projectId,
        string level = "system",
        string scopes = "[]") => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = name,
        Level = level,
        Scopes = scopes,
        ProjectId = projectId,
    };

    private static UserProjectRecord NewUserProject(
        long id,
        long projectId,
        string scopes = "[]") => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        IsOwner = false,
        Scopes = scopes,
        UserId = 9,
        ProjectId = projectId,
    };

    private static ProjectRecord NewProject(long id, string name) => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = name,
        Description = string.Empty,
        Status = "active",
        Profiles = "null",
    };

    private static OidcIdentityRecord NewIdentity(long id, long userId) => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Issuer = "https://issuer.example.com",
        Subject = "subject-" + id,
        Email = "oidc@example.com",
        IdpName = "Example",
        UserId = userId,
    };
}

/// <summary>In-memory IUserProfileStore with per-method failure hooks.</summary>
internal sealed class FakeUserProfileStore : IUserProfileStore
{
    public UserRecord? User { get; set; }

    public UserRecord? UpdateResult { get; set; }

    public List<RoleRecord> Roles { get; } = [];

    public List<UserProjectRecord> UserProjects { get; } = [];

    public List<OidcIdentityRecord> OidcIdentities { get; } = [];

    public List<ProjectRecord> ActiveProjects { get; } = [];

    public OidcIdentityRecord? OidcIdentity { get; set; }

    public int OidcIdentityCount { get; set; }

    public bool UnlinkResult { get; set; } = true;

    public OwnProfileUpdate? LastUpdate { get; private set; }

    public long? UnlinkedIdentityId { get; private set; }

    public long? LastProjectsUserId { get; private set; }

    public int CountCalls { get; private set; }

    public List<string> Calls { get; } = [];

    public Exception? GetUserError { get; set; }

    public Exception? ListRolesError { get; set; }

    public Exception? GetOidcIdentityError { get; set; }

    public Exception? CountError { get; set; }

    public Exception? UpdateError { get; set; }

    public Exception? UnlinkError { get; set; }

    public Task<UserRecord?> GetUserAsync(long id, CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetUserAsync));
        if (GetUserError is not null)
        {
            throw GetUserError;
        }

        return Task.FromResult(User);
    }

    public Task<IReadOnlyList<RoleRecord>> ListUserRolesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ListUserRolesAsync));
        if (ListRolesError is not null)
        {
            throw ListRolesError;
        }

        return Task.FromResult<IReadOnlyList<RoleRecord>>(Roles);
    }

    public Task<IReadOnlyList<UserProjectRecord>> ListUserProjectsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ListUserProjectsAsync));
        return Task.FromResult<IReadOnlyList<UserProjectRecord>>(UserProjects);
    }

    public Task<IReadOnlyList<OidcIdentityRecord>> ListOidcIdentitiesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ListOidcIdentitiesAsync));
        return Task.FromResult<IReadOnlyList<OidcIdentityRecord>>(OidcIdentities);
    }

    public Task<IReadOnlyList<ProjectRecord>> ListActiveProjectsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(ListActiveProjectsAsync));
        LastProjectsUserId = userId;
        return Task.FromResult<IReadOnlyList<ProjectRecord>>(ActiveProjects);
    }

    public Task<UserRecord?> UpdateOwnProfileAsync(
        OwnProfileUpdate update,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(UpdateOwnProfileAsync));
        if (UpdateError is not null)
        {
            throw UpdateError;
        }

        LastUpdate = update;
        return Task.FromResult(UpdateResult);
    }

    public Task<OidcIdentityRecord?> GetOidcIdentityAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(GetOidcIdentityAsync));
        if (GetOidcIdentityError is not null)
        {
            throw GetOidcIdentityError;
        }

        return Task.FromResult(OidcIdentity);
    }

    public Task<int> CountOidcIdentitiesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(CountOidcIdentitiesAsync));
        CountCalls++;
        if (CountError is not null)
        {
            throw CountError;
        }

        return Task.FromResult(OidcIdentityCount);
    }

    public Task<bool> UnlinkOidcIdentityAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(nameof(UnlinkOidcIdentityAsync));
        if (UnlinkError is not null)
        {
            throw UnlinkError;
        }

        if (!UnlinkResult)
        {
            return Task.FromResult(false);
        }

        UnlinkedIdentityId = id;
        return Task.FromResult(true);
    }
}

/// <summary>Recording IPasswordHasher with failure hooks.</summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public Exception? VerifyError { get; set; }

    public Exception? HashError { get; set; }

    public string HashResult { get; set; } = "hashed";

    public List<(string Hashed, string Password)> Verifications { get; } = [];

    public List<string> HashedPasswords { get; } = [];

    public string Hash(string password)
    {
        HashedPasswords.Add(password);
        if (HashError is not null)
        {
            throw HashError;
        }

        return HashResult;
    }

    public void Verify(string hashedPassword, string password)
    {
        Verifications.Add((hashedPassword, password));
        if (VerifyError is not null)
        {
            throw VerifyError;
        }
    }
}
