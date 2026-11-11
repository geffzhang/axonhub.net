using System.Buffers.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/biz/invitation_test.go plus the reference
/// validation messages of InvitationService.CreateInvitation/GetInvitation/
/// RegisterInvitation (internal/server/biz/invitation.go:65-335).
/// </summary>
public sealed class InvitationServiceTests
{
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeInvitationStore _invitations = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeManagementIdentity _identity = new(null);
    private readonly InvitationService _service;

    public InvitationServiceTests()
    {
        var validator = new PermissionValidator(_identity, _users, _roles);
        _service = new InvitationService(
            _invitations,
            _projects,
            _roles,
            _users,
            _profiles,
            _identity,
            validator,
            new PasswordHasher());

        _invitations.Users = _users;
        _invitations.RolesSource = _roles;
        _invitations.Profiles = _profiles;
        _users.RolesSource = _roles;
    }

    // ---------------- reference tests ----------------

    [Fact]
    public async Task SingleUseInvitationGrantsOneRegistration()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("single-use-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);

        var registered = await _service.RegisterInvitationAsync(
            created.Token,
            "first@example.com",
            "password",
            "First",
            "Member");

        Assert.Equal("first@example.com", registered.User.Email);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                created.Token,
                "second@example.com",
                "password",
                "Second",
                "Member"));
        Assert.Equal("invitation is no longer valid", error.Message);

        // user_projects edge and user_roles link created by the transaction.
        Assert.Contains(
            _profiles.UserProjects,
            membership => membership.UserId == registered.User.Id
                && membership.ProjectId == project.Id);
        Assert.Contains(role.Id, _roles.UserRoleIds(registered.User.Id));

        // ConvertUserToUserInfo after the post-commit reload: one project whose
        // effective scopes fold in the invitation role.
        var profileProject = Assert.Single(registered.Profile.Projects);
        Assert.Equal(project.Id, profileProject.ProjectId);
        Assert.Contains("write_requests", profileProject.EffectiveScopes);
    }

    [Fact]
    public async Task UnlimitedInvitationRegistersEveryTime()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("unlimited-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var created = await _service.CreateInvitationAsync(project.Id, role.Id, 0, 0);

        var first = await _service.RegisterInvitationAsync(
            created.Token,
            "first@example.com",
            "password",
            "First",
            "Member");
        var second = await _service.RegisterInvitationAsync(
            created.Token,
            "second@example.com",
            "password",
            "Second",
            "Member");

        Assert.NotEqual(first.User.Id, second.User.Id);

        var info = await _service.GetInvitationAsync(created.Token);
        Assert.Equal(0, info.MaxUses);
        Assert.Equal(2, info.UsedCount);
        Assert.Equal(0, info.RemainingUses);
    }

    [Fact]
    public async Task RejectsUnmigratedLegacyInvitation()
    {
        var project = await CreateProjectAsync("legacy-project");
        const string token = "legacy-token";
        _invitations.Rows.Add(new InvitationRecord
        {
            TokenHash = InvitationService.HashInvitationToken(token),
            ProjectId = project.Id,
            RoleId = null,
            MaxUses = 1,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                token,
                "legacy@example.com",
                "password",
                "Legacy",
                "Member"));

        Assert.Equal("invitation role is required", error.Message);
    }

    [Fact]
    public async Task RejectsRoleTheInviterCannotGrant()
    {
        var project = await CreateProjectAsync("restricted-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        var inviter = await CreateUserAsync("inviter@example.com");
        _users.AssignProject(inviter.Id, project.Id, scopes: ["write_users"]);
        _identity.User = new ManagementUser(inviter.Id, IsOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(project.Id, role.Id, null, 1));

        Assert.Equal(
            "permission denied: insufficient permissions: cannot grant scope "
                + "'write_requests' that you don't possess",
            error.Message);
    }

    [Fact]
    public async Task RejectsDeletedInvitationRole()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("deleted-role-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);
        await _roles.DeleteAsync(role.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                created.Token,
                "member@example.com",
                "password",
                "Member",
                "User"));

        Assert.Equal("invitation role is no longer available", error.Message);
    }

    [Fact]
    public async Task ExpiredInvitationIsRejectedByLookupAndRegistration()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("expired-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var created = await _service.CreateInvitationAsync(project.Id, role.Id, 1, 1);
        _invitations.Rows.Single().ExpiresAt = DateTime.UtcNow.AddHours(-1);

        var lookup = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GetInvitationAsync(created.Token));
        Assert.Equal("invitation is no longer valid", lookup.Message);

        var register = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                created.Token,
                "member@example.com",
                "password",
                "Member",
                "User"));
        Assert.Equal("invitation is no longer valid", register.Message);
    }

    [Fact]
    public void GenerateInvitationTokenProducesEightBase64UrlCharacters()
    {
        var token = InvitationService.GenerateInvitationToken();

        Assert.Equal(8, token.Length);

        var bytes = Base64Url.DecodeFromChars(token);
        Assert.Equal(6, bytes.Length);
    }

    // ---------------- creation validation ----------------

    [Fact]
    public async Task CreateRequiresAProjectRole()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(project.Id, 0, null, 1));

        Assert.Equal("project role is required", error.Message);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task CreateRejectsMaxUsesOtherThanOneOrZero(int maxUses)
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(project.Id, role.Id, null, maxUses));

        Assert.Equal("max uses must be 1 or 0", error.Message);
    }

    [Fact]
    public async Task CreateRejectsANegativeExpiry()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(project.Id, role.Id, -1, 1));

        Assert.Equal("expiration must not be negative", error.Message);
    }

    [Fact]
    public async Task CreateRejectsAnExcessiveExpiry()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(project.Id, role.Id, 100_001, 1));

        Assert.Equal("expiration hours exceed maximum limit", error.Message);
    }

    [Fact]
    public void InvitationExpiryDefaultsToSevenDays()
    {
        var before = DateTime.UtcNow;

        var expiresAt = InvitationService.InvitationExpiry(null);

        Assert.NotNull(expiresAt);
        Assert.InRange(
            expiresAt!.Value,
            before.Add(InvitationService.DefaultInvitationLifetime),
            DateTime.UtcNow.Add(InvitationService.DefaultInvitationLifetime));
    }

    [Fact]
    public void InvitationExpiryZeroMeansNever()
        => Assert.Null(InvitationService.InvitationExpiry(0));

    [Fact]
    public async Task CreateFailsWhenTheProjectIsMissing()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(999, role.Id, null, 1));

        Assert.Equal("failed to get project: ent: project not found", error.Message);
    }

    [Fact]
    public async Task CreateFailsWhenTheRoleBelongsToAnotherProject()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var other = await CreateProjectAsync("other-project");
        var role = await CreateProjectRoleAsync(other.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateInvitationAsync(project.Id, role.Id, null, 1));

        Assert.Equal("project role not found", error.Message);
    }

    // ---------------- canInvite ----------------

    [Fact]
    public async Task CanInviteRejectsACallerWithoutProjectAccess()
    {
        var outsider = await CreateUserAsync("outsider@example.com");
        var project = await CreateProjectAsync("project");
        _identity.User = new ManagementUser(outsider.Id, IsOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CanInviteAsync(project.Id));

        Assert.Equal(
            "permission denied: project user management access required",
            error.Message);
    }

    [Fact]
    public async Task CanInviteAcceptsASystemWriteUsersScope()
    {
        var user = await CreateUserAsync("manager@example.com");
        user.Scopes = """["write_users","write_requests"]""";
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(user.Id, IsOwner: false);

        // canInvite passes on the system scope; the grant check passes because
        // the caller also holds the role's scope.
        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);

        Assert.NotEmpty(created.Token);
    }

    [Fact]
    public async Task CanInviteAcceptsAProjectOwnerMembership()
    {
        var user = await CreateUserAsync("project-owner@example.com");
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _users.AssignProject(user.Id, project.Id, isOwner: true);
        _identity.User = new ManagementUser(user.Id, IsOwner: false);

        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);

        Assert.NotEmpty(created.Token);
    }

    [Fact]
    public async Task CanInviteAcceptsAProjectRoleOnlyInsideTheMatchingMembership()
    {
        var user = await CreateUserAsync("role-holder@example.com");
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_users", "write_requests");
        _users.AssignProject(user.Id, project.Id);
        _roles.Assign(user.Id, role.Id);
        _identity.User = new ManagementUser(user.Id, IsOwner: false);

        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);

        Assert.NotEmpty(created.Token);
    }

    [Fact]
    public async Task CanInviteIgnoresProjectRolesWithoutAMembership()
    {
        // invitation.go:278-283 only consults project roles inside the matching
        // membership branch; a role alone grants nothing.
        var user = await CreateUserAsync("role-holder@example.com");
        var project = await CreateProjectAsync("project");
        var other = await CreateProjectAsync("other-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_users");
        _users.AssignProject(user.Id, other.Id);
        _roles.Assign(user.Id, role.Id);
        _identity.User = new ManagementUser(user.Id, IsOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CanInviteAsync(project.Id));

        Assert.Equal(
            "permission denied: project user management access required",
            error.Message);
    }

    [Fact]
    public async Task CanInviteRequiresAContextUser()
    {
        var project = await CreateProjectAsync("project");
        _identity.User = null;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CanInviteAsync(project.Id));

        Assert.Equal("user not found in context", error.Message);
    }

    // ---------------- lookup ----------------

    [Fact]
    public async Task GetInvitationReportsAnUnknownToken()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.GetInvitationAsync("missing-token"));

        Assert.Equal("invitation not found", error.Message);
    }

    [Fact]
    public async Task GetInvitationReportsAnEmptyProjectNameForASoftDeletedProject()
    {
        // The reference dereferences the nil WithProject edge and panics; the
        // port reports an empty name instead (C201).
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("gone-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);
        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);
        _invitations.ProjectNames.Remove(project.Id);

        var info = await _service.GetInvitationAsync(created.Token);

        Assert.Equal(string.Empty, info.ProjectName);
    }

    [Fact]
    public async Task GetInvitationIncludesTheProjectNameAndRemainingUses()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("named-project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);
        var created = await _service.CreateInvitationAsync(project.Id, role.Id, 2, 1);

        var info = await _service.GetInvitationAsync(created.Token);

        Assert.Equal("named-project", info.ProjectName);
        Assert.Equal(1, info.MaxUses);
        Assert.Equal(0, info.UsedCount);
        Assert.Equal(1, info.RemainingUses);
        Assert.NotNull(info.ExpiresAt);
    }

    // ---------------- registration validation ----------------

    [Fact]
    public async Task RegisterRequiresEmailAndPassword()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                "any",
                "   ",
                "password",
                "First",
                "Member"));

        Assert.Equal("email and password are required", error.Message);
    }

    [Fact]
    public async Task RegisterRequiresAPasswordOfAtLeastSevenBytes()
    {
        // Go's len(password) counts bytes; "密码" is two runes in six bytes.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                "any",
                "member@example.com",
                "密码",
                "First",
                "Member"));

        Assert.Equal("password must be at least 7 characters", error.Message);
    }

    [Fact]
    public async Task RegisterMeasuresThePasswordInBytesNotRunes()
    {
        // Three CJK runes are nine bytes, so the byte-length check passes even
        // though a rune-based minimum of seven would reject it.
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);
        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);

        var registered = await _service.RegisterInvitationAsync(
            created.Token,
            "member@example.com",
            "密码密",
            "Member",
            "User");

        Assert.NotNull(registered.User);
    }

    [Fact]
    public async Task RegisterRejectsAnExistingEmail()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);
        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);
        _invitations.ExistingEmails.Add("taken@example.com");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                created.Token,
                "taken@example.com",
                "password",
                "Member",
                "User"));

        Assert.Equal("a user with this email already exists", error.Message);
    }

    [Fact]
    public async Task RegisterTrimsTheEmailAndNames()
    {
        var owner = await CreateUserAsync("owner@example.com", isOwner: true);
        var project = await CreateProjectAsync("project");
        var role = await CreateProjectRoleAsync(project.Id, "write_requests");
        _identity.User = new ManagementUser(owner.Id, IsOwner: true);
        var created = await _service.CreateInvitationAsync(project.Id, role.Id, null, 1);

        var registered = await _service.RegisterInvitationAsync(
            created.Token,
            "  member@example.com\t",
            "password",
            "  Member ",
            "\tUser  ");

        Assert.Equal("member@example.com", registered.User.Email);
        Assert.Equal("Member", registered.User.FirstName);
        Assert.Equal("User", registered.User.LastName);
        Assert.Equal("member@example.com", registered.Profile.Email);
    }

    [Fact]
    public async Task RegisterReportsAnUnknownToken()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.RegisterInvitationAsync(
                "missing-token",
                "member@example.com",
                "password",
                "Member",
                "User"));

        Assert.Equal("invitation not found", error.Message);
    }

    [Fact]
    public async Task RegisterForwardsTheStoreFailureWithoutReprefixing()
    {
        // InvitationStore emits the reference's per-step wording
        // (invitation.go:234-239); the service must not wrap it again.
        _invitations.RegisterFailure = new StoreFailureException(
            "failed to create user: boom",
            new InvalidOperationException("boom"));

        // The store's exception type passes through unchanged; the service only
        // re-keys unknown errors, so this must stay a StoreFailureException.
        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => _service.RegisterInvitationAsync(
                "token",
                "member@example.com",
                "password",
                "Member",
                "User"));

        Assert.Equal("failed to create user: boom", error.Message);
    }

    [Fact]
    public void HashInvitationTokenIsLowercaseSha256Hex()
    {
        var hash = InvitationService.HashInvitationToken("token");

        Assert.Equal(
            "3c469e9d6c5875d37a43f353d4f88e61fcf812c66eee3457465a40b0da4153e0",
            hash);
    }

    // ---------------- helpers ----------------

    private async Task<UserRecord> CreateUserAsync(string email, bool isOwner = false)
    {
        var user = new UserRecord
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Email = email,
            Password = "hashed",
            Status = "activated",
            PreferLanguage = "en",
            IsOwner = isOwner,
        };

        await _users.CreateAsync(user, null);
        return user;
    }

    private async Task<ProjectRecord> CreateProjectAsync(string name)
    {
        var project = new ProjectRecord
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Name = name,
            Status = "active",
        };

        await _projects.CreateAsync(project);
        _invitations.ProjectNames[project.Id] = name;
        return project;
    }

    private async Task<RoleRecord> CreateProjectRoleAsync(long projectId, params string[] scopes)
    {
        var role = new RoleRecord
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Name = "Developer",
            Level = "project",
            ProjectId = projectId,
            Scopes = JsonSerializer.Serialize(scopes),
        };

        await _roles.CreateAsync(role);
        return role;
    }
}

/// <summary>
/// In-memory IInvitationStore mirroring <c>InvitationStore</c>: live rows only,
/// the registration transaction's validation order and messages, the
/// conditional use-count update, and the soft-delete revocation.
/// </summary>
/// <remarks>
/// The fake has no per-user role projection: every user role added by a
/// registration lands in <see cref="FakeUserProfileStore.Roles"/>, which is
/// enough because each test registers a single user's edges. The real join
/// filtering is covered by InvitationStoreTests (Postgres).
/// </remarks>
internal sealed class FakeInvitationStore : IInvitationStore
{
    private long _nextId = 1;

    public List<InvitationRecord> Rows { get; } = [];

    /// <summary>The project edge for GetByTokenHashAsync; a missing key means the project is gone.</summary>
    public Dictionary<long, string?> ProjectNames { get; } = [];

    /// <summary>Receives the users row created by RegisterAsync.</summary>
    public FakeUserAdminStore? Users { get; set; }

    /// <summary>Resolves the invitation role's liveness.</summary>
    public IRoleStore? RolesSource { get; set; }

    /// <summary>Receives the user_projects and user_roles edges of a registration.</summary>
    public FakeUserProfileStore? Profiles { get; set; }

    /// <summary>Emails already present in the users table.</summary>
    public HashSet<string> ExistingEmails { get; } = new(StringComparer.Ordinal);

    public Exception? CreateFailure { get; set; }

    public Exception? GetFailure { get; set; }

    public Exception? RegisterFailure { get; set; }

    public Exception? RevokeFailure { get; set; }

    /// <summary>Every role-id list passed to DeleteByRoleIdsAsync.</summary>
    public List<long> RevokedRoleIds { get; } = [];

    public Task<InvitationRecord> CreateAsync(
        InvitationRecord invitation,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        invitation.Id = invitation.Id > 0 ? invitation.Id : _nextId++;
        if (invitation.Id >= _nextId)
        {
            _nextId = invitation.Id + 1;
        }

        invitation.CreatedAt = invitation.CreatedAt == default
            ? DateTime.UtcNow
            : invitation.CreatedAt;
        invitation.UpdatedAt = invitation.UpdatedAt == default
            ? invitation.CreatedAt
            : invitation.UpdatedAt;

        Rows.Add(invitation);
        return Task.FromResult(invitation);
    }

    public Task<InvitationWithProject?> GetByTokenHashAsync(
        string tokenHash,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetFailure is not null)
        {
            throw GetFailure;
        }

        var row = Rows.FirstOrDefault(
            item => item.DeletedAt == 0 && item.TokenHash == tokenHash);
        if (row is null)
        {
            return Task.FromResult<InvitationWithProject?>(null);
        }

        return Task.FromResult<InvitationWithProject?>(
            new InvitationWithProject(
                row,
                ProjectNames.TryGetValue(row.ProjectId, out var name) ? name : null));
    }

    public async Task<UserRecord> RegisterAsync(
        InvitationRegistration registration,
        CancellationToken cancellationToken = default)
    {
        if (RegisterFailure is not null)
        {
            throw RegisterFailure;
        }

        var invitation = Rows.FirstOrDefault(
            item => item.DeletedAt == 0 && item.TokenHash == registration.TokenHash);
        if (invitation is null)
        {
            throw new InvalidOperationException("invitation not found");
        }

        var now = DateTime.UtcNow;
        if (invitation.ExpiresAt is { } expiresAt && expiresAt <= now)
        {
            throw new InvalidOperationException("invitation is no longer valid");
        }

        if (invitation.MaxUses > 0 && invitation.UsedCount >= invitation.MaxUses)
        {
            throw new InvalidOperationException("invitation is no longer valid");
        }

        if (invitation.RoleId is not { } roleId)
        {
            throw new InvalidOperationException("invitation role is required");
        }

        var role = RolesSource is null
            ? null
            : await RolesSource.GetByIdAsync(roleId, cancellationToken).ConfigureAwait(false);
        if (role is null || role.ProjectId != invitation.ProjectId)
        {
            throw new InvalidOperationException("invitation role is no longer available");
        }

        if (ExistingEmails.Contains(registration.Email))
        {
            throw new InvalidOperationException("a user with this email already exists");
        }

        // The conditional AddUsedCount update: the expiry predicate is always
        // present, the used_count predicate only for limited invitations.
        if (invitation.MaxUses > 0 && invitation.UsedCount >= invitation.MaxUses)
        {
            throw new InvalidOperationException("invitation is no longer valid");
        }

        invitation.UsedCount++;
        invitation.UpdatedAt = now;

        var user = new UserRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Email = registration.Email,
            Status = "activated",
            PreferLanguage = "en",
            Password = registration.PasswordHash,
            FirstName = registration.FirstName,
            LastName = registration.LastName,
        };

        if (Users is not null)
        {
            await Users.CreateAsync(user, null, cancellationToken).ConfigureAwait(false);
        }

        if (Profiles is not null)
        {
            if (!Profiles.UserProjects.Any(
                    membership => membership.UserId == user.Id
                        && membership.ProjectId == invitation.ProjectId))
            {
                Profiles.UserProjects.Add(new UserProjectRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    UserId = user.Id,
                    ProjectId = invitation.ProjectId,
                    IsOwner = false,
                });
            }

            if (!Profiles.Roles.Any(item => item.Id == role.Id))
            {
                Profiles.Roles.Add(role);
            }
        }

        if (RolesSource is FakeRoleStore fakeRoles)
        {
            fakeRoles.Assign(user.Id, roleId);
        }

        Users?.AssignProject(user.Id, invitation.ProjectId);
        return user;
    }

    public Task<int> DeleteByRoleIdsAsync(
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (RevokeFailure is not null)
        {
            throw RevokeFailure;
        }

        RevokedRoleIds.AddRange(roleIds);
        if (roleIds.Count == 0)
        {
            return Task.FromResult(0);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var affected = 0;
        foreach (var row in Rows)
        {
            if (row.DeletedAt == 0
                && row.RoleId is { } roleId
                && roleIds.Contains(roleId))
            {
                row.DeletedAt = now;
                row.UpdatedAt = DateTime.UtcNow;
                affected++;
            }
        }

        return Task.FromResult(affected);
    }
}
