using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the OpenAPI <c>Query</c> root
/// (internal/server/gql/openapi/openapi.graphql:149-164,
/// openapi.resolvers.go:84-124).
/// </summary>
public sealed class OpenApiQuery
{
    [GraphQLName("apiKey")]
    public async Task<OpenApiApiKeyType> ApiKeyAsync(
        [GraphQLType(typeof(IdType))] string? id,
        string? key,
        string? name,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        var keyId = OpenApiMapping.ResolveGuidId(id, "APIKey");
        var apiKey = await apiKeys
            .GetForReadAsync(keyId, key, name, cancellationToken)
            .ConfigureAwait(false);
        return OpenApiMapping.ToOpenApi(apiKey);
    }

    [GraphQLName("apiKeyQuotaUsages")]
    public async Task<IReadOnlyList<ApiKeyProfileQuotaUsageType>> ApiKeyQuotaUsagesAsync(
        [GraphQLName("apiKeyId")] [GraphQLType(typeof(IdType))] string? apiKeyId,
        string? key,
        string? name,
        [Service] ApiKeyManagementService apiKeys,
        [Service] IApiKeyProfileQuotaReader quotas,
        CancellationToken cancellationToken)
    {
        var keyId = OpenApiMapping.ResolveGuidId(apiKeyId, "APIKey");
        var apiKey = await apiKeys
            .GetForReadAsync(keyId, key, name, cancellationToken)
            .ConfigureAwait(false);

        // The reference returns the quota service error unwrapped.
        var usages = await quotas
            .ProfileQuotaUsagesAsync(apiKey, cancellationToken)
            .ConfigureAwait(false);
        return usages.Select(ApiKeyMapping.ToGraphQL).ToList();
    }
}

/// <summary>
/// Port of the OpenAPI <c>Mutation</c> root
/// (openapi.graphql:139-147, openapi.resolvers.go:16-82).
/// </summary>
public sealed class OpenApiMutation
{
    [GraphQLName("createLLMAPIKey")]
    public async Task<OpenApiApiKeyType> CreateLlmApiKeyAsync(
        string name,
        [Service] IManagementIdentity identity,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        var owner = identity.GetCurrentApiKey()
            ?? throw new InvalidOperationException("api key not found in context");

        var created = await apiKeys
            .CreateLlmApiKeyAsync(owner, name, cancellationToken)
            .ConfigureAwait(false);
        return OpenApiMapping.ToOpenApi(created);
    }

    [GraphQLName("updateAPIKeyProfiles")]
    public async Task<OpenApiApiKeyType> UpdateApiKeyProfilesAsync(
        [GraphQLType(typeof(IdType))] string? id,
        string? name,
        OpenApiUpdateApiKeyProfilesInput input,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        // The admin UI's Zod schema rejects null modelMappings, so the OpenAPI
        // resolver coerces a nil list to [] before saving (openapi.resolvers.go:38-42).
        foreach (var profile in input.Profiles)
        {
            profile.ModelMappings ??= [];
        }

        var keyId = OpenApiMapping.ResolveGuidId(id, "APIKey");
        var target = await apiKeys
            .GetForReadAsync(keyId, null, name, cancellationToken)
            .ConfigureAwait(false);

        var updated = await apiKeys
            .UpdateProfilesAsync(
                target.Id,
                OpenApiMapping.ToModel(input),
                cancellationToken)
            .ConfigureAwait(false);
        return OpenApiMapping.ToOpenApi(updated);
    }

    [GraphQLName("loadApiKeyProfileTemplate")]
    public async Task<OpenApiApiKeyType> LoadApiKeyProfileTemplateAsync(
        OpenApiLoadApiKeyProfileTemplateInput input,
        [Service] ApiKeyProfileTemplateService templates,
        [Service] ApiKeyManagementService apiKeys,
        CancellationToken cancellationToken)
    {
        // Resolve the template first, then the target key, each through its
        // privacy-gated exactly-one-of read path (openapi.resolvers.go:66-79).
        var templateId = OpenApiMapping.ResolveGuidId(
            input.TemplateID,
            "APIKeyProfileTemplate");
        var template = await templates
            .GetForReadAsync(templateId, input.TemplateName, cancellationToken)
            .ConfigureAwait(false);

        var keyId = OpenApiMapping.ResolveGuidId(input.ApiKeyID, "APIKey");
        var target = await apiKeys
            .GetForReadAsync(keyId, null, input.ApiKeyName, cancellationToken)
            .ConfigureAwait(false);

        var loaded = await templates
            .LoadAsync(template.Id, target.Id, cancellationToken)
            .ConfigureAwait(false);
        return OpenApiMapping.ToOpenApi(loaded);
    }
}
