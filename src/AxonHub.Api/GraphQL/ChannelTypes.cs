using System.Text.Json;
using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>ChannelStatus</c> enum (ent.graphql:1261-1265).</summary>
[GraphQLName("ChannelStatus")]
public enum ChannelStatusValue
{
    [GraphQLName("enabled")]
    Enabled,

    [GraphQLName("disabled")]
    Disabled,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>Port of the gqlgen <c>ChannelType</c> enum (ent.graphql:1269-1344).</summary>
[GraphQLName("ChannelType")]
public enum ChannelTypeValue
{
    [GraphQLName("openai")]
    Openai,

    [GraphQLName("openai_responses")]
    OpenaiResponses,

    [GraphQLName("atlascloud")]
    Atlascloud,

    [GraphQLName("cline")]
    Cline,

    [GraphQLName("codex")]
    Codex,

    [GraphQLName("vercel")]
    Vercel,

    [GraphQLName("anthropic")]
    Anthropic,

    [GraphQLName("anthropic_aws")]
    AnthropicAws,

    [GraphQLName("anthropic_gcp")]
    AnthropicGcp,

    [GraphQLName("gemini_openai")]
    GeminiOpenai,

    [GraphQLName("gemini")]
    Gemini,

    [GraphQLName("gemini_vertex")]
    GeminiVertex,

    [GraphQLName("deepseek")]
    Deepseek,

    [GraphQLName("deepseek_anthropic")]
    DeepseekAnthropic,

    [GraphQLName("deepinfra")]
    Deepinfra,

    [GraphQLName("qiniu")]
    Qiniu,

    [GraphQLName("fireworks")]
    Fireworks,

    [GraphQLName("doubao")]
    Doubao,

    [GraphQLName("doubao_anthropic")]
    DoubaoAnthropic,

    [GraphQLName("moonshot")]
    Moonshot,

    [GraphQLName("moonshot_anthropic")]
    MoonshotAnthropic,

    [GraphQLName("zhipu")]
    Zhipu,

    [GraphQLName("zai")]
    Zai,

    [GraphQLName("zhipu_anthropic")]
    ZhipuAnthropic,

    [GraphQLName("zai_anthropic")]
    ZaiAnthropic,

    [GraphQLName("anthropic_fake")]
    AnthropicFake,

    [GraphQLName("openai_fake")]
    OpenaiFake,

    [GraphQLName("openrouter")]
    Openrouter,

    [GraphQLName("xiaomi")]
    Xiaomi,

    [GraphQLName("xiaomi_anthropic")]
    XiaomiAnthropic,

    [GraphQLName("xai")]
    Xai,

    [GraphQLName("xai_responses")]
    XaiResponses,

    [GraphQLName("xai_subscription")]
    XaiSubscription,

    [GraphQLName("ppio")]
    Ppio,

    [GraphQLName("siliconflow")]
    Siliconflow,

    [GraphQLName("volcengine")]
    Volcengine,

    [GraphQLName("volcengine_anthropic")]
    VolcengineAnthropic,

    [GraphQLName("longcat")]
    Longcat,

    [GraphQLName("longcat_anthropic")]
    LongcatAnthropic,

    [GraphQLName("minimax")]
    Minimax,

    [GraphQLName("minimax_anthropic")]
    MinimaxAnthropic,

    [GraphQLName("aihubmix")]
    Aihubmix,

    [GraphQLName("aihubmix_anthropic")]
    AihubmixAnthropic,

    [GraphQLName("burncloud")]
    Burncloud,

    [GraphQLName("modelscope")]
    Modelscope,

    [GraphQLName("bailian")]
    Bailian,

    [GraphQLName("bailian_responses")]
    BailianResponses,

    [GraphQLName("bailian_anthropic")]
    BailianAnthropic,

    [GraphQLName("moonshot_coding")]
    MoonshotCoding,

    [GraphQLName("jina")]
    Jina,

    [GraphQLName("github")]
    Github,

    [GraphQLName("github_copilot")]
    GithubCopilot,

    [GraphQLName("claudecode")]
    Claudecode,

    [GraphQLName("cerebras")]
    Cerebras,

    [GraphQLName("antigravity")]
    Antigravity,

    [GraphQLName("nanogpt")]
    Nanogpt,

    [GraphQLName("nanogpt_responses")]
    NanogptResponses,

    [GraphQLName("opencode_go")]
    OpencodeGo,

    [GraphQLName("opencode_go_anthropic")]
    OpencodeGoAnthropic,

    [GraphQLName("ollama")]
    Ollama,

    [GraphQLName("ollama_anthropic")]
    OllamaAnthropic,

    [GraphQLName("evolink")]
    Evolink,

    [GraphQLName("evolink_anthropic")]
    EvolinkAnthropic,

    [GraphQLName("groq")]
    Groq,

    [GraphQLName("qiniu_anthropic")]
    QiniuAnthropic,

    [GraphQLName("fenno")]
    Fenno,

    [GraphQLName("zenmux")]
    Zenmux,

    [GraphQLName("zenmux_responses")]
    ZenmuxResponses,

    [GraphQLName("zenmux_anthropic")]
    ZenmuxAnthropic,

    [GraphQLName("zenmux_gemini")]
    ZenmuxGemini,

    [GraphQLName("zenmux_video")]
    ZenmuxVideo,

    [GraphQLName("commandcode")]
    Commandcode,

    [GraphQLName("commandcode_anthropic")]
    CommandcodeAnthropic,

    [GraphQLName("typesafe")]
    Typesafe,

    // Live-tree extension used by the migrated management frontend (C139).
    [GraphQLName("cloudflare")]
    Cloudflare,
}

/// <summary>Port of the gqlgen <c>ChannelOrderField</c> enum (ent.graphql:959-966).</summary>
[GraphQLName("ChannelOrderField")]
public enum ChannelOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,

    [GraphQLName("TYPE")]
    Type,

    [GraphQLName("NAME")]
    Name,

    [GraphQLName("STATUS")]
    Status,

    [GraphQLName("ORDERING_WEIGHT")]
    OrderingWeight,
}

/// <summary>Port of <c>ChannelOrder</c> (ent.graphql:946-955).</summary>
[GraphQLName("ChannelOrder")]
[InputObjectType("ChannelOrder")]
public sealed class ChannelOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ChannelOrderFieldValue Field { get; set; } = ChannelOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of <c>ChannelWhereInput</c> (ent.graphql:1349-1576). All scalar field
/// predicates and the hasChannelProbes / hasChannelModelPrices /
/// hasProviderQuotaStatus edges are modeled; the hasRequests / hasExecutions /
/// hasUsageLogs edges remain a registered gap (C135).
/// </summary>
public sealed class ChannelWhereInput
{
    [GraphQLName("id")]
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLName("idNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLName("idIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLName("idNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLName("idGT")]
    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLName("idGTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLName("idLT")]
    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLName("idLTE")]
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

    public ChannelTypeValue? Type { get; set; }

    public ChannelTypeValue? TypeNEQ { get; set; }

    public IReadOnlyList<ChannelTypeValue>? TypeIn { get; set; }

    public IReadOnlyList<ChannelTypeValue>? TypeNotIn { get; set; }

    [GraphQLName("baseURL")]
    public string? BaseURL { get; set; }

    [GraphQLName("baseURLNEQ")]
    public string? BaseURLNEQ { get; set; }

    [GraphQLName("baseURLIn")]
    public IReadOnlyList<string>? BaseURLIn { get; set; }

    [GraphQLName("baseURLNotIn")]
    public IReadOnlyList<string>? BaseURLNotIn { get; set; }

    [GraphQLName("baseURLGT")]
    public string? BaseURLGT { get; set; }

    [GraphQLName("baseURLGTE")]
    public string? BaseURLGTE { get; set; }

    [GraphQLName("baseURLLT")]
    public string? BaseURLLT { get; set; }

    [GraphQLName("baseURLLTE")]
    public string? BaseURLLTE { get; set; }

    [GraphQLName("baseURLContains")]
    public string? BaseURLContains { get; set; }

    [GraphQLName("baseURLHasPrefix")]
    public string? BaseURLHasPrefix { get; set; }

    [GraphQLName("baseURLHasSuffix")]
    public string? BaseURLHasSuffix { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    [GraphQLName("baseURLIsNil")]
    public bool? BaseURLIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    [GraphQLName("baseURLNotNil")]
    public bool? BaseURLNotNil { get; set; }

    [GraphQLName("baseURLEqualFold")]
    public string? BaseURLEqualFold { get; set; }

    [GraphQLName("baseURLContainsFold")]
    public string? BaseURLContainsFold { get; set; }

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

    public ChannelStatusValue? Status { get; set; }

    public ChannelStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<ChannelStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ChannelStatusValue>? StatusNotIn { get; set; }

    public bool? AutoSyncSupportedModels { get; set; }

    public bool? AutoSyncSupportedModelsNEQ { get; set; }

    public string? AutoSyncModelPattern { get; set; }

    public string? AutoSyncModelPatternNEQ { get; set; }

    public IReadOnlyList<string>? AutoSyncModelPatternIn { get; set; }

    public IReadOnlyList<string>? AutoSyncModelPatternNotIn { get; set; }

    public string? AutoSyncModelPatternGT { get; set; }

    public string? AutoSyncModelPatternGTE { get; set; }

    public string? AutoSyncModelPatternLT { get; set; }

    public string? AutoSyncModelPatternLTE { get; set; }

    public string? AutoSyncModelPatternContains { get; set; }

    public string? AutoSyncModelPatternHasPrefix { get; set; }

    public string? AutoSyncModelPatternHasSuffix { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AutoSyncModelPatternIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AutoSyncModelPatternNotNil { get; set; }

    public string? AutoSyncModelPatternEqualFold { get; set; }

    public string? AutoSyncModelPatternContainsFold { get; set; }

    public string? DefaultTestModel { get; set; }

    public string? DefaultTestModelNEQ { get; set; }

    public IReadOnlyList<string>? DefaultTestModelIn { get; set; }

    public IReadOnlyList<string>? DefaultTestModelNotIn { get; set; }

    public string? DefaultTestModelGT { get; set; }

    public string? DefaultTestModelGTE { get; set; }

    public string? DefaultTestModelLT { get; set; }

    public string? DefaultTestModelLTE { get; set; }

    public string? DefaultTestModelContains { get; set; }

    public string? DefaultTestModelHasPrefix { get; set; }

    public string? DefaultTestModelHasSuffix { get; set; }

    public string? DefaultTestModelEqualFold { get; set; }

    public string? DefaultTestModelContainsFold { get; set; }

    public int? OrderingWeight { get; set; }

    public int? OrderingWeightNEQ { get; set; }

    public IReadOnlyList<int>? OrderingWeightIn { get; set; }

    public IReadOnlyList<int>? OrderingWeightNotIn { get; set; }

    public int? OrderingWeightGT { get; set; }

    public int? OrderingWeightGTE { get; set; }

    public int? OrderingWeightLT { get; set; }

    public int? OrderingWeightLTE { get; set; }

    public string? ErrorMessage { get; set; }

    public string? ErrorMessageNEQ { get; set; }

    public IReadOnlyList<string>? ErrorMessageIn { get; set; }

    public IReadOnlyList<string>? ErrorMessageNotIn { get; set; }

    public string? ErrorMessageGT { get; set; }

    public string? ErrorMessageGTE { get; set; }

    public string? ErrorMessageLT { get; set; }

    public string? ErrorMessageLTE { get; set; }

    public string? ErrorMessageContains { get; set; }

    public string? ErrorMessageHasPrefix { get; set; }

    public string? ErrorMessageHasSuffix { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? ErrorMessageIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? ErrorMessageNotNil { get; set; }

    public string? ErrorMessageEqualFold { get; set; }

    public string? ErrorMessageContainsFold { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? AutoDisabledAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? AutoDisabledAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAtLTE { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AutoDisabledAtIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AutoDisabledAtNotNil { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? AutoDisableExpiresAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? AutoDisableExpiresAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAtLTE { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AutoDisableExpiresAtIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AutoDisableExpiresAtNotNil { get; set; }

    public string? Remark { get; set; }

    public string? RemarkNEQ { get; set; }

    public IReadOnlyList<string>? RemarkIn { get; set; }

    public IReadOnlyList<string>? RemarkNotIn { get; set; }

    public string? RemarkGT { get; set; }

    public string? RemarkGTE { get; set; }

    public string? RemarkLT { get; set; }

    public string? RemarkLTE { get; set; }

    public string? RemarkContains { get; set; }

    public string? RemarkHasPrefix { get; set; }

    public string? RemarkHasSuffix { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? RemarkIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? RemarkNotNil { get; set; }

    public string? RemarkEqualFold { get; set; }

    public string? RemarkContainsFold { get; set; }

    public bool? HasChannelProbes { get; set; }

    public IReadOnlyList<ChannelProbeWhereInput>? HasChannelProbesWith { get; set; }

    public bool? HasChannelModelPrices { get; set; }

    public IReadOnlyList<ChannelModelPriceWhereInput>? HasChannelModelPricesWith { get; set; }

    public bool? HasProviderQuotaStatus { get; set; }

    public IReadOnlyList<ProviderQuotaStatusWhereInput>? HasProviderQuotaStatusWith { get; set; }

    public ChannelWhereInput? Not { get; set; }

    public IReadOnlyList<ChannelWhereInput>? And { get; set; }

    public IReadOnlyList<ChannelWhereInput>? Or { get; set; }
}

/// <summary>Port of <c>QueryChannelInput</c> (axonhub.graphql:1054-1094).</summary>
public sealed class QueryChannelInput
{
    [GraphQLType(typeof(CursorType))]
    public CursorValue? After { get; set; }

    public int? First { get; set; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue? Before { get; set; }

    public int? Last { get; set; }

    public ChannelOrder? OrderBy { get; set; }

    public ChannelWhereInput? Where { get; set; }

    public string? HasTag { get; set; }

    public string? Model { get; set; }
}

/// <summary>Port of <c>CountChannelsByTypeInput</c> (axonhub.graphql:1166-1171).</summary>
public sealed class CountChannelsByTypeInput
{
    public IReadOnlyList<ChannelStatusValue>? StatusIn { get; set; }
}

/// <summary>Port of <c>ModelMapping</c> (axonhub.graphql:5-8).</summary>
[GraphQLName("ModelMapping")]
public sealed class ModelMappingType
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;
}

/// <summary>Port of <c>OverrideMatch</c> (axonhub.graphql:29-32).</summary>
[GraphQLName("OverrideMatch")]
public sealed class OverrideMatchType
{
    public string Path { get; init; } = string.Empty;

    public string Eq { get; init; } = string.Empty;
}

/// <summary>Port of <c>OverrideOperation</c> (axonhub.graphql:13-24).</summary>
[GraphQLName("OverrideOperation")]
public sealed class OverrideOperationType
{
    public string Op { get; init; } = string.Empty;

    public string? Path { get; init; }

    public string? From { get; init; }

    public string? To { get; init; }

    public string? Value { get; init; }

    public string? Condition { get; init; }

    public OverrideMatchType? Match { get; init; }

    public int? Index { get; init; }

    public bool? Splat { get; init; }
}

/// <summary>Port of <c>ReasoningEffortMapping</c> (axonhub.graphql:44-47).</summary>
[GraphQLName("ReasoningEffortMapping")]
public sealed class ReasoningEffortMappingType
{
    public string From { get; init; } = string.Empty;

    public string To { get; init; } = string.Empty;
}

/// <summary>Port of <c>TransformOptions</c> (axonhub.graphql:49-54).</summary>
[GraphQLName("TransformOptions")]
public sealed class TransformOptionsType
{
    public bool ForceArrayInstructions { get; init; }

    public bool ForceArrayInputs { get; init; }

    public bool ReplaceDeveloperRoleWithSystem { get; init; }

    public IReadOnlyList<ReasoningEffortMappingType>? ReasoningEffortMapping { get; init; }
}

/// <summary>Port of the gqlgen <c>ProxyType</c> enum values.</summary>
/// <remarks>
/// The SDL declares DISABLED/ENVIRONMENT/URL but the reference binds an
/// external type whose marshaler writes the raw stored value (lowercase);
/// the frontend compares lowercase, so the port models the wire values (C139).
/// </remarks>
[GraphQLName("ProxyType")]
public enum ProxyTypeValue
{
    [GraphQLName("disabled")]
    Disabled,

    [GraphQLName("environment")]
    Environment,

    [GraphQLName("url")]
    Url,
}

/// <summary>Port of <c>ProxyConfig</c> (axonhub.graphql:64-70).</summary>
[GraphQLName("ProxyConfig")]
public sealed class ProxyConfigType
{
    public ProxyTypeValue Type { get; init; }

    public string? Url { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool DisableConnectionReuse { get; init; }
}

/// <summary>Port of <c>RetryableErrorPattern</c> (axonhub.graphql:118-121).</summary>
[GraphQLName("RetryableErrorPattern")]
public sealed class RetryableErrorPatternType
{
    public string Pattern { get; init; } = string.Empty;

    public bool Regex { get; init; }
}

/// <summary>Port of <c>ModelProtocol</c> (axonhub.graphql:129-133).</summary>
[GraphQLName("ModelProtocol")]
public sealed class ModelProtocolType
{
    public string Model { get; init; } = string.Empty;

    public IReadOnlyList<string> ApiFormats { get; init; } = [];

    public bool? Enabled { get; init; }
}

/// <summary>Port of <c>CommandCodeQuotaSettings</c> (axonhub.graphql:172-174).</summary>
[GraphQLName("CommandCodeQuotaSettings")]
public sealed class CommandCodeQuotaSettingsType
{
    public string? AuthCookie { get; init; }
}

/// <summary>Port of <c>OllamaQuotaSettings</c> (axonhub.graphql:186-188).</summary>
[GraphQLName("OllamaQuotaSettings")]
public sealed class OllamaQuotaSettingsType
{
    public string? AuthCookie { get; init; }
}

/// <summary>Port of <c>ChannelProviderQuotaSettings</c> (axonhub.graphql:191-194).</summary>
[GraphQLName("ChannelProviderQuotaSettings")]
public sealed class ChannelProviderQuotaSettingsType
{
    public CommandCodeQuotaSettingsType? CommandCode { get; init; }

    public OllamaQuotaSettingsType? Ollama { get; init; }
}

/// <summary>Port of <c>ChannelRateLimit</c> (axonhub.graphql:321-327).</summary>
[GraphQLName("ChannelRateLimit")]
public sealed class ChannelRateLimitType
{
    public int? Rpm { get; init; }

    public int? Tpm { get; init; }

    public int? MaxConcurrent { get; init; }

    public int? QueueSize { get; init; }

    public int? QueueTimeoutMs { get; init; }
}

/// <summary>Port of the gqlgen <c>ChannelQuotaRoutingMode</c> enum values.</summary>
[GraphQLName("ChannelQuotaRoutingMode")]
public enum ChannelQuotaRoutingModeValue
{
    [GraphQLName("INHERIT")]
    Inherit,

    [GraphQLName("IGNORE_QUOTA")]
    IgnoreQuota,

    [GraphQLName("REMOVE_ON_EXHAUSTED")]
    RemoveOnExhausted,

    [GraphQLName("BACKPRESSURE")]
    Backpressure,
}

/// <summary>Port of the live <c>ChannelResponseTimeoutMode</c> enum.</summary>
[GraphQLName("ChannelResponseTimeoutMode")]
public enum ChannelResponseTimeoutModeValue
{
    [GraphQLName("INHERIT")]
    Inherit,

    [GraphQLName("CUSTOM")]
    Custom,
}

/// <summary>Port of the live <c>ChannelResponseTimeout</c> type.</summary>
[GraphQLName("ChannelResponseTimeout")]
public sealed class ChannelResponseTimeoutType
{
    public ChannelResponseTimeoutModeValue? Mode { get; init; }

    public int? StreamFirstEventTimeoutSeconds { get; init; }

    public int? NonStreamResponseTimeoutSeconds { get; init; }
}

/// <summary>Port of the gqlgen <c>CapabilityPolicy</c> enum values.</summary>
[GraphQLName("CapabilityPolicy")]
public enum CapabilityPolicyValue
{
    [GraphQLName("unlimited")]
    Unlimited,

    [GraphQLName("require")]
    Require,

    [GraphQLName("forbid")]
    Forbid,
}

// APIKeyAutoDisableActionValue lives in SystemSettingsTypes.cs (shared by the
// retry-policy and channel-policy surfaces, one SDL enum).

/// <summary>Port of the gqlgen <c>APIKeyAutoDisableMode</c> enum values.</summary>
[GraphQLName("APIKeyAutoDisableMode")]
public enum ApiKeyAutoDisableModeValue
{
    [GraphQLName("inherit")]
    Inherit,

    [GraphQLName("custom")]
    Custom,

    [GraphQLName("off")]
    Off,
}

// ApiKeyAutoDisableRuleType lives in SystemSettingsTypes.cs (axonhub.graphql:
// 252-261); ChannelPolicies reuses that exact SDL type.

/// <summary>Port of <c>ChannelPolicies</c> (axonhub.graphql:263-267).</summary>
[GraphQLName("ChannelPolicies")]
public sealed class ChannelPoliciesType
{
    public CapabilityPolicyValue? Stream { get; init; }

    public ApiKeyAutoDisableModeValue? ApiKeyAutoDisableMode { get; init; }

    public IReadOnlyList<ApiKeyAutoDisableRuleType>? ApiKeyAutoDisableRules { get; init; }
}

/// <summary>Port of <c>ChannelSettings</c> (axonhub.graphql:196-220).</summary>
[GraphQLName("ChannelSettings")]
public sealed class ChannelSettingsType
{
    public string? ExtraModelPrefix { get; init; }

    public IReadOnlyList<ModelMappingType>? ModelMappings { get; init; }

    public IReadOnlyList<string>? AutoTrimedModelPrefixes { get; init; }

    public bool HideOriginalModels { get; init; }

    public bool HideMappedModels { get; init; }

    public bool LowercaseModelId { get; init; }

    public ProxyConfigType? Proxy { get; init; }

    public TransformOptionsType? TransformOptions { get; init; }

    public IReadOnlyList<OverrideOperationType> HeaderOverrideOperations { get; init; } = [];

    public IReadOnlyList<OverrideOperationType> BodyOverrideOperations { get; init; } = [];

    public bool? PassThroughUserAgent { get; init; }

    public bool? PassThroughBody { get; init; }

    public ChannelRateLimitType? RateLimit { get; init; }

    public IReadOnlyList<int>? RetryableStatusCodes { get; init; }

    public IReadOnlyList<RetryableErrorPatternType>? RetryableErrorPatterns { get; init; }

    public int? MaxSingleChannelRetries { get; init; }

    public ChannelResponseTimeoutType? ResponseTimeout { get; init; }

    public IReadOnlyList<ModelProtocolType>? ModelProtocols { get; init; }

    [GraphQLName("apiKeyStrategy")]
    public string? ApiKeyStrategy { get; init; }

    public int? ApiKeyRoundRobinSwitchAfter { get; init; }

    /// <summary>
    /// Set by ChannelMapping.ToSettings; the live resolver behind it gates the
    /// value on write_channels (<see cref="GetProviderQuotaAsync"/>).
    /// </summary>
    [GraphQLIgnore]
    public ChannelProviderQuotaSettingsType? ProviderQuota { get; init; }

    public ChannelQuotaRoutingModeValue? QuotaRoutingMode { get; init; }

    /// <summary>
    /// Port of channelSettingsResolver.ProviderQuota
    /// (axonhub.resolvers.go:162-171): quota-only credentials (e.g. the Command
    /// Code session cookie) are only exposed to channel writers.
    /// </summary>
    [GraphQLName("providerQuota")]
    public async Task<ChannelProviderQuotaSettingsType?> GetProviderQuotaAsync(
        [Service] AuthorizationScope scope,
        CancellationToken cancellationToken)
        => await scope
            .HasUserScopeAsync(AuthorizationScope.WriteChannels, cancellationToken)
            .ConfigureAwait(false)
            ? ProviderQuota
            : null;
}

/// <summary>Port of <c>ChannelEndpoint</c> (axonhub.graphql:72-77).</summary>
[GraphQLName("ChannelEndpoint")]
public sealed class ChannelEndpointType
{
    [GraphQLName("apiFormat")]
    public string ApiFormat { get; init; } = string.Empty;

    public string? Path { get; init; }

    [GraphQLName("baseURL")]
    public string? BaseUrl { get; init; }

    public string? Transport { get; init; }
}

/// <summary>Port of the live <c>VertexAPIKey</c> type.</summary>
[GraphQLName("VertexAPIKey")]
public sealed class VertexApiKeyType
{
    [GraphQLName("projectID")]
    public string ProjectId { get; init; } = string.Empty;

    [GraphQLName("apiKey")]
    public string ApiKey { get; init; } = string.Empty;

    public string Region { get; init; } = string.Empty;
}

/// <summary>Port of <c>GCPCredential</c> (axonhub.graphql:401-405).</summary>
[GraphQLName("GCPCredential")]
public sealed class GcpCredentialType
{
    public string Region { get; init; } = string.Empty;

    [GraphQLName("projectID")]
    public string ProjectId { get; init; } = string.Empty;

    public string JsonData { get; init; } = string.Empty;
}

/// <summary>Port of <c>OAuthCredentials</c> (axonhub.graphql:386-393).</summary>
[GraphQLName("OAuthCredentials")]
public sealed class OAuthCredentialsType
{
    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    [GraphQLName("clientID")]
    public string? ClientId { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ExpiresAt { get; init; }

    public string? TokenType { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }
}

/// <summary>Port of <c>ChannelCredentials</c> (axonhub.graphql:310-316 + live vertexApiKeys).</summary>
[GraphQLName("ChannelCredentials")]
public sealed class ChannelCredentialsType
{
    [GraphQLName("apiKey")]
    public string? ApiKey { get; init; }

    [GraphQLName("apiKeys")]
    public IReadOnlyList<string>? ApiKeys { get; init; }

    [GraphQLName("vertexApiKeys")]
    public IReadOnlyList<VertexApiKeyType>? VertexApiKeys { get; init; }

    [GraphQLName("gcp")]
    public GcpCredentialType? Gcp { get; init; }

    [GraphQLName("oauth")]
    public OAuthCredentialsType? OAuth { get; init; }
}

/// <summary>Port of <c>DisabledAPIKey</c> (axonhub.graphql:318-324).</summary>
[GraphQLName("DisabledAPIKey")]
public sealed class DisabledApiKeyType
{
    public string Key { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime DisabledAt { get; init; }

    public int ErrorCode { get; init; }

    public string? Reason { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ExpiresAt { get; init; }
}

/// <summary>Port of <c>ChannelLimiterStats</c> (axonhub.graphql:809-826).</summary>
[GraphQLName("ChannelLimiterStats")]
public sealed class ChannelLimiterStatsType
{
    public int InFlight { get; init; }

    public int Waiting { get; init; }

    public int Capacity { get; init; }

    public int QueueSize { get; init; }
}

/// <summary>Port of <c>ChannelModelEntry</c> (model.graphql:204-208).</summary>
[GraphQLName("ChannelModelEntry")]
public sealed class ChannelModelEntryType
{
    public string RequestModel { get; init; } = string.Empty;

    public string ActualModel { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;
}

/// <summary>Port of <c>ChannelTypeCount</c> (axonhub.graphql:669-672).</summary>
[GraphQLName("ChannelTypeCount")]
public sealed class ChannelTypeCountType
{
    public string Type { get; init; } = string.Empty;

    public int Count { get; init; }
}

/// <summary>Port of the gqlgen <c>ProviderQuotaStatusProviderType</c> enum values.</summary>
[GraphQLName("ProviderQuotaStatusProviderType")]
public enum ProviderQuotaStatusProviderTypeValue
{
    [GraphQLName("claudecode")]
    Claudecode,

    [GraphQLName("codex")]
    Codex,

    [GraphQLName("antigravity")]
    Antigravity,

    [GraphQLName("xai_subscription")]
    XaiSubscription,

    [GraphQLName("github_copilot")]
    GithubCopilot,

    [GraphQLName("nanogpt")]
    Nanogpt,

    [GraphQLName("cline")]
    Cline,

    [GraphQLName("wafer")]
    Wafer,

    [GraphQLName("synthetic")]
    Synthetic,

    [GraphQLName("neuralwatt")]
    Neuralwatt,

    [GraphQLName("apertis")]
    Apertis,

    [GraphQLName("opencode_go")]
    OpencodeGo,

    [GraphQLName("kimi_code")]
    KimiCode,

    [GraphQLName("minimax")]
    Minimax,

    [GraphQLName("zhipu")]
    Zhipu,

    [GraphQLName("zai")]
    Zai,

    [GraphQLName("charm_hyper")]
    CharmHyper,

    [GraphQLName("zenmux")]
    Zenmux,

    [GraphQLName("commandcode")]
    Commandcode,

    [GraphQLName("ollama")]
    Ollama,
}

/// <summary>Port of the gqlgen <c>ProviderQuotaStatusStatus</c> enum values.</summary>
[GraphQLName("ProviderQuotaStatusStatus")]
public enum ProviderQuotaStatusStatusValue
{
    [GraphQLName("available")]
    Available,

    [GraphQLName("warning")]
    Warning,

    [GraphQLName("exhausted")]
    Exhausted,

    [GraphQLName("unknown")]
    Unknown,
}

/// <summary>
/// Port of gqlgen's <c>Map</c> scalar (ent.graphql:2316), the type of
/// <c>ProviderQuotaStatus.quotaData</c>. gqlgen's <c>graphql.MarshalMap</c>
/// writes <c>json.Marshal(map[string]any)</c> verbatim, so the value appears in
/// the response as a JSON object, not as a GraphQL String; the port reuses the
/// raw-JSON scalar behaviour with the schema name <c>Map</c>. The scalar is an
/// output-only type in the schema, so the inherited input coercion is never
/// exercised.
/// </summary>
public sealed class MapScalarType : JsonRawMessageType
{
    public MapScalarType()
        : base("Map")
    {
    }
}

/// <summary>
/// Port of <c>ProviderQuotaStatusWhereInput</c> (ent.graphql). The filter
/// itself never restricts <c>deleted_at</c>, matching the generated sqlgraph
/// predicates used inside a nested edge filter.
/// </summary>
[GraphQLName("ProviderQuotaStatusWhereInput")]
public sealed class ProviderQuotaStatusWhereInput
{
    public ProviderQuotaStatusWhereInput? Not { get; set; }

    public IReadOnlyList<ProviderQuotaStatusWhereInput>? And { get; set; }

    public IReadOnlyList<ProviderQuotaStatusWhereInput>? Or { get; set; }

    [GraphQLName("id")]
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLName("idNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLName("idIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLName("idNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLName("idGT")]
    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLName("idGTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLName("idLT")]
    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLName("idLTE")]
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

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID { get; set; }

    [GraphQLName("channelIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelIDNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDNotIn { get; set; }

    public ProviderQuotaStatusProviderTypeValue? ProviderType { get; set; }

    public ProviderQuotaStatusProviderTypeValue? ProviderTypeNEQ { get; set; }

    public IReadOnlyList<ProviderQuotaStatusProviderTypeValue>? ProviderTypeIn { get; set; }

    public IReadOnlyList<ProviderQuotaStatusProviderTypeValue>? ProviderTypeNotIn { get; set; }

    public ProviderQuotaStatusStatusValue? Status { get; set; }

    public ProviderQuotaStatusStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<ProviderQuotaStatusStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ProviderQuotaStatusStatusValue>? StatusNotIn { get; set; }

    [GraphQLName("nextResetAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAt { get; set; }

    [GraphQLName("nextResetAtNEQ")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAtNEQ { get; set; }

    [GraphQLName("nextResetAtIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? NextResetAtIn { get; set; }

    [GraphQLName("nextResetAtNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? NextResetAtNotIn { get; set; }

    [GraphQLName("nextResetAtGT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAtGT { get; set; }

    [GraphQLName("nextResetAtGTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAtGTE { get; set; }

    [GraphQLName("nextResetAtLT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAtLT { get; set; }

    [GraphQLName("nextResetAtLTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAtLTE { get; set; }

    [GraphQLName("nextResetAtIsNil")]
    public bool? NextResetAtIsNil { get; set; }

    [GraphQLName("nextResetAtNotNil")]
    public bool? NextResetAtNotNil { get; set; }

    public bool? Ready { get; set; }

    public bool? ReadyNEQ { get; set; }

    [GraphQLName("nextCheckAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextCheckAt { get; set; }

    [GraphQLName("nextCheckAtNEQ")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextCheckAtNEQ { get; set; }

    [GraphQLName("nextCheckAtIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? NextCheckAtIn { get; set; }

    [GraphQLName("nextCheckAtNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? NextCheckAtNotIn { get; set; }

    [GraphQLName("nextCheckAtGT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextCheckAtGT { get; set; }

    [GraphQLName("nextCheckAtGTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextCheckAtGTE { get; set; }

    [GraphQLName("nextCheckAtLT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextCheckAtLT { get; set; }

    [GraphQLName("nextCheckAtLTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextCheckAtLTE { get; set; }

    [GraphQLName("accountKey")]
    public string? AccountKey { get; set; }

    [GraphQLName("accountKeyNEQ")]
    public string? AccountKeyNEQ { get; set; }

    [GraphQLName("accountKeyIn")]
    public IReadOnlyList<string>? AccountKeyIn { get; set; }

    [GraphQLName("accountKeyNotIn")]
    public IReadOnlyList<string>? AccountKeyNotIn { get; set; }

    [GraphQLName("accountKeyGT")]
    public string? AccountKeyGT { get; set; }

    [GraphQLName("accountKeyGTE")]
    public string? AccountKeyGTE { get; set; }

    [GraphQLName("accountKeyLT")]
    public string? AccountKeyLT { get; set; }

    [GraphQLName("accountKeyLTE")]
    public string? AccountKeyLTE { get; set; }

    [GraphQLName("accountKeyContains")]
    public string? AccountKeyContains { get; set; }

    [GraphQLName("accountKeyHasPrefix")]
    public string? AccountKeyHasPrefix { get; set; }

    [GraphQLName("accountKeyHasSuffix")]
    public string? AccountKeyHasSuffix { get; set; }

    [GraphQLName("accountKeyIsNil")]
    public bool? AccountKeyIsNil { get; set; }

    [GraphQLName("accountKeyNotNil")]
    public bool? AccountKeyNotNil { get; set; }

    [GraphQLName("accountKeyEqualFold")]
    public string? AccountKeyEqualFold { get; set; }

    [GraphQLName("accountKeyContainsFold")]
    public string? AccountKeyContainsFold { get; set; }

    public bool? HasChannel { get; set; }

    public IReadOnlyList<ChannelWhereInput>? HasChannelWith { get; set; }
}

/// <summary>
/// Port of <c>ProviderQuotaStatus</c> (ent.graphql:3729-3758). The reference
/// resolver only returns a row when quota collection is enabled for the
/// provider; see <see cref="ChannelType.GetProviderQuotaStatusAsync"/>.
/// </summary>
[GraphQLName("ProviderQuotaStatus")]
public sealed class ProviderQuotaStatusType
{
    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string ChannelId { get; init; } = string.Empty;

    public ProviderQuotaStatusProviderTypeValue ProviderType { get; init; }

    public ProviderQuotaStatusStatusValue Status { get; init; }

    /// <summary>
    /// The stored <c>quota_data</c> jsonb document, emitted as a raw JSON
    /// object (gqlgen <c>Map</c>).
    /// </summary>
    [GraphQLType(typeof(MapScalarType))]
    public JsonElement QuotaData { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? NextResetAt { get; init; }

    public bool Ready { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime NextCheckAt { get; init; }

    public string? AccountKey { get; init; }

    public ChannelType Channel { get; init; } = new();
}

/// <summary>
/// Port of <c>BulkImportChannelsResult</c> (axonhub.graphql:422-428;
/// channel_bulk.go:474-481). <c>errors</c> is a nullable list: the reference's
/// nil slice marshals as null, while the non-null <c>channels</c> list
/// marshals a nil slice as [].
/// </summary>
[GraphQLName("BulkImportChannelsResult")]
public sealed class BulkImportChannelsResultType
{
    public bool Success { get; init; }

    public int Created { get; init; }

    public int Failed { get; init; }

    public IReadOnlyList<string>? Errors { get; init; }

    public IReadOnlyList<ChannelType> Channels { get; init; } = [];
}

/// <summary>Port of the <c>Channel</c> object type (ent.graphql:407-543).</summary>
[GraphQLName("Channel")]
public sealed class ChannelType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    /// <summary>Raw <c>type</c> column value used by field resolvers.</summary>
    [GraphQLIgnore]
    public string RawType { get; init; } = string.Empty;

    [GraphQLIgnore]
    public string? CredentialsJson { get; init; }

    [GraphQLIgnore]
    public string? DisabledApiKeysJson { get; init; }

    [GraphQLIgnore]
    public string? SettingsJson { get; init; }

    /// <summary>Raw <c>supported_models</c> column used by allModelEntries.</summary>
    [GraphQLIgnore]
    public string? SupportedModelsJson { get; init; }

    /// <summary>Raw <c>endpoints</c> column used by defaultEndpoints.</summary>
    [GraphQLIgnore]
    public string? EndpointsJson { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public ChannelTypeValue Type { get; init; }

    [GraphQLName("baseURL")]
    public string? BaseUrl { get; init; }

    public string Name { get; init; } = string.Empty;

    public ChannelStatusValue Status { get; init; }

    public IReadOnlyList<string> SupportedModels { get; init; } = [];

    public IReadOnlyList<string>? ManualModels { get; init; }

    public bool AutoSyncSupportedModels { get; init; }

    public string? AutoSyncModelPattern { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string DefaultTestModel { get; init; } = string.Empty;

    public ChannelPoliciesType? Policies { get; init; }

    public ChannelSettingsType? Settings { get; init; }

    public int OrderingWeight { get; init; }

    public string? ErrorMessage { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisabledAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? AutoDisableExpiresAt { get; init; }

    public string? Remark { get; init; }

    public IReadOnlyList<ChannelEndpointType>? Endpoints { get; init; }

    /// <summary>
    /// Port of channelResolver.DefaultEndpoints (axonhub.resolvers.go:34-41):
    /// the live resolver runs <c>(&amp;biz.Channel{Channel: obj}).DefaultEndpoints()</c>,
    /// which applies the stored Responses-family intent before returning the
    /// type defaults.
    /// </summary>
    [GraphQLName("defaultEndpoints")]
    public IReadOnlyList<ChannelEndpointType> GetDefaultEndpoints()
        => ChannelMapping.DefaultEndpoints(RawType, EndpointsJson, BaseUrl);

    /// <summary>Port of channelResolver.AllModelEntries.</summary>
    [GraphQLName("allModelEntries")]
    public IReadOnlyList<ChannelModelEntryType> GetAllModelEntries()
        => ChannelMapping.AllModelEntries(this);

    /// <summary>
    /// Port of the live channelResolver.Credentials
    /// (axonhub.resolvers.go:57-77): secrets are only exposed to callers
    /// holding write_channels, checked with <c>scopes.UserHasScope</c> — an
    /// API-key principal never qualifies.
    /// </summary>
    [GraphQLName("credentials")]
    public async Task<ChannelCredentialsType?> GetCredentialsAsync(
        [Service] AuthorizationScope scope,
        CancellationToken cancellationToken)
        => await scope
            .HasUserScopeAsync(AuthorizationScope.WriteChannels, cancellationToken)
            .ConfigureAwait(false)
            ? ChannelMapping.ToCredentials(CredentialsJson, RawType)
            : null;

    /// <summary>Port of channelResolver.DisabledAPIKeys (axonhub.resolvers.go:81-92).</summary>
    [GraphQLName("disabledAPIKeys")]
    public async Task<IReadOnlyList<DisabledApiKeyType>?> GetDisabledApiKeysAsync(
        [Service] AuthorizationScope scope,
        CancellationToken cancellationToken)
        => await scope
            .HasUserScopeAsync(AuthorizationScope.WriteChannels, cancellationToken)
            .ConfigureAwait(false)
            ? ChannelMapping.ToDisabledApiKeys(DisabledApiKeysJson)
            : null;

    /// <summary>
    /// Port of the generated edge accessor (gql_edge.go:131-141):
    /// <c>QueryChannelModelPrices().All(ctx)</c> with the ChannelModelPrice
    /// query policy applied. The reference leaves the row order unspecified;
    /// the port's store orders by id (C048).
    /// </summary>
    [GraphQLName("channelModelPrices")]
    public async Task<IReadOnlyList<ChannelModelPriceType>> GetChannelModelPricesAsync(
        [Service] ChannelPrivacy privacy,
        [Service] IChannelModelPriceStore prices,
        CancellationToken cancellationToken)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var rows = await prices.ListByChannelAsync(DbId, cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(PriceMapping.ToGraphQL)];
    }

    /// <summary>Port of channelResolver.LiveLimiterStats.</summary>
    [GraphQLName("liveLimiterStats")]
    public ChannelLimiterStatsType? GetLiveLimiterStats(
        [Service] ChannelLimiterManager limiters)
        => ChannelMapping.LiveLimiterStats(SettingsJson, DbId, limiters);

    /// <summary>
    /// Port of channelResolver.ProviderQuotaStatus (ent.resolvers.go:98-142):
    /// a missing row is null, a retired provider type is null, and a row whose
    /// provider has quota collection disabled is null. Every other failure is
    /// surfaced with the reference's wrapper.
    /// </summary>
    [GraphQLName("providerQuotaStatus")]
    public async Task<ProviderQuotaStatusType?> GetProviderQuotaStatusAsync(
        [Service] IProviderQuotaStatusStore quotaStatuses,
        [Service] IProviderQuotaCollectionSettingsService quotaSettings,
        CancellationToken cancellationToken)
    {
        var status = await quotaStatuses.GetByChannelIdAsync(DbId, cancellationToken)
            .ConfigureAwait(false);
        if (status is null)
        {
            return null;
        }

        if (status.ProviderType.Length == 0)
        {
            // The reference re-selects provider_type when the loaded edge
            // carries the enum zero value; the port's row query always selects
            // the column, so the reload can only observe the same row (or a
            // concurrent delete) and its provider type stays empty.
            ProviderQuotaStatusRecord? reloaded;
            try
            {
                reloaded = await quotaStatuses.GetByChannelIdAsync(DbId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "failed to load provider quota type: " + ex.Message,
                    ex);
            }

            if (reloaded is null)
            {
                return null;
            }

            status.ProviderType = reloaded.ProviderType;
        }

        // Rows created before a provider type was retired (e.g. 'wafer') stay
        // in the database until purge runs; surface them as "no status" so one
        // orphan row cannot break the quota view for every channel.
        if (!ProviderQuotaCollectionSettingsService.IsSupportedProviderQuotaType(
                status.ProviderType))
        {
            return null;
        }

        bool enabled;
        try
        {
            enabled = await quotaSettings.IsEnabledAsync(status.ProviderType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to read provider quota collection settings: " + ex.Message,
                ex);
        }

        return enabled ? ChannelMapping.ToGraphQL(status, this) : null;
    }
}

/// <summary>Port of <c>ChannelEdge</c> (ent.graphql:564-573).</summary>
[GraphQLName("ChannelEdge")]
public sealed class ChannelEdgeType
{
    public ChannelType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>ChannelConnection</c> (ent.graphql:547-562).</summary>
[GraphQLName("ChannelConnection")]
public sealed class ChannelConnectionType
{
    public IReadOnlyList<ChannelEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}
