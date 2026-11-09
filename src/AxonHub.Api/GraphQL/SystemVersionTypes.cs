using AxonHub.Api.Settings;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the <c>SystemVersion</c> type (system.graphql:443-450).</summary>
[GraphQLName("SystemVersion")]
public sealed class SystemVersionType
{
    public string Version { get; init; } = string.Empty;

    public string Commit { get; init; } = string.Empty;

    public string BuildTime { get; init; } = string.Empty;

    public string GoVersion { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Uptime { get; init; } = string.Empty;
}

/// <summary>Port of the <c>VersionCheck</c> type (system.graphql:452-457).</summary>
[GraphQLName("VersionCheck")]
public sealed class VersionCheckType
{
    public string CurrentVersion { get; init; } = string.Empty;

    public string LatestVersion { get; init; } = string.Empty;

    public bool HasUpdate { get; init; }

    public string ReleaseUrl { get; init; } = string.Empty;
}

/// <summary>Domain ↔ GraphQL mapping for the version surface.</summary>
public static class SystemVersionMapping
{
    public static SystemVersionType ToGraphQL(SystemBuildInfo info) => new()
    {
        Version = info.Version,
        Commit = info.Commit,
        BuildTime = info.BuildTime,
        GoVersion = info.GoVersion,
        Platform = info.Platform,
        Uptime = info.Uptime,
    };

    public static VersionCheckType ToGraphQL(VersionCheckResult result) => new()
    {
        CurrentVersion = result.CurrentVersion,
        LatestVersion = result.LatestVersion,
        HasUpdate = result.HasUpdate,
        ReleaseUrl = result.ReleaseUrl,
    };
}
