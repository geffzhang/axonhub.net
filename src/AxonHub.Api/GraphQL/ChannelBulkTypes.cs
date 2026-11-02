using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the gqlgen <c>BulkAutoDisableAction</c> enum
/// (axonhub.graphql:460-464, live 527-531). The enum constrains the action
/// before the resolver runs, so the reference's "unsupported auto-disable
/// action" branch is unreachable from GraphQL.
/// </summary>
[GraphQLName("BulkAutoDisableAction")]
public enum BulkAutoDisableActionValue
{
    [GraphQLName("write_rules")]
    WriteRules,

    [GraphQLName("inherit")]
    Inherit,

    [GraphQLName("off")]
    Off,
}

/// <summary>Port of <c>ChannelOrderingItem</c> (axonhub.graphql:445-448).</summary>
[GraphQLName("ChannelOrderingItem")]
public sealed class ChannelOrderingItemInput
{
    [GraphQLType(typeof(IdType))]
    public string Id { get; set; } = string.Empty;

    public int OrderingWeight { get; set; }
}

/// <summary>
/// Port of <c>BulkUpdateChannelOrderingInput</c> (axonhub.graphql:450-452).
/// </summary>
public sealed class BulkUpdateChannelOrderingInput
{
    public IReadOnlyList<ChannelOrderingItemInput> Channels { get; set; } = [];
}

/// <summary>
/// Port of <c>BulkUpdateChannelOrderingResult</c> (axonhub.graphql:454-458).
/// </summary>
public sealed class BulkUpdateChannelOrderingResult
{
    public bool Success { get; init; }

    public int Updated { get; init; }

    public IReadOnlyList<ChannelType> Channels { get; init; } = [];
}

/// <summary>
/// Port of <c>BulkUpdateChannelAutoDisableInput</c> (axonhub.graphql:466-471).
/// </summary>
public sealed class BulkUpdateChannelAutoDisableInput
{
    [GraphQLName("channelIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string> ChannelIDs { get; set; } = [];

    public BulkAutoDisableActionValue Action { get; set; }

    public IReadOnlyList<ApiKeyAutoDisableRuleInput>? Rules { get; set; }
}

/// <summary>
/// Port of <c>BulkUpdateChannelAutoDisablePayload</c> (axonhub.graphql:472-477).
/// </summary>
public sealed class BulkUpdateChannelAutoDisablePayload
{
    public bool Success { get; init; }

    public int Updated { get; init; }

    public IReadOnlyList<ChannelType> Channels { get; init; } = [];
}
