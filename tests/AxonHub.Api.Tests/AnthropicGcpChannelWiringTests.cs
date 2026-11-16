using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the anthropic_gcp branch of the channel transformer switch
/// (channel_llm.go:1069-1085): the channel is keyless, reads its
/// <c>gcp</c> credentials object, and wraps the Anthropic outbound in the
/// Vertex transformer.
/// </summary>
public sealed class AnthropicGcpChannelWiringTests
{
    private const string Model = "claude-sonnet-4-20250514";

    private const string AuthorizedUserJson =
        """{"type":"authorized_user","client_id":"cid","client_secret":"secret","refresh_token":"rt"}""";

    [Fact]
    public void AnthropicGcpChannelBuildsTheVertexTransformerWithoutAnApiKey()
    {
        var transformer = Create(Credentials());

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);
        Assert.IsType<VertexTransformer>(transformer);

        var httpRequest = transformer.TransformRequest(Request());

        // The outbound leaves BaseUrl empty for Vertex; the executor prepends
        // the regional endpoint and rewrites it to the rawPredict path.
        Assert.Equal(
            "/v1/projects/proj/locations/us-central1/publishers/anthropic/models/" + Model + ":rawPredict",
            httpRequest.Url);
        Assert.Equal("vertex-2023-10-16", HttpClientUtils.HeaderGet(httpRequest.Headers, "Anthropic-Version"));
        Assert.Null(httpRequest.Auth);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"sk-ignored"}""")]
    public void AnthropicGcpChannelRequiresTheGcpCredentials(string credentials)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Create(credentials));

        // The key check exempts this channel type, so a missing gcp object is
        // reported by its own branch rather than as a missing api key.
        Assert.Equal("GCP credentials are required for anthropic_vertex channel", error.Message);
    }

    [Fact]
    public void AnthropicGcpChannelWrapsTransformerConstructionErrors()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Create(Credentials(jsonData: string.Empty)));

        Assert.Equal(
            "failed to create outbound transformer: failed to create vertex transformer: failed to parse credentials: unexpected end of JSON input",
            error.Message);
    }

    private static IOutboundTransformer Create(string credentials)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = 61,
            ChannelName = "anthropic-gcp-wiring",
            ActualModel = Model,
            Type = ChannelOutboundFactory.AnthropicGcpChannelType,
            Credentials = credentials,
        };

        return ChannelOutboundFactory.Create(candidate, Request(), DateTimeOffset.UnixEpoch);
    }

    private static LlmRequest Request()
        => new()
        {
            Model = Model,
            MaxTokens = 1024,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static string Credentials(
        string region = "us-central1",
        string projectId = "proj",
        string jsonData = AuthorizedUserJson)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["gcp"] = new Dictionary<string, string>
            {
                ["region"] = region,
                // The Go struct tag is projectID; the port folds ASCII.
                ["projectID"] = projectId,
                ["jsonData"] = jsonData,
            },
        });
}
