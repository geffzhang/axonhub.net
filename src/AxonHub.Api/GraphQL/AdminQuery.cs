using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Api.Scopes;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// The GraphQL <c>ScopeInfo</c> type (internal/server/gql/scopes.graphql:3-7).
/// HotChocolate's default naming convention maps the properties to
/// <c>scope</c>, <c>description</c> and <c>levels</c>.
/// </summary>
public sealed record ScopeInfo(
    string Scope,
    string Description,
    IReadOnlyList<string> Levels);

/// <summary>
/// Port of queryResolver.AllScopes
/// (internal/server/gql/scopes.resolvers.go:15-42).
/// </summary>
/// <remarks>
/// The SDL is <c>allScopes(level: String): [ScopeInfo!]!</c>
/// (internal/server/gql/scopes.graphql:10). The resolver passes a non-nil level
/// through as-is (no validation), so an unknown value yields an empty list.
/// </remarks>
public sealed class AdminQuery
{
    /// <summary>Port of queryResolver.AllScopes.</summary>
    public IReadOnlyList<ScopeInfo> AllScopes(string? level)
        => [.. ScopeCatalog.AllScopes(level).Select(static scope => new ScopeInfo(
            scope.Slug,
            scope.Description,
            scope.Levels))];

    /// <summary>Port of queryResolver.SystemStatus (system.resolvers.go:479-488).</summary>
    [GraphQLName("systemStatus")]
    public async Task<SystemStatusType> SystemStatusAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return SystemSettingsMapping.ToGraphQL(
                await settings.IsInitializedAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to check initialization status: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of queryResolver.RetryPolicy (system.resolvers.go:519-522).</summary>
    [GraphQLName("retryPolicy")]
    public async Task<RetryPolicyType> RetryPolicyAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
        => SystemSettingsMapping.ToGraphQL(
            await settings.GetRetryPolicyAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Port of queryResolver.SystemModelSettings (system.resolvers.go:529-537).</summary>
    [GraphQLName("systemModelSettings")]
    public async Task<SystemModelSettingsType> SystemModelSettingsAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return SystemSettingsMapping.ToGraphQL(
                await settings.GetModelSettingsAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get system model settings: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of queryResolver.SecuritySettings (system.resolvers.go:639-642).</summary>
    [GraphQLName("securitySettings")]
    public async Task<SecuritySettingsType> SecuritySettingsAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
        => SystemSettingsMapping.ToGraphQL(
            await settings.GetSecuritySettingsAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Port of queryResolver.Users (ent.resolvers.go:579-592).</summary>
    [GraphQLName("users")]
    public async Task<UserConnectionType> UsersAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        UserOrder? orderBy,
        UserWhereInput? where,
        [Service] UserManagementService users,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = ManagementMapping.ToUserSortField(orderBy);
        var page = await users.ListAsync(
                new UserListQuery
                {
                    Where = ManagementMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.Roles (ent.resolvers.go:503-516).</summary>
    [GraphQLName("roles")]
    public async Task<RoleConnectionType> RolesAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RoleOrder? orderBy,
        RoleWhereInput? where,
        [Service] RoleManagementService roles,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = ManagementMapping.ToRoleSortField(orderBy);
        var page = await roles.ListAsync(
                new RoleListQuery
                {
                    Where = ManagementMapping.ToFilter(where),
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

    /// <summary>
    /// Port of queryResolver.ChannelOverrideTemplates (ent.resolvers.go:383-397).
    /// </summary>
    [GraphQLName("channelOverrideTemplates")]
    public async Task<ChannelOverrideTemplateConnectionType> ChannelOverrideTemplatesAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ChannelOverrideTemplateOrder? orderBy,
        ChannelOverrideTemplateWhereInput? where,
        [Service] ChannelOverrideTemplateService templates,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = ChannelOverrideTemplateMapping.ToSortField(orderBy);
        var page = await templates.ListAsync(
                new ChannelOverrideTemplateListQuery
                {
                    Where = ChannelOverrideTemplateMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.Channels (ent.resolvers.go:372-381).</summary>
    [GraphQLName("channels")]
    public Task<ChannelConnectionType> ChannelsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ChannelOrder? orderBy,
        ChannelWhereInput? where,
        [Service] ChannelQueryService channels,
        CancellationToken cancellationToken)
    {
        var sortField = ChannelMapping.ToSortField(orderBy);

        return channels.ChannelsAsync(
            new ChannelListQuery
            {
                Where = ChannelMapping.ToFilter(where),
                SortField = sortField,
                Direction = ChannelMapping.ToSortDirection(orderBy),
                Ordered = orderBy is not null,
                After = ManagementMapping.ToListCursor(after),
                Before = ManagementMapping.ToListCursor(before),
                First = first,
                Last = last,
            },
            sortField,
            cancellationToken);
    }

    /// <summary>Port of queryResolver.QueryChannels (axonhub.resolvers.go:977-983).</summary>
    [GraphQLName("queryChannels")]
    public Task<ChannelConnectionType> QueryChannelsAsync(
        QueryChannelInput input,
        [Service] ChannelQueryService channels,
        CancellationToken cancellationToken)
        => channels.QueryChannelsAsync(input, cancellationToken);

    /// <summary>Port of queryResolver.AllChannelSummarys (non-project branch).</summary>
    [GraphQLName("allChannelSummarys")]
    public Task<IReadOnlyList<ChannelType>> AllChannelSummarysAsync(
        bool? includeArchived,
        [Service] ChannelQueryService channels,
        CancellationToken cancellationToken)
        => channels.AllChannelSummarysAsync(includeArchived == true, cancellationToken);

    /// <summary>Port of queryResolver.AllChannelTags.</summary>
    [GraphQLName("allChannelTags")]
    public Task<IReadOnlyList<string>> AllChannelTagsAsync(
        [Service] ChannelQueryService channels,
        CancellationToken cancellationToken)
        => channels.AllChannelTagsAsync(cancellationToken);

    /// <summary>Port of queryResolver.CountChannelsByType.</summary>
    [GraphQLName("countChannelsByType")]
    public Task<IReadOnlyList<ChannelTypeCountType>> CountChannelsByTypeAsync(
        CountChannelsByTypeInput input,
        [Service] ChannelQueryService channels,
        CancellationToken cancellationToken)
        => channels.CountChannelsByTypeAsync(input, cancellationToken);

    /// <summary>Port of queryResolver.Me (me.resolvers.go:112-125).</summary>
    [GraphQLName("me")]
    public async Task<UserInfoType> MeAsync(
        [Service] UserProfileService profiles,
        CancellationToken cancellationToken)
        => UserProfileMapping.ToGraphQL(
            await profiles.GetMeAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Port of queryResolver.MyProjects (me.resolvers.go:127-140).</summary>
    [GraphQLName("myProjects")]
    public async Task<IReadOnlyList<ProjectType>> MyProjectsAsync(
        [Service] UserProfileService profiles,
        CancellationToken cancellationToken)
    {
        var projects = await profiles.GetMyProjectsAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. projects.Select(UserProfileMapping.ToGraphQL)];
    }

    /// <summary>Port of queryResolver.Node (ent.resolvers.go:324-332).</summary>
    /// <remarks>
    /// The reference maps every ent type string to a Noder table lookup; the
    /// port resolves only Project and User so far and reports every other type
    /// as "unknown node type: %s" (C165).
    /// </remarks>
    [GraphQLName("node")]
    public async Task<INode?> NodeAsync(
        [GraphQLType(typeof(IdType))] string id,
        [Service] IProjectStore projects,
        [Service] IUserAdminStore users,
        CancellationToken cancellationToken)
    {
        var gid = GidValue.Parse(id);

        switch (gid.Type)
        {
            case "Project":
            {
                var project = await projects.GetByIdAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return project is null
                    ? throw new InvalidOperationException("ent: project not found")
                    : UserProfileMapping.ToGraphQL(project);
            }

            case "User":
            {
                var user = await users.GetByIdAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return user is null
                    ? throw new InvalidOperationException("ent: user not found")
                    : ManagementMapping.ToGraphQL(user);
            }

            default:
                throw new InvalidOperationException("unknown node type: " + gid.Type);
        }
    }
}
