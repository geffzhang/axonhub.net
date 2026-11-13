using System.Globalization;
using System.Text;
using System.Text.Json;
using HotChocolate;
using HotChocolate.Features;
using HotChocolate.Language;
using HotChocolate.Text.Json;
using HotChocolate.Types;
using AxonHub.Api.Billing;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the gqlgen Decimal scalar (internal/objects/decimal.go:14-18),
/// mapped to <c>objects.Decimal</c> in gqlgen.yml:94-97. Output writes the
/// shopspring <c>d.String()</c> form as an unquoted JSON number; input accepts
/// strings, integer and float literals, like UnmarshalDecimal.
/// </summary>
/// <remarks>
/// The .NET decimal domain is narrower than shopspring/big.Int (C176). Invalid
/// values report a shopspring-shaped parse message.
/// </remarks>
public abstract class GoDecimalScalarBase : ScalarType
{
    protected GoDecimalScalarBase(string name)
        : base(name, BindingBehavior.Explicit)
    {
    }

    public override Type RuntimeType => typeof(decimal);

    public override ScalarSerializationType SerializationType
        => ScalarSerializationType.Float;

    public override object CoerceInputLiteral(IValueNode valueLiteral) => valueLiteral switch
    {
        StringValueNode text => Parse(text.Value),
        IntValueNode integer => decimal.Parse(
            integer.Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture),
        FloatValueNode number => decimal.Parse(
            number.Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(
            "failed to decode decimal: " + valueLiteral),
    };

    public override object CoerceInputValue(
        JsonElement inputValue,
        IFeatureProvider context)
    {
        _ = context;
        return inputValue.ValueKind switch
        {
            JsonValueKind.String => Parse(inputValue.GetString() ?? string.Empty),
            JsonValueKind.Number => inputValue.TryGetDecimal(out var value)
                ? value
                : throw new InvalidOperationException(
                    "failed to decode decimal: " + inputValue.GetRawText()),
            _ => throw new InvalidOperationException(
                "failed to decode decimal: " + inputValue.GetRawText()),
        };
    }

    public override void CoerceOutputValue(object runtimeValue, ResultElement resultValue)
        => resultValue.SetNumberValue(
            Encoding.UTF8.GetBytes(
                GoDecimal.Format(Convert.ToDecimal(runtimeValue, CultureInfo.InvariantCulture))));

    public override IValueNode ValueToLiteral(object runtimeValue)
        => new FloatValueNode(Convert.ToDecimal(runtimeValue, CultureInfo.InvariantCulture));

    private static decimal Parse(string text)
        => decimal.TryParse(
            text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : throw new InvalidOperationException(
                "can't convert " + text + " to decimal: error parsing decimal " + text);
}

public sealed class GoDecimalType() : GoDecimalScalarBase("Decimal");

/// <summary>Port of the APIKeyType enum (ent.graphql:282-287).</summary>
[GraphQLName("APIKeyType")]
public enum ApiKeyTypeValue
{
    [GraphQLName("user")]
    User,

    [GraphQLName("service_account")]
    ServiceAccount,

    [GraphQLName("noauth")]
    Noauth,

    [GraphQLName("personal")]
    Personal,
}

/// <summary>Port of the APIKeyStatus enum (ent.graphql:274-278).</summary>
[GraphQLName("APIKeyStatus")]
public enum ApiKeyStatusValue
{
    [GraphQLName("enabled")]
    Enabled,

    [GraphQLName("disabled")]
    Disabled,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>Port of APIKeyOrderField (ent.graphql).</summary>
[GraphQLName("APIKeyOrderField")]
public enum ApiKeyOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,

    [GraphQLName("NAME")]
    Name,
}

/// <summary>Port of APIKeyOrder (ent.graphql).</summary>
[GraphQLName("APIKeyOrder")]
[InputObjectType("APIKeyOrder")]
public sealed class ApiKeyOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ApiKeyOrderFieldValue Field { get; set; } = ApiKeyOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of <c>APIKeyWhereInput</c> (ent.graphql:292-406). All 68 scalar field
/// predicates are modeled; the user, project and request edge predicate groups
/// remain tracked by C135.
/// </summary>
[GraphQLName("APIKeyWhereInput")]
public sealed class ApiKeyWhereInput
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
    public string? UserID { get; set; }

    [GraphQLName("userIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? UserIDNEQ { get; set; }

    [GraphQLName("userIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIDIn { get; set; }

    [GraphQLName("userIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIDNotIn { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    [GraphQLName("userIDIsNil")]
    public bool? UserIDIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    [GraphQLName("userIDNotNil")]
    public bool? UserIDNotNil { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectID { get; set; }

    [GraphQLName("projectIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectIDNEQ { get; set; }

    [GraphQLName("projectIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIDIn { get; set; }

    [GraphQLName("projectIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIDNotIn { get; set; }

    public string? Key { get; set; }

    public string? KeyNEQ { get; set; }

    public IReadOnlyList<string>? KeyIn { get; set; }

    public IReadOnlyList<string>? KeyNotIn { get; set; }

    public string? KeyGT { get; set; }

    public string? KeyGTE { get; set; }

    public string? KeyLT { get; set; }

    public string? KeyLTE { get; set; }

    public string? KeyContains { get; set; }

    public string? KeyHasPrefix { get; set; }

    public string? KeyHasSuffix { get; set; }

    public string? KeyEqualFold { get; set; }

    public string? KeyContainsFold { get; set; }

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

    public ApiKeyTypeValue? Type { get; set; }

    public ApiKeyTypeValue? TypeNEQ { get; set; }

    public IReadOnlyList<ApiKeyTypeValue>? TypeIn { get; set; }

    public IReadOnlyList<ApiKeyTypeValue>? TypeNotIn { get; set; }

    public ApiKeyStatusValue? Status { get; set; }

    public ApiKeyStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<ApiKeyStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ApiKeyStatusValue>? StatusNotIn { get; set; }

    public bool? HasUser { get; set; }

    public IReadOnlyList<UserWhereInput>? HasUserWith { get; set; }

    public bool? HasProject { get; set; }

    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }

    public bool? HasRequests { get; set; }

    public IReadOnlyList<RequestWhereInput>? HasRequestsWith { get; set; }

    public ApiKeyWhereInput? Not { get; set; }

    public IReadOnlyList<ApiKeyWhereInput>? And { get; set; }

    public IReadOnlyList<ApiKeyWhereInput>? Or { get; set; }
}

/// <summary>Port of CreateAPIKeyInput (ent.graphql).</summary>
[GraphQLName("CreateAPIKeyInput")]
public sealed class CreateApiKeyInput
{
    public string Name { get; set; } = string.Empty;

    public ApiKeyTypeValue? Type { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    public IReadOnlyList<string>? AllowedIps { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID { get; set; } = string.Empty;
}

/// <summary>Port of UpdateAPIKeyInput (ent.graphql).</summary>
[GraphQLName("UpdateAPIKeyInput")]
public sealed class UpdateApiKeyInput
{
    public string? Name { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    public IReadOnlyList<string>? AppendScopes { get; set; }

    public bool? ClearScopes { get; set; }

    public IReadOnlyList<string>? AllowedIps { get; set; }

    public IReadOnlyList<string>? AppendAllowedIps { get; set; }

    public bool? ClearAllowedIps { get; set; }
}

/// <summary>Port of UpdateAPIKeyProfilesInput (axonhub.graphql:475-478).</summary>
[GraphQLName("UpdateAPIKeyProfilesInput")]
public sealed class UpdateApiKeyProfilesInput
{
    public string ActiveProfile { get; set; } = string.Empty;

    public IReadOnlyList<ApiKeyProfileInput>? Profiles { get; set; }
}

/// <summary>Port of APIKeyProfileInput (axonhub.graphql:480-491).</summary>
[GraphQLName("APIKeyProfileInput")]
public sealed class ApiKeyProfileInput
{
    public string Name { get; set; } = string.Empty;

    [GraphQLName("templateID")]
    public int? TemplateID { get; set; }

    public string? TemplateName { get; set; }

    public IReadOnlyList<ModelMappingInput>? ModelMappings { get; set; }

    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IntType>>))]
    public IReadOnlyList<int>? ChannelIDs { get; set; }

    public IReadOnlyList<string>? ChannelTags { get; set; }

    [GraphQLType(typeof(ChannelTagsMatchModeType))]
    public string? ChannelTagsMatchMode { get; set; }

    [GraphQLName("modelIDs")]
    public IReadOnlyList<string>? ModelIDs { get; set; }

    public ApiKeyQuotaInput? Quota { get; set; }

    public string? LoadBalanceStrategy { get; set; }

    public string? TraceStickyMode { get; set; }
}

/// <summary>Port of APIKeyQuotaInput (axonhub.graphql:547-552).</summary>
[GraphQLName("APIKeyQuotaInput")]
public sealed class ApiKeyQuotaInput
{
    public int? Requests { get; set; }

    public int? TotalTokens { get; set; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal? Cost { get; set; }

    public ApiKeyQuotaPeriodInput Period { get; set; } = new();
}

/// <summary>Port of APIKeyQuotaPeriodInput (axonhub.graphql:554-558).</summary>
[GraphQLName("APIKeyQuotaPeriodInput")]
public sealed class ApiKeyQuotaPeriodInput
{
    public ApiKeyQuotaPeriodTypeValue Type { get; set; }

    public ApiKeyQuotaPastDurationInput? PastDuration { get; set; }

    public ApiKeyQuotaCalendarDurationInput? CalendarDuration { get; set; }
}

/// <summary>Port of the APIKeyQuotaPeriodType enum (axonhub.graphql).</summary>
[GraphQLName("APIKeyQuotaPeriodType")]
public enum ApiKeyQuotaPeriodTypeValue
{
    [GraphQLName("all_time")]
    AllTime,

    [GraphQLName("past_duration")]
    PastDuration,

    [GraphQLName("calendar_duration")]
    CalendarDuration,
}

/// <summary>Port of APIKeyQuotaPastDurationInput (axonhub.graphql:560-563).</summary>
[GraphQLName("APIKeyQuotaPastDurationInput")]
public sealed class ApiKeyQuotaPastDurationInput
{
    public int Value { get; set; }

    public ApiKeyQuotaPastDurationUnitValue Unit { get; set; }
}

/// <summary>Port of the APIKeyQuotaPastDurationUnit enum (axonhub.graphql).</summary>
[GraphQLName("APIKeyQuotaPastDurationUnit")]
public enum ApiKeyQuotaPastDurationUnitValue
{
    [GraphQLName("minute")]
    Minute,

    [GraphQLName("hour")]
    Hour,

    [GraphQLName("day")]
    Day,
}

/// <summary>Port of APIKeyQuotaCalendarDurationInput (axonhub.graphql:565-567).</summary>
[GraphQLName("APIKeyQuotaCalendarDurationInput")]
public sealed class ApiKeyQuotaCalendarDurationInput
{
    public ApiKeyQuotaCalendarDurationUnitValue Unit { get; set; }
}

/// <summary>Port of the APIKeyQuotaCalendarDurationUnit enum (axonhub.graphql).</summary>
[GraphQLName("APIKeyQuotaCalendarDurationUnit")]
public enum ApiKeyQuotaCalendarDurationUnitValue
{
    [GraphQLName("day")]
    Day,

    [GraphQLName("month")]
    Month,
}

/// <summary>Port of APIKeyProfiles (axonhub.graphql:525-528).</summary>
[GraphQLName("APIKeyProfiles")]
public sealed class ApiKeyProfilesType
{
    public string ActiveProfile { get; init; } = string.Empty;

    public IReadOnlyList<ApiKeyProfileType>? Profiles { get; init; }
}

/// <summary>Port of APIKeyProfile (axonhub.graphql:530-542).</summary>
[GraphQLName("APIKeyProfile")]
public sealed class ApiKeyProfileType
{
    public string Name { get; init; } = string.Empty;

    [GraphQLName("templateID")]
    public int? TemplateID { get; init; }

    public string? TemplateName { get; init; }

    public IReadOnlyList<ModelMappingType>? ModelMappings { get; init; }

    [GraphQLName("channelIDs")]
    public IReadOnlyList<int>? ChannelIDs { get; init; }

    public IReadOnlyList<string>? ChannelTags { get; init; }

    [GraphQLType(typeof(ChannelTagsMatchModeType))]
    public string? ChannelTagsMatchMode { get; init; }

    [GraphQLName("modelIDs")]
    public IReadOnlyList<string>? ModelIDs { get; init; }

    public ApiKeyQuotaType? Quota { get; init; }

    public string? LoadBalanceStrategy { get; init; }

    public string? TraceStickyMode { get; init; }
}

/// <summary>Port of APIKeyQuota (axonhub.graphql:573-578).</summary>
[GraphQLName("APIKeyQuota")]
public sealed class ApiKeyQuotaType
{
    public int? Requests { get; init; }

    public int? TotalTokens { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal? Cost { get; init; }

    public ApiKeyQuotaPeriodType Period { get; init; } = new();
}

/// <summary>Port of APIKeyQuotaPeriod (axonhub.graphql:580-584).</summary>
[GraphQLName("APIKeyQuotaPeriod")]
public sealed class ApiKeyQuotaPeriodType
{
    public ApiKeyQuotaPeriodTypeValue Type { get; init; }

    public ApiKeyQuotaPastDurationType? PastDuration { get; init; }

    public ApiKeyQuotaCalendarDurationType? CalendarDuration { get; init; }
}

/// <summary>Port of APIKeyQuotaPastDuration (axonhub.graphql:586-589).</summary>
[GraphQLName("APIKeyQuotaPastDuration")]
public sealed class ApiKeyQuotaPastDurationType
{
    public int Value { get; init; }

    public ApiKeyQuotaPastDurationUnitValue Unit { get; init; }
}

/// <summary>Port of APIKeyQuotaCalendarDuration (axonhub.graphql:591-593).</summary>
[GraphQLName("APIKeyQuotaCalendarDuration")]
public sealed class ApiKeyQuotaCalendarDurationType
{
    public ApiKeyQuotaCalendarDurationUnitValue Unit { get; init; }
}

/// <summary>Port of APIKeyQuotaUsage (axonhub.graphql:623-627).</summary>
[GraphQLName("APIKeyQuotaUsage")]
public sealed class ApiKeyQuotaUsageType
{
    // int(u.Usage.RequestCount) / int(u.Usage.TotalTokens) in the reference:
    // 64-bit on the host, 32-bit on the GraphQL wire (C190).
    public int RequestCount { get; init; }

    public int TotalTokens { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal TotalCost { get; init; }
}

/// <summary>Port of APIKeyQuotaWindow (axonhub.graphql:629-632).</summary>
[GraphQLName("APIKeyQuotaWindow")]
public sealed class ApiKeyQuotaWindowType
{
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? Start { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? End { get; init; }
}

/// <summary>Port of APIKeyProfileQuotaUsage (axonhub.graphql:634-639).</summary>
[GraphQLName("APIKeyProfileQuotaUsage")]
public sealed class ApiKeyProfileQuotaUsageType
{
    public string ProfileName { get; init; } = string.Empty;

    public ApiKeyQuotaType Quota { get; init; } = new();

    public ApiKeyQuotaWindowType Window { get; init; } = new();

    public ApiKeyQuotaUsageType Usage { get; init; } = new();
}

/// <summary>Port of the <c>APIKey</c> object type (ent.graphql:2-75).</summary>
[GraphQLName("APIKey")]
public sealed class ApiKeyType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLIgnore]
    public long ProjectDbId { get; init; }

    /// <summary>Ent's non-nullable int: an unset edge is 0.</summary>
    [GraphQLIgnore]
    public long UserDbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string? UserID { get; init; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public ApiKeyTypeValue Type { get; init; }

    public ApiKeyStatusValue Status { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public ApiKeyProfilesType? Profiles { get; init; }

    public IReadOnlyList<string>? AllowedIps { get; init; }

    /// <summary>
    /// Port of the APIKey.user field resolver (getNilableUser): privacy
    /// decides visibility, NotFound and deny both yield null.
    /// </summary>
    [GraphQLName("user")]
    public async Task<UserType?> GetUserAsync(
        [Service] ApiKeyPrivacy privacy,
        CancellationToken cancellationToken)
    {
        var user = await privacy.GetVisibleUserOrNullAsync(UserDbId, cancellationToken)
            .ConfigureAwait(false);
        return user is null ? null : ManagementMapping.ToGraphQL(user);
    }

    /// <summary>
    /// Port of the entgql eager Project edge: the Project query policy applies,
    /// and a filtered-out project is reported as "ent: project not found".
    /// </summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] ApiKeyPrivacy privacy,
        CancellationToken cancellationToken)
    {
        var project = await privacy.GetVisibleProjectAsync(ProjectDbId, cancellationToken)
            .ConfigureAwait(false);
        return project is null
            ? throw new InvalidOperationException("ent: project not found")
            : UserProfileMapping.ToGraphQL(project);
    }
}

/// <summary>Port of APIKeyEdge (ent.graphql).</summary>
[GraphQLName("APIKeyEdge")]
public sealed class ApiKeyEdgeType
{
    public ApiKeyType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of APIKeyConnection (ent.graphql).</summary>
[GraphQLName("APIKeyConnection")]
public sealed class ApiKeyConnectionType
{
    public IReadOnlyList<ApiKeyEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the API keys slice.</summary>
public static class ApiKeyMapping
{
    public static ApiKeyType ToGraphQL(ApiKeyRecord record) => new()
    {
        DbId = record.Id,
        Id = new GidValue("APIKey", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        UserDbId = record.UserId ?? 0,
        UserID = new GidValue("User", record.UserId ?? 0).ToString(),
        ProjectDbId = record.ProjectId,
        ProjectID = new GidValue("Project", record.ProjectId).ToString(),
        Key = record.Key,
        Name = record.Name,
        Type = ToGraphQLType(record.Type),
        Status = ToGraphQLStatus(record.Status),
        Scopes = ManagementMapping.ParseScopes(record.Scopes),
        Profiles = record.Profiles is null
            ? null
            : ToGraphQL(ApiKeyProfilesCodec.Deserialize(record.Profiles)),
        AllowedIps = ManagementMapping.ParseScopes(record.AllowedIps),
    };

    public static ApiKeyProfilesType ToGraphQL(ApiKeyProfilesModel model) => new()
    {
        ActiveProfile = model.ActiveProfile,
        Profiles = model.Profiles?
            .Select(ToGraphQL)
            .ToList(),
    };

    public static ApiKeyProfileType ToGraphQL(ApiKeyProfileModel profile) => new()
    {
        Name = profile.Name,
        TemplateID = profile.TemplateId is null
            ? null
            : checked((int)profile.TemplateId.Value),
        TemplateName = profile.TemplateName,
        ModelMappings = profile.ModelMappings?
            .Select(static mapping => new ModelMappingType
            {
                From = mapping.From,
                To = mapping.To,
            })
            .ToList(),
        ChannelIDs = profile.ChannelIDs?
            .Select(static id => checked((int)id))
            .ToList(),
        ChannelTags = profile.ChannelTags,
        // gqlgen's optional enum marshaller emits the raw string, including "".
        ChannelTagsMatchMode = profile.ChannelTagsMatchMode,
        ModelIDs = profile.ModelIDs,
        Quota = profile.Quota is null ? null : ToGraphQL(profile.Quota),
        LoadBalanceStrategy = profile.LoadBalanceStrategy,
        TraceStickyMode = profile.TraceStickyMode,
    };

    public static ApiKeyQuotaType ToGraphQL(ApiKeyQuotaModel quota) => new()
    {
        Requests = quota.Requests is null ? null : checked((int)quota.Requests.Value),
        TotalTokens = quota.TotalTokens is null
            ? null
            : checked((int)quota.TotalTokens.Value),
        Cost = quota.Cost,
        Period = new ApiKeyQuotaPeriodType
        {
            Type = ToGraphQLQuotaPeriodType(quota.Period.Type),
            PastDuration = quota.Period.PastDuration is null
                ? null
                : new ApiKeyQuotaPastDurationType
                {
                    Value = checked((int)quota.Period.PastDuration.Value),
                    Unit = ToGraphQLPastDurationUnit(quota.Period.PastDuration.Unit),
                },
            CalendarDuration = quota.Period.CalendarDuration is null
                ? null
                : new ApiKeyQuotaCalendarDurationType
                {
                    Unit = ToGraphQLCalendarDurationUnit(quota.Period.CalendarDuration.Unit),
                },
        },
    };

    /// <summary>
    /// Port of queryResolver.APIKeyQuotaUsages' result projection
    /// (axonhub.resolvers.go:952-981). The reference folds the int64 counters
    /// into a host-width int and marshals them through gqlgen's Int writer; the
    /// port keeps the C190 checked truncation at the GraphQL boundary.
    /// </summary>
    public static ApiKeyProfileQuotaUsageType ToGraphQL(ProfileQuotaUsage usage) => new()
    {
        ProfileName = usage.ProfileName,
        Quota = ToGraphQL(usage.Quota),
        Window = new ApiKeyQuotaWindowType
        {
            Start = usage.Window.Start,
            End = usage.Window.End,
        },
        Usage = new ApiKeyQuotaUsageType
        {
            RequestCount = checked((int)usage.Usage.RequestCount),
            TotalTokens = checked((int)usage.Usage.TotalTokens),
            TotalCost = usage.Usage.TotalCost,
        },
    };

    public static ApiKeyListFilter? ToFilter(ApiKeyWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ApiKeyListFilter
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
            UserId = where.UserID is null ? null : GidValue.Parse(where.UserID).Id,
            UserIdNEQ = where.UserIDNEQ is null
                ? null
                : GidValue.Parse(where.UserIDNEQ).Id,
            UserIdIn = where.UserIDIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            UserIdNotIn = where.UserIDNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            UserIdIsNil = where.UserIDIsNil == true,
            UserIdNotNil = where.UserIDNotNil == true,
            ProjectId = where.ProjectID is null
                ? null
                : GidValue.Parse(where.ProjectID).Id,
            ProjectIdNEQ = where.ProjectIDNEQ is null
                ? null
                : GidValue.Parse(where.ProjectIDNEQ).Id,
            ProjectIdIn = where.ProjectIDIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            ProjectIdNotIn = where.ProjectIDNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            Key = where.Key,
            KeyNEQ = where.KeyNEQ,
            KeyIn = where.KeyIn,
            KeyNotIn = where.KeyNotIn,
            KeyGT = where.KeyGT,
            KeyGTE = where.KeyGTE,
            KeyLT = where.KeyLT,
            KeyLTE = where.KeyLTE,
            KeyContains = where.KeyContains,
            KeyHasPrefix = where.KeyHasPrefix,
            KeyHasSuffix = where.KeyHasSuffix,
            KeyEqualFold = where.KeyEqualFold,
            KeyContainsFold = where.KeyContainsFold,
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
            Type = where.Type is null ? null : ToWireType(where.Type.Value),
            TypeNEQ = where.TypeNEQ is null ? null : ToWireType(where.TypeNEQ.Value),
            TypeIn = where.TypeIn?
                .Select(static type => ToWireType(type))
                .ToList(),
            TypeNotIn = where.TypeNotIn?
                .Select(static type => ToWireType(type))
                .ToList(),
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            HasUser = where.HasUser,
            HasUserWith = where.HasUserWith?
                .Select(ManagementMapping.ToFilter)
                .OfType<UserListFilter>()
                .ToList(),
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(ProjectMapping.ToFilter)
                .OfType<ProjectListFilter>()
                .ToList(),
            HasRequests = where.HasRequests,
            HasRequestsWith = where.HasRequestsWith?
                .Select(static nested => TraceMapping.ToFilter(nested))
                .OfType<RequestListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ApiKeyListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ApiKeyListFilter>().ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is rewritten to ent.DefaultAPIKeyOrder's field, which is
    /// <c>id</c> (ent/gql_pagination.go). NAME orders by the name column.
    /// </summary>
    public static ApiKeySortField ToSortField(ApiKeyOrder? orderBy)
        => orderBy?.Field switch
        {
            ApiKeyOrderFieldValue.UpdatedAt => ApiKeySortField.UpdatedAt,
            ApiKeyOrderFieldValue.Name => ApiKeySortField.Name,
            _ => ApiKeySortField.Id,
        };

    /// <summary>
    /// Edge connections keep the generated order field; only the root
    /// resolver rewrites CREATED_AT to the default id order.
    /// </summary>
    public static ApiKeySortField ToEdgeSortField(ApiKeyOrder? orderBy)
        => orderBy?.Field switch
        {
            ApiKeyOrderFieldValue.CreatedAt => ApiKeySortField.CreatedAt,
            ApiKeyOrderFieldValue.UpdatedAt => ApiKeySortField.UpdatedAt,
            ApiKeyOrderFieldValue.Name => ApiKeySortField.Name,
            _ => ApiKeySortField.Id,
        };

    public static SortDirection ToSortDirection(ApiKeyOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static ApiKeyConnectionType BuildConnection(
        ListPage<ApiKeyRecord> page,
        ApiKeySortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new ApiKeyEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        ApiKeySortField.CreatedAt => node.CreatedAt,
                        ApiKeySortField.UpdatedAt => node.UpdatedAt,
                        ApiKeySortField.Name => node.Name,
                        _ => null,
                    }),
            })
            .ToList();

        return new ApiKeyConnectionType
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

    public static string ToWireType(ApiKeyTypeValue type) => type switch
    {
        ApiKeyTypeValue.User => "user",
        ApiKeyTypeValue.ServiceAccount => "service_account",
        ApiKeyTypeValue.Noauth => "noauth",
        ApiKeyTypeValue.Personal => "personal",
        _ => throw new GraphQLException(
            "Enum \"APIKeyType\" cannot represent value: \"" + type + "\""),
    };

    public static ApiKeyTypeValue ToGraphQLType(string type) => type switch
    {
        "user" => ApiKeyTypeValue.User,
        "service_account" => ApiKeyTypeValue.ServiceAccount,
        "noauth" => ApiKeyTypeValue.Noauth,
        "personal" => ApiKeyTypeValue.Personal,
        _ => throw new GraphQLException(
            "Enum \"APIKeyType\" cannot represent value: \"" + type + "\""),
    };

    public static string ToWireStatus(ApiKeyStatusValue status) => status switch
    {
        ApiKeyStatusValue.Enabled => "enabled",
        ApiKeyStatusValue.Disabled => "disabled",
        ApiKeyStatusValue.Archived => "archived",
        _ => throw new GraphQLException(
            "Enum \"APIKeyStatus\" cannot represent value: \"" + status + "\""),
    };

    public static ApiKeyStatusValue ToGraphQLStatus(string status) => status switch
    {
        "enabled" => ApiKeyStatusValue.Enabled,
        "disabled" => ApiKeyStatusValue.Disabled,
        "archived" => ApiKeyStatusValue.Archived,
        _ => throw new GraphQLException(
            "Enum \"APIKeyStatus\" cannot represent value: \"" + status + "\""),
    };

    public static string ToWireType(ApiKeyQuotaPeriodTypeValue type) => type switch
    {
        ApiKeyQuotaPeriodTypeValue.AllTime => "all_time",
        ApiKeyQuotaPeriodTypeValue.PastDuration => "past_duration",
        ApiKeyQuotaPeriodTypeValue.CalendarDuration => "calendar_duration",
        _ => throw new GraphQLException(
            "Enum \"APIKeyQuotaPeriodType\" cannot represent value: \"" + type + "\""),
    };

    public static ApiKeyQuotaPeriodTypeValue ToGraphQLQuotaPeriodType(string type) => type switch
    {
        "all_time" => ApiKeyQuotaPeriodTypeValue.AllTime,
        "past_duration" => ApiKeyQuotaPeriodTypeValue.PastDuration,
        "calendar_duration" => ApiKeyQuotaPeriodTypeValue.CalendarDuration,
        _ => throw new GraphQLException(
            "Enum \"APIKeyQuotaPeriodType\" cannot represent value: \"" + type + "\""),
    };

    public static string ToWireUnit(ApiKeyQuotaPastDurationUnitValue unit) => unit switch
    {
        ApiKeyQuotaPastDurationUnitValue.Minute => "minute",
        ApiKeyQuotaPastDurationUnitValue.Hour => "hour",
        ApiKeyQuotaPastDurationUnitValue.Day => "day",
        _ => throw new GraphQLException(
            "Enum \"APIKeyQuotaPastDurationUnit\" cannot represent value: \"" + unit + "\""),
    };

    public static ApiKeyQuotaPastDurationUnitValue ToGraphQLPastDurationUnit(string unit) => unit switch
    {
        "minute" => ApiKeyQuotaPastDurationUnitValue.Minute,
        "hour" => ApiKeyQuotaPastDurationUnitValue.Hour,
        "day" => ApiKeyQuotaPastDurationUnitValue.Day,
        _ => throw new GraphQLException(
            "Enum \"APIKeyQuotaPastDurationUnit\" cannot represent value: \"" + unit + "\""),
    };

    public static string ToWireUnit(ApiKeyQuotaCalendarDurationUnitValue unit) => unit switch
    {
        ApiKeyQuotaCalendarDurationUnitValue.Day => "day",
        ApiKeyQuotaCalendarDurationUnitValue.Month => "month",
        _ => throw new GraphQLException(
            "Enum \"APIKeyQuotaCalendarDurationUnit\" cannot represent value: \"" + unit + "\""),
    };

    public static ApiKeyQuotaCalendarDurationUnitValue ToGraphQLCalendarDurationUnit(string unit) => unit switch
    {
        "day" => ApiKeyQuotaCalendarDurationUnitValue.Day,
        "month" => ApiKeyQuotaCalendarDurationUnitValue.Month,
        _ => throw new GraphQLException(
            "Enum \"APIKeyQuotaCalendarDurationUnit\" cannot represent value: \"" + unit + "\""),
    };

    /// <summary>Port of the input-to-objects conversion done by gqlgen bindings.</summary>
    public static ApiKeyProfilesModel ToModel(UpdateApiKeyProfilesInput input) => new()
    {
        ActiveProfile = input.ActiveProfile,
        Profiles = input.Profiles?
            .Select(ToModel)
            .ToList(),
    };

    public static ApiKeyProfileModel ToModel(ApiKeyProfileInput input) => new()
    {
        Name = input.Name,
        TemplateId = input.TemplateID,
        TemplateName = input.TemplateName ?? string.Empty,
        ModelMappings = input.ModelMappings?
            .Select(static mapping => new ApiKeyModelMapping
            {
                From = mapping.From,
                To = mapping.To,
            })
            .ToList(),
        Quota = input.Quota is null ? null : ToModel(input.Quota),
        LoadBalanceStrategy = input.LoadBalanceStrategy,
        TraceStickyMode = input.TraceStickyMode,
        ChannelIDs = input.ChannelIDs?
            .Select(static id => (long)id)
            .ToList(),
        ChannelTags = input.ChannelTags,
        ChannelTagsMatchMode = input.ChannelTagsMatchMode ?? string.Empty,
        ModelIDs = input.ModelIDs,
    };

    public static ApiKeyQuotaModel ToModel(ApiKeyQuotaInput input) => new()
    {
        Requests = input.Requests,
        TotalTokens = input.TotalTokens,
        Cost = input.Cost,
        Period = new ApiKeyQuotaPeriodModel
        {
            Type = ToWireType(input.Period.Type),
            PastDuration = input.Period.PastDuration is null
                ? null
                : new ApiKeyQuotaPastDurationModel
                {
                    Value = input.Period.PastDuration.Value,
                    Unit = ToWireUnit(input.Period.PastDuration.Unit),
                },
            CalendarDuration = input.Period.CalendarDuration is null
                ? null
                : new ApiKeyQuotaCalendarDurationModel
                {
                    Unit = ToWireUnit(input.Period.CalendarDuration.Unit),
                },
        },
    };
}
