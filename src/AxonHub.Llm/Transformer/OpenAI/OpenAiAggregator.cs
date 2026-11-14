using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of llm/transformer/openai/aggregator.go: folds OpenAI streaming chunks
/// into one complete chat completion body. Used when a client asked for a
/// non-streaming response but the attempt had to stream upstream.
/// </summary>
public static class OpenAiAggregator
{
    /// <summary>Port of DefaultTransformChunk.</summary>
    public static OpenAiResponse DefaultTransformChunk(StreamEvent chunk)
    {
        try
        {
            return LlmJson.Deserialize<OpenAiResponse>(chunk.Data) ?? new OpenAiResponse();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>Port of AggregateStreamChunks.</summary>
    public static byte[] AggregateStreamChunks(
        IReadOnlyList<StreamEvent> chunks,
        Func<StreamEvent, OpenAiResponse> chunkTransformer,
        out ResponseMeta meta)
    {
        if (chunks.Count == 0)
        {
            meta = new ResponseMeta();
            return MarshalZeroResponse();
        }

        OpenAiResponse? lastChunkResponse = null;
        OpenAiUsage? usage = null;
        var usageEvidence = AccountingEvidence.Unavailable;
        var seenChoices = new HashSet<long>();
        var finishedChoices = new HashSet<long>();
        var zeroChoiceUsages = 0;
        var systemFingerprint = string.Empty;
        var serviceTier = string.Empty;
        var choicesAggs = new Dictionary<long, ChoiceAggregator>();
        var citationsMap = new HashSet<string>(StringComparer.Ordinal);

        foreach (var chunk in chunks)
        {
            // Skip [DONE] events.
            if (StartsWith(chunk.Data, "[DONE]"))
            {
                continue;
            }

            OpenAiResponse parsed;
            try
            {
                parsed = chunkTransformer(chunk);
            }
            catch (InvalidOperationException)
            {
                continue; // Skip invalid chunks.
            }

            foreach (var rawChoice in parsed.Choices ?? [])
            {
                // Go decodes a null element as the zero Choice struct.
                var choice = rawChoice ?? new OpenAiChoice();
                var choiceIndex = choice.Index;

                seenChoices.Add(choiceIndex);
                if (choice.FinishReason is not null)
                {
                    finishedChoices.Add(choiceIndex);
                }

                if (!choicesAggs.TryGetValue(choiceIndex, out var choiceAgg))
                {
                    choiceAgg = new ChoiceAggregator(choiceIndex);
                    choicesAggs[choiceIndex] = choiceAgg;
                }

                if (choice.Delta is { } delta)
                {
                    if (!string.IsNullOrEmpty(delta.Role))
                    {
                        choiceAgg.Role = delta.Role!;
                    }

                    if (delta.Content?.Content is not null)
                    {
                        choiceAgg.Content.Append(delta.Content.Content);
                    }

                    // Track presence separately from length so a semantically
                    // meaningful empty string survives on the aggregated message.
                    if (delta.ReasoningContent is not null)
                    {
                        choiceAgg.HasReasoningContent = true;
                        choiceAgg.ReasoningContent.Append(delta.ReasoningContent);
                    }

                    if (delta.Reasoning is not null)
                    {
                        choiceAgg.HasReasoning = true;
                        choiceAgg.Reasoning.Append(delta.Reasoning);
                    }

                    if (!string.IsNullOrEmpty(delta.Refusal))
                    {
                        choiceAgg.Refusal.Append(delta.Refusal);
                    }

                    // Audio arrives fragmented: ID and expiry on the first chunk,
                    // data and transcript across many.
                    if (delta.Audio is { } audio)
                    {
                        choiceAgg.Audio ??= new OutputAudio();
                        if (string.IsNullOrEmpty(choiceAgg.Audio.Id))
                        {
                            // Go: string with omitempty — an empty id stays out of the wire.
                            choiceAgg.Audio.Id = string.IsNullOrEmpty(audio.Id) ? null : audio.Id;
                        }

                        if ((choiceAgg.Audio.ExpiresAt ?? 0) == 0)
                        {
                            // Go: int64 with omitempty — a zero expiry stays out of the wire.
                            choiceAgg.Audio.ExpiresAt = audio.ExpiresAt is null or 0 ? null : audio.ExpiresAt;
                        }

                        choiceAgg.AudioData.Append(audio.Data);
                        choiceAgg.AudioTranscript.Append(audio.Transcript);
                    }

                    foreach (var deltaToolCall in delta.ToolCalls ?? [])
                    {
                        var toolCallIndex = deltaToolCall.Index;

                        if (!choiceAgg.ToolCalls.TryGetValue(toolCallIndex, out var toolCall))
                        {
                            toolCall = new ToolCall
                            {
                                Index = toolCallIndex,
                                Id = deltaToolCall.Id,
                                Type = deltaToolCall.Type,
                                Function = new FunctionCall
                                {
                                    Name = deltaToolCall.Function.Name ?? string.Empty,
                                    Arguments = string.Empty,
                                },
                            };
                            choiceAgg.ToolCalls[toolCallIndex] = toolCall;
                        }

                        if (!string.IsNullOrEmpty(deltaToolCall.Function.Arguments))
                        {
                            toolCall.Function.Arguments += deltaToolCall.Function.Arguments;
                        }

                        if (!string.IsNullOrEmpty(deltaToolCall.Function.Name))
                        {
                            toolCall.Function.Name = deltaToolCall.Function.Name;
                        }

                        if (!string.IsNullOrEmpty(deltaToolCall.Id))
                        {
                            toolCall.Id = deltaToolCall.Id;
                        }

                        if (!string.IsNullOrEmpty(deltaToolCall.Type))
                        {
                            toolCall.Type = deltaToolCall.Type;
                        }
                    }
                }

                choiceAgg.AddAnnotations(choice.Delta);
                choiceAgg.AddAnnotations(choice.Message);

                if (choice.Logprobs is { } logprobs)
                {
                    choiceAgg.Logprobs.AddRange(logprobs.Content ?? []);
                }

                if (choice.FinishReason is not null)
                {
                    choiceAgg.FinishReason = choice.FinishReason;
                }
            }

            if (parsed.Usage is not null)
            {
                usage = parsed.Usage;
                usageEvidence = AccountingEvidence.Unavailable;

                if (parsed.Choices is { Count: > 0 } && IsZeroUsage(parsed.Usage))
                {
                    zeroChoiceUsages++;
                }

                if (parsed.Choices is not { Count: > 0 } && AllChoicesFinished(seenChoices, finishedChoices))
                {
                    // A usage-only chunk after every choice finished cannot be a
                    // mid-stream snapshot: the provider closed the stream.
                    usageEvidence = AccountingEvidence.ExplicitFinal;
                }
                else if (parsed.Choices is { Count: > 0 } && AllChoicesFinished(seenChoices, finishedChoices))
                {
                    usageEvidence = AccountingEvidence.CleanEndCandidate;
                }
                else if (parsed.Choices is not { Count: > 0 } && seenChoices.Count > 0)
                {
                    // A trailing usage-only chunk after observed output is a
                    // clean-EOF candidate even when the provider omitted
                    // finish_reason.
                    usageEvidence = AccountingEvidence.CleanEndCandidate;
                }
            }
            else if (parsed.Choices is { Count: > 0 })
            {
                usageEvidence = AccountingEvidence.Unavailable;
            }

            foreach (var citation in parsed.Citations ?? [])
            {
                citationsMap.Add(citation);
            }

            if (systemFingerprint.Length == 0 && !string.IsNullOrEmpty(parsed.SystemFingerprint))
            {
                systemFingerprint = parsed.SystemFingerprint;
            }

            if (serviceTier.Length == 0 && !string.IsNullOrEmpty(parsed.ServiceTier))
            {
                serviceTier = parsed.ServiceTier;
            }

            // Keep the last chunk with choices for metadata; skip cost-only events
            // that would overwrite the real ID/Model/Created.
            if (parsed.Choices is { Count: > 0 })
            {
                lastChunkResponse = parsed;
            }
        }

        if (lastChunkResponse is null)
        {
            meta = new ResponseMeta();
            return MarshalZeroResponse();
        }

        var choiceIndexes = choicesAggs.Keys.ToList();
        choiceIndexes.Sort();

        var choices = new List<Choice>(choiceIndexes.Count);
        var completed = choiceIndexes.Count > 0;

        foreach (var choiceIndex in choiceIndexes)
        {
            var choiceAgg = choicesAggs[choiceIndex];

            List<ToolCall>? finalToolCalls = null;
            if (choiceAgg.ToolCalls.Count > 0)
            {
                var toolCallIndexes = choiceAgg.ToolCalls.Keys.ToList();
                toolCallIndexes.Sort();

                finalToolCalls = new List<ToolCall>(toolCallIndexes.Count);
                foreach (var toolCallIndex in toolCallIndexes)
                {
                    finalToolCalls.Add(choiceAgg.ToolCalls[toolCallIndex]);
                }
            }

            var message = new Message { Role = choiceAgg.Role };

            if (choiceAgg.HasReasoningContent)
            {
                message.ReasoningContent = choiceAgg.ReasoningContent.ToString();
            }

            if (choiceAgg.HasReasoning)
            {
                message.Reasoning = choiceAgg.Reasoning.ToString();
            }

            if (choiceAgg.Refusal.Length > 0)
            {
                message.Refusal = choiceAgg.Refusal.ToString();
            }

            if (choiceAgg.Audio is not null)
            {
                message.Audio = choiceAgg.Audio;
                // Go: strings with omitempty — empty fragments stay out of the wire.
                message.Audio.Data = NullIfEmpty(choiceAgg.AudioData.ToString());
                message.Audio.Transcript = NullIfEmpty(choiceAgg.AudioTranscript.ToString());
            }

            if (choiceAgg.Content.Length > 0)
            {
                message.Content = MessageContent.FromText(choiceAgg.Content.ToString());
            }

            if (finalToolCalls is { Count: > 0 })
            {
                message.ToolCalls = finalToolCalls;
            }

            if (choiceAgg.Annotations.Count > 0)
            {
                var annotations = choiceAgg.Annotations.Values.ToList();
                annotations.Sort(CompareAnnotations);
                message.Annotations = annotations;
            }

            string finishReason;
            if (choiceAgg.FinishReason is null)
            {
                completed = false;
                finishReason = finalToolCalls is { Count: > 0 } ? "tool_calls" : "stop";
            }
            else
            {
                finishReason = choiceAgg.FinishReason;
            }

            var choice = new Choice
            {
                Index = choiceIndex,
                Message = message,
                FinishReason = finishReason,
            };

            if (choiceAgg.Logprobs.Count > 0)
            {
                choice.Logprobs = OpenAiConvertToLlm.ToLlmLogprobs(new OpenAiLogprobs { Content = choiceAgg.Logprobs });
            }

            choices.Add(choice);
        }

        var responseUsage = usage.ToLlmUsage();

        // Repeated all-zero usage snapshots on choice chunks are the provider's
        // placeholder, not a final measurement: a clean end that reports the
        // placeholder more than once carries no trustworthy usage.
        if (usageEvidence == AccountingEvidence.CleanEndCandidate
            && zeroChoiceUsages > 1
            && IsZeroUsage(usage))
        {
            usageEvidence = AccountingEvidence.Unavailable;
        }

        var response = new LlmResponse
        {
            Id = lastChunkResponse.Id,
            Model = lastChunkResponse.Model,
            Object = "chat.completion", // Was "chat.completion.chunk".
            Created = lastChunkResponse.Created,
            // Go: strings with omitempty — an absent fingerprint/tier stays off the wire.
            SystemFingerprint = NullIfEmpty(systemFingerprint),
            ServiceTier = NullIfEmpty(serviceTier),
            Choices = choices,
            Usage = responseUsage,
        };

        var citations = citationsMap.ToList();
        citations.Sort(StringComparer.Ordinal);

        meta = new ResponseMeta
        {
            Id = response.Id,
            Usage = responseUsage,
            Completed = completed,
            AccountingEvidence = usageEvidence,
        };

        return WithCitations(GoJson.Marshal(response), citations);
    }

    /// <summary>Port of allChoicesFinished (aggregator.go:496-502).</summary>
    private static bool AllChoicesFinished(HashSet<long> seen, HashSet<long> finished)
        => seen.Count > 0 && seen.Count == finished.Count;

    /// <summary>Port of isZeroUsage (aggregator.go:504-506).</summary>
    private static bool IsZeroUsage(OpenAiUsage? usage)
        => usage is not null
            && usage.PromptTokens == 0
            && usage.CompletionTokens == 0
            && usage.TotalTokens == 0;

    /// <summary>
    /// Go: <c>json.Marshal(&llm.Response{})</c>. The zero response has a nil
    /// choices slice, which Go writes as <c>null</c> — not as <c>[]</c>.
    /// </summary>
    private static byte[] MarshalZeroResponse()
        => Encoding.UTF8.GetBytes("{\"id\":\"\",\"choices\":null,\"object\":\"\",\"created\":0,\"model\":\"\"}");

    /// <summary>
    /// Port of the <c>aggregatedChatResponse</c> wrapper: the embedded
    /// response's bytes with a trailing <c>citations</c> key when non-empty.
    /// Splicing keeps the response bytes untouched, exactly like embedding does.
    /// </summary>
    private static byte[] WithCitations(byte[] responseJson, List<string> citations)
    {
        if (citations.Count == 0)
        {
            return responseJson;
        }

        var sb = new StringBuilder(responseJson.Length + 64);
        sb.Append(Encoding.UTF8.GetString(responseJson));
        sb.Length -= 1; // Drop the closing brace.
        sb.Append(responseJson.Length > 2 ? ",\"citations\":[" : "\"citations\":[");

        for (var i = 0; i < citations.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(GoJson.MarshalToString(citations[i]));
        }

        sb.Append("]}");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int CompareAnnotations(Annotation left, Annotation right)
    {
        var (less, decided) = CompareOptionalAnnotationIndex(left.StartIndex, right.StartIndex);
        if (decided)
        {
            return less ? -1 : 1;
        }

        (less, decided) = CompareOptionalAnnotationIndex(left.EndIndex, right.EndIndex);
        if (decided)
        {
            return less ? -1 : 1;
        }

        var typeCompare = string.CompareOrdinal(left.Type ?? string.Empty, right.Type ?? string.Empty);
        if (typeCompare != 0)
        {
            return typeCompare;
        }

        return string.CompareOrdinal(AnnotationUrl(left), AnnotationUrl(right));
    }

    private static (bool Less, bool Decided) CompareOptionalAnnotationIndex(long? left, long? right)
        => (left, right) switch
        {
            (null, null) => (false, false),
            (null, _) => (false, true),
            (_, null) => (true, true),
            _ => left != right ? (left < right, true) : (false, false),
        };

    private static string AnnotationUrl(Annotation annotation) => annotation.UrlCitation?.Url ?? string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

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

    /// <summary>Port of openai.choiceAggregator.</summary>
    private sealed class ChoiceAggregator(long index)
    {
        public long Index { get; } = index;

        public StringBuilder Content { get; } = new();

        public StringBuilder ReasoningContent { get; } = new();

        public bool HasReasoningContent { get; set; }

        public StringBuilder Reasoning { get; } = new();

        public bool HasReasoning { get; set; }

        public StringBuilder Refusal { get; } = new();

        public OutputAudio? Audio { get; set; }

        public StringBuilder AudioData { get; } = new();

        public StringBuilder AudioTranscript { get; } = new();

        public List<OpenAiTokenLogprob> Logprobs { get; } = [];

        public Dictionary<long, ToolCall> ToolCalls { get; } = [];

        public string? FinishReason { get; set; }

        public string Role { get; set; } = "assistant";

        /// <summary>
        /// Go keeps annotations in a map and derives a stable key from
        /// type/url/start/end, so a repeated annotation is not duplicated.
        /// </summary>
        public Dictionary<string, Annotation> Annotations { get; } = new(StringComparer.Ordinal);

        public void AddAnnotations(OpenAiMessage? msg)
        {
            if (msg?.Annotations is not { Count: > 0 })
            {
                return;
            }

            foreach (var annotation in msg.Annotations)
            {
                if (annotation.UrlCitation is null || string.IsNullOrEmpty(annotation.UrlCitation.Url))
                {
                    continue;
                }

                var key = BuildAnnotationKey(annotation);
                var incoming = annotation.ToLlmAnnotation();

                if (Annotations.TryGetValue(key, out var existing))
                {
                    if (ShouldPreferIncomingAnnotationTitle(existing, incoming))
                    {
                        existing.UrlCitation!.Title = incoming.UrlCitation!.Title;
                    }

                    continue;
                }

                Annotations[key] = incoming;
            }
        }

        private static string BuildAnnotationKey(OpenAiAnnotation annotation)
        {
            var url = annotation.UrlCitation?.Url ?? string.Empty;
            var start = annotation.StartIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nil";
            var end = annotation.EndIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nil";

            return string.Join(
                "\0",
                annotation.Type ?? string.Empty,
                url,
                start,
                end);
        }

        private static bool ShouldPreferIncomingAnnotationTitle(Annotation existing, Annotation incoming)
        {
            if (existing.UrlCitation is null
                || incoming.UrlCitation is null
                || string.IsNullOrEmpty(incoming.UrlCitation.Title))
            {
                return false;
            }

            return string.IsNullOrEmpty(existing.UrlCitation.Title)
                || incoming.UrlCitation.Title.Length > existing.UrlCitation.Title.Length;
        }
    }
}
