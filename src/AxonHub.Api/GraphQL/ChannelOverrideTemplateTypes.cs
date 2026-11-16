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
/// Port of <c>ChannelOverrideTemplateWhereInput</c> (ent.graphql): every
/// scalar field predicate group and the <c>user</c> edge predicates.
/// </summary>
[GraphQLName("ChannelOverrideTemplateWhereInput")]
public sealed class ChannelOverrideTemplateWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLTE { get; set; }

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string? UserId { get; set; }

    [GraphQLName("userIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? UserIdNEQ { get; set; }

    [GraphQLName("userIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIdIn { get; set; }

    [GraphQLName("userIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIdNotIn { get; set; }

    [GraphQLName("userIDIsNil")]
    public bool? UserIdIsNil { get; set; }

    [GraphQLName("userIDNotNil")]
    public bool? UserIdNotNil { get; set; }

    public string? Name { get; set; }

    public string? NameNEQ { get; set; }

    public IReadOnlyList<string>? NameIn { get; set; }

    public IReadOnlyList<string>? NameNotIn { get; set; }

    public string? NameGT { get; set; }

    public string? NameGTE { get; set; }

    public string? NameLT { get; set; }

    public string? NameLTE { get; set; }

    public string? NameContains { get; set; }

    public string? NameHasPrefix { get; set; }

    public string? NameHasSuffix { get; set; }

    public string? NameEqualFold { get; set; }

    public string? NameContainsFold { get; set; }

    public string? Description { get; set; }

    public string? DescriptionNEQ { get; set; }

    public IReadOnlyList<string>? DescriptionIn { get; set; }

    public IReadOnlyList<string>? DescriptionNotIn { get; set; }

    public string? DescriptionGT { get; set; }

    public string? DescriptionGTE { get; set; }

    public string? DescriptionLT { get; set; }

    public string? DescriptionLTE { get; set; }

    public string? DescriptionContains { get; set; }

    public string? DescriptionHasPrefix { get; set; }

    public string? DescriptionHasSuffix { get; set; }

    public bool? DescriptionIsNil { get; set; }

    public bool? DescriptionNotNil { get; set; }

    public string? DescriptionEqualFold { get; set; }

    public string? DescriptionContainsFold { get; set; }

    public string? OverrideParameters { get; set; }

    public string? OverrideParametersNEQ { get; set; }

    public IReadOnlyList<string>? OverrideParametersIn { get; set; }

    public IReadOnlyList<string>? OverrideParametersNotIn { get; set; }

    public string? OverrideParametersGT { get; set; }

    public string? OverrideParametersGTE { get; set; }

    public string? OverrideParametersLT { get; set; }

    public string? OverrideParametersLTE { get; set; }

    public string? OverrideParametersContains { get; set; }

    public string? OverrideParametersHasPrefix { get; set; }

    public string? OverrideParametersHasSuffix { get; set; }

    public string? OverrideParametersEqualFold { get; set; }

    public string? OverrideParametersContainsFold { get; set; }

    public bool? HasUser { get; set; }

    public IReadOnlyList<UserWhereInput>? HasUserWith { get; set; }

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
public sealed class ChannelOverrideTemplateType : INode
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
        [Service] UserPrivacy privacy,
        CancellationToken cancellationToken)
    {
        UserRecord? user;
        try
        {
            // getNilableUser runs client.User.Get (query policy) and turns
            // both NotFound and deny into a null edge.
            user = await privacy.GetVisibleUserOrNullAsync(UserDbId, cancellationToken)
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
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdNotIn = where.IdNotIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdGT = where.IdGT is null ? null : GidValue.Parse(where.IdGT).Id,
            IdGTE = where.IdGTE is null ? null : GidValue.Parse(where.IdGTE).Id,
            IdLT = where.IdLT is null ? null : GidValue.Parse(where.IdLT).Id,
            IdLTE = where.IdLTE is null ? null : GidValue.Parse(where.IdLTE).Id,
            CreatedAt = where.CreatedAt,
            CreatedAtNEQ = where.CreatedAtNEQ,
            CreatedAtIn = where.CreatedAtIn,
            CreatedAtNotIn = where.CreatedAtNotIn,
            CreatedAtGT = where.CreatedAtGT,
            CreatedAtGTE = where.CreatedAtGTE,
            CreatedAtLT = where.CreatedAtLT,
            CreatedAtLTE = where.CreatedAtLTE,
            UpdatedAt = where.UpdatedAt,
            UpdatedAtNEQ = where.UpdatedAtNEQ,
            UpdatedAtIn = where.UpdatedAtIn,
            UpdatedAtNotIn = where.UpdatedAtNotIn,
            UpdatedAtGT = where.UpdatedAtGT,
            UpdatedAtGTE = where.UpdatedAtGTE,
            UpdatedAtLT = where.UpdatedAtLT,
            UpdatedAtLTE = where.UpdatedAtLTE,
            UserId = where.UserId is null ? null : GidValue.Parse(where.UserId).Id,
            UserIdNEQ = where.UserIdNEQ is null
                ? null
                : GidValue.Parse(where.UserIdNEQ).Id,
            UserIdIn = where.UserIdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            UserIdNotIn = where.UserIdNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            UserIdIsNil = where.UserIdIsNil,
            UserIdNotNil = where.UserIdNotNil,
            Name = where.Name,
            NameNEQ = where.NameNEQ,
            NameIn = where.NameIn,
            NameNotIn = where.NameNotIn,
            NameGT = where.NameGT,
            NameGTE = where.NameGTE,
            NameLT = where.NameLT,
            NameLTE = where.NameLTE,
            NameContains = where.NameContains,
            NameHasPrefix = where.NameHasPrefix,
            NameHasSuffix = where.NameHasSuffix,
            NameEqualFold = where.NameEqualFold,
            NameContainsFold = where.NameContainsFold,
            Description = where.Description,
            DescriptionNEQ = where.DescriptionNEQ,
            DescriptionIn = where.DescriptionIn,
            DescriptionNotIn = where.DescriptionNotIn,
            DescriptionGT = where.DescriptionGT,
            DescriptionGTE = where.DescriptionGTE,
            DescriptionLT = where.DescriptionLT,
            DescriptionLTE = where.DescriptionLTE,
            DescriptionContains = where.DescriptionContains,
            DescriptionHasPrefix = where.DescriptionHasPrefix,
            DescriptionHasSuffix = where.DescriptionHasSuffix,
            DescriptionIsNil = where.DescriptionIsNil,
            DescriptionNotNil = where.DescriptionNotNil,
            DescriptionEqualFold = where.DescriptionEqualFold,
            DescriptionContainsFold = where.DescriptionContainsFold,
            OverrideParameters = where.OverrideParameters,
            OverrideParametersNEQ = where.OverrideParametersNEQ,
            OverrideParametersIn = where.OverrideParametersIn,
            OverrideParametersNotIn = where.OverrideParametersNotIn,
            OverrideParametersGT = where.OverrideParametersGT,
            OverrideParametersGTE = where.OverrideParametersGTE,
            OverrideParametersLT = where.OverrideParametersLT,
            OverrideParametersLTE = where.OverrideParametersLTE,
            OverrideParametersContains = where.OverrideParametersContains,
            OverrideParametersHasPrefix = where.OverrideParametersHasPrefix,
            OverrideParametersHasSuffix = where.OverrideParametersHasSuffix,
            OverrideParametersEqualFold = where.OverrideParametersEqualFold,
            OverrideParametersContainsFold = where.OverrideParametersContainsFold,
            HasUser = where.HasUser,
            HasUserWith = where.HasUserWith?
                .Select(ManagementMapping.ToFilter)
                .OfType<UserListFilter>()
                .ToList(),
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

    /// <summary>
    /// Edge connections keep the generated order field; only the root
    /// resolver rewrites CREATED_AT to the default id order.
    /// </summary>
    public static ChannelOverrideTemplateSortField ToEdgeSortField(
        ChannelOverrideTemplateOrder? orderBy) => orderBy?.Field switch
        {
            ChannelOverrideTemplateOrderFieldValue.CreatedAt =>
                ChannelOverrideTemplateSortField.CreatedAt,
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
                    sortField switch
                    {
                        ChannelOverrideTemplateSortField.CreatedAt => node.CreatedAt,
                        ChannelOverrideTemplateSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
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
