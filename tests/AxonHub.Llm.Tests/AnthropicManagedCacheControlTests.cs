using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of the managed-platform cache_control tests added by live 697b13a5
/// (#2697) and 28cab912 (#2702): converted requests opt into automatic caching,
/// and Vertex/Bedrock require the marker at block level.
/// </summary>
public sealed class AnthropicManagedCacheControlTests
{
    public static TheoryData<AnthropicPlatformType> ManagedPlatforms => new()
    {
        AnthropicPlatformType.Vertex,
        AnthropicPlatformType.Bedrock,
    };

    [Theory]
    [MemberData(nameof(ManagedPlatforms))]
    public void ConvertedRequestUsesBlockLevelCacheControl(AnthropicPlatformType platform)
    {
        var result = Transformer(platform).TransformRequest(
            Request(ApiFormats.OpenAIChatCompletion, MessageContent.FromText("Hello")));

        var body = Body(result);
        Assert.False(body.TryGetProperty("cache_control", out _));
        var block = Assert.Single(body.GetProperty("messages")[0].GetProperty("content").EnumerateArray());
        Assert.Equal("ephemeral", block.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(ManagedPlatforms))]
    public void NativeTopLevelCacheControlIsNormalized(AnthropicPlatformType platform)
    {
        var request = Request(ApiFormats.AnthropicMessage, MessageContent.FromText("Hello"));
        request.TransformerMetadata = CacheControlMetadata();

        var result = Transformer(platform).TransformRequest(request);

        var body = Body(result);
        Assert.False(body.TryGetProperty("cache_control", out _));
        var block = Assert.Single(body.GetProperty("messages")[0].GetProperty("content").EnumerateArray());
        Assert.Equal("ephemeral", block.GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(ManagedPlatforms))]
    public void ExplicitAndTopLevelCacheControlsArePreserved(AnthropicPlatformType platform)
    {
        var request = Request(
            ApiFormats.AnthropicMessage,
            MessageContent.FromParts(
            [
                new MessageContentPart
                {
                    Type = "text",
                    Text = "Earlier",
                    CacheControl = new CacheControl { Type = "ephemeral" },
                },
            ]),
            MessageContent.FromText("Latest"));
        request.TransformerMetadata = CacheControlMetadata();

        var result = Transformer(platform).TransformRequest(request);

        var body = Body(result);
        Assert.False(body.TryGetProperty("cache_control", out _));
        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal(
            "ephemeral",
            messages[0].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal(
            "ephemeral",
            messages[1].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(ManagedPlatforms))]
    public void FullMatchingBreakpointSetIsAccepted(AnthropicPlatformType platform)
    {
        var messages = new List<Message>();
        for (var i = 0; i < AnthropicCacheControlOptimizer.MaxCacheControlBreakpoints; i++)
        {
            messages.Add(new Message
            {
                Role = "user",
                Content = MessageContent.FromParts(
                [
                    new MessageContentPart
                    {
                        Type = "text",
                        Text = "cached",
                        CacheControl = new CacheControl { Type = "ephemeral" },
                    },
                ]),
            });
        }

        var request = new LlmRequest
        {
            Model = "claude-opus-4-6",
            MaxTokens = 1024,
            ApiFormat = ApiFormats.AnthropicMessage,
            Messages = messages,
            TransformerMetadata = CacheControlMetadata(),
        };

        var result = Transformer(platform).TransformRequest(request);

        var body = Body(result);
        Assert.False(body.TryGetProperty("cache_control", out _));
        var bodyMessages = body.GetProperty("messages");
        Assert.Equal(AnthropicCacheControlOptimizer.MaxCacheControlBreakpoints, bodyMessages.GetArrayLength());
        foreach (var message in bodyMessages.EnumerateArray())
        {
            Assert.Equal(
                "ephemeral",
                message.GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
        }
    }

    [Fact]
    public void DirectConvertedRequestKeepsTopLevelCacheControl()
    {
        var result = AnthropicOutboundTransformer
            .Create("https://api.anthropic.com", "test-key")
            .TransformRequest(Request(ApiFormats.OpenAIChatCompletion, MessageContent.FromText("Hello")));

        var body = Body(result);
        Assert.Equal("ephemeral", body.GetProperty("cache_control").GetProperty("type").GetString());
        // Native Anthropic accepts the top-level shape on the string content.
        Assert.Equal(JsonValueKind.String, body.GetProperty("messages")[0].GetProperty("content").ValueKind);
    }

    [Fact]
    public void NativeRequestWithoutCacheMetadataDoesNotOptIn()
    {
        var result = Transformer(AnthropicPlatformType.Vertex).TransformRequest(
            Request(ApiFormats.AnthropicMessage, MessageContent.FromText("Hello")));

        var body = Body(result);
        Assert.False(body.TryGetProperty("cache_control", out _));
        // No marker was added, so the native string content shape is untouched.
        Assert.Equal(
            JsonValueKind.String,
            body.GetProperty("messages")[0].GetProperty("content").ValueKind);
    }

    private static AnthropicOutboundTransformer Transformer(AnthropicPlatformType platform)
        => new(new AnthropicOutboundConfig
        {
            Type = platform,
            BaseUrl = "https://example.test",
            ProjectId = "project",
            Region = "us-central1",
        });

    private static LlmRequest Request(string apiFormat, params MessageContent[] contents)
    {
        var messages = new List<Message>();
        foreach (var content in contents)
        {
            messages.Add(new Message { Role = "user", Content = content });
        }

        return new LlmRequest
        {
            Model = "claude-opus-4-6",
            MaxTokens = 1024,
            ApiFormat = apiFormat,
            Messages = messages,
        };
    }

    private static Dictionary<string, JsonElement> CacheControlMetadata()
    {
        using var document = JsonDocument.Parse("""{"type":"ephemeral"}""");
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [AnthropicJson.KeyCacheControl] = document.RootElement.Clone(),
        };
    }

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
