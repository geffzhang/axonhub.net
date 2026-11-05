using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Persistence;

namespace AxonHub.Api.Traces;

/// <summary>
/// Port of the trace segment builder
/// (internal/server/biz/trace.go:448-1919): <c>GetRootSegment</c>,
/// <c>GetTraceFirstSegment</c> and their span extraction helpers.
/// </summary>
/// <remarks>
/// <para>
/// The reference reads every request/response body and runs the inbound and
/// outbound transformers to recover message-shaped spans. The port performs the
/// same transformation through the ported transformer module; failures are
/// logged and skipped by the reference, so the port returns the segment without
/// the affected spans instead of throwing (difference C191).
/// </para>
/// <para>
/// The ent privacy policies are evaluated explicitly before the stores are
/// touched (C189): the Request query policy for the completed-request reads and
/// the Trace query policy for the previous-trace lookup.
/// </para>
/// </remarks>
public sealed class TraceSegmentService(
    ITraceAdminStore traces,
    IRequestSegmentStore requests,
    TracePrivacy privacy)
{
    /// <summary>Port of MaxConcurrentBodyLoads (trace.go:446).</summary>
    public const int MaxConcurrentBodyLoads = 10;

    private const string EmptyJson = "{}";

    /// <summary>Port of request.ExternalResponseBodyMarker (request.go:124).</summary>
    private const string ExternalResponseBodyMarker = "{\"_ext\":1}";

    /// <summary>
    /// Port of TraceService.GetRootSegment (trace.go:448-555).
    /// </summary>
    public async Task<TraceSegment?> GetRootSegmentAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        RequestAccessFilter? access;
        try
        {
            access = await privacy.EvaluateRequestQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to query requests: ", ex);
        }

        IReadOnlyList<RequestRecord> traceRequests;
        try
        {
            traceRequests = await requests.ListCompletedByTraceAsync(
                    traceDbId,
                    access?.ProjectId,
                    access?.VisibilityUserId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to query requests: ", ex);
        }

        if (traceRequests.Count == 0)
        {
            return null;
        }

        // The reference loads the previous trace's spans on the same errgroup
        // as the bodies, so a body failure cancels the lookup. The port keeps
        // the observable order (previous spans are only used after every body
        // loaded) and lets the lookup swallow its own errors like the reference.
        var previousSpansTask = GetPreviousTraceSpansAsync(traceDbId, access, cancellationToken);
        var loaded = new List<RequestRecord>(traceRequests.Count);
        foreach (var request in traceRequests)
        {
            try
            {
                LoadBodies(request);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw Wrap("failed to load request body: ", ex);
            }

            loaded.Add(request);
        }

        var previousSpans = await previousSpansTask.ConfigureAwait(false);

        var buildInfos = new List<SegmentBuildInfo>(loaded.Count);
        var toolCallIndex = new Dictionary<string, SegmentBuildInfo>(StringComparer.Ordinal);

        for (var i = 0; i < loaded.Count; i++)
        {
            var segment = RequestToSegment(loaded[i]);

            var info = new SegmentBuildInfo
            {
                Segment = segment,
                OriginSpans = Concat(segment.RequestSpans, segment.ResponseSpans),
                OriginRequestSpans = Concat(segment.RequestSpans, null),
                ProducedToolCallIds = ExtractProducedToolCallIds(segment.ResponseSpans),
                ConsumedToolCallIds = ExtractConsumedToolCallIds(segment.RequestSpans),
            };
            buildInfos.Add(info);

            if (i == 0)
            {
                // Deduplicate the first segment's request spans against the
                // previous trace in the same thread; the origin span lists are
                // deliberately left untouched (trace.go:517-528).
                if (previousSpans.Count > 0)
                {
                    segment.RequestSpans = DeduplicateSpansWithParent(
                        segment.RequestSpans, previousSpans);
                }

                foreach (var id in info.ProducedToolCallIds)
                {
                    toolCallIndex[id] = info;
                }

                continue;
            }

            var parent = FindSegmentParent(
                info,
                buildInfos.GetRange(0, i),
                toolCallIndex);
            segment.ParentId = parent.Segment.Id;
            parent.Segment.Children ??= [];
            parent.Segment.Children.Add(segment);

            segment.RequestSpans = DeduplicateSpansWithParent(
                segment.RequestSpans, parent.OriginSpans);

            foreach (var id in info.ProducedToolCallIds)
            {
                toolCallIndex[id] = info;
            }
        }

        return buildInfos[0].Segment;
    }

    /// <summary>
    /// Port of RequestService.GetTraceFirstSegment (request.go:1872-1897): the
    /// first completed request of a trace converted to a segment.
    /// </summary>
    public async Task<TraceSegment?> GetFirstSegmentAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        RequestAccessFilter? access;
        try
        {
            access = await privacy.EvaluateRequestQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to get first request for trace: ", ex);
        }

        RequestRecord? request;
        try
        {
            request = await requests.GetFirstCompletedByTraceAsync(
                    traceDbId,
                    access?.ProjectId,
                    access?.VisibilityUserId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to get first request for trace: ", ex);
        }

        if (request is null)
        {
            return null;
        }

        LoadBodies(request);
        return RequestToSegment(request);
    }

    /// <summary>Port of TraceService.GetFirstUserQuery (trace.go:149-164).</summary>
    public async Task<string?> GetFirstUserQueryAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        var segment = await GetFirstSegmentAsync(traceDbId, cancellationToken)
            .ConfigureAwait(false);
        return segment?.FirstUserQuery();
    }

    /// <summary>Port of TraceService.GetFirstText (trace.go:166-178).</summary>
    public async Task<string?> GetFirstTextAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        var segment = await GetFirstSegmentAsync(traceDbId, cancellationToken)
            .ConfigureAwait(false);
        return segment?.FirstText();
    }

    /// <summary>
    /// Port of TraceService.getPreviousTraceSpans (trace.go:560-609). Every
    /// failure returns no spans; the caller cannot distinguish them.
    /// </summary>
    private async Task<List<TraceSpan>> GetPreviousTraceSpansAsync(
        long traceDbId,
        RequestAccessFilter? requestAccess,
        CancellationToken cancellationToken)
    {
        try
        {
            long? traceScope;
            try
            {
                traceScope = await privacy.EvaluateQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return [];
            }

            var currentTrace = await traces.GetByIdAsync(traceDbId, traceScope, cancellationToken)
                .ConfigureAwait(false);
            if (currentTrace?.ThreadId is null or 0)
            {
                return [];
            }

            var previousTrace = await traces.GetPreviousTraceAsync(
                    currentTrace.ThreadId.Value,
                    traceDbId,
                    currentTrace.CreatedAt,
                    traceScope,
                    cancellationToken)
                .ConfigureAwait(false);
            if (previousTrace is null)
            {
                return [];
            }

            var lastRequest = await requests.GetLastCompletedByTraceAsync(
                    previousTrace.Id,
                    requestAccess?.ProjectId,
                    requestAccess?.VisibilityUserId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lastRequest is null)
            {
                return [];
            }

            LoadBodies(lastRequest);
            var segment = RequestToSegment(lastRequest);
            return Concat(segment.RequestSpans, segment.ResponseSpans);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Port of RequestService.LoadRequestBody (request.go:1532-1564). The port
    /// has no external object storage, so the stored column is authoritative
    /// (C191).
    /// </summary>
    public static string LoadRequestBody(RequestRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return string.IsNullOrEmpty(request.RequestBody) ? EmptyJson : request.RequestBody;
    }

    /// <summary>
    /// Port of RequestService.LoadResponseBody (request.go:1566-1591) plus
    /// sanitizeLoadedResponseBody (request.go:141-147).
    /// </summary>
    public static string LoadResponseBody(RequestRecord request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Status != "completed")
        {
            return EmptyJson;
        }

        var body = request.ResponseBody;
        if (string.IsNullOrEmpty(body))
        {
            return EmptyJson;
        }

        return IsExternalResponseBodyMarker(body) ? EmptyJson : body;
    }

    /// <summary>
    /// Port of RequestService.LoadRequestExecutionRequestBody
    /// (request.go:1727-1756). The port has no external object storage, so the
    /// stored column is authoritative (C191).
    /// </summary>
    public static string LoadRequestBody(RequestExecutionRecord execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return string.IsNullOrEmpty(execution.RequestBody) ? EmptyJson : execution.RequestBody;
    }

    /// <summary>
    /// Port of RequestService.LoadRequestExecutionResponseBody
    /// (request.go:1758-1791) plus sanitizeLoadedResponseBody. The port has no
    /// external object storage, so the stored column is authoritative (C191).
    /// </summary>
    public static string LoadResponseBody(RequestExecutionRecord execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (execution.Status != "completed")
        {
            return EmptyJson;
        }

        var body = execution.ResponseBody;
        if (string.IsNullOrEmpty(body))
        {
            return EmptyJson;
        }

        return IsExternalResponseBodyMarker(body) ? EmptyJson : body;
    }

    /// <summary>Port of isExternalResponseBodyMarker (request.go:128-138).</summary>
    public static bool IsExternalResponseBodyMarker(string body)
        => string.Equals(
            Encoding.UTF8.GetString(GoJson.Compact(Encoding.UTF8.GetBytes(body))),
            ExternalResponseBodyMarker,
            StringComparison.Ordinal);

    private static void LoadBodies(RequestRecord request)
    {
        request.RequestBody = LoadRequestBody(request);
        request.ResponseBody = LoadResponseBody(request);
    }

    /// <summary>Port of requestToSegment (trace.go:640-762).</summary>
    private static TraceSegment RequestToSegment(RequestRecord request)
    {
        var segment = new TraceSegment
        {
            Id = request.Id,
            Model = request.ModelId,
            StartTime = request.CreatedAt,
            EndTime = request.UpdatedAt,
            Duration = (long)(request.UpdatedAt - request.CreatedAt).TotalMilliseconds,
        };

        var requestSpans = new List<TraceSpan>();
        var responseSpans = new List<TraceSpan>();

        var requestBody = request.RequestBody;
        if (!string.IsNullOrEmpty(requestBody))
        {
            var apiFormat = request.Format;
            var idPrefix = "request-" + request.Id.ToString(CultureInfo.InvariantCulture);

            if (apiFormat == ApiFormats.OpenAIResponseCompact)
            {
                requestSpans.AddRange(ExtractSpansFromCompactRequestBody(requestBody, idPrefix));
            }
            else if (IsImageFormat(apiFormat))
            {
                requestSpans.AddRange(ExtractSpansFromImageRequestBody(requestBody, idPrefix));
            }
            else if (IsModerationFormat(apiFormat))
            {
                requestSpans.AddRange(ExtractSpansFromModerationRequestBody(requestBody, idPrefix));
            }
            else if (IsAlphaSearchFormat(apiFormat))
            {
                // Alpha search is an opaque provider payload; it is not
                // message-shaped.
            }
            else if (apiFormat == ApiFormats.OpenAIDecisions)
            {
                requestSpans.AddRange(ExtractSpansFromDecisionsRequestBody(requestBody, idPrefix));
            }
            else
            {
                var httpRequest = new HttpRequestData
                {
                    Body = Encoding.UTF8.GetBytes(requestBody),
                    // Ensure the gemini path format.
                    Path = request.ModelId + ":generateContent",
                    Headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Content-Type"] = ["application/json"],
                    },
                    TransformerMetadata = [],
                };

                var inbound = GetInboundTransformer(apiFormat);
                if (inbound is null)
                {
                    // Format has no message-shaped request body (e.g.
                    // embeddings); skip it instead of failing the trace.
                }
                else
                {
                    try
                    {
                        var llmRequest = inbound.TransformRequest(httpRequest);
                        requestSpans.AddRange(ExtractSpansFromMessages(
                            llmRequest.Messages, idPrefix));
                    }
                    catch (Exception)
                    {
                        // The reference logs a warning and returns the segment
                        // before assigning any spans.
                        return segment;
                    }
                }
            }
        }

        var responseBody = request.ResponseBody;
        if (!string.IsNullOrEmpty(responseBody))
        {
            var apiFormat = request.Format;
            var idPrefix = "response-" + request.Id.ToString(CultureInfo.InvariantCulture);

            if (apiFormat == ApiFormats.OpenAIResponseCompact)
            {
                List<TraceSpan>? compactSpans;
                Usage? usage;
                try
                {
                    (compactSpans, usage) = ExtractSpansFromCompactResponseBody(
                        responseBody, idPrefix);
                }
                catch (Exception)
                {
                    return segment;
                }

                if (compactSpans is not null)
                {
                    responseSpans.AddRange(compactSpans);
                }

                segment.Metadata = ExtractMetadataFromUsage(usage);
            }
            else if (IsModerationFormat(apiFormat))
            {
                responseSpans.AddRange(
                    ExtractSpansFromModerationResponseBody(responseBody, idPrefix));
            }
            else if (IsAlphaSearchFormat(apiFormat))
            {
                // Alpha search responses are provider-defined JSON without
                // usage/messages.
            }
            else if (apiFormat == ApiFormats.OpenAIDecisions)
            {
                var (decisionsSpans, decisionsUsage) =
                    ExtractSpansFromDecisionsResponseBody(responseBody, idPrefix);
                if (decisionsSpans is not null)
                {
                    responseSpans.AddRange(decisionsSpans);
                }

                segment.Metadata = ExtractMetadataFromDecisionsUsage(decisionsUsage);
            }
            else
            {
                var outbound = GetOutboundTransformer(apiFormat);
                if (outbound is null)
                {
                    // Format has no message-shaped response body.
                }
                else
                {
                    var httpRequest = new HttpRequestData { ApiFormat = request.Format };
                    if (IsImageFormat(apiFormat))
                    {
                        httpRequest.TransformerMetadata = new Dictionary<string, JsonElement>
                        {
                            ["model"] = JsonSerializer.SerializeToElement(request.ModelId),
                        };
                    }

                    var httpResponse = new HttpResponseData
                    {
                        Body = Encoding.UTF8.GetBytes(responseBody),
                        StatusCode = 200,
                        Headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Content-Type"] = ["application/json"],
                        },
                        Request = httpRequest,
                    };

                    LlmResponse unifiedResponse;
                    try
                    {
                        unifiedResponse = outbound.TransformResponse(httpResponse);
                    }
                    catch (Exception)
                    {
                        return segment;
                    }

                    segment.Metadata = ExtractMetadataFromResponse(unifiedResponse);
                    if (unifiedResponse.Choices.Count > 0
                        && unifiedResponse.Choices[0].Message is { } message)
                    {
                        responseSpans.AddRange(ExtractSpansFromMessage(message, idPrefix));
                    }
                }
            }
        }

        segment.RequestSpans = requestSpans;
        segment.ResponseSpans = responseSpans;
        return segment;
    }

    private static IInboundTransformer? GetInboundTransformer(string format)
        => format switch
        {
            ApiFormats.OpenAIChatCompletion => new OpenAiInboundTransformer(),
            ApiFormats.OpenAIResponse or ApiFormats.OpenAIResponseWebSocket
                => new ResponsesInboundTransformer(),
            ApiFormats.AnthropicMessage => new AnthropicInboundTransformer(),
            ApiFormats.GeminiContents => new GeminiInboundTransformer(),
            ApiFormats.OpenAIDecisions => new DecisionsInboundTransformer(),
            _ => null,
        };

    private static IOutboundTransformer? GetOutboundTransformer(string format)
        => format switch
        {
            ApiFormats.OpenAIChatCompletion
                or ApiFormats.OpenAIImageGeneration
                or ApiFormats.OpenAIImageEdit
                or ApiFormats.OpenAIImageVariation
                => OpenAiOutboundTransformer.Create("https://api.openai.com/v1", "dummy"),
            ApiFormats.OpenAIResponse or ApiFormats.OpenAIResponseWebSocket
                => ResponsesOutboundTransformer.Create("https://api.openai.com/v1", "dummy"),
            ApiFormats.AnthropicMessage
                => AnthropicOutboundTransformer.Create("https://api.anthropic.com", "dummy"),
            ApiFormats.GeminiContents
                => GeminiOutboundTransformer.Create(
                    "https://generativelanguage.googleapis.com", "dummy"),
            ApiFormats.OpenAIDecisions
                => DecisionsOutboundTransformer.Create("https://api.openai.com/v1", "dummy"),
            _ => null,
        };

    private static bool IsImageFormat(string format)
        => format is ApiFormats.OpenAIImageGeneration
            or ApiFormats.OpenAIImageEdit
            or ApiFormats.OpenAIImageVariation;

    private static bool IsModerationFormat(string format)
        => format == ApiFormats.OpenAIModeration;

    private static bool IsAlphaSearchFormat(string format)
        => format == ApiFormats.OpenAIAlphaSearch;

    /// <summary>Port of extractMetadataFromResponse (trace.go:1676-1682).</summary>
    private static TraceRequestMetadata? ExtractMetadataFromResponse(LlmResponse? response)
        => response is null ? null : ExtractMetadataFromUsage(response.Usage);

    /// <summary>Port of extractMetadataFromUsage (trace.go:1684-1704).</summary>
    private static TraceRequestMetadata? ExtractMetadataFromUsage(Usage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var metadata = new TraceRequestMetadata { TotalTokens = usage.TotalTokens };
        if (usage.PromptTokens > 0)
        {
            metadata.InputTokens = usage.PromptTokens;
        }

        if (usage.CompletionTokens > 0)
        {
            metadata.OutputTokens = usage.CompletionTokens;
        }

        if (usage.PromptTokensDetails is { CachedTokens: > 0 } details)
        {
            metadata.CachedTokens = details.CachedTokens;
        }

        return metadata;
    }

    /// <summary>Port of extractMetadataFromDecisionsUsage (trace.go:846-851).</summary>
    private static TraceRequestMetadata? ExtractMetadataFromDecisionsUsage(Usage? usage)
        => usage is null ? null : new TraceRequestMetadata { InputTokens = usage.PromptTokens };

    private static List<TraceSpan> ExtractSpansFromMessages(
        List<Message>? messages,
        string idPrefix)
    {
        var spans = new List<TraceSpan>();
        if (messages is null)
        {
            return spans;
        }

        for (var i = 0; i < messages.Count; i++)
        {
            spans.AddRange(ExtractSpansFromMessage(
                messages[i],
                idPrefix + "-" + i.ToString(CultureInfo.InvariantCulture)));
        }

        return spans;
    }

    /// <summary>Port of extractSpansFromMessage (trace.go:1230-1605).</summary>
    private static List<TraceSpan> ExtractSpansFromMessage(Message message, string idPrefix)
    {
        var spans = new List<TraceSpan>();
        var now = DateTime.UtcNow;

        string NextId(string kind) => idPrefix + "-" + kind + "-"
            + spans.Count.ToString(CultureInfo.InvariantCulture);

        if (message.ReasoningContent is { Length: > 0 } reasoning)
        {
            spans.Add(new TraceSpan
            {
                Id = NextId("reasoning"),
                Type = "thinking",
                StartTime = now,
                EndTime = now,
                Value = new TraceSpanValue
                {
                    Thinking = new TraceSpanThinking { Thinking = reasoning },
                },
            });
        }

        if (message.Audio is { } audio)
        {
            spans.Add(new TraceSpan
            {
                Id = NextId("audio"),
                Type = "audio",
                StartTime = now,
                EndTime = now,
                Value = new TraceSpanValue
                {
                    Audio = new TraceSpanAudio
                    {
                        Id = audio.Id ?? string.Empty,
                        Data = audio.Data ?? string.Empty,
                        Transcript = audio.Transcript ?? string.Empty,
                    },
                },
            });
        }

        if (message.Content?.Content is { Length: > 0 } content)
        {
            switch (message.Role)
            {
                case "system":
                    spans.Add(NewSystemInstructionSpan(
                        NextId("system_instruction"), now, content));
                    break;
                case "user":
                    spans.Add(NewUserQuerySpan(NextId("text"), now, content));
                    break;
                case "tool":
                    spans.Add(new TraceSpan
                    {
                        Id = NextId("text"),
                        Type = "tool_result",
                        StartTime = now,
                        EndTime = now,
                        Value = new TraceSpanValue
                        {
                            ToolResult = new TraceSpanToolResult
                            {
                                ToolCallId = message.ToolCallId ?? string.Empty,
                                Text = content,
                            },
                        },
                    });
                    break;
                default:
                    spans.Add(NewTextSpan(NextId("text"), now, content));
                    break;
            }
        }

        foreach (var part in message.Content?.MultipleContent ?? [])
        {
            switch (part.Type)
            {
                case "text":
                    if (part.Text is null)
                    {
                        continue;
                    }

                    switch (message.Role)
                    {
                        case "system":
                            spans.Add(NewSystemInstructionSpan(
                                NextId("system_instruction"), now, part.Text));
                            break;
                        case "user":
                            spans.Add(NewUserQuerySpan(NextId("text"), now, part.Text));
                            break;
                        case "tool":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("tool_result"),
                                Type = "tool_result",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    ToolResult = new TraceSpanToolResult
                                    {
                                        ToolCallId = message.ToolCallId ?? string.Empty,
                                        Text = part.Text,
                                    },
                                },
                            });
                            break;
                        default:
                            spans.Add(NewTextSpan(NextId("text"), now, part.Text));
                            break;
                    }

                    break;

                case "compaction_summary":
                {
                    var summary = string.Empty;
                    if (part.Compact is { } compact)
                    {
                        summary = CompactTextSummary(compact.EncryptedContent);
                        if (summary.Length == 0)
                        {
                            summary = CompactTextSummary(compact.Id);
                        }
                    }

                    if (summary.Length == 0)
                    {
                        continue;
                    }

                    spans.Add(new TraceSpan
                    {
                        Id = NextId("compaction_summary"),
                        Type = "compaction_summary",
                        StartTime = now,
                        EndTime = now,
                        Value = new TraceSpanValue
                        {
                            Text = new TraceSpanText { Text = summary },
                            Compaction = new TraceSpanCompaction { Summary = summary },
                        },
                    });
                    break;
                }

                case "compaction":
                {
                    if (part.Compact is not { } compact)
                    {
                        continue;
                    }

                    var summary = CompactTextSummary(compact.EncryptedContent);
                    if (summary.Length == 0)
                    {
                        summary = CompactTextSummary(compact.Id);
                    }

                    if (summary.Length == 0)
                    {
                        summary = "Compaction item";
                    }

                    spans.Add(new TraceSpan
                    {
                        Id = NextId("compaction"),
                        Type = "compaction",
                        StartTime = now,
                        EndTime = now,
                        Value = new TraceSpanValue
                        {
                            Text = new TraceSpanText { Text = summary },
                            Compaction = new TraceSpanCompaction { Summary = summary },
                        },
                    });
                    break;
                }

                case "image_url":
                {
                    if (part.ImageUrl is not { } imageUrl)
                    {
                        continue;
                    }

                    switch (message.Role)
                    {
                        case "user":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("image_url"),
                                Type = "user_image_url",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    UserImageURL = new TraceSpanUserImageUrl
                                    {
                                        URL = imageUrl.Url,
                                    },
                                },
                            });
                            break;
                        case "tool":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("image_url"),
                                Type = "tool_result",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    ToolResult = new TraceSpanToolResult
                                    {
                                        // Image URL in tool result - stored as output.
                                        Text = imageUrl.Url,
                                    },
                                },
                            });
                            break;
                        default:
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("image_url"),
                                Type = "image_url",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    ImageURL = new TraceSpanImageUrl { URL = imageUrl.Url },
                                },
                            });
                            break;
                    }

                    break;
                }

                case "video_url":
                {
                    if (part.VideoUrl is not { } videoUrl)
                    {
                        continue;
                    }

                    switch (message.Role)
                    {
                        case "user":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("video_url"),
                                Type = "user_video_url",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    UserVideoURL = new TraceSpanUserVideoUrl
                                    {
                                        URL = videoUrl.URL,
                                    },
                                },
                            });
                            break;
                        case "tool":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("video_url"),
                                Type = "tool_result",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    ToolResult = new TraceSpanToolResult
                                    {
                                        Text = videoUrl.URL,
                                    },
                                },
                            });
                            break;
                        default:
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("video_url"),
                                Type = "video_url",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    VideoURL = new TraceSpanVideoUrl { URL = videoUrl.URL },
                                },
                            });
                            break;
                    }

                    break;
                }

                case "input_audio":
                {
                    if (part.InputAudio is not { } inputAudio)
                    {
                        continue;
                    }

                    switch (message.Role)
                    {
                        case "user":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("input_audio"),
                                Type = "user_input_audio",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    UserInputAudio = new TraceSpanUserInputAudio
                                    {
                                        Format = inputAudio.Format,
                                        Data = inputAudio.Data,
                                    },
                                },
                            });
                            break;
                        case "tool":
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("input_audio"),
                                Type = "tool_result",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    ToolResult = new TraceSpanToolResult
                                    {
                                        Text = "[audio input: " + inputAudio.Format + "]",
                                    },
                                },
                            });
                            break;
                        default:
                            spans.Add(new TraceSpan
                            {
                                Id = NextId("input_audio"),
                                Type = "audio",
                                StartTime = now,
                                EndTime = now,
                                Value = new TraceSpanValue
                                {
                                    Audio = new TraceSpanAudio
                                    {
                                        Format = inputAudio.Format,
                                        Data = inputAudio.Data,
                                    },
                                },
                            });
                            break;
                    }

                    break;
                }

                default:
                    // ignore for now.
                    break;
            }
        }

        foreach (var toolCall in message.ToolCalls ?? [])
        {
            var toolId = toolCall.Id ?? string.Empty;
            var toolType = toolCall.Type ?? string.Empty;
            var toolName = toolCall.Function.Name;
            var toolArgs = toolCall.Function.Arguments;

            if (toolCall.ResponseCustomToolCall is { } custom)
            {
                toolId = custom.CallId;
                toolName = custom.Name;
                toolArgs = custom.Input;
            }

            spans.Add(new TraceSpan
            {
                Id = NextId("tool"),
                Type = "tool_use",
                StartTime = now,
                EndTime = now,
                Value = new TraceSpanValue
                {
                    ToolUse = new TraceSpanToolUse
                    {
                        Id = toolId,
                        Type = toolType,
                        Name = toolName,
                        Arguments = toolArgs,
                    },
                },
            });
        }

        return spans;
    }

    private static TraceSpan NewSystemInstructionSpan(string id, DateTime now, string text)
        => new()
        {
            Id = id,
            Type = "system_instruction",
            StartTime = now,
            EndTime = now,
            Value = new TraceSpanValue
            {
                SystemInstruction = new TraceSpanSystemInstruction { Instruction = text },
            },
        };

    private static TraceSpan NewUserQuerySpan(string id, DateTime now, string text)
        => new()
        {
            Id = id,
            Type = "user_query",
            StartTime = now,
            EndTime = now,
            Value = new TraceSpanValue
            {
                UserQuery = new TraceSpanUserQuery { Text = text },
            },
        };

    private static TraceSpan NewTextSpan(string id, DateTime now, string text)
        => new()
        {
            Id = id,
            Type = "text",
            StartTime = now,
            EndTime = now,
            Value = new TraceSpanValue
            {
                Text = new TraceSpanText { Text = text },
            },
        };

    /// <summary>Port of extractSpansFromModerationRequestBody (trace.go:871-899).</summary>
    private static List<TraceSpan> ExtractSpansFromModerationRequestBody(
        string body,
        string idPrefix)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            string model;
            try
            {
                model = ReadString(root, "model", string.Empty);
            }
            catch (JsonException)
            {
                return [];
            }

            var summary = ModerationInputSummary(GetRawOrUndefined(root, "input"));
            if (summary.Length == 0)
            {
                return [];
            }

            if (model.Length > 0)
            {
                summary = model + ": " + summary;
            }

            var now = DateTime.UtcNow;
            return
            [
                new TraceSpan
                {
                    Id = idPrefix + "-moderation-input",
                    Type = "user_query",
                    StartTime = now,
                    EndTime = now,
                    Value = new TraceSpanValue
                    {
                        UserQuery = new TraceSpanUserQuery { Text = summary },
                    },
                },
            ];
        }
    }

    /// <summary>Port of moderationInputSummary (trace.go:963-1006).</summary>
    private static string ModerationInputSummary(JsonElement raw)
    {
        if (raw.ValueKind == JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        if (raw.ValueKind == JsonValueKind.String)
        {
            return raw.GetString() ?? string.Empty;
        }

        if (raw.ValueKind == JsonValueKind.Array)
        {
            var allStrings = true;
            var texts = new List<string>();
            foreach (var item in raw.EnumerateArray())
            {
                // Go decodes a null element into a string as the zero value
                // without failing the decode.
                if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                {
                    allStrings = false;
                    break;
                }

                texts.Add(item.ValueKind == JsonValueKind.String
                    ? item.GetString() ?? string.Empty
                    : string.Empty);
            }

            if (allStrings)
            {
                return string.Join(" | ", texts);
            }

            var items = new List<string>();
            foreach (var part in raw.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                {
                    return string.Empty;
                }

                string type;
                string text;
                JsonElement imageUrl;
                try
                {
                    type = ReadString(part, "type", string.Empty);
                    text = ReadString(part, "text", string.Empty);
                    imageUrl = GetRawOrUndefined(part, "image_url");
                }
                catch (JsonException)
                {
                    return string.Empty;
                }

                switch (type)
                {
                    case "text":
                        if (text.Length > 0)
                        {
                            items.Add(text);
                        }

                        break;
                    case "image_url":
                        if (imageUrl.ValueKind == JsonValueKind.Object)
                        {
                            string url;
                            try
                            {
                                url = ReadString(imageUrl, "url", string.Empty);
                            }
                            catch (JsonException)
                            {
                                return string.Empty;
                            }

                            if (url.Length > 0)
                            {
                                items.Add("image:" + url);
                            }
                        }
                        else if (imageUrl.ValueKind != JsonValueKind.Null
                                 && imageUrl.ValueKind != JsonValueKind.Undefined)
                        {
                            return string.Empty;
                        }

                        break;
                }
            }

            return string.Join(" | ", items);
        }

        return string.Empty;
    }

    /// <summary>
    /// Port of extractSpansFromModerationResponseBody (trace.go:901-961).
    /// </summary>
    private static List<TraceSpan> ExtractSpansFromModerationResponseBody(
        string body,
        string idPrefix)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            string model;
            JsonElement results;
            try
            {
                model = ReadString(root, "model", string.Empty);
                results = GetRawOrUndefined(root, "results");
            }
            catch (JsonException)
            {
                return [];
            }

            if (results.ValueKind == JsonValueKind.Null
                || results.ValueKind == JsonValueKind.Undefined)
            {
                return [];
            }

            if (results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                return [];
            }

            var parts = new List<string>();
            var index = 0;
            foreach (var result in results.EnumerateArray())
            {
                // A null element decodes into the zero result struct in Go.
                if (result.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                {
                    return [];
                }

                bool flagged;
                JsonElement categories;
                JsonElement scores;
                if (result.ValueKind == JsonValueKind.Null)
                {
                    flagged = false;
                    categories = default;
                    scores = default;
                }
                else
                {
                    try
                    {
                        flagged = ReadBoolean(result, "flagged", false);
                        categories = GetRawOrUndefined(result, "categories");
                        scores = GetRawOrUndefined(result, "category_scores");
                    }
                    catch (JsonException)
                    {
                        return [];
                    }
                }

                var flaggedCategories = new List<string>();
                if (categories.ValueKind == JsonValueKind.Object)
                {
                    foreach (var category in categories.EnumerateObject())
                    {
                        // Go decodes a null value into a bool as false.
                        if (category.Value.ValueKind is JsonValueKind.Null)
                        {
                            continue;
                        }

                        if (category.Value.ValueKind is not (
                                JsonValueKind.True or JsonValueKind.False))
                        {
                            return [];
                        }

                        if (category.Value.GetBoolean())
                        {
                            flaggedCategories.Add(category.Name);
                        }
                    }
                }
                else if (categories.ValueKind is not (
                             JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    return [];
                }

                if (scores.ValueKind == JsonValueKind.Object)
                {
                    foreach (var score in scores.EnumerateObject())
                    {
                        // Go decodes a null value into a float64 as zero.
                        if (score.Value.ValueKind is JsonValueKind.Null)
                        {
                            continue;
                        }

                        if (score.Value.ValueKind != JsonValueKind.Number)
                        {
                            return [];
                        }
                    }
                }
                else if (scores.ValueKind is not (
                             JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    return [];
                }

                flaggedCategories.Sort(StringComparer.Ordinal);

                var line = "result[" + index.ToString(CultureInfo.InvariantCulture)
                    + "] flagged=" + (flagged ? "true" : "false");
                if (flaggedCategories.Count > 0)
                {
                    line += " categories=" + string.Join(",", flaggedCategories);
                }

                parts.Add(line);
                index++;
            }

            var summary = string.Join("; ", parts);
            if (model.Length > 0)
            {
                summary = model + ": " + summary;
            }

            var now = DateTime.UtcNow;
            return
            [
                new TraceSpan
                {
                    Id = idPrefix + "-moderation-output",
                    Type = "text",
                    StartTime = now,
                    EndTime = now,
                    Value = new TraceSpanValue
                    {
                        Text = new TraceSpanText { Text = summary },
                    },
                },
            ];
        }
    }

    /// <summary>Port of extractSpansFromImageRequestBody (trace.go:1008-1065).</summary>
    private static List<TraceSpan> ExtractSpansFromImageRequestBody(
        string body,
        string idPrefix)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var spans = new List<TraceSpan>();
            var now = DateTime.UtcNow;
            var index = 0;

            if (root.TryGetProperty("prompt", out var prompt)
                && prompt.ValueKind == JsonValueKind.String
                && prompt.GetString() is { Length: > 0 } promptText)
            {
                spans.Add(new TraceSpan
                {
                    Id = idPrefix + "-prompt-" + index.ToString(CultureInfo.InvariantCulture),
                    Type = "user_query",
                    StartTime = now,
                    EndTime = now,
                    Value = new TraceSpanValue
                    {
                        UserQuery = new TraceSpanUserQuery { Text = promptText },
                    },
                });
                index++;
            }

            void AppendImageSpan(string url)
            {
                spans.Add(new TraceSpan
                {
                    Id = idPrefix + "-image-" + index.ToString(CultureInfo.InvariantCulture),
                    Type = "user_image_url",
                    StartTime = now,
                    EndTime = now,
                    Value = new TraceSpanValue
                    {
                        UserImageURL = new TraceSpanUserImageUrl { URL = url },
                    },
                });
                index++;
            }

            if (root.TryGetProperty("image", out var image))
            {
                if (image.ValueKind == JsonValueKind.String)
                {
                    AppendImageSpan(image.GetString() ?? string.Empty);
                }
                else if (image.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in image.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            AppendImageSpan(item.GetString() ?? string.Empty);
                        }
                    }
                }
            }

            if (root.TryGetProperty("mask", out var mask)
                && mask.ValueKind == JsonValueKind.String
                && mask.GetString() is { Length: > 0 } maskUrl)
            {
                AppendImageSpan(maskUrl);
            }

            return spans;
        }
    }

    /// <summary>
    /// Port of extractSpansFromCompactRequestBody (trace.go:1067-1110).
    /// </summary>
    private static List<TraceSpan> ExtractSpansFromCompactRequestBody(
        string body,
        string idPrefix)
    {
        CompactApiRequest? request;
        try
        {
            request = LlmJson.Deserialize<CompactApiRequest>(body);
        }
        catch (JsonException)
        {
            return [];
        }

        if (request is null)
        {
            return [];
        }

        var spans = new List<TraceSpan>();
        var now = DateTime.UtcNow;

        var summary = CompactInputSummary(request.Input);
        if (summary.Length > 0)
        {
            spans.Add(new TraceSpan
            {
                Id = idPrefix + "-compact-input",
                Type = "user_query",
                StartTime = now,
                EndTime = now,
                Value = new TraceSpanValue
                {
                    UserQuery = new TraceSpanUserQuery { Text = summary },
                    Compaction = new TraceSpanCompaction { Summary = summary },
                },
            });
        }

        if (request.Instructions is { Length: > 0 } instructions)
        {
            spans.Add(new TraceSpan
            {
                Id = idPrefix + "-compact-instructions",
                Type = "system_instruction",
                StartTime = now,
                EndTime = now,
                Value = new TraceSpanValue
                {
                    SystemInstruction = new TraceSpanSystemInstruction
                    {
                        Instruction = instructions,
                    },
                    Compaction = new TraceSpanCompaction
                    {
                        Summary = CompactTextSummary(instructions),
                    },
                },
            });
        }

        return spans;
    }

    /// <summary>
    /// Port of extractSpansFromCompactResponseBody (trace.go:1112-1140).
    /// </summary>
    private static (List<TraceSpan>? Spans, Usage? Usage) ExtractSpansFromCompactResponseBody(
        string body,
        string idPrefix)
    {
        // Go decodes a JSON null into the zero struct, yielding the
        // "Compaction completed" span without metadata.
        var response = LlmJson.Deserialize<CompactApiResponse>(body) ?? new CompactApiResponse();

        var now = DateTime.UtcNow;
        var summary = CompactOutputSummary(response.Output);
        if (summary.Length == 0)
        {
            summary = "Compaction completed";
        }

        var usage = response.Usage?.ToUsage();
        return (
            [
                new TraceSpan
                {
                    Id = idPrefix + "-compact-output",
                    Type = "text",
                    StartTime = now,
                    EndTime = now,
                    Value = new TraceSpanValue
                    {
                        Text = new TraceSpanText { Text = summary },
                        Compaction = new TraceSpanCompaction { Summary = summary },
                    },
                },
            ],
            usage);
    }

    /// <summary>Port of compactInputSummary (trace.go:1131-1166).</summary>
    private static string CompactInputSummary(ResponsesInput? input)
    {
        if (input?.Text is { } text)
        {
            return CompactTextSummary(text);
        }

        var items = input?.Items;
        if (items is not { Count: > 0 })
        {
            return string.Empty;
        }

        var texts = new List<string>();
        foreach (var item in items)
        {
            switch (item.Type)
            {
                case "message":
                    foreach (var part in item.GetContentItems() ?? [])
                    {
                        if ((part.Type == "input_text" || part.Type == "output_text"
                                || part.Type == "text")
                            && !string.IsNullOrEmpty(part.Text))
                        {
                            texts.Add(part.Text);
                        }
                    }

                    break;
                case "function_call":
                    if (item.Name.Length > 0)
                    {
                        texts.Add("Function call: " + item.Name);
                    }

                    break;
                case "function_call_output":
                case "custom_tool_call_output":
                    if (item.Output?.Text is { Length: > 0 } output)
                    {
                        texts.Add(output);
                    }

                    break;
            }
        }

        return CompactJoinSummary(texts, items.Count);
    }

    /// <summary>Port of compactOutputSummary (trace.go:1168-1194).</summary>
    private static string CompactOutputSummary(List<ResponsesItem>? items)
    {
        if (items is not { Count: > 0 })
        {
            return string.Empty;
        }

        var texts = new List<string>();
        foreach (var item in items)
        {
            switch (item.Type)
            {
                case "message":
                    foreach (var part in item.GetContentItems() ?? [])
                    {
                        if ((part.Type == "output_text" || part.Type == "text")
                            && !string.IsNullOrEmpty(part.Text))
                        {
                            texts.Add(part.Text);
                        }
                    }

                    break;
                case "output_text":
                case "summary_text":
                    if (item.Text is { Length: > 0 } text)
                    {
                        texts.Add(text);
                    }

                    break;
            }
        }

        return CompactJoinSummary(texts, items.Count);
    }

    /// <summary>Port of compactJoinSummary (trace.go:1196-1218).</summary>
    private static string CompactJoinSummary(List<string> texts, int itemCount)
    {
        var trimmed = new List<string>();
        foreach (var text in texts)
        {
            var summary = CompactTextSummary(text);
            if (summary.Length > 0)
            {
                trimmed.Add(summary);
            }
        }

        if (trimmed.Count == 0)
        {
            return itemCount > 0
                ? itemCount.ToString(CultureInfo.InvariantCulture) + " compact items"
                : string.Empty;
        }

        if (trimmed.Count == 1)
        {
            return trimmed[0];
        }

        return trimmed[0] + " (+"
            + (trimmed.Count - 1).ToString(CultureInfo.InvariantCulture) + " more)";
    }

    /// <summary>Port of compactTextSummary (trace.go:1220-1235).</summary>
    private static string CompactTextSummary(string? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        text = string.Join(" ", text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var runes = text.EnumerateRunes().ToArray();
        if (runes.Length <= 120)
        {
            return text;
        }

        return string.Concat(runes.Take(117).Select(static rune => rune.ToString())) + "...";
    }

    /// <summary>
    /// Port of extractSpansFromDecisionsRequestBody (trace.go:762-808).
    /// </summary>
    private static List<TraceSpan> ExtractSpansFromDecisionsRequestBody(
        string body,
        string idPrefix)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            // The Go wire struct also decodes `questions` as a raw-message
            // array, so a non-array/non-null value fails the whole decode.
            if (root.TryGetProperty("questions", out var questions)
                && questions.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                return [];
            }

            if (!root.TryGetProperty("input", out var input))
            {
                return [];
            }

            var spans = new List<TraceSpan>();
            if (input.ValueKind == JsonValueKind.String)
            {
                if (input.GetString() is { Length: > 0 } text)
                {
                    spans.Add(DecisionsUserQuerySpan(idPrefix, text));
                }

                return spans;
            }

            if (input.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return spans;
            }

            if (input.ValueKind != JsonValueKind.Array)
            {
                return spans;
            }

            foreach (var item in input.EnumerateArray())
            {
                // Go's json.Unmarshal into the item struct fails for non-object
                // elements, aborting the whole decode (no spans at all).
                if (item.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                {
                    return [];
                }

                if (item.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                string itemText;
                JsonElement content;
                try
                {
                    itemText = ReadString(item, "text", string.Empty);
                    // role/type are string fields of the Go struct, so a
                    // non-string value fails the whole item decode.
                    _ = ReadString(item, "role", string.Empty);
                    _ = ReadString(item, "type", string.Empty);
                    content = GetRawOrUndefined(item, "content");
                }
                catch (JsonException)
                {
                    return [];
                }

                if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var raw in content.EnumerateArray())
                    {
                        if (raw.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
                        {
                            continue;
                        }

                        if (raw.ValueKind == JsonValueKind.Null)
                        {
                            continue;
                        }

                        string type;
                        string text;
                        try
                        {
                            type = ReadString(raw, "type", string.Empty);
                            text = ReadString(raw, "text", string.Empty);
                            // image_url is a string field of the Go part
                            // struct; a non-string value skips the part.
                            _ = ReadString(raw, "image_url", string.Empty);
                        }
                        catch (JsonException)
                        {
                            continue;
                        }

                        switch (type)
                        {
                            case "input_text":
                                if (text.Length > 0)
                                {
                                    spans.Add(DecisionsUserQuerySpan(idPrefix, text));
                                }

                                break;
                            case "input_image":
                                spans.Add(DecisionsImageSpan(idPrefix));
                                break;
                        }
                    }
                }
                else if (content.ValueKind is not (
                             JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    // content must be an array; a type error aborts the decode.
                    return [];
                }

                if (itemText.Length > 0)
                {
                    spans.Add(DecisionsUserQuerySpan(idPrefix, itemText));
                }
            }

            return spans;
        }
    }

    /// <summary>
    /// Port of extractSpansFromDecisionsResponseBody (trace.go:810-844). A
    /// JSON null answer re-marshals to "null", matching Go's nil map marshal.
    /// </summary>
    private static (List<TraceSpan>? Spans, Usage? Usage) ExtractSpansFromDecisionsResponseBody(
        string body,
        string idPrefix)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return (null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var spans = new List<TraceSpan>();

            if (root.TryGetProperty("answers", out var answers)
                && answers.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (answers.ValueKind != JsonValueKind.Array)
                {
                    return (null, null);
                }

                foreach (var answer in answers.EnumerateArray())
                {
                    var encoded = TraceJson.RemarshalRawObject(answer);
                    if (encoded is null)
                    {
                        continue;
                    }

                    var now = DateTime.UtcNow;
                    spans.Add(new TraceSpan
                    {
                        Id = idPrefix + "-answer-"
                            + spans.Count.ToString(CultureInfo.InvariantCulture),
                        Type = "text",
                        StartTime = now,
                        EndTime = now,
                        Value = new TraceSpanValue
                        {
                            Text = new TraceSpanText { Text = encoded },
                        },
                    });
                }
            }

            Usage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement)
                && usageElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (usageElement.ValueKind != JsonValueKind.Object)
                {
                    return (null, null);
                }

                long inputTokens = 0;
                if (usageElement.TryGetProperty("input_tokens", out var tokens)
                    && tokens.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                {
                    // Go decodes the int64 from the number literal, so an
                    // exponent or fraction fails the whole body decode.
                    if (tokens.ValueKind != JsonValueKind.Number
                        || !TryReadGoInt64(tokens, out inputTokens))
                    {
                        return (null, null);
                    }
                }

                usage = new Usage { PromptTokens = inputTokens };
            }

            return (spans, usage);
        }
    }

    private static TraceSpan DecisionsUserQuerySpan(string idPrefix, string text)
    {
        var now = DateTime.UtcNow;
        return new TraceSpan
        {
            Id = idPrefix + "-text-" + UnixNano(now).ToString(CultureInfo.InvariantCulture),
            Type = "user_query",
            StartTime = now,
            EndTime = now,
            Value = new TraceSpanValue
            {
                UserQuery = new TraceSpanUserQuery { Text = text },
            },
        };
    }

    private static TraceSpan DecisionsImageSpan(string idPrefix)
    {
        var now = DateTime.UtcNow;
        return new TraceSpan
        {
            Id = idPrefix + "-image-" + UnixNano(now).ToString(CultureInfo.InvariantCulture),
            Type = "user_image_url",
            StartTime = now,
            EndTime = now,
            Value = new TraceSpanValue
            {
                UserImageURL = new TraceSpanUserImageUrl { URL = "[image]" },
            },
        };
    }

    /// <summary>Port of Go <c>time.Time.UnixNano</c> for the span ids.</summary>
    private static long UnixNano(DateTime utcNow)
        => (utcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    /// <summary>Port of extractProducedToolCallIDs (trace.go:1712-1726).</summary>
    private static HashSet<string> ExtractProducedToolCallIds(List<TraceSpan>? responseSpans)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in responseSpans ?? [])
        {
            if (span.Type == "tool_use"
                && span.Value?.ToolUse is { Id.Length: > 0 } toolUse)
            {
                ids.Add(toolUse.Id);
            }
        }

        return ids;
    }

    /// <summary>Port of extractConsumedToolCallIDs (trace.go:1728-1742).</summary>
    private static HashSet<string> ExtractConsumedToolCallIds(List<TraceSpan>? requestSpans)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in requestSpans ?? [])
        {
            if (span.Type == "tool_result"
                && span.Value?.ToolResult is { ToolCallId.Length: > 0 } toolResult)
            {
                ids.Add(toolResult.ToolCallId);
            }
        }

        return ids;
    }

    /// <summary>Port of findSegmentParent (trace.go:1743-1783).</summary>
    private static SegmentBuildInfo FindSegmentParent(
        SegmentBuildInfo current,
        List<SegmentBuildInfo> predecessors,
        Dictionary<string, SegmentBuildInfo> toolCallIndex)
    {
        if (current.ConsumedToolCallIds.Count > 0)
        {
            SegmentBuildInfo? latestProducer = null;
            foreach (var id in current.ConsumedToolCallIds)
            {
                if (toolCallIndex.TryGetValue(id, out var producer)
                    && (latestProducer is null
                        || producer.Segment.StartTime > latestProducer.Segment.StartTime))
                {
                    latestProducer = producer;
                }
            }

            if (latestProducer is not null)
            {
                return latestProducer;
            }
        }

        SegmentBuildInfo? bestMatch = null;
        var bestMatchLength = 0;
        foreach (var predecessor in predecessors)
        {
            var matchLength = CountCommonSpanPrefix(
                current.OriginRequestSpans, predecessor.OriginSpans);
            if (matchLength > bestMatchLength)
            {
                bestMatchLength = matchLength;
                bestMatch = predecessor;
            }
        }

        return bestMatch ?? predecessors[^1];
    }

    /// <summary>Port of countCommonSpanPrefix (trace.go:1785-1802).</summary>
    private static int CountCommonSpanPrefix(
        List<TraceSpan> current,
        List<TraceSpan> predecessor)
    {
        var maxLength = Math.Min(current.Count, predecessor.Count);
        var count = 0;
        for (var i = 0; i < maxLength; i++)
        {
            if (!string.Equals(
                    SpanToKey(current[i]), SpanToKey(predecessor[i]), StringComparison.Ordinal))
            {
                break;
            }

            count++;
        }

        return count;
    }

    /// <summary>Port of deduplicateSpansWithParent (trace.go:1804-1831).</summary>
    private static List<TraceSpan> DeduplicateSpansWithParent(
        List<TraceSpan>? current,
        List<TraceSpan>? parent)
    {
        if (current is not { Count: > 0 })
        {
            return current ?? [];
        }

        if (parent is not { Count: > 0 })
        {
            return current;
        }

        var result = new List<TraceSpan>(Math.Max(current.Count - parent.Count, 0));
        for (var i = 0; i < current.Count; i++)
        {
            if (i >= parent.Count)
            {
                result.Add(current[i]);
                continue;
            }

            if (string.Equals(
                    SpanToKey(current[i]), SpanToKey(parent[i]), StringComparison.Ordinal))
            {
                continue;
            }

            result.Add(current[i]);
        }

        return result;
    }

    /// <summary>Port of spanToKey (trace.go:1848-1919).</summary>
    private static string SpanToKey(TraceSpan span)
    {
        if (span.Value is not { } value)
        {
            return span.Type + ":";
        }

        switch (span.Type)
        {
            case "system_instruction":
                if (value.SystemInstruction is { } systemInstruction)
                {
                    return span.Type + ":" + systemInstruction.Instruction;
                }

                break;
            case "user_query":
                if (value.UserQuery is { } userQuery)
                {
                    return span.Type + ":" + userQuery.Text;
                }

                break;
            case "user_image_url":
                if (value.UserImageURL is { } userImage)
                {
                    return span.Type + ":" + userImage.URL;
                }

                break;
            case "user_video_url":
                if (value.UserVideoURL is { } userVideo)
                {
                    return span.Type + ":" + userVideo.URL;
                }

                break;
            case "user_input_audio":
                if (value.UserInputAudio is { } userInputAudio)
                {
                    return span.Type + ":" + userInputAudio.Format + ":" + userInputAudio.Data;
                }

                break;
            case "text":
                if (value.Text is { } text)
                {
                    return span.Type + ":" + text.Text;
                }

                break;
            case "thinking":
                if (value.Thinking is { } thinking)
                {
                    return span.Type + ":" + thinking.Thinking;
                }

                break;
            case "image_url":
                if (value.ImageURL is { } imageUrl)
                {
                    return span.Type + ":" + imageUrl.URL;
                }

                break;
            case "video_url":
                if (value.VideoURL is { } videoUrl)
                {
                    return span.Type + ":" + videoUrl.URL;
                }

                break;
            case "audio":
                if (value.Audio is { } audio)
                {
                    return span.Type + ":" + audio.Id + ":" + audio.Format + ":"
                        + audio.Transcript;
                }

                break;
            case "compaction":
            case "compaction_summary":
                if (value.Compaction is { } compaction)
                {
                    return span.Type + ":" + compaction.Summary;
                }

                break;
            case "tool_use":
                if (value.ToolUse is { } toolUse)
                {
                    var arguments = toolUse.Arguments is null
                        ? string.Empty
                        : TraceJson.NormalizeJson(toolUse.Arguments);
                    return span.Type + ":" + toolUse.Id + ":" + toolUse.Name + ":" + arguments;
                }

                break;
            case "tool_result":
                if (value.ToolResult is { } toolResult)
                {
                    var output = toolResult.Text ?? string.Empty;
                    return span.Type + ":" + toolResult.ToolCallId + ":"
                        + (toolResult.IsError ? "true" : "false") + ":" + output;
                }

                break;
        }

        return span.Type + ":";
    }

    private static List<TraceSpan> Concat(List<TraceSpan>? first, List<TraceSpan>? second)
    {
        var result = new List<TraceSpan>();
        if (first is { Count: > 0 })
        {
            result.AddRange(first);
        }

        if (second is { Count: > 0 })
        {
            result.AddRange(second);
        }

        return result;
    }

    private static string ReadString(JsonElement element, string name, string fallback)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new JsonException("expected string");
    }

    /// <summary>
    /// Decodes an integer the way Go's <c>encoding/json</c> decodes into
    /// <c>int64</c>: the raw literal must be digits with an optional leading
    /// minus, so a fraction or exponent fails.
    /// </summary>
    private static bool TryReadGoInt64(JsonElement element, out long value)
    {
        value = 0;
        var raw = element.GetRawText();
        if (raw.Length == 0)
        {
            return false;
        }

        foreach (var c in raw)
        {
            if (c is '.' or 'e' or 'E')
            {
                return false;
            }
        }

        return long.TryParse(
            raw,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool ReadBoolean(JsonElement element, string name, bool fallback)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new JsonException("expected boolean"),
        };
    }

    private static JsonElement GetRawOrUndefined(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value : default;

    private static InvalidOperationException Wrap(string prefix, Exception exception)
        => new(prefix + exception.Message, exception);

    private sealed class SegmentBuildInfo
    {
        public required TraceSegment Segment { get; init; }

        /// <summary>Original request + response spans (dedup and prefix target).</summary>
        public required List<TraceSpan> OriginSpans { get; init; }

        /// <summary>Original request spans only (prefix matching source).</summary>
        public required List<TraceSpan> OriginRequestSpans { get; init; }

        public required HashSet<string> ProducedToolCallIds { get; init; }

        public required HashSet<string> ConsumedToolCallIds { get; init; }
    }
}
