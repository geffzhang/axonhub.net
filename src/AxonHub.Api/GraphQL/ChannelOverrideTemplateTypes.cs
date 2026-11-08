using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of <c>HeaderEntry</c> (axonhub.graphql:11-14).</summary>
[GraphQLName("HeaderEntry")]
public sealed class HeaderEntryType
{
    public string Key { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>Port of the gqlgen <c>ChannelOverrideTemplateOrderField</c> enum.</summary>
[GraphQLName("ChannelOverrideTemplateOrderField")]
public enum ChannelOverrideTemplateOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of <c>ChannelOverrideTemplateOrder</c> (ent.graphql:1034-1043).</summary>
[GraphQLName("ChannelOverrideTemplateOrder")]
[InputObjectType("ChannelOverrideTemplateOrder")]
public sealed class ChannelOverrideTemplateOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ChannelOverrideTemplateOrderFieldValue Field { get; set; } =
        ChannelOverrideTemplateOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of the subset of <c>ChannelOverrideTemplateWhereInput</c> used by the
/// management frontend (id/idIn, name/nameIn/nameContains/nameContainsFold, and
/// and/or/not). The remaining generated predicates are a registered gap (C162).
/// </summary>
[GraphQLName("ChannelOverrideTemplateWhereInput")]
public sealed class ChannelOverrideTemplateWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    public string? Name { get; set; }

    public IReadOnlyList<string>? NameIn { get; set; }

    public string? NameContains { get; set; }

    public string? NameContainsFold { get; set; }

    public ChannelOverrideTemplateWhereInput? Not { get; set; }

    public IReadOnlyList<ChannelOverrideTemplateWhereInput>? And { get; set; }

    public IReadOnlyList<ChannelOverrideTemplateWhereInput>? Or { get; set; }
}

/// <summary>Port of <c>CreateChannelOverrideTemplateInput</c> (ent.graphql:1649-1666).</summary>
[GraphQLName("CreateChannelOverrideTemplateInput")]
public sealed class CreateChannelOverrideTemplateInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public IReadOnlyList<OverrideOperationInput>? HeaderOverrideOperations { get; set; }

    public IReadOnlyList<OverrideOperationInput>? BodyOverrideOperations { get; set; }
}

/// <summary>Port of <c>UpdateChannelOverrideTemplateInput</c> (ent.graphql:6233-6255).</summary>
[GraphQLName("UpdateChannelOverrideTemplateInput")]
public sealed class UpdateChannelOverrideTemplateInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public bool? ClearDescription { get; set; }

    public IReadOnlyList<OverrideOperationInput>? HeaderOverrideOperations { get; set; }

    public IReadOnlyList<OverrideOperationInput>? AppendHeaderOverrideOperations { get; set; }

    /// <summary>The reference's SDL exposes the flag but UpdateTemplate ignores it.</summary>
    public bool? ClearHeaderOverrideOperations { get; set; }

    public IReadOnlyList<OverrideOperationInput>? BodyOverrideOperations { get; set; }

    public IReadOnlyList<OverrideOperationInput>? AppendBodyOverrideOperations { get; set; }

    /// <summary>The reference's SDL exposes the flag but UpdateTemplate ignores it.</summary>
    public bool? ClearBodyOverrideOperations { get; set; }
}

/// <summary>Port of the gqlgen <c>OverrideApplyMode</c> enum (axonhub.graphql:905-908).</summary>
[GraphQLName("OverrideApplyMode")]
public enum OverrideApplyModeValue
{
    [GraphQLName("MERGE")]
    Merge,

    [GraphQLName("REPLACE")]
    Replace,
}

/// <summary>Port of <c>ApplyChannelOverrideTemplateInput</c> (axonhub.graphql:910-914).</summary>
[GraphQLName("ApplyChannelOverrideTemplateInput")]
public sealed class ApplyChannelOverrideTemplateInput
{
    [GraphQLName("templateID")]
    [GraphQLType(typeof(IdType))]
    public string TemplateID { get; set; } = string.Empty;

    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string> ChannelIDs { get; set; } = [];

    public OverrideApplyModeValue? Mode { get; set; }
}

/// <summary>Port of <c>ApplyChannelOverrideTemplatePayload</c> (axonhub.graphql:916-920).</summary>
[GraphQLName("ApplyChannelOverrideTemplatePayload")]
public sealed class ApplyChannelOverrideTemplatePayloadType
{
    public bool Success { get; init; }

    public int Updated { get; init; }

    public IReadOnlyList<ChannelType> Channels { get; init; } = [];
}

/// <summary>Port of <c>ClearChannelOverrideTemplatesInput</c> (axonhub.graphql:922-924).</summary>
[GraphQLName("ClearChannelOverrideTemplatesInput")]
public sealed class ClearChannelOverrideTemplatesInput
{
    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string> ChannelIDs { get; set; } = [];
}

/// <summary>Port of <c>ClearChannelOverrideTemplatesPayload</c> (axonhub.graphql:926-930).</summary>
[GraphQLName("ClearChannelOverrideTemplatesPayload")]
public sealed class ClearChannelOverrideTemplatesPayloadType
{
    public bool Success { get; init; }

    public int Updated { get; init; }

    public IReadOnlyList<ChannelType> Channels { get; init; } = [];
}

/// <summary>Port of the <c>ChannelOverrideTemplate</c> object type (ent.graphql:967-1000).</summary>
[GraphQLName("ChannelOverrideTemplate")]
public sealed class ChannelOverrideTemplateType
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    /// <summary>Ent's optional user_id field is a plain int; unset reads as 0.</summary>
    [GraphQLIgnore]
    public long UserDbId { get; init; }

    [GraphQLIgnore]
    public string? HeaderOverrideOperationsJson { get; init; }

    [GraphQLIgnore]
    public string? BodyOverrideOperationsJson { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    /// <summary>SDL type is nullable <c>ID</c> even though the resolver never returns null.</summary>
    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string? UserID { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string OverrideParameters { get; init; } = "{}";

    public IReadOnlyList<HeaderEntryType> OverrideHeaders { get; init; } = [];

    [GraphQLName("headerOverrideOperations")]
    public IReadOnlyList<OverrideOperationType>? GetHeaderOverrideOperations()
    {
        // The new field wins when it has any content; otherwise the legacy
        // header entries are converted (ent.resolvers.go:181-194).
        var operations = ChannelOverrideOperations.ParseStoredOperations(
            HeaderOverrideOperationsJson);
        if (operations.Count > 0)
        {
            return ChannelOverrideTemplateMapping.ToGraphQLOperations(operations);
        }

        if (OverrideHeaders.Count > 0)
        {
            var converted = ChannelOverrideOperations.HeaderEntriesToOperations(
                OverrideHeaders
                    .Select(static header => new HeaderEntryDraft
                    {
                        Key = header.Key,
                        Value = header.Value,
                    })
                    .ToList());
            return converted is null
                ? []
                : ChannelOverrideTemplateMapping.ToGraphQLOperations(converted);
        }

        return [];
    }

    [GraphQLName("bodyOverrideOperations")]
    public IReadOnlyList<OverrideOperationType>? GetBodyOverrideOperations()
    {
        var operations = ChannelOverrideOperations.ParseStoredOperations(
            BodyOverrideOperationsJson);
        if (operations.Count > 0)
        {
            return ChannelOverrideTemplateMapping.ToGraphQLOperations(operations);
        }

        // Legacy overrideParameters are parsed on demand and a parse failure is
        // a resolver error (ent.resolvers.go:198-215).
        if (OverrideParameters.Length > 0 && OverrideParameters != "{}")
        {
            IReadOnlyList<OverrideOperationDraft>? parsed;
            try
            {
                parsed = ChannelOverrideOperations.ParseOverrideOperations(
                    OverrideParameters);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "failed to parse override parameters: " + ex.Message,
                    ex);
            }

            return parsed is null
                ? []
                : ChannelOverrideTemplateMapping.ToGraphQLOperations(parsed);
        }

        return [];
    }

    /// <summary>Port of channelOverrideTemplateResolver.User (getNilableUser).</summary>
    [GraphQLName("user")]
    public async Task<UserType?> GetUserAsync(
        [Service] IUserStore users,
        CancellationToken cancellationToken)
    {
        if (UserDbId == 0)
        {
            return null;
        }

        UserRecord? user;
        try
        {
            user = await users.GetByIdAsync(UserDbId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to load user: " + ex.Message,
                ex);
        }

        return user is null ? null : ManagementMapping.ToGraphQL(user);
    }
}

/// <summary>Port of <c>ChannelOverrideTemplateEdge</c> (ent.graphql:1021-1030).</summary>
[GraphQLName("ChannelOverrideTemplateEdge")]
public sealed class ChannelOverrideTemplateEdgeType
{
    public ChannelOverrideTemplateType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>ChannelOverrideTemplateConnection</c> (ent.graphql:1004-1017).</summary>
[GraphQLName("ChannelOverrideTemplateConnection")]
public sealed class ChannelOverrideTemplateConnectionType
{
    public IReadOnlyList<ChannelOverrideTemplateEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the channel override template slice.</summary>
public static class ChannelOverrideTemplateMapping
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ChannelOverrideTemplateType ToGraphQL(
        ChannelOverrideTemplateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var userId = record.UserId ?? 0;
        return new ChannelOverrideTemplateType
        {
            DbId = record.Id,
            UserDbId = userId,
            Id = new GidValue("ChannelOverrideTemplate", record.Id).ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            UserID = new GidValue("User", userId).ToString(),
            Name = record.Name,
            Description = record.Description,
            OverrideParameters = record.OverrideParameters ?? string.Empty,
            OverrideHeaders = ParseHeaderEntries(record.OverrideHeaders),
            HeaderOverrideOperationsJson = record.HeaderOverrideOperations,
            BodyOverrideOperationsJson = record.BodyOverrideOperations,
        };
    }

    public static IReadOnlyList<OverrideOperationType> ToGraphQLOperations(
        IReadOnlyList<OverrideOperationDraft> operations) =>
    [
        .. operations.Select(static operation => new OverrideOperationType
        {
            Op = operation.Op,
            Path = operation.Path,
            From = operation.From,
            To = operation.To,
            Value = operation.Value,
            Condition = operation.Condition,
            Match = operation.Match is null
                ? null
                : new OverrideMatchType
                {
                    Path = operation.Match.Path ?? string.Empty,
                    Eq = operation.Match.Eq ?? string.Empty,
                },
            // Go's int is 64-bit; GraphQL Int is 32-bit (C190).
            Index = operation.Index is null ? null : checked((int)operation.Index.Value),
            Splat = operation.Splat,
        }),
    ];

    public static ChannelOverrideTemplateListFilter? ToFilter(
        ChannelOverrideTemplateWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ChannelOverrideTemplateListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            Name = where.Name,
            NameIn = where.NameIn,
            NameContains = where.NameContains,
            NameContainsFold = where.NameContainsFold,
            Not = ToFilter(where.Not),
            And = where.And?
                .Select(ToFilter)
                .OfType<ChannelOverrideTemplateListFilter>()
                .ToList(),
            Or = where.Or?
                .Select(ToFilter)
                .OfType<ChannelOverrideTemplateListFilter>()
                .ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is remapped to ent.DefaultChannelOverrideTemplateOrder (id)
    /// before the query runs, so its edge cursor only carries the id.
    /// </summary>
    public static ChannelOverrideTemplateSortField ToSortField(
        ChannelOverrideTemplateOrder? orderBy) => orderBy?.Field switch
    {
        ChannelOverrideTemplateOrderFieldValue.UpdatedAt =>
            ChannelOverrideTemplateSortField.UpdatedAt,
        _ => ChannelOverrideTemplateSortField.Id,
    };

    public static SortDirection ToSortDirection(ChannelOverrideTemplateOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static ChannelOverrideTemplateConnectionType BuildConnection(
        ListPage<ChannelOverrideTemplateRecord> page,
        ChannelOverrideTemplateSortField sortField)
    {
        ArgumentNullException.ThrowIfNull(page);

        var edges = page.Nodes
            .Select(node => new ChannelOverrideTemplateEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField == ChannelOverrideTemplateSortField.UpdatedAt
                        ? node.UpdatedAt
                        : null),
            })
            .ToList();

        return new ChannelOverrideTemplateConnectionType
        {
            Edges = edges,
            PageInfo = new PageInfoType
            {
                HasNextPage = page.HasNextPage,
                HasPreviousPage = page.HasPreviousPage,
                StartCursor = edges.Count > 0 ? edges[0].Cursor : null,
                EndCursor = edges.Count > 0 ? edges[^1].Cursor : null,
            },
            TotalCount = page.TotalCount,
        };
    }

    private static List<HeaderEntryType> ParseHeaderEntries(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            return JsonSerializer
                .Deserialize<List<HeaderEntryDraft?>>(raw, JsonOptions)?
                .Select(static header => new HeaderEntryType
                {
                    Key = header?.Key ?? string.Empty,
                    Value = header?.Value ?? string.Empty,
                })
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // Ent would reject the row during scan; the port degrades to empty
            // like the other raw-column readers (C141).
            return [];
        }
    }
}
