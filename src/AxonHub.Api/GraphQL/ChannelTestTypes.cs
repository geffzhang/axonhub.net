using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of <c>TestChannelInput</c> (axonhub.graphql): channelID is a required
/// ID, modelID an optional override and proxy an optional per-test proxy.
/// </summary>
/// <remarks>
/// The proxy is the tester's per-request override (axonhub.resolvers.go:344):
/// it wins over the channel's persisted <c>settings.proxy</c>.
/// </remarks>
public sealed class TestChannelInput
{
    [GraphQLName("channelID")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelID { get; set; } = string.Empty;

    [GraphQLName("modelID")]
    public string? ModelID { get; set; }

    public ProxyConfigInput? Proxy { get; set; }
}

/// <summary>Port of <c>TestChannelPayload</c> (axonhub.graphql).</summary>
[GraphQLName("TestChannelPayload")]
public sealed class TestChannelPayloadType
{
    public double Latency { get; init; }

    public bool Success { get; init; }

    public string? Message { get; init; }

    public string? Error { get; init; }
}

/// <summary>Port of <c>TestAPIKeyResult</c> (axonhub.graphql).</summary>
[GraphQLName("TestAPIKeyResult")]
public sealed class TestApiKeyResultType
{
    [GraphQLName("keyPrefix")]
    public string KeyPrefix { get; init; } = string.Empty;

    public bool Success { get; init; }

    public double Latency { get; init; }

    public string? Error { get; init; }

    public bool Disabled { get; init; }
}

/// <summary>Port of <c>TestChannelAPIKeysPayload</c> (axonhub.graphql).</summary>
[GraphQLName("TestChannelAPIKeysPayload")]
public sealed class TestChannelApiKeysPayloadType
{
    [GraphQLName("channelID")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string ChannelID { get; init; } = string.Empty;

    public int Total { get; init; }

    public int SuccessCount { get; init; }

    public int FailedCount { get; init; }

    public IReadOnlyList<TestApiKeyResultType> Results { get; init; } = [];
}
