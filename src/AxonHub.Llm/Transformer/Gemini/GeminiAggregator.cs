using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Gemini;

public sealed partial class GeminiOutboundTransformer
{
    /// <summary>Port of the OutboundTransformer.AggregateStreamChunks method.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        _ = request;
        return GeminiAggregator.AggregateStreamChunks(chunks, out meta);
    }
}

/// <summary>
/// Port of the package-level gemini.AggregateStreamChunks
/// (llm/transformer/gemini/aggregator.go): aggregates Gemini streaming
/// response chunks into a complete Gemini-format response. The output is
/// always in Gemini format.
/// </summary>
public static class GeminiAggregator
{
    /// <summary>Port of candidateAggregator (aggregator.go:13-21).</summary>
    private sealed class CandidateAggregator
    {
        public long Index;

        public readonly StringBuilder ReasoningContent = new();

        public readonly StringBuilder TextParts = new();

        // Go keys the map by arrival position (len(agg.toolCalls)); iteration
        // over 0..n-1 in buildGeminiResponse reads them back in that order.
        public readonly List<ToolCall> ToolCalls = [];

        public readonly List<Blob> InlineDataParts = [];

        public string FinishReason = string.Empty;

        public GroundingMetadata? GroundingMetadata;
    }

    /// <summary>Port of AggregateStreamChunks (aggregator.go:23-108).</summary>
    public static byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        if (chunks.Count == 0)
        {
            meta = new ResponseMeta();
            return GoJson.Marshal(new GenerateContentResponse());
        }

        GenerateContentResponse? lastResp = null;
        UsageMetadata? usage = null;
        var responseId = string.Empty;
        var modelVersion = string.Empty;
        var candidateAggs = new Dictionary<long, CandidateAggregator>();

        foreach (var chunk in chunks)
        {
            // Skip empty or [DONE] events.
            if (chunk is not { Data.Length: > 0 })
            {
                continue;
            }

            if (Encoding.UTF8.GetString(chunk.Data) == "[DONE]")
            {
                continue;
            }

            GenerateContentResponse geminiResp;
            try
            {
                // A JSON null leaves the zero struct in place (Go Unmarshal no-op).
                geminiResp = LlmJson.Deserialize<GenerateContentResponse>(chunk.Data)
                    ?? new GenerateContentResponse();
            }
            catch (JsonException)
            {
                continue; // Skip invalid chunks
            }

            // Capture response ID and model version.
            if (responseId.Length == 0 && geminiResp.ResponseId is { Length: > 0 } id)
            {
                responseId = id;
            }

            if (modelVersion.Length == 0 && geminiResp.ModelVersion is { Length: > 0 } version)
            {
                modelVersion = version;
            }

            // Process each candidate.
            foreach (var candidate in geminiResp.Candidates ?? [])
            {
                if (candidate is null)
                {
                    continue;
                }

                var candidateIndex = candidate.Index;

                // Initialize candidate aggregator if it doesn't exist.
                if (!candidateAggs.TryGetValue(candidateIndex, out var agg))
                {
                    agg = new CandidateAggregator { Index = candidateIndex };
                    candidateAggs[candidateIndex] = agg;
                }

                // Process content parts.
                if (candidate.Content is { } content)
                {
                    foreach (var part in content.Parts ?? [])
                    {
                        if (part is null)
                        {
                            continue;
                        }

                        if (part.Text is { Length: > 0 } text)
                        {
                            if (part.Thought)
                            {
                                agg.ReasoningContent.Append(text);
                            }
                            else
                            {
                                agg.TextParts.Append(text);
                            }
                        }
                        else if (part.FunctionCall is { } functionCall)
                        {
                            var toolCallIndex = agg.ToolCalls.Count;
                            var argsJson = functionCall.Args is null
                                ? "null"
                                : GoJson.MarshalToString(functionCall.Args);
                            var toolCall = new ToolCall
                            {
                                Index = toolCallIndex,
                                Id = functionCall.Id ?? string.Empty,
                                Type = "function",
                                Function = new AxonHub.Llm.FunctionCall
                                {
                                    Name = functionCall.Name ?? string.Empty,
                                    Arguments = argsJson,
                                },
                            };
                            GeminiThoughtSignatures.SetOutboundToolCallThoughtSignature(toolCall, part.ThoughtSignature);
                            agg.ToolCalls.Add(toolCall);
                        }
                        else if (part.InlineData is { } inlineData)
                        {
                            agg.InlineDataParts.Add(inlineData);
                        }
                    }
                }

                // Capture finish reason.
                if (candidate.FinishReason is { Length: > 0 } finishReason)
                {
                    agg.FinishReason = finishReason;
                }

                // Capture grounding metadata (use the last one if multiple chunks have it).
                if (candidate.GroundingMetadata is { } groundingMetadata)
                {
                    agg.GroundingMetadata = groundingMetadata;
                }
            }

            // Extract usage information if present.
            if (geminiResp.UsageMetadata is { } usageMetadata)
            {
                usage = usageMetadata;
            }

            lastResp = geminiResp;
        }

        // Build the final response in Gemini format.
        if (lastResp is null)
        {
            meta = new ResponseMeta();
            return GoJson.Marshal(new GenerateContentResponse());
        }

        return BuildGeminiResponse(candidateAggs, responseId, modelVersion, usage, out meta);
    }

    /// <summary>Port of buildGeminiResponse (aggregator.go:110-166).</summary>
    private static byte[] BuildGeminiResponse(
        IReadOnlyDictionary<long, CandidateAggregator> candidateAggs,
        string responseId,
        string modelVersion,
        UsageMetadata? usage,
        out ResponseMeta meta)
    {
        // Go: make([]*Candidate, len(candidateAggs)) indexed by position — the
        // reference assumes contiguous candidate indexes and drops any
        // aggregator whose index falls outside the array (aggregator.go:116).
        var candidates = new List<Candidate>();
        var completed = candidateAggs.Count > 0;

        for (var i = 0; i < candidateAggs.Count; i++)
        {
            if (!candidateAggs.TryGetValue(i, out var agg))
            {
                // Go leaves a nil slot here, which marshals as null in the
                // candidates array. The port's model cannot hold null
                // elements, so in-range gaps shrink the array instead (C113).
                continue;
            }

            var content = new Content
            {
                Role = "model",
            };

            var parts = new List<Part>();

            // Add reasoning content (thinking) first if present.
            if (agg.ReasoningContent.Length > 0)
            {
                parts.Add(new Part
                {
                    Text = agg.ReasoningContent.ToString(),
                    Thought = true,
                });
            }

            // Add text content.
            if (agg.TextParts.Length > 0)
            {
                parts.Add(new Part { Text = agg.TextParts.ToString() });
            }

            // Add tool calls.
            foreach (var toolCall in agg.ToolCalls)
            {
                // Go re-parses the stored arguments JSON; an empty or
                // unparseable string leaves args nil (omitted on the wire).
                Dictionary<string, JsonElement>? args = null;
                if (toolCall.Function.Arguments.Length > 0)
                {
                    try
                    {
                        args = LlmJson.Deserialize<Dictionary<string, JsonElement>>(toolCall.Function.Arguments);
                    }
                    catch (JsonException)
                    {
                        args = null;
                    }
                }

                // Go: omitempty drops the empty map ("{}" parses to one).
                args = args is { Count: 0 } ? null : args;

                parts.Add(new Part
                {
                    FunctionCall = new FunctionCall
                    {
                        Id = toolCall.Id,
                        Name = toolCall.Function.Name,
                        Args = args,
                    },
                    ThoughtSignature = GeminiThoughtSignatures.GetInboundGeminiToolCallThoughtSignature(toolCall)
                        ?? string.Empty,
                });
            }

            // Add inline data parts (images, etc.).
            foreach (var inlineData in agg.InlineDataParts)
            {
                parts.Add(new Part { InlineData = inlineData });
            }

            // Go: omitempty drops the empty slice.
            content.Parts = parts.Count == 0 ? null : parts;

            // Determine finish reason.
            var finishReason = agg.FinishReason;
            if (finishReason.Length == 0)
            {
                completed = false;
                finishReason = "STOP";
            }

            candidates.Add(new Candidate
            {
                Index = agg.Index,
                Content = content,
                FinishReason = finishReason,
                GroundingMetadata = agg.GroundingMetadata,
            });
        }

        var response = new GenerateContentResponse
        {
            // Go: omitempty drops "" and the empty slice.
            ResponseId = NullIfEmpty(responseId),
            ModelVersion = NullIfEmpty(modelVersion),
            Candidates = candidates.Count == 0 ? null : candidates,
            UsageMetadata = usage,
        };

        meta = new ResponseMeta
        {
            Id = responseId,
            Usage = usage is null ? null : GeminiConvert.ConvertToLlmUsage(usage),
            Completed = completed,
        };

        return GoJson.Marshal(response);
    }

    /// <summary>Go <c>omitempty</c> on a string drops "" as well as nil.</summary>
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
