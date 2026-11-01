using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.PromptProtection;

/// <summary>An immutable snapshot of a normalized prompt value.</summary>
internal readonly record struct ProtectedPromptText(string Role, string Text);

/// <summary>
/// Port of orchestrator.promptProtectionBodyCheck
/// (internal/server/orchestrator/pass_through_prompt_protection.go): ties raw
/// replay to the inbound mapping that was actually protected, rather than
/// trusting a second interpretation of the schema.
/// </summary>
internal sealed class PromptProtectionBodyCheck(
    IInboundTransformer? inbound,
    HttpRequestData? raw,
    IReadOnlyList<ProtectedPromptText> expected)
{
    /// <summary>
    /// Port of validate: rejects partial patches and regex semantics that cannot
    /// be represented safely in native fields, retaining the already-protected
    /// generated body instead. Returns the reference's error text, or null on
    /// success; the caller only uses the text for a diagnostic log.
    /// </summary>
    public string? Validate(byte[] body)
    {
        if (inbound is null || raw is null)
        {
            return "protected pass-through body has no inbound mapping";
        }

        // Go: raw := *check.raw; raw.Body = body; raw.Headers = headers.Clone().
        var replay = CloneWithBody(raw, body);

        LlmRequest replayed;
        try
        {
            replayed = inbound.TransformRequest(replay);
        }
        catch (Exception)
        {
            // Decoder errors may contain user content; the reference substitutes
            // a fixed message for that reason.
            return "protected pass-through body cannot be decoded by inbound mapping";
        }

        if (!PromptProtectionTexts.Capture(replayed).SequenceEqual(expected))
        {
            return "pass-through prompt values differ from protected request";
        }

        return null;
    }

    private static HttpRequestData CloneWithBody(HttpRequestData raw, byte[] body)
        => new()
        {
            Method = raw.Method,
            Url = raw.Url,
            Path = raw.Path,
            // Go's struct copy shares the query map but clones the header map.
            Query = raw.Query,
            Headers = CloneHeaders(raw.Headers),
            ContentType = raw.ContentType,
            Body = body,
            JsonBody = raw.JsonBody,
            Auth = raw.Auth,
            RequestId = raw.RequestId,
            ClientIp = raw.ClientIp,
            UserAgent = raw.UserAgent,
            RequestType = raw.RequestType,
            ApiFormat = raw.ApiFormat,
            RawRequest = raw.RawRequest,
            Metadata = raw.Metadata,
            TransformerMetadata = raw.TransformerMetadata,
            SkipInboundQueryMerge = raw.SkipInboundQueryMerge,
            SessionId = raw.SessionId,
            SessionScope = raw.SessionScope,
            OnResponseHeaders = raw.OnResponseHeaders,
        };

    private static Dictionary<string, List<string>>? CloneHeaders(
        Dictionary<string, List<string>>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        var clone = new Dictionary<string, List<string>>(headers.Count, StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            clone[key] = new List<string>(values);
        }

        return clone;
    }
}

/// <summary>
/// Port of promptProtectionTexts: copies the role and text values inspected by
/// prompt protection before its in-place changes can overwrite the snapshot.
/// </summary>
internal static class PromptProtectionTexts
{
    public static List<ProtectedPromptText> Capture(LlmRequest? request)
    {
        if (request is null)
        {
            return [];
        }

        var texts = new List<ProtectedPromptText>();
        var messages = request.Messages;

        if (request.Compact is not null)
        {
            if (!string.IsNullOrEmpty(request.Compact.Instructions))
            {
                texts.Add(new ProtectedPromptText("system", request.Compact.Instructions));
            }

            messages = request.Compact.Input ?? [];
        }

        if (request.Decisions is not null)
        {
            var body = request.Decisions.Body;
            if (PromptProtectionJson.IsString(body, "input"))
            {
                return [new ProtectedPromptText("user", PromptProtectionJson.String(body, "input"))];
            }

            var inputLength = PromptProtectionJson.ArrayLength(body, "input");
            for (var i = 0; i < inputLength; i++)
            {
                var itemPath = "input." + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var role = PromptProtectionJson.String(body, itemPath + ".role");
                if (role.Length == 0)
                {
                    role = "user";
                }

                var contentLength = PromptProtectionJson.ArrayLength(body, itemPath + ".content");
                for (var j = 0; j < contentLength; j++)
                {
                    var partPath = itemPath + ".content."
                        + j.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (string.Equals(
                            PromptProtectionJson.String(body, partPath + ".type"),
                            "input_text",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        texts.Add(new ProtectedPromptText(
                            role,
                            PromptProtectionJson.String(body, partPath + ".text")));
                    }
                }
            }
        }

        foreach (var message in messages)
        {
            var role = message.Role ?? string.Empty;
            if (message.Content?.Content is { } content)
            {
                texts.Add(new ProtectedPromptText(role, content));
            }

            if (message.Content?.MultipleContent is { } parts)
            {
                foreach (var part in parts)
                {
                    if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase)
                        && part.Text is not null)
                    {
                        texts.Add(new ProtectedPromptText(role, part.Text));
                    }
                }
            }
        }

        return texts;
    }
}
