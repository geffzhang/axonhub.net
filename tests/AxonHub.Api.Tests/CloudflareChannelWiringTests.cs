using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer.Cloudflare;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Cloudflare Workers AI channel wiring (live #2674): the primary
/// outbound is the OpenAI-compatible surface, while a user-configured
/// typesafe/systemone endpoint is served by the native /run/{model} outbound.
/// </summary>
public sealed class CloudflareChannelWiringTests
{
    private const string BaseUrl =
        "https://api.cloudflare.com/client/v4/accounts/account-id/ai";

    [Fact]
    public void ChatUsesThePlainOpenAiOutbound()
    {
        var transformer = ChannelOutboundFactory.Create(
            Candidate(),
            ChatRequest(),
            DateTimeOffset.UnixEpoch);

        var openAi = Assert.IsType<OpenAiOutboundTransformer>(transformer);
        var request = openAi.TransformRequest(ChatRequest());

        Assert.Equal(BaseUrl + "/v1/chat/completions", request.Url);
        Assert.Equal("provider-key", request.Auth?.ApiKey);
    }

    [Fact]
    public void SystemOneEndpointUsesTheWorkersAiOutbound()
    {
        var candidate = Candidate(
            """[{"api_format":"typesafe/systemone","path":"/run"}]""");
        candidate.ApiFormat = ApiFormats.TypeSafeSystemOne;

        var transformer = Assert.IsType<CloudflareSystemOneOutboundTransformer>(
            ChannelOutboundFactory.Create(
                candidate,
                SystemOneRequest(),
                DateTimeOffset.UnixEpoch));

        var request = transformer.TransformRequest(SystemOneRequest());

        // The constructor strips a trailing /v1; the endpoint path is the route
        // prefix and the model id is appended.
        Assert.Equal(BaseUrl + "/run/@cf/cloudflare/clef", request.Url);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("clef", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public void PrimaryApiFormatIsChatForCloudflare()
    {
        Assert.Equal(
            ApiFormats.OpenAIChatCompletion,
            ChannelOutboundFactory.PrimaryApiFormat(ChannelOutboundFactory.CloudflareChannelType));
    }

    [Theory]
    [InlineData("cloudflare")]
    [InlineData("openai")]
    [InlineData("typesafe")]
    public void SystemOneEndpointIsAcceptedForCloudflareOpenAiAndTypesafe(string type)
    {
        var error = ChannelValidation.ValidateEndpointsForChannelType(
            type,
            [new ChannelEndpointDraft { ApiFormat = ApiFormats.TypeSafeSystemOne }],
            BaseUrl);

        Assert.Null(error);
    }

    [Fact]
    public void SystemOneEndpointIsRejectedForOtherChannelTypes()
    {
        var error = ChannelValidation.ValidateEndpointsForChannelType(
            "groq",
            [new ChannelEndpointDraft { ApiFormat = ApiFormats.TypeSafeSystemOne }],
            BaseUrl);

        Assert.Equal(
            "api_format \"typesafe/systemone\" is only supported by TypeSafe, OpenAI and Cloudflare channel types",
            error);
    }

    private static ChannelCandidate Candidate(string? endpoints = null) => new()
    {
        ChannelId = 1301,
        ChannelName = "cloudflare-wiring",
        ActualModel = "@cf/cloudflare/clef",
        Type = ChannelOutboundFactory.CloudflareChannelType,
        BaseUrl = BaseUrl,
        Credentials = """{"apiKey":"provider-key"}""",
        Endpoints = endpoints,
    };

    private static LlmRequest ChatRequest() => new()
    {
        Model = "cloudflare-model",
        ApiFormat = ApiFormats.OpenAIChatCompletion,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    private static LlmRequest SystemOneRequest() => new()
    {
        Model = "@cf/cloudflare/clef",
        RequestType = RequestTypes.SystemOne,
        ApiFormat = ApiFormats.TypeSafeSystemOne,
        SystemOne = new SystemOneRequest
        {
            State = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["ticket"] = "Refund request",
            }),
            Questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal)
            {
                ["check"] = new()
                {
                    Type = "noul",
                    Instructions = JsonSerializer.SerializeToElement("Is this a refund?"),
                },
            },
        },
    };
}
