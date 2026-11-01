using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Gemini;

public sealed partial class GeminiOutboundTransformer
{
    /// <summary>Port of gemini.streamState (outbound_stream.go:15-19).</summary>
    private sealed class StreamState
    {
        public int ToolCallIndex;

        public string ResponseId = string.Empty;
    }

    /// <summary>
    /// Port of TransformStream (outbound_stream.go:21-36): append the DONE
    /// sentinel (Gemini never emits one), then map each SSE chunk through
    /// <see cref="TransformStreamChunkWithState"/> with a tool-call index and
    /// response id shared across the whole stream. MapStream skips null
    /// results, matching the reference's streams.NoNil behaviour for chunks
    /// that carry no unified event.
    /// </summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
    {
        _ = request;

        // streams.AppendStream(stream, lo.ToPtr(llm.DoneStreamEvent)).
        var withDone = new AppendStream<StreamEvent>(
            stream,
            new StreamEvent { Data = "[DONE]"u8.ToArray() });

        // Track tool call index across stream events.
        var state = new StreamState();

        // streams.MapErr: a chunk failure becomes the stream's terminal error.
        return new MapStream<StreamEvent, LlmResponse>(
            withDone,
            chunk => TransformStreamChunkWithState(chunk, state));
    }

    /// <summary>Port of TransformStreamChunk (outbound_stream.go:38-45).</summary>
    public LlmResponse? TransformStreamChunk(StreamEvent? chunk)
        => TransformStreamChunkWithState(chunk, new StreamState());

    /// <summary>Port of transformStreamChunkWithState (outbound_stream.go:47-82).</summary>
    private LlmResponse? TransformStreamChunkWithState(StreamEvent? chunk, StreamState state)
    {
        if (chunk is not { Data.Length: > 0 })
        {
            return null;
        }

        // Handle [DONE] marker - Gemini doesn't use this, but handle it for
        // consistency with the other providers.
        if (Encoding.UTF8.GetString(chunk.Data) == "[DONE]")
        {
            return LlmResponse.DoneResponse;
        }

        // Go returns the raw json.Unmarshal error; the port raises the
        // Go-style decoder message (no STJ " Path: " suffix).
        GenerateContentResponse resp;
        try
        {
            // A JSON null leaves the zero struct in place (Go Unmarshal no-op).
            resp = LlmJson.Deserialize<GenerateContentResponse>(chunk.Data)
                ?? new GenerateContentResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        // Gemini does not guarantee responseId in every streaming chunk; the
        // stream keeps one stable id (Go: "chatcmpl-" + uuid).
        if (state.ResponseId.Length == 0)
        {
            state.ResponseId = resp.ResponseId ?? string.Empty;
            if (state.ResponseId.Length == 0)
            {
                state.ResponseId = "chatcmpl-" + Guid.NewGuid().ToString("D").ToLowerInvariant();
            }
        }

        resp.ResponseId = state.ResponseId;

        // Convert to unified response format (streaming).
        var (llmResp, nextIndex) = GeminiOutboundConvert.ConvertGeminiToLlmResponseWithState(
            resp,
            true,
            state.ToolCallIndex);
        state.ToolCallIndex = nextIndex;

        return llmResp;
    }
}
