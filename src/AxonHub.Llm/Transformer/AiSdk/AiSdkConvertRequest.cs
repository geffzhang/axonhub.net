using System.Text;
using System.Text.Json;

namespace AxonHub.Llm.Transformer.AiSdk;

/// <summary>
/// Port of aisdk.convertToLLMRequestWithAPIFormat
/// (llm/transformer/aisdk/convert_request.go:30-402).
/// </summary>
public static class AiSdkConvertRequest
{
    public static LlmRequest Convert(AiSdkRequest request, string apiFormat)
    {
        ArgumentNullException.ThrowIfNull(request);

        var llmRequest = new LlmRequest
        {
            Model = request.Model ?? string.Empty,
            Messages = [],
            RequestType = RequestTypes.Chat,
            ApiFormat = apiFormat,
            Stream = true,
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            llmRequest.Messages.Add(new Message
            {
                Role = "system",
                Content = MessageContent.FromText(request.System),
            });
        }

        foreach (var message in request.Messages ?? [])
        {
            var role = (message.Role ?? string.Empty).ToLowerInvariant();
            switch (role)
            {
                case "system":
                    AppendSystemMessage(llmRequest, message);
                    break;
                case "user":
                    AppendUserMessage(llmRequest, message);
                    break;
                case "assistant":
                    AppendAssistantMessage(llmRequest, message);
                    break;
                default:
                    throw new InvalidOperationException("unsupported role: " + role);
            }
        }

        if (request.Tools is { Count: > 0 })
        {
            var tools = new List<Tool>(request.Tools.Count);
            foreach (var tool in request.Tools)
            {
                var function = tool.Function;
                var llmTool = new Tool
                {
                    Type = tool.Type ?? string.Empty,
                    Function = new Function
                    {
                        Name = function?.Name ?? string.Empty,
                        Description = GoStr(function?.Description),
                    },
                };

                if (function is not null
                    && function.Parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    try
                    {
                        llmTool.Function.Parameters = AiSdkJson.ElementFromBytes(
                            AiSdkJson.MarshalAny(function.Parameters));
                    }
                    catch (JsonException)
                    {
                        // Go's json.Marshal of an any value parsed from JSON
                        // cannot fail, so a failure here leaves Parameters nil
                        // exactly as the reference's `if err == nil` guard does.
                    }
                }

                tools.Add(llmTool);
            }

            llmRequest.Tools = tools;
        }

        return llmRequest;
    }

    private static void AppendSystemMessage(LlmRequest llmRequest, AiSdkUiMessage message)
    {
        var contentText = string.Empty;

        if (message.Parts is { Count: > 0 })
        {
            var builder = new StringBuilder();
            foreach (var part in message.Parts)
            {
                if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
                {
                    builder.Append(part.Text);
                }
            }

            contentText = builder.ToString();
        }
        else if (message.Content.ValueKind == JsonValueKind.String)
        {
            contentText = message.Content.GetString() ?? string.Empty;
        }

        if (contentText.Length > 0)
        {
            llmRequest.Messages.Add(new Message
            {
                Role = "system",
                Content = MessageContent.FromText(contentText),
            });
        }
    }

    private static void AppendUserMessage(LlmRequest llmRequest, AiSdkUiMessage message)
    {
        List<MessageContentPart>? parts = null;
        var contentText = string.Empty;

        if (message.Parts is { Count: > 0 })
        {
            parts = [];
            foreach (var part in message.Parts)
            {
                switch (part.Type)
                {
                    case "text":
                        if (!string.IsNullOrEmpty(part.Text))
                        {
                            parts.Add(new MessageContentPart { Type = "text", Text = part.Text });
                        }

                        break;
                    case "file":
                        var contentPart = ToContentPartFromFile(part);
                        if (contentPart is not null)
                        {
                            parts.Add(contentPart);
                        }

                        break;
                }
            }
        }
        else if (message.Content.ValueKind == JsonValueKind.String)
        {
            contentText = message.Content.GetString() ?? string.Empty;
        }

        if (parts is { Count: > 0 })
        {
            llmRequest.Messages.Add(new Message
            {
                Role = "user",
                Content = MessageContent.FromParts(parts),
            });
        }
        else if (contentText.Length > 0)
        {
            llmRequest.Messages.Add(new Message
            {
                Role = "user",
                Content = MessageContent.FromText(contentText),
            });
        }
    }

    private static void AppendAssistantMessage(LlmRequest llmRequest, AiSdkUiMessage message)
    {
        if (message.Parts is not { Count: > 0 })
        {
            if (message.Content.ValueKind == JsonValueKind.String)
            {
                var text = message.Content.GetString() ?? string.Empty;
                if (text.Length > 0)
                {
                    llmRequest.Messages.Add(new Message
                    {
                        Role = "assistant",
                        Content = MessageContent.FromText(text),
                    });
                }
            }

            return;
        }

        var block = new List<AiSdkUiMessagePart>();
        foreach (var part in message.Parts)
        {
            if (part.Type == "step-start")
            {
                AppendAssistantBlock(llmRequest, block);
                block = [];
                continue;
            }

            block.Add(part);
        }

        AppendAssistantBlock(llmRequest, block);
    }

    private static void AppendAssistantBlock(
        LlmRequest llmRequest,
        List<AiSdkUiMessagePart> block)
    {
        if (block.Count == 0)
        {
            return;
        }

        var contentParts = new List<MessageContentPart>();
        var toolCalls = new List<ToolCall>();
        var toolMessages = new List<Message>();

        foreach (var part in block)
        {
            if (part.Type == "text" && !string.IsNullOrEmpty(part.Text))
            {
                contentParts.Add(new MessageContentPart { Type = "text", Text = part.Text });
            }
            else if (part.Type == "file")
            {
                var contentPart = ToContentPartFromFile(part);
                if (contentPart is not null)
                {
                    contentParts.Add(contentPart);
                }
            }
            else if (part.Type == "reasoning" && !string.IsNullOrEmpty(part.Text))
            {
                // Reasoning is carried as plain text content for now.
                contentParts.Add(new MessageContentPart { Type = "text", Text = part.Text });
            }
            else if (part.Type == "dynamic-tool"
                || part.Type.StartsWith("tool-", StringComparison.Ordinal))
            {
                if (part.State == "input-streaming")
                {
                    continue;
                }

                byte[]? arguments;
                if (part.State == "output-error")
                {
                    if (part.Input.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                    {
                        arguments = AiSdkJson.AnyToRaw(part.Input);
                    }
                    else if (part.RawInput is { Length: > 0 })
                    {
                        arguments = part.RawInput;
                    }
                    else
                    {
                        arguments = null;
                    }
                }
                else
                {
                    arguments = AiSdkJson.AnyToRaw(part.Input);
                }

                var argumentText = AiSdkJson.RawToString(arguments);
                if (argumentText.Length == 0)
                {
                    argumentText = "{}";
                }

                toolCalls.Add(new ToolCall
                {
                    Id = GoStr(part.ToolCallId),
                    Type = ToolTypes.Function,
                    Function = new FunctionCall
                    {
                        Name = GetToolName(part),
                        Arguments = argumentText,
                    },
                });

                if (part.State is "output-available" or "output-error")
                {
                    string outputText;
                    if (part.State == "output-error" && !string.IsNullOrEmpty(part.ErrorText))
                    {
                        outputText = part.ErrorText;
                    }
                    else
                    {
                        outputText = AiSdkJson.RawToString(AiSdkJson.AnyToRaw(part.Output));
                    }

                    if (outputText.Length > 0)
                    {
                        toolMessages.Add(new Message
                        {
                            Role = "tool",
                            ToolCallId = part.ToolCallId ?? string.Empty,
                            Content = MessageContent.FromText(outputText),
                        });
                    }
                }
            }
        }

        if (contentParts.Count > 0 || toolCalls.Count > 0)
        {
            llmRequest.Messages.Add(new Message
            {
                Role = "assistant",
                Content = contentParts.Count > 0 ? MessageContent.FromParts(contentParts) : null,
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            });
        }

        llmRequest.Messages.AddRange(toolMessages);
    }

    private static MessageContentPart? ToContentPartFromFile(AiSdkUiMessagePart part)
    {
        if (string.IsNullOrEmpty(part.Url))
        {
            return null;
        }

        if ((part.MediaType ?? string.Empty)
            .ToLowerInvariant()
            .StartsWith("image/", StringComparison.Ordinal))
        {
            return new MessageContentPart
            {
                Type = "image_url",
                ImageUrl = new ImageUrl { Url = part.Url },
            };
        }

        return null;
    }

    private static string GetToolName(AiSdkUiMessagePart part)
    {
        if (!string.IsNullOrEmpty(part.ToolName))
        {
            return part.ToolName;
        }

        const string prefix = "tool-";
        if (part.Type.StartsWith(prefix, StringComparison.Ordinal))
        {
            return part.Type[prefix.Length..];
        }

        return string.Empty;
    }

    private static string? GoStr(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
