using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/request_extensions.go: replays the
/// raw request fragments the unified model cannot represent so they survive the
/// inbound/outbound round trip.
/// </summary>
/// <remarks>
/// Go carries fragments as <c>json.RawMessage</c>. The port stores
/// <see cref="JsonElement"/> snapshots normalized with
/// <see cref="GoJson.CompactElement"/>, which is what Go produces when it
/// re-marshals a RawMessage (whitespace removed, HTML characters escaped), so
/// replay bytes match without a marshal-time pass.
/// </remarks>
internal static class ResponsesRequestExtensions
{
    /// <summary>Go: <c>rawCreateRequestFields</c>.</summary>
    public static readonly string[] RawCreateRequestFields =
    [
        "client_metadata",
        "context_management",
        "conversation",
        "moderation",
        "prompt",
        "prompt_cache_options",
    ];

    /// <summary>Go: <c>rawCompactRequestFields</c>.</summary>
    public static readonly string[] RawCompactRequestFields =
    [
        "previous_response_id",
        "prompt_cache_options",
        "prompt_cache_retention",
        "service_tier",
    ];

    /// <summary>
    /// Go: <c>additionalToolsInputItemType</c>. A private Codex construct used
    /// by Responses Lite: tool definitions travel inside a <c>developer</c>
    /// input item instead of the top-level <c>tools</c> array. OpenAI-compatible
    /// upstreams reject it, so it is replayed only when the caller preserved it.
    /// </summary>
    public const string AdditionalToolsInputItemType = "additional_tools";

    /// <summary>Go: <c>attachOpenAIResponsesRequestExtensions</c>.</summary>
    public static void AttachOpenAIResponsesRequestExtensions(LlmRequest? chatReq, ResponsesRequest? req, ReadOnlySpan<byte> rawBody)
    {
        if (chatReq is null || req is null)
        {
            return;
        }

        var raw = ParseRawRequestFragments(rawBody);
        var reasoningContext = req.Reasoning?.Context ?? string.Empty;
        var requestExt = new OpenAIResponsesRequestExtensions
        {
            ReasoningContext = reasoningContext,
            RawFields = SelectRawRequestFields(raw.Fields, RawCreateRequestFields),
            RawTools = BuildRawOnlyToolFragments(req.Tools, raw.Tools),
            ToolSignatures = BuildRepresentedToolSignatures(req.Tools),
            RawToolChoice = RawUnsupportedToolChoice(req.ToolChoice, raw.ToolChoice),
            RawInputItems = BuildRawOnlyInputFragments(req.Input, raw.InputItems),
        };

        if (requestExt.ReasoningContext.Length == 0
            && requestExt.RawFields is not { Count: > 0 }
            && requestExt.RawTools is not { Count: > 0 }
            && requestExt.RawToolChoice is null
            && requestExt.RawInputItems is not { Count: > 0 })
        {
            return;
        }

        var ext = OpenAIResponsesExtensions.EnsureOpenAIResponsesProviderExtensions(chatReq);
        if (ext is null)
        {
            return;
        }

        ext.Request = requestExt;
    }

    private sealed class RawRequestFragments
    {
        public Dictionary<string, JsonElement>? Fields { get; init; }

        public List<JsonElement>? Tools { get; init; }

        public JsonElement? ToolChoice { get; init; }

        public List<JsonElement>? InputItems { get; init; }
    }

    /// <summary>Go: <c>parseRawRequestFragments</c>.</summary>
    private static RawRequestFragments ParseRawRequestFragments(ReadOnlySpan<byte> rawBody)
    {
        if (rawBody.Length == 0)
        {
            return new RawRequestFragments();
        }

        JsonElement root;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody.ToArray());
            root = doc.RootElement;
        }
        catch (JsonException)
        {
            return new RawRequestFragments();
        }

        using (doc)
        {
            // Go decodes into a struct first and a map second; both require a
            // JSON object, and a tools type error discards every fragment.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new RawRequestFragments();
            }

            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                fields[property.Name] = CloneRaw(property.Value)!.Value;
            }

            List<JsonElement>? tools = null;
            if (root.TryGetProperty("tools", out var toolsElement) && toolsElement.ValueKind is not JsonValueKind.Null)
            {
                if (toolsElement.ValueKind != JsonValueKind.Array)
                {
                    return new RawRequestFragments();
                }

                tools = new List<JsonElement>(toolsElement.GetArrayLength());
                foreach (var element in toolsElement.EnumerateArray())
                {
                    tools.Add(CloneRaw(element)!.Value);
                }
            }

            JsonElement? toolChoice = null;
            if (root.TryGetProperty("tool_choice", out var toolChoiceElement))
            {
                toolChoice = CloneRaw(toolChoiceElement);
            }

            List<JsonElement>? inputItems = null;
            if (root.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Array)
            {
                inputItems = new List<JsonElement>(inputElement.GetArrayLength());
                foreach (var element in inputElement.EnumerateArray())
                {
                    inputItems.Add(CloneRaw(element)!.Value);
                }
            }

            return new RawRequestFragments
            {
                Fields = fields,
                Tools = tools,
                ToolChoice = toolChoice,
                InputItems = inputItems,
            };
        }
    }

    /// <summary>Go: <c>attachOpenAIResponsesRawRequestFields</c>.</summary>
    public static void AttachOpenAIResponsesRawRequestFields(LlmRequest? chatReq, ReadOnlySpan<byte> rawBody, string[] fieldNames)
    {
        if (chatReq is null || rawBody.Length == 0)
        {
            return;
        }

        Dictionary<string, JsonElement>? fields = null;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(rawBody.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                fields[property.Name] = CloneRaw(property.Value)!.Value;
            }
        }

        var rawFields = SelectRawRequestFields(fields, fieldNames);
        if (rawFields is null)
        {
            return;
        }

        var ext = OpenAIResponsesExtensions.EnsureOpenAIResponsesProviderExtensions(chatReq);
        if (ext is null)
        {
            return;
        }

        ext.Request ??= new OpenAIResponsesRequestExtensions();
        ext.Request.RawFields ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in rawFields)
        {
            ext.Request.RawFields[key] = value;
        }
    }

    /// <summary>Go: <c>selectRawRequestFields</c>.</summary>
    public static Dictionary<string, JsonElement>? SelectRawRequestFields(Dictionary<string, JsonElement>? fields, string[] fieldNames)
    {
        if (fields is not { Count: > 0 } || fieldNames.Length == 0)
        {
            return null;
        }

        var selected = new Dictionary<string, JsonElement>(fieldNames.Length, StringComparer.Ordinal);
        foreach (var name in fieldNames)
        {
            if (fields.TryGetValue(name, out var value))
            {
                selected[name] = CloneRaw(value)!.Value;
            }
        }

        return selected.Count == 0 ? null : selected;
    }

    /// <summary>Go: <c>buildRepresentedToolSignatures</c>.</summary>
    public static List<string>? BuildRepresentedToolSignatures(List<ResponsesTool>? tools)
    {
        if (tools is not { Count: > 0 })
        {
            return null;
        }

        var signatures = new List<string>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool.Type == "namespace")
            {
                var represented = new ResponsesTool { Type = "namespace", Name = tool.Name };
                if (tool.Tools is { } subTools)
                {
                    foreach (var subTool in subTools)
                    {
                        if (subTool.Type == "function")
                        {
                            represented.Tools ??= [];
                            represented.Tools.Add(subTool);
                        }
                    }
                }

                if (represented.Tools is { Count: > 0 })
                {
                    signatures.Add(ResponseToolSignature(represented));
                }

                continue;
            }

            if (!IsStructurallyRepresentedToolType(tool.Type))
            {
                continue;
            }

            signatures.Add(ResponseToolSignature(tool));
        }

        return signatures;
    }

    /// <summary>Go: <c>buildRawOnlyToolFragments</c>.</summary>
    public static List<OpenAIResponsesRawFragment>? BuildRawOnlyToolFragments(List<ResponsesTool>? tools, List<JsonElement>? rawTools)
    {
        if (tools is not { Count: > 0 })
        {
            return null;
        }

        var fragments = new List<OpenAIResponsesRawFragment>(tools.Count);
        for (var i = 0; i < tools.Count; i++)
        {
            if (rawTools is null || i >= rawTools.Count || IsStructurallyRepresentedToolType(tools[i].Type))
            {
                continue;
            }

            fragments.Add(new OpenAIResponsesRawFragment
            {
                Type = tools[i].Type ?? string.Empty,
                Name = tools[i].Name ?? string.Empty,
                OriginalIndex = i,
                RepresentedToolCount = RepresentedNamespaceToolCount(tools[i]),
                Raw = rawTools[i],
            });
        }

        return fragments;
    }

    /// <summary>Go: <c>representedNamespaceToolCount</c>.</summary>
    public static int RepresentedNamespaceToolCount(ResponsesTool tool)
    {
        if (tool.Type != "namespace")
        {
            return 0;
        }

        // A namespace is now represented by one grouped Responses tool.
        if (tool.Tools is { } subTools)
        {
            foreach (var subTool in subTools)
            {
                if (subTool.Type == "function")
                {
                    return 1;
                }
            }
        }

        return 0;
    }

    /// <summary>Go: <c>isStructurallyRepresentedToolType</c>.</summary>
    public static bool IsStructurallyRepresentedToolType(string? toolType)
        => toolType is "function" or "image_generation" or "web_search" or "custom";

    /// <summary>Go: <c>responseToolSignature</c>.</summary>
    public static string ResponseToolSignature(ResponsesTool tool)
    {
        switch (tool.Type)
        {
            case "namespace":
                var names = new List<string> { tool.Name ?? string.Empty };
                if (tool.Tools is { } subTools)
                {
                    foreach (var subTool in subTools)
                    {
                        names.Add(subTool.Type ?? string.Empty);
                        names.Add(subTool.Name ?? string.Empty);
                    }
                }

                return "namespace:" + JsonSerializer.Serialize(names, LlmJson.Options);
            case "function":
            case "custom":
                return tool.Type + ":" + (tool.Name ?? string.Empty);
            default:
                return tool.Type ?? string.Empty;
        }
    }

    /// <summary>Go: <c>rawUnsupportedToolChoice</c>.</summary>
    public static JsonElement? RawUnsupportedToolChoice(ResponsesToolChoice? choice, JsonElement? rawChoice)
    {
        if (choice is null || rawChoice is null)
        {
            return null;
        }

        return choice.Tools is { Count: > 0 } ? CloneRaw(rawChoice) : null;
    }

    /// <summary>Go: <c>buildRawOnlyInputFragments</c>.</summary>
    public static List<OpenAIResponsesRawFragment>? BuildRawOnlyInputFragments(ResponsesInput? input, List<JsonElement>? rawItems)
    {
        if (input?.Items is not { Count: > 0 } inputItems)
        {
            return null;
        }

        var fragments = new List<OpenAIResponsesRawFragment>();
        for (var i = 0; i < inputItems.Count; i++)
        {
            var item = inputItems[i];
            if (rawItems is null || i >= rawItems.Count || IsStructurallyRepresentedInputItem(item.Type))
            {
                continue;
            }

            fragments.Add(new OpenAIResponsesRawFragment
            {
                Type = item.Type,
                Name = item.Name,
                CallId = item.CallId,
                OriginalIndex = i,
                Raw = rawItems[i],
            });
        }

        return fragments;
    }

    /// <summary>Go: <c>isStructurallyRepresentedInputItem</c>.</summary>
    public static bool IsStructurallyRepresentedInputItem(string? itemType)
        => itemType is "" or null or "message" or "input_text" or "input_image" or "function_call"
            or "function_call_output" or "custom_tool_call" or "custom_tool_call_output" or "reasoning"
            or "compaction" or "compaction_summary";

    /// <summary>Go: <c>openAIResponsesRequestExtensions</c>.</summary>
    public static OpenAIResponsesRequestExtensions? OpenAiResponsesRequestExtensions(LlmRequest? llmReq)
    {
        if (llmReq?.ProviderExtensions?.OpenAIResponses is null)
        {
            return null;
        }

        return llmReq.ProviderExtensions.OpenAIResponses.Request;
    }

    /// <summary>
    /// Go: <c>marshalRequestPayload</c>. Marshals the unified request and
    /// replays the raw fields, tools and input items the caller sent so they
    /// survive the round trip.
    /// </summary>
    public static byte[] MarshalRequestPayload(ResponsesRequest payload, LlmRequest? llmReq, bool preserveAdditionalTools)
    {
        var body = GoJson.Marshal(payload);

        var requestExt = OpenAiResponsesRequestExtensions(llmReq);
        if (requestExt is null)
        {
            return body;
        }

        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, LlmJson.Options)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        MergeRawRequestFields(obj, requestExt);

        var (tools, mergedTools) = MergeRawOnlyTools(
            obj.TryGetValue("tools", out var toolsElement) ? toolsElement : null, requestExt);
        if (mergedTools)
        {
            obj["tools"] = JsonSerializer.SerializeToElement(tools, LlmJson.Options);
        }

        if (requestExt.RawToolChoice is { } rawToolChoice && RawToolChoiceMatchesCurrentTools(rawToolChoice, payload.ToolChoice))
        {
            obj["tool_choice"] = CloneRaw(rawToolChoice)!.Value;
        }

        var (input, mergedInput) = MergeRawOnlyInputItems(
            obj.TryGetValue("input", out var inputElement) ? inputElement : null, requestExt, preserveAdditionalTools);
        if (mergedInput)
        {
            obj["input"] = JsonSerializer.SerializeToElement(input, LlmJson.Options);
        }

        return GoJson.Marshal(obj);
    }

    /// <summary>Go: <c>mergeRawRequestFields</c>.</summary>
    public static void MergeRawRequestFields(Dictionary<string, JsonElement>? obj, OpenAIResponsesRequestExtensions? requestExt)
    {
        if (obj is null || requestExt?.RawFields is not { Count: > 0 } rawFields)
        {
            return;
        }

        foreach (var (key, value) in rawFields)
        {
            if (!obj.ContainsKey(key) && value.ValueKind != JsonValueKind.Undefined)
            {
                obj[key] = CloneRaw(value)!.Value;
            }
        }
    }

    /// <summary>Go: <c>marshalCompactRequestPayload</c>.</summary>
    public static byte[] MarshalCompactRequestPayload(CompactApiRequest payload, LlmRequest? llmReq)
    {
        var body = GoJson.Marshal(payload);

        var requestExt = OpenAiResponsesRequestExtensions(llmReq);
        if (requestExt?.RawFields is not { Count: > 0 })
        {
            return body;
        }

        var obj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body, LlmJson.Options)
            ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        MergeRawRequestFields(obj, requestExt);

        return GoJson.Marshal(obj);
    }

    /// <summary>
    /// Go: <c>mergeRawOnlyInputItems</c>. Rebuilds the outgoing <c>input</c>
    /// array from the merged request: items rebuilt from messages and raw items
    /// that had no representation are interleaved back into the positions they
    /// were sent in. <c>additional_tools</c> is dropped unless the upstream
    /// speaks the private Responses Lite protocol.
    /// </summary>
    public static (List<JsonElement>? Items, bool Ok) MergeRawOnlyInputItems(
        JsonElement? structuredRaw, OpenAIResponsesRequestExtensions? requestExt, bool preserveAdditionalTools)
    {
        if (requestExt?.RawInputItems is not { Count: > 0 } fragments)
        {
            return (null, false);
        }

        if (!preserveAdditionalTools)
        {
            fragments = WithoutInputFragmentsOfType(fragments, AdditionalToolsInputItemType);
            if (fragments.Count == 0)
            {
                return (null, false);
            }
        }

        if (!TryParseStructuredArray(structuredRaw, out var structuredItems))
        {
            return (null, false);
        }

        var total = structuredItems.Count + fragments.Count;
        var items = new List<JsonElement>(total);
        var structuredIndex = 0;
        var rawByIndex = new Dictionary<int, JsonElement>();
        foreach (var fragment in fragments)
        {
            if (fragment.Raw is not { } raw || fragment.OriginalIndex < 0)
            {
                return (null, false);
            }

            rawByIndex[fragment.OriginalIndex] = raw;
        }

        for (var i = 0; i < total; i++)
        {
            if (rawByIndex.TryGetValue(i, out var raw))
            {
                items.Add(raw);
                continue;
            }

            if (structuredIndex >= structuredItems.Count)
            {
                return (null, false);
            }

            items.Add(structuredItems[structuredIndex]);
            structuredIndex++;
        }

        if (structuredIndex != structuredItems.Count)
        {
            return (null, false);
        }

        return (items, true);
    }

    /// <summary>
    /// Go: <c>withoutInputFragmentsOfType</c>. Kept fragments are reindexed to
    /// their position in the compacted sequence; the input list is not modified
    /// in place because it is owned by the request extensions and may be
    /// replayed on a retry.
    /// </summary>
    public static List<OpenAIResponsesRawFragment> WithoutInputFragmentsOfType(
        List<OpenAIResponsesRawFragment> fragments, string itemType)
    {
        var kept = new List<OpenAIResponsesRawFragment>(fragments.Count);
        var dropped = 0;

        foreach (var fragment in fragments)
        {
            if (fragment.Type == itemType)
            {
                dropped++;
                continue;
            }

            if (dropped > 0)
            {
                // Go mutates the range copy, so the stored fragment is untouched.
                var reindexed = CloneFragment(fragment);
                reindexed.OriginalIndex -= dropped;
                kept.Add(reindexed);
                continue;
            }

            kept.Add(fragment);
        }

        return kept;
    }

    /// <summary>Go: <c>mergeRawOnlyTools</c>.</summary>
    public static (List<JsonElement>? Tools, bool Ok) MergeRawOnlyTools(JsonElement? structuredRaw, OpenAIResponsesRequestExtensions? requestExt)
    {
        if (requestExt?.RawTools is not { Count: > 0 } rawFragments)
        {
            return (null, false);
        }

        if (!TryParseStructuredArray(structuredRaw, out var structuredTools))
        {
            return (null, false);
        }

        if (!StructuredToolSignaturesMatch(structuredTools, requestExt.ToolSignatures))
        {
            return (null, false);
        }

        var representedCount = 0;
        foreach (var fragment in rawFragments)
        {
            if (fragment.RepresentedToolCount < 0)
            {
                return (null, false);
            }

            representedCount += fragment.RepresentedToolCount;
        }

        if (representedCount > structuredTools.Count)
        {
            return (null, false);
        }

        var total = structuredTools.Count - representedCount + rawFragments.Count;
        var tools = new List<JsonElement>(total);
        var structuredIndex = 0;
        var rawByIndex = new Dictionary<int, OpenAIResponsesRawFragment>(rawFragments.Count);
        foreach (var fragment in rawFragments)
        {
            if (fragment.Raw is null || fragment.OriginalIndex < 0)
            {
                return (null, false);
            }

            rawByIndex[fragment.OriginalIndex] = fragment;
        }

        for (var i = 0; i < total; i++)
        {
            if (rawByIndex.TryGetValue(i, out var fragment))
            {
                tools.Add(fragment.Raw!.Value);
                structuredIndex += fragment.RepresentedToolCount;
                if (structuredIndex > structuredTools.Count)
                {
                    return (null, false);
                }

                continue;
            }

            if (structuredIndex >= structuredTools.Count)
            {
                return (null, false);
            }

            tools.Add(structuredTools[structuredIndex]);
            structuredIndex++;
        }

        if (structuredIndex != structuredTools.Count)
        {
            return (null, false);
        }

        return (tools, true);
    }

    /// <summary>Go: <c>structuredToolSignaturesMatch</c>.</summary>
    public static bool StructuredToolSignaturesMatch(List<JsonElement> structuredTools, List<string>? expected)
    {
        if (structuredTools.Count != (expected?.Count ?? 0))
        {
            return false;
        }

        for (var i = 0; i < structuredTools.Count; i++)
        {
            ResponsesTool tool;
            try
            {
                tool = structuredTools[i].Deserialize<ResponsesTool>(LlmJson.Options) ?? new ResponsesTool();
            }
            catch (JsonException)
            {
                return false;
            }

            if (ResponseToolSignature(tool) != expected![i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Go: <c>rawToolChoiceMatchesCurrentTools</c>.</summary>
    public static bool RawToolChoiceMatchesCurrentTools(JsonElement raw, ResponsesToolChoice? current)
    {
        ResponsesToolChoice rawChoice;
        try
        {
            rawChoice = raw.Deserialize<ResponsesToolChoice>(LlmJson.Options)!;
        }
        catch (JsonException)
        {
            return false;
        }

        if (rawChoice.Type == "allowed_tools" || current?.Type == "allowed_tools")
        {
            if (current is null)
            {
                return false;
            }

            // Matching only mode would restore a stale allowlist after a caller
            // changed its members or replaced it with an unrestricted string mode.
            var expected = JsonSerializer.SerializeToUtf8Bytes(rawChoice, LlmJson.Options);
            var actual = JsonSerializer.SerializeToUtf8Bytes(current, LlmJson.Options);
            return expected.AsSpan().SequenceEqual(actual);
        }

        if (current is null)
        {
            return true;
        }

        var currentSignature = ToolChoiceSignature(current);
        if (currentSignature.Length == 0)
        {
            return true;
        }

        return ToolChoiceSignature(rawChoice) == currentSignature;
    }

    /// <summary>Go: <c>toolChoiceSignature</c>.</summary>
    public static string ToolChoiceSignature(ResponsesToolChoice? choice)
    {
        if (choice is null)
        {
            return string.Empty;
        }

        if (choice.Mode is not null)
        {
            return "mode:" + choice.Mode;
        }

        if (choice.Type is not null && choice.Name is not null)
        {
            return "named:" + choice.Type + ":" + choice.Name;
        }

        if (choice.Tools is { Count: > 0 })
        {
            return "tools";
        }

        return string.Empty;
    }

    /// <summary>
    /// Go: <c>cloneRaw</c>. The stored snapshot is the compacted form Go
    /// produces when it marshals a RawMessage.
    /// </summary>
    public static JsonElement? CloneRaw(JsonElement? src)
        => src is { } element ? GoJson.CompactElement(element) : null;

    private static OpenAIResponsesRawFragment CloneFragment(OpenAIResponsesRawFragment fragment) => new()
    {
        Type = fragment.Type,
        Name = fragment.Name,
        CallId = fragment.CallId,
        OriginalIndex = fragment.OriginalIndex,
        RepresentedToolCount = fragment.RepresentedToolCount,
        Raw = fragment.Raw,
    };

    /// <summary>
    /// Go: <c>json.Unmarshal(structuredRaw, &amp;[]json.RawMessage)</c>. Absent
    /// and JSON null both decode as an empty list; anything but an array fails.
    /// </summary>
    private static bool TryParseStructuredArray(JsonElement? structuredRaw, out List<JsonElement> items)
    {
        items = [];
        if (structuredRaw is not { } raw || raw.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (raw.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var element in raw.EnumerateArray())
        {
            items.Add(element.Clone());
        }

        return true;
    }
}
