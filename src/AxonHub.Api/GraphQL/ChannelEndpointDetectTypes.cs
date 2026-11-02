using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of <c>DetectChannelEndpointsInput</c> (axonhub.graphql:120-123):
/// channelID is a required ID, model is an optional probe pin.
/// </summary>
public sealed class DetectChannelEndpointsInput
{
    [GraphQLName("channelID")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelID { get; set; } = string.Empty;

    public string? Model { get; set; }
}

/// <summary>
/// Port of <c>DetectedChannelEndpoint</c> (axonhub.graphql:125-130). The SDL
/// declares statusCode nullable while the Go struct carries a plain int, so the
/// wire value is 0 (not null) for probes that never saw a response.
/// </summary>
[GraphQLName("DetectedChannelEndpoint")]
public sealed class DetectedChannelEndpointType
{
    public string ApiFormat { get; init; } = string.Empty;

    public bool Supported { get; init; }

    public int? StatusCode { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>Port of <c>DetectChannelEndpointsPayload</c> (axonhub.graphql:132-134).</summary>
public sealed class DetectChannelEndpointsPayload
{
    public IReadOnlyList<DetectedChannelEndpointType> Endpoints { get; init; } = [];
}
