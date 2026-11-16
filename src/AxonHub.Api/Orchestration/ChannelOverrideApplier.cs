using System.Text;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the channel request-rewrite middlewares
/// (internal/server/orchestrator/override.go): body operations patch the
/// outbound JSON body with sjson semantics, header operations rewrite the
/// outbound headers, and both render their values with the channel's Go
/// templates.
/// </summary>
/// <remarks>
/// Per-operation failures are isolated: the reference logs the error and keeps
/// applying the remaining operations. The reference assigns the value returned
/// by sjson on failure (nil) and therefore drops the body on a low-level set
/// error; this port keeps the last successfully built body instead (difference
/// C041). Every operation error is still swallowed the same way.
/// </remarks>
internal static class ChannelOverrideApplier
{
    /// <summary>Port of applyOverrideRequestBody (override.go:150-224).</summary>
    public static void ApplyBodyOperations(
        HttpRequestData request,
        LlmRequest? llmRequest,
        string originalModel,
        ChannelOverrideOperation[] operations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Length == 0 || llmRequest is null || !BodyOverrideSupported(request))
        {
            return;
        }

        var renderContext = BuildRenderContext(llmRequest, originalModel);
        var body = request.Body;
        var originalBody = ReadOriginalBody(llmRequest);

        foreach (var operation in operations)
        {
            // sjson would happily turn this into a body field that the format
            // transformers then rebuild from the unified request.
            if (string.Equals(operation.Path, "stream", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                body = ApplyBodyOperation(body, operation, renderContext, originalBody);
            }
            catch (OverrideOperationException)
            {
                // The reference logs the error and continues with the next
                // operation; nothing reaches the client.
            }
        }

        request.Body = body;
    }

    /// <summary>Port of applyOverrideRequestHeaders (override.go:554-580).</summary>
    public static void ApplyHeaderOperations(
        HttpRequestData request,
        LlmRequest? llmRequest,
        string originalModel,
        ChannelOverrideOperation[] operations)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(operations);

        if (operations.Length == 0 || llmRequest is null)
        {
            return;
        }

        request.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var renderContext = BuildRenderContext(llmRequest, originalModel);
        foreach (var operation in operations)
        {
            ApplyHeaderOperation(request.Headers, operation, renderContext);
        }
    }

    /// <summary>
    /// Port of ApplyModelFetchHeaderOverrides (channel_override.go:313-365):
    /// model probing runs outside the LLM pipeline, so conditions and templated
    /// values are skipped and only literal operations apply.
    /// </summary>
    public static void ApplyModelFetchHeaderOverrides(
        Dictionary<string, List<string>> headers,
        IReadOnlyList<OverrideOperationDraft>? operations)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (operations is null || operations.Count == 0)
        {
            return;
        }

        foreach (var operation in operations)
        {
            if (!string.IsNullOrEmpty(operation.Condition))
            {
                continue;
            }

            var path = operation.Path ?? string.Empty;
            var from = operation.From ?? string.Empty;
            var to = operation.To ?? string.Empty;
            var value = operation.Value ?? string.Empty;

            switch (operation.Op)
            {
                case ChannelOverrideOps.Set:
                    if (value.Contains("{{", StringComparison.Ordinal))
                    {
                        // A templated value cannot be rendered without an LLM
                        // request context; sending the raw template would
                        // corrupt the probe request.
                        continue;
                    }

                    if (string.Equals(value, ChannelOverrideOps.ClearDirective, StringComparison.Ordinal))
                    {
                        HttpClientUtils.HeaderDel(headers, path);
                        continue;
                    }

                    HttpClientUtils.HeaderSet(headers, path, value);
                    break;

                case ChannelOverrideOps.Delete:
                    HttpClientUtils.HeaderDel(headers, path);
                    break;

                case ChannelOverrideOps.Rename:
                    {
                        var values = HeaderValues(headers, from);
                        if (values.Count == 0)
                        {
                            continue;
                        }

                        HttpClientUtils.HeaderDel(headers, from);
                        foreach (var headerValue in values)
                        {
                            HttpClientUtils.HeaderAdd(headers, to, headerValue);
                        }

                        break;
                    }

                case ChannelOverrideOps.Copy:
                    foreach (var headerValue in HeaderValues(headers, from))
                    {
                        HttpClientUtils.HeaderAdd(headers, to, headerValue);
                    }

                    break;

                default:
                    // Unknown header operations are ignored.
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // Render context (override.go:23-135)
    // ------------------------------------------------------------------

    private static OverrideRenderContext BuildRenderContext(LlmRequest llmRequest, string originalModel)
        => new()
        {
            RequestModel = originalModel,
            Model = llmRequest.Model,
            Metadata = llmRequest.Metadata is null ? TemplateMap.Nil : new TemplateMap(llmRequest.Metadata),
            RequestHeader = BuildRequestHeaderMap(llmRequest),
            // override.go:81-85: a present top-level key wins (even ""); the
            // compact request's key is only the fallback when it is absent.
            PromptCacheKey = llmRequest.PromptCacheKey
                ?? llmRequest.Compact?.PromptCacheKey
                ?? string.Empty,
            ReasoningEffort = llmRequest.ReasoningEffort ?? string.Empty,
        };

    /// <summary>Port of buildRequestHeaderMap (override.go:49-71).</summary>
    private static TemplateMap BuildRequestHeaderMap(LlmRequest llmRequest)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawRequest = llmRequest.RawRequest;
        if (rawRequest?.Headers is null)
        {
            return new TemplateMap(headers);
        }

        foreach (var (key, values) in rawRequest.Headers)
        {
            if (values.Count == 0 || HttpClientUtils.IsSensitiveHeader(key))
            {
                continue;
            }

            var value = values[0];
            headers[HttpClientUtils.CanonicalHeaderKey(key)] = value;
            headers[key.ToLowerInvariant()] = value;
        }

        return new TemplateMap(headers);
    }

    /// <summary>
    /// Port of renderOverrideValue (override.go:117-135): a rendered value that
    /// looks like a JSON document or literal is decoded with
    /// <c>encoding/json</c> semantics (numbers become float64, object keys lose
    /// their original order), and any decode failure falls back to the raw
    /// rendered string.
    /// </summary>
    private static GoValue RenderOverrideValue(string value, OverrideRenderContext context)
    {
        var rendered = GoTextTemplate.Render(value, context);
        var trimmed = GoTrimSpace(rendered);
        if (trimmed.Length == 0)
        {
            return new GoStringValue(rendered);
        }

        var first = trimmed[0];
        var looksStructured = first is '{' or '[' or '-' or >= '0' and <= '9';
        if (looksStructured || trimmed is "true" or "false" or "null")
        {
            if (OverrideJson.TryParse(Encoding.UTF8.GetBytes(trimmed), out var node)
                && node is not null
                && AllNumbersFinite(node))
            {
                return node.ToGoValue();
            }
        }

        return new GoStringValue(rendered);
    }

    /// <summary>Port of evaluateCondition (override.go:139-147).</summary>
    private static bool EvaluateCondition(string condition, OverrideRenderContext context)
        => condition.Length == 0
            || string.Equals(
                GoTrimSpace(GoTextTemplate.Render(condition, context)),
                "true",
                StringComparison.Ordinal);

    // ------------------------------------------------------------------
    // Body operations (override.go:268-507)
    // ------------------------------------------------------------------

    private static byte[]? ApplyBodyOperation(
        byte[]? body,
        ChannelOverrideOperation operation,
        OverrideRenderContext context,
        byte[]? originalBody)
    {
        if (!EvaluateCondition(operation.Condition, context))
        {
            return body;
        }

        return operation.Op switch
        {
            ChannelOverrideOps.Set => ApplyBodySet(body, operation, context),
            ChannelOverrideOps.SetIfAbsent => ApplyBodySetIfAbsent(body, operation, context, originalBody),
            ChannelOverrideOps.Delete => Delete(body, operation.Path),
            ChannelOverrideOps.Rename => ApplyBodyRename(body, operation),
            ChannelOverrideOps.Copy => ApplyBodyCopy(body, operation),
            ChannelOverrideOps.ArrayAppend => ApplyBodyArrayInsert(body, operation, context, ArrayInsertMode.End),
            ChannelOverrideOps.ArrayPrepend => ApplyBodyArrayInsert(body, operation, context, ArrayInsertMode.Start),
            ChannelOverrideOps.ArrayInsert => ApplyBodyArrayInsert(body, operation, context, ArrayInsertMode.Index),
            ChannelOverrideOps.ArrayRemove => ApplyBodyArrayRemove(body, operation),
            _ => body,
        };
    }

    private static byte[]? ApplyBodySet(
        byte[]? body,
        ChannelOverrideOperation operation,
        OverrideRenderContext context)
    {
        var renderedValue = RenderOverrideValue(operation.Value, context);

        if (renderedValue is GoStringValue { Value: ChannelOverrideOps.ClearDirective })
        {
            return Delete(body, operation.Path);
        }

        return Set(body, operation.Path, renderedValue);
    }

    private static byte[]? ApplyBodySetIfAbsent(
        byte[]? body,
        ChannelOverrideOperation operation,
        OverrideRenderContext context,
        byte[]? originalBody)
    {
        if (IsPresent(OverrideJson.Get(body ?? [], operation.Path)))
        {
            return body;
        }

        // A field the client sent may have been dropped or remapped by the
        // format transformation, so set_if_absent also consults the inbound
        // body: a field the client provided still counts as present.
        if (originalBody is not null && IsPresent(OverrideJson.Get(originalBody, operation.Path)))
        {
            return body;
        }

        return ApplyBodySet(body, operation, context);
    }

    private static byte[]? ApplyBodyRename(byte[]? body, ChannelOverrideOperation operation)
    {
        var result = OverrideJson.Get(body ?? [], operation.From);
        if (!result.Exists)
        {
            return body;
        }

        var withoutSource = Delete(body, operation.From);
        return Set(withoutSource, operation.To, result.ToGoValue());
    }

    private static byte[]? ApplyBodyCopy(byte[]? body, ChannelOverrideOperation operation)
    {
        var result = OverrideJson.Get(body ?? [], operation.From);
        if (!result.Exists)
        {
            return body;
        }

        return Set(body, operation.To, result.ToGoValue());
    }

    private static byte[]? ApplyBodyArrayInsert(
        byte[]? body,
        ChannelOverrideOperation operation,
        OverrideRenderContext context,
        ArrayInsertMode mode)
    {
        if (operation.Path.Length == 0)
        {
            throw new OverrideOperationException("array op requires a path");
        }

        var rendered = RenderOverrideValue(operation.Value, context);
        var splat = operation.Splat ?? true;

        List<GoValue> toInsert;
        if (splat && rendered is GoSliceValue renderedArray)
        {
            toInsert = renderedArray.Items;
        }
        else
        {
            toInsert = [rendered];
        }

        var existing = OverrideJson.Get(body ?? [], operation.Path);
        if (!existing.Exists)
        {
            // A missing path is created as a fresh array; an index is not
            // required (and would be clamped to 0 anyway).
            return Set(body, operation.Path, new GoSliceValue(toInsert));
        }

        if (existing.Node is not OverrideJsonArray array)
        {
            throw new OverrideOperationException($"path \"{operation.Path}\" is not an array");
        }

        // json.Unmarshal rejects numbers that do not fit a float64; the
        // reference then keeps the previous body.
        if (!AllNumbersFinite(array))
        {
            throw new OverrideOperationException($"decode array at \"{operation.Path}\"");
        }

        var current = array.ToGoValue() as GoSliceValue
            ?? throw new OverrideOperationException($"decode array at \"{operation.Path}\"");

        var position = mode switch
        {
            ArrayInsertMode.Start => 0,
            ArrayInsertMode.End => current.Items.Count,
            _ => ResolveInsertPosition(operation, current.Items.Count),
        };

        var merged = new List<GoValue>(current.Items.Count + toInsert.Count);
        merged.AddRange(current.Items.GetRange(0, position));
        merged.AddRange(toInsert);
        merged.AddRange(current.Items.GetRange(position, current.Items.Count - position));

        // The reference decodes the existing array into []any and re-marshals
        // it together with the new elements, so untouched elements are
        // normalized by encoding/json as well.
        return Set(body, operation.Path, new GoSliceValue(merged));
    }

    private static byte[]? ApplyBodyArrayRemove(byte[]? body, ChannelOverrideOperation operation)
    {
        if (operation.Path.Length == 0)
        {
            throw new OverrideOperationException("array_remove requires a path");
        }

        if (operation.Match is null)
        {
            throw new OverrideOperationException("array_remove requires a match");
        }

        if (GoTrimSpace(operation.Match.Path).Length == 0)
        {
            throw new OverrideOperationException("array_remove requires a match path");
        }

        if (GoTrimSpace(operation.Match.Eq).Length == 0)
        {
            throw new OverrideOperationException("array_remove requires a match eq value");
        }

        var existing = OverrideJson.Get(body ?? [], operation.Path);
        if (!existing.Exists)
        {
            return body;
        }

        if (existing.Node is not OverrideJsonArray array)
        {
            throw new OverrideOperationException($"path \"{operation.Path}\" is not an array");
        }

        var matchEq = GoTrimSpace(operation.Match.Eq);
        var kept = new List<OverrideJsonNode>(array.Items.Count);
        foreach (var item in array.Items)
        {
            var result = OverrideJson.Get(Encoding.UTF8.GetBytes(item.Raw), operation.Match.Path);
            if (result.Exists && string.Equals(result.StringValue, matchEq, StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(item);
        }

        return SetNode(body, operation.Path, new OverrideJsonArray(kept));
    }

    private static int ResolveInsertPosition(ChannelOverrideOperation operation, int length)
    {
        if (operation.Index is null)
        {
            throw new OverrideOperationException("array_insert requires an index");
        }

        var position = operation.Index.Value;
        if (position < 0)
        {
            position = length + position;
        }

        if (position < 0)
        {
            position = 0;
        }

        if (position > length)
        {
            position = length;
        }

        return (int)position;
    }

    /// <summary>gjson's Result.Exists plus the reference's explicit null check.</summary>
    private static bool IsPresent(JsonLookup lookup)
        => lookup.Exists
            || (lookup.Node is not null
                && string.Equals(lookup.Node.Raw, "null", StringComparison.Ordinal));

    /// <summary>encoding/json rejects NaN and infinities; this port must too.</summary>
    private static bool AllNumbersFinite(OverrideJsonNode node) => node switch
    {
        OverrideJsonScalar { Kind: OverrideJsonKind.Number } scalar => double.IsFinite(scalar.NumberValue),
        OverrideJsonObject obj => obj.Members.TrueForAll(static member => AllNumbersFinite(member.Value)),
        OverrideJsonArray array => array.Items.TrueForAll(AllNumbersFinite),
        _ => true,
    };

    private static byte[] Set(byte[]? body, string path, GoValue value)
    {
        if (!OverrideJson.TrySet(body ?? [], path, value, out var result, out var error))
        {
            throw new OverrideOperationException(error ?? "failed to set value");
        }

        return result;
    }

    private static byte[] SetNode(byte[]? body, string path, OverrideJsonNode value)
    {
        if (!OverrideJson.TrySetNode(body ?? [], path, value, out var result, out var error))
        {
            throw new OverrideOperationException(error ?? "failed to set value");
        }

        return result;
    }

    private static byte[]? Delete(byte[]? body, string path)
    {
        if (!OverrideJson.TryDelete(body ?? [], path, out var result, out var error))
        {
            throw new OverrideOperationException(error ?? "failed to delete value");
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Header operations (override.go:509-552)
    // ------------------------------------------------------------------

    private static void ApplyHeaderOperation(
        Dictionary<string, List<string>> headers,
        ChannelOverrideOperation operation,
        OverrideRenderContext context)
    {
        if (!EvaluateCondition(operation.Condition, context))
        {
            return;
        }

        switch (operation.Op)
        {
            case ChannelOverrideOps.Set:
                {
                    var renderedValue = GoTextTemplate.Render(operation.Value, context);
                    if (string.Equals(renderedValue, ChannelOverrideOps.ClearDirective, StringComparison.Ordinal))
                    {
                        HttpClientUtils.HeaderDel(headers, operation.Path);
                        return;
                    }

                    HttpClientUtils.HeaderSet(headers, operation.Path, renderedValue);
                    return;
                }

            case ChannelOverrideOps.Delete:
                HttpClientUtils.HeaderDel(headers, operation.Path);
                return;

            case ChannelOverrideOps.Rename:
                {
                    var values = HeaderValues(headers, operation.From);
                    if (values.Count == 0)
                    {
                        return;
                    }

                    HttpClientUtils.HeaderDel(headers, operation.From);
                    foreach (var value in values)
                    {
                        HttpClientUtils.HeaderAdd(headers, operation.To, value);
                    }

                    return;
                }

            case ChannelOverrideOps.Copy:
                foreach (var value in HeaderValues(headers, operation.From))
                {
                    HttpClientUtils.HeaderAdd(headers, operation.To, value);
                }

                return;

            default:
                // Unknown header operations are ignored (the reference logs a warning).
                return;
        }
    }

    /// <summary>Port of http.Header.Values: the whole canonical-key slice.</summary>
    private static List<string> HeaderValues(Dictionary<string, List<string>> headers, string key)
        => headers.TryGetValue(HttpClientUtils.CanonicalHeaderKey(key), out var values)
            ? [.. values]
            : [];

    // ------------------------------------------------------------------
    // Request-body gate (override.go:226-266)
    // ------------------------------------------------------------------

    private static bool BodyOverrideSupported(HttpRequestData request)
    {
        if (request.Body is not { Length: > 0 })
        {
            return false;
        }

        // The reference prefers the explicit field and falls back to the header.
        var contentType = request.ContentType;
        if (string.IsNullOrEmpty(contentType))
        {
            contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        }

        if (!string.IsNullOrEmpty(contentType))
        {
            var mediaType = ParseMediaType(contentType);
            if (mediaType is not null && !IsJsonMediaType(mediaType))
            {
                return false;
            }
        }

        return OverrideJson.IsValid(request.Body);
    }

    /// <summary>
    /// Port of mime.ParseMediaType's media-type half: the part before the first
    /// semicolon, trimmed, split as token "/" token. Returns null when Go would
    /// report a parse error, which makes the caller skip the media-type gate.
    /// </summary>
    private static string? ParseMediaType(string contentType)
    {
        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = GoTrimSpace(separator >= 0 ? contentType[..separator] : contentType);
        var slash = mediaType.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == mediaType.Length - 1)
        {
            return null;
        }

        if (!IsMimeToken(mediaType[..slash]) || !IsMimeToken(mediaType[(slash + 1)..]))
        {
            return null;
        }

        return mediaType.ToLowerInvariant();
    }

    /// <summary>Port of mime's isTokenChar (RFC 7230 token characters).</summary>
    private static bool IsMimeToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isToken = c is >= '0' and <= '9'
                or >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!isToken)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of isJSONMediaType (override.go:260-266).</summary>
    private static bool IsJsonMediaType(string mediaType)
        => mediaType is "application/json" or "text/json"
            || mediaType.EndsWith("+json", StringComparison.Ordinal);

    private static byte[]? ReadOriginalBody(LlmRequest llmRequest)
    {
        var body = llmRequest.RawRequest?.Body;
        return body is { Length: > 0 } && OverrideJson.IsValid(body) ? body : null;
    }

    // ------------------------------------------------------------------
    // Go string helpers
    // ------------------------------------------------------------------

    /// <summary>Port of strings.TrimSpace (unicode.IsSpace, minus .NET's 0x1C-0x1F).</summary>
    private static string GoTrimSpace(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsGoSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsGoSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsGoSpace(char c)
        => c is not ('\u001c' or '\u001d' or '\u001e' or '\u001f') && char.IsWhiteSpace(c);

    private enum ArrayInsertMode
    {
        Start,
        End,
        Index,
    }
}

/// <summary>Port of applyOverrideRequestBody's middleware wrapper.</summary>
internal sealed class ChannelOverrideBodyMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("override-request-body")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        var candidate = outbound.CurrentCandidate;
        if (candidate is null)
        {
            return request;
        }

        ChannelOverrideApplier.ApplyBodyOperations(
            request,
            outbound.CurrentLlmRequest,
            outbound.OriginalModel,
            candidate.OverrideSettings.BodyOperations);

        return request;
    }
}

/// <summary>Port of applyOverrideRequestHeaders's middleware wrapper.</summary>
internal sealed class ChannelOverrideHeaderMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("override-request-headers")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = outbound.CurrentCandidate;
        if (candidate is null)
        {
            return request;
        }

        ChannelOverrideApplier.ApplyHeaderOperations(
            request,
            outbound.CurrentLlmRequest,
            outbound.OriginalModel,
            candidate.OverrideSettings.HeaderOperations);

        return request;
    }
}
