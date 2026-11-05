using System.Text;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the request-shape helpers at the bottom of
/// internal/server/orchestrator/candidates_condition.go (195-385): the content
/// feature flags, the prompt-token estimate, and the header map used to
/// evaluate association <c>when</c> conditions.
/// </summary>
public static class CandidateRequestFeatures
{
    /// <summary>Port of <c>reqStream</c>: nil request or nil stream reads false.</summary>
    public static bool RequestStream(LlmRequest? request) => request?.Stream ?? false;

    /// <summary>Port of <c>reqAPIFormat</c>: <c>string(req.APIFormat)</c>.</summary>
    public static string RequestApiFormat(LlmRequest? request) => request?.ApiFormat ?? string.Empty;

    /// <summary>Port of <c>reqReasoningEffort</c>.</summary>
    public static string RequestReasoningEffort(LlmRequest? request)
        => request?.ReasoningEffort ?? string.Empty;

    /// <summary>Port of <c>requestContentFeatures</c>.</summary>
    public sealed record RequestContentFeatures(
        bool HasImage,
        bool HasVideo,
        bool HasDocument,
        bool HasAudio)
    {
        /// <summary>Port of <c>features.hasAll()</c>.</summary>
        public bool HasAll => HasImage && HasVideo && HasDocument && HasAudio;
    }

    /// <summary>
    /// Port of <c>detectRequestContentFeatures</c> (candidates_condition.go:195-221).
    /// Only structured content parts count; a bare string content never sets a
    /// flag. The scan stops as soon as all four flags are set.
    /// </summary>
    public static RequestContentFeatures DetectRequestContentFeatures(LlmRequest? request)
    {
        if (request is null)
        {
            return new RequestContentFeatures(false, false, false, false);
        }

        var hasImage = false;
        var hasVideo = false;
        var hasDocument = false;
        var hasAudio = false;

        foreach (var message in request.Messages)
        {
            var parts = message.Content?.MultipleContent;
            if (parts is null)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part.ImageUrl is not null)
                {
                    hasImage = true;
                }
                else if (part.VideoUrl is not null)
                {
                    hasVideo = true;
                }
                else if (part.Document is not null)
                {
                    hasDocument = true;
                }
                else if (part.InputAudio is not null)
                {
                    hasAudio = true;
                }

                if (hasImage && hasVideo && hasDocument && hasAudio)
                {
                    return new RequestContentFeatures(true, true, true, true);
                }
            }
        }

        return new RequestContentFeatures(hasImage, hasVideo, hasDocument, hasAudio);
    }

    /// <summary>
    /// Port of <c>estimatePromptTokens</c> (candidates_condition.go:227-251).
    /// This is a heuristic, not a tokenizer: it must stay numerically identical
    /// to the reference so association thresholds fire on the same requests.
    /// </summary>
    public static long EstimatePromptTokens(LlmRequest? request)
    {
        if (request is null)
        {
            return 0;
        }

        var total = 0;
        foreach (var message in request.Messages)
        {
            total += EstimateTokens(message.Role ?? string.Empty);
            total += EstimateTokens(message.Name);
            total += CountPromptMessageContent(message.Content);
            total += EstimateTokens(message.ToolCallId);
            total += EstimateTokens(message.ToolCallName);
            total += EstimateTokens(message.ReasoningContent);

            if (message.ToolCalls is not null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    total += EstimateTokens(toolCall.Type);
                    total += EstimateTokens(toolCall.Function.Name);
                    total += EstimateTokens(toolCall.Function.Arguments);
                }
            }
        }

        total += CountPromptTools(request.Tools);

        return total;
    }

    /// <summary>Port of <c>countPromptMessageContent</c> (candidates_condition.go:253-284).</summary>
    private static int CountPromptMessageContent(MessageContent? content)
    {
        if (content is null)
        {
            // Go: a nil *MessageContent would panic on content.Content, but
            // Message.Content is a value struct, so the zero value reaches here
            // with both representations absent.
            return 0;
        }

        var tokens = EstimateTokens(content.Content);
        if (content.MultipleContent is null)
        {
            return tokens;
        }

        foreach (var part in content.MultipleContent)
        {
            tokens += EstimateTokens(part.Type);
            tokens += EstimateTokens(part.Text);

            if (part.ImageUrl is not null)
            {
                tokens += 128;
                tokens += EstimateTokens(part.ImageUrl.Detail);
            }

            if (part.VideoUrl is not null)
            {
                tokens += 128;
            }

            if (part.Document is not null)
            {
                tokens += Math.Min(EstimateTokens(part.Document.Url), 512);
                tokens += EstimateTokens(part.Document.MimeType);
            }

            if (part.InputAudio is not null)
            {
                tokens += 128;
                tokens += EstimateTokens(part.InputAudio.Format);
            }

            if (part.Compact is not null)
            {
                tokens += Math.Min(EstimateTokens(part.Compact.EncryptedContent), 256);
            }
        }

        return tokens;
    }

    /// <summary>Port of <c>countPromptTools</c> (candidates_condition.go:286-336).</summary>
    private static int CountPromptTools(List<Tool>? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var tool in tools)
        {
            total += EstimateTokens(tool.Type);

            total += EstimateTokens(tool.Function.Name);
            total += EstimateTokens(tool.Function.Description);
            total += EstimateTokens(ParametersText(tool.Function.Parameters));

            if (tool.WebSearch is not null)
            {
                var webSearch = tool.WebSearch;
                if (webSearch.MaxUses is not null)
                {
                    total += 2;
                }

                if (webSearch.Strict is not null)
                {
                    total++;
                }

                if (webSearch.AllowedDomains is not null)
                {
                    foreach (var domain in webSearch.AllowedDomains)
                    {
                        total += EstimateTokens(domain);
                    }
                }

                if (webSearch.BlockedDomains is not null)
                {
                    foreach (var domain in webSearch.BlockedDomains)
                    {
                        total += EstimateTokens(domain);
                    }
                }

                total += EstimateTokens(webSearch.UserLocation?.City);
                total += EstimateTokens(webSearch.UserLocation?.Country);
                total += EstimateTokens(webSearch.UserLocation?.Region);
                total += EstimateTokens(webSearch.UserLocation?.Timezone);
                total += EstimateTokens(webSearch.UserLocation?.Type);
            }

            if (tool.ResponseCustomTool is not null)
            {
                var custom = tool.ResponseCustomTool;
                total += EstimateTokens(custom.Name);
                total += EstimateTokens(custom.Description);

                if (custom.Format is not null)
                {
                    total += EstimateTokens(custom.Format.Type);
                    total += EstimateTokens(custom.Format.Syntax);
                    total += EstimateTokens(custom.Format.Definition);
                }
            }
        }

        return total;
    }

    /// <summary>Go: <c>string(tool.Function.Parameters)</c> over a <c>json.RawMessage</c>.</summary>
    private static string ParametersText(System.Text.Json.JsonElement? parameters)
    {
        if (parameters is null)
        {
            return string.Empty;
        }

        var element = parameters.Value;
        return element.ValueKind == System.Text.Json.JsonValueKind.Undefined
            ? string.Empty
            : element.GetRawText();
    }

    /// <summary>
    /// Ports <c>estimateTokensPtr</c> and <c>estimateTokens</c>
    /// (candidates_condition.go:338-370): a nil pointer and the empty string both
    /// estimate zero. CJK runes cost 1.5 runes per token, everything else four
    /// runes per token; whitespace is free; non-empty input never estimates below
    /// one token.
    /// </summary>
    private static int EstimateTokens(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var cjkChars = 0;
        var otherChars = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (IsCjk(rune.Value))
            {
                cjkChars++;
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            otherChars++;
        }

        var cjkTokens = cjkChars / 1.5;
        var otherTokens = otherChars / 4.0;

        var total = (int)(cjkTokens + otherTokens);
        if (total == 0)
        {
            return 1;
        }

        return total;
    }

    /// <summary>
    /// Port of <c>isCJK</c> (candidates_condition.go:372-377):
    /// <c>unicode.Is(Han|Hiragana|Katakana|Hangul, r)</c>. The ranges below are
    /// the Go script tables used by the reference toolchain.
    /// </summary>
    private static bool IsCjk(int rune) =>
        IsHan(rune) || IsHiragana(rune) || IsKatakana(rune) || IsHangul(rune);

    private static bool IsHan(int r) =>
        InRange(r, 0x2E80, 0x2E99)
        || InRange(r, 0x2E9B, 0x2EF3)
        || InRange(r, 0x2F00, 0x2FD5)
        || r == 0x3005
        || r == 0x3007
        || InRange(r, 0x3021, 0x3029)
        || InRange(r, 0x3038, 0x303B)
        || InRange(r, 0x3400, 0x4DBF)
        || InRange(r, 0x4E00, 0x9FFF)
        || InRange(r, 0xF900, 0xFA6D)
        || InRange(r, 0xFA70, 0xFAD9)
        || InRange(r, 0x16FE2, 0x16FE3)
        || InRange(r, 0x16FF0, 0x16FF1)
        || InRange(r, 0x20000, 0x2A6DF)
        || InRange(r, 0x2A700, 0x2B739)
        || InRange(r, 0x2B740, 0x2B81D)
        || InRange(r, 0x2B820, 0x2CEA1)
        || InRange(r, 0x2CEB0, 0x2EBE0)
        || InRange(r, 0x2F800, 0x2FA1D)
        || InRange(r, 0x30000, 0x3134A)
        || InRange(r, 0x31350, 0x323AF);

    private static bool IsHiragana(int r) =>
        InRange(r, 0x3041, 0x3096)
        || InRange(r, 0x309D, 0x309F)
        || InRange(r, 0x1B001, 0x1B11F)
        || InRange(r, 0x1B150, 0x1B152);

    private static bool IsKatakana(int r) =>
        InRange(r, 0x30A1, 0x30FA)
        || InRange(r, 0x30FD, 0x30FF)
        || InRange(r, 0x31F0, 0x31FF)
        || InRange(r, 0x32D0, 0x32FE)
        || InRange(r, 0x3300, 0x3357)
        || InRange(r, 0xFF66, 0xFF6F)
        || InRange(r, 0xFF71, 0xFF9D)
        || r == 0x1B000
        || InRange(r, 0x1B120, 0x1B122)
        || InRange(r, 0x1B164, 0x1B167);

    private static bool IsHangul(int r) =>
        InRange(r, 0x1100, 0x11FF)
        || InRange(r, 0x302E, 0x302F)
        || InRange(r, 0x3131, 0x318E)
        || InRange(r, 0x3200, 0x321E)
        || InRange(r, 0x3260, 0x327E)
        || InRange(r, 0xA960, 0xA97C)
        || InRange(r, 0xAC00, 0xD7A3)
        || InRange(r, 0xD7B0, 0xD7C6)
        || InRange(r, 0xD7CB, 0xD7FB)
        || InRange(r, 0xFFA0, 0xFFDC);

    private static bool InRange(int value, int start, int end) => value >= start && value <= end;

    /// <summary>
    /// Port of <c>buildRequestHeaderMap</c>
    /// (internal/server/orchestrator/override.go:49-71): first value of every
    /// non-sensitive inbound header, stored under both the canonical and the
    /// lower-case key so either spelling matches a condition.
    /// </summary>
    public static Dictionary<string, string> BuildRequestHeaderMap(LlmRequest? request)
    {
        var requestHeaders = new Dictionary<string, string>(StringComparer.Ordinal);
        var headers = request?.RawRequest?.Headers;
        if (headers is null)
        {
            return requestHeaders;
        }

        foreach (var (key, values) in headers)
        {
            if (values is null || values.Count == 0)
            {
                continue;
            }

            if (HttpClientUtils.IsSensitiveHeader(key))
            {
                continue;
            }

            var value = values[0];
            var canonicalKey = HttpClientUtils.CanonicalHeaderKey(key);
            requestHeaders[canonicalKey] = value;
            requestHeaders[key.ToLowerInvariant()] = value;
        }

        return requestHeaders;
    }
}
