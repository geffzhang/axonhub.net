using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel read paths: the entgql <c>channels</c> connection and
/// biz.ChannelService.QueryChannels, plus the allChannelSummarys /
/// allChannelTags / countChannelsByType query resolvers.
/// </summary>
public sealed class ChannelQueryService(
    IChannelAdminStore channels,
    ChannelPrivacy privacy)
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

    /// <summary>Port of queryResolver.AllChannelSummarys (non-project branch).</summary>
    public async Task<IReadOnlyList<ChannelType>> AllChannelSummarysAsync(
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelRecord> rows;
        try
        {
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

    /// <summary>Port of queryResolver.AllChannelTags.</summary>
    public async Task<IReadOnlyList<string>> AllChannelTagsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string?> rows;
        try
        {
            rows = await channels
                .ListTagRowsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query channels: " + ex.Message,
                ex);
        }

        // lo.Reduce flattens the per-row tags in row order; lo.Uniq keeps the
        // first occurrence of each tag.
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var tag in ParseTags(row))
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
