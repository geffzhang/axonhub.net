using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using HotChocolate;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the <c>CodexCompatibilitySettings</c> type
/// (system.graphql:409-412). Go keeps <c>ChannelID *int</c>; the wire type is
/// <c>Int</c>, so the service's 64-bit value is clamped like every other id on
/// this surface.
/// </summary>
[GraphQLName("CodexCompatibilitySettings")]
public sealed class CodexCompatibilitySettingsType
{
    public bool Enabled { get; init; }

    [GraphQLName("channelID")]
    public int? ChannelID { get; init; }
}

/// <summary>
/// Port of the <c>UpdateCodexCompatibilitySettingsInput</c> input
/// (system.graphql:414-417). gqlgen decodes it straight into
/// biz.CodexCompatibilitySettings, so a missing channelID is nil.
/// </summary>
public sealed class UpdateCodexCompatibilitySettingsInput
{
    public bool Enabled { get; set; }

    [GraphQLName("channelID")]
    public int? ChannelID { get; set; }
}

/// <summary>
/// Port of the <c>CodexCatalogChannel</c> type (system.graphql:419-423): the
/// id/name/status slice of every channel whose type is <c>codex</c>.
/// </summary>
[GraphQLName("CodexCatalogChannel")]
public sealed class CodexCatalogChannelType
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public ChannelStatusValue Status { get; init; }
}

/// <summary>
/// Port of the <c>CodexCatalogTestResult</c> type (system.graphql:425-430).
/// <see cref="UpstreamStatus"/> and <see cref="Error"/> are nullable, matching
/// the Go pointers and their omitempty tags.
/// </summary>
[GraphQLName("CodexCatalogTestResult")]
public sealed class CodexCatalogTestResultType
{
    public bool Success { get; init; }

    public int ModelCount { get; init; }

    [GraphQLName("upstreamStatus")]
    public int? UpstreamStatus { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Maps the Codex compatibility settings and catalog rows between the
/// biz-shaped domain and the GraphQL types. The resolvers pass values through
/// unchanged (system.resolvers.go:26-56, 470-497).
/// </summary>
public static class CodexCompatibilityMapping
{
    public static CodexCompatibilitySettingsType ToGraphQL(
        CodexCompatibilitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new CodexCompatibilitySettingsType
        {
            Enabled = settings.Enabled,
            ChannelID = ToNullableInt32(settings.ChannelID),
        };
    }

    public static CodexCompatibilitySettings ToDomain(
        UpdateCodexCompatibilitySettingsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new CodexCompatibilitySettings
        {
            Enabled = input.Enabled,
            ChannelID = input.ChannelID,
        };
    }

    public static CodexCatalogChannelType ToGraphQL(ChannelRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new CodexCatalogChannelType
        {
            Id = ToInt32(record.Id),
            Name = record.Name,
            Status = ChannelMapping.ToChannelStatusValue(record.Status),
        };
    }

    /// <summary>
    /// gqlgen's Int marshal clamps out-of-range values instead of failing
    /// (ChannelMapping.ToInt32); the catalog shares that behaviour.
    /// </summary>
    private static int ToInt32(long value)
        => value > int.MaxValue
            ? int.MaxValue
            : value < int.MinValue ? int.MinValue : (int)value;

    private static int? ToNullableInt32(long? value)
        => value is null ? null : ToInt32(value.Value);
}
