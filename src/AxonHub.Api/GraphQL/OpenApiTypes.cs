using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the OpenAPI schema's distinct <c>DecimalInput</c> scalar
/// (internal/server/gql/openapi/openapi.graphql:2). It accepts the same input
/// literals as the admin <c>Decimal</c> scalar but is a separate named type.
/// </summary>
public sealed class GoDecimalInputType() : GoDecimalScalarBase("DecimalInput");

/// <summary>
/// Port of the OpenAPI <c>APIKey</c> object type (openapi.graphql:5-11): the
/// projection carries only id/key/name/scopes/profiles, unlike the rich admin
/// APIKey type.
/// </summary>
[GraphQLName("APIKey")]
public sealed class OpenApiApiKeyType
{
    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string>? Scopes { get; init; }

    public OpenApiApiKeyProfilesType? Profiles { get; init; }
}

/// <summary>Port of OpenAPI <c>APIKeyProfiles</c> (openapi.graphql:13-16).</summary>
[GraphQLName("APIKeyProfiles")]
public sealed class OpenApiApiKeyProfilesType
{
    public string ActiveProfile { get; init; } = string.Empty;

    public IReadOnlyList<OpenApiApiKeyProfileType>? Profiles { get; init; }
}

/// <summary>
/// Port of OpenAPI <c>APIKeyProfile</c> (openapi.graphql:18-28). The OpenAPI
/// schema deliberately omits the admin-only templateID/templateName fields.
/// </summary>
[GraphQLName("APIKeyProfile")]
public sealed class OpenApiApiKeyProfileType
{
    public string Name { get; init; } = string.Empty;

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

/// <summary>
/// Port of OpenAPI <c>UpdateAPIKeyProfilesInput</c> (openapi.graphql:80-83).
/// The CLR name differs from the admin input because both schemas define the
/// same GraphQL name with different CLR types.
/// </summary>
[GraphQLName("UpdateAPIKeyProfilesInput")]
public sealed class OpenApiUpdateApiKeyProfilesInput
{
    public string ActiveProfile { get; set; } = string.Empty;

    /// <summary>
    /// SDL <c>profiles: [APIKeyProfileInput!]!</c>: the non-nullable CLR list
    /// makes HotChocolate infer both levels as non-null.
    /// </summary>
    public IReadOnlyList<OpenApiApiKeyProfileInput> Profiles { get; set; } = [];
}

/// <summary>Port of OpenAPI <c>APIKeyProfileInput</c> (openapi.graphql:85-95).</summary>
[GraphQLName("APIKeyProfileInput")]
public sealed class OpenApiApiKeyProfileInput
{
    public string Name { get; set; } = string.Empty;

    public IReadOnlyList<ModelMappingInput>? ModelMappings { get; set; }

    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IntType>>))]
    public IReadOnlyList<int>? ChannelIDs { get; set; }

    public IReadOnlyList<string>? ChannelTags { get; set; }

    [GraphQLType(typeof(ChannelTagsMatchModeType))]
    public string? ChannelTagsMatchMode { get; set; }

    [GraphQLName("modelIDs")]
    public IReadOnlyList<string>? ModelIDs { get; set; }

    public OpenApiApiKeyQuotaInput? Quota { get; set; }

    public string? LoadBalanceStrategy { get; set; }

    public string? TraceStickyMode { get; set; }
}

/// <summary>
/// Port of OpenAPI <c>APIKeyQuotaInput</c> (openapi.graphql:97-102). The fields
/// are identical to the admin input except that <c>cost</c> uses the
/// <c>DecimalInput</c> scalar; the period input types are shared.
/// </summary>
[GraphQLName("APIKeyQuotaInput")]
public sealed class OpenApiApiKeyQuotaInput
{
    public int? Requests { get; set; }

    public int? TotalTokens { get; set; }

    [GraphQLType(typeof(GoDecimalInputType))]
    public decimal? Cost { get; set; }

    public ApiKeyQuotaPeriodInput Period { get; set; } = new();
}

/// <summary>
/// Port of OpenAPI <c>LoadApiKeyProfileTemplateInput</c> (openapi.graphql:124-137).
/// All four fields are nullable so each pair's exactly-one-of rule is enforced
/// by the service, not by GraphQL validation.
/// </summary>
[GraphQLName("LoadApiKeyProfileTemplateInput")]
public sealed class OpenApiLoadApiKeyProfileTemplateInput
{
    [GraphQLName("templateID")]
    [GraphQLType(typeof(IdType))]
    public string? TemplateID { get; set; }

    public string? TemplateName { get; set; }

    [GraphQLName("apiKeyID")]
    [GraphQLType(typeof(IdType))]
    public string? ApiKeyID { get; set; }

    public string? ApiKeyName { get; set; }
}

/// <summary>Domain ↔ OpenAPI GraphQL mapping.</summary>
public static class OpenApiMapping
{
    /// <summary>Port of openapi helper.toOpenAPIAPIKey (helper.go:57-69).</summary>
    public static OpenApiApiKeyType ToOpenApi(ApiKeyRecord record) => new()
    {
        Id = new GidValue("APIKey", record.Id).ToString(),
        Key = record.Key,
        Name = record.Name,
        Scopes = ManagementMapping.ParseScopes(record.Scopes),
        Profiles = record.Profiles is null
            ? null
            : ToOpenApi(ApiKeyProfilesCodec.Deserialize(record.Profiles)),
    };

    public static OpenApiApiKeyProfilesType ToOpenApi(ApiKeyProfilesModel model) => new()
    {
        ActiveProfile = model.ActiveProfile,
        Profiles = model.Profiles?.Select(ToOpenApi).ToList(),
    };

    public static OpenApiApiKeyProfileType ToOpenApi(ApiKeyProfileModel profile) => new()
    {
        Name = profile.Name,
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
        ChannelTagsMatchMode = profile.ChannelTagsMatchMode,
        ModelIDs = profile.ModelIDs,
        Quota = profile.Quota is null ? null : ApiKeyMapping.ToGraphQL(profile.Quota),
        LoadBalanceStrategy = profile.LoadBalanceStrategy,
        TraceStickyMode = profile.TraceStickyMode,
    };

    public static ApiKeyProfilesModel ToModel(OpenApiUpdateApiKeyProfilesInput input) => new()
    {
        ActiveProfile = input.ActiveProfile,
        Profiles = input.Profiles.Select(ToModel).ToList(),
    };

    public static ApiKeyProfileModel ToModel(OpenApiApiKeyProfileInput input) => new()
    {
        Name = input.Name,
        TemplateId = null,
        TemplateName = string.Empty,
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

    public static ApiKeyQuotaModel ToModel(OpenApiApiKeyQuotaInput input)
        => ApiKeyMapping.ToModel(new ApiKeyQuotaInput
        {
            Requests = input.Requests,
            TotalTokens = input.TotalTokens,
            Cost = input.Cost,
            Period = input.Period,
        });

    /// <summary>
    /// Port of openapi helper.guidID (helper.go:40-50): a nil GUID passes
    /// through as a nil id; a wrong-typed GUID is rejected before any lookup.
    /// </summary>
    public static long? ResolveGuidId(string? value, string expectedType)
    {
        if (value is null)
        {
            return null;
        }

        var guid = GidValue.Parse(value);
        if (!string.Equals(guid.Type, expectedType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"invalid id: expected a {expectedType} GUID, got {guid.Type}");
        }

        return guid.Id;
    }
}
