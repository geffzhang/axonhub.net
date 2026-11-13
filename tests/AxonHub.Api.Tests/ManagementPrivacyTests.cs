using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the User / Role / Project / UserProject schema policies
/// (internal/ent/schema/user.go:100-114, role.go:85-97, project.go:122-134,
/// user_project.go:67-79 and internal/scopes/rule_*.go): the rule order, the
/// filters each query rule installs, the mutation decisions and the deny
/// messages ent reports.
/// </summary>
public sealed class ManagementPrivacyTests
{
    private const string NoUserDeny =
        "no user in context: " + PrivacyDenyException.DenyMessage;

    private const string DefaultDeny =
        "default deny: " + PrivacyDenyException.DenyMessage;

    [Fact]
    public async Task UserQueryOwnerIsUnfilteredEvenWithAProjectHeader()
    {
        // OwnerRule runs before ProjectOwnerReadUsersRule, so a header does not
        // narrow the owner's user query (user.go:103-107).
        var h = NewHarness(new ManagementUser(1, IsOwner: true), projectId: 5);

        Assert.Null(await h.UserPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task UserQueryTestPrincipalBypassesThePolicy()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false, IsTest: true));

        Assert.Null(await h.UserPrivacy.EvaluateQueryAsync());
        await h.UserPrivacy.EvaluateMutationAsync();
    }

    [Fact]
    public async Task UserQueryWithoutAUserDeniesWithTheNoUserMessage()
    {
        var h = NewHarness(null);

        var error = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => h.UserPrivacy.EvaluateQueryAsync());

        Assert.Equal(NoUserDeny, error.Message);
    }

    [Fact]
    public async Task UserQueryScopeLessNonOwnerEndsAtDefaultDeny()
    {
        // UserOwnedQueryRule skips for *ent.UserFilter (no WhereUserID), so the
        // policy ends at its default deny (rule_user_owned.go:31-39).
        var h = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(h.Users, 1, scopes: "[]");

        var error = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => h.UserPrivacy.EvaluateQueryAsync());

        Assert.Equal(DefaultDeny, error.Message);
    }

    [Fact]
    public async Task UserQueryProjectOwnerNarrowsToTheHeader()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        h.Users.AssignProject(1, projectId: 5, isOwner: true);

        Assert.Equal(5, await h.UserPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task UserQueryProjectMemberWithReadUsersNarrowsToTheHeader()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        h.Users.AssignProject(
            1,
            projectId: 5,
            isOwner: false,
            scopes: ["read_users"]);

        Assert.Equal(5, await h.UserPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task UserQuerySystemReadUsersKeepsTheViewUnrestricted()
    {
        // ProjectMemberReadUsersRule allows a system scope without a filter
        // (rule_user_project_scope.go:110-112).
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["read_users"]""");
        h.Users.AssignProject(1, projectId: 5, scopes: ["write_users"]);

        Assert.Null(await h.UserPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task UserMutationRequiresSystemWriteUsers()
    {
        var scopeLess = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(scopeLess.Users, 1, scopes: "[]");
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => scopeLess.UserPrivacy.EvaluateMutationAsync());
        Assert.Equal(DefaultDeny, denied.Message);

        var noUser = NewHarness(null);
        var noUserDenied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => noUser.UserPrivacy.EvaluateMutationAsync());
        Assert.Equal(NoUserDeny, noUserDenied.Message);

        var allowed = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(allowed.Users, 1, scopes: """["write_users"]""");
        await allowed.UserPrivacy.EvaluateMutationAsync();

        var owner = NewHarness(new ManagementUser(1, IsOwner: true));
        await owner.UserPrivacy.EvaluateMutationAsync();
    }

    [Fact]
    public async Task UserVisibilityHidesRowsFilteredByTheProjectScope()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        SeedUser(h.Users, 2, scopes: "[]");
        h.Users.AssignProject(1, projectId: 5, isOwner: true);

        var self = await h.UserPrivacy.GetVisibleUserOrThrowAsync(1);
        Assert.Equal(1, self.Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.UserPrivacy.GetVisibleUserOrThrowAsync(2));
        Assert.Equal("ent: user not found", error.Message);
    }

    [Fact]
    public async Task UserVisibilityDropsDeniesAndMissingRowsForNilableEdges()
    {
        // getNilableUser returns nil for id 0 before querying, and maps both
        // NotFound and privacy.Deny to nil (graphql.go:249-267).
        var denied = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(denied.Users, 1, scopes: "[]");
        SeedUser(denied.Users, 2, scopes: "[]");

        Assert.Null(await denied.UserPrivacy.GetVisibleUserOrNullAsync(0));
        Assert.Null(await denied.UserPrivacy.GetVisibleUserOrNullAsync(2));

        var owner = NewHarness(new ManagementUser(1, IsOwner: true));
        SeedUser(owner.Users, 2, scopes: "[]");
        Assert.NotNull(await owner.UserPrivacy.GetVisibleUserOrNullAsync(2));
        Assert.Null(await owner.UserPrivacy.GetVisibleUserOrNullAsync(99));
    }

    [Fact]
    public async Task RoleQueryOwnerWithAHeaderIsNarrowedToThatProject()
    {
        // UserProjectScopeReadRule runs before OwnerRule and HasSystemScope is
        // true for owners (role.go:87-91).
        var h = NewHarness(new ManagementUser(1, IsOwner: true), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["*"]""", isOwner: true);

        Assert.Equal(5, await h.RolePrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task RoleQueryWithoutAHeaderFollowsTheOwnerAndScopeRules()
    {
        var owner = NewHarness(new ManagementUser(1, IsOwner: true));
        Assert.Null(await owner.RolePrivacy.EvaluateQueryAsync());

        var noUser = NewHarness(null);
        var noUserDenied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => noUser.RolePrivacy.EvaluateQueryAsync());
        Assert.Equal(NoUserDeny, noUserDenied.Message);

        var scopeLess = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(scopeLess.Users, 1, scopes: "[]");
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => scopeLess.RolePrivacy.EvaluateQueryAsync());
        Assert.Equal(DefaultDeny, denied.Message);
    }

    [Fact]
    public async Task RoleQuerySystemReadRolesStillNarrowsWithAHeader()
    {
        // Unlike the User policy, the project rule is the first rule: a system
        // scope with a header filters by project_id.
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["read_roles"]""");

        Assert.Equal(5, await h.RolePrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task RoleMutationAllowsSystemWriteRolesWithoutAPredicate()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["write_roles"]""");

        Assert.Null(await h.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.Create, 7));
        Assert.Null(await h.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.DeleteOne, null));
    }

    [Fact]
    public async Task RoleMutationProjectScopeChecksCreateTargetsAndNarrowsWrites()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        h.Users.AssignProject(
            1,
            projectId: 5,
            isOwner: false,
            scopes: ["write_roles"]);

        Assert.Null(await h.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.Create, 5));
        Assert.Equal(
            5,
            await h.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.UpdateOne, null));
        Assert.Equal(
            5,
            await h.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.Delete, null));

        // A create targeting another project skips the rule and falls through
        // to UserWriteScopeRule, which the caller does not pass.
        var mismatch = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => h.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.Create, 7));
        Assert.Equal(DefaultDeny, mismatch.Message);
    }

    [Fact]
    public async Task RoleMutationWithoutAUserOrScopeFallsThroughToTheDenies()
    {
        var noUser = NewHarness(null);
        var noUserDenied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => noUser.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.Create, 5));
        Assert.Equal(NoUserDeny, noUserDenied.Message);

        var scopeLess = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(scopeLess.Users, 1, scopes: "[]");
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => scopeLess.RolePrivacy.EvaluateMutationAsync(RoleMutationOp.Create, 5));
        Assert.Equal(DefaultDeny, denied.Message);
    }

    [Fact]
    public async Task RoleVisibilityHidesOtherProjects()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: true), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["*"]""", isOwner: true);
        var visible = SeedRole(h.Roles, "Admin", projectId: 5);
        var hidden = SeedRole(h.Roles, "Viewer", projectId: 7);

        Assert.Equal(visible.Id, (await h.RolePrivacy.GetVisibleRoleOrThrowAsync(visible.Id)).Id);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.RolePrivacy.GetVisibleRoleOrThrowAsync(hidden.Id));
        Assert.Equal("ent: role not found", error.Message);
    }

    [Fact]
    public async Task ProjectQueryOwnerWithAHeaderIsNarrowedToThatProject()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: true), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["*"]""", isOwner: true);

        Assert.Equal(5, await h.ProjectPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task ProjectQueryMemberWithReadProjectsNarrowsToTheHeader()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        h.Users.AssignProject(
            1,
            projectId: 5,
            isOwner: false,
            scopes: ["read_projects"]);

        Assert.Equal(5, await h.ProjectPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task ProjectQueryWithoutAUserOrScopeFallsThroughToTheDenies()
    {
        var noUser = NewHarness(null);
        var noUserDenied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => noUser.ProjectPrivacy.EvaluateQueryAsync());
        Assert.Equal(NoUserDeny, noUserDenied.Message);

        var scopeLess = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(scopeLess.Users, 1, scopes: "[]");
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => scopeLess.ProjectPrivacy.EvaluateQueryAsync());
        Assert.Equal(DefaultDeny, denied.Message);
    }

    [Fact]
    public async Task ProjectMutationRequiresTheSystemWriteProjectsScope()
    {
        // project.go:129-132: OwnerRule -> UserWriteScopeRule(write_projects).
        var allowed = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(allowed.Users, 1, scopes: """["write_projects"]""");
        await allowed.ProjectPrivacy.EvaluateMutationAsync();

        var owner = NewHarness(new ManagementUser(1, IsOwner: true));
        await owner.ProjectPrivacy.EvaluateMutationAsync();
    }

    [Fact]
    public async Task ProjectMutationDoesNotConsultProjectMembership()
    {
        // The Project chain never runs UserProjectScopeWriteRule, so the
        // *ent.ProjectMutation branch is unreachable here: even the membership
        // owner of the target project with project-level write_projects falls
        // through to UserWriteScopeRule and ends at default deny.
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        h.Users.AssignProject(
            1,
            projectId: 5,
            isOwner: true,
            scopes: ["write_projects"]);

        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => h.ProjectPrivacy.EvaluateMutationAsync());
        Assert.Equal(DefaultDeny, denied.Message);
    }

    [Fact]
    public async Task ProjectMutationWithoutAUserOrScopeFallsThroughToTheDenies()
    {
        var noUser = NewHarness(null);
        var noUserDenied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => noUser.ProjectPrivacy.EvaluateMutationAsync());
        Assert.Equal(NoUserDeny, noUserDenied.Message);

        var scopeLess = NewHarness(new ManagementUser(1, IsOwner: false));
        SeedUser(scopeLess.Users, 1, scopes: "[]");
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => scopeLess.ProjectPrivacy.EvaluateMutationAsync());
        Assert.Equal(DefaultDeny, denied.Message);
    }

    [Fact]
    public async Task ProjectVisibilityHidesOtherProjects()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: true), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["*"]""", isOwner: true);
        h.Projects.Add(project => project.Id = 5);
        h.Projects.Add(project => project.Id = 7);

        Assert.NotNull(await h.ProjectPrivacy.GetVisibleProjectAsync(5));
        Assert.Null(await h.ProjectPrivacy.GetVisibleProjectAsync(7));
    }

    [Fact]
    public async Task UserProjectQueryScopeLessCallerSeesOnlyTheirOwnRows()
    {
        // UserOwnedQueryRule applies because *ent.UserProjectFilter has
        // WhereUserID (user_project.go:69-73).
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");

        Assert.Equal(
            new UserProjectAccessFilter { UserId = 1 },
            await h.UserProjectPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task UserProjectQueryScopesNarrowToTheHeader()
    {
        var system = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(system.Users, 1, scopes: """["read_users"]""");
        Assert.Equal(
            new UserProjectAccessFilter { ProjectId = 5 },
            await system.UserProjectPrivacy.EvaluateQueryAsync());

        var member = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(member.Users, 1, scopes: "[]");
        member.Users.AssignProject(
            1,
            projectId: 5,
            isOwner: false,
            scopes: ["read_users"]);
        Assert.Equal(
            new UserProjectAccessFilter { ProjectId = 5 },
            await member.UserProjectPrivacy.EvaluateQueryAsync());

        var owner = NewHarness(new ManagementUser(1, IsOwner: true), projectId: 5);
        Assert.Null(await owner.UserProjectPrivacy.EvaluateQueryAsync());
    }

    [Fact]
    public async Task UserProjectQueryWithoutAUserDeniesWithNoUser()
    {
        var h = NewHarness(null);

        var error = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => h.UserProjectPrivacy.EvaluateQueryAsync());

        Assert.Equal(NoUserDeny, error.Message);
    }

    [Fact]
    public async Task UserProjectMutationCreateMustTargetTheHeaderProject()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: "[]");
        h.Users.AssignProject(
            1,
            projectId: 5,
            isOwner: false,
            scopes: ["write_users"]);

        Assert.Null(
            await h.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Create, 5));
        Assert.Equal(
            5,
            await h.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Update, 5));
        Assert.Equal(
            5,
            await h.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Delete, 5));

        var mismatch = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => h.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Create, 6));
        Assert.Equal(DefaultDeny, mismatch.Message);
    }

    [Fact]
    public async Task UserProjectMutationWithoutAUserIsADefaultDeny()
    {
        // The UserProject mutation chain has no UserWriteScopeRule, so a
        // missing user ends at the policy default rather than no-user.
        var noUser = NewHarness(null);
        var error = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => noUser.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Create, 5));
        Assert.Equal(DefaultDeny, error.Message);

        var scopeLess = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(scopeLess.Users, 1, scopes: "[]");
        var denied = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => scopeLess.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Update, 5));
        Assert.Equal(DefaultDeny, denied.Message);
    }

    [Fact]
    public async Task UserProjectMutationSystemWriteUsersAllowsWithoutAPredicate()
    {
        var h = NewHarness(new ManagementUser(1, IsOwner: false), projectId: 5);
        SeedUser(h.Users, 1, scopes: """["write_users"]""");

        Assert.Null(
            await h.UserProjectPrivacy.EvaluateMutationAsync(UserProjectMutationOp.Create, 7));
    }

    private static Harness NewHarness(ManagementUser? user, long? projectId = null)
    {
        var users = new FakeUserAdminStore();
        var roles = new FakeRoleStore();
        var projects = new FakeProjectStore();
        users.RolesSource = roles;
        var identity = new FakeManagementIdentity(user) { ProjectId = projectId };

        return new Harness(
            users,
            roles,
            projects,
            new UserPrivacy(identity, users),
            new RolePrivacy(identity, users, roles),
            new ProjectPrivacy(identity, users, projects),
            new UserProjectPrivacy(identity, users));
    }

    private static void SeedUser(
        FakeUserAdminStore users,
        long id,
        string scopes = "[]",
        bool isOwner = false)
    {
        users.CreateAsync(
                new UserRecord
                {
                    Id = id,
                    Email = $"user{id}@example.com",
                    Status = "activated",
                    IsOwner = isOwner,
                    Scopes = scopes,
                },
                roleIds: null)
            .GetAwaiter()
            .GetResult();
    }

    private static RoleRecord SeedRole(
        FakeRoleStore roles,
        string name,
        long projectId,
        string level = RoleManagementService.LevelProject)
    {
        var now = DateTime.UtcNow;
        var role = new RoleRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Name = name,
            Level = level,
            ProjectId = projectId,
        };

        roles.CreateAsync(role).GetAwaiter().GetResult();
        return role;
    }

    private sealed record Harness(
        FakeUserAdminStore Users,
        FakeRoleStore Roles,
        FakeProjectStore Projects,
        UserPrivacy UserPrivacy,
        RolePrivacy RolePrivacy,
        ProjectPrivacy ProjectPrivacy,
        UserProjectPrivacy UserProjectPrivacy);
}
