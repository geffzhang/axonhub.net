using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the biz.UserService / biz.RoleService management semantics and their
/// reference error strings (internal/server/biz/user.go, role.go).
/// </summary>
public sealed class ManagementServiceTests
{
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly UserManagementService _userService;
    private readonly RoleManagementService _roleService;

    public ManagementServiceTests()
    {
        _userService = new UserManagementService(_users, new PasswordHasher());
        _roleService = new RoleManagementService(_roles);
    }

    [Fact]
    public async Task UpdateUserReturnsTheEntNotFoundError()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userService.UpdateAsync(new UpdateUserCommand { Id = 42 }));

        Assert.Equal("failed to update user: ent: user not found", error.Message);
    }

    [Fact]
    public async Task UpdateUserStatusReturnsTheEntNotFoundError()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userService.UpdateStatusAsync(42, "deactivated"));

        Assert.Equal("failed to update user status: ent: user not found", error.Message);
    }

    [Fact]
    public async Task DeleteUserReturnsTheEntNotFoundError()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userService.DeleteAsync(42));

        Assert.Equal("failed to get user: ent: user not found", error.Message);
    }

    [Fact]
    public async Task UpdateUserRehashesOnlyWhenThePasswordIsSupplied()
    {
        var user = await CreateUserAsync();
        // The fake store returns the same instance it tracks, so keep a copy.
        var originalPassword = user.Password;

        var updated = await _userService.UpdateAsync(new UpdateUserCommand
        {
            Id = user.Id,
            FirstName = "Renamed",
        });

        Assert.Equal(originalPassword, updated.Password);
        Assert.Equal("Renamed", updated.FirstName);

        var resigned = await _userService.UpdateAsync(new UpdateUserCommand
        {
            Id = user.Id,
            Password = "new-secret",
        });

        Assert.NotEqual(originalPassword, resigned.Password);
        new PasswordHasher().Verify(resigned.Password, "new-secret");
    }

    [Fact]
    public async Task SystemRolesRejectAProjectID()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _roleService.CreateAsync(new CreateRoleCommand
            {
                Name = "Viewer",
                Level = RoleManagementService.LevelSystem,
                ProjectId = 7,
            }));

        Assert.Equal("project ID is not allowed for system roles", error.Message);
    }

    [Fact]
    public async Task BulkDeleteReportsTheMissingRoleCount()
    {
        var role = await CreateRoleAsync("Viewer", 0);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _roleService.BulkDeleteAsync([role.Id, 999]));

        Assert.Equal("expected to find 2 roles, but found 1", error.Message);
    }

    [Fact]
    public async Task BulkDeleteWithNoIdsIsANoOp()
    {
        await _roleService.BulkDeleteAsync([]);
    }

    [Fact]
    public async Task RenamingARoleToAnotherRolesNameReturnsTheCodedError()
    {
        var first = await CreateRoleAsync("Alpha", 0);
        var second = await CreateRoleAsync("Beta", 0);

        var error = await Assert.ThrowsAsync<CodedException>(
            () => _roleService.UpdateAsync(new UpdateRoleCommand
            {
                Id = second.Id,
                Name = first.Name,
            }));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("role name 'Alpha' already exists", error.Message);
        Assert.Equal("role", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("Alpha", error.Extensions["value"]);
    }

    [Fact]
    public async Task RenamingARoleToItsOwnNameSkipsTheUniquenessCheck()
    {
        var role = await CreateRoleAsync("Alpha", 0);

        var updated = await _roleService.UpdateAsync(new UpdateRoleCommand
        {
            Id = role.Id,
            Name = "Alpha",
        });

        Assert.Equal("Alpha", updated.Name);
    }

    [Fact]
    public async Task UpdateStatusForwardsTheStoreFailureWithoutReprefixing()
    {
        // Reference UpdateUserStatus emits "failed to disable user personal API
        // keys: %w" itself; RunInTransaction returns it unchanged.
        var user = await CreateUserAsync();
        _users.UpdateStatusFailure = new StoreFailureException(
            "failed to disable user personal API keys: boom",
            new InvalidOperationException("boom"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userService.UpdateStatusAsync(user.Id, "deactivated"));

        Assert.Equal("failed to disable user personal API keys: boom", error.Message);
    }

    [Fact]
    public async Task DeleteForwardsTheStoreFailureWithoutReprefixing()
    {
        // Reference DeleteUser emits "failed to archive user personal API keys: %w".
        var user = await CreateUserAsync();
        _users.DeleteFailure = new StoreFailureException(
            "failed to archive user personal API keys: boom",
            new InvalidOperationException("boom"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _userService.DeleteAsync(user.Id));

        Assert.Equal("failed to archive user personal API keys: boom", error.Message);
    }

    private async Task<UserRecord> CreateUserAsync()
    {
        var user = await _userService.CreateAsync(new CreateUserCommand
        {
            Email = "user@example.com",
            Password = "secret",
        });

        return user;
    }

    private async Task<RoleRecord> CreateRoleAsync(string name, long projectId)
    {
        var now = DateTime.UtcNow;
        var role = new RoleRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Name = name,
            Level = RoleManagementService.LevelSystem,
            ProjectId = projectId,
        };

        await _roles.CreateAsync(role);
        return role;
    }
}
