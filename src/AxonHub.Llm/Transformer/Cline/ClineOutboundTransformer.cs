using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Cline;

/// <summary>Port of cline.Config (llm/transformer/cline/outbound.go:21-26).</summary>
public sealed class ClineOutboundConfig
{
    /// <summary>Base URL of the Cline API.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Endpoint path override; empty uses the OpenAI outbound's default.</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the Cline outbound transformer (llm/transformer/cline): the OpenAI
/// outbound with <c>reasoning</c> instead of <c>reasoning_content</c>, a
/// <c>X-Client-Type: cline-cli</c> marker, an empty-content normalization the
/// Cline CLI requires, and the <c>{success,data,error,errors}</c> envelope on
/// responses and stream chunks.
/// </summary>
/// <remarks>
/// The reference embeds the OpenAI outbound; the port delegates request
/// building, API format, and key provenance to it, then applies Cline's
/// post-processing, exactly like the reference. A nil config panics in Go
/// (C100).
/// </remarks>
public sealed class ClineOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private const string EmptyTextPart = "[{\"type\":\"text\",\"text\":\"\"}]";

    private readonly ClineOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public ClineOutboundTransformer(ClineOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Cline transformer configuration: config cannot be nil");
        }

        _config = config;

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                EndpointPath = config.EndpointPath,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Reasoning,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "invalid Cline transformer configuration: " + ex.Message,
                ex);
        }
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest (outbound.go:54-88).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        // The embedded OpenAI outbound raises the same error; checking here
        // keeps the message identical and lets the patch loop see a non-null
        // request.
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        var httpRequest = _inner.TransformRequest(request);
        _selectedApiKey = _inner.SelectedApiKey;

        if (httpRequest.ApiFormat == ApiFormats.OpenAIChatCompletion)
        {
            httpRequest.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            HttpClientUtils.HeaderSet(httpRequest.Headers, "X-Client-Type", "cline-cli");
        }

        // The reference patches the wire body at the *unified* message indices,
        // even though RequestFromLLM may have merged system messages. That can
        // hit a different message (or pad the array) when a request carries
        // several system messages; the port reproduces it deliberately.
        var body = httpRequest.Body ?? [];
        for (var i = 0; i < request.Messages.Count; i++)
        {
            var message = request.Messages[i];
            if (!ClineRequiresContent(message.Role) || !IsEmptyClineContent(message.Content))
            {
                continue;
            }

            body = SetMessageContent(body, i);
        }

        httpRequest.Body = body;
        return httpRequest;
    }

    /// <summary>Port of clineRequiresContent.</summary>
    private static bool ClineRequiresContent(string? role)
        => role is "user" or "system" or "developer";

    /// <summary>Port of isEmptyClineContent.</summary>
    private static bool IsEmptyClineContent(MessageContent? content)
    {
        if (content?.MultipleContent is { Count: > 0 } parts)
        {
            if (parts.Count != 1 || parts[0].Type != "text")
            {
                return false;
            }

            // Go's len(part.Text) == 0 treats nil and "" alike.
            var text = parts[0].Text;
            return text is null || text.Length == 0;
        }

        return content?.Content is null || content.Content.Length == 0;
    }

    /// <summary>
    /// Port of <c>sjson.SetRawBytes(body, "messages.{i}.content", ...)</c>: the
    /// body is re-serialized through a JSON tree, which preserves property order
    /// and the raw form of untouched values the way sjson does (C100).
    /// </summary>
    private static byte[] SetMessageContent(byte[] body, int index)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to normalize Cline message content: " + ex.Message,
                ex);
        }

        if (root is not JsonObject rootObject)
        {
            throw new InvalidOperationException(
                "failed to normalize Cline message content: json must be an object or array");
        }

        if (!rootObject.TryGetPropertyValue("messages", out var messagesNode) || messagesNode is null)
        {
            throw new InvalidOperationException(
                "failed to normalize Cline message content: messages: path not found");
        }

        if (messagesNode is not JsonArray messages)
        {
            throw new InvalidOperationException(
                "failed to normalize Cline message content: messages: not an array");
        }

        // sjson pads a missing tail with nulls when the index is out of range.
        while (messages.Count <= index)
        {
            messages.Add(null);
        }

        if (messages[index] is not JsonObject messageObject)
        {
            // sjson replaces a missing/null/scalar element with a fresh object
            // before setting the leaf.
            messageObject = new JsonObject();
            messages[index] = messageObject;
        }

        messageObject["content"] = JsonNode.Parse(EmptyTextPart);
        return GoJson.Marshal(rootObject);
    }

    /// <summary>Port of TransformResponse (outbound.go:90-134).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        if (response.StatusCode >= 400)
        {
            // Go returns the *llm.ResponseError as the error value; the port's
            // ResponseError is itself an exception.
            throw ClineErrors.Parse(response.StatusCode, response.Body);
        }

        ClineWrapped? wrapped;
        try
        {
            wrapped = LlmJson.Deserialize<ClineWrapped>(response.Body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal Cline response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        if (wrapped?.Success is { } success)
        {
            if (!success)
            {
                if (wrapped.Error is { ValueKind: not JsonValueKind.Null } error)
                {
                    throw ClineErrors.FromRaw(502, error);
                }

                if (wrapped.Errors is { ValueKind: not JsonValueKind.Null } errors)
                {
                    throw ClineErrors.FromRaw(502, errors);
                }

                throw ClineErrors.FromRaw(502, "Cline request failed");
            }

            return ToLlmResponse(wrapped.Data);
        }

        ClineResponse bare;
        try
        {
            bare = LlmJson.Deserialize<ClineResponse>(response.Body) ?? new ClineResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        return ToLlmResponse(bare);
    }

    private static LlmResponse ToLlmResponse(ClineResponse? response)
    {
        var openAiResponse = response?.ToOpenAiResponse()
            ?? new ClineResponse().ToOpenAiResponse();
        return openAiResponse.ToLlmResponse() ?? new LlmResponse();
    }

    /// <summary>Port of TransformStream: one event in, one unified response out.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => new MapStream<StreamEvent, LlmResponse>(stream, TransformStreamChunk);

    /// <summary>Port of TransformStreamChunk (outbound.go:145-160).</summary>
    public LlmResponse? TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("stream event is nil");
        }

        if (StartsWith(streamEvent.Data, "[DONE]"))
        {
            return LlmResponse.DoneResponse;
        }

        var streamError = ClineErrors.ParseStreamErrorEvent(streamEvent);
        if (streamError is not null)
        {
            throw streamError;
        }

        return TransformResponse(new HttpResponseData { Body = streamEvent.Data });
    }

    /// <summary>Port of TransformError (outbound.go:162-170).</summary>
    public ResponseError TransformError(HttpClientError? error)
        => error is null
            ? ClineErrors.FromRaw(500, GoHttpStatus.Text(500))
            : ClineErrors.Parse(error.StatusCode, error.Body);

    /// <summary>Port of AggregateStreamChunks (outbound.go:172-186).</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => OpenAiAggregator.AggregateStreamChunks(chunks, ClineChunkTransform, out meta);

    /// <summary>
    /// Port of clineChunkTransform: the aggregator skips any chunk whose
    /// transform fails, so both in-band errors and malformed JSON are reported
    /// through the exception type the aggregator ignores.
    /// </summary>
    private static OpenAiResponse ClineChunkTransform(StreamEvent chunk)
    {
        var streamError = ClineErrors.ParseStreamErrorEvent(chunk);
        if (streamError is not null)
        {
            throw new InvalidOperationException(streamError.Detail.Message, streamError);
        }

        try
        {
            return (LlmJson.Deserialize<ClineResponse>(chunk.Data) ?? new ClineResponse())
                .ToOpenAiResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    private static bool StartsWith(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != (byte)prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Port of the anonymous <c>{success,data,error,errors}</c> struct at
    /// outbound.go:104-109. <c>error</c>/<c>errors</c> stay as raw JSON because
    /// the reference decodes them into <c>any</c> and hands them to
    /// <see cref="ClineErrors.FromRaw(int, JsonElement)"/>.
    /// </summary>
    private sealed class ClineWrapped
    {
        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        [JsonPropertyName("data")]
        public ClineResponse? Data { get; set; }

        [JsonPropertyName("error")]
        public JsonElement? Error { get; set; }

        [JsonPropertyName("errors")]
        public JsonElement? Errors { get; set; }
    }
}
