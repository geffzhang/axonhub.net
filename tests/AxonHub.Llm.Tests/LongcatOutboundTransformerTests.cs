using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Longcat;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Longcat outbound (llm/transformer/longcat and its
/// outbound_test.go): the OpenAI wire format with every message content forced
/// into the array form.
/// </summary>
public sealed class LongcatOutboundTransformerTests
{
    // Reference TestOutboundTransformer_TransformRequest_ForceMultipleContent.
    [Theory]
    [InlineData("Hello!", "Hello!")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TransformRequest_ForcesArrayContent(string? text, string expectedText)
    {
        var request = SimpleRequest();
        request.Messages =
        [
            new Message
            {
                Role = "user",
                Content = text is null ? null : MessageContent.FromText(text),
            },
        ];

        var body = Body(Transformer().TransformRequest(request));
        var content = body.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var parts = content.EnumerateArray().ToList();
        var part = Assert.Single(parts);
        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal(expectedText, part.GetProperty("text").GetString());
    }

    // Reference TestOutboundTransformer_TransformRequest_MultipleContentPreserved.
    [Fact]
    public void TransformRequest_PreservesMultipleContent()
    {
        var request = SimpleRequest();
        request.Messages =
        [
            new Message
            {
                Role = "user",
                Content = MessageContent.FromParts(
                [
                    new MessageContentPart { Type = "text", Text = "What is this?" },
                    new MessageContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ImageUrl { Url = "https://example.com/img.png" },
                    },
                ]),
            },
        ];

        var body = Body(Transformer().TransformRequest(request));
        var content = body.GetProperty("messages")[0].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var parts = content.EnumerateArray().ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("What is this?", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal(
            "https://example.com/img.png",
            parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void TransformRequest_SerializesMessagesAndContentLast()
    {
        // longcat.Request / longcat.Message embed the OpenAI types and re-declare
        // messages / content, so the outer fields shadow the embedded ones and
        // serialize last. Go's encoding/json makes this observable on the wire.
        var body = Body(Transformer().TransformRequest(SimpleRequest()));

        var rootKeys = body.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal("model", rootKeys[0]);
        Assert.Equal("messages", rootKeys[^1]);

        var message = body.GetProperty("messages")[0];
        var messageKeys = message.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal("role", messageKeys[0]);
        Assert.Equal("content", messageKeys[^1]);

        var partKeys = message.GetProperty("content")[0]
            .EnumerateObject()
            .Select(p => p.Name)
            .ToList();
        Assert.Equal(2, partKeys.Count);
        Assert.Equal("type", partKeys[0]);
        Assert.Equal("text", partKeys[1]);
    }

    [Fact]
    public void TransformRequest_MutatesTheCallerLikeTheReference()
    {
        var request = SimpleRequest();
        request.Messages = [new Message { Role = "user" }];

        Transformer().TransformRequest(request);

        // The reference fills in Content on chatReq.Messages before delegating.
        Assert.NotNull(request.Messages[0].Content);
        Assert.Equal("", request.Messages[0].Content!.Content);
    }

    [Fact]
    public void TransformRequest_NilRequest()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));

        Assert.Equal("chat completion request is nil", error.Message);
    }

    [Fact]
    public void ConstructorValidation()
    {
        var nil = Assert.Throws<InvalidOperationException>(
            () => new LongcatOutboundTransformer(null!));
        Assert.Equal(
            "failed to create longcat outbound transformer: config cannot be nil",
            nil.Message);

        var missingKey = Assert.Throws<InvalidOperationException>(
            () => new LongcatOutboundTransformer(new LongcatOutboundConfig
            {
                BaseUrl = "https://api.longcat.chat",
            }));
        Assert.StartsWith(
            "failed to create longcat outbound transformer: "
            + "invalid OpenAI transformer configuration: ",
            missingKey.Message,
            StringComparison.Ordinal);
        Assert.Contains("API key provider is required", missingKey.Message, StringComparison.Ordinal);

        var missingUrl = Assert.Throws<InvalidOperationException>(
            () => new LongcatOutboundTransformer(new LongcatOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("longcat-key"),
            }));
        Assert.StartsWith(
            "failed to create longcat outbound transformer: "
            + "invalid OpenAI transformer configuration: ",
            missingUrl.Message,
            StringComparison.Ordinal);
        Assert.Contains("base URL is required", missingUrl.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static LongcatOutboundTransformer Transformer()
        => new(new LongcatOutboundConfig
        {
            BaseUrl = "https://api.longcat.chat",
            ApiKeyProvider = new StaticApiKeyProvider("longcat-key"),
        });

    private static LlmRequest SimpleRequest(string model = "LongCat-Flash-Omni-2603")
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
