using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.AiSdk;

/// <summary>
/// Port of aisdk.DataStreamTransformer
/// (llm/transformer/aisdk/datastream.go:18-207): the AI SDK data stream
/// protocol inbound transformer used by the admin playground chat endpoint.
/// </summary>
public sealed class AiSdkDataStreamTransformer : IInboundTransformer
{
    private const string InvalidRequestPrefix = "invalid request: ";

    public LlmRequest TransformRequest(HttpRequestData? request)
    {
        var body = request?.Body ?? [];

        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to parse AI SDK request: " + syntaxError);
        }

        var kind = GoStructJson.RootKind(body);
        AiSdkRequest aiSdkRequest;
        if (kind == "null")
        {
            aiSdkRequest = new AiSdkRequest();
        }
        else if (kind is not null)
        {
            throw TransformerErrors.InvalidRequest(
                "failed to parse AI SDK request: "
                + GoStructJson.TypeMismatch(kind, "aisdk.Request"));
        }
        else
        {
            try
            {
                aiSdkRequest = LlmJson.Deserialize<AiSdkRequest>(body) ?? new AiSdkRequest();
            }
            catch (JsonException ex)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to parse AI SDK request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                    ex);
            }
        }

        return AiSdkConvertRequest.Convert(aiSdkRequest, ApiFormats.AiSDKDataStream);
    }

    public HttpResponseData TransformResponse(LlmResponse? response)
        => throw new InvalidOperationException(
            "data stream protocol only supports streaming responses");

    public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // streams.AppendStream then streams.NoNil, with llm.DoneStreamEvent as
        // the trailing marker.
        return GoStreams.NoNil(
            new AppendStream<StreamEvent>(
                new AiSdkConvertStream(stream),
                new StreamEvent { Type = string.Empty, Data = "[DONE]"u8.ToArray() }));
    }

    public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var messageId = string.Empty;
        var currentText = new StringBuilder();
        var textOpen = false;
        var currentReasoning = new StringBuilder();
        var reasoningOpen = false;
        var parts = new List<(string Type, string Text)>();

        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.Data.Length == 0)
            {
                continue;
            }

            if (chunk.Data.AsSpan().SequenceEqual("[DONE]"u8))
            {
                continue;
            }

            AiSdkStreamEvent? streamEvent;
            try
            {
                streamEvent = LlmJson.Deserialize<AiSdkStreamEvent>(chunk.Data);
            }
            catch (JsonException)
            {
                // Not a JSON StreamEvent (for example an SSE-formatted line):
                // the reference skips it for aggregation.
                continue;
            }

            if (streamEvent is null)
            {
                continue;
            }

            switch (streamEvent.Type)
            {
                case "start":
                    messageId = streamEvent.MessageId ?? string.Empty;
                    break;
                case "text-start":
                    if (textOpen)
                    {
                        parts.Add(("text", currentText.ToString()));
                        currentText.Clear();
                    }

                    textOpen = true;
                    break;
                case "text-delta":
                    if (textOpen)
                    {
                        currentText.Append(streamEvent.Delta);
                    }

                    break;
                case "text-end":
                    if (textOpen)
                    {
                        parts.Add(("text", currentText.ToString()));
                        currentText.Clear();
                        textOpen = false;
                    }

                    break;
                case "reasoning-start":
                    if (reasoningOpen)
                    {
                        parts.Add(("reasoning", currentReasoning.ToString()));
                        currentReasoning.Clear();
                    }

                    reasoningOpen = true;
                    break;
                case "reasoning-delta":
                    if (reasoningOpen)
                    {
                        currentReasoning.Append(streamEvent.Delta);
                    }

                    break;
                case "reasoning-end":
                    if (reasoningOpen)
                    {
                        parts.Add(("reasoning", currentReasoning.ToString()));
                        currentReasoning.Clear();
                        reasoningOpen = false;
                    }

                    break;
                case "finish-step":
                case "finish":
                case "tool-input-start":
                case "tool-input-delta":
                case "tool-input-available":
                    break;
                default:
                    break;
            }
        }

        if (textOpen)
        {
            parts.Add(("text", currentText.ToString()));
        }

        if (reasoningOpen)
        {
            parts.Add(("reasoning", currentReasoning.ToString()));
        }

        meta = new ResponseMeta { Id = messageId };
        return MarshalUiMessage(messageId, parts);
    }

    public HttpResponseData TransformError(Exception? error)
    {
        if (error is null)
        {
            return FromBody(500, "{\"message\":\"internal server error\",\"type\":\"internal_server_error\"}");
        }

        if (GoErrors.As<HttpClientError>(error) is { } httpError)
        {
            return new HttpResponseData
            {
                StatusCode = httpError.StatusCode,
                Body = httpError.Body,
            };
        }

        if (GoErrors.As<TransformerException>(error) is { Kind: TransformerErrorKind.InvalidRequest })
        {
            var message = error.Message;
            if (message.StartsWith(InvalidRequestPrefix, StringComparison.Ordinal))
            {
                message = message[InvalidRequestPrefix.Length..];
            }

            return FromBody(400, "{\"message\":\"" + message + "\",\"type\":\"invalid_request\"}");
        }

        if (GoErrors.As<ResponseError>(error) is { } llmError)
        {
            return FromBody(
                llmError.StatusCode,
                "{\"message\":\"" + llmError.Detail.Message + "\",\"type\":\""
                + llmError.Detail.Type + "\"}");
        }

        return FromBody(
            500,
            "{\"message\":\"" + error.Message + "\",\"type\":\"internal_server_error\"}");
    }

    private static HttpResponseData FromBody(int statusCode, string body)
        => new()
        {
            StatusCode = statusCode,
            Body = Encoding.UTF8.GetBytes(body),
        };

    private static byte[] MarshalUiMessage(
        string messageId,
        List<(string Type, string Text)> parts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
        {
            writer.WriteStartObject();
            if (messageId.Length > 0)
            {
                writer.WriteString("id", messageId);
            }

            writer.WriteString("role", "assistant");
            writer.WritePropertyName("content");
            writer.WriteNullValue();
            writer.WritePropertyName("parts");
            if (parts.Count == 0)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var (type, text) in parts)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", type);
                    if (text.Length > 0)
                    {
                        writer.WriteString("text", text);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return GoJson.Escape(buffer.ToArray());
    }
}
