using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the permission-hierarchy rules of biz.PermissionValidator
/// (internal/server/biz/permission_validator_test.go, 19 subtests): the scope
/// sources (direct, system roles, project membership, project roles), the
/// owner shortcuts, the target/role fetches and the reference error strings.
/// </summary>
public sealed class PermissionValidatorTests
{
    [Fact]
    public async Task AnOwnerCanGrantAnyScopes()
    {
        var fixture = new Fixture();
        var owner = await fixture.CreateUserAsync("owner@example.com", isOwner: true);
        fixture.As(owner, isOwner: true);

        await fixture.Validator.CanGrantScopesAsync(
            ["read_users", "write_users", "read_projects"], null);
    }

    [Fact]
    public async Task AUserCanGrantScopesTheyPossess()
    {
        var fixture = new Fixture();
        var user = await fixture.CreateUserAsync(
            "user@example.com", ["read_users", "write_users"]);
        fixture.As(user, isOwner: false);

        await fixture.Validator.CanGrantScopesAsync(["read_users"], null);
        await fixture.Validator.CanGrantScopesAsync(["read_users", "write_users"], null);
    }

    [Fact]
    public async Task AUserCannotGrantScopesTheyDoNotPossess()
    {
        var fixture = new Fixture();
        var user = await fixture.CreateUserAsync("limited@example.com", ["read_users"]);
        fixture.As(user, isOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanGrantScopesAsync(["write_users"], null));
        Assert.Equal(
            "insufficient permissions: cannot grant scope 'write_users' that you don't possess",
            error.Message);

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanGrantScopesAsync(
                ["read_users", "write_projects"], null));
        Assert.Equal(
            "insufficient permissions: cannot grant scope 'write_projects' that you don't possess",
            error.Message);
    }

    [Fact]
    public async Task AUserWithARoleCanGrantRoleScopes()
    {
        var fixture = new Fixture();
        var role = await fixture.CreateRoleAsync(
            "Test Role", ["read_channels", "write_channels"]);
        var user = await fixture.CreateUserAsync(
            "roleuser@example.com", roleIds: [role]);
        fixture.As(user, isOwner: false);

        await fixture.Validator.CanGrantScopesAsync(["read_channels"], null);
    }

    [Fact]
    public async Task AnOwnerCanEditAnyUser()
    {
        var fixture = new Fixture();
        var owner = await fixture.CreateUserAsync("owner@example.com", isOwner: true);
        var target = await fixture.CreateUserAsync(
            "target@example.com", ["read_users", "write_users", "read_projects"]);
        fixture.As(owner, isOwner: true);

        await fixture.Validator.CanEditUserPermissionsAsync(target, null);
    }

    [Fact]
    public async Task AUserCanEditAUserWithLowerPermissions()
    {
        var fixture = new Fixture();
        var current = await fixture.CreateUserAsync(
            "current@example.com", ["read_users", "write_users", "read_channels"]);
        var target = await fixture.CreateUserAsync("target2@example.com", ["read_users"]);
        fixture.As(current, isOwner: false);

        await fixture.Validator.CanEditUserPermissionsAsync(target, null);
    }

    [Fact]
    public async Task AUserCannotEditAUserWithHigherPermissions()
    {
        var fixture = new Fixture();
        var current = await fixture.CreateUserAsync("limited2@example.com", ["read_users"]);
        var target = await fixture.CreateUserAsync(
            "target3@example.com", ["read_users", "write_users", "read_projects"]);
        fixture.As(current, isOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanEditUserPermissionsAsync(target, null));
        Assert.Contains("insufficient permissions", error.Message);
    }

    [Fact]
    public async Task ANonOwnerCannotEditAnOwner()
    {
        var fixture = new Fixture();
        var regular = await fixture.CreateUserAsync(
            "regular@example.com", ["read_users", "write_users"]);
        var owner = await fixture.CreateUserAsync("owner2@example.com", isOwner: true);
        fixture.As(regular, isOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanEditUserPermissionsAsync(owner, null));
        Assert.Equal("insufficient permissions: cannot edit owner users", error.Message);
    }

    [Fact]
    public async Task AUserCanEditARoleWithScopesTheyPossess()
    {
        var fixture = new Fixture();
        var user = await fixture.CreateUserAsync(
            "user@example.com", ["read_users", "write_users"]);
        var role = await fixture.CreateRoleAsync("Test Role", ["read_users"]);
        fixture.As(user, isOwner: false);

        await fixture.Validator.CanEditRoleAsync(role, null);
    }

    [Fact]
    public async Task AUserCannotEditARoleWithScopesTheyDoNotPossess()
    {
        var fixture = new Fixture();
        var user = await fixture.CreateUserAsync("limited3@example.com", ["read_users"]);
        var role = await fixture.CreateRoleAsync(
            "High Permission Role", ["read_users", "write_users", "read_projects"]);
        fixture.As(user, isOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanEditRoleAsync(role, null));
        Assert.Contains("insufficient permissions", error.Message);
    }

    [Fact]
    public async Task AProjectMemberCanEditAProjectRoleWithItsEffectiveScopes()
    {
        var fixture = new Fixture();
        const long projectId = 1;
        var managedRole = await fixture.CreateRoleAsync(
            "Managed Role", ["read_prompts"], level: "project", projectId: projectId);
        var userRole = await fixture.CreateRoleAsync(
            "Project Administrator",
            ["read_prompts", "write_roles"],
            level: "project",
            projectId: projectId);
        var projectAdmin = await fixture.CreateUserAsync(
            "project-admin@example.com", roleIds: [userRole]);
        fixture.Users.AssignProject(projectAdmin, projectId);
        fixture.As(projectAdmin, isOwner: false);

        await fixture.Validator.CanEditRoleAsync(managedRole, null);
    }

    [Fact]
    public async Task AProjectOwnerCanGrantAnyProjectScopes()
    {
        var fixture = new Fixture();
        const long projectId = 1;
        var user = await fixture.CreateUserAsync("projectowner@example.com");
        fixture.Users.AssignProject(user, projectId, isOwner: true);
        fixture.As(user, isOwner: false);

        await fixture.Validator.CanGrantScopesAsync(
            ["read_api_keys", "write_api_keys"], projectId);
    }

    [Fact]
    public async Task AUserWithProjectScopesCanGrantThoseScopes()
    {
        var fixture = new Fixture();
        const long projectId = 1;
        var user = await fixture.CreateUserAsync("projectuser@example.com");
        fixture.Users.AssignProject(user, projectId, scopes: ["read_api_keys"]);
        fixture.As(user, isOwner: false);

        await fixture.Validator.CanGrantScopesAsync(["read_api_keys"], projectId);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanGrantScopesAsync(["write_api_keys"], projectId));
        Assert.Contains("insufficient permissions", error.Message);
    }

    [Fact]
    public async Task AUserWithAProjectRoleCanGrantRoleScopes()
    {
        var fixture = new Fixture();
        const long projectId = 1;
        var projectRole = await fixture.CreateRoleAsync(
            "Project Role", ["read_requests", "write_requests"], level: "project", projectId: projectId);
        var user = await fixture.CreateUserAsync(
            "projectroleuser@example.com", roleIds: [projectRole]);
        fixture.Users.AssignProject(user, projectId);
        fixture.As(user, isOwner: false);

        await fixture.Validator.CanGrantScopesAsync(["read_requests"], projectId);
    }

    [Fact]
    public async Task CreatingARoleValidatesPermissions()
    {
        var fixture = new Fixture();
        var user = await fixture.CreateUserAsync("creator@example.com", ["read_users"]);
        fixture.As(user, isOwner: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanGrantScopesAsync(
                ["write_users", "read_projects"], null));
        Assert.Contains("insufficient permissions", error.Message);
    }

    [Fact]
    public async Task UpdatingARoleValidatesPermissions()
    {
        var fixture = new Fixture();
        var role = await fixture.CreateRoleAsync("Editable Role", ["read_users"]);
        var user = await fixture.CreateUserAsync("editor@example.com", ["read_users"]);
        fixture.As(user, isOwner: false);

        // The user can edit a role with scopes they have…
        await fixture.Validator.CanEditRoleAsync(role, null);

        // …but cannot grant new scopes they don't have.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanGrantScopesAsync(
                ["read_users", "write_projects"], null));
        Assert.Contains("insufficient permissions", error.Message);
    }

    [Fact]
    public async Task WithoutAUserInContextEveryCheckFails()
    {
        var fixture = new Fixture();
        var user = await fixture.CreateUserAsync("target@example.com");
        var role = await fixture.CreateRoleAsync("Some Role", ["read_users"]);

        var grant = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanGrantScopesAsync([], null));
        Assert.Equal("user not found in context", grant.Message);

        var edit = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanEditUserPermissionsAsync(user, null));
        Assert.Equal("user not found in context", edit.Message);

        var deleteUser = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanDeleteUserAsync(user));
        Assert.Equal("user not found in context", deleteUser.Message);

        var deleteProject = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanDeleteProjectAsync(1));
        Assert.Equal("user not found in context", deleteProject.Message);
    }

    [Fact]
    public async Task CanEditRoleFetchesTheRoleBeforeCheckingTheUser()
    {
        var fixture = new Fixture();

        // The reference fetches the role first, so a missing role wins over
        // the missing user (permission_validator.go:218-224).
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanEditRoleAsync(42, null));
        Assert.Equal("failed to get role: ent: role not found", missing.Message);

        // With a live role the context check surfaces instead.
        var role = await fixture.CreateRoleAsync("Some Role", []);
        var noUser = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanEditRoleAsync(role, null));
        Assert.Equal("user not found in context", noUser.Message);
    }

    [Fact]
    public async Task AnOwnerCannotDeleteThemselves()
    {
        var fixture = new Fixture();
        var owner = await fixture.CreateUserAsync("owner@example.com", isOwner: true);
        fixture.As(owner, isOwner: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanDeleteUserAsync(owner));
        Assert.Equal("cannot delete yourself", error.Message);
    }

    [Fact]
    public async Task OnlySystemOwnersCanDeleteUsersOrProjects()
    {
        var fixture = new Fixture();
        var regular = await fixture.CreateUserAsync("regular@example.com");
        var target = await fixture.CreateUserAsync("target@example.com");
        fixture.As(regular, isOwner: false);

        var userError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanDeleteUserAsync(target));
        Assert.Equal(
            "insufficient permissions: only system owners can delete users",
            userError.Message);

        var projectError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Validator.CanDeleteProjectAsync(1));
        Assert.Equal(
            "insufficient permissions: only system owners can delete projects",
            projectError.Message);
    }

    /// <summary>In-memory user/role stores plus a swappable current user.</summary>
    private sealed class Fixture
    {
        public FakeUserAdminStore Users { get; } = new();

        public FakeRoleStore Roles { get; } = new();

        public FakeManagementIdentity Identity { get; } = new(null);

        public PermissionValidator Validator { get; }

        public Fixture()
        {
            Users.RolesSource = Roles;
            Validator = new PermissionValidator(Identity, Users, Roles);
        }

        public async Task<long> CreateUserAsync(
            string email,
            IReadOnlyList<string>? scopes = null,
            bool isOwner = false,
            IReadOnlyList<long>? roleIds = null)
        {
            var user = new UserRecord
            {
                Email = email,
                Status = "activated",
                IsOwner = isOwner,
                Scopes = scopes is null ? null : JsonSerializer.Serialize(scopes),
            };

            var id = await Users.CreateAsync(user, null);
            if (roleIds is not null)
            {
                foreach (var roleId in roleIds)
                {
                    Roles.Assign(id, roleId);
                }
            }

            return id;
        }

        public async Task<long> CreateRoleAsync(
            string name,
            IReadOnlyList<string> scopes,
            string level = "system",
            long projectId = 0)
        {
            var role = new RoleRecord
            {
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Name = name,
                Level = level,
                ProjectId = projectId,
                Scopes = JsonSerializer.Serialize(scopes),
            };

            await Roles.CreateAsync(role);
            return role.Id;
        }

        public void As(long userId, bool isOwner)
            => Identity.User = new ManagementUser(userId, isOwner);
    }
}
