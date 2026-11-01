using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of llm/transformer/openai/completion_aggregator.go: folds legacy
/// completion stream chunks into one unified body. Used when a client asked
/// for a non-streaming response but the attempt had to stream upstream.
/// </summary>
public static class CompletionAggregator
{
    // Go: json.Marshal(&llm.Response{}) — a nil Choices slice marshals as null.
    private const string ZeroResponse =
        "{\"id\":\"\",\"choices\":null,\"object\":\"\",\"created\":0,\"model\":\"\"}";

    /// <summary>Port of AggregateCompletionStreamChunks.</summary>
    public static byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
    {
        if (chunks.Count == 0)
        {
            meta = new ResponseMeta();
            return Encoding.UTF8.GetBytes(ZeroResponse);
        }

        OpenAiCompletionResponse? lastChunkResponse = null;
        var id = string.Empty;
        var model = string.Empty;
        long created = 0;
        Usage? usage = null;
        var accumulatedText = new StringBuilder();
        string? finishReason = null;

        foreach (var chunk in chunks)
        {
            if (StartsWith(chunk.Data, "[DONE]"))
            {
                continue;
            }

            OpenAiCompletionResponse compResp;
            try
            {
                compResp = LlmJson.Deserialize<OpenAiCompletionResponse>(chunk.Data)
                    ?? new OpenAiCompletionResponse();
            }
            catch (JsonException)
            {
                continue;
            }

            lastChunkResponse = compResp;

            var chunkId = compResp.Id ?? string.Empty;
            var chunkModel = compResp.Model ?? string.Empty;

            if (id.Length == 0 && chunkId.Length > 0)
            {
                id = chunkId;
            }

            if (model.Length == 0 && chunkModel.Length > 0)
            {
                model = chunkModel;
            }

            if (created == 0 && compResp.Created != 0)
            {
                created = compResp.Created;
            }

            foreach (var choice in compResp.Choices ?? [])
            {
                if (choice is null)
                {
                    continue;
                }

                accumulatedText.Append(choice.Text ?? string.Empty);
                if (choice.FinishReason is not null)
                {
                    finishReason = choice.FinishReason;
                }
            }

            var chunkUsage = compResp.Usage ?? new OpenAiCompletionUsage();
            if (chunkUsage.PromptTokens > 0 || chunkUsage.TotalTokens > 0)
            {
                usage = new Usage
                {
                    PromptTokens = chunkUsage.PromptTokens,
                    CompletionTokens = chunkUsage.CompletionTokens,
                    TotalTokens = chunkUsage.TotalTokens,
                };
            }
        }

        if (lastChunkResponse is null)
        {
            meta = new ResponseMeta();
            return Encoding.UTF8.GetBytes(ZeroResponse);
        }

        var completed = finishReason is not null;
        finishReason ??= "stop";

        var response = new CompletionStreamAggregate
        {
            Id = id,
            // Go leaves the unified response's Choices nil; it marshals as null.
            Choices = null,
            Object = "text_completion",
            Created = created,
            Model = model,
            Usage = usage,
            Completion = new CompletionResponse
            {
                Choices =
                [
                    new CompletionChoice
                    {
                        Text = accumulatedText.ToString(),
                        Index = 0,
                        FinishReason = finishReason,
                    },
                ],
            },
            RequestType = RequestTypes.Completion,
            ApiFormat = ApiFormats.OpenAICompletion,
        };

        meta = new ResponseMeta
        {
            Id = id,
            Usage = usage,
            Completed = completed,
        };

        return GoJson.Marshal(response);
    }

    private static bool StartsWith(byte[] data, string prefix)
        => data.Length >= prefix.Length
            && Encoding.UTF8.GetString(data, 0, prefix.Length) == prefix;

    /// <summary>
    /// The subset of llm.Response the reference marshals, with the Go struct's
    /// field order (usage before the sub-responses, request_type last).
    /// </summary>
    private sealed class CompletionStreamAggregate
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }

        [JsonPropertyName("object")]
        public string Object { get; set; } = string.Empty;

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("usage")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Usage? Usage { get; set; }

        [JsonPropertyName("completion")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CompletionResponse? Completion { get; set; }

        [JsonPropertyName("request_type")]
        public string RequestType { get; set; } = string.Empty;

        [JsonPropertyName("api_format")]
        public string ApiFormat { get; set; } = string.Empty;
    }
}
