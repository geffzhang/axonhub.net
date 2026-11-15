using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace AxonHub.Api.AnthropicUsage;

/// <summary>
/// Port of internal/server/api/anthropic_input_usage.go's
/// <c>estimateAnthropicInput</c>: counts textual evidence in an Anthropic
/// Messages request body, not transport/control fields or encoded media. This
/// is a client-display estimate, not provider usage or billing data.
/// </summary>
public static class AnthropicInputUsageEstimator
{
    // Go: inputUsageTokenizer sync.OnceValues(tokenizer.Get(O200kBase)).
    private static readonly Lazy<Tokenizer> Tokenizer =
        new(static () => TiktokenTokenizer.CreateForEncoding("o200k_base"),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Go: <c>estimateAnthropicInput</c>.</summary>
    public static long Estimate(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);

        using var document = JsonDocument.Parse(
            body,
            new JsonDocumentOptions { MaxDepth = 10_000 });
        var root = document.RootElement;

        // Go: json.Unmarshal("null", &struct) succeeds and leaves the zero
        // value; every other non-object root fails to unmarshal.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("json: cannot unmarshal value into Go value of type struct");
        }

        var parts = new List<string>();
        void AppendText(string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                parts.Add(value);
            }
        }

        void AppendContent(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    AppendText(value.GetString());
                    return;
                case JsonValueKind.Array:
                    foreach (var child in value.EnumerateArray())
                    {
                        AppendContent(child);
                    }

                    return;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return;
                case not JsonValueKind.Object:
                    return;
            }

            var type = GjsonString(value, "type");
            switch (type)
            {
                case "text":
                    AppendText(GjsonString(value, "text"));
                    break;
                case "thinking":
                    AppendText(GjsonString(value, "thinking"));
                    break;
                case "tool_use":
                case "server_tool_use":
                case "mcp_tool_use":
                    AppendText(GjsonString(value, "name"));
                    AppendText(GjsonRaw(value, "input"));
                    break;
                case "tool_result":
                case "mcp_tool_result":
                case "web_search_tool_result":
                case "web_fetch_tool_result":
                case "code_execution_tool_result":
                case "bash_code_execution_tool_result":
                case "text_editor_code_execution_tool_result":
                    if (value.TryGetProperty("content", out var content))
                    {
                        AppendContent(content);
                    }

                    break;
                case "document":
                    AppendText(GjsonString(value, "title"));
                    AppendText(GjsonString(value, "context"));
                    if (value.TryGetProperty("source", out var source)
                        && source.ValueKind == JsonValueKind.Object)
                    {
                        switch (GjsonString(source, "type"))
                        {
                            case "text":
                                AppendText(GjsonString(source, "data"));
                                break;
                            case "content":
                                if (source.TryGetProperty("content", out var sourceContent))
                                {
                                    AppendContent(sourceContent);
                                }

                                break;
                        }
                    }

                    break;
                case "search_result":
                case "web_search_result":
                    AppendText(GjsonString(value, "title"));
                    AppendText(GjsonString(value, "url"));
                    if (value.TryGetProperty("content", out var searchContent))
                    {
                        AppendContent(searchContent);
                    }

                    break;
                case "web_fetch_result":
                    if (value.TryGetProperty("content", out var fetchedContent))
                    {
                        AppendContent(fetchedContent);
                    }

                    break;
            }
        }

        if (root.TryGetProperty("system", out var system))
        {
            AppendContent(system);
        }

        if (root.TryGetProperty("messages", out var messages))
        {
            if (messages.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
            {
                throw new JsonException("json: cannot unmarshal value into Go value of type []struct");
            }

            if (messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var message in messages.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("json: cannot unmarshal value into Go value of type struct");
                    }

                    var role = message.TryGetProperty("role", out var roleValue)
                        ? roleValue.ValueKind switch
                        {
                            JsonValueKind.String => roleValue.GetString(),
                            JsonValueKind.Null => string.Empty,
                            _ => throw new JsonException(
                                "json: cannot unmarshal value into Go value of type string"),
                        }
                        : string.Empty;
                    AppendText(role);

                    if (message.TryGetProperty("content", out var content))
                    {
                        AppendContent(content);
                    }
                }
            }
        }

        if (root.TryGetProperty("tools", out var tools)
            && tools.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
        {
            throw new JsonException("json: cannot unmarshal value into Go value of type []struct");
        }

        if (root.TryGetProperty("tools", out tools) && tools.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("json: cannot unmarshal value into Go value of type struct");
                }

                AppendText(GjsonString(tool, "name"));
                AppendText(GjsonString(tool, "description"));
                AppendText(GjsonRaw(tool, "input_schema"));
            }
        }

        if (root.TryGetProperty("tool_choice", out var toolChoice))
        {
            AppendText(toolChoice.GetRawText());
        }

        if (parts.Count == 0)
        {
            return 0;
        }

        return Tokenizer.Value.CountTokens(string.Join("\n", parts));
    }

    /// <summary>Go: gjson Result.String() for a missing key is empty.</summary>
    private static string GjsonString(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found))
        {
            return string.Empty;
        }

        return found.ValueKind switch
        {
            JsonValueKind.String => found.GetString() ?? string.Empty,
            JsonValueKind.Number => found.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => found.GetRawText(),
        };
    }

    /// <summary>Go: gjson Result.Raw; a missing key is empty.</summary>
    private static string GjsonRaw(JsonElement value, string property)
        => value.TryGetProperty(property, out var found)
            ? found.GetRawText()
            : string.Empty;
}
