using System.Text.Json;
using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Catalog;
using AxonHub.Api.Settings;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the <c>CatalogSettings</c> type (system.graphql:399-402).</summary>
[GraphQLName("CatalogSettings")]
public sealed class CatalogSettingsType
{
    [GraphQLName("upstreamURL")]
    public string UpstreamUrl { get; init; } = string.Empty;

    public int RefreshSeconds { get; init; }
}

/// <summary>Port of <c>UpdateCatalogSettingsInput</c> (system.graphql:404-407).</summary>
public sealed class UpdateCatalogSettingsInput
{
    [GraphQLName("upstreamURL")]
    public string? UpstreamUrl { get; set; }

    public int? RefreshSeconds { get; set; }
}

/// <summary>Port of the <c>ProvidersCatalog</c> type (system.graphql:392-397).</summary>
[GraphQLName("ProvidersCatalog")]
public sealed class ProvidersCatalogType
{
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement Data { get; init; }

    /// <summary>The SDL leaves the Time nullable; the service always sets it.</summary>
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? FetchedAt { get; init; }

    public string Source { get; init; } = string.Empty;

    public bool Filtered { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the catalog slice.</summary>
public static class CatalogMapping
{
    public static CatalogSettingsType ToGraphQL(CatalogSettings settings) => new()
    {
        UpstreamUrl = settings.UpstreamUrl,
        RefreshSeconds = settings.RefreshSeconds,
    };

    /// <summary>Port of <c>providersCatalogFromSnapshot</c> (catalog_helpers.go).</summary>
    public static ProvidersCatalogType ToGraphQL(CatalogSnapshot snapshot) => new()
    {
        Data = snapshot.Data,
        FetchedAt = snapshot.FetchedAt,
        Source = snapshot.Source,
        Filtered = snapshot.Filtered,
    };
}
