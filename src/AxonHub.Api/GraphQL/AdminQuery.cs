using AxonHub.Api.Scopes;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// The GraphQL <c>ScopeInfo</c> type (internal/server/gql/scopes.graphql:3-7).
/// HotChocolate's default naming convention maps the properties to
/// <c>scope</c>, <c>description</c> and <c>levels</c>.
/// </summary>
public sealed record ScopeInfo(
    string Scope,
    string Description,
    IReadOnlyList<string> Levels);

/// <summary>
/// Port of queryResolver.AllScopes
/// (internal/server/gql/scopes.resolvers.go:15-42).
/// </summary>
/// <remarks>
/// The SDL is <c>allScopes(level: String): [ScopeInfo!]!</c>
/// (internal/server/gql/scopes.graphql:10). The resolver passes a non-nil level
/// through as-is (no validation), so an unknown value yields an empty list.
/// </remarks>
public sealed class AdminQuery
{
    /// <summary>Port of queryResolver.AllScopes.</summary>
    public IReadOnlyList<ScopeInfo> AllScopes(string? level)
        => [.. ScopeCatalog.AllScopes(level).Select(static scope => new ScopeInfo(
            scope.Slug,
            scope.Description,
            scope.Levels))];
}
