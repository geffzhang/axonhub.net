using System.Text.RegularExpressions;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.SystemService.Initialize (internal/server/biz/system.go:698-846):
/// the already-initialized no-op, the owner user, the default project with its
/// role presets and owner row, the JWT secret (32 random bytes, lowercase hex),
/// the brand name, the Primary data storage and its default setting, the
/// initialized flag, the build version, and every step's reference error
/// prefix including the inner failed-to-create-system-setting wrapper.
/// </summary>
public sealed class SystemInitializationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeDataStorageStore _storages = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly TestTimeProvider _clock = new(Now);

    public SystemInitializationServiceTests()
    {
        _adminUsers.RolesSource = _roles;
    }

    private SystemInitializationService NewService()
    {
        var identity = new FakeManagementIdentity(null);
        return new SystemInitializationService(
            _systems,
            _users,
            _storages,
            new ProjectManagementService(
                _projects,
                _roles,
                identity,
                new PermissionValidator(identity, _adminUsers, _roles)),
            _hasher,
            new SystemSettingsAdminService(_systems),
            _clock);
    }

    private static InitializeSystemParameters Parameters(
        string preferLanguage = "",
        string password = "secret1") => new()
        {
            OwnerEmail = "owner@example.com",
            OwnerPassword = password,
            OwnerFirstName = "First",
            OwnerLastName = "Last",
            BrandName = "AxonHub",
            PreferLanguage = preferLanguage,
        };

    [Fact]
    public async Task InitializeCreatesTheFirstRunState()
    {
        await NewService().InitializeAsync(Parameters());

        var user = Assert.Single(_users.Rows);
        Assert.Equal("owner@example.com", user.Email);
        Assert.Equal("hashed", user.Password);
        Assert.Equal("First", user.FirstName);
        Assert.Equal("Last", user.LastName);
        Assert.Equal("en", user.PreferLanguage);
        Assert.Equal("activated", user.Status);
        Assert.True(user.IsOwner);
        Assert.Equal("""["*"]""", user.Scopes);
        Assert.Equal(Now.UtcDateTime, user.CreatedAt);
        Assert.Equal(Now.UtcDateTime, user.UpdatedAt);
        Assert.Equal(["secret1"], _hasher.HashedPasswords);

        var project = Assert.Single(_projects.Rows);
        Assert.Equal("Default", project.Name);
        Assert.Equal("Default project", project.Description);
        Assert.Equal("active", project.Status);
        Assert.Equal(
            """{"activeProfile":"","profiles":null}""",
            project.Profiles);

        Assert.Equal(["Admin", "Developer", "Viewer"], _roles.Rows.Select(static row => row.Name));
        Assert.All(_roles.Rows, row =>
        {
            Assert.Equal(RoleManagementService.LevelProject, row.Level);
            Assert.Equal(project.Id, row.ProjectId);
        });

        var owner = Assert.Single(_projects.ProjectUsers);
        Assert.Equal(user.Id, owner.UserId);
        Assert.Equal(project.Id, owner.ProjectId);
        Assert.True(owner.IsOwner);
        Assert.Equal("[]", owner.Scopes);

        var storage = Assert.Single(_storages.Rows);
        Assert.Equal("Primary", storage.Name);
        Assert.Equal("Primary database storage", storage.Description);
        Assert.True(storage.Primary);
        Assert.Equal("database", storage.Type);
        Assert.Equal("{}", storage.Settings);
        Assert.Equal("active", storage.Status);
        Assert.Equal(Now.UtcDateTime, storage.CreatedAt);

        var secret = Value(ManagementAuthService.SecretKeyKey);
        Assert.Matches(new Regex("^[0-9a-f]{64}$"), secret);
        Assert.Equal("AxonHub", Value(SystemInitializationService.BrandNameKey));
        Assert.Equal(
            storage.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value(SystemSettingsAdminService.DefaultDataStorageIdKey));
        Assert.Equal("true", Value(SystemSettingsAdminService.InitializedKey));
        Assert.Equal(
            SystemVersionService.Version,
            Value(SystemInitializationService.VersionKey));
    }

    [Fact]
    public async Task InitializeHonoursAnExplicitPreferLanguage()
    {
        await NewService().InitializeAsync(Parameters(preferLanguage: "zh"));

        Assert.Equal("zh", Assert.Single(_users.Rows).PreferLanguage);
    }

    [Fact]
    public async Task InitializeIsANoOpWhenTheSystemIsAlreadyInitialized()
    {
        _systems.Add(SystemSettingsAdminService.InitializedKey, "TRUE");

        await NewService().InitializeAsync(Parameters());

        Assert.Empty(_users.Rows);
        Assert.Empty(_projects.Rows);
        Assert.Empty(_roles.Rows);
        Assert.Empty(_storages.Rows);
        Assert.Single(_systems.Rows);
        Assert.Empty(_hasher.HashedPasswords);
    }

    [Fact]
    public async Task CheckFailureIsWrapped()
    {
        _systems.GetFailure = new InvalidOperationException("db down");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().InitializeAsync(Parameters()));

        Assert.Equal(
            "failed to check initialization status: db down",
            error.Message);
    }

    [Fact]
    public async Task PasswordHashingFailureKeepsTheReferenceDoublePrefix()
    {
        // biz.HashPassword already prefixes, and Initialize prefixes again.
        _hasher.HashError = new InvalidOperationException(
            "failed to hash password: bcrypt: password length exceeds 72 bytes");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().InitializeAsync(Parameters()));

        Assert.Equal(
            "failed to hash password: failed to hash password: "
            + "bcrypt: password length exceeds 72 bytes",
            error.Message);
        Assert.Empty(_users.Rows);
        Assert.Empty(_storages.Rows);
    }

    [Fact]
    public async Task OwnerCreationFailureStopsBeforeTheProject()
    {
        _users.CreateFailure = new InvalidOperationException(
            "duplicate key value violates unique constraint \"user_email_deleted_at\"");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().InitializeAsync(Parameters()));

        Assert.Equal(
            "failed to create owner user: duplicate key value violates "
            + "unique constraint \"user_email_deleted_at\"",
            error.Message);
        Assert.Empty(_projects.Rows);
        Assert.Empty(_roles.Rows);
        Assert.Empty(_storages.Rows);
    }

    [Fact]
    public async Task DefaultProjectFailureKeepsTheProjectServiceChain()
    {
        _roles.CreateFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().InitializeAsync(Parameters()));

        Assert.Equal(
            "failed to create default project: failed to create admin role: boom",
            error.Message);
    }

    [Fact]
    public async Task StorageFailureIsWrappedAndRollsTheSystemRowsBack()
    {
        _storages.CreateFailure = new StoreFailureException(
            "boom",
            new InvalidOperationException("boom"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().InitializeAsync(Parameters()));

        Assert.Equal("failed to create primary data storage: boom", error.Message);
        Assert.Empty(_systems.Rows);
        Assert.Empty(_storages.Rows);
    }

    [Fact]
    public async Task SystemValueFailureKeepsTheInnerUpsertWrapper()
    {
        _systems.SetFailuresByKey[ManagementAuthService.SecretKeyKey] =
            new InvalidOperationException("permission denied");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().InitializeAsync(Parameters()));

        Assert.Equal(
            "failed to set secret key: failed to create system setting: permission denied",
            error.Message);
        Assert.Empty(_systems.Rows);
    }

    private string Value(string key)
        => Assert.Single(_systems.Rows, row => row.Key == key).Value;
}
