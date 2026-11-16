using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Nanogpt;

/// <summary>Port of nanogpt.Config.</summary>
public sealed class NanogptOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Port-only audit index; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/nanogpt/outbound.go. NanoGPT is OpenAI-compatible
/// but reports reasoning in a top-level <c>reasoning</c> field and sometimes
/// emits tool calls as XML inside plain message content.
/// </summary>
public sealed class NanogptOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly OpenAiOutboundTransformer _inner;
    private readonly int? _channelApiKeyIndex;

    public NanogptOutboundTransformer(NanogptOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid NanoGPT transformer configuration: config cannot be nil");
        }

        _channelApiKeyIndex = config.ChannelApiKeyIndex;

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Reasoning,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "invalid NanoGPT transformer configuration: " + ex.Message, ex);
        }
    }

    public string ApiFormat => _inner.ApiFormat;

    public string? SelectedApiKey => _inner.SelectedApiKey;

    public int? SelectedApiKeyIndex => _channelApiKeyIndex;

    public HttpRequestData TransformRequest(LlmRequest? request)
        => _inner.TransformRequest(request);

    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            var body = response.Body ?? [];
            var bodyText = Encoding.UTF8.GetString(body);

            // Prefer a string `error` field for diagnostics; otherwise include
            // the raw body when there is one.
            if (TryGetErrorField(body, out var errorField) && errorField.Length > 0)
            {
                throw new InvalidOperationException(
                    "HTTP error " + response.StatusCode + ": " + errorField);
            }

            if (bodyText.Length > 0)
            {
                throw new InvalidOperationException(
                    "HTTP error " + response.StatusCode + ": " + bodyText);
            }

            throw new InvalidOperationException("HTTP error " + response.StatusCode);
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Embedding, image, moderation and audio responses use the embedded
        // OpenAI transformer's dedicated decoders.
        if (response.Request is not null)
        {
            switch (response.Request.ApiFormat)
            {
                case ApiFormats.OpenAIEmbedding:
                case ApiFormats.OpenAIModeration:
                case ApiFormats.OpenAIImageGeneration:
                case ApiFormats.OpenAIImageEdit:
                case ApiFormats.OpenAIImageVariation:
                case ApiFormats.OpenAISpeech:
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return _inner.TransformResponse(response);
            }
        }

        var nanoResponse = DecodeResponse(response.Body);

        return nanoResponse.ToOpenAiResponse().ToLlmResponse()!;
    }

    private static NanogptResponse DecodeResponse(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            if (rootKind == "null")
            {
                return new NanogptResponse();
            }

            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: "
                + GoStructJson.TypeMismatch(rootKind, "nanogpt.Response"));
        }

        try
        {
            var decoded = LlmJson.Deserialize<NanogptResponse>(body);
            if (decoded is null)
            {
                return new NanogptResponse();
            }

            if (decoded.Choices is not null)
            {
                for (var i = 0; i < decoded.Choices.Count; i++)
                {
                    decoded.Choices[i] ??= new NanogptChoice();
                }
            }

            return decoded;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }
    }

    /// <summary>
    /// Port of <c>json.Unmarshal(body, &amp;struct{ Error string })</c>: the body
    /// must be a single valid JSON value; a null root or null field leaves the
    /// zero string, other field types fail the whole decode.
    /// </summary>
    private static bool TryGetErrorField(byte[] body, out string error)
    {
        error = string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                error = property.Value.GetString() ?? string.Empty;
            }

            return true;
        }
    }

    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
    {
        if (request is not null)
        {
            switch (request.ApiFormat)
            {
                case ApiFormats.OpenAISpeech:
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return _inner.TransformStream(request, stream);
            }
        }

        var filtered = new FilterStream<StreamEvent>(
            stream, e => !StartsWith(e.Data, "[DONE]"));

        var transformed = new MapStream<StreamEvent, LlmResponse>(filtered, TransformStreamChunk);

        return new AppendStream<LlmResponse>(transformed, LlmResponse.DoneResponse);
    }

    public LlmResponse TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("stream event is nil");
        }

        if (GjsonErrorProbe.TryGetErrorString(streamEvent.Data, out var message))
        {
            throw new ResponseError
            {
                Detail = new ErrorDetail { Message = message },
            };
        }

        return TransformResponse(new HttpResponseData { Body = streamEvent.Data });
    }

    /// <summary>Port of nanoGPTChunkTransform used by the aggregator.</summary>
    public static OpenAiResponse ChunkTransform(StreamEvent chunk)
        => DecodeResponse(chunk.Data).ToOpenAiResponse();

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        if (request is not null)
        {
            switch (request.ApiFormat)
            {
                case ApiFormats.OpenAISpeech:
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return _inner.AggregateStreamChunks(request, chunks, out meta);
            }
        }

        return OpenAiAggregator.AggregateStreamChunks(chunks, ChunkTransform, out meta);
    }

    public ResponseError TransformError(HttpClientError? error)
        => _inner.TransformError(error);

    private static bool StartsWith(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }
}
