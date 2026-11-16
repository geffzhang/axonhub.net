using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of <c>SyncChannelModelsPayload</c> (axonhub.graphql:67-71).</summary>
[GraphQLName("SyncChannelModelsPayload")]
public sealed class SyncChannelModelsPayloadType
{
    [GraphQLName("channelID")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelID { get; init; } = string.Empty;

    public IReadOnlyList<string> SupportedModels { get; init; } = [];

    public IReadOnlyList<string> ManualModels { get; init; } = [];
}
