using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the Relay <c>Node</c> interface (ent.graphql:2580-2586). Every
/// management-slice type that ent generates as a Node implementor does;
/// node() resolves the 19 types in the reference's guidTypeToNodeType map and
/// reports every other ent type as "unknown node type: %s".
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
        [Service] UserPrivacy privacy,
        CancellationToken cancellationToken)
    {
        // obj.QueryUser().Only runs the User query policy.
        var user = await privacy.GetVisibleUserOrThrowAsync(UserDbId, cancellationToken)
            .ConfigureAwait(false);
        return ManagementMapping.ToGraphQL(user);
    }

    /// <summary>Port of the entgql edge resolver UserProject.project.</summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] ProjectPrivacy privacy,
        CancellationToken cancellationToken)
    {
        // obj.QueryProject().Only runs the Project query policy.
        var project = await privacy.GetVisibleProjectAsync(ProjectDbId, cancellationToken)
            .ConfigureAwait(false);
        return project is null
            ? throw new InvalidOperationException("ent: project not found")
            : UserProfileMapping.ToGraphQL(project);
    }
}

/// <summary>Port of the entgql <c>UserRole</c> type (ent.graphql:7420-7428).</summary>
/// <remarks>
/// The join table declares no mixins and no privacy policy, so node(UserRole)
/// reads the row as-is; the user/role edges still run those schemas' query
/// policies through their required-edge resolvers.
/// </remarks>
[GraphQLName("UserRole")]
public sealed class UserRoleType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLIgnore]
    public long UserDbId { get; init; }

    [GraphQLIgnore]
    public long RoleDbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string UserId { get; init; } = string.Empty;

    [GraphQLName("roleID")]
    [GraphQLType(typeof(IdType))]
    public string RoleId { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAt { get; init; }

    /// <summary>Port of the entgql edge resolver UserRole.user.</summary>
    [GraphQLName("user")]
    public async Task<UserType> GetUserAsync(
        [Service] UserPrivacy privacy,
        CancellationToken cancellationToken)
    {
        // obj.QueryUser().Only runs the User query policy.
        var user = await privacy.GetVisibleUserOrThrowAsync(UserDbId, cancellationToken)
            .ConfigureAwait(false);
        return ManagementMapping.ToGraphQL(user);
    }

    /// <summary>Port of the entgql edge resolver UserRole.role.</summary>
    [GraphQLName("role")]
    public async Task<RoleType> GetRoleAsync(
        [Service] RolePrivacy privacy,
        CancellationToken cancellationToken)
    {
        // obj.QueryRole().Only runs the Role query policy.
        var role = await privacy.GetVisibleRoleOrThrowAsync(RoleDbId, cancellationToken)
            .ConfigureAwait(false);
        return ManagementMapping.ToGraphQL(role);
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
/// Port of the entgql Project edges (ent.graphql:2836-3132; gql_edge.go:245-433):
/// nine generated connection resolvers plus the projectUsers list. Each
/// connection routes through its entity's query policy, so the port calls the
/// management service (which evaluates privacy) and ANDs the edge's foreign-key
/// predicate into the user-supplied where filter — the services overwrite their
/// own ProjectScope field, so the FK must not travel there.
/// </summary>
[ExtendObjectType(typeof(ProjectType))]
public sealed class ProjectTypeExtensions
{
    /// <summary>Port of Project.users (gql_edge.go:245-265).</summary>
    [GraphQLName("users")]
    public async Task<UserConnectionType> GetUsersAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        UserOrder? orderBy,
        UserWhereInput? where,
        [Service] UserManagementService users,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            ManagementMapping.ToFilter(where),
            new UserListFilter { HasProjectUsersWithProjectId = project.DbId });

        var sortField = ManagementMapping.ToUserEdgeSortField(orderBy);
        var page = await users.ListAsync(
                new UserListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ManagementMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.roles (gql_edge.go:266-286).</summary>
    [GraphQLName("roles")]
    public async Task<RoleConnectionType> GetRolesAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RoleOrder? orderBy,
        RoleWhereInput? where,
        [Service] RoleManagementService roles,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            ManagementMapping.ToFilter(where),
            new RoleListFilter { ProjectId = project.DbId });

        var sortField = ManagementMapping.ToRoleEdgeSortField(orderBy);
        var page = await roles.ListAsync(
                new RoleListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ManagementMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.apiKeys (gql_edge.go:287-307).</summary>
    [GraphQLName("apiKeys")]
    public async Task<ApiKeyConnectionType> GetApiKeysAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ApiKeyOrder? orderBy,
        ApiKeyWhereInput? where,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            ApiKeyMapping.ToFilter(where),
            new ApiKeyListFilter { ProjectId = project.DbId });

        var sortField = ApiKeyMapping.ToEdgeSortField(orderBy);
        var page = await apiKeys.ListAsync(
                new ApiKeyListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ApiKeyMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ApiKeyMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.requests (gql_edge.go:308-328).</summary>
    [GraphQLName("requests")]
    public async Task<RequestConnectionType> GetRequestsAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RequestOrder? orderBy,
        RequestWhereInput? where,
        [Service] RequestManagementService requests,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            TraceMapping.ToFilter(where),
            new RequestListFilter { ProjectId = project.DbId });

        var sortField = TraceMapping.ToEdgeRequestSortField(orderBy);
        var page = await requests.ListAsync(
                new RequestListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = TraceMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TraceMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.usageLogs (gql_edge.go:329-349).</summary>
    [GraphQLName("usageLogs")]
    public async Task<UsageLogConnectionType> GetUsageLogsAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        UsageLogOrder? orderBy,
        UsageLogWhereInput? where,
        [Service] UsageLogManagementService usageLogs,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            RequestMapping.ToFilter(where),
            new UsageLogListFilter { ProjectId = project.DbId });

        var sortField = RequestMapping.ToEdgeUsageLogSortField(orderBy);
        var page = await usageLogs.ListAsync(
                new UsageLogListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = RequestMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return RequestMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.threads (gql_edge.go:350-370).</summary>
    [GraphQLName("threads")]
    public async Task<ThreadConnectionType> GetThreadsAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ThreadOrder? orderBy,
        ThreadWhereInput? where,
        [Service] ThreadManagementService threads,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            TraceMapping.ToFilter(where),
            new ThreadListFilter { ProjectId = project.DbId });

        var sortField = TraceMapping.ToEdgeThreadSortField(orderBy);
        var page = await threads.ListAsync(
                new ThreadListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = TraceMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TraceMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.traces (gql_edge.go:371-391).</summary>
    [GraphQLName("traces")]
    public async Task<TraceConnectionType> GetTracesAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        TraceOrder? orderBy,
        TraceWhereInput? where,
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            TraceMapping.ToFilter(where),
            new TraceListFilter { ProjectId = project.DbId });

        var sortField = TraceMapping.ToEdgeTraceSortField(orderBy);
        var page = await traces.ListAsync(
                new TraceListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = TraceMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TraceMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.prompts (gql_edge.go:392-412).</summary>
    [GraphQLName("prompts")]
    public async Task<PromptConnectionType> GetPromptsAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        PromptOrder? orderBy,
        PromptWhereInput? where,
        [Service] PromptManagementService prompts,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            PromptMapping.ToFilter(where),
            new PromptListFilter { ProjectId = project.DbId });

        var sortField = PromptMapping.ToEdgeSortField(orderBy);
        var page = await prompts.ListAsync(
                new PromptListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = PromptMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return PromptMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of Project.apiKeyProfileTemplates (gql_edge.go:413-433).</summary>
    [GraphQLName("apiKeyProfileTemplates")]
    public async Task<ApiKeyProfileTemplateConnectionType> GetApiKeyProfileTemplatesAsync(
        [Parent] ProjectType project,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ApiKeyProfileTemplateOrder? orderBy,
        ApiKeyProfileTemplateWhereInput? where,
        [Service] ApiKeyProfileTemplateService templates,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            ApiKeyProfileTemplateMapping.ToFilter(where),
            new ApiKeyProfileTemplateListFilter { ProjectId = project.DbId });

        var sortField = ApiKeyProfileTemplateMapping.ToEdgeSortField(orderBy);
        var page = await templates.ListAsync(
                new ApiKeyProfileTemplateListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ApiKeyProfileTemplateMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ApiKeyProfileTemplateMapping.BuildConnection(page, sortField);
    }

    /// <summary>
    /// Port of Project.projectUsers (ent.graphql:3132). The entgql resolver is
    /// obj.QueryProjectUsers().All(ctx) — no ordering, no pagination; the port
    /// keeps the database's natural row order (C164).
    /// </summary>
    [GraphQLName("projectUsers")]
    public async Task<IReadOnlyList<UserProjectType>?> GetProjectUsersAsync(
        [Parent] ProjectType project,
        [Service] IUserAdminStore users,
        [Service] UserProjectPrivacy privacy,
        CancellationToken cancellationToken)
    {
        // obj.QueryProjectUsers().All runs the UserProject query policy: a
        // project-scoped caller sees the project's rows, a scope-less caller
        // only their own.
        var queryScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await users
            .ListProjectUsersAsync(project.DbId, cancellationToken, queryScope)
            .ConfigureAwait(false);
        return [.. rows.Select(ProjectUserMapping.ToGraphQL)];
    }
}

/// <summary>
/// Port of the entgql User edges (ent.graphql:7128-7285; gql_edge.go:717-862):
/// the five generated connections plus the projectUsers/userRoles lists.
/// </summary>
[ExtendObjectType(typeof(UserType))]
public sealed class UserTypeExtensions
{
    /// <summary>Port of User.projects (gql_edge.go:717-737).</summary>
    [GraphQLName("projects")]
    public async Task<ProjectConnectionType> GetProjectsAsync(
        [Parent] UserType user,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ProjectOrder? orderBy,
        ProjectWhereInput? where,
        [Service] ProjectManagementService projects,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        // The edge predicate is user_projects.user_id = X; ProjectStore renders
        // HasUsersWith as an EXISTS over user_projects JOIN users.
        var filter = ProjectUserEdgeFilters.And(
            ProjectMapping.ToFilter(where),
            new ProjectListFilter
            {
                HasUsersWith = [new UserListFilter { Id = user.DbId }],
            });

        var sortField = ProjectMapping.ToEdgeSortField(orderBy);
        var page = await projects.ListAsync(
                new ProjectListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ProjectMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ProjectMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of User.apiKeys (gql_edge.go:738-779).</summary>
    [GraphQLName("apiKeys")]
    public async Task<ApiKeyConnectionType> GetApiKeysAsync(
        [Parent] UserType user,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ApiKeyOrder? orderBy,
        ApiKeyWhereInput? where,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            ApiKeyMapping.ToFilter(where),
            new ApiKeyListFilter { UserId = user.DbId });

        var sortField = ApiKeyMapping.ToEdgeSortField(orderBy);
        var page = await apiKeys.ListAsync(
                new ApiKeyListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ApiKeyMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ApiKeyMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of User.channelOverrideTemplates (gql_edge.go:780-800).</summary>
    [GraphQLName("channelOverrideTemplates")]
    public async Task<ChannelOverrideTemplateConnectionType> GetChannelOverrideTemplatesAsync(
        [Parent] UserType user,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ChannelOverrideTemplateOrder? orderBy,
        ChannelOverrideTemplateWhereInput? where,
        [Service] ChannelOverrideTemplateService templates,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var filter = ProjectUserEdgeFilters.And(
            ChannelOverrideTemplateMapping.ToFilter(where),
            new ChannelOverrideTemplateListFilter { UserId = user.DbId });

        var sortField = ChannelOverrideTemplateMapping.ToEdgeSortField(orderBy);
        var page = await templates.ListAsync(
                new ChannelOverrideTemplateListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = ChannelOverrideTemplateMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ChannelOverrideTemplateMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of User.oidcIdentities (gql_edge.go:801-842).</summary>
    [GraphQLName("oidcIdentities")]
    public async Task<OidcIdentityConnectionType> GetOidcIdentitiesAsync(
        [Parent] UserType user,
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        OidcIdentityOrder? orderBy,
        OidcIdentityWhereInput? where,
        [Service] IOidcIdentityAdminStore identities,
        [Service] SystemPrivacy privacy,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        // The OIDCIdentity policy's UserOwnedQueryRule filters non-owner
        // callers to their own rows (same evaluation as the root resolver).
        var ownerFilter = await privacy
            .EvaluateOidcIdentityQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        var edge = new OidcIdentityListFilter { UserId = user.DbId };
        var filter = ProjectUserEdgeFilters.And(SystemAdminMapping.ToFilter(where), edge);
        if (ownerFilter is not null)
        {
            filter = ProjectUserEdgeFilters.And(filter, new OidcIdentityListFilter { UserId = ownerFilter });
        }

        var sortField = SystemAdminMapping.ToSortField(orderBy);
        var page = await identities.ListAsync(
                new OidcIdentityListQuery
                {
                    Where = filter,
                    SortField = sortField,
                    Direction = SystemAdminMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return SystemAdminMapping.BuildConnection(page, sortField);
    }

    /// <summary>
    /// Port of User.userRoles (ent.graphql:7284). The SDL narrows the entgql
    /// connection to a plain list, and the reference runs
    /// obj.QueryUserRoles().All(ctx): no ordering, no privacy policy.
    /// </summary>
    [GraphQLName("userRoles")]
    public async Task<IReadOnlyList<UserRoleType>?> GetUserRolesAsync(
        [Parent] UserType user,
        [Service] IRoleStore roles,
        CancellationToken cancellationToken)
    {
        var rows = await roles.ListUserRolesForUserAsync(user.DbId, cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(UserRoleMapping.ToGraphQL)];
    }

    /// <summary>
    /// Port of User.projectUsers (ent.graphql:7283). The entgql resolver is
    /// obj.QueryProjectUsers().All(ctx) — no ordering, no pagination; the port
    /// keeps the database's natural row order (C164).
    /// </summary>
    [GraphQLName("projectUsers")]
    public async Task<IReadOnlyList<UserProjectType>?> GetProjectUsersAsync(
        [Parent] UserType user,
        [Service] IUserAdminStore users,
        [Service] UserProjectPrivacy privacy,
        CancellationToken cancellationToken)
    {
        // obj.QueryProjectUsers().All runs the UserProject query policy (see
        // ProjectTypeExtensions.GetProjectUsersAsync).
        var queryScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await users
            .ListProjectUsersForUserAsync(user.DbId, cancellationToken, queryScope)
            .ConfigureAwait(false);
        return [.. rows.Select(ProjectUserMapping.ToGraphQL)];
    }
}

/// <summary>
/// ANDs an entgql edge's foreign-key predicate into the caller's where filter.
/// The filter records only nest; combining keeps both predicates visible to the
/// stores' generated-SQL renderer.
/// </summary>
internal static class ProjectUserEdgeFilters
{
    public static ProjectListFilter? And(ProjectListFilter? where, ProjectListFilter edge)
        => where is null ? edge : new ProjectListFilter { And = [where, edge] };

    public static UserListFilter? And(UserListFilter? where, UserListFilter edge)
        => where is null ? edge : new UserListFilter { And = [where, edge] };

    public static RoleListFilter? And(RoleListFilter? where, RoleListFilter edge)
        => where is null ? edge : new RoleListFilter { And = [where, edge] };

    public static ApiKeyListFilter? And(ApiKeyListFilter? where, ApiKeyListFilter edge)
        => where is null ? edge : new ApiKeyListFilter { And = [where, edge] };

    public static RequestListFilter? And(RequestListFilter? where, RequestListFilter edge)
        => where is null ? edge : new RequestListFilter { And = [where, edge] };

    public static UsageLogListFilter? And(UsageLogListFilter? where, UsageLogListFilter edge)
        => where is null ? edge : new UsageLogListFilter { And = [where, edge] };

    public static RequestExecutionListFilter? And(
        RequestExecutionListFilter? where,
        RequestExecutionListFilter edge)
        => where is null ? edge : new RequestExecutionListFilter { And = [where, edge] };

    public static ThreadListFilter? And(ThreadListFilter? where, ThreadListFilter edge)
        => where is null ? edge : new ThreadListFilter { And = [where, edge] };

    public static TraceListFilter? And(TraceListFilter? where, TraceListFilter edge)
        => where is null ? edge : new TraceListFilter { And = [where, edge] };

    public static PromptListFilter? And(PromptListFilter? where, PromptListFilter edge)
        => where is null ? edge : new PromptListFilter { And = [where, edge] };

    public static ApiKeyProfileTemplateListFilter? And(
        ApiKeyProfileTemplateListFilter? where,
        ApiKeyProfileTemplateListFilter edge)
        => where is null ? edge : new ApiKeyProfileTemplateListFilter { And = [where, edge] };

    public static ChannelOverrideTemplateListFilter? And(
        ChannelOverrideTemplateListFilter? where,
        ChannelOverrideTemplateListFilter edge)
        => where is null ? edge : new ChannelOverrideTemplateListFilter { And = [where, edge] };

    public static OidcIdentityListFilter? And(
        OidcIdentityListFilter? where,
        OidcIdentityListFilter edge)
        => where is null ? edge : new OidcIdentityListFilter { And = [where, edge] };
}

/// <summary>Domain → GraphQL projection for the UserRole join rows.</summary>
public static class UserRoleMapping
{
    public static UserRoleType ToGraphQL(UserRoleRecord record) => new()
    {
        DbId = record.Id,
        UserDbId = record.UserId,
        RoleDbId = record.RoleId,
        Id = new GidValue("UserRole", record.Id).ToString(),
        UserId = new GidValue("User", record.UserId).ToString(),
        RoleId = new GidValue("Role", record.RoleId).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
    };
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
