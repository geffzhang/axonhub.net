using System.Text;
using System.Text.Json;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the request half of orchestrator/pass_through.go: reuses the client's
/// inbound body when the channel enables pass-through and the inbound and
/// outbound API formats are identical, forwards the Codex Responses metadata
/// that travels with such a body, and applies the User-Agent policy.
/// </summary>
/// <remarks>
/// Response and stream pass-through live in
/// <c>PassThroughStreamMiddlewares.cs</c>. Prompt-protection patching of the
/// replayed body lives in <see cref="PassThroughPromptProtection"/> and runs
/// after the model patch, exactly like the reference's
/// mergePassThroughRequestBodyWithPromptProtection.
/// </remarks>
internal static class PassThrough
{
    /// <summary>
    /// Codex identity metadata that can accompany a pass-through body. An
    /// explicit allowlist: inbound credentials, transport headers and
    /// protocol-selection headers are never copied.
    /// </summary>
    public static readonly string[] CodexResponsesPassThroughHeaders =
    [
        "X-Codex-Turn-Metadata",
        "X-Codex-Turn-State",
        "X-Codex-Window-Id",
        "X-Client-Request-Id",
        "X-Codex-Beta-Features",
        "Session-Id",
        "Originator",
        "Thread-Id",
    ];

    /// <summary>The paths preserveResponsesModel patches, in reference order.</summary>
    private static readonly string[] ResponsesModelPaths = ["model", "response.model"];

    private static readonly JsonReaderOptions ResponsesReaderOptions = new() { MaxDepth = 10_000 };

    /// <summary>Port of passThroughStreamAligned.</summary>
    public static bool StreamAligned(bool? originalStream, bool? effectiveStream)
        => (originalStream ?? false) == (effectiveStream ?? false);

    /// <summary>Port of responsestransformer.IsResponsesFormat.</summary>
    public static bool IsResponsesFormat(string? apiFormat)
        => ResponsesProtocolCodec.IsResponsesFormat(apiFormat);

    /// <summary>
    /// Port of responsesTransportAdaptationEnabled: the client and provider
    /// speak different Responses transports (HTTP vs WebSocket).
    /// </summary>
    public static bool TransportAdaptationEnabled(PersistentOutboundTransformer? outbound)
    {
        if (outbound?.CurrentLlmRequest is not { } llmRequest
            || outbound.RawProviderRequest is not { } providerRequest)
        {
            return false;
        }

        var source = llmRequest.ApiFormat;
        var target = providerRequest.ApiFormat;
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            return false;
        }

        return ResponsesProtocolCodec.IsResponsesFormat(source)
            && ResponsesProtocolCodec.IsResponsesFormat(target);
    }

    /// <summary>
    /// Port of responsesIdentityPreservationEnabled: both sides speak the same
    /// Responses transport, so only the model echoed in provider payloads is
    /// restored to the client's value.
    /// </summary>
    public static bool IdentityPreservationEnabled(PersistentOutboundTransformer? outbound)
    {
        if (outbound?.CurrentLlmRequest is not { } llmRequest
            || outbound.RawProviderRequest is not { } providerRequest)
        {
            return false;
        }

        return string.Equals(llmRequest.ApiFormat, providerRequest.ApiFormat, StringComparison.Ordinal)
            && ResponsesProtocolCodec.IsResponsesFormat(llmRequest.ApiFormat);
    }

    /// <summary>
    /// Port of responsesClientModel: gjson's <c>model</c> read of the inbound
    /// body (or its parsed form when the raw body is empty).
    /// </summary>
    public static string ResponsesClientModel(PersistentOutboundTransformer? outbound)
    {
        var request = outbound?.CurrentLlmRequest?.RawRequest;
        if (request is null)
        {
            return string.Empty;
        }

        var body = request.Body;
        if (body is null || body.Length == 0)
        {
            body = request.JsonBody;
        }

        return body is null || body.Length == 0
            ? string.Empty
            : OverrideJson.Get(body, "model").StringValue;
    }

    /// <summary>
    /// Port of preserveResponsesModel: rewrite the model the provider echoed
    /// back to the client's requested value. Each patch touches only the
    /// matched value's bytes; a failed patch keeps the original body.
    /// </summary>
    public static byte[] PreserveResponsesModel(byte[]? body, string clientModel)
    {
        var data = body ?? [];
        if (clientModel.Length == 0)
        {
            return (byte[])data.Clone();
        }

        var patched = (byte[])data.Clone();
        foreach (var path in ResponsesModelPaths)
        {
            if (!OverrideJson.Get(patched, path).Exists)
            {
                continue;
            }

            if (!TrySetResponsesModelValue(patched, path, clientModel, out var next))
            {
                return (byte[])data.Clone();
            }

            patched = next;
        }

        return patched;
    }

    /// <summary>Port of preserveResponsesStreamEventModel.</summary>
    public static StreamEvent? PreserveResponsesStreamEventModel(StreamEvent? @event, string clientModel)
    {
        if (@event is null || clientModel.Length == 0)
        {
            return @event;
        }

        return new StreamEvent
        {
            LastEventId = @event.LastEventId,
            Type = @event.Type,
            Data = PreserveResponsesModel(@event.Data, clientModel),
            CleanEofCompletionEvidence = @event.CleanEofCompletionEvidence,
            Size = @event.Size,
            Headers = @event.Headers,
        };
    }

    /// <summary>Port of allowPassThroughBody.</summary>
    public static bool AllowPassThroughBody(
        PersistentOutboundTransformer outbound,
        LlmRequest? llmRequest,
        HttpRequestData? providerRequest)
        => outbound.CurrentTransformer is not IPassThroughBodyPolicy policy
            || policy.AllowPassThroughBody(llmRequest, providerRequest);

    /// <summary>Port of allowPassThroughResponse.</summary>
    public static bool AllowPassThroughResponse(PersistentOutboundTransformer outbound)
        => outbound.CurrentTransformer is not IPassThroughResponsePolicy policy
            || policy.AllowPassThroughResponse(
                outbound.CurrentLlmRequest,
                outbound.RawProviderRequest);

    /// <summary>
    /// Port of isPassThroughEnabled: the effective flag is the channel-level
    /// setting when present, otherwise the global system setting.
    /// </summary>
    public static bool IsEnabled(
        PersistentOutboundTransformer outbound,
        PassThroughSettings settings)
    {
        ArgumentNullException.ThrowIfNull(outbound);

        var candidate = outbound.CurrentCandidate;
        if (candidate is null)
        {
            return false;
        }

        var rawRequest = outbound.RawProviderRequest;
        if (rawRequest is null || rawRequest.ApiFormat.Length == 0)
        {
            return false;
        }

        var llmRequest = outbound.CurrentLlmRequest;
        if (llmRequest is null
            || !string.Equals(llmRequest.ApiFormat ?? string.Empty, rawRequest.ApiFormat, StringComparison.Ordinal))
        {
            return false;
        }

        if (!StreamAligned(outbound.OriginalRequestStream, llmRequest.Stream))
        {
            return false;
        }

        return candidate.PassThroughSettings?.PassThroughBody ?? settings.PassThrough;
    }

    /// <summary>Port of passThroughBodySupported.</summary>
    public static bool BodySupported(LlmRequest llmRequest)
    {
        ArgumentNullException.ThrowIfNull(llmRequest);

        switch (llmRequest.ApiFormat)
        {
            case ApiFormats.OpenAITranscription:
            case ApiFormats.OpenAITranslation:
            case ApiFormats.OpenAIImageVariation:
                return false;

            case ApiFormats.OpenAIImageEdit:
                // Only the image-edit inbound accepts JSON today; the other
                // formats never reach here because their inbounds still
                // require multipart.
                if (llmRequest.RawRequest is null)
                {
                    return false;
                }

                return GoMimeTypes.TryParseMediaType(
                        HttpClientUtils.HeaderGet(llmRequest.RawRequest.Headers, "Content-Type"),
                        out var mediaType,
                        out _)
                    && string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);

            case ApiFormats.OpenAIVideo:
                if (llmRequest.RawRequest is null)
                {
                    return false;
                }

                return !HttpClientUtils
                    .HeaderGet(llmRequest.RawRequest.Headers, "Content-Type")
                    .ToLowerInvariant()
                    .StartsWith("multipart/", StringComparison.Ordinal);

            default:
                return true;
        }
    }

    /// <summary>Port of passThroughBodyNeedsModelPatch.</summary>
    public static bool NeedsModelPatch(string? apiFormat) => apiFormat switch
    {
        ApiFormats.OpenAIChatCompletion
            or ApiFormats.OpenAICompletion
            or ApiFormats.OpenAIResponse
            or ApiFormats.OpenAIResponseWebSocket
            or ApiFormats.OpenAIResponseCompact
            or ApiFormats.OpenAIEmbedding
            or ApiFormats.OpenAIModeration
            or ApiFormats.OpenAIAlphaSearch
            or ApiFormats.OpenAIDecisions
            or ApiFormats.OpenAIImageGeneration
            or ApiFormats.OpenAIVideo
            or ApiFormats.JinaEmbedding
            or ApiFormats.JinaRerank
            or ApiFormats.AnthropicMessage
            // Speech (TTS) has a JSON body with a model field; transcription and
            // translation use multipart bodies that cannot be patched via sjson.
            or ApiFormats.OpenAISpeech
            // JSON image edits carry a top-level model field; multipart edit
            // bodies never reach here (BodySupported rejects them).
            or ApiFormats.OpenAIImageEdit
            or ApiFormats.TypeSafeSystemOne => true,
        _ => false,
    };

    /// <summary>
    /// Port of mergePassThroughRequestBody: copy the raw body, then write the
    /// mapped model into it for formats that encode the model in the body.
    /// </summary>
    public static bool TryMergeRequestBody(
        byte[]? rawBody,
        string? apiFormat,
        string model,
        out byte[] body,
        out string? error)
    {
        body = rawBody is null ? [] : (byte[])rawBody.Clone();
        error = null;

        if (!NeedsModelPatch(apiFormat) || model.Length == 0)
        {
            return true;
        }

        // sjson treats an empty or whitespace-only document as "{}"; the
        // override json port rejects it as invalid, so normalize first.
        var source = IsBlank(body) ? "{}"u8.ToArray() : body;

        if (!OverrideJson.TrySet(
                source,
                "model",
                new GoStringValue(model),
                out var next,
                out var setError))
        {
            error = "set model in pass-through body: " + setError;
            return false;
        }

        body = next;
        return true;
    }

    /// <summary>
    /// Port of mergePassThroughRequestBodyWithPromptProtection: preserves the
    /// original JSON body while applying model mapping and the matched
    /// prompt-protection mask rules. A patch failure keeps the generated body.
    /// </summary>
    public static bool TryMergeRequestBodyWithPromptProtection(
        byte[]? rawBody,
        string? apiFormat,
        string model,
        IReadOnlyList<PromptProtection.PromptProtectionRule> rules,
        out byte[] body,
        out string? error)
    {
        if (!TryMergeRequestBody(rawBody, apiFormat, model, out body, out error))
        {
            return false;
        }

        if (error is not null || rules.Count == 0)
        {
            return true;
        }

        if (!PassThroughPromptProtection.TryPatch(body, apiFormat, rules, out var patched, out error))
        {
            return false;
        }

        body = patched;
        return true;
    }

    private static bool IsBlank(byte[] body)
    {
        foreach (var value in body)
        {
            if (value > ' ')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Port of sjson.SetBytes for the two fixed model paths: only the matched
    /// value's byte range is replaced so every other byte of the provider
    /// payload survives. <see cref="OverrideJson.TrySet"/> re-renders the whole
    /// document, which identity preservation must not do (C039).
    /// </summary>
    private static bool TrySetResponsesModelValue(
        ReadOnlySpan<byte> document,
        string path,
        string value,
        out byte[] patched)
    {
        patched = [];
        var parts = path.Split('.');

        if (!TryFindPathValue(document, 0, parts, 0, out var start, out var end))
        {
            return false;
        }

        var quoted = ResponsesModelJsonValue(value);
        var result = new byte[document.Length - (end - start) + quoted.Length];
        document[..start].CopyTo(result);
        quoted.CopyTo(result.AsSpan(start));
        document[end..].CopyTo(result.AsSpan(start + quoted.Length));
        patched = result;
        return true;
    }

    /// <summary>
    /// gjson object-path lookup: the first member with the exact key wins and
    /// arrays are only reachable by index (an object key on an array does not
    /// resolve).
    /// </summary>
    private static bool TryFindPathValue(
        ReadOnlySpan<byte> document,
        int offset,
        string[] parts,
        int depth,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;

        var reader = new Utf8JsonReader(document[offset..], ResponsesReaderOptions);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return false;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            if (!reader.Read())
            {
                return false;
            }

            var valueStart = offset + checked((int)reader.TokenStartIndex);
            if (string.Equals(key, parts[depth], StringComparison.Ordinal))
            {
                if (depth + 1 == parts.Length)
                {
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    {
                        reader.Skip();
                    }

                    start = valueStart;
                    end = offset + checked((int)reader.BytesConsumed);
                    return true;
                }

                return reader.TokenType == JsonTokenType.StartObject
                    && TryFindPathValue(document, valueStart, parts, depth + 1, out start, out end);
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
            }
        }

        return false;
    }

    /// <summary>
    /// sjson's encoding for a string value: printable ASCII without quote or
    /// backslash is emitted verbatim; everything else goes through Go's
    /// json.Marshal (HTML escaping included).
    /// </summary>
    private static byte[] ResponsesModelJsonValue(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var simple = true;
        foreach (var b in utf8)
        {
            if (b < ' ' || b > 0x7F || b == '"' || b == '\\')
            {
                simple = false;
                break;
            }
        }

        if (!simple)
        {
            // json.Marshal(string): HTML escaping, lowercase hex control
            // escapes and raw UTF-8 for everything else.
            return Encoding.UTF8.GetBytes(GoJson.Quote(value));
        }

        var quoted = new byte[utf8.Length + 2];
        quoted[0] = (byte)'"';
        utf8.CopyTo(quoted.AsSpan(1));
        quoted[^1] = (byte)'"';
        return quoted;
    }
}

/// <summary>
/// Port of applyPassThroughRequestBody. Runs before the override middlewares so
/// override operations can still modify the pass-through body. Always stores
/// the outbound provider request so the pass-through checks see the emitted API
/// format.
/// </summary>
internal sealed class PassThroughRequestBodyMiddleware(
    PersistentOutboundTransformer outbound,
    PassThroughSettings settings)
    : DummyMiddleware("pass-through-request-body")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        outbound.RawProviderRequest = request;

        // Responses transport adaptation (HTTP <-> WebSocket) replays the
        // inbound body too, even when the channel has pass-through disabled.
        if (!PassThrough.IsEnabled(outbound, settings)
            && !PassThrough.TransportAdaptationEnabled(outbound))
        {
            return request;
        }

        var llmRequest = outbound.CurrentLlmRequest;
        if (llmRequest is null)
        {
            // The reference dereferences state.LlmRequest; the port's pipeline
            // never reaches this hook without one, so guard instead of crashing.
            return request;
        }

        if (!AllowPassThroughBody(llmRequest, request))
        {
            return request;
        }

        // Multipart bodies cannot be reused: the outbound transformer rebuilds
        // the payload with a new boundary in Content-Type, so replaying the
        // inbound bytes would mismatch the header.
        if (!PassThrough.BodySupported(llmRequest))
        {
            return request;
        }

        if (llmRequest.RawRequest is null)
        {
            // Same defensive guard as above; the reference would panic here.
            return request;
        }

        if (!PassThrough.TryMergeRequestBodyWithPromptProtection(
                outbound.EffectiveResponsesRequestBody(),
                llmRequest.ApiFormat,
                llmRequest.Model,
                outbound.PromptProtectionMaskRules,
                out var body,
                out _))
        {
            // Keep the outbound body when the raw body cannot be patched.
            return request;
        }

        // The replay is only trusted when the inbound mapping reads back
        // exactly the prompts prompt protection produced. Partial native
        // patches and regex semantics that span JSON values fall back to the
        // generated (already-protected) body.
        if (outbound.PromptProtectionBodyCheck is { } bodyCheck
            && bodyCheck.Validate(body) is not null)
        {
            return request;
        }

        request.Body = body;

        // The replayed body keeps the inbound media type: sync Content-Type so
        // a JSON image edit is not sent with the multipart header the outbound
        // transformer built.
        var contentType = HttpClientUtils.HeaderGet(
            llmRequest.RawRequest.Headers,
            "Content-Type");
        if (contentType.Length > 0)
        {
            request.Headers ??= [];
            HttpClientUtils.HeaderSet(request.Headers, "Content-Type", contentType);
            request.ContentType = contentType;
        }

        outbound.PassThroughApplied = true;

        return request;
    }

    /// <summary>Port of allowPassThroughBody.</summary>
    private bool AllowPassThroughBody(LlmRequest llmRequest, HttpRequestData providerRequest)
        => PassThrough.AllowPassThroughBody(outbound, llmRequest, providerRequest);
}

/// <summary>
/// Port of applyResponsesTransportRequestBody
/// (orchestrator/pass_through.go:159-208): between two Responses transports
/// (HTTP vs WebSocket) the inbound payload is replayed instead of round-tripped
/// through the unified model, so provider item IDs, unknown fields and raw item
/// bytes survive. Lane routing belongs to the caller; stream_id is dropped.
/// </summary>
internal sealed class ResponsesTransportRequestBodyMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("responses-transport-request-body")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!PassThrough.TransportAdaptationEnabled(outbound))
        {
            return request;
        }

        var llmRequest = outbound.CurrentLlmRequest;
        if (llmRequest is null)
        {
            // The gating above already proved it non-null; the reference
            // dereferences state.LlmRequest here.
            return request;
        }

        if (!PassThrough.AllowPassThroughBody(outbound, llmRequest, request))
        {
            return request;
        }

        var source = llmRequest.ApiFormat;
        var target = request.ApiFormat;

        if (!PassThrough.TryMergeRequestBodyWithPromptProtection(
                outbound.EffectiveResponsesRequestBody(),
                source,
                llmRequest.Model,
                outbound.PromptProtectionMaskRules,
                out var body,
                out _))
        {
            // The reference logs and keeps the outbound body.
            return request;
        }

        if (outbound.PromptProtectionBodyCheck is { } bodyCheck
            && bodyCheck.Validate(body) is not null)
        {
            return request;
        }

        body = ResponsesProtocolCodec.AdaptRequestBody(body, source, target);

        if (!PassThrough.TryMergeRequestBody(body, target, llmRequest.Model, out var adapted, out var error))
        {
            throw new InvalidOperationException(error);
        }

        request.Body = adapted;
        request.ContentType = "application/json";
        request.Headers ??= [];
        HttpClientUtils.HeaderSet(request.Headers, "Content-Type", "application/json");
        outbound.PassThroughApplied = true;

        return request;
    }
}

/// <summary>
/// Port of applyPassThroughRequestHeaders: forwards Codex identity metadata
/// paired with a pass-through body. Protocol-selection headers such as
/// Responses Lite are deliberately excluded.
/// </summary>
internal sealed class PassThroughRequestHeadersMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("pass-through-request-headers")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var llmRequest = outbound.CurrentLlmRequest;
        if (!outbound.PassThroughApplied
            || llmRequest is null
            || !PassThrough.IsResponsesFormat(llmRequest.ApiFormat)
            || llmRequest.RawRequest is null)
        {
            return request;
        }

        request.Headers ??= [];

        var inboundHeaders = llmRequest.RawRequest.Headers;
        foreach (var header in PassThrough.CodexResponsesPassThroughHeaders)
        {
            var values = HeaderValues(inboundHeaders, header);
            if (values.Count == 0)
            {
                continue;
            }

            HttpClientUtils.HeaderDel(request.Headers, header);
            foreach (var value in values)
            {
                HttpClientUtils.HeaderAdd(request.Headers, header, value);
            }
        }

        return request;
    }

    /// <summary>Port of <c>http.Header.Values</c> on a canonical-keyed map.</summary>
    private static List<string> HeaderValues(
        Dictionary<string, List<string>>? headers,
        string key)
        => headers is not null
            && headers.TryGetValue(HttpClientUtils.CanonicalHeaderKey(key), out var values)
            ? values
            : [];
}

/// <summary>
/// Port of applyUserAgentPassThrough. Runs before the header overrides so it
/// sets the initial User-Agent (client value or the <c>axonhub/1.0</c>
/// default); a provider-required User-Agent set by the outbound transformer is
/// preserved when pass-through is disabled.
/// </summary>
internal sealed class UserAgentPassThroughMiddleware(
    PersistentOutboundTransformer outbound,
    PassThroughSettings settings)
    : DummyMiddleware("user-agent-pass-through")
{
    /// <summary>The reference's default downstream User-Agent.</summary>
    public const string DefaultUserAgent = "axonhub/1.0";

    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = outbound.CurrentCandidate;
        if (candidate is null)
        {
            return request;
        }

        var passThroughEnabled = candidate.PassThroughSettings?.PassThroughUserAgent
            ?? settings.UserAgentPassThrough;

        request.Headers ??= [];

        if (passThroughEnabled)
        {
            var llmRequest = outbound.CurrentLlmRequest;
            if (llmRequest?.RawRequest is not null)
            {
                var clientUserAgent = HttpClientUtils.HeaderGet(
                    llmRequest.RawRequest.Headers,
                    "User-Agent");
                if (clientUserAgent.Length > 0)
                {
                    HttpClientUtils.HeaderSet(request.Headers, "User-Agent", clientUserAgent);
                }
            }
        }
        else if (HttpClientUtils.HeaderGet(request.Headers, "User-Agent").Length == 0)
        {
            HttpClientUtils.HeaderSet(request.Headers, "User-Agent", DefaultUserAgent);
        }

        return request;
    }
}
