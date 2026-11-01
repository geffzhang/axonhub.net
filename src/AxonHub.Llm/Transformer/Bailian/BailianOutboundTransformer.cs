using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Bailian;

/// <summary>Port of bailian.Config (llm/transformer/bailian/outbound.go:22-25).</summary>
public sealed class BailianOutboundConfig
{
    /// <summary>Custom base URL (optional in the reference; the OpenAI outbound requires one).</summary>
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
/// Port of the Bailian (DashScope) outbound transformer
/// (llm/transformer/bailian/outbound.go): the OpenAI outbound plus the two
/// normalizations DashScope needs — consecutive assistant tool-call messages
/// are merged, and reasoning effort <c>none</c> is sent as
/// <c>enable_thinking: false</c> instead of <c>reasoning_effort</c>. The stream
/// carries extra filters for text emitted after tool calls and repeated empty
/// tool arguments.
/// </summary>
/// <remarks>
/// The reference embeds the OpenAI outbound, so response, error and
/// aggregation handling delegate to it unchanged; only <c>TransformRequest</c>
/// and <c>TransformStream</c> are overridden. A nil config returns an error in
/// Go; the port raises <see cref="InvalidOperationException"/> (C103).
/// </remarks>
public sealed class BailianOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly BailianOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public BailianOutboundTransformer(BailianOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Bailian transformer configuration: config is nil");
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
                "invalid Bailian transformer configuration: " + ex.Message,
                ex);
        }
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        var llmRequest = MergeConsecutiveToolCallMessages(request);
        var reasoningNone = llmRequest is not null
            && llmRequest.ReasoningEffort == ReasoningEfforts.None;

        if (reasoningNone)
        {
            // Bake thinking off on a copy: "none" is not forwardable as
            // reasoning_effort, and the caller's request must stay untouched.
            llmRequest = llmRequest!.ShallowClone();
            llmRequest.ReasoningEffort = null;
        }

        var httpRequest = _inner.TransformRequest(llmRequest);
        _selectedApiKey = _inner.SelectedApiKey;

        if (!reasoningNone)
        {
            return httpRequest;
        }

        // Go: sjson.SetBytes(httpReq.Body, "enable_thinking", false).
        try
        {
            ReadOnlySpan<byte> body = httpRequest.Body ?? [];
            httpRequest.Body = RawJsonPatch.SetTopLevelRaw(
                body,
                "enable_thinking"u8,
                "false"u8);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException(
                "failed to set Bailian enable_thinking: " + ex.Message,
                ex);
        }

        return httpRequest;
    }

    /// <summary>Port of the embedded OpenAI outbound: response handling is untouched.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
        => _inner.TransformResponse(response);

    /// <summary>Port of TransformStream: the OpenAI stream plus the Bailian filter.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
    {
        var baseStream = _inner.TransformStream(request, stream);

        return new BailianStreamFilter(baseStream);
    }

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of mergeConsecutiveToolCallMessages (outbound.go:100-147).</summary>
    internal static LlmRequest? MergeConsecutiveToolCallMessages(LlmRequest? request)
    {
        if (request is null || request.Messages is null || request.Messages.Count < 2)
        {
            return request;
        }

        var changed = false;
        var messages = new List<Message>(request.Messages.Count);
        Message? pending = null;

        foreach (var message in request.Messages)
        {
            if (IsMergeableToolCallMessage(message))
            {
                if (pending is null)
                {
                    // Go copies the struct; cloning keeps the caller's message
                    // untouched when the pending copy absorbs another call.
                    pending = message.ShallowClone();
                }
                else
                {
                    pending.ToolCalls =
                    [
                        .. pending.ToolCalls ?? [],
                        .. message.ToolCalls ?? [],
                    ];
                    changed = true;
                }

                continue;
            }

            if (pending is not null)
            {
                messages.Add(pending);
                pending = null;
            }

            messages.Add(message);
        }

        if (pending is not null)
        {
            messages.Add(pending);
        }

        if (!changed)
        {
            return request;
        }

        var updated = request.ShallowClone();
        updated.Messages = messages;

        return updated;
    }

    /// <summary>Port of isMergeableToolCallMessage (outbound.go:149-169).</summary>
    internal static bool IsMergeableToolCallMessage(Message message)
    {
        if (!string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (message.ToolCalls is not { Count: > 0 })
        {
            return false;
        }

        if (message.ToolCallId is not null
            || message.Name is not null
            || !string.IsNullOrEmpty(message.Refusal)
            || message.MessageIndex is not null
            || message.ToolCallName is not null
            || message.ToolCallIsError is not null)
        {
            return false;
        }

        if (message.ReasoningContent is not null
            || message.ReasoningSignature is not null
            || message.RedactedReasoningContent is not null
            || message.CacheControl is not null)
        {
            return false;
        }

        return IsEmptyMessageContent(message.Content);
    }

    /// <summary>Port of isEmptyMessageContent (outbound.go:171-177).</summary>
    private static bool IsEmptyMessageContent(MessageContent? content)
    {
        if (content is null)
        {
            return true;
        }

        if (content.Content is { Length: > 0 })
        {
            return false;
        }

        return content.MultipleContent is not { Count: > 0 };
    }
}

/// <summary>
/// Port of bailianStreamFilter (stream_filter.go): forwards text until a tool
/// call appears, then holds back text so it is emitted in one chunk right
/// before the finish chunk, and drops repeated empty <c>{}</c> arguments.
/// </summary>
internal sealed class BailianStreamFilter : IStream<LlmResponse>
{
    private readonly IStream<LlmResponse> _source;
    private readonly Queue<LlmResponse> _pending = new();
    private readonly StringBuilder _bufferedText = new();
    private readonly Dictionary<(int ChoiceIndex, int CallIndex), string> _toolArgs = new();

    private bool _sawToolCalls;
    private int _lastTextChoice;

    public BailianStreamFilter(IStream<LlmResponse> source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public LlmResponse? Current { get; private set; }

    public Exception? Err => _source.Err;

    public bool Next()
    {
        if (_pending.Count > 0)
        {
            Current = _pending.Dequeue();
            return true;
        }

        while (_source.Next())
        {
            var response = _source.Current;
            if (response is null)
            {
                continue;
            }

            // Go compares against the shared llm.DoneResponse pointer.
            if (ReferenceEquals(response, LlmResponse.DoneResponse))
            {
                Current = response;
                return true;
            }

            var filtered = FilterResponse(response);

            if (_pending.Count > 0)
            {
                Current = _pending.Dequeue();
                return true;
            }

            if (filtered is null)
            {
                continue;
            }

            Current = filtered;
            return true;
        }

        return false;
    }

    public void Dispose() => _source.Dispose();

    /// <summary>Port of filterResponse.</summary>
    private LlmResponse? FilterResponse(LlmResponse response)
    {
        foreach (var choice in response.Choices)
        {
            if (choice.Delta?.ToolCalls is { Count: > 0 } toolCalls)
            {
                _sawToolCalls = true;
                FilterToolCalls(choice.Index, toolCalls);
            }
        }

        // Before the first tool call everything streams through untouched.
        if (!_sawToolCalls)
        {
            return response;
        }

        var hasFinish = false;

        foreach (var choice in response.Choices)
        {
            if (choice.FinishReason is not null)
            {
                hasFinish = true;
            }

            var text = ExtractTextDelta(choice);
            if (text.Length > 0)
            {
                _lastTextChoice = choice.Index;
                _bufferedText.Append(text);
            }
        }

        // On finish, release the buffered text just before the finish chunk.
        if (hasFinish && _bufferedText.Length > 0)
        {
            var textChunk = BuildTextChunk(response, _lastTextChoice, _bufferedText.ToString());
            _bufferedText.Clear();

            if (textChunk is not null)
            {
                _pending.Enqueue(textChunk);
            }

            _pending.Enqueue(response);

            return null;
        }

        return response;
    }

    /// <summary>Port of filterToolCalls.</summary>
    private void FilterToolCalls(int choiceIndex, List<ToolCall> toolCalls)
    {
        foreach (var toolCall in toolCalls)
        {
            var argument = toolCall.Function.Arguments;
            if (argument.Length == 0)
            {
                continue;
            }

            var key = (choiceIndex, toolCall.Index);
            _toolArgs.TryGetValue(key, out var accumulated);

            if (GoTrimSpace(argument) == "{}" && GoTrimSpace(accumulated ?? string.Empty).Length > 0)
            {
                toolCall.Function.Arguments = string.Empty;
                continue;
            }

            _toolArgs[key] = (accumulated ?? string.Empty) + argument;
        }
    }

    /// <summary>Port of extractTextDelta: pulls text out of the delta in place.</summary>
    private static string ExtractTextDelta(Choice choice)
    {
        if (choice.Delta is null)
        {
            return string.Empty;
        }

        var content = choice.Delta.Content;
        if (content is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        if (content.Content is { } value)
        {
            if (value.Length > 0)
            {
                text.Append(value);
            }

            content.Content = null;
        }

        if (content.MultipleContent is { Count: > 0 } parts)
        {
            var kept = new List<MessageContentPart>(parts.Count);
            foreach (var part in parts)
            {
                if (part.Type == "text" && part.Text is { Length: > 0 } partText)
                {
                    text.Append(partText);
                    continue;
                }

                kept.Add(part);
            }

            content.MultipleContent = kept;
        }

        return text.ToString();
    }

    /// <summary>Port of buildTextChunk.</summary>
    private static LlmResponse? BuildTextChunk(LlmResponse baseResponse, int choiceIndex, string text)
    {
        return new LlmResponse
        {
            Id = baseResponse.Id,
            Object = baseResponse.Object,
            Created = baseResponse.Created,
            Model = baseResponse.Model,
            SystemFingerprint = baseResponse.SystemFingerprint,
            ServiceTier = baseResponse.ServiceTier,
            Choices =
            [
                new Choice
                {
                    Index = choiceIndex,
                    Delta = new Message { Content = MessageContent.FromText(text) },
                },
            ],
        };
    }

    /// <summary>Port of unicode.IsSpace (Go): Latin-1 spaces plus the Unicode White_Space set.</summary>
    private static string GoTrimSpace(string value)
    {
        var start = 0;
        while (start < value.Length && IsGoSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsGoSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsGoSpace(char c) => Array.IndexOf(GoWhitespace, c) >= 0;

    private static readonly char[] GoWhitespace =
    [
        '\t', '\n', '\v', '\f', '\r', ' ', '\u0085', '\u00A0',
        '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004',
        '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A',
        '\u2028', '\u2029', '\u202F', '\u205F', '\u3000'
    ];
}
