using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel read paths: the entgql <c>channels</c> connection and
/// biz.ChannelService.QueryChannels, plus the allChannelSummarys /
/// allChannelTags / countChannelsByType query resolvers. Every root applies
/// the Channel query policy before touching the store (C014); the summary and
/// tag roots also carry the reference's project branch (context project id,
/// available-channel scope checks and the project-profile filter).
/// </summary>
public sealed class ChannelQueryService(
    IChannelAdminStore channels,
    ChannelPrivacy privacy,
    AuthorizationScope auth,
    IManagementIdentity identity,
    IProjectStore projects)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Port of the entgql Node lookup for Channel (gql_node.go:247-255):
    /// <c>Channel.Query().Where(channel.ID(id)).Only(ctx)</c> with the query
    /// policy applied. A missing live row surfaces ent's NotFoundError.
    /// </summary>
    /// <exception cref="PrivacyDenyException">The query policy denied.</exception>
    public async Task<ChannelType> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var record = await channels.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        return record is null
            ? throw new InvalidOperationException("ent: channel not found")
            : ChannelMapping.ToGraphQL(record);
    }

    /// <summary>Port of queryResolver.Channels (entgql Paginate).</summary>
    public async Task<ChannelConnectionType> ChannelsAsync(
        ChannelListQuery query,
        ChannelSortField sortField,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Paginate validates before doing any work; this is the same order as
        // gqlgen, and it is why the model-filter branch below has no validation.
        ManagementValidation.ValidateFirstLast(query.First, query.Last);

        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var page = await channels.ListAsync(query, cancellationToken).ConfigureAwait(false);
        return ChannelMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of biz.ChannelService.QueryChannels (channel_query.go).</summary>
    public async Task<ChannelConnectionType> QueryChannelsAsync(
        QueryChannelInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var sortField = ChannelMapping.ToSortField(input.OrderBy);
        var direction = ChannelMapping.ToSortDirection(input.OrderBy);
        var filter = ChannelMapping.ToFilter(input.Where);
        var hasTag = string.IsNullOrEmpty(input.HasTag) ? null : input.HasTag;

        if (string.IsNullOrEmpty(input.Model))
        {
            return await ChannelsAsync(
                    new ChannelListQuery
                    {
                        Where = filter,
                        HasTag = hasTag,
                        SortField = sortField,
                        Direction = direction,
                        Ordered = input.OrderBy is not null,
                        After = ToListCursor(input.After),
                        Before = ToListCursor(input.Before),
                        First = input.First,
                        Last = input.Last,
                    },
                    sortField,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Model filtering fetches every matching row and paginates in memory;
        // the pagination arguments (and their validation) are ignored.
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var rows = await channels.ListAllAsync(
                new ChannelListQuery
                {
                    Where = filter,
                    HasTag = hasTag,
                    SortField = sortField,
                    Direction = direction,
                    Ordered = input.OrderBy is not null,
                },
                cancellationToken)
            .ConfigureAwait(false);

        var model = input.Model;
        var matched = rows
            .Where(row => ChannelModelEntries
                .Build(ChannelMapping.ToDescriptor(row))
                .ContainsKey(model))
            .ToList();

        return ChannelMapping.BuildModelConnection(matched, sortField);
    }

    /// <summary>
    /// Port of queryResolver.AllChannelSummarys
    /// (axonhub.resolvers.go:823-880).
    /// </summary>
    public async Task<IReadOnlyList<ChannelType>> AllChannelSummarysAsync(
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var projectId = identity.GetCurrentProjectId();
        var canReadChannels = await auth
            .HasScopeAsync(ChannelPrivacy.ReadChannels, cancellationToken)
            .ConfigureAwait(false);

        if (projectId is null or 0)
        {
            // The reference returns the authz error unwrapped, before the query.
            if (!canReadChannels)
            {
                await auth.RequireScopeAsync(ChannelPrivacy.ReadChannels, cancellationToken)
                    .ConfigureAwait(false);
            }

            IReadOnlyList<ChannelRecord> rows;
            try
            {
                await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

                rows = await channels
                    .ListSummariesAsync(includeArchived, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to query channels: " + ex.Message,
                    ex);
            }

            return [.. rows.Select(ChannelMapping.ToGraphQL)];
        }

        if (!canReadChannels
            && !await auth.HasScopeAsync(AuthorizationScope.WriteRequests, cancellationToken)
                .ConfigureAwait(false)
            && !await auth.HasScopeAsync(AuthorizationScope.WriteApiKeys, cancellationToken)
                .ConfigureAwait(false))
        {
            // Unwrapped, like the reference's RequireScope(write_requests).
            await auth.RequireScopeAsync(AuthorizationScope.WriteRequests, cancellationToken)
                .ConfigureAwait(false);
        }

        IReadOnlyList<ChannelRecord> projectRows;
        try
        {
            if (canReadChannels)
            {
                await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
                projectRows = await channels
                    .ListSummariesAsync(includeArchived, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // authz.RunWithSystemBypass("project-available-channels"):
                // status EQ enabled, ignoring the query policy.
                projectRows = await channels
                    .ListEnabledSummariesAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query project channels: " + ex.Message,
                ex);
        }

        // The reference resolves the profile under a system bypass
        // ("project-available-channels-profile") regardless of canReadChannels.
        var project = await LoadProjectAsync(
                projectId.Value,
                systemBypass: true,
                cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. FilterChannelsByProjectProfile(
                    projectRows,
                    ProfileFilters.ActiveProjectProfile(project))
                .Select(ChannelMapping.ToGraphQL),
        ];
    }

    /// <summary>Port of queryResolver.AllChannelTags (axonhub.resolvers.go:882-909).</summary>
    public async Task<IReadOnlyList<string>> AllChannelTagsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelRecord> rows;
        try
        {
            await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

            rows = await channels
                .ListTagSourcesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query channels: " + ex.Message,
                ex);
        }

        var projectId = identity.GetCurrentProjectId();
        if (projectId is not null and not 0)
        {
            // Project.Get runs with the normal context here (no bypass), so
            // the Project query policy applies to this lookup.
            var project = await LoadProjectAsync(
                    projectId.Value,
                    systemBypass: false,
                    cancellationToken)
                .ConfigureAwait(false);

            rows = FilterChannelsByProjectProfile(
                rows,
                ProfileFilters.ActiveProjectProfile(project));
        }

        // lo.Reduce flattens the per-row tags in row order; lo.Uniq keeps the
        // first occurrence of each tag.
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var tag in ParseTags(row.Tags))
            {
                if (seen.Add(tag))
                {
                    tags.Add(tag);
                }
            }
        }

        return tags;
    }

    /// <summary>Port of queryResolver.CountChannelsByType.</summary>
    public async Task<IReadOnlyList<ChannelTypeCountType>> CountChannelsByTypeAsync(
        CountChannelsByTypeInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var statusIn = input.StatusIn?
            .Select(ChannelMapping.ToWire)
            .ToList();

        IReadOnlyList<ChannelTypeCount> rows;
        try
        {
            await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

            rows = await channels
                .CountByTypeAsync(statusIn, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query channel type counts: " + ex.Message,
                ex);
        }

        return
        [
            .. rows.Select(static row => new ChannelTypeCountType
            {
                Type = row.Type,
                Count = row.Count,
            }),
        ];
    }

    /// <summary>
    /// Port of the <c>Project.Get</c> calls behind the summary/tag roots
    /// (axonhub.resolvers.go:866-874, 888-895). The profile lookup is wrapped
    /// by "failed to get project: " exactly like the reference; a missing row
    /// surfaces ent's NotFoundError.
    /// </summary>
    private async Task<ProjectRecord> LoadProjectAsync(
        long projectId,
        bool systemBypass,
        CancellationToken cancellationToken)
    {
        try
        {
            await privacy.EvaluateProjectQueryAsync(systemBypass, cancellationToken)
                .ConfigureAwait(false);

            var project = await projects
                .GetByIdAsync(projectId, cancellationToken)
                .ConfigureAwait(false);

            return project
                ?? throw new InvalidOperationException("ent: project not found");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get project: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of filterChannelsByProjectProfile
    /// (internal/server/gql/channel_visibility_helpers.go:10-29): the id list
    /// and then the tag matcher narrow the rows; a nil profile is a no-op.
    /// </summary>
    private static IReadOnlyList<ChannelRecord> FilterChannelsByProjectProfile(
        IReadOnlyList<ChannelRecord> rows,
        ProjectProfileModel? profile)
    {
        if (profile is null)
        {
            return rows;
        }

        IEnumerable<ChannelRecord> filtered = rows;

        if (profile.ChannelIDs is { Count: > 0 } channelIds)
        {
            var allowed = new HashSet<long>(channelIds.Select(static id => (long)id));
            filtered = filtered.Where(row => allowed.Contains(row.Id));
        }

        if (profile.ChannelTags is { Count: > 0 } channelTags)
        {
            filtered = filtered.Where(row => ChannelTagsCodec.Match(
                channelTags,
                profile.ChannelTagsMatchMode,
                ChannelTagsCodec.Parse(row.Tags)));
        }

        return [.. filtered];
    }

    private static ListCursor? ToListCursor(CursorValue? cursor)
        => cursor is null ? null : new ListCursor(cursor.Value.Id, cursor.Value.Value);

    private static List<string> ParseTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string?[]>(raw, JsonOptions)?
                .Select(static tag => tag ?? string.Empty)
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // Ent would fail the row scan; the port skips the malformed row (C141).
            return [];
        }
    }
}
