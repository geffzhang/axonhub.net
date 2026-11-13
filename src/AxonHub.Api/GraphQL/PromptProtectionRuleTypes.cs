using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;
using AxonHub.Api.Management;
using AxonHub.Api.PromptProtection;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the PromptProtectionAction enum (prompt_protection_rule.graphql).</summary>
[GraphQLName("PromptProtectionAction")]
public enum PromptProtectionActionValue
{
    [GraphQLName("mask")]
    Mask,

    [GraphQLName("reject")]
    Reject,
}

/// <summary>Port of the PromptProtectionScope enum (prompt_protection_rule.graphql).</summary>
[GraphQLName("PromptProtectionScope")]
public enum PromptProtectionScopeValue
{
    [GraphQLName("system")]
    System,

    [GraphQLName("developer")]
    Developer,

    [GraphQLName("user")]
    User,

    [GraphQLName("assistant")]
    Assistant,

    [GraphQLName("tool")]
    Tool,
}

/// <summary>Port of the PromptProtectionRuleStatus enum (ent.graphql:3484-3488).</summary>
[GraphQLName("PromptProtectionRuleStatus")]
public enum PromptProtectionRuleStatusValue
{
    [GraphQLName("enabled")]
    Enabled,

    [GraphQLName("disabled")]
    Disabled,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>Port of PromptProtectionRuleOrderField (ent.graphql:3476-3480).</summary>
[GraphQLName("PromptProtectionRuleOrderField")]
public enum PromptProtectionRuleOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,

    [GraphQLName("NAME")]
    Name,
}

/// <summary>Port of PromptProtectionRuleOrder (ent.graphql:3463-3472).</summary>
[GraphQLName("PromptProtectionRuleOrder")]
[InputObjectType("PromptProtectionRuleOrder")]
public sealed class PromptProtectionRuleOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public PromptProtectionRuleOrderFieldValue Field { get; set; } =
        PromptProtectionRuleOrderFieldValue.CreatedAt;
}

/// <summary>Port of <c>PromptProtectionRuleWhereInput</c> (axonhub.graphql).</summary>
[GraphQLName("PromptProtectionRuleWhereInput")]
public sealed class PromptProtectionRuleWhereInput
{
    public PromptProtectionRuleWhereInput? Not { get; set; }

    public IReadOnlyList<PromptProtectionRuleWhereInput>? And { get; set; }

    public IReadOnlyList<PromptProtectionRuleWhereInput>? Or { get; set; }

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

    public string? DescriptionEqualFold { get; set; }

    public string? DescriptionContainsFold { get; set; }

    public string? Pattern { get; set; }

    public string? PatternNEQ { get; set; }

    public IReadOnlyList<string>? PatternIn { get; set; }

    public IReadOnlyList<string>? PatternNotIn { get; set; }

    public string? PatternGT { get; set; }

    public string? PatternGTE { get; set; }

    public string? PatternLT { get; set; }

    public string? PatternLTE { get; set; }

    public string? PatternContains { get; set; }

    public string? PatternHasPrefix { get; set; }

    public string? PatternHasSuffix { get; set; }

    public string? PatternEqualFold { get; set; }

    public string? PatternContainsFold { get; set; }

    public PromptProtectionRuleStatusValue? Status { get; set; }

    public PromptProtectionRuleStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<PromptProtectionRuleStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<PromptProtectionRuleStatusValue>? StatusNotIn { get; set; }
}

/// <summary>Port of the <c>PromptProtectionSettings</c> type (prompt_protection_rule.graphql).</summary>
[GraphQLName("PromptProtectionSettings")]
public sealed class PromptProtectionSettingsType
{
    public PromptProtectionActionValue Action { get; init; }

    /// <summary>
    /// Nullable in the SDL, but the reference marshals a Go string, so an
    /// empty replacement is emitted as "" rather than null.
    /// </summary>
    public string? Replacement { get; init; }

    public IReadOnlyList<PromptProtectionScopeValue> Scopes { get; init; } = [];
}

/// <summary>Port of <c>PromptProtectionSettingsInput</c> (prompt_protection_rule.graphql).</summary>
[GraphQLName("PromptProtectionSettingsInput")]
public sealed class PromptProtectionSettingsInput
{
    public PromptProtectionActionValue Action { get; set; }

    public string? Replacement { get; set; }

    public IReadOnlyList<PromptProtectionScopeValue>? Scopes { get; set; }
}

/// <summary>
/// Port of <c>PromptProtectionRulePreviewInput</c>
/// (prompt_protection_rule.graphql).
/// </summary>
[GraphQLName("PromptProtectionRulePreviewInput")]
public sealed class PromptProtectionRulePreviewInput
{
    public string Pattern { get; set; } = string.Empty;

    public string TestText { get; set; } = string.Empty;

    public PromptProtectionSettingsInput Settings { get; set; } = new();
}

/// <summary>
/// Port of <c>PromptProtectionRulePreviewResult</c>
/// (prompt_protection_rule.graphql).
/// </summary>
[GraphQLName("PromptProtectionRulePreviewResult")]
public sealed class PromptProtectionRulePreviewResultType
{
    public string Result { get; init; } = string.Empty;

    public bool HasMatch { get; init; }
}

/// <summary>
/// Port of the ent <c>CreatePromptProtectionRuleInput</c> (ent.graphql:1809-1826).
/// </summary>
[GraphQLName("CreatePromptProtectionRuleInput")]
public sealed class CreatePromptProtectionRuleInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Pattern { get; set; } = string.Empty;

    public PromptProtectionSettingsInput Settings { get; set; } = new();
}

/// <summary>
/// Port of the ent <c>UpdatePromptProtectionRuleInput</c> (ent.graphql:6401-6419).
/// </summary>
[GraphQLName("UpdatePromptProtectionRuleInput")]
public sealed class UpdatePromptProtectionRuleInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Pattern { get; set; }

    public PromptProtectionRuleStatusValue? Status { get; set; }

    public PromptProtectionSettingsInput? Settings { get; set; }
}

/// <summary>
/// Port of the <c>PromptProtectionRule</c> object type (ent.graphql:3408-3429).
/// </summary>
[GraphQLName("PromptProtectionRule")]
public sealed class PromptProtectionRuleType : INode
{
    [GraphQLIgnore]
    public PromptProtectionRuleRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("PromptProtectionRule", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    public string Name => Record.Name;

    public string Description => Record.Description;

    public string Pattern => Record.Pattern;

    public PromptProtectionRuleStatusValue Status =>
        PromptProtectionRuleMapping.ToGraphQLStatus(Record.Status);

    public PromptProtectionSettingsType Settings =>
        PromptProtectionRuleMapping.ToGraphQLSettings(
            PromptProtectionRuleManagementService.DecodeSettings(Record.Settings));
}

/// <summary>Port of <c>PromptProtectionRuleEdge</c> (ent.graphql:3450-3459).</summary>
[GraphQLName("PromptProtectionRuleEdge")]
public sealed class PromptProtectionRuleEdgeType
{
    public PromptProtectionRuleType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>PromptProtectionRuleConnection</c> (ent.graphql:3433-3446).</summary>
[GraphQLName("PromptProtectionRuleConnection")]
public sealed class PromptProtectionRuleConnectionType
{
    public IReadOnlyList<PromptProtectionRuleEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the prompt protection rules slice.</summary>
public static class PromptProtectionRuleMapping
{
    public static PromptProtectionRuleType ToGraphQL(PromptProtectionRuleRecord record)
        => new() { Record = record };

    public static PromptProtectionRuleListFilter? ToFilter(
        PromptProtectionRuleWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new PromptProtectionRuleListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdNotIn = where.IdNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
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
            DescriptionEqualFold = where.DescriptionEqualFold,
            DescriptionContainsFold = where.DescriptionContainsFold,
            Pattern = where.Pattern,
            PatternNEQ = where.PatternNEQ,
            PatternIn = where.PatternIn,
            PatternNotIn = where.PatternNotIn,
            PatternGT = where.PatternGT,
            PatternGTE = where.PatternGTE,
            PatternLT = where.PatternLT,
            PatternLTE = where.PatternLTE,
            PatternContains = where.PatternContains,
            PatternHasPrefix = where.PatternHasPrefix,
            PatternHasSuffix = where.PatternHasSuffix,
            PatternEqualFold = where.PatternEqualFold,
            PatternContainsFold = where.PatternContainsFold,
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And
                ?.Select(ToFilter)
                .OfType<PromptProtectionRuleListFilter>()
                .ToList(),
            Or = where.Or
                ?.Select(ToFilter)
                .OfType<PromptProtectionRuleListFilter>()
                .ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is rewritten by the resolver to
    /// ent.DefaultPromptProtectionRuleOrder's field, which is <c>id</c>
    /// (ent.resolvers.go:471-484). UPDATED_AT and NAME order by their columns.
    /// </summary>
    public static PromptProtectionRuleSortField ToSortField(
        PromptProtectionRuleOrder? orderBy)
        => orderBy?.Field switch
        {
            PromptProtectionRuleOrderFieldValue.UpdatedAt =>
                PromptProtectionRuleSortField.UpdatedAt,
            PromptProtectionRuleOrderFieldValue.Name => PromptProtectionRuleSortField.Name,
            _ => PromptProtectionRuleSortField.Id,
        };

    public static SortDirection ToSortDirection(PromptProtectionRuleOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static PromptProtectionRuleConnectionType BuildConnection(
        ListPage<PromptProtectionRuleRecord> page,
        PromptProtectionRuleSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new PromptProtectionRuleEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        PromptProtectionRuleSortField.UpdatedAt => node.UpdatedAt,
                        PromptProtectionRuleSortField.Name => node.Name,
                        _ => null,
                    }),
            })
            .ToList();

        return new PromptProtectionRuleConnectionType
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

    /// <summary>
    /// Port of gqlgen's PromptProtectionSettings marshalling: replacement is a
    /// Go string (never null) and nil scopes marshal as an empty list. An
    /// unknown stored enum value fails the field like the other ported enum
    /// slices (C177 item 5 / C231).
    /// </summary>
    public static PromptProtectionSettingsType ToGraphQLSettings(
        PromptProtectionSettings? settings) => new()
        {
            Action = ToGraphQLAction(settings?.Action),
            Replacement = settings?.Replacement ?? string.Empty,
            Scopes = (settings?.Scopes ?? [])
            .Select(static scope => ToGraphQLScope(scope))
            .ToList(),
        };

    public static PromptProtectionSettings ToDomain(PromptProtectionSettingsInput input) => new()
    {
        Action = ToWireAction(input.Action),
        Replacement = input.Replacement ?? string.Empty,
        Scopes = input.Scopes?.Select(static scope => ToWireScope(scope)).ToList(),
    };

    public static string ToWireAction(PromptProtectionActionValue action) => action switch
    {
        PromptProtectionActionValue.Mask => PromptProtectionActions.Mask,
        PromptProtectionActionValue.Reject => PromptProtectionActions.Reject,
        _ => throw new GraphQLException(
            "Enum \"PromptProtectionAction\" cannot represent value: \"" + action + "\""),
    };

    public static PromptProtectionActionValue ToGraphQLAction(string? action) => action switch
    {
        PromptProtectionActions.Mask => PromptProtectionActionValue.Mask,
        PromptProtectionActions.Reject => PromptProtectionActionValue.Reject,
        _ => throw new GraphQLException(
            "Enum \"PromptProtectionAction\" cannot represent value: \""
            + (action ?? string.Empty) + "\""),
    };

    public static string ToWireScope(PromptProtectionScopeValue scope) => scope switch
    {
        PromptProtectionScopeValue.System => PromptProtectionScopes.System,
        PromptProtectionScopeValue.Developer => PromptProtectionScopes.Developer,
        PromptProtectionScopeValue.User => PromptProtectionScopes.User,
        PromptProtectionScopeValue.Assistant => PromptProtectionScopes.Assistant,
        PromptProtectionScopeValue.Tool => PromptProtectionScopes.Tool,
        _ => throw new GraphQLException(
            "Enum \"PromptProtectionScope\" cannot represent value: \"" + scope + "\""),
    };

    public static PromptProtectionScopeValue ToGraphQLScope(string? scope) => scope switch
    {
        PromptProtectionScopes.System => PromptProtectionScopeValue.System,
        PromptProtectionScopes.Developer => PromptProtectionScopeValue.Developer,
        PromptProtectionScopes.User => PromptProtectionScopeValue.User,
        PromptProtectionScopes.Assistant => PromptProtectionScopeValue.Assistant,
        PromptProtectionScopes.Tool => PromptProtectionScopeValue.Tool,
        _ => throw new GraphQLException(
            "Enum \"PromptProtectionScope\" cannot represent value: \""
            + (scope ?? string.Empty) + "\""),
    };

    public static string ToWireStatus(PromptProtectionRuleStatusValue status) => status switch
    {
        PromptProtectionRuleStatusValue.Enabled => "enabled",
        PromptProtectionRuleStatusValue.Disabled => "disabled",
        PromptProtectionRuleStatusValue.Archived => "archived",
        _ => throw new GraphQLException(
            "Enum \"PromptProtectionRuleStatus\" cannot represent value: \"" + status + "\""),
    };

    public static PromptProtectionRuleStatusValue ToGraphQLStatus(string status) => status switch
    {
        "enabled" => PromptProtectionRuleStatusValue.Enabled,
        "disabled" => PromptProtectionRuleStatusValue.Disabled,
        "archived" => PromptProtectionRuleStatusValue.Archived,
        _ => throw new GraphQLException(
            "Enum \"PromptProtectionRuleStatus\" cannot represent value: \""
            + status + "\""),
    };

    public static CreatePromptProtectionRuleCommand ToCommand(
        CreatePromptProtectionRuleInput input) => new()
        {
            Name = input.Name,
            Description = input.Description,
            Pattern = input.Pattern,
            Settings = ToDomain(input.Settings),
        };

    public static UpdatePromptProtectionRuleCommand ToCommand(
        string id,
        UpdatePromptProtectionRuleInput input) => new()
        {
            Id = GidValue.Parse(id).Id,
            Name = input.Name,
            Description = input.Description,
            Pattern = input.Pattern,
            Status = input.Status is null ? null : ToWireStatus(input.Status.Value),
            Settings = input.Settings is null ? null : ToDomain(input.Settings),
        };
}
