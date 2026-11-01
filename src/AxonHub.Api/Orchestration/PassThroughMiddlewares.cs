using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
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

    /// <summary>Port of passThroughStreamAligned.</summary>
    public static bool StreamAligned(bool? originalStream, bool? effectiveStream)
        => (originalStream ?? false) == (effectiveStream ?? false);

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

        if (!PassThrough.IsEnabled(outbound, settings))
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
                llmRequest.RawRequest.Body,
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
        => outbound.CurrentTransformer is not IPassThroughBodyPolicy policy
            || policy.AllowPassThroughBody(llmRequest, providerRequest);
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
            || !string.Equals(llmRequest.ApiFormat, ApiFormats.OpenAIResponse, StringComparison.Ordinal)
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
