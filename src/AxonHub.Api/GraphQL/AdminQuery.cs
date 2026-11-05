using HotChocolate;
using HotChocolate.Types;
using Microsoft.Extensions.DependencyInjection;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
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

    /// <summary>Port of queryResolver.Projects (ent.resolvers.go:439-452).</summary>
    [GraphQLName("projects")]
    public async Task<ProjectConnectionType> ProjectsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ProjectOrder? orderBy,
        ProjectWhereInput? where,
        [Service] ProjectManagementService projects,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = ProjectMapping.ToSortField(orderBy);
        var page = await projects.ListAsync(
                new ProjectListQuery
                {
                    Where = ProjectMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.APIKeys (ent.resolvers.go:340-354).</summary>
    [GraphQLName("apiKeys")]
    public async Task<ApiKeyConnectionType> ApiKeysAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ApiKeyOrder? orderBy,
        ApiKeyWhereInput? where,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = ApiKeyMapping.ToSortField(orderBy);
        var page = await apiKeys.ListAsync(
                new ApiKeyListQuery
                {
                    Where = ApiKeyMapping.ToFilter(where),
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

    /// <summary>
    /// Port of queryResolver.Models (ent.resolvers.go:416-424).
    /// </summary>
    /// <remarks>
    /// The reference resolver does not call validatePaginationArgs or
    /// validateFirstLast, so the port does not either. A CREATED_AT order is
    /// rewritten to the default (id) sort field.
    /// </remarks>
    [GraphQLName("models")]
    public async Task<ModelConnectionType> ModelsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ModelOrder? orderBy,
        ModelWhereInput? where,
        [Service] ModelManagementService models,
        CancellationToken cancellationToken)
    {
        var sortField = ModelMapping.ToSortField(orderBy);
        var page = await models.ListAsync(
                new ModelListQuery
                {
                    Where = ModelMapping.ToFilter(where),
                    SortField = sortField,
                    Direction = ModelMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ModelMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of queryResolver.Prompts (ent.resolvers.go:465-478).</summary>
    /// <remarks>
    /// The reference validates pagination and rewrites a CREATED_AT order to
    /// ent.DefaultPromptOrder's field (id) before paginating; the list itself
    /// runs through the Prompt schema's query policy (C188).
    /// </remarks>
    [GraphQLName("prompts")]
    public async Task<PromptConnectionType> PromptsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        PromptOrder? orderBy,
        PromptWhereInput? where,
        [Service] PromptManagementService prompts,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = PromptMapping.ToSortField(orderBy);
        var page = await prompts.ListAsync(
                new PromptListQuery
                {
                    Where = PromptMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.QueryModels (model.resolvers.go:160-227).</summary>
    [GraphQLName("queryModels")]
    public async Task<IReadOnlyList<ModelIdentityWithStatusType>> QueryModelsAsync(
        QueryModelsInput input,
        [Service] ModelManagementService models,
        CancellationToken cancellationToken)
    {
        var identities = await models.QueryModelsAsync(
                ModelMapping.ToCommand(input),
                cancellationToken)
            .ConfigureAwait(false);

        return [.. identities.Select(ModelMapping.ToGraphQL)];
    }

    /// <summary>
    /// Port of queryResolver.QueryModelChannelConnections
    /// (model.resolvers.go:229-231).
    /// </summary>
    [GraphQLName("queryModelChannelConnections")]
    public async Task<IReadOnlyList<ModelChannelConnectionType>>
        QueryModelChannelConnectionsAsync(
            IReadOnlyList<ModelAssociationInput> associations,
            [Service] ModelManagementService models,
            CancellationToken cancellationToken)
    {
        var connections = await models.QueryModelChannelConnectionsAsync(
                [.. associations.Select(static association =>
                    (ModelAssociation?)SystemSettingsMapping.ToDomain(association))],
                cancellationToken)
            .ConfigureAwait(false);

        return [.. connections.Select(ModelMapping.ToGraphQL)];
    }

    /// <summary>
    /// Port of queryResolver.QueryUnassociatedChannels
    /// (model.resolvers.go:233-235).
    /// </summary>
    [GraphQLName("queryUnassociatedChannels")]
    public async Task<IReadOnlyList<UnassociatedChannelType>> QueryUnassociatedChannelsAsync(
        [Service] ModelManagementService models,
        CancellationToken cancellationToken)
    {
        var channels = await models.QueryUnassociatedChannelsAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. channels.Select(ModelMapping.ToGraphQL)];
    }

    /// <summary>Port of queryResolver.FetchModels (model.resolvers.go:126-152).</summary>
    [GraphQLName("fetchModels")]
    public async Task<FetchModelsPayloadType> FetchModelsAsync(
        FetchModelsInputType input,
        [Service] ModelManagementService models,
        CancellationToken cancellationToken)
        => ModelMapping.ToGraphQL(
            await models.FetchModelsAsync(
                    ModelMapping.ToDomain(input),
                    cancellationToken)
                .ConfigureAwait(false));

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

    /// <summary>Port of queryResolver.Traces (ent.resolvers.go:557-570).</summary>
    /// <remarks>
    /// The reference validates pagination, rewrites a CREATED_AT order to
    /// ent.DefaultTraceOrder's field (id) and then paginates; the list runs
    /// through the Trace schema's query policy (C189).
    /// </remarks>
    [GraphQLName("traces")]
    public async Task<TraceConnectionType> TracesAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        TraceOrder? orderBy,
        TraceWhereInput? where,
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = TraceMapping.ToRootTraceSortField(orderBy);
        var page = await traces.ListAsync(
                new TraceListQuery
                {
                    Where = TraceMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.Threads (ent.resolvers.go:541-554).</summary>
    [GraphQLName("threads")]
    public async Task<ThreadConnectionType> ThreadsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        ThreadOrder? orderBy,
        ThreadWhereInput? where,
        [Service] ThreadManagementService threads,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = TraceMapping.ToRootThreadSortField(orderBy);
        var page = await threads.ListAsync(
                new ThreadListQuery
                {
                    Where = TraceMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.Requests (ent.resolvers.go:496-511).</summary>
    [GraphQLName("requests")]
    public async Task<RequestConnectionType> RequestsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RequestOrder? orderBy,
        RequestWhereInput? where,
        [Service] RequestManagementService requests,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = TraceMapping.ToRootRequestSortField(orderBy);
        var page = await requests.ListAsync(
                new RequestListQuery
                {
                    Where = TraceMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.UsageLogs (ent.resolvers.go:572-586).</summary>
    [GraphQLName("usageLogs")]
    public async Task<UsageLogConnectionType> UsageLogsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        UsageLogOrder? orderBy,
        UsageLogWhereInput? where,
        [Service] UsageLogManagementService usageLogs,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidatePaginationArgs(first, last);
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = RequestMapping.ToRootUsageLogSortField(orderBy);
        var page = await usageLogs.ListAsync(
                new UsageLogListQuery
                {
                    Where = RequestMapping.ToFilter(where),
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

    /// <summary>Port of queryResolver.Node (ent.resolvers.go:324-332).</summary>
    /// <remarks>
    /// The reference maps every ent type string to a Noder table lookup; the
    /// port resolves Project, User, APIKey, Model, Prompt, Trace, Thread,
    /// Request and UsageLog so far and reports every other type as
    /// "unknown node type: %s" (C165).
    /// </remarks>
    [GraphQLName("node")]
    public async Task<INode?> NodeAsync(
        [GraphQLType(typeof(IdType))] string id,
        [Service] IProjectStore projects,
        [Service] IUserAdminStore users,
        [Service] IServiceProvider services,
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

            case "APIKey":
            {
                // Resolved lazily so node(Project|User) and unknown-type
                // reporting do not require the API key slice's services; the
                // production host always registers them.
                var apiKeys = services.GetRequiredService<ApiKeyManagementService>();
                var apiKey = await apiKeys.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return ApiKeyMapping.ToGraphQL(apiKey);
            }

            case "Model":
            {
                var models = services.GetRequiredService<ModelManagementService>();
                var model = await models.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return ModelMapping.ToGraphQL(model);
            }

            case "Prompt":
            {
                var prompts = services.GetRequiredService<PromptManagementService>();
                var prompt = await prompts.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return PromptMapping.ToGraphQL(prompt);
            }

            case "Trace":
            {
                var traces = services.GetRequiredService<TraceManagementService>();
                var trace = await traces.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return TraceMapping.ToGraphQL(trace);
            }

            case "Thread":
            {
                var threads = services.GetRequiredService<ThreadManagementService>();
                var thread = await threads.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return TraceMapping.ToGraphQL(thread);
            }

            case "Request":
            {
                var requests = services.GetRequiredService<RequestQueryService>();
                var request = await requests.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return TraceMapping.ToGraphQL(request);
            }

            case "UsageLog":
            {
                var usageLogs = services.GetRequiredService<UsageLogManagementService>();
                var usageLog = await usageLogs.GetNodeAsync(gid.Id, cancellationToken)
                    .ConfigureAwait(false);
                return RequestMapping.ToGraphQL(usageLog);
            }

            default:
                throw new InvalidOperationException("unknown node type: " + gid.Type);
        }
    }
}
