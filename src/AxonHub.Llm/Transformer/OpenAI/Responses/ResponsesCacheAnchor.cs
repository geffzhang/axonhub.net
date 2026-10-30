using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of llm/transformer/openai/responses/cache_anchor.go: a stable
/// fingerprint for the conversation a request belongs to.
/// </summary>
/// <remarks>
/// The anchor covers the contiguous leading system/developer messages plus the
/// first user message. Later turns of the same conversation keep this head
/// unchanged, while sibling conversations multiplexed over one client session
/// (e.g. Claude Code subagents sharing a session_id) diverge in their first
/// user message. Combining this anchor with the session ID therefore yields a
/// per-conversation prompt_cache_key instead of a per-session one.
/// </remarks>
internal static class ResponsesCacheAnchor
{
    /// <summary>
    /// Bounds how much each content unit (a message's plain string content, or
    /// a single field of a content part) contributes to the hash. The budget is
    /// per unit rather than global so that a large instruction prefix can never
    /// starve the first user message out of the fingerprint.
    /// </summary>
    private const int AnchorMaxBytesPerUnit = 4096;

    /// <summary>Go: <c>conversationAnchor</c>.</summary>
    public static string ConversationAnchor(IReadOnlyList<Message>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return string.Empty;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Each unit is hashed as <len>:<capped bytes>. The length prefix keeps
        // concatenated units unambiguous and still distinguishes oversized units
        // (e.g. two base64 images sharing their first 4 KiB) by their size.
        void WriteUnit(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var lengthPrefix = Encoding.ASCII.GetBytes(
                bytes.Length.ToString(CultureInfo.InvariantCulture));

            hash.AppendData(lengthPrefix);
            hash.AppendData(":"u8);
            hash.AppendData(bytes.AsSpan(0, Math.Min(bytes.Length, AnchorMaxBytesPerUnit)));
        }

        var hashed = false;

        foreach (var message in messages)
        {
            var role = message.Role ?? string.Empty;
            if (role is not ("system" or "developer" or "user"))
            {
                break;
            }

            hash.AppendData(Encoding.UTF8.GetBytes(role));
            hash.AppendData([0x00]);

            if (message.Content?.Content is { } content)
            {
                WriteUnit(content);
            }

            foreach (var part in message.Content?.MultipleContent ?? [])
            {
                hash.AppendData(Encoding.UTF8.GetBytes(part.Type));
                hash.AppendData([0x1f]);

                // The reference's switch picks the first non-nil field.
                if (part.Text is { } text)
                {
                    WriteUnit(text);
                }
                else if (part.ImageUrl is { } imageUrl)
                {
                    WriteUnit(imageUrl.Url);
                }
                else if (part.VideoUrl is { } videoUrl)
                {
                    WriteUnit(videoUrl.Url);
                }
                else if (part.Document is { } document)
                {
                    WriteUnit(document.MimeType ?? string.Empty);
                    WriteUnit(document.Url);
                }
                else if (part.InputAudio is { } inputAudio)
                {
                    WriteUnit(inputAudio.Format);
                    WriteUnit(inputAudio.Data);
                }
                else if (part.Compact is { } compact)
                {
                    WriteUnit(compact.Id ?? string.Empty);
                    WriteUnit(compact.EncryptedContent ?? string.Empty);
                }

                hash.AppendData([0x1f]);
            }

            hash.AppendData([0x1e]);

            hashed = true;

            // The first user message ends the stable conversation head.
            if (role == "user")
            {
                break;
            }
        }

        if (!hashed)
        {
            return string.Empty;
        }

        // Go hex.EncodeToString is lowercase.
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..16];
    }
}
