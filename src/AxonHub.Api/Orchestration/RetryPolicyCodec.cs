using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Orchestration;

/// <summary>Thrown when a rule fails <c>normalizeAutoDisableRules</c>.</summary>
public sealed class AutoDisableRuleValidationException(string message) : Exception(message);

/// <summary>
/// Go-compatible JSON codec and normalization for the <c>retry_policy</c>
/// system setting (internal/server/biz/system.go:1042-1172 and
/// internal/server/biz/channel.go:759-846).
/// </summary>
public static class RetryPolicyCodec
{
    /// <summary>biz.SystemKeyRetryPolicy.</summary>
    public const string RetryPolicyKey = "retry_policy";

    /// <summary>biz.maxRetryResponseTimeoutSeconds.</summary>
    private const int MaxRetryResponseTimeoutSeconds = 600;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // Go matches JSON keys to struct tags case-insensitively.
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonFieldValueConverter() },
    };

    /// <summary>
    /// Port of json.Unmarshal into biz.RetryPolicy (including the custom
    /// TraceStickyMode unmarshaler) followed by normalizeRetryPolicy.
    /// </summary>
    /// <exception cref="JsonException">The document cannot be decoded.</exception>
    public static RetryPolicy Deserialize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var root = value.AsSpan().TrimStart();
        if (root.IsEmpty)
        {
            throw new JsonException("unexpected end of JSON input");
        }

        if (root.SequenceEqual("null"))
        {
            // json.Unmarshal("null", &policy) is a no-op; normalization then
            // applies the defaults to the zero value. RetryPolicy's C#
            // initializers hold the reference defaults, so zero explicitly.
            return Normalize(ZeroPolicy());
        }

        if (root[0] != '{')
        {
            // Mirror Go's top-level type error, which has no "json:" field path.
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(root[0]) + " into Go value of type biz.RetryPolicy");
        }

        var stored = JsonSerializer.Deserialize<RetryPolicyJson>(value, ReadOptions)
            ?? new RetryPolicyJson();

        var stickyMode = ParseTraceStickyMode(stored.TraceStickyMode);

        var channel = stored.AutoDisableChannel;
        var rules = channel?.Rules?.Select(static rule => ToRule(rule ?? new AutoDisableRuleJson()))
            .ToList();

        if ((rules is null || rules.Count == 0) && channel?.Statuses is { Count: > 0 } statuses)
        {
            // Legacy retry_policy JSON: statuses are migrated into rules and
            // never written back.
            rules = statuses
                .Select(static status =>
                {
                    var config = status ?? new AutoDisableStatusJson();
                    return new AutoDisableRule(
                        [config.Status ?? 0],
                        null,
                        config.Times ?? 0,
                        RetryPolicy.AutoDisableActionPermanent,
                        null,
                        string.Empty,
                        string.Empty);
                })
                .ToList();
        }

        var policy = new RetryPolicy
        {
            Enabled = stored.Enabled ?? false,
            MaxChannelRetries = stored.MaxChannelRetries ?? 0,
            MaxSingleChannelRetries = stored.MaxSingleChannelRetries ?? 0,
            RetryDelayMs = stored.RetryDelayMs ?? 0,
            StreamFirstEventTimeoutSeconds = stored.StreamFirstEventTimeoutSeconds ?? 0,
            NonStreamResponseTimeoutSeconds = stored.NonStreamResponseTimeoutSeconds ?? 0,
            LoadBalancerStrategy = stored.LoadBalancerStrategy ?? string.Empty,
            TraceStickyMode = stickyMode,
            AutoDisableChannel = new AutoDisableChannel(
                channel?.Enabled ?? false,
                rules ?? []),
            EmptyResponseDetection = stored.EmptyResponseDetection ?? false,
            UpstreamErrorPolicy = new UpstreamErrorPolicy(
                stored.UpstreamErrorPolicy?.Mode ?? string.Empty,
                stored.UpstreamErrorPolicy?.CustomMessage ?? string.Empty),
        };

        return Normalize(policy);
    }

    /// <summary>Port of normalizeRetryPolicy.</summary>
    public static RetryPolicy Normalize(RetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var strategy = NormalizeStrategy(policy.LoadBalancerStrategy);
        var stickyMode = policy.TraceStickyMode is RetryPolicy.TraceStickyDisabled
            or RetryPolicy.TraceStickyPreferPreviousChannel
                ? policy.TraceStickyMode
                : RetryPolicy.Default.TraceStickyMode;

        var mode = policy.UpstreamErrorPolicy?.Mode ?? string.Empty;
        if (mode is not (RetryPolicy.UpstreamErrorModePassthrough
            or RetryPolicy.UpstreamErrorModeHidden
            or RetryPolicy.UpstreamErrorModeCustom))
        {
            mode = RetryPolicy.Default.UpstreamErrorPolicy.Mode;
        }

        var message = policy.UpstreamErrorPolicy?.CustomMessage ?? string.Empty;
        if (mode == RetryPolicy.UpstreamErrorModeCustom && string.IsNullOrWhiteSpace(message))
        {
            mode = RetryPolicy.UpstreamErrorModeHidden;
        }

        return policy with
        {
            StreamFirstEventTimeoutSeconds = NormalizeTimeout(policy.StreamFirstEventTimeoutSeconds),
            NonStreamResponseTimeoutSeconds = NormalizeTimeout(policy.NonStreamResponseTimeoutSeconds),
            LoadBalancerStrategy = strategy,
            TraceStickyMode = stickyMode,
            AutoDisableChannel = (policy.AutoDisableChannel ?? new AutoDisableChannel(false, []))
                with
                {
                    Rules = policy.AutoDisableChannel?.Rules ?? [],
                },
            UpstreamErrorPolicy = new UpstreamErrorPolicy(mode, message),
        };
    }

    private static string NormalizeStrategy(string? strategy)
    {
        if (string.IsNullOrEmpty(strategy))
        {
            return RetryPolicy.Default.LoadBalancerStrategy;
        }

        // The weighted load balancer strategy is deprecated; use failover.
        if (strategy == "weighted")
        {
            return RetryPolicy.FailoverStrategy;
        }

        return strategy is RetryPolicy.AdaptiveStrategy
            or RetryPolicy.FailoverStrategy
            or RetryPolicy.CircuitBreakerStrategy
            or RetryPolicy.RoundRobinStrategy
                ? strategy
                : RetryPolicy.Default.LoadBalancerStrategy;
    }

    private static int NormalizeTimeout(int seconds)
        => Math.Clamp(seconds, 0, MaxRetryResponseTimeoutSeconds);

    /// <summary>
    /// Port of normalizeAutoDisableRules (internal/server/biz/channel.go:761).
    /// Global retry policy uses <paramref name="allowDelete"/>=false so
    /// <c>permanent_disable_delete</c> is rejected there.
    /// </summary>
    /// <exception cref="AutoDisableRuleValidationException">A rule is invalid.</exception>
    public static IReadOnlyList<AutoDisableRule> NormalizeAutoDisableRules(
        IReadOnlyList<AutoDisableRule> rules,
        bool allowDelete)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (rules.Count == 0)
        {
            return rules;
        }

        var normalized = new List<AutoDisableRule>(rules.Count);

        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var position = index + 1;

            if (rule.Times < 1)
            {
                throw new AutoDisableRuleValidationException(
                    "API key rule " + position + " consecutive error count must be at least 1");
            }

            // Each action owns one schedule field; the others are cleared so a
            // rule edited from one action to another cannot leave stale
            // settings behind.
            switch (rule.Action)
            {
                case RetryPolicy.AutoDisableActionTemporary:
                    if (rule.DisableDurationMinutes is null)
                    {
                        throw new AutoDisableRuleValidationException(
                            "API key rule " + position
                            + " requires a disable duration for temporary disable");
                    }

                    if (rule.DisableDurationMinutes < 1)
                    {
                        throw new AutoDisableRuleValidationException(
                            "API key rule " + position
                            + " disable duration must be at least 1 minute");
                    }

                    rule = rule with
                    {
                        DisableUntilCron = string.Empty,
                        DisableUntilTimezone = string.Empty,
                    };
                    break;
                case RetryPolicy.AutoDisableActionUntilCron:
                    var cron = rule.DisableUntilCron.Trim();
                    var timezone = rule.DisableUntilTimezone.Trim();

                    if (cron.Length == 0)
                    {
                        throw new AutoDisableRuleValidationException(
                            "API key rule " + position
                            + " requires a cron expression for scheduled recovery");
                    }

                    var cronError = CronExpression.Validate(cron);
                    if (cronError is not null)
                    {
                        throw new AutoDisableRuleValidationException(
                            "API key rule " + position + " has invalid cron expression "
                            + GoCast.GoQuote(cron) + ": " + cronError);
                    }

                    if (timezone.Length != 0 && !IsKnownTimeZone(timezone))
                    {
                        throw new AutoDisableRuleValidationException(
                            "API key rule " + position + " has invalid timezone "
                            + GoCast.GoQuote(timezone) + ": unknown time zone " + timezone);
                    }

                    rule = rule with
                    {
                        DisableUntilCron = cron,
                        DisableUntilTimezone = timezone,
                        DisableDurationMinutes = null,
                    };
                    break;
                case RetryPolicy.AutoDisableActionPermanent:
                    rule = rule with
                    {
                        DisableDurationMinutes = null,
                        DisableUntilCron = string.Empty,
                        DisableUntilTimezone = string.Empty,
                    };
                    break;
                case RetryPolicy.AutoDisableActionPermanentDelete:
                    if (!allowDelete)
                    {
                        throw new AutoDisableRuleValidationException(
                            "API key rule " + position
                            + " cannot use permanent_disable_delete in global auto-disable settings");
                    }

                    rule = rule with
                    {
                        DisableDurationMinutes = null,
                        DisableUntilCron = string.Empty,
                        DisableUntilTimezone = string.Empty,
                    };
                    break;
                default:
                    throw new AutoDisableRuleValidationException(
                        "API key rule " + position + " has unsupported action "
                        + GoCast.GoQuote(rule.Action));
            }

            var codes = rule.StatusCodes?.ToList() ?? [];
            foreach (var code in codes)
            {
                if (code < 100 || code > 599)
                {
                    throw new AutoDisableRuleValidationException(
                        "API key rule " + position + " has invalid HTTP status code " + code);
                }
            }

            codes.Sort();
            var compactCodes = new List<int>(codes.Count);
            foreach (var code in codes)
            {
                if (compactCodes.Count == 0 || compactCodes[^1] != code)
                {
                    compactCodes.Add(code);
                }
            }

            var patterns = new List<string>(rule.KeywordPatterns?.Count ?? 0);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in rule.KeywordPatterns ?? [])
            {
                var pattern = raw.Trim();
                if (pattern.Length == 0 || !seen.Add(pattern))
                {
                    continue;
                }

                patterns.Add(pattern);
            }

            normalized.Add(rule with
            {
                StatusCodes = compactCodes,
                KeywordPatterns = patterns,
            });
        }

        return normalized;
    }

    /// <summary>Port of time.LoadLocation's success path: "Local" and IANA names.</summary>
    private static bool IsKnownTimeZone(string name)
    {
        if (name == "Local")
        {
            return true;
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(name);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>Go's zero biz.RetryPolicy (no field initializers).</summary>
    private static RetryPolicy ZeroPolicy() => new()
    {
        Enabled = false,
        MaxChannelRetries = 0,
        MaxSingleChannelRetries = 0,
        RetryDelayMs = 0,
        StreamFirstEventTimeoutSeconds = 0,
        NonStreamResponseTimeoutSeconds = 0,
        LoadBalancerStrategy = string.Empty,
        TraceStickyMode = string.Empty,
        AutoDisableChannel = new AutoDisableChannel(false, []),
        EmptyResponseDetection = false,
        UpstreamErrorPolicy = new UpstreamErrorPolicy(string.Empty, string.Empty),
    };

    /// <summary>Port of json.Marshal(biz.RetryPolicy) (field order and omitempty).</summary>
    public static string Serialize(RetryPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var write = new RetryPolicyWriteJson
        {
            Enabled = policy.Enabled,
            MaxChannelRetries = policy.MaxChannelRetries,
            MaxSingleChannelRetries = policy.MaxSingleChannelRetries,
            RetryDelayMs = policy.RetryDelayMs,
            StreamFirstEventTimeoutSeconds = policy.StreamFirstEventTimeoutSeconds,
            NonStreamResponseTimeoutSeconds = policy.NonStreamResponseTimeoutSeconds,
            LoadBalancerStrategy = policy.LoadBalancerStrategy,
            TraceStickyMode = policy.TraceStickyMode,
            AutoDisableChannel = new AutoDisableChannelWriteJson
            {
                Enabled = policy.AutoDisableChannel.Enabled,
                Rules = policy.AutoDisableChannel.Rules.Select(static rule => new AutoDisableRuleWriteJson
                {
                    StatusCodes = rule.StatusCodes is { Count: > 0 } codes ? codes : null,
                    KeywordPatterns = rule.KeywordPatterns is { Count: > 0 } patterns ? patterns : null,
                    Times = rule.Times,
                    Action = rule.Action,
                    DisableDurationMinutes = rule.DisableDurationMinutes,
                    DisableUntilCron = rule.DisableUntilCron.Length == 0 ? null : rule.DisableUntilCron,
                    DisableUntilTimezone = rule.DisableUntilTimezone.Length == 0
                        ? null
                        : rule.DisableUntilTimezone,
                }).ToList(),
            },
            EmptyResponseDetection = policy.EmptyResponseDetection,
            UpstreamErrorPolicy = new UpstreamErrorPolicyWriteJson
            {
                Mode = policy.UpstreamErrorPolicy.Mode,
                CustomMessage = policy.UpstreamErrorPolicy.CustomMessage,
            },
        };

        // Fully qualified: AxonHub.Api.Orchestration has its own internal
        // GoJson helper (OverrideJson.cs) that shadows the Llm one.
        return AxonHub.Llm.Json.GoJson.MarshalToString(write);
    }

    private static string ParseTraceStickyMode(JsonFieldValue field)
    {
        if (!field.IsPresent)
        {
            // The field is absent: the Go zero value, normalized later.
            return string.Empty;
        }

        var value = field.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var raw = value.GetString() ?? string.Empty;
                return raw switch
                {
                    "DISABLED" or RetryPolicy.TraceStickyDisabled
                        => RetryPolicy.TraceStickyDisabled,
                    "PREFER_PREVIOUS_CHANNEL" or RetryPolicy.TraceStickyPreferPreviousChannel
                        => RetryPolicy.TraceStickyPreferPreviousChannel,
                    _ => throw new JsonException(
                        "invalid TraceStickyMode: " + GoCast.GoQuote(raw)),
                };
            case JsonValueKind.Null:
                // json.Unmarshal(null, &string) succeeds with "", which then
                // fails the custom switch.
                throw new JsonException("invalid TraceStickyMode: \"\"");
            default:
                throw new JsonException(
                    "invalid TraceStickyMode: json: cannot unmarshal "
                    + GoJsonKindName(value.ValueKind) + " into Go value of type string");
        }
    }

    private static string GoKindName(char first) => first switch
    {
        '[' => "array",
        '"' => "string",
        't' or 'f' => "bool",
        '-' or (>= '0' and <= '9') => "number",
        _ => "object",
    };

    private static string GoJsonKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Number => "number",
        _ => "string",
    };

    private static AutoDisableRule ToRule(AutoDisableRuleJson rule)
        => new(
            rule.StatusCodes?.Select(static code => code ?? 0).ToList(),
            rule.KeywordPatterns?.Select(static pattern => pattern ?? string.Empty).ToList(),
            rule.Times ?? 0,
            rule.Action ?? string.Empty,
            rule.DisableDurationMinutes,
            rule.DisableUntilCron ?? string.Empty,
            rule.DisableUntilTimezone ?? string.Empty);

    /// <summary>
    /// Go's json.Unmarshal accepts <c>null</c> for scalar members and leaves the
    /// zero value; nullable DTO members distinguish "absent/null" from a wrong
    /// type (which fails the whole document, like Go).
    /// </summary>
    private sealed class RetryPolicyJson
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("max_channel_retries")]
        public int? MaxChannelRetries { get; set; }

        [JsonPropertyName("max_single_channel_retries")]
        public int? MaxSingleChannelRetries { get; set; }

        [JsonPropertyName("retry_delay_ms")]
        public int? RetryDelayMs { get; set; }

        [JsonPropertyName("stream_first_event_timeout_seconds")]
        public int? StreamFirstEventTimeoutSeconds { get; set; }

        [JsonPropertyName("non_stream_response_timeout_seconds")]
        public int? NonStreamResponseTimeoutSeconds { get; set; }

        [JsonPropertyName("load_balancer_strategy")]
        public string? LoadBalancerStrategy { get; set; }

        [JsonPropertyName("trace_sticky_mode")]
        public JsonFieldValue TraceStickyMode { get; set; }

        [JsonPropertyName("auto_disable_channel")]
        public AutoDisableChannelJson? AutoDisableChannel { get; set; }

        [JsonPropertyName("empty_response_detection")]
        public bool? EmptyResponseDetection { get; set; }

        [JsonPropertyName("upstream_error_policy")]
        public UpstreamErrorPolicyJson? UpstreamErrorPolicy { get; set; }
    }

    private sealed class AutoDisableChannelJson
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("rules")]
        public List<AutoDisableRuleJson?>? Rules { get; set; }

        [JsonPropertyName("statuses")]
        public List<AutoDisableStatusJson?>? Statuses { get; set; }
    }

    /// <summary>
    /// Distinguishes an absent member from an explicit <c>null</c>: Go calls
    /// TraceStickyMode.UnmarshalJSON for JSON null and the custom unmarshaler
    /// rejects it, while STJ maps null to a null <see cref="Nullable{T}"/>.
    /// </summary>
    private readonly struct JsonFieldValue
    {
        public bool IsPresent { get; init; }

        public JsonElement Value { get; init; }
    }

    private sealed class JsonFieldValueConverter : JsonConverter<JsonFieldValue>
    {
        public override JsonFieldValue Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => new() { IsPresent = true, Value = JsonElement.ParseValue(ref reader) };

        public override void Write(
            Utf8JsonWriter writer,
            JsonFieldValue value,
            JsonSerializerOptions options)
            => throw new NotSupportedException("JsonFieldValue is read-only.");
    }

    private sealed class AutoDisableStatusJson
    {
        [JsonPropertyName("status")]
        public int? Status { get; set; }

        [JsonPropertyName("times")]
        public int? Times { get; set; }
    }

    private sealed class AutoDisableRuleJson
    {
        [JsonPropertyName("statusCodes")]
        public List<int?>? StatusCodes { get; set; }

        [JsonPropertyName("keywordPatterns")]
        public List<string?>? KeywordPatterns { get; set; }

        [JsonPropertyName("times")]
        public int? Times { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("disableDurationMinutes")]
        public int? DisableDurationMinutes { get; set; }

        [JsonPropertyName("disableUntilCron")]
        public string? DisableUntilCron { get; set; }

        [JsonPropertyName("disableUntilTimezone")]
        public string? DisableUntilTimezone { get; set; }
    }

    private sealed class UpstreamErrorPolicyJson
    {
        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("custom_message")]
        public string? CustomMessage { get; set; }
    }

    private sealed class RetryPolicyWriteJson
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("max_channel_retries")]
        public int MaxChannelRetries { get; init; }

        [JsonPropertyName("max_single_channel_retries")]
        public int MaxSingleChannelRetries { get; init; }

        [JsonPropertyName("retry_delay_ms")]
        public int RetryDelayMs { get; init; }

        [JsonPropertyName("stream_first_event_timeout_seconds")]
        public int StreamFirstEventTimeoutSeconds { get; init; }

        [JsonPropertyName("non_stream_response_timeout_seconds")]
        public int NonStreamResponseTimeoutSeconds { get; init; }

        [JsonPropertyName("load_balancer_strategy")]
        public string LoadBalancerStrategy { get; init; } = string.Empty;

        [JsonPropertyName("trace_sticky_mode")]
        public string TraceStickyMode { get; init; } = string.Empty;

        [JsonPropertyName("auto_disable_channel")]
        public AutoDisableChannelWriteJson AutoDisableChannel { get; init; } = new();

        [JsonPropertyName("empty_response_detection")]
        public bool EmptyResponseDetection { get; init; }

        [JsonPropertyName("upstream_error_policy")]
        public UpstreamErrorPolicyWriteJson UpstreamErrorPolicy { get; init; } = new();
    }

    private sealed class AutoDisableChannelWriteJson
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("rules")]
        public List<AutoDisableRuleWriteJson> Rules { get; init; } = [];
    }

    private sealed class AutoDisableRuleWriteJson
    {
        [JsonPropertyName("statusCodes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<int>? StatusCodes { get; init; }

        [JsonPropertyName("keywordPatterns")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<string>? KeywordPatterns { get; init; }

        [JsonPropertyName("times")]
        public int Times { get; init; }

        [JsonPropertyName("action")]
        public string Action { get; init; } = string.Empty;

        [JsonPropertyName("disableDurationMinutes")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? DisableDurationMinutes { get; init; }

        [JsonPropertyName("disableUntilCron")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisableUntilCron { get; init; }

        [JsonPropertyName("disableUntilTimezone")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisableUntilTimezone { get; init; }
    }

    private sealed class UpstreamErrorPolicyWriteJson
    {
        [JsonPropertyName("mode")]
        public string Mode { get; init; } = string.Empty;

        [JsonPropertyName("custom_message")]
        public string CustomMessage { get; init; } = string.Empty;
    }
}
