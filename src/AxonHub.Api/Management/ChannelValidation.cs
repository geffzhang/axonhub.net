using System.Text.RegularExpressions;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using GoCast = AxonHub.Llm.Json.GoCast;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel write validators and normalizers used by
/// biz.ChannelService.createChannel (channel.go:525-671,
/// channel_endpoint.go:41-200, channel_override.go:181-266,
/// channel_rate_limit.go, channel_response_timeout.go, channel_llm.go:1500-1562,
/// provider_quota/commandcode_checker.go:249-321).
/// </summary>
/// <remarks>
/// Every method returns the reference's error text or null, so the caller can
/// add the frozen <c>fmt.Errorf("...: %w", err)</c> prefix at exactly the same
/// place as the reference.
/// </remarks>
public static class ChannelValidation
{
    private static readonly HashSet<string> SupportedApiFormats = new(StringComparer.Ordinal)
    {
        ApiFormats.OpenAIChatCompletion,
        ApiFormats.OpenAICompletion,
        ApiFormats.OpenAIResponse,

        // Live extension: the migrated management frontend and the live
        // endpoint validation accept the Responses WebSocket format (C139/C144).
        ApiFormats.OpenAIResponseWebSocket,
        ApiFormats.OpenAIResponseCompact,
        ApiFormats.OpenAIEmbedding,
        ApiFormats.OpenAIImageGeneration,
        ApiFormats.OpenAIImageEdit,
        ApiFormats.OpenAIImageVariation,
        ApiFormats.OpenAIVideo,
        ApiFormats.ZenmuxVideo,
        ApiFormats.OpenAISpeech,
        ApiFormats.OpenAITranscription,
        ApiFormats.OpenAITranslation,
        ApiFormats.OpenAIModeration,
        ApiFormats.OpenAIAlphaSearch,
        ApiFormats.OpenAIDecisions,
        ApiFormats.AnthropicMessage,
        ApiFormats.GeminiContents,
        ApiFormats.GeminiEmbedding,
        ApiFormats.JinaRerank,
        ApiFormats.JinaEmbedding,
        ApiFormats.TypeSafeSystemOne,
    };

    private static readonly HashSet<string> ZenmuxChannelTypes = new(StringComparer.Ordinal)
    {
        "zenmux",
        "zenmux_responses",
        "zenmux_anthropic",
        "zenmux_gemini",
        "zenmux_video",
    };

    private static readonly HashSet<string> CommandCodeChannelTypes = new(StringComparer.Ordinal)
    {
        "commandcode",
        "commandcode_anthropic",
    };

    private static readonly Regex VertexResourceSegment =
        new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The six allowlisted Command Code session cookies
    /// (commandcode_checker.go:38-45). Lookups are case-insensitive.
    /// </summary>
    private static readonly HashSet<string> CommandCodeCookieNames =
        new(StringComparer.Ordinal)
        {
            "__secure-commandcode_prod_.session_token",
            "__host-commandcode_prod_.session_token",
            "commandcode_prod_.session_token",
            "__secure-commandcode_prod_.session_data",
            "__host-commandcode_prod_.session_data",
            "commandcode_prod_.session_data",
        };

    /// <summary>channel.TypeXaiSubscription's official base URL.</summary>
    public const string XaiSubscriptionBaseUrl = "https://cli-chat-proxy.grok.com/v1";

    public static bool IsCommandCodeChannelType(string channelType)
        => CommandCodeChannelTypes.Contains(channelType);

    public static bool IsZenmuxChannelType(string channelType)
        => ZenmuxChannelTypes.Contains(channelType);

    /// <summary>
    /// Port of normalizeCommandCodeQuotaCookieSettings (channel.go:1357-1382):
    /// canonicalizes the cookie on the two Command Code types and clears the
    /// block everywhere else. Returns the reference error text, if any.
    /// </summary>
    public static string? NormalizeCommandCodeQuotaCookieSettings(
        ChannelSettingsDraft? settings,
        string channelType)
    {
        if (settings?.ProviderQuota?.CommandCode is null)
        {
            return null;
        }

        if (!IsCommandCodeChannelType(channelType))
        {
            ClearCommandCodeQuotaSettings(settings);
            return null;
        }

        var error = NormalizeCommandCodeCookie(
            settings.ProviderQuota.CommandCode.AuthCookie ?? string.Empty,
            out var cookie);
        if (error is not null)
        {
            return "invalid Command Code quota auth cookie: " + error;
        }

        settings.ProviderQuota.CommandCode.AuthCookie = cookie;
        return null;
    }

    /// <summary>
    /// Port of clearCommandCodeQuotaSettings (channel.go:1389-1404): drops the
    /// Command Code block and the now-empty provider quota object.
    /// </summary>
    public static void ClearCommandCodeQuotaSettings(ChannelSettingsDraft settings)
    {
        if (settings.ProviderQuota is null)
        {
            return;
        }

        settings.ProviderQuota.CommandCode = null;
        if (settings.ProviderQuota.Ollama is null)
        {
            settings.ProviderQuota = null;
        }
    }

    /// <summary>
    /// Port of commandCodeQuotaCookieIsBlank (channel.go:1364-1369): the
    /// explicit "clear the stored quota cookie" signal.
    /// </summary>
    public static bool CommandCodeQuotaCookieIsBlank(ChannelSettingsDraft? settings)
        => settings?.ProviderQuota?.CommandCode is { } commandCode
            && string.IsNullOrWhiteSpace(commandCode.AuthCookie);

    /// <summary>Port of validateCommandCodeBaseURL (channel_llm.go:690-697).</summary>
    public static string? ValidateCommandCodeBaseUrl(string rawUrl)
    {
        var trimmed = rawUrl.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || parsed.Scheme != "https"
            || parsed.Host.Length == 0)
        {
            return "Command Code base URL must use HTTPS";
        }

        return null;
    }

    /// <summary>
    /// Port of NormalizeAPIKeyAutoDisableRules (channel.go:731-757). Mutates the
    /// policies draft; returns the raw rule error (no prefix in the reference).
    /// </summary>
    public static string? NormalizeApiKeyAutoDisableRules(ChannelPoliciesDraft? policies)
    {
        if (policies is null)
        {
            return null;
        }

        if (policies.ApiKeyAutoDisableMode == "inherit")
        {
            policies.ApiKeyAutoDisableRules = null;
            return null;
        }

        if (policies.ApiKeyAutoDisableMode == "custom"
            && (policies.ApiKeyAutoDisableRules is null
                || policies.ApiKeyAutoDisableRules.Count == 0))
        {
            policies.ApiKeyAutoDisableMode = "inherit";
            return null;
        }

        if (policies.ApiKeyAutoDisableRules is null
            || policies.ApiKeyAutoDisableRules.Count == 0)
        {
            return null;
        }

        IReadOnlyList<AutoDisableRule> normalized;
        try
        {
            normalized = RetryPolicyCodec.NormalizeAutoDisableRules(
                policies.ApiKeyAutoDisableRules.Select(ToAutoDisableRule).ToList(),
                allowDelete: true);
        }
        catch (AutoDisableRuleValidationException ex)
        {
            return ex.Message;
        }

        policies.ApiKeyAutoDisableRules = normalized.Select(ToDraft).ToList();
        return null;
    }

    /// <summary>
    /// Port of RemoveRemovedModelProtocolOverrides (channel_endpoint.go:143-197).
    /// Only runs for a non-empty model list; mutates the draft when an override
    /// no longer names an exposed model.
    /// </summary>
    /// <returns>Whether an override was pruned (the reference's bool result).</returns>
    public static bool RemoveRemovedModelProtocolOverrides(
        ChannelSettingsDraft settings,
        IReadOnlyList<string> supportedModels)
    {
        if (supportedModels.Count == 0
            || settings.ModelProtocols is null
            || settings.ModelProtocols.Count == 0)
        {
            return false;
        }

        // The reference probes biz.Channel.GetModelEntries, which is derived
        // from SupportedModels and Settings (prefix/auto-trim/mapping aliases).
        var descriptor = new ChannelDescriptor
        {
            Id = 0,
            Name = string.Empty,
            OrderingWeight = 0,
            Type = string.Empty,
            SupportedModels = GoJson.MarshalToString(supportedModels),
            Settings = GoJson.MarshalToString(settings),
        };
        var entries = ChannelModelEntries.Build(descriptor);

        var available = new HashSet<string>(entries.Keys, StringComparer.Ordinal);
        foreach (var model in supportedModels)
        {
            available.Add(model);
            if (settings.LowercaseModelId)
            {
                available.Add(model.ToLowerInvariant());
            }
        }

        var kept = settings.ModelProtocols
            .Where(protocol => available.Contains(protocol.Model))
            .ToList();
        if (kept.Count != settings.ModelProtocols.Count)
        {
            settings.ModelProtocols = kept;
            return true;
        }

        return false;
    }

    /// <summary>Port of ValidateBodyOverrideOperations (channel_override.go:181-238).</summary>
    public static string? ValidateBodyOverrideOperations(
        IReadOnlyList<OverrideOperationDraft> operations)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            switch (op.Op)
            {
                case "set":
                case "set_if_absent":
                    if ((op.Path ?? string.Empty).Trim().Length == 0)
                    {
                        return $"body operation at index {i} ({op.Op}) has an empty path";
                    }

                    if (op.Op == "set_if_absent"
                        && (op.Value ?? string.Empty).Trim().Length == 0)
                    {
                        return $"body operation at index {i} (set_if_absent) has an empty value";
                    }

                    if (string.Equals(op.Path, "stream", StringComparison.OrdinalIgnoreCase))
                    {
                        return "override parameters cannot contain the field \"stream\"";
                    }

                    break;
                case "delete":
                    if ((op.Path ?? string.Empty).Trim().Length == 0)
                    {
                        return $"body operation at index {i} (delete) has an empty path";
                    }

                    break;
                case "rename":
                case "copy":
                    if ((op.From ?? string.Empty).Trim().Length == 0
                        || (op.To ?? string.Empty).Trim().Length == 0)
                    {
                        return $"body operation at index {i} ({op.Op}) requires non-empty from and to";
                    }

                    break;
                case "array_append":
                case "array_prepend":
                case "array_insert":
                case "array_remove":
                    if ((op.Path ?? string.Empty).Trim().Length == 0)
                    {
                        return $"body operation at index {i} ({op.Op}) has an empty path";
                    }

                    if (string.Equals(op.Path, "stream", StringComparison.OrdinalIgnoreCase))
                    {
                        return "override parameters cannot contain the field \"stream\"";
                    }

                    if (op.Op == "array_insert" && op.Index is null)
                    {
                        return $"body operation at index {i} (array_insert) requires an index";
                    }

                    if (op.Op == "array_remove")
                    {
                        if (op.Match is null)
                        {
                            return $"body operation at index {i} (array_remove) requires a match";
                        }

                        if ((op.Match.Path ?? string.Empty).Trim().Length == 0)
                        {
                            return $"body operation at index {i} (array_remove) requires a match path";
                        }

                        if ((op.Match.Eq ?? string.Empty).Trim().Length == 0)
                        {
                            return $"body operation at index {i} (array_remove) requires a match eq value";
                        }
                    }

                    break;
                default:
                    return $"body operation at index {i} has unknown op {GoCast.GoQuote(op.Op)}";
            }
        }

        return null;
    }

    /// <summary>Port of ValidateOverrideHeaders (channel_override.go:240-266).</summary>
    public static string? ValidateHeaderOverrideOperations(
        IReadOnlyList<OverrideOperationDraft> operations)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            switch (op.Op)
            {
                case "set":
                case "delete":
                    if ((op.Path ?? string.Empty).Trim().Length == 0)
                    {
                        return $"header operation at index {i} ({op.Op}) has an empty path";
                    }

                    break;
                case "rename":
                case "copy":
                    if ((op.From ?? string.Empty).Trim().Length == 0
                        || (op.To ?? string.Empty).Trim().Length == 0)
                    {
                        return $"header operation at index {i} ({op.Op}) requires non-empty from and to";
                    }

                    break;
                case "set_if_absent":
                    return $"header operation at index {i} ({op.Op}) is not supported on headers; it only applies to the body";
                case "array_append":
                case "array_prepend":
                case "array_insert":
                case "array_remove":
                    return $"header operation at index {i} ({op.Op}) is not supported on headers; array ops only apply to the body";
                default:
                    return $"header operation at index {i} has unknown op {GoCast.GoQuote(op.Op)}";
            }
        }

        return null;
    }

    /// <summary>Port of ValidateRateLimit (channel_rate_limit.go).</summary>
    public static string? ValidateRateLimit(ChannelRateLimitDraft? rateLimit)
    {
        if (rateLimit is null)
        {
            return null;
        }

        var error = NonNegativeRateLimitField("rpm", rateLimit.Rpm)
            ?? NonNegativeRateLimitField("tpm", rateLimit.Tpm)
            ?? NonNegativeRateLimitField("maxConcurrent", rateLimit.MaxConcurrent)
            ?? NonNegativeRateLimitField("queueSize", rateLimit.QueueSize)
            ?? NonNegativeRateLimitField("queueTimeoutMs", rateLimit.QueueTimeoutMs);
        if (error is not null)
        {
            return error;
        }

        if (rateLimit.QueueSize is > 0
            && (rateLimit.MaxConcurrent is null || rateLimit.MaxConcurrent <= 0))
        {
            return "queueSize requires maxConcurrent > 0";
        }

        return null;
    }

    private static string? NonNegativeRateLimitField(string name, long? value)
        => value is < 0 ? name + " must be >= 0" : null;

    /// <summary>
    /// Port of NormalizeRetryableStatusCodes (channel.go:723-739): validates the
    /// 400-599 range, then sorts and compacts the list in place.
    /// </summary>
    public static string? NormalizeRetryableStatusCodes(ChannelSettingsDraft settings)
    {
        if (settings.RetryableStatusCodes is null || settings.RetryableStatusCodes.Count == 0)
        {
            return null;
        }

        foreach (var code in settings.RetryableStatusCodes)
        {
            if (code is < 400 or > 599)
            {
                return $"invalid retryable status code {code}: must be between 400 and 599";
            }
        }

        var codes = settings.RetryableStatusCodes.ToList();
        codes.Sort();
        var compacted = new List<int>(codes.Count);
        foreach (var code in codes)
        {
            if (compacted.Count == 0 || compacted[^1] != code)
            {
                compacted.Add(code);
            }
        }

        settings.RetryableStatusCodes = compacted;
        return null;
    }

    /// <summary>
    /// Port of NormalizeRetryableErrorPatterns (channel.go:743-777): trims,
    /// drops empty patterns, compiles regexes, then deduplicates preserving
    /// first-occurrence order.
    /// </summary>
    public static string? NormalizeRetryableErrorPatterns(ChannelSettingsDraft settings)
    {
        if (settings.RetryableErrorPatterns is null
            || settings.RetryableErrorPatterns.Count == 0)
        {
            return null;
        }

        var patterns = new List<RetryableErrorPatternDraft>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pattern in settings.RetryableErrorPatterns)
        {
            var trimmed = (pattern.Pattern ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var isRegex = pattern.Regex is true;
            if (isRegex)
            {
                var regexError = CompileRegex(trimmed);
                if (regexError is not null)
                {
                    return "invalid retryable error regex "
                        + GoCast.GoQuote(trimmed) + ": " + regexError;
                }
            }

            var key = (isRegex ? "true" : "false") + "\0" + trimmed;
            if (!seen.Add(key))
            {
                continue;
            }

            patterns.Add(new RetryableErrorPatternDraft
            {
                Pattern = trimmed,
                Regex = isRegex ? true : null,
            });
        }

        settings.RetryableErrorPatterns = patterns.Count > 0 ? patterns : null;
        return null;
    }

    /// <summary>
    /// Go regexp.Compile mirror for the retryable-pattern check. Go and .NET
    /// accept different dialects, so only the success/failure split is shared
    /// (C144).
    /// </summary>
    private static string? CompileRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, Regex.InfiniteMatchTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Port of ValidateModelProtocols (channel_endpoint.go:91-136).</summary>
    public static string? ValidateModelProtocols(
        ChannelSettingsDraft? settings,
        string channelType,
        IReadOnlyList<ChannelEndpointDraft>? endpoints)
    {
        if (settings?.ModelProtocols is null || settings.ModelProtocols.Count == 0)
        {
            return null;
        }

        var available = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in ChannelMapping.DefaultEndpoints(channelType))
        {
            available.Add(endpoint.ApiFormat);
        }

        foreach (var endpoint in endpoints ?? [])
        {
            available.Add(endpoint.ApiFormat);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < settings.ModelProtocols.Count; i++)
        {
            var protocol = settings.ModelProtocols[i];
            if (protocol.Model.Length == 0)
            {
                return $"modelProtocols[{i}]: model is required";
            }

            if (!seen.Add(protocol.Model))
            {
                return $"modelProtocols[{i}]: duplicate model {GoCast.GoQuote(protocol.Model)}";
            }

            if (protocol.ApiFormats.Count == 0)
            {
                return $"modelProtocols[{i}] ({protocol.Model}): at least one api_format is required";
            }

            // A manually disabled override no longer has to track endpoints.
            if (protocol.Enabled is false)
            {
                continue;
            }

            foreach (var format in protocol.ApiFormats)
            {
                if (!available.Contains(format))
                {
                    return $"modelProtocols[{i}] ({protocol.Model}): api_format "
                        + GoCast.GoQuote(format) + " is not configured on this channel";
                }
            }
        }

        return null;
    }

    /// <summary>Port of NormalizeResponseTimeout (channel_response_timeout.go:17-29).</summary>
    public static string? NormalizeResponseTimeout(ChannelSettingsDraft? settings)
    {
        if (settings is null)
        {
            return null;
        }

        if (settings.ResponseTimeout is null || settings.ResponseTimeout.Mode != "custom")
        {
            settings.ResponseTimeout = null;
            return null;
        }

        return ValidateResponseTimeoutSeconds(
                "streamFirstEventTimeoutSeconds",
                settings.ResponseTimeout.StreamFirstEventTimeoutSeconds)
            ?? ValidateResponseTimeoutSeconds(
                "nonStreamResponseTimeoutSeconds",
                settings.ResponseTimeout.NonStreamResponseTimeoutSeconds);
    }

    private static string? ValidateResponseTimeoutSeconds(string name, int? seconds)
    {
        if (seconds is null)
        {
            return null;
        }

        if (seconds < 0)
        {
            return name + " must be >= 0";
        }

        if (seconds > RetryPolicyCodec.MaxRetryResponseTimeoutSeconds)
        {
            return name + " must be <= " + RetryPolicyCodec.MaxRetryResponseTimeoutSeconds;
        }

        return null;
    }

    /// <summary>Port of ValidateMaxSingleChannelRetries (channel.go:708-719).</summary>
    public static string? ValidateMaxSingleChannelRetries(ChannelSettingsDraft? settings)
    {
        if (settings?.MaxSingleChannelRetries is not { } value)
        {
            return null;
        }

        return value is < 0 or > 65535
            ? $"invalid max single channel retries {value}: must be between 0 and 65535"
            : null;
    }

    /// <summary>Port of ValidateVertexAPIKeys (channel_llm.go:1521-1564).</summary>
    public static string? ValidateVertexApiKeys(
        string channelType,
        ChannelCredentialsDraft credentials)
    {
        if (credentials.VertexApiKeys is null || credentials.VertexApiKeys.Count == 0)
        {
            return null;
        }

        if (channelType != "gemini_vertex")
        {
            return "vertex api keys are only supported by gemini_vertex channels";
        }

        var bindings = new Dictionary<string, (string ProjectId, string Region)>(StringComparer.Ordinal);
        foreach (var credential in credentials.VertexApiKeys)
        {
            var apiKey = credential.ApiKey.Trim();
            var projectId = credential.ProjectId.Trim();
            var region = credential.Region.Trim();

            if (apiKey.Length == 0 || projectId.Length == 0 || region.Length == 0)
            {
                return "vertex api key, project ID, and region are all required";
            }

            if (!VertexResourceSegment.IsMatch(projectId))
            {
                return "vertex project ID " + GoCast.GoQuote(projectId)
                    + " is not a valid Google project ID";
            }

            if (!VertexResourceSegment.IsMatch(region))
            {
                return "vertex region " + GoCast.GoQuote(region)
                    + " is not a valid Google region";
            }

            if (bindings.TryGetValue(apiKey, out var existing))
            {
                if (existing.ProjectId != projectId || existing.Region != region)
                {
                    return $"vertex api key is bound to both {existing.ProjectId}/{existing.Region}"
                        + $" and {projectId}/{region}";
                }

                continue;
            }

            bindings[apiKey] = (projectId, region);
        }

        return null;
    }

    /// <summary>
    /// Port of NormalizeCommandCodeCookie (commandcode_checker.go:249-321).
    /// Returns the reference error text and, on success, the canonical cookie.
    /// </summary>
    private static string? NormalizeCommandCodeCookie(string raw, out string normalized)
    {
        normalized = string.Empty;
        var cleaned = raw.Trim();
        if (cleaned.Length == 0)
        {
            return "cookie is empty";
        }

        // Strip an optional leading "Cookie:" label (browser devtools paste).
        var colon = cleaned.IndexOf(':');
        if (colon >= 0 && string.Equals(cleaned[..colon].Trim(), "cookie", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[(colon + 1)..].Trim();
        }

        // Reject multi-line pastes (e.g. cURL text) outright.
        if (cleaned.IndexOfAny(['\r', '\n']) >= 0)
        {
            return "cookie contains line breaks";
        }

        var kept = new List<string>();
        var seenToken = false;

        foreach (var rawPart in cleaned.Split(';'))
        {
            var part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            var cut = part.IndexOf('=');
            var found = cut >= 0;
            var name = (found ? part[..cut] : part).Trim();
            var value = found ? part[(cut + 1)..] : string.Empty;

            if (name.Length == 0)
            {
                return "invalid cookie segment: expected name=value";
            }

            var lower = name.ToLowerInvariant();
            if (!CommandCodeCookieNames.Contains(lower))
            {
                continue;
            }

            // The reference shadows the Cut result: an allowlisted name without
            // "=" reports the segment error, while a non-allowlisted malformed
            // segment is dropped first. Reproduced deliberately (C144).
            if (!found)
            {
                return "invalid cookie segment: expected name=value";
            }

            foreach (var r in name)
            {
                if (r <= 0x20 || r >= 0x7f || "()<>@,;:\\\"/[]?={}".Contains(r))
                {
                    return "invalid cookie name";
                }
            }

            value = value.Trim();
            if (value.Length == 0)
            {
                return "cookie has an empty value";
            }

            foreach (var r in value)
            {
                if (r <= 0x20 || r == 0x7f)
                {
                    return "cookie " + GoCast.GoQuote(name) + " has an invalid value";
                }
            }

            if (lower.EndsWith(".session_token", StringComparison.Ordinal))
            {
                seenToken = true;
            }

            kept.Add(name + "=" + value);
        }

        if (!seenToken)
        {
            return "no Command Code session_token cookie found";
        }

        normalized = string.Join("; ", kept);
        return null;
    }

    /// <summary>objects.APIKeyAutoDisableRule → domain rule for the normalizer.</summary>
    public static AutoDisableRule ToAutoDisableRule(ApiKeyAutoDisableRuleDraft rule)
        => new(
            rule.StatusCodes,
            rule.KeywordPatterns,
            rule.Times,
            rule.Action,
            rule.DisableDurationMinutes,
            rule.DisableUntilCron ?? string.Empty,
            rule.DisableUntilTimezone ?? string.Empty);

    /// <summary>Normalized domain rule → objects.APIKeyAutoDisableRule wire shape.</summary>
    public static ApiKeyAutoDisableRuleDraft ToDraft(AutoDisableRule rule)
        => new()
        {
            StatusCodes = rule.StatusCodes is { Count: > 0 } ? rule.StatusCodes : null,
            KeywordPatterns = rule.KeywordPatterns is { Count: > 0 } ? rule.KeywordPatterns : null,
            Times = rule.Times,
            Action = rule.Action,
            DisableDurationMinutes = rule.DisableDurationMinutes,
            DisableUntilCron = string.IsNullOrEmpty(rule.DisableUntilCron)
                ? null
                : rule.DisableUntilCron,
            DisableUntilTimezone = string.IsNullOrEmpty(rule.DisableUntilTimezone)
                ? null
                : rule.DisableUntilTimezone,
        };

    /// <summary>Port of ValidateEndpoints (channel_endpoint.go:41-86, frozen order).</summary>
    public static string? ValidateEndpoints(IReadOnlyList<ChannelEndpointDraft> endpoints)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];
            if (endpoint.ApiFormat.Length == 0)
            {
                return $"endpoint[{i}]: api_format is required";
            }

            if (!SupportedApiFormats.Contains(endpoint.ApiFormat))
            {
                return $"endpoint[{i}]: unsupported api_format {GoCast.GoQuote(endpoint.ApiFormat)}";
            }

            if (!seen.Add(endpoint.ApiFormat))
            {
                return $"endpoint[{i}]: duplicate api_format {GoCast.GoQuote(endpoint.ApiFormat)}";
            }

            if (endpoint.Transport is { Length: > 0 } transport
                && transport != "http"
                && transport != "websocket")
            {
                return $"endpoint[{i}]: unsupported transport {GoCast.GoQuote(transport)}";
            }

            if (EndpointTransport(endpoint) == "websocket"
                && !SupportsWebSocketTransport(endpoint.ApiFormat))
            {
                return $"endpoint[{i}]: websocket transport only supports api_format "
                    + GoCast.GoQuote(ApiFormats.OpenAIResponse);
            }

            if (endpoint.Path is { Length: > 0 } path)
            {
                if (path.StartsWith("http://", StringComparison.Ordinal)
                    || path.StartsWith("https://", StringComparison.Ordinal))
                {
                    return $"endpoint[{i}]: path must not be a full URL, got {GoCast.GoQuote(path)}";
                }

                if (!path.StartsWith('/'))
                {
                    return $"endpoint[{i}]: path must start with '/', got {GoCast.GoQuote(path)}";
                }
            }
        }

        return null;
    }

    /// <summary>Port of endpointTransport (channel_llm.go:263-274).</summary>
    private static string EndpointTransport(ChannelEndpointDraft endpoint)
    {
        if (endpoint.Transport is { Length: > 0 } transport)
        {
            return transport;
        }

        var baseUrl = (endpoint.BaseUrl ?? string.Empty).Trim().ToLowerInvariant();
        return baseUrl.StartsWith("ws://", StringComparison.Ordinal)
            || baseUrl.StartsWith("wss://", StringComparison.Ordinal)
                ? "websocket"
                : string.Empty;
    }

    /// <summary>
    /// Live supportsWebSocketTransport = isResponsesEndpoint: both Responses
    /// formats accept the WebSocket transport. The error text keeps the frozen
    /// openai/responses argument (C139/C144).
    /// </summary>
    private static bool SupportsWebSocketTransport(string apiFormat)
        => apiFormat is ApiFormats.OpenAIResponse or ApiFormats.OpenAIResponseWebSocket;

    /// <summary>Port of validateEndpointsForChannelType (channel_endpoint.go:346-376).</summary>
    public static string? ValidateEndpointsForChannelType(
        string channelType,
        IReadOnlyList<ChannelEndpointDraft>? endpoints)
    {
        var list = endpoints ?? [];
        var error = ValidateEndpoints(list);
        if (error is not null)
        {
            return error;
        }

        if (channelType == "typesafe")
        {
            foreach (var endpoint in list)
            {
                if (endpoint.ApiFormat != ApiFormats.TypeSafeSystemOne)
                {
                    return "channel type " + GoCast.GoQuote(channelType)
                        + " only supports api_format " + GoCast.GoQuote(ApiFormats.TypeSafeSystemOne);
                }
            }
        }

        foreach (var endpoint in list)
        {
            if (endpoint.ApiFormat == ApiFormats.TypeSafeSystemOne && channelType != "typesafe")
            {
                return "api_format " + GoCast.GoQuote(endpoint.ApiFormat)
                    + " is only supported by TypeSafe channel types";
            }
        }

        if (IsZenmuxChannelType(channelType))
        {
            return null;
        }

        foreach (var endpoint in list)
        {
            if (endpoint.ApiFormat == ApiFormats.ZenmuxVideo)
            {
                return "api_format " + GoCast.GoQuote(endpoint.ApiFormat)
                    + " is only supported by ZenMux channel types";
            }
        }

        return null;
    }
}
