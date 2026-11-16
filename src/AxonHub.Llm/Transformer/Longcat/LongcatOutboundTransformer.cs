using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Longcat;

/// <summary>Port of longcat.Config (llm/transformer/longcat/outbound.go:37-40).</summary>
public sealed class LongcatOutboundConfig
{
    /// <summary>Base URL of the Longcat deployment; required by the OpenAI outbound.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the Longcat outbound transformer (llm/transformer/longcat): the
/// OpenAI outbound plus a post-processing pass that forces every message's
/// <c>content</c> into the array form, because LongCat models reject plain
/// string content with "json format error".
/// </summary>
/// <remarks>
/// The reference unmarshals the OpenAI request into <c>openai.Request</c> and
/// re-marshals it as <c>longcat.Request</c>, whose embedded fields make the
/// outer <c>messages</c> / <c>content</c> fields shadow the embedded ones and
/// therefore serialize last. The port reproduces that byte order by moving the
/// properties in the serialized object instead of re-encoding from a second
/// model (C099). The request mutation (nil content becomes the empty string) is
/// applied to the caller's <see cref="LlmRequest"/> exactly like the reference.
/// </remarks>
public sealed class LongcatOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly LongcatOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public LongcatOutboundTransformer(LongcatOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "failed to create longcat outbound transformer: config cannot be nil");
        }

        _config = config;

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Content,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create longcat outbound transformer: " + ex.Message,
                ex);
        }
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        // Ensure every message has non-nil content before the OpenAI
        // serialization, otherwise the content field would be omitted.
        foreach (var message in request.Messages)
        {
            if (message.Content is null || message.Content.IsZero)
            {
                message.Content = MessageContent.FromText("");
            }
        }

        var httpRequest = _inner.TransformRequest(request);
        _selectedApiKey = _inner.SelectedApiKey;

        if (httpRequest.Body is null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal openai request: unexpected end of JSON input");
        }

        httpRequest.Body = ForceArrayContent(httpRequest.Body);
        return httpRequest;
    }

    /// <summary>Port of the unmarshal → longcat.Request → marshal round-trip.</summary>
    private static byte[] ForceArrayContent(byte[] body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("failed to unmarshal openai request: " + ex.Message, ex);
        }

        if (root is not JsonObject request)
        {
            throw new InvalidOperationException("failed to unmarshal openai request: invalid request object");
        }

        // longcat.Request embeds openai.Request and re-declares messages, so the
        // outer field shadows the embedded one and is emitted last.
        var messages = request["messages"];
        request.Remove("messages");

        if (messages is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var message = array[i] as JsonObject;
                if (message is null)
                {
                    message = new JsonObject();
                    array[i] = message;
                }

                // longcat.Message embeds openai.Message and re-declares content.
                var content = message["content"];
                message.Remove("content");
                message["content"] = LongcatContent(content);
            }
        }

        request["messages"] = messages;

        try
        {
            return GoJson.Marshal(request);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("failed to marshal longcat request: " + ex.Message, ex);
        }
    }

    /// <summary>Port of longcat.MessageContent.MarshalJSON.</summary>
    private static JsonArray LongcatContent(JsonNode? content)
    {
        // len(MultipleContent) > 0: an existing non-empty array is marshalled as-is.
        if (content is JsonArray { Count: > 0 } parts)
        {
            return parts;
        }

        var text = content is JsonValue value && value.TryGetValue<string>(out var s) ? s : "";

        return new JsonArray(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text,
        });
    }

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
        => _inner.TransformResponse(response);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);
}
