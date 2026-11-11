using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the ModelStatus enum (ent.graphql:2385-2389).</summary>
[GraphQLName("ModelStatus")]
public enum ModelStatusValue
{
    [GraphQLName("enabled")]
    Enabled,

    [GraphQLName("disabled")]
    Disabled,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>Port of the ModelType enum (ent.graphql:2391-2397).</summary>
[GraphQLName("ModelType")]
public enum ModelTypeValue
{
    [GraphQLName("chat")]
    Chat,

    [GraphQLName("embedding")]
    Embedding,

    [GraphQLName("rerank")]
    Rerank,

    [GraphQLName("image_generation")]
    ImageGeneration,

    [GraphQLName("video_generation")]
    VideoGeneration,
}

/// <summary>Port of ModelOrderField (ent.graphql:2375-2379).</summary>
[GraphQLName("ModelOrderField")]
public enum ModelOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,

    [GraphQLName("NAME")]
    Name,
}

/// <summary>Port of ModelOrder (ent.graphql:2360-2370).</summary>
[GraphQLName("ModelOrder")]
[InputObjectType("ModelOrder")]
public sealed class ModelOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ModelOrderFieldValue Field { get; set; } = ModelOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of the subset of <c>ModelWhereInput</c> used by the management
/// frontend (id/idIn, developer/developerContainsFold,
/// modelID/modelIDContainsFold, type/typeIn/typeNotIn, name/nameContainsFold,
/// status/statusIn/statusNotIn, remarkIsNil/remarkNotNil, and/or/not). The
/// remaining ent predicates are not ported (C177).
/// </summary>
[GraphQLName("ModelWhereInput")]
public sealed class ModelWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    public string? Developer { get; set; }

    public string? DeveloperContainsFold { get; set; }

    [GraphQLName("modelID")]
    public string? ModelId { get; set; }

    [GraphQLName("modelIDContainsFold")]
    public string? ModelIdContainsFold { get; set; }

    public ModelTypeValue? Type { get; set; }

    public IReadOnlyList<ModelTypeValue>? TypeIn { get; set; }

    public IReadOnlyList<ModelTypeValue>? TypeNotIn { get; set; }

    public string? Name { get; set; }

    public string? NameContainsFold { get; set; }

    public ModelStatusValue? Status { get; set; }

    public IReadOnlyList<ModelStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ModelStatusValue>? StatusNotIn { get; set; }

    /// <summary>
    /// entgql binds this nullable Boolean to a plain Go bool, so only an
    /// explicit <c>true</c> applies a predicate.
    /// </summary>
    public bool? RemarkIsNil { get; set; }

    /// <summary>
    /// entgql binds this nullable Boolean to a plain Go bool, so only an
    /// explicit <c>true</c> applies a predicate.
    /// </summary>
    public bool? RemarkNotNil { get; set; }

    public ModelWhereInput? Not { get; set; }

    public IReadOnlyList<ModelWhereInput>? And { get; set; }

    public IReadOnlyList<ModelWhereInput>? Or { get; set; }
}

/// <summary>Port of the <c>ModelCard</c> type (model.graphql:2-14).</summary>
[GraphQLName("ModelCard")]
public sealed class ModelCardType
{
    [GraphQLIgnore]
    public ModelCard Card { get; init; } = ModelCard.Default;

    public ModelCardReasoningType Reasoning => new()
    {
        Supported = Card.Reasoning.Supported,
        Default = Card.Reasoning.Default,
    };

    public IReadOnlyList<string>? ReasoningEfforts => Card.ReasoningEfforts;

    public bool ToolCall => Card.ToolCall;

    public bool Temperature => Card.Temperature;

    public ModelCardModalitiesType Modalities => new()
    {
        Input = Card.Modalities.Input ?? [],
        Output = Card.Modalities.Output ?? [],
    };

    /// <summary>
    /// Nullable in the SDL, but the reference stores a plain Go bool, so the
    /// field is always true or false and never null.
    /// </summary>
    public bool? Vision => Card.Vision;

    public ModelCardCostType Cost => new()
    {
        Input = Card.Cost.Input,
        Output = Card.Cost.Output,
        CacheRead = Card.Cost.CacheRead,
        CacheWrite = Card.Cost.CacheWrite,
    };

    public ModelCardLimitType Limit => new()
    {
        Context = Card.Limit.Context,
        Output = Card.Limit.Output,
    };

    /// <summary>Nullable in the SDL; the Go field is a plain string, so "" is emitted.</summary>
    public string? Knowledge => Card.Knowledge;

    /// <summary>Nullable in the SDL; the Go field is a plain string, so "" is emitted.</summary>
    public string? ReleaseDate => Card.ReleaseDate;

    /// <summary>Nullable in the SDL; the Go field is a plain string, so "" is emitted.</summary>
    public string? LastUpdated => Card.LastUpdated;
}

/// <summary>Port of the <c>ModelCardReasoning</c> type (model.graphql:16-19).</summary>
[GraphQLName("ModelCardReasoning")]
public sealed class ModelCardReasoningType
{
    public bool Supported { get; init; }

    public bool Default { get; init; }
}

/// <summary>Port of the <c>ModelCardModalities</c> type (model.graphql:21-24).</summary>
[GraphQLName("ModelCardModalities")]
public sealed class ModelCardModalitiesType
{
    /// <summary>gqlgen's non-null list marshaller renders a nil slice as [].</summary>
    public IReadOnlyList<string> Input { get; init; } = [];

    public IReadOnlyList<string> Output { get; init; } = [];
}

/// <summary>Port of the <c>ModelCardCost</c> type (model.graphql:26-31).</summary>
[GraphQLName("ModelCardCost")]
public sealed class ModelCardCostType
{
    public double Input { get; init; }

    public double Output { get; init; }

    public double CacheRead { get; init; }

    public double CacheWrite { get; init; }
}

/// <summary>Port of the <c>ModelCardLimit</c> type (model.graphql:33-36).</summary>
[GraphQLName("ModelCardLimit")]
public sealed class ModelCardLimitType
{
    public int Context { get; init; }

    public int Output { get; init; }
}

/// <summary>Port of <c>ModelCardInput</c> (model.graphql:101-113).</summary>
[GraphQLName("ModelCardInput")]
public sealed class ModelCardInput
{
    public ModelCardReasoningInput? Reasoning { get; set; }

    public IReadOnlyList<string>? ReasoningEfforts { get; set; }

    public bool? ToolCall { get; set; }

    public bool? Temperature { get; set; }

    public ModelCardModalitiesInput? Modalities { get; set; }

    public bool? Vision { get; set; }

    public ModelCardCostInput? Cost { get; set; }

    public ModelCardLimitInput? Limit { get; set; }

    public string? Knowledge { get; set; }

    public string? ReleaseDate { get; set; }

    public string? LastUpdated { get; set; }
}

/// <summary>Port of <c>ModelCardReasoningInput</c> (model.graphql:115-118).</summary>
[GraphQLName("ModelCardReasoningInput")]
public sealed class ModelCardReasoningInput
{
    public bool? Supported { get; set; }

    public bool? Default { get; set; }
}

/// <summary>Port of <c>ModelCardModalitiesInput</c> (model.graphql:120-123).</summary>
[GraphQLName("ModelCardModalitiesInput")]
public sealed class ModelCardModalitiesInput
{
    public IReadOnlyList<string>? Input { get; set; }

    public IReadOnlyList<string>? Output { get; set; }
}

/// <summary>Port of <c>ModelCardCostInput</c> (model.graphql:125-130).</summary>
[GraphQLName("ModelCardCostInput")]
public sealed class ModelCardCostInput
{
    public double? Input { get; set; }

    public double? Output { get; set; }

    public double? CacheRead { get; set; }

    public double? CacheWrite { get; set; }
}

/// <summary>Port of <c>ModelCardLimitInput</c> (model.graphql:132-135).</summary>
[GraphQLName("ModelCardLimitInput")]
public sealed class ModelCardLimitInput
{
    public int? Context { get; set; }

    public int? Output { get; set; }
}

/// <summary>Port of the <c>ModelSettings</c> type (model.graphql:39-44).</summary>
[GraphQLName("ModelSettings")]
public sealed class ModelSettingsType
{
    [GraphQLIgnore]
    public ModelEntitySettings Settings { get; init; } = new();

    public bool DisableDeveloperSettingsInheritance
        => Settings.DisableDeveloperSettingsInheritance;

    public IReadOnlyList<ModelAssociationType> Associations => Settings.Associations is null
        ? []
        : [.. Settings.Associations.Select(static association => association is null
            ? throw new GraphQLException(
                "the requested element is null which the schema does not allow")
            : SystemSettingsMapping.ToGraphQL(association))];

    public string LoadBalancerStrategy => Settings.LoadBalancerStrategy;

    public string TraceStickyMode => Settings.TraceStickyMode;
}

/// <summary>Port of <c>ModelSettingsInput</c> (model.graphql:137-142).</summary>
[GraphQLName("ModelSettingsInput")]
public sealed class ModelSettingsInput
{
    public bool? DisableDeveloperSettingsInheritance { get; set; }

    public IReadOnlyList<ModelAssociationInput> Associations { get; set; } = [];

    public string? LoadBalancerStrategy { get; set; }

    public string? TraceStickyMode { get; set; }
}

/// <summary>Port of the ent <c>CreateModelInput</c> (ent.graphql:1697-1736).</summary>
[GraphQLName("CreateModelInput")]
public sealed class CreateModelInput
{
    public string Developer { get; set; } = string.Empty;

    [GraphQLName("modelID")]
    public string ModelId { get; set; } = string.Empty;

    public ModelTypeValue? Type { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;

    public ModelCardInput ModelCard { get; set; } = new();

    public ModelSettingsInput Settings { get; set; } = new();

    public string? Remark { get; set; }
}

/// <summary>Port of the ent <c>UpdateModelInput</c> (ent.graphql:6282-6321).</summary>
[GraphQLName("UpdateModelInput")]
public sealed class UpdateModelInput
{
    public string? Developer { get; set; }

    [GraphQLName("modelID")]
    public string? ModelId { get; set; }

    public ModelTypeValue? Type { get; set; }

    public string? Name { get; set; }

    public string? Icon { get; set; }

    public string? Group { get; set; }

    public ModelCardInput? ModelCard { get; set; }

    public ModelSettingsInput? Settings { get; set; }

    public ModelStatusValue? Status { get; set; }

    public string? Remark { get; set; }

    public bool? ClearRemark { get; set; }
}

/// <summary>Port of <c>QueryModelsInput</c> (axonhub.graphql:658-663).</summary>
[GraphQLName("QueryModelsInput")]
public sealed class QueryModelsInput
{
    public IReadOnlyList<ChannelStatusValue>? StatusIn { get; set; }

    public bool? IncludeMapping { get; set; }

    public bool? IncludePrefix { get; set; }

    public bool? IncludeAllChannelModels { get; set; }
}

/// <summary>Port of <c>FetchModelsInput</c> (axonhub.graphql:638-643).</summary>
[GraphQLName("FetchModelsInput")]
public sealed class FetchModelsInputType
{
    public string ChannelType { get; set; } = string.Empty;

    [GraphQLName("baseURL")]
    public string BaseUrl { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    /// <summary>objects.GUID pointer: parsed at the resolver boundary.</summary>
    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID { get; set; }
}

/// <summary>Port of <c>FetchModelsPayload</c> (axonhub.graphql:645-648).</summary>
[GraphQLName("FetchModelsPayload")]
public sealed class FetchModelsPayloadType
{
    public IReadOnlyList<ModelIdentifyType> Models { get; init; } = [];

    public string? Error { get; init; }
}

/// <summary>Port of <c>ModelIdentify</c> (axonhub.graphql:650-652).</summary>
[GraphQLName("ModelIdentify")]
public sealed class ModelIdentifyType
{
    public string Id { get; init; } = string.Empty;
}

/// <summary>Port of <c>ModelIdentityWithStatus</c> (axonhub.graphql:654-657).</summary>
[GraphQLName("ModelIdentityWithStatus")]
public sealed class ModelIdentityWithStatusType
{
    public string Id { get; init; } = string.Empty;

    public ChannelStatusValue Status { get; init; }
}

/// <summary>Port of <c>ModelChannelConnection</c> (model.graphql:198-202).</summary>
[GraphQLName("ModelChannelConnection")]
public sealed class ModelChannelConnectionType
{
    public ChannelType Channel { get; init; } = null!;

    public IReadOnlyList<ChannelModelEntryType> Models { get; init; } = [];

    public int Priority { get; init; }
}

/// <summary>Port of <c>UnassociatedChannel</c> (model.graphql:210-213).</summary>
[GraphQLName("UnassociatedChannel")]
public sealed class UnassociatedChannelType
{
    public ChannelType Channel { get; init; } = null!;

    public IReadOnlyList<string> Models { get; init; } = [];
}

/// <summary>Port of the <c>Model</c> object type (ent.graphql:2320-2345).</summary>
[GraphQLName("Model")]
public sealed class ModelType : INode
{
    [GraphQLIgnore]
    public ModelRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("Model", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    public string Developer => Record.Developer;

    [GraphQLName("modelID")]
    public string ModelId => Record.ModelId;

    public ModelTypeValue Type => ModelMapping.ToGraphQLType(Record.Type);

    public string Name => Record.Name;

    public string Icon => Record.Icon;

    public string Group => Record.Group;

    /// <summary>
    /// ent scans the JSON column into *objects.ModelCard; a NULL column (or a
    /// JSON null) leaves the pointer nil and gqlgen fails the non-null field
    /// with its "requested element is null" message.
    /// </summary>
    public ModelCardType ModelCard => ModelCardCodec.Deserialize(Record.ModelCard) is { } card
        ? new ModelCardType { Card = card }
        : throw new GraphQLException(
            "the requested element is null which the schema does not allow");

    /// <summary>Same nil-pointer handling as <see cref="ModelCard"/>.</summary>
    public ModelSettingsType Settings
        => ModelSettingsCodec.DeserializeEntitySettings(Record.Settings) is { } settings
            ? new ModelSettingsType { Settings = settings }
            : throw new GraphQLException(
                "the requested element is null which the schema does not allow");

    public ModelStatusValue Status => ModelMapping.ToGraphQLStatus(Record.Status);

    public string? Remark => Record.Remark;
}

/// <summary>Port of <c>ModelEdge</c> (ent.graphql:2353-2363).</summary>
[GraphQLName("ModelEdge")]
public sealed class ModelEdgeType
{
    public ModelType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>ModelConnection</c> (ent.graphql:2347-2359).</summary>
[GraphQLName("ModelConnection")]
public sealed class ModelConnectionType
{
    public IReadOnlyList<ModelEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of the Model extension <c>associatedChannelCount</c> (model.graphql:215-217).</summary>
[ExtendObjectType(typeof(ModelType))]
public sealed class ModelTypeExtensions
{
    [GraphQLName("associatedChannelCount")]
    public Task<int> AssociatedChannelCountAsync(
        [Parent] ModelType model,
        [Service] ModelManagementService models,
        CancellationToken cancellationToken)
        => models.CountModelAssociatedChannelsAsync(model.Record, cancellationToken);
}

/// <summary>Domain ↔ GraphQL mapping for the models slice.</summary>
public static class ModelMapping
{
    public static ModelType ToGraphQL(ModelRecord record) => new() { Record = record };

    public static ModelChannelConnectionType ToGraphQL(ModelChannelConnection connection) => new()
    {
        Channel = ChannelMapping.ToGraphQL(connection.Channel),
        Models = connection.Models
            .Select(static entry => new ChannelModelEntryType
            {
                RequestModel = entry.RequestModel,
                ActualModel = entry.ActualModel,
                Source = entry.Source,
            })
            .ToList(),
        Priority = connection.Priority,
    };

    public static UnassociatedChannelType ToGraphQL(UnassociatedChannel channel) => new()
    {
        Channel = ChannelMapping.ToGraphQL(channel.Channel),
        Models = channel.Models,
    };

    public static ModelIdentityWithStatusType ToGraphQL(ModelIdentityWithStatus model) => new()
    {
        Id = model.Id,
        Status = ChannelMapping.ToChannelStatusValue(model.Status),
    };

    public static FetchModelsPayloadType ToGraphQL(FetchModelsResult result) => new()
    {
        Models = result.Models
            .Select(static model => new ModelIdentifyType { Id = model.Id })
            .ToList(),
        Error = result.Error,
    };

    public static ModelListFilter? ToFilter(ModelWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ModelListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            Developer = where.Developer,
            DeveloperContainsFold = where.DeveloperContainsFold,
            ModelId = where.ModelId,
            ModelIdContainsFold = where.ModelIdContainsFold,
            Type = where.Type is null ? null : ToWireType(where.Type.Value),
            TypeIn = where.TypeIn?
                .Select(static type => ToWireType(type))
                .ToList(),
            TypeNotIn = where.TypeNotIn?
                .Select(static type => ToWireType(type))
                .ToList(),
            Name = where.Name,
            NameContainsFold = where.NameContainsFold,
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            RemarkIsNil = where.RemarkIsNil == true,
            RemarkNotNil = where.RemarkNotNil == true,
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ModelListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ModelListFilter>().ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is rewritten to ent.DefaultModelOrder's field, which is
    /// <c>id</c> (ent/gql_pagination.go). NAME orders by the name column.
    /// </summary>
    public static ModelSortField ToSortField(ModelOrder? orderBy)
        => orderBy?.Field switch
        {
            ModelOrderFieldValue.UpdatedAt => ModelSortField.UpdatedAt,
            ModelOrderFieldValue.Name => ModelSortField.Name,
            _ => ModelSortField.Id,
        };

    public static SortDirection ToSortDirection(ModelOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static ModelConnectionType BuildConnection(
        ListPage<ModelRecord> page,
        ModelSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new ModelEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        ModelSortField.UpdatedAt => node.UpdatedAt,
                        ModelSortField.Name => node.Name,
                        _ => null,
                    }),
            })
            .ToList();

        return new ModelConnectionType
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

    public static string ToWireType(ModelTypeValue type) => type switch
    {
        ModelTypeValue.Chat => "chat",
        ModelTypeValue.Embedding => "embedding",
        ModelTypeValue.Rerank => "rerank",
        ModelTypeValue.ImageGeneration => "image_generation",
        ModelTypeValue.VideoGeneration => "video_generation",
        _ => throw new GraphQLException(
            "Enum \"ModelType\" cannot represent value: \"" + type + "\""),
    };

    public static ModelTypeValue ToGraphQLType(string type) => type switch
    {
        "chat" => ModelTypeValue.Chat,
        "embedding" => ModelTypeValue.Embedding,
        "rerank" => ModelTypeValue.Rerank,
        "image_generation" => ModelTypeValue.ImageGeneration,
        "video_generation" => ModelTypeValue.VideoGeneration,
        _ => throw new GraphQLException(
            "Enum \"ModelType\" cannot represent value: \"" + type + "\""),
    };

    public static string ToWireStatus(ModelStatusValue status) => status switch
    {
        ModelStatusValue.Enabled => "enabled",
        ModelStatusValue.Disabled => "disabled",
        ModelStatusValue.Archived => "archived",
        _ => throw new GraphQLException(
            "Enum \"ModelStatus\" cannot represent value: \"" + status + "\""),
    };

    public static ModelStatusValue ToGraphQLStatus(string status) => status switch
    {
        "enabled" => ModelStatusValue.Enabled,
        "disabled" => ModelStatusValue.Disabled,
        "archived" => ModelStatusValue.Archived,
        _ => throw new GraphQLException(
            "Enum \"ModelStatus\" cannot represent value: \"" + status + "\""),
    };

    /// <summary>
    /// Port of gqlgen's ModelCardInput → objects.ModelCard binding: absent
    /// nested objects decode as their zero value and absent scalars as false
    /// or "".
    /// </summary>
    public static ModelCard ToDomain(ModelCardInput input) => new()
    {
        Reasoning = input.Reasoning is null
            ? new ModelCardReasoning(false, false)
            : new ModelCardReasoning(
                input.Reasoning.Supported ?? false,
                input.Reasoning.Default ?? false),
        ReasoningEfforts = input.ReasoningEfforts,
        ToolCall = input.ToolCall ?? false,
        Temperature = input.Temperature ?? false,
        Modalities = input.Modalities is null
            ? new ModelCardModalities(null, null)
            : new ModelCardModalities(input.Modalities.Input, input.Modalities.Output),
        Vision = input.Vision ?? false,
        Cost = input.Cost is null
            ? new ModelCardCost(0, 0, 0, 0)
            : new ModelCardCost(
                input.Cost.Input ?? 0,
                input.Cost.Output ?? 0,
                input.Cost.CacheRead ?? 0,
                input.Cost.CacheWrite ?? 0),
        Limit = input.Limit is null
            ? new ModelCardLimit(0, 0)
            : new ModelCardLimit(input.Limit.Context ?? 0, input.Limit.Output ?? 0),
        Knowledge = input.Knowledge ?? string.Empty,
        ReleaseDate = input.ReleaseDate ?? string.Empty,
        LastUpdated = input.LastUpdated ?? string.Empty,
    };

    /// <summary>
    /// Port of gqlgen's ModelSettingsInput → objects.ModelSettings binding.
    /// The reference's routing normalizer runs in validateModelSettings, so
    /// absent strings stay "" until the service validates them.
    /// </summary>
    public static ModelEntitySettings ToDomain(ModelSettingsInput input) => new()
    {
        DisableDeveloperSettingsInheritance =
            input.DisableDeveloperSettingsInheritance ?? false,
        Associations = input.Associations
            .Select(static association => (ModelAssociation?)SystemSettingsMapping.ToDomain(
                association))
            .ToList(),
        LoadBalancerStrategy = input.LoadBalancerStrategy ?? string.Empty,
        TraceStickyMode = input.TraceStickyMode ?? string.Empty,
    };

    public static CreateModelCommand ToCommand(CreateModelInput input) => new()
    {
        Developer = input.Developer,
        ModelId = input.ModelId,
        Type = input.Type is null ? null : ToWireType(input.Type.Value),
        Name = input.Name,
        Icon = input.Icon,
        Group = input.Group,
        ModelCard = ToDomain(input.ModelCard),
        Settings = ToDomain(input.Settings),
        Remark = input.Remark,
    };

    public static UpdateModelCommand ToCommand(string id, UpdateModelInput input) => new()
    {
        Id = GidValue.Parse(id).Id,
        Developer = input.Developer,
        ModelId = input.ModelId,
        Type = input.Type is null ? null : ToWireType(input.Type.Value),
        Name = input.Name,
        Icon = input.Icon,
        Group = input.Group,
        ModelCard = input.ModelCard is null ? null : ToDomain(input.ModelCard),
        Settings = input.Settings is null ? null : ToDomain(input.Settings),
        Status = input.Status is null ? null : ToWireStatus(input.Status.Value),
        Remark = input.Remark,
        ClearRemark = input.ClearRemark == true,
    };

    public static ListModelsCommand ToCommand(QueryModelsInput input) => new()
    {
        StatusIn = input.StatusIn?
            .Select(static status => ChannelMapping.ToWire(status))
            .ToList(),
        IncludeMapping = input.IncludeMapping ?? false,
        IncludePrefix = input.IncludePrefix ?? false,
        IncludeAllChannelModels = input.IncludeAllChannelModels ?? false,
    };

    public static AxonHub.Api.Management.FetchModelsInput ToDomain(FetchModelsInputType input)
        => new()
        {
            ChannelType = input.ChannelType,
            BaseUrl = input.BaseUrl,
            ApiKey = input.ApiKey,
            ChannelId = input.ChannelID is null ? null : GidValue.Parse(input.ChannelID).Id,
        };
}
