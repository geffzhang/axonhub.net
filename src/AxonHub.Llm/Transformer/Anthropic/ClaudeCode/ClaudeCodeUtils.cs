using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

/// <summary>Port of llm/transformer/anthropic/claudecode/utils.go.</summary>
public static class ClaudeCodeUtils
{
    /// <summary>Identifies billing header system messages injected by the Claude CLI.</summary>
    public const string BillingHeaderPrefix = "x-anthropic-billing-header:";

    /// <summary>
    /// Port of <c>injectFakeUserIDStructured</c>: keeps an existing parsable
    /// user id, otherwise generates one and stores it in the unified metadata
    /// (which becomes Anthropic's <c>metadata.user_id</c>).
    /// </summary>
    public static LlmRequest InjectFakeUserId(
        LlmRequest llmRequest,
        string accountIdentity,
        string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(llmRequest);

        llmRequest.Metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);

        var existingUserId = llmRequest.Metadata.TryGetValue("user_id", out var value)
            ? value
            : string.Empty;

        if (existingUserId.Length == 0 || ClaudeCodeUserID.ParseUserId(existingUserId) is null)
        {
            llmRequest.Metadata["user_id"] =
                ClaudeCodeUserID.GenerateUserId(accountIdentity, sessionId);
        }

        return llmRequest;
    }

    /// <summary>
    /// Port of <c>disableThinkingIfToolChoiceForcedStructured</c>: Anthropic
    /// rejects thinking when tool_choice forces tool use.
    /// </summary>
    public static LlmRequest DisableThinkingIfToolChoiceForced(LlmRequest llmRequest)
    {
        ArgumentNullException.ThrowIfNull(llmRequest);

        var toolChoice = llmRequest.ToolChoice;
        if (toolChoice is null)
        {
            return llmRequest;
        }

        bool forcesToolUse;
        if (toolChoice.ToolChoiceMode is not null)
        {
            // The bare-string form, or the mode of an allowed_tools envelope.
            forcesToolUse = toolChoice.ToolChoiceMode == "any";
        }
        else if (toolChoice.NamedToolChoice is not null)
        {
            forcesToolUse = toolChoice.NamedToolChoice.Type == "tool";
        }
        else
        {
            forcesToolUse = false;
        }

        if (forcesToolUse && !string.IsNullOrEmpty(llmRequest.ReasoningEffort))
        {
            var requestCopy = llmRequest.ShallowClone();
            requestCopy.ReasoningEffort = null;
            requestCopy.ReasoningBudget = null;

            return requestCopy;
        }

        return llmRequest;
    }

    /// <summary>Port of <c>applyClaudeToolPrefixStructured</c>.</summary>
    public static LlmRequest ApplyClaudeToolPrefix(LlmRequest llmRequest, string prefix)
    {
        ArgumentNullException.ThrowIfNull(llmRequest);

        if (prefix.Length == 0)
        {
            return llmRequest;
        }

        var request = llmRequest.ShallowClone();

        // Go clones the tools slice; the nested Function is a value struct, so
        // the port clones the Tool and its Function before renaming.
        if (llmRequest.Tools is not null)
        {
            var tools = new List<Tool>(llmRequest.Tools.Count);
            foreach (var tool in llmRequest.Tools)
            {
                var cloned = tool.ShallowClone();
                cloned.Function = tool.Function.ShallowClone();
                if (!cloned.Function.Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    cloned.Function.Name = prefix + cloned.Function.Name;
                }

                tools.Add(cloned);
            }

            request.Tools = tools;
        }

        if (llmRequest.ToolChoice is { } toolChoice)
        {
            var choice = new ToolChoice
            {
                ToolChoiceMode = toolChoice.ToolChoiceMode,
                Tools = toolChoice.Tools,
            };

            if (toolChoice.NamedToolChoice is { } named)
            {
                // ToolFunction is a class in the port; Go copies the struct, so
                // the name is copied rather than shared.
                var namedCopy = new NamedToolChoice
                {
                    Type = named.Type,
                    Function = new ToolFunction { Name = named.Function.Name },
                };

                if (namedCopy.Type is "tool" or "function")
                {
                    var name = namedCopy.Function.Name;
                    if (name.Length > 0 && !name.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        namedCopy.Function.Name = prefix + name;
                    }
                }

                choice.NamedToolChoice = namedCopy;
            }

            request.ToolChoice = choice;
        }

        return request;
    }

    /// <summary>
    /// Port of <c>stripClaudeToolPrefixFromResponse</c>: removes the prefix from
    /// every tool_use block name in a non-streaming response body.
    /// </summary>
    /// <remarks>
    /// The reference reads with gjson and rewrites with sjson, which edits only
    /// the name value and leaves every other byte alone. The port scans the raw
    /// bytes with <see cref="Utf8JsonReader"/> and splices the same value spans,
    /// so the document's whitespace, ordering and number literals are preserved.
    /// </remarks>
    public static byte[] StripClaudeToolPrefixFromResponse(byte[] body, string prefix)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (prefix.Length == 0 || body.Length == 0)
        {
            return body;
        }

        var edits = new List<(int Start, int End, byte[] Replacement)>();

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
            });

            // gjson reads "content" from a top-level object only; anything else
            // leaves the body untouched.
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return body;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return body;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                var isContent = string.Equals(reader.GetString(), "content", StringComparison.Ordinal);
                if (!reader.Read())
                {
                    return body;
                }

                if (!isContent)
                {
                    SkipComposite(ref reader);
                    continue;
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    return body;
                }

                ScanContentArray(body, ref reader, prefix, edits);
                break;
            }
        }
        catch (JsonException)
        {
            // gjson reports a non-existent path on malformed input.
            return body;
        }

        return edits.Count == 0 ? body : ApplyEdits(body, edits);
    }

    /// <summary>
    /// Port of <c>stripClaudeToolPrefixFromStreamLine</c>: rewrites a decoded
    /// SSE chunk that starts a prefixed tool_use block.
    /// </summary>
    /// <remarks>
    /// Go decodes into <c>map[string]any</c> and re-marshals, which sorts the
    /// object keys; the port keeps the original key order. Downstream consumers
    /// parse the JSON, so only the shape matters (difference C096).
    /// </remarks>
    public static byte[] StripClaudeToolPrefixFromStreamLine(byte[] line, string prefix)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (prefix.Length == 0 || line.Length == 0)
        {
            return line;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return line;
        }

        if (node is not JsonObject root
            || root["content_block"] is not JsonObject contentBlock
            || contentBlock["type"] is not JsonValue typeValue
            || typeValue.GetValueKind() != JsonValueKind.String
            || typeValue.GetValue<string>() != "tool_use"
            || contentBlock["name"] is not JsonValue nameValue
            || nameValue.GetValueKind() != JsonValueKind.String)
        {
            return line;
        }

        var name = nameValue.GetValue<string>();
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return line;
        }

        contentBlock["name"] = name[prefix.Length..];

        return GoJson.Marshal(node);
    }

    /// <summary>Port of <c>mergeBetasIntoHeader</c>: ordered, de-duplicated union.</summary>
    public static string MergeBetasIntoHeader(string baseBetas, IEnumerable<string> extraBetas)
    {
        ArgumentNullException.ThrowIfNull(extraBetas);

        var parts = new List<string>();
        var existing = new HashSet<string>(StringComparer.Ordinal);

        baseBetas = baseBetas.Trim();
        if (baseBetas.Length > 0)
        {
            foreach (var part in baseBetas.Split(','))
            {
                var beta = part.Trim();
                if (beta.Length > 0)
                {
                    // The reference does not de-duplicate the base entries.
                    parts.Add(beta);
                    existing.Add(beta);
                }
            }
        }

        foreach (var part in extraBetas)
        {
            var beta = (part ?? string.Empty).Trim();
            if (beta.Length > 0 && existing.Add(beta))
            {
                parts.Add(beta);
            }
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// Port of <c>RemoveBillingSystemMessages</c>. The reference invokes this
    /// from an orchestrator middleware (internal/server/orchestrator/claude_code.go)
    /// that has no port pipeline hook yet, so the utility is ported and tested
    /// but not wired (difference C096). The returned request is a copy when
    /// anything is removed, so retries do not mutate the shared request.
    /// </summary>
    public static LlmRequest RemoveBillingSystemMessages(LlmRequest llmRequest)
    {
        ArgumentNullException.ThrowIfNull(llmRequest);

        if (llmRequest.Messages.Count == 0)
        {
            return llmRequest;
        }

        var filtered = new List<Message>(llmRequest.Messages.Count);
        var changed = false;

        foreach (var message in llmRequest.Messages)
        {
            if (message.Role is null
                || !message.Role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(message);
                continue;
            }

            var multipleContent = message.Content?.MultipleContent;
            if (multipleContent is null || multipleContent.Count == 0)
            {
                if (message.Content?.Content is not { } content || !IsBillingHeaderText(content))
                {
                    filtered.Add(message);
                    continue;
                }

                changed = true;
                continue;
            }

            var parts = new List<MessageContentPart>(multipleContent.Count);
            foreach (var part in multipleContent)
            {
                if (part.Type.Equals("text", StringComparison.OrdinalIgnoreCase)
                    && part.Text is { } text
                    && IsBillingHeaderText(text))
                {
                    changed = true;
                    continue;
                }

                parts.Add(part);
            }

            if (parts.Count == 0)
            {
                continue;
            }

            // Go assigns the filtered parts to its (value) copy of the message;
            // the port clones the message and its content to match.
            var filteredMessage = message.ShallowClone();
            filteredMessage.Content = new MessageContent
            {
                Content = message.Content?.Content,
                MultipleContent = parts,
            };

            filtered.Add(filteredMessage);
        }

        if (!changed)
        {
            return llmRequest;
        }

        var result = llmRequest.ShallowClone();
        result.Messages = filtered;

        return result;
    }

    /// <summary>Port of <c>isBillingHeaderText</c>.</summary>
    public static bool IsBillingHeaderText(string text)
        => text.Trim().ToLowerInvariant().StartsWith(BillingHeaderPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Port of <c>injectClaudeCodeSystemMessageStructured</c>: prepends the
    /// Claude Code system message with an ephemeral cache breakpoint.
    /// </summary>
    public static LlmRequest InjectClaudeCodeSystemMessage(LlmRequest llmRequest)
    {
        ArgumentNullException.ThrowIfNull(llmRequest);

        if (llmRequest.Messages.Count > 0
            && llmRequest.Messages[0].Role == "system"
            && llmRequest.Messages[0].Content?.Content == ClaudeCodeConstants.SystemMessage)
        {
            return llmRequest;
        }

        var claudeCodeMessage = new Message
        {
            Role = "system",
            Content = new MessageContent { Content = ClaudeCodeConstants.SystemMessage },
            // Force enable cache_control for the Claude Code system message.
            CacheControl = new CacheControl { Type = "ephemeral" },
        };

        // Go rebuilds the slice; the port must not prepend into the shared list
        // held by the caller's request.
        var messages = new List<Message>(llmRequest.Messages.Count + 1)
        {
            claudeCodeMessage,
        };
        messages.AddRange(llmRequest.Messages);
        llmRequest.Messages = messages;

        // Array format is required for cache_control. TransformOptions is a
        // Go value struct, so the port clones it before setting the flag.
        if (llmRequest.TransformOptions?.ArrayInstructions is null)
        {
            llmRequest.TransformOptions = llmRequest.TransformOptions is null
                ? new TransformOptions { ArrayInstructions = true }
                : new TransformOptions
                {
                    ArrayInstructions = true,
                    ArrayInputs = llmRequest.TransformOptions.ArrayInputs,
                    DefaultMaxTokens = llmRequest.TransformOptions.DefaultMaxTokens,
                };
        }

        return llmRequest;
    }

    /// <summary>Port of <c>isClaudeCLIUserAgent</c>.</summary>
    public static bool IsClaudeCliUserAgent(string value)
        => value.StartsWith("claude-cli/", StringComparison.Ordinal);

    private static void ScanContentArray(
        byte[] body,
        ref Utf8JsonReader reader,
        string prefix,
        List<(int Start, int End, byte[] Replacement)> edits)
    {
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray:
                    return;
                case JsonTokenType.StartObject:
                    ScanToolUseElement(body, ref reader, prefix, edits);
                    break;
                case JsonTokenType.StartArray:
                    reader.Skip();
                    break;
            }
        }
    }

    private static void ScanToolUseElement(
        byte[] body,
        ref Utf8JsonReader reader,
        string prefix,
        List<(int Start, int End, byte[] Replacement)> edits)
    {
        string? type = null;
        string? name = null;
        var nameStart = -1;
        var nameEnd = -1;
        var typeSeen = false;
        var nameSeen = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var property = reader.GetString();
            if (!reader.Read())
            {
                break;
            }

            if (property == "type")
            {
                if (!typeSeen)
                {
                    typeSeen = true;
                    type = reader.TokenType == JsonTokenType.String ? reader.GetString() : string.Empty;
                }
            }
            else if (property == "name")
            {
                if (!nameSeen)
                {
                    nameSeen = true;
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        name = reader.GetString();
                        nameStart = (int)reader.TokenStartIndex;
                        nameEnd = FindStringEnd(body, nameStart);
                    }
                }
            }

            SkipComposite(ref reader);
        }

        if (type == "tool_use"
            && name is not null
            && name.StartsWith(prefix, StringComparison.Ordinal))
        {
            var replacement = JsonSerializer.SerializeToUtf8Bytes(
                name[prefix.Length..],
                LlmJson.Options);
            edits.Add((nameStart, nameEnd, replacement));
        }
    }

    private static void SkipComposite(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    /// <summary>Finds the byte just past a string token's closing quote.</summary>
    private static int FindStringEnd(byte[] body, int tokenStart)
    {
        var i = tokenStart + 1;
        while (i < body.Length)
        {
            switch (body[i])
            {
                case (byte)'\\':
                    i += 2;
                    break;
                case (byte)'"':
                    return i + 1;
                default:
                    i++;
                    break;
            }
        }

        return body.Length;
    }

    private static byte[] ApplyEdits(
        byte[] body,
        List<(int Start, int End, byte[] Replacement)> edits)
    {
        var result = body;

        // The edits were collected in document order; splice from the end so
        // earlier offsets stay valid.
        for (var i = edits.Count - 1; i >= 0; i--)
        {
            var (start, end, replacement) = edits[i];
            var updated = new byte[result.Length - (end - start) + replacement.Length];
            result.AsSpan(0, start).CopyTo(updated);
            replacement.CopyTo(updated.AsSpan(start));
            result.AsSpan(end).CopyTo(updated.AsSpan(start + replacement.Length));
            result = updated;
        }

        return result;
    }
}
