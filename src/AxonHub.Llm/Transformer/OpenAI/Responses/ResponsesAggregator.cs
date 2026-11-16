using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of streamAggregator (aggregator.go:18-53): folds OpenAI Responses API
/// streaming events into one complete response body.
/// </summary>
internal sealed class ResponsesStreamAggregator
{
    /// <summary>
    /// Bounds provider-controlled content indexes before the aggregator expands
    /// its dense content-part storage (aggregator.go:15-17).
    /// </summary>
    public const int MaxAggregatedContentParts = 1024;

    private Dictionary<int, List<ResponsesAggregatedItem>> _outputItems = new();
    private readonly Dictionary<string, ResponsesAggregatedItem> _outputItemsById = new(StringComparer.Ordinal);

    public string ResponseId { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public long CreatedAt { get; private set; }

    public string Status { get; set; } = "in_progress";

    public string? PreviousResponseId { get; private set; }

    public ResponsesUsage? Usage { get; private set; }

    /// <summary>
    /// Port of streamAggregator.terminalSeen / terminalUsage (aggregator.go:36-41):
    /// usage delivered with a terminal response event is the provider's final
    /// measurement, unlike the in-progress snapshots.
    /// </summary>
    public bool TerminalSeen { get; private set; }

    public ResponsesUsage? TerminalUsage { get; private set; }

    /// <summary>Terminal error snapshot (Go: <c>responseError</c>).</summary>
    public ResponsesError? ResponseError { get; private set; }

    /// <summary>Terminal incomplete details (Go: <c>incompleteDetails</c>).</summary>
    public ResponsesResponseIncompleteDetails? IncompleteDetails { get; set; }

    /// <summary>Port of validAggregatedContentIndex.</summary>
    public static bool ValidAggregatedContentIndex(int contentIndex)
        => contentIndex >= 0 && contentIndex < MaxAggregatedContentParts;

    /// <summary>Port of ensureContentPart: a validated bounded dense slot.</summary>
    public static ResponsesAggregatedContentPart? EnsureContentPart(ResponsesAggregatedItem? item, int contentIndex)
    {
        if (item is null || !ValidAggregatedContentIndex(contentIndex))
        {
            return null;
        }

        while (item.Content.Count <= contentIndex)
        {
            item.Content.Add(new ResponsesAggregatedContentPart());
        }

        return item.Content[contentIndex];
    }

    /// <summary>Port of ensureSummaryPart.</summary>
    public static ResponsesAggregatedSummaryPart? EnsureSummaryPart(ResponsesAggregatedItem? item, int summaryIndex)
    {
        if (item is null)
        {
            return null;
        }

        if (item.SummaryParts.TryGetValue(summaryIndex, out var existing))
        {
            if (string.IsNullOrEmpty(existing.Type))
            {
                existing.Type = "summary_text";
            }

            return existing;
        }

        var part = new ResponsesAggregatedSummaryPart();
        item.SummaryParts[summaryIndex] = part;
        return part;
    }

    /// <summary>Port of applyDoneText (aggregator.go:150-160): Go compares byte lengths.</summary>
    public static void ApplyDoneText(StringBuilder dst, string? doneText)
    {
        if (string.IsNullOrEmpty(doneText))
        {
            return;
        }

        var existing = dst.ToString();
        if (existing.Length == 0 || Encoding.UTF8.GetByteCount(doneText) >= Encoding.UTF8.GetByteCount(existing))
        {
            dst.Clear();
            dst.Append(doneText);
        }
    }

    /// <summary>Port of streamAggregator.lastItemByOutputIndex.</summary>
    private ResponsesAggregatedItem? LastItemByOutputIndex(int outputIndex)
        => _outputItems.TryGetValue(outputIndex, out var items) && items.Count > 0
            ? items[^1]
            : null;

    /// <summary>Port of streamAggregator.getItemForEvent.</summary>
    private ResponsesAggregatedItem? GetItemForEvent(int outputIndex, string? itemId)
    {
        if (!string.IsNullOrEmpty(itemId))
        {
            if (_outputItemsById.TryGetValue(itemId, out var item))
            {
                return item;
            }

            // Some upstream implementations might use call_id as item_id in
            // delta events.
            foreach (var items in _outputItems.Values)
            {
                foreach (var it in items)
                {
                    if (string.Equals(it.CallId, itemId, StringComparison.Ordinal))
                    {
                        return it;
                    }
                }
            }
        }

        return LastItemByOutputIndex(outputIndex);
    }

    private ResponsesAggregatedItem CreateReasoningPlaceholder(int outputIndex, string? itemId)
    {
        var item = new ResponsesAggregatedItem
        {
            Type = "reasoning",
            Status = "in_progress",
        };

        if (!string.IsNullOrEmpty(itemId))
        {
            item.Id = itemId;
            _outputItemsById[item.Id] = item;
        }

        GetOrCreateOutputList(outputIndex).Add(item);
        return item;
    }

    private List<ResponsesAggregatedItem> GetOrCreateOutputList(int outputIndex)
    {
        if (!_outputItems.TryGetValue(outputIndex, out var items))
        {
            items = [];
            _outputItems[outputIndex] = items;
        }

        return items;
    }

    /// <summary>Port of streamAggregator.processEvent (aggregator.go:212-608).</summary>
    public void ProcessEvent(ResponsesStreamEvent ev)
    {
        switch (ev.Type)
        {
            case ResponsesStreamEventTypes.ResponseCreated:
            case ResponsesStreamEventTypes.ResponseInProgress:
                if (ev.Response is { } created)
                {
                    ResponseId = created.Id;
                    Model = created.Model;
                    CreatedAt = created.CreatedAt;
                    PreviousResponseId = created.PreviousResponseId;

                    if (created.Usage is not null)
                    {
                        Usage = created.Usage;
                    }
                }

                break;

            case ResponsesStreamEventTypes.OutputItemAdded:
                {
                    ResponsesAggregatedItem? item = null;
                    if (ev.Item is { Id.Length: > 0 } added && _outputItemsById.TryGetValue(added.Id, out var existing))
                    {
                        item = existing;
                    }

                    if (item is null)
                    {
                        item = new ResponsesAggregatedItem { Status = "in_progress" };
                        GetOrCreateOutputList(ev.OutputIndex).Add(item);
                    }

                    if (ev.Item is { } itemData)
                    {
                        item.Id = itemData.Id;
                        item.Type = itemData.Type;
                        item.Role = itemData.Role;
                        item.CallId = itemData.CallId;
                        item.Name = itemData.Name;
                        item.Namespace = itemData.Namespace;
                        item.Arguments.Append(itemData.Arguments);
                        item.EncryptedContent = itemData.EncryptedContent;
                        item.Input = itemData.Input;

                        if (itemData.Summary is { Count: > 0 } summary)
                        {
                            for (var idx = 0; idx < summary.Count; idx++)
                            {
                                var s = summary[idx];
                                var part = EnsureSummaryPart(item, idx)!;
                                part.Type = s.Type;
                                ApplyDoneText(part.Text, s.Text);
                                part.Final = true;
                            }
                        }
                    }

                    if (item.Id.Length > 0)
                    {
                        _outputItemsById[item.Id] = item;
                    }

                    break;
                }

            case ResponsesStreamEventTypes.ContentPartAdded:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId);
                    if (item is not null)
                    {
                        var contentPart = new ResponsesAggregatedContentPart();

                        if (ev.Part is { } part)
                        {
                            contentPart.Type = part.Type;
                            if (part.Text.Length > 0)
                            {
                                contentPart.Text.Append(part.Text);
                            }

                            if (part.Annotations is { Count: > 0 })
                            {
                                contentPart.Annotations.AddRange(part.Annotations);
                            }
                        }

                        item.Content.Add(contentPart);
                    }

                    break;
                }

            case ResponsesStreamEventTypes.OutputTextDelta:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId);
                    if (item is not null)
                    {
                        // The reference indexes item.Content[-1] and panics; this
                        // port treats a negative index as absent (difference C068).
                        if (ev.ContentIndex is { } ci && ci >= 0 && ci < item.Content.Count)
                        {
                            item.Content[ci].Text.Append(ev.Delta);
                        }
                    }

                    break;
                }

            case ResponsesStreamEventTypes.OutputTextDone:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId);
                    if (item is not null)
                    {
                        if (ev.ContentIndex is { } ci && ci >= 0 && ci < item.Content.Count && !string.IsNullOrEmpty(ev.Text))
                        {
                            ApplyDoneText(item.Content[ci].Text, ev.Text);
                        }
                    }

                    break;
                }

            case ResponsesStreamEventTypes.FunctionCallArgumentsDelta:
                if (ev.ItemId is not null)
                {
                    if (GetItemForEvent(ev.OutputIndex, ev.ItemId) is { } item)
                    {
                        item.Arguments.Append(ev.Delta);
                    }
                }

                break;

            case ResponsesStreamEventTypes.FunctionCallArgumentsDone:
                if (ev.ItemId is not null)
                {
                    if (GetItemForEvent(ev.OutputIndex, ev.ItemId) is { } item)
                    {
                        if (!string.IsNullOrEmpty(ev.Name))
                        {
                            item.Name = ev.Name;
                        }

                        if (!string.IsNullOrEmpty(ev.Namespace))
                        {
                            item.Namespace = ev.Namespace;
                        }

                        if (!string.IsNullOrEmpty(ev.Arguments))
                        {
                            // Replace accumulated arguments with the final version.
                            item.Arguments.Clear();
                            item.Arguments.Append(ev.Arguments);
                        }
                    }
                }

                break;

            case ResponsesStreamEventTypes.CustomToolCallInputDelta:
                if (ev.ItemId is not null)
                {
                    if (GetItemForEvent(ev.OutputIndex, ev.ItemId) is { } item)
                    {
                        item.Input = (item.Input ?? string.Empty) + ev.Delta;
                    }
                }

                break;

            case ResponsesStreamEventTypes.CustomToolCallInputDone:
                if (ev.ItemId is not null)
                {
                    if (GetItemForEvent(ev.OutputIndex, ev.ItemId) is { } item)
                    {
                        if (!string.IsNullOrEmpty(ev.Input))
                        {
                            item.Input = ev.Input;
                        }
                    }
                }

                break;

            case ResponsesStreamEventTypes.ReasoningSummaryPartAdded:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId)
                        ?? CreateReasoningPlaceholder(ev.OutputIndex, ev.ItemId);

                    var part = EnsureSummaryPart(item, ev.SummaryIndex ?? 0)!;

                    if (ev.Part is { } addedPart)
                    {
                        if (!string.IsNullOrEmpty(addedPart.Type))
                        {
                            part.Type = addedPart.Type;
                        }

                        if (addedPart.Text.Length > 0)
                        {
                            part.Text.Append(addedPart.Text);
                        }
                    }

                    break;
                }

            case ResponsesStreamEventTypes.ReasoningSummaryPartDone:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId)
                        ?? CreateReasoningPlaceholder(ev.OutputIndex, ev.ItemId);

                    var part = EnsureSummaryPart(item, ev.SummaryIndex ?? 0)!;

                    if (ev.Part is { } donePart)
                    {
                        if (!string.IsNullOrEmpty(donePart.Type))
                        {
                            part.Type = donePart.Type;
                        }

                        ApplyDoneText(part.Text, donePart.Text);
                    }

                    part.Final = true;
                    break;
                }

            case ResponsesStreamEventTypes.ReasoningSummaryTextDelta:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId)
                        ?? CreateReasoningPlaceholder(ev.OutputIndex, ev.ItemId);

                    var part = EnsureSummaryPart(item, ev.SummaryIndex ?? 0)!;
                    part.Text.Append(ev.Delta);
                    break;
                }

            case ResponsesStreamEventTypes.ReasoningSummaryTextDone:
                {
                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId)
                        ?? CreateReasoningPlaceholder(ev.OutputIndex, ev.ItemId);

                    var part = EnsureSummaryPart(item, ev.SummaryIndex ?? 0)!;
                    ApplyDoneText(part.Text, ev.Text);
                    part.Final = true;
                    break;
                }

            case ResponsesStreamEventTypes.ReasoningTextDelta:
                {
                    var contentIndex = ev.ContentIndex ?? 0;
                    if (!ValidAggregatedContentIndex(contentIndex))
                    {
                        return;
                    }

                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId)
                        ?? CreateReasoningPlaceholder(ev.OutputIndex, ev.ItemId);

                    var part = EnsureContentPart(item, contentIndex);
                    if (part is null)
                    {
                        return;
                    }

                    part.Type = "reasoning_text";
                    part.Text.Append(ev.Delta);
                    break;
                }

            case ResponsesStreamEventTypes.ReasoningTextDone:
                {
                    var contentIndex = ev.ContentIndex ?? 0;
                    if (!ValidAggregatedContentIndex(contentIndex))
                    {
                        return;
                    }

                    var item = GetItemForEvent(ev.OutputIndex, ev.ItemId)
                        ?? CreateReasoningPlaceholder(ev.OutputIndex, ev.ItemId);

                    var part = EnsureContentPart(item, contentIndex);
                    if (part is null)
                    {
                        return;
                    }

                    part.Type = "reasoning_text";
                    ApplyDoneText(part.Text, ev.Text);
                    break;
                }

            case ResponsesStreamEventTypes.OutputItemDone:
                {
                    // Mark the item as completed and update it with final data.
                    if (ev.Item is { } doneItem)
                    {
                        var item = _outputItemsById.TryGetValue(doneItem.Id, out var byId)
                            ? byId
                            : LastItemByOutputIndex(ev.OutputIndex);

                        if (item is not null)
                        {
                            if (doneItem.Status is { } status)
                            {
                                item.Status = status;
                            }

                            if (item.Status.Length == 0)
                            {
                                item.Status = "completed";
                            }

                            // Update with final data if provided.
                            if (!string.IsNullOrEmpty(doneItem.Arguments))
                            {
                                item.Arguments.Clear();
                                item.Arguments.Append(doneItem.Arguments);
                            }

                            if (doneItem.Content?.Items is { } contentItems)
                            {
                                for (var idx = 0; idx < contentItems.Count; idx++)
                                {
                                    var contentItem = contentItems[idx];
                                    var part = EnsureContentPart(item, idx);
                                    if (part is null)
                                    {
                                        continue;
                                    }

                                    if (!string.IsNullOrEmpty(contentItem.Type))
                                    {
                                        part.Type = contentItem.Type;
                                    }

                                    if (contentItem.Text is not null)
                                    {
                                        ApplyDoneText(part.Text, contentItem.Text);
                                    }

                                    if (contentItem.Annotations is not null)
                                    {
                                        part.Annotations.Clear();
                                        part.Annotations.AddRange(contentItem.Annotations);
                                    }
                                }
                            }

                            if (doneItem.Summary is { Count: > 0 } summary)
                            {
                                for (var idx = 0; idx < summary.Count; idx++)
                                {
                                    var s = summary[idx];
                                    var part = EnsureSummaryPart(item, idx)!;
                                    part.Type = s.Type;
                                    ApplyDoneText(part.Text, s.Text);
                                    part.Final = true;
                                }
                            }

                            if (doneItem.EncryptedContent is not null)
                            {
                                item.EncryptedContent = doneItem.EncryptedContent;
                            }

                            if (doneItem.Result is not null)
                            {
                                item.Result = doneItem.Result;
                            }
                        }
                    }

                    break;
                }

            case ResponsesStreamEventTypes.ResponseCompleted:
                TerminalSeen = true;
                TerminalUsage = ev.Response?.Usage;
                ApplyResponseSnapshot(ev.Response);

                // The terminal event wins over a missing or stale in-progress
                // status, while explicit failed, incomplete and canceled
                // outcomes are preserved.
                if (ev.Response is null
                    || ev.Response.Status is null
                    || Status.Length == 0
                    || Status == "in_progress")
                {
                    Status = "completed";
                }

                break;

            case ResponsesStreamEventTypes.ResponseFailed:
                TerminalSeen = true;
                TerminalUsage = ev.Response?.Usage;
                ApplyResponseSnapshot(ev.Response);
                if (ev.Response?.Status is null)
                {
                    Status = "failed";
                }

                break;

            case ResponsesStreamEventTypes.ResponseCancelled:
                TerminalSeen = true;
                TerminalUsage = ev.Response?.Usage;
                ApplyResponseSnapshot(ev.Response);
                if (ev.Response?.Status is null)
                {
                    Status = "canceled";
                }

                break;

            case ResponsesStreamEventTypes.ResponseIncomplete:
                TerminalSeen = true;
                TerminalUsage = ev.Response?.Usage;
                ApplyResponseSnapshot(ev.Response);
                if (ev.Response?.Status is null)
                {
                    Status = "incomplete";
                }

                break;
        }
    }

    /// <summary>Port of streamAggregator.applyResponseSnapshot (aggregator.go:610-639).</summary>
    private void ApplyResponseSnapshot(ResponsesResponse? response)
    {
        if (response is null)
        {
            return;
        }

        if (response.Id.Length > 0)
        {
            ResponseId = response.Id;
        }

        if (response.Model.Length > 0)
        {
            Model = response.Model;
        }

        if (response.CreatedAt != 0)
        {
            CreatedAt = response.CreatedAt;
        }

        if (response.PreviousResponseId is not null)
        {
            PreviousResponseId = response.PreviousResponseId;
        }

        if (response.Status is not null)
        {
            Status = response.Status;
        }

        if (response.Usage is not null)
        {
            Usage = response.Usage;
        }

        if (response.Error is not null)
        {
            ResponseError = response.Error;
        }

        if (response.IncompleteDetails is not null)
        {
            IncompleteDetails = response.IncompleteDetails;
        }
    }

    /// <summary>Port of streamAggregator.buildResponse (aggregator.go:641-790).</summary>
    public ResponsesResponse BuildResponse()
    {
        var output = new List<ResponsesItem>(_outputItems.Count);

        // Sort by output index.
        var maxIndex = 0;
        foreach (var idx in _outputItems.Keys)
        {
            if (idx > maxIndex)
            {
                maxIndex = idx;
            }
        }

        for (var i = 0; i <= maxIndex; i++)
        {
            if (!_outputItems.TryGetValue(i, out var items) || items.Count == 0)
            {
                continue;
            }

            foreach (var item in items)
            {
                switch (item.Type)
                {
                    case "message":
                        {
                            var contentItems = new List<ResponsesItem>(item.Content.Count);
                            foreach (var cp in item.Content)
                            {
                                contentItems.Add(new ResponsesItem
                                {
                                    Type = cp.Type,
                                    Text = cp.Text.ToString(),
                                    Annotations = cp.Annotations.Count > 0 ? [.. cp.Annotations] : null,
                                });
                            }

                            output.Add(new ResponsesItem
                            {
                                Id = item.Id,
                                Type = item.Type,
                                Role = item.Role,
                                Status = item.Status,
                                Content = new ResponsesInput { Items = contentItems },
                            });
                            break;
                        }

                    case "function_call":
                        output.Add(new ResponsesItem
                        {
                            Id = item.Id,
                            Type = item.Type,
                            Status = item.Status,
                            CallId = item.CallId,
                            Name = item.Name,
                            Namespace = item.Namespace,
                            Arguments = item.Arguments.ToString(),
                        });
                        break;

                    case "custom_tool_call":
                        output.Add(new ResponsesItem
                        {
                            Id = item.Id,
                            Type = item.Type,
                            Status = item.Status,
                            CallId = item.CallId,
                            Name = item.Name,
                            Input = item.Input,
                        });
                        break;

                    case "reasoning":
                        {
                            List<ResponsesReasoningSummary>? summary = null;
                            ResponsesInput? content = null;

                            if (item.SummaryParts.Count > 0)
                            {
                                var maxSummaryIndex = -1;
                                foreach (var idx in item.SummaryParts.Keys)
                                {
                                    if (idx > maxSummaryIndex)
                                    {
                                        maxSummaryIndex = idx;
                                    }
                                }

                                summary = new List<ResponsesReasoningSummary>(maxSummaryIndex + 1);
                                for (var idx = 0; idx <= maxSummaryIndex; idx++)
                                {
                                    if (!item.SummaryParts.TryGetValue(idx, out var sp))
                                    {
                                        summary.Add(new ResponsesReasoningSummary
                                        {
                                            Type = "summary_text",
                                            Text = string.Empty,
                                        });
                                        continue;
                                    }

                                    var summaryType = string.IsNullOrEmpty(sp.Type) ? "summary_text" : sp.Type;
                                    summary.Add(new ResponsesReasoningSummary
                                    {
                                        Type = summaryType,
                                        Text = sp.Text.ToString(),
                                    });
                                }
                            }

                            if (item.Content.Count > 0)
                            {
                                var contentItems = new List<ResponsesItem>(item.Content.Count);
                                foreach (var part in item.Content)
                                {
                                    var contentType = string.IsNullOrEmpty(part.Type) ? "reasoning_text" : part.Type;
                                    contentItems.Add(new ResponsesItem
                                    {
                                        Type = contentType,
                                        Text = part.Text.ToString(),
                                    });
                                }

                                content = new ResponsesInput { Items = contentItems };
                            }

                            output.Add(new ResponsesItem
                            {
                                Id = item.Id,
                                Type = item.Type,
                                Status = item.Status,
                                Summary = summary,
                                Content = content,
                                EncryptedContent = item.EncryptedContent,
                            });
                            break;
                        }

                    case "image_generation_call":
                        output.Add(new ResponsesItem
                        {
                            Id = item.Id,
                            Type = item.Type,
                            Status = item.Status,
                            CallId = item.CallId,
                            Result = item.Result,
                        });
                        break;

                    case "compaction":
                    case "compaction_summary":
                        output.Add(new ResponsesItem
                        {
                            Id = item.Id,
                            Type = item.Type,
                            EncryptedContent = item.EncryptedContent,
                        });
                        break;

                    default:
                        // Generic item.
                        output.Add(new ResponsesItem
                        {
                            Id = item.Id,
                            Type = item.Type,
                            Status = item.Status,
                            Role = item.Role,
                        });
                        break;
                }
            }
        }

        return new ResponsesResponse
        {
            Object = "response",
            Id = ResponseId,
            Model = Model,
            CreatedAt = CreatedAt,
            Status = Status,
            Output = output,
            Usage = Usage,
            PreviousResponseId = PreviousResponseId,
            Error = ResponseError,
            IncompleteDetails = IncompleteDetails,
        };
    }
}

/// <summary>Port of aggregatedItem (aggregator.go:55-78).</summary>
internal sealed class ResponsesAggregatedItem
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string CallId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Namespace { get; set; } = string.Empty;

    public StringBuilder Arguments { get; } = new();

    public string? EncryptedContent { get; set; }

    // For custom_tool_call type.
    public string? Input { get; set; }

    // For image_generation_call type.
    public string? Result { get; set; }

    // For message type.
    public List<ResponsesAggregatedContentPart> Content { get; } = [];

    // For reasoning type.
    public Dictionary<int, ResponsesAggregatedSummaryPart> SummaryParts { get; } = new();
}

/// <summary>Port of aggregatedSummaryPart (aggregator.go:80-84).</summary>
internal sealed class ResponsesAggregatedSummaryPart
{
    public string Type { get; set; } = "summary_text";

    public StringBuilder Text { get; } = new();

    public bool Final { get; set; }
}

/// <summary>Port of aggregatedContentPart (aggregator.go:86-90).</summary>
internal sealed class ResponsesAggregatedContentPart
{
    public string Type { get; set; } = string.Empty;

    public StringBuilder Text { get; } = new();

    public List<ResponsesAnnotation> Annotations { get; } = [];
}

/// <summary>
/// Port of the shared AggregateStreamChunks (aggregator.go:162-210). Both the
/// Responses inbound and outbound transformers use this.
/// </summary>
internal static class ResponsesAggregate
{
    public static byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent>? chunks, out ResponseMeta meta)
    {
        // Go's len(nil slice) is zero, so a null list is the nil-slice case.
        if (chunks is null || chunks.Count == 0)
        {
            throw new InvalidOperationException("empty stream chunks");
        }

        var agg = new ResponsesStreamAggregator();

        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.Data is not { Length: > 0 })
            {
                continue;
            }

            // Skip [DONE] marker.
            if (chunk.Data.AsSpan().SequenceEqual("[DONE]"u8))
            {
                continue;
            }

            ResponsesStreamEvent ev;
            try
            {
                ev = LlmJson.Deserialize<ResponsesStreamEvent>(chunk.Data)
                    ?? throw new JsonException("null event");
            }
            catch (JsonException)
            {
                continue;
            }

            agg.ProcessEvent(ev);
        }

        var resp = agg.BuildResponse();

        byte[] body;
        try
        {
            body = GoJson.Marshal(resp);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        meta = new ResponseMeta
        {
            Id = agg.ResponseId,
            Completed = agg.Status == "completed",
            AccountingEvidence = AccountingEvidence.Unavailable,
        };

        if (agg.Usage is not null)
        {
            meta.Usage = agg.Usage.ToUsage();
        }

        // The terminal snapshot overrides whatever the in-progress events
        // reported, and only it proves the usage final.
        if (agg.TerminalSeen && agg.TerminalUsage is not null)
        {
            meta.Usage = agg.TerminalUsage.ToUsage();
            meta.AccountingEvidence = AccountingEvidence.ExplicitFinal;
        }

        return body;
    }
}
