using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropic.FakeTransformer (fake.go): embeds the real Anthropic
/// outbound under the <c>https://fake.anthropic.com</c> base URL and replaces
/// its transport with an executor that serves <c>testdata/anthropic-stop.*</c>.
/// Used by the test-only <c>anthropic_fake</c> channel; it never reaches the
/// network.
/// </summary>
public sealed class AnthropicFakeTransformer : IOutboundTransformer, IChannelCustomizedExecutor, IApiKeyProvenance
{
    private const string ResponseResource =
        "AxonHub.Llm.Transformer.Anthropic.TestData.anthropic-stop.response.json";

    private const string StreamResource =
        "AxonHub.Llm.Transformer.Anthropic.TestData.anthropic-stop.stream.jsonl";

    // fake.go:27: NewOutboundTransformer("https://fake.anthropic.com", "fake").
    // One per fake transformer, like the reference (the outbound keeps the
    // selected-key audit state of the latest request).
    private readonly AnthropicOutboundTransformer _inner =
        AnthropicOutboundTransformer.Create("https://fake.anthropic.com", "fake");

    /// <summary>Port of CustomizeExecutor: the fixed-response executor replaces the transport.</summary>
    public IExecutor CustomizeExecutor(IExecutor executor) => new FakeExecutor();

    public string ApiFormat => _inner.ApiFormat;

    // The reference's performance record reads the key from the channel key
    // provider context; the fake channel never runs that provider, so the row
    // carries no API key even though the wire request uses "fake".
    public string? SelectedApiKey => null;

    public int? SelectedApiKeyIndex => null;

    public HttpRequestData TransformRequest(LlmRequest? request) => _inner.TransformRequest(request);

    public LlmResponse TransformResponse(HttpResponseData? response) => _inner.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of fakeExecutor: fixed 200/JSON and a fixed JSONL event list.</summary>
    private sealed class FakeExecutor : IExecutor
    {
        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => new()
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = FakeTestData.Read(ResponseResource),
            };

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
        {
            var lines = Encoding.UTF8.GetString(FakeTestData.Read(StreamResource)).Trim().Split('\n');
            var events = new List<StreamEvent>(lines.Length);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                FakeStreamLine? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<FakeStreamLine>(line);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("failed to parse stream event: " + ex.Message, ex);
                }

                // Go's json.Unmarshal leaves a null into a struct as the zero
                // value; a literal "null" line therefore still becomes an event.
                events.Add(new StreamEvent
                {
                    LastEventId = parsed?.LastEventId ?? string.Empty,
                    Type = parsed?.Type ?? string.Empty,
                    Data = Encoding.UTF8.GetBytes(parsed?.Data ?? string.Empty),
                });
            }

            return new SliceStream<StreamEvent>(events);
        }
    }

    /// <summary>The JSONL envelope (fake.go:83-87); Go field names are exact.</summary>
    private sealed class FakeStreamLine
    {
        [JsonPropertyName("LastEventID")]
        public string? LastEventId { get; set; }

        [JsonPropertyName("Type")]
        public string? Type { get; set; }

        [JsonPropertyName("Data")]
        public string? Data { get; set; }
    }
}
