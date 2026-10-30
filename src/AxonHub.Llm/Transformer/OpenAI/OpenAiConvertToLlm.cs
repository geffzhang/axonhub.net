using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of llm/transformer/openai/inbound_convert.go — OpenAI wire model to the
/// unified model.
/// </summary>
public static class OpenAiConvertToLlm
{
    public const string TransformerMetadataKeyGoogleThoughtSignature = "google_thought_signature";

    public static LlmRequest? ToLlmRequest(this OpenAiRequest? r)
    {
        if (r is null)
        {
            return null;
        }

        var req = new LlmRequest
        {
            Model = r.Model,
            FrequencyPenalty = r.FrequencyPenalty,
            Logprobs = r.Logprobs,
            MaxCompletionTokens = r.MaxCompletionTokens,
            MaxTokens = r.MaxTokens,
            PresencePenalty = r.PresencePenalty,
            Seed = r.Seed,
            Store = r.Store,
            Temperature = r.Temperature,
            TopLogprobs = r.TopLogprobs,
            TopP = r.TopP,
            PromptCacheKey = r.PromptCacheKey,
            SafetyIdentifier = r.SafetyIdentifier,
            User = r.User,
            LogitBias = r.LogitBias,
            Metadata = r.Metadata,
            Modalities = r.Modalities,
            ReasoningEffort = r.ReasoningEffort,
            ReasoningBudget = r.ReasoningBudget,
            ReasoningSummary = r.ReasoningSummary,
            ServiceTier = r.ServiceTier,
            Stream = r.Stream,
            ParallelToolCalls = r.ParallelToolCalls,
            Verbosity = r.Verbosity,
        };

        req.Messages = (r.Messages ?? []).Select(m => m.ToLlmMessage()).ToList();

        if (r.Stop is not null)
        {
            req.Stop = new Stop
            {
                Single = r.Stop.Single,
                Multiple = r.Stop.Multiple,
            };
        }

        if (r.StreamOptions is not null)
        {
            req.StreamOptions = new StreamOptions
            {
                IncludeUsage = r.StreamOptions.IncludeUsage,
            };
        }

        // Go leaves the slice nil when the client sent no tools: `tools,omitempty`
        // drops a nil/empty slice from the wire, so an empty list would emit "[]".
        req.Tools = r.Tools is { Count: > 0 } tools ? tools.Select(t => t.ToLlmTool()).ToList() : null;

        req.ToolChoice = r.ToolChoice.ToLlmToolChoice();

        if (r.ResponseFormat is not null)
        {
            req.ResponseFormat = new ResponseFormat
            {
                Type = r.ResponseFormat.Type,
                JsonSchema = r.ResponseFormat.JsonSchema is { } schema ? GoJson.CompactElement(schema) : null,
            };
        }

        // Thinking is collapsed into reasoning_effort, matching the reference.
        if (r.Thinking is not null && r.Thinking.Type == "disabled")
        {
            req.ReasoningEffort = "none";
        }

        return req;
    }

    public static Tool ToLlmTool(this OpenAiTool t) => new()
    {
        Type = t.Type,
        Function = new Function
        {
            Name = t.Function.Name,
            Description = t.Function.Description,
            Parameters = t.Function.Parameters is { } p ? GoJson.CompactElement(p) : null,
            Strict = t.Function.Strict,
        },
    };

    public static ToolChoice? ToLlmToolChoice(this OpenAiToolChoice? t)
    {
        if (t is null)
        {
            return null;
        }

        var choice = new ToolChoice
        {
            ToolChoiceMode = t.ToolChoice,
        };

        if (t.NamedToolChoice is not null)
        {
            choice.NamedToolChoice = new NamedToolChoice
            {
                Type = t.NamedToolChoice.Type,
                Function = new ToolFunction { Name = t.NamedToolChoice.Function.Name },
            };
        }

        // An allowed_tools choice carries its mode in ToolChoiceMode and its
        // tool subset in Tools, matching the Responses convention.
        if (t.AllowedTools is not null)
        {
            choice.ToolChoiceMode = t.AllowedTools.Mode;
            choice.Tools = (t.AllowedTools.Tools ?? [])
                .Select(tool => new ToolOption { Type = tool.Type, Name = tool.Function.Name })
                .ToList();
        }

        return choice;
    }

    public static Message ToLlmMessage(this OpenAiMessage m)
    {
        var msg = new Message
        {
            Role = m.Role,
            Name = m.Name,
            Refusal = m.Refusal,
            ToolCallId = m.ToolCallId,
            ReasoningContent = m.ReasoningContent,
            Reasoning = m.Reasoning,
        };

        if (m.Audio is not null)
        {
            msg.Audio = new OutputAudio
            {
                Id = m.Audio.Id,
                Data = m.Audio.Data,
                ExpiresAt = m.Audio.ExpiresAt,
                Transcript = m.Audio.Transcript,
            };
        }

        // Sync reasoning fields: if one has a value and the other is nil, copy it.
        if (msg.ReasoningContent is null && m.Reasoning is { Length: > 0 })
        {
            msg.ReasoningContent = m.Reasoning;
        }

        if (msg.Reasoning is null && msg.ReasoningContent is { Length: > 0 })
        {
            msg.Reasoning = msg.ReasoningContent;
        }

        msg.Content = m.Content.ToLlmContent();

        if (m.ToolCalls is not null)
        {
            msg.ToolCalls = m.ToolCalls.Select(tc => tc.ToLlmToolCall()).ToList();

            var first = msg.ToolCalls.FirstOrDefault(tc =>
                tc.TransformerMetadata is not null
                && tc.TransformerMetadata.TryGetValue(TransformerMetadataKeyGoogleThoughtSignature, out var raw)
                && raw.ValueKind == System.Text.Json.JsonValueKind.String
                && raw.GetString() is { Length: > 0 });

            if (first?.TransformerMetadata is not null
                && first.TransformerMetadata.TryGetValue(TransformerMetadataKeyGoogleThoughtSignature, out var signature)
                && signature.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                msg.ReasoningSignature = signature.GetString();
            }
        }

        if (m.Annotations is { Count: > 0 })
        {
            msg.Annotations = m.Annotations.Select(a => a.ToLlmAnnotation()).ToList();
        }

        return msg;
    }

    public static ToolCall ToLlmToolCall(this OpenAiToolCall tc)
    {
        var toolCall = new ToolCall
        {
            Id = tc.Id,
            Type = tc.Type,
            Function = new FunctionCall
            {
                Name = tc.Function.Name ?? string.Empty,
                Arguments = tc.Function.Arguments,
            },
            Index = tc.Index,
        };

        var extraContent = tc.ExtraContent;
        if (extraContent is null && tc.ExtraFields is not null)
        {
            extraContent = tc.ExtraFields.ExtraContent;
        }

        if (extraContent?.Google is { ThoughtSignature.Length: > 0 } google)
        {
            toolCall.TransformerMetadata = new Dictionary<string, System.Text.Json.JsonElement>
            {
                [TransformerMetadataKeyGoogleThoughtSignature] = JetString(google.ThoughtSignature),
            };
        }

        return toolCall;
    }

    public static Annotation ToLlmAnnotation(this OpenAiAnnotation a)
    {
        var annotation = new Annotation
        {
            Type = a.Type,
            StartIndex = a.StartIndex,
            EndIndex = a.EndIndex,
        };

        if (a.UrlCitation is not null)
        {
            annotation.UrlCitation = new UrlCitation
            {
                Url = a.UrlCitation.Url,
                Title = a.UrlCitation.Title,
            };
        }

        return annotation;
    }

    public static MessageContent ToLlmContent(this OpenAiMessageContent? c)
    {
        var content = new MessageContent
        {
            Content = c?.Content,
        };

        if (c?.MultipleContent is not null)
        {
            content.MultipleContent = c.MultipleContent.Select(p => p.ToLlmPart()).ToList();
        }

        return content;
    }

    public static MessageContentPart ToLlmPart(this OpenAiMessageContentPart p)
    {
        var part = new MessageContentPart
        {
            Type = p.Type,
            Text = p.Text,
        };

        if (p.ImageUrl is not null)
        {
            part.ImageUrl = new ImageUrl
            {
                Url = p.ImageUrl.Url,
                Detail = p.ImageUrl.Detail,
            };
        }

        if (p.VideoUrl is not null)
        {
            part.VideoUrl = new VideoUrl
            {
                Url = p.VideoUrl.Url,
            };
        }

        if (p.InputAudio is not null)
        {
            part.InputAudio = new InputAudio
            {
                Format = p.InputAudio.Format,
                Data = p.InputAudio.Data,
            };
        }

        if (p.File is not null)
        {
            part.Type = "document";
            part.Document = new DocumentUrl
            {
                Url = p.File.FileData ?? string.Empty,
                FileId = p.File.FileId,
                Filename = p.File.Filename,
            };
        }

        return part;
    }

    /// <summary>Port of openai.Response.ToLLMResponse.</summary>
    public static LlmResponse? ToLlmResponse(this OpenAiResponse? r)
    {
        if (r is null)
        {
            return null;
        }

        var resp = new LlmResponse
        {
            Id = r.Id,
            Object = r.Object,
            Created = r.Created,
            Model = r.Model,
            SystemFingerprint = r.SystemFingerprint,
            ServiceTier = r.ServiceTier,
        };

        resp.Choices = (r.Choices ?? []).Select(c => c.ToLlmChoice()).ToList();

        if (r.Usage is not null)
        {
            resp.Usage = r.Usage.ToLlmUsage();
        }

        if (r.Error is not null)
        {
            resp.Error = new ResponseError
            {
                StatusCode = r.Error.StatusCode,
                Detail = r.Error.Detail,
            };
        }

        // Citations ride along in transformer metadata.
        if (r.Citations is { Count: > 0 })
        {
            resp.TransformerMetadata ??= new Dictionary<string, System.Text.Json.JsonElement>();
            resp.TransformerMetadata[TransformerMetadataKeyCitations] =
                System.Text.Json.JsonSerializer.SerializeToElement(r.Citations);
        }

        return resp;
    }

    public const string TransformerMetadataKeyCitations = "citations";

    /// <summary>Port of openai.Choice.ToLLMChoice.</summary>
    public static Choice ToLlmChoice(this OpenAiChoice c)
    {
        var choice = new Choice
        {
            Index = c.Index,
            FinishReason = c.FinishReason,
        };

        if (c.Message is not null)
        {
            choice.Message = c.Message.ToLlmMessage();
        }

        if (c.Delta is not null)
        {
            choice.Delta = c.Delta.ToLlmMessage();
        }

        choice.Logprobs = ToLlmLogprobs(c.Logprobs);

        return choice;
    }

    /// <summary>Port of toLLMLogprobs.</summary>
    public static LogprobsContent? ToLlmLogprobs(OpenAiLogprobs? lp)
    {
        if (lp is null)
        {
            return null;
        }

        return new LogprobsContent
        {
            Content = (lp.Content ?? []).Select(t => new TokenLogprob
            {
                Token = t.Token,
                Logprob = t.Logprob,
                Bytes = t.Bytes is { Count: > 0 } ? t.Bytes : null,
                TopLogprobs = t.TopLogprobs is { Count: > 0 } top
                    ? top.Select(tl => new TopLogprob
                    {
                        Token = tl.Token,
                        Logprob = tl.Logprob,
                        Bytes = tl.Bytes is { Count: > 0 } bytes ? bytes : null,
                    }).ToList()
                    : null,
            }).ToList(),
        };
    }

    internal static System.Text.Json.JsonElement JetString(string value)
        => System.Text.Json.JsonSerializer.SerializeToElement(value);
}
