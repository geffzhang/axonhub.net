using HotChocolate;
using AxonHub.Api.Scopes;
using AxonHub.Api.Settings;

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

    /// <summary>Port of queryResolver.SystemStatus (system.resolvers.go:479-488).</summary>
    [GraphQLName("systemStatus")]
    public async Task<SystemStatusType> SystemStatusAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return SystemSettingsMapping.ToGraphQL(
                await settings.IsInitializedAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to check initialization status: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of queryResolver.RetryPolicy (system.resolvers.go:519-522).</summary>
    [GraphQLName("retryPolicy")]
    public async Task<RetryPolicyType> RetryPolicyAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
        => SystemSettingsMapping.ToGraphQL(
            await settings.GetRetryPolicyAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Port of queryResolver.SystemModelSettings (system.resolvers.go:529-537).</summary>
    [GraphQLName("systemModelSettings")]
    public async Task<SystemModelSettingsType> SystemModelSettingsAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
    {
        try
        {
            return SystemSettingsMapping.ToGraphQL(
                await settings.GetModelSettingsAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get system model settings: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of queryResolver.SecuritySettings (system.resolvers.go:639-642).</summary>
    [GraphQLName("securitySettings")]
    public async Task<SecuritySettingsType> SecuritySettingsAsync(
        [Service] ISystemSettingsAdminService settings,
        CancellationToken cancellationToken)
        => SystemSettingsMapping.ToGraphQL(
            await settings.GetSecuritySettingsAsync(cancellationToken).ConfigureAwait(false));
}
