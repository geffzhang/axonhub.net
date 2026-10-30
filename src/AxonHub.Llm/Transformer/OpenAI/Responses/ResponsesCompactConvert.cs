namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/compact.go: conversion between
/// compact items and unified messages.
/// </summary>
public static class ResponsesCompactConvert
{
    /// <summary>Go: <c>compactionContentFromItem</c>.</summary>
    public static CompactContent CompactionContentFromItem(ResponsesItem item)
    {
        return new CompactContent
        {
            Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
            EncryptedContent = ResponsesConvertUtil.NullIfEmpty(item.EncryptedContent),
            CreatedBy = item.CreatedBy,
        };
    }

    /// <summary>Go: <c>compactionMessageFromItem</c>.</summary>
    public static Message CompactionMessageFromItem(ResponsesItem item, string contentType)
    {
        return new Message
        {
            Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
            Role = "assistant",
            Content = new MessageContent
            {
                MultipleContent =
                [
                    new MessageContentPart
                    {
                        Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
                        Type = contentType,
                        Compact = CompactionContentFromItem(item),
                    },
                ],
            },
        };
    }

    /// <summary>Go: <c>compactionContentPartFromItem</c>.</summary>
    public static MessageContentPart CompactionContentPartFromItem(
        ResponsesItem item, string contentType)
    {
        return new MessageContentPart
        {
            Id = ResponsesConvertUtil.NullIfEmpty(item.Id),
            Type = contentType,
            Compact = CompactionContentFromItem(item),
        };
    }

    /// <summary>
    /// Go: <c>compactionItemFromPart</c>. Go always takes a pointer to the
    /// encrypted content (<c>lo.ToPtr</c>), but compaction items force the field
    /// on the wire, so null and empty serialize identically.
    /// </summary>
    public static ResponsesItem CompactionItemFromPart(MessageContentPart part, string contentType)
    {
        CompactContent compact = part.Compact!;

        return new ResponsesItem
        {
            Id = compact.Id ?? string.Empty,
            Type = contentType,
            EncryptedContent = compact.EncryptedContent,
            CreatedBy = compact.CreatedBy,
        };
    }

    /// <summary>
    /// Go: <c>convertCompactInputToMessages</c>. Compact shares item decoding
    /// with Responses but has no tool declaration validation step, so function
    /// names are flattened here.
    /// </summary>
    public static List<Message> ConvertCompactInputToMessages(ResponsesInput input)
    {
        var messages = ResponsesInboundConvert.ConvertInputToMessages(input) ?? [];

        foreach (var message in messages)
        {
            foreach (var call in message.ToolCalls ?? [])
            {
                if (call.Type is "function" or null or "")
                {
                    call.Function.Name = ResponsesNamespace.FlatFunctionName(
                        call.Function.Namespace, call.Function.Name);
                }
            }
        }

        return messages;
    }

    /// <summary>Go: <c>convertCompactMessagesToItems</c>.</summary>
    public static List<ResponsesItem> ConvertCompactMessagesToItems(List<Message> messages)
    {
        var items = new List<ResponsesItem>(messages.Count);

        foreach (var message in messages)
        {
            items.AddRange(ResponsesInboundConvert.BuildReasoningItems(message));
            items.AddRange(ConvertCompactMessageToItems(message));
        }

        return items;
    }

    /// <summary>Go: <c>convertCompactMessageToItems</c>.</summary>
    public static List<ResponsesItem> ConvertCompactMessageToItems(Message msg)
    {
        var role = msg.Role;
        if (string.IsNullOrEmpty(role))
        {
            role = "assistant";
        }

        var items = new List<ResponsesItem>();
        List<ResponsesItem>? contentItems = null;
        var annotationsAttached = false;

        var textItemType = role == "assistant" ? "output_text" : "input_text";

        void FlushMessage()
        {
            if (contentItems is not { Count: > 0 })
            {
                return;
            }

            if (!annotationsAttached)
            {
                var (attachedItems, attached) =
                    ResponsesInboundConvert.AttachAnnotationsToFirstTextItem(
                        contentItems, msg.Annotations);
                contentItems = attachedItems;
                annotationsAttached = attached;
            }

            items.Add(new ResponsesItem
            {
                Id = msg.Id ?? string.Empty,
                Type = "message",
                Role = role,
                Content = new ResponsesInput { Items = contentItems },
                Status = "completed",
            });
            contentItems = null;
        }

        if (msg.Content?.Content is { } content)
        {
            contentItems ??= [];
            contentItems.Add(new ResponsesItem
            {
                Type = textItemType,
                Text = content,
                Annotations = [],
            });
        }

        foreach (var part in msg.Content?.MultipleContent ?? [])
        {
            switch (part.Type)
            {
                case "text":
                    if (part.Text is { } text)
                    {
                        contentItems ??= [];
                        contentItems.Add(new ResponsesItem
                        {
                            Type = textItemType,
                            Text = text,
                            Annotations = [],
                        });
                    }

                    break;

                case "image_url":
                    if (part.ImageUrl is { } imageUrl)
                    {
                        contentItems ??= [];
                        contentItems.Add(new ResponsesItem
                        {
                            Type = "input_image",
                            ImageUrl = imageUrl.Url,
                            Detail = imageUrl.Detail,
                        });
                    }

                    break;

                case "compaction":
                case "compaction_summary":
                    if (part.Compact is not null)
                    {
                        FlushMessage();

                        items.Add(CompactionItemFromPart(part, part.Type));
                    }

                    break;
            }
        }

        FlushMessage();

        return items;
    }
}
