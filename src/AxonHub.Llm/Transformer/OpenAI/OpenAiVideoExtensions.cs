using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.video_extensions.go: the JSON request parser (Go decoding
/// with the extra-field passthrough), the multipart extension fields and the
/// extra_body merge. Unknown top-level fields are swept into
/// <c>extra_body</c>; known fields never are.
/// </summary>
internal static class OpenAiVideoExtensions
{
    /// <summary>Port of parseVideoJSONRequest.</summary>
    public static VideoCreateRequest ParseVideoJsonRequest(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidRequest("failed to decode video request: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null && rootKind != "null")
        {
            throw TransformerErrors.InvalidRequest(
                "failed to decode video request: " + GoStructJson.TypeMismatch(rootKind, "openai.VideoCreateRequest"));
        }

        VideoCreateRequest request;
        if (rootKind == "null")
        {
            // Go: json.Unmarshal null into a struct leaves the zero value.
            request = new VideoCreateRequest();
        }
        else
        {
            try
            {
                request = LlmJson.Deserialize<VideoCreateRequest>(body)
                    ?? throw new JsonException("unexpected null");
            }
            catch (JsonException ex)
            {
                throw TransformerErrors.InvalidRequest(
                    "failed to decode video request: " + OpenAiInboundTransformer.JsonErrorText(ex),
                    ex);
            }

            // Go re-unmarshals into map[string]json.RawMessage; a non-object
            // root was already rejected above, so every property lands here.
            var extras = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(body))
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (!IsKnownVideoRequestField(property.Name))
                    {
                        extras[property.Name] = property.Value.Clone();
                    }
                }
            }

            request.ExtraBody = MergeVideoExtraBody(request.ExtraBody, extras);
        }

        return request;
    }

    /// <summary>Port of applyVideoMultipartExtensions.</summary>
    public static void ApplyVideoMultipartExtensions(
        VideoCreateRequest request,
        Dictionary<string, string> fields)
    {
        request.Ratio = TrimSpaceField(fields, "ratio");
        request.Resolution = TrimSpaceField(fields, "resolution");
        request.ServiceTier = TrimSpaceField(fields, "service_tier");
        request.CallbackURL = TrimSpaceField(fields, "callback_url");

        if (TrimSpaceField(fields, "execution_expires_after") is { Length: > 0 } rawExpires)
        {
            if (!GoCast.TryParseInt64(rawExpires, out var expires))
            {
                throw TransformerErrors.InvalidRequest("execution_expires_after must be an integer");
            }

            request.ExecutionExpiresAfter = expires;
        }

        if (TrimSpaceField(fields, "content") is { Length: > 0 } rawContent)
        {
            if (!TryParseJsonArray(rawContent, out List<VideoContent>? content))
            {
                throw TransformerErrors.InvalidRequest("content must be a JSON array");
            }

            request.Content = content;
        }

        if (TrimSpaceField(fields, "return_last_frame") is { Length: > 0 } rawReturn)
        {
            if (!GoCast.TryParseBool(rawReturn, out var returnLastFrame))
            {
                throw TransformerErrors.InvalidRequest("return_last_frame must be a boolean");
            }

            request.ReturnLastFrame = returnLastFrame;
        }

        if (TrimSpaceField(fields, "tools") is { Length: > 0 } rawTools)
        {
            if (!TryParseJsonArray(rawTools, out List<JsonElement>? tools))
            {
                throw TransformerErrors.InvalidRequest("tools must be a JSON array");
            }

            request.Tools = tools;
        }

        // Unknown multipart fields are swept into extra_body as raw JSON: the
        // bare field value must itself be valid JSON (e.g. "true", "5",
        // "{\"a\":1}") — a bare URL string is not.
        var extras = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (IsKnownVideoRequestField(key))
            {
                continue;
            }

            var raw = System.Text.Encoding.UTF8.GetBytes(value);
            if (GoJsonSyntax.Validate(raw) is not null)
            {
                throw TransformerErrors.InvalidRequest(
                    "native field " + GoCast.GoQuote(key) + " must be valid JSON");
            }

            using var doc = JsonDocument.Parse(raw);
            extras[key] = doc.RootElement.Clone();
        }

        JsonElement? explicitExtra = null;
        if (TrimSpaceField(fields, "extra_body") is { Length: > 0 } rawExtraBody)
        {
            explicitExtra = ParseExtraBodyObject(System.Text.Encoding.UTF8.GetBytes(rawExtraBody));
        }

        request.ExtraBody = MergeVideoExtraBody(explicitExtra, extras);
    }

    /// <summary>Port of mergeVideoExtraBody.</summary>
    public static JsonElement? MergeVideoExtraBody(
        JsonElement? explicitBody,
        Dictionary<string, JsonElement> extras)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (explicitBody is { } body)
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                // Go: unmarshal into a map fails for any non-object, and a JSON
                // null sets the map to nil — both take this branch.
                throw TransformerErrors.InvalidRequest("extra_body must be a JSON object");
            }

            foreach (var property in body.EnumerateObject())
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        foreach (var (key, value) in extras)
        {
            merged[key] = value;
        }

        if (merged.Count == 0)
        {
            return null;
        }

        var encoded = GoJson.Marshal(merged);
        using var doc = JsonDocument.Parse(encoded);
        return doc.RootElement.Clone();
    }

    /// <summary>Port of isKnownVideoRequestField.</summary>
    public static bool IsKnownVideoRequestField(string field)
        => field is "model" or "prompt" or "input_reference" or "content" or "seconds" or "size"
            or "ratio" or "resolution" or "service_tier" or "execution_expires_after"
            or "callback_url" or "return_last_frame" or "tools" or "extra_body";

    private static string TrimSpaceField(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    /// <summary>
    /// Go: json.Unmarshal into a slice — any failure (syntax, non-array root,
    /// field-level type error inside the items) rejects the value.
    /// </summary>
    private static bool TryParseJsonArray<T>(string raw, out List<T>? parsed)
    {
        parsed = null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(raw);
        if (GoJsonSyntax.Validate(bytes) is not null)
        {
            return false;
        }

        var rootKind = GoStructJson.RootKind(bytes);
        if (rootKind != "array")
        {
            return false;
        }

        try
        {
            parsed = LlmJson.Deserialize<List<T>>(bytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement ParseExtraBodyObject(byte[] raw)
    {
        // GoStructJson.RootKind returns null for object roots; any other kind
        // (array, string, number, bool, null) unmarshals into a nil Go map and
        // takes the reference's merged == nil branch.
        if (GoJsonSyntax.Validate(raw) is not null
            || GoStructJson.RootKind(raw) is not null)
        {
            throw TransformerErrors.InvalidRequest("extra_body must be a JSON object");
        }

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
