using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the test-only fake channel transformers
/// (llm/transformer/anthropic/fake.go, llm/transformer/openai/fake.go): the
/// real provider outbound is kept, but CustomizeExecutor replaces the transport
/// with the embedded testdata fixtures.
/// </summary>
public sealed class FakeTransformerTests
{
    private static readonly string TestDataRoot = Path.Combine(AppContext.BaseDirectory, "testdata");

    // ------------------------------------------------------------------
    // anthropic_fake
    // ------------------------------------------------------------------

    [Fact]
    public void AnthropicFake_TransformRequest_TargetsTheFakeEndpoint()
    {
        var transformer = new AnthropicFakeTransformer();

        Assert.Equal(ApiFormats.AnthropicMessage, transformer.ApiFormat);

        var request = transformer.TransformRequest(SimpleRequest());

        Assert.Equal("https://fake.anthropic.com/v1/messages", request.Url);
        Assert.Equal("fake", request.Auth!.ApiKey);
        Assert.Equal("X-API-Key", request.Auth.HeaderKey);
        Assert.Null(transformer.SelectedApiKey);
        Assert.Null(transformer.SelectedApiKeyIndex);
    }

    [Fact]
    public void AnthropicFake_Do_ServesTheEmbeddedResponse()
    {
        var executor = Customized(new AnthropicFakeTransformer());

        var response = executor.Do(CancellationToken.None, new HttpRequestData());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", Assert.Single(response.Headers["Content-Type"]));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(TestDataRoot, "anthropic", "anthropic-stop.response.json")),
            response.Body);
    }

    [Fact]
    public void AnthropicFake_DoStream_ServesTheEmbeddedEvents()
    {
        var executor = Customized(new AnthropicFakeTransformer());

        using var stream = executor.DoStream(CancellationToken.None, new HttpRequestData());
        var actual = Drain(stream);

        AssertMirrorsFixture(
            actual,
            Path.Combine(TestDataRoot, "anthropic", "anthropic-stop.stream.jsonl"));

        Assert.Equal(10, actual.Count);
        Assert.Equal("message_start", actual[0].Type);
        Assert.Equal("message_stop", actual[^1].Type);
    }

    // ------------------------------------------------------------------
    // openai_fake
    // ------------------------------------------------------------------

    [Fact]
    public void OpenAiFake_TransformRequest_TargetsTheFakeEndpoint()
    {
        var transformer = new OpenAiFakeTransformer();

        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var request = transformer.TransformRequest(SimpleRequest());

        Assert.Equal("https://fake.openai.com/v1/chat/completions", request.Url);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("fake", request.Auth.ApiKey);
        Assert.Null(transformer.SelectedApiKey);
        Assert.Null(transformer.SelectedApiKeyIndex);
    }

    [Fact]
    public void OpenAiFake_Do_ServesTheEmbeddedResponse()
    {
        var executor = Customized(new OpenAiFakeTransformer());

        var response = executor.Do(CancellationToken.None, new HttpRequestData());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("application/json", Assert.Single(response.Headers["Content-Type"]));
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(TestDataRoot, "openai", "openai-stop.response.json")),
            response.Body);
    }

    [Fact]
    public void OpenAiFake_DoStream_ServesTheEmbeddedEvents()
    {
        var executor = Customized(new OpenAiFakeTransformer());

        using var stream = executor.DoStream(CancellationToken.None, new HttpRequestData());
        var actual = Drain(stream);

        AssertMirrorsFixture(
            actual,
            Path.Combine(TestDataRoot, "openai", "openai-stop.stream.jsonl"));

        Assert.Equal(68, actual.Count);

        // fake.go parses Type/LastEventID as strings; the OpenAI fixture leaves
        // the SSE event type empty and ends with the [DONE] sentinel.
        Assert.All(actual, e => Assert.Equal(string.Empty, e.Type));
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(actual[^1].Data));
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static IExecutor Customized(IChannelCustomizedExecutor transformer)
        => transformer.CustomizeExecutor(new ThrowingExecutor());

    private static List<StreamEvent> Drain(IStream<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        while (stream.Next())
        {
            events.Add(stream.Current!);
        }

        Assert.Null(stream.Err);
        return events;
    }

    private static void AssertMirrorsFixture(List<StreamEvent> actual, string fixturePath)
    {
        var expected = LoadStreamEvents(fixturePath);
        Assert.Equal(expected.Count, actual.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].LastEventId, actual[i].LastEventId);
            Assert.Equal(expected[i].Type, actual[i].Type);
            Assert.Equal(expected[i].Data, Encoding.UTF8.GetString(actual[i].Data));
        }
    }

    private static List<(string LastEventId, string Type, string Data)> LoadStreamEvents(string path)
    {
        var events = new List<(string, string, string)>();

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            events.Add((
                root.GetProperty("LastEventID").GetString()!,
                root.GetProperty("Type").GetString()!,
                root.GetProperty("Data").GetString()!));
        }

        return events;
    }

    private static LlmRequest SimpleRequest()
        => new()
        {
            Model = "claude-3-7-sonnet-20250219",
            MaxTokens = 1024,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    /// <summary>Proves CustomizeExecutor discards the installed transport.</summary>
    private sealed class ThrowingExecutor : IExecutor
    {
        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => throw new InvalidOperationException("fake channels must not use the network executor");

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => throw new InvalidOperationException("fake channels must not use the network executor");
    }
}
