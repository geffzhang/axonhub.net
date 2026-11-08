using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the Relay <c>Node</c> interface (ent.graphql:2580-2586). Every
/// management-slice type that ent generates as a Node implementor does;
/// node() resolves the types listed in <c>AdminQuery.NodeAsync</c> and reports
/// the remaining ent types as "unknown node type: %s" (C165).
/// </summary>
[InterfaceType("Node")]
public interface INode
{
    /// <summary>The id of the object.</summary>
    [GraphQLType(typeof(IdType))]
    string Id { get; }
}

/// <summary>Port of the entgql <c>UserProject</c> type (ent.graphql:7336-7352).</summary>
[GraphQLName("UserProject")]
public sealed class UserProjectType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLIgnore]
    public long UserDbId { get; init; }

    [GraphQLIgnore]
    public long ProjectDbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string UserId { get; init; } = string.Empty;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectId { get; init; } = string.Empty;

    public bool IsOwner { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Port of the entgql edge resolver UserProject.user.</summary>
    [GraphQLName("user")]
    public async Task<UserType> GetUserAsync(
        [Service] IUserAdminStore users,
        CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(UserDbId, cancellationToken)
            .ConfigureAwait(false);
        return user is null
            ? throw new InvalidOperationException("ent: user not found")
            : ManagementMapping.ToGraphQL(user);
    }

    /// <summary>Port of the entgql edge resolver UserProject.project.</summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] IProjectStore projects,
        CancellationToken cancellationToken)
    {
        var project = await projects.GetByIdAsync(ProjectDbId, cancellationToken)
            .ConfigureAwait(false);
        return project is null
            ? throw new InvalidOperationException("ent: project not found")
            : UserProfileMapping.ToGraphQL(project);
    }
}

/// <summary>Port of <c>AddUserToProjectInput</c> (axonhub.graphql:674-680).</summary>
public sealed class AddUserToProjectInput
{
    [GraphQLName("projectId")]
    [GraphQLType(typeof(IdType))]
    public string ProjectId { get; set; } = string.Empty;

    [GraphQLName("userId")]
    [GraphQLType(typeof(IdType))]
    public string UserId { get; set; } = string.Empty;

    public bool? IsOwner { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    [GraphQLName("roleIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RoleIDs { get; set; }
}

/// <summary>Port of <c>UpdateProjectUserInput</c> (axonhub.graphql:682-689).</summary>
public sealed class UpdateProjectUserInput
{
    [GraphQLName("projectId")]
    [GraphQLType(typeof(IdType))]
    public string ProjectId { get; set; } = string.Empty;

    [GraphQLName("userId")]
    [GraphQLType(typeof(IdType))]
    public string UserId { get; set; } = string.Empty;

    public bool? IsOwner { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    [GraphQLName("addRoleIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? AddRoleIDs { get; set; }

    [GraphQLName("removeRoleIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RemoveRoleIDs { get; set; }
}

/// <summary>Port of <c>RemoveUserFromProjectInput</c> (axonhub.graphql:691-694).</summary>
public sealed class RemoveUserFromProjectInput
{
    [GraphQLName("projectId")]
    [GraphQLType(typeof(IdType))]
    public string ProjectId { get; set; } = string.Empty;

    [GraphQLName("userId")]
    [GraphQLType(typeof(IdType))]
    public string UserId { get; set; } = string.Empty;
}

/// <summary>
/// Port of Project.projectUsers (ent.graphql:3132). The entgql resolver is
/// obj.QueryProjectUsers().All(ctx) — no ordering, no pagination; the port
/// keeps the database's natural row order (C164).
/// </summary>
[ExtendObjectType(typeof(ProjectType))]
public sealed class ProjectTypeExtensions
{
    [GraphQLName("projectUsers")]
    public async Task<IReadOnlyList<UserProjectType>?> GetProjectUsersAsync(
        [Parent] ProjectType project,
        [Service] IUserAdminStore users,
        CancellationToken cancellationToken)
    {
        var rows = await users.ListProjectUsersAsync(project.DbId, cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ProjectUserMapping.ToGraphQL)];
    }
}

/// <summary>
/// Port of User.projectUsers (ent.graphql:7283). The entgql resolver is
/// obj.QueryProjectUsers().All(ctx) — no ordering, no pagination; the port
/// keeps the database's natural row order (C164).
/// </summary>
[ExtendObjectType(typeof(UserType))]
public sealed class UserTypeExtensions
{
    [GraphQLName("projectUsers")]
    public async Task<IReadOnlyList<UserProjectType>?> GetProjectUsersAsync(
        [Parent] UserType user,
        [Service] IUserAdminStore users,
        CancellationToken cancellationToken)
    {
        var rows = await users.ListProjectUsersForUserAsync(user.DbId, cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(ProjectUserMapping.ToGraphQL)];
    }
}

/// <summary>Domain ↔ GraphQL mapping for the UserProject slice.</summary>
public static class ProjectUserMapping
{
    public static UserProjectType ToGraphQL(UserProjectRecord record) => new()
    {
        DbId = record.Id,
        UserDbId = record.UserId,
        ProjectDbId = record.ProjectId,
        Id = new GidValue("UserProject", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        UserId = new GidValue("User", record.UserId).ToString(),
        ProjectId = new GidValue("Project", record.ProjectId).ToString(),
        IsOwner = record.IsOwner,
        Scopes = ManagementMapping.ParseScopes(record.Scopes),
    };
}
