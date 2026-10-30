using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>Read side of the reference's SystemService.RetryPolicyOrDefault.</summary>
public interface IRetryPolicyProvider
{
    /// <summary>
    /// Port of SystemService.RetryPolicyOrDefault: a missing row, a lookup
    /// failure or malformed JSON falls back to <see cref="RetryPolicy.Default"/>.
    /// A JSON <c>null</c> is a valid document and yields the zero policy after
    /// normalization, exactly like Go's encoding/json.
    /// </summary>
    Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and normalizes the <c>retry_policy</c> system setting
/// (internal/server/biz/system.go:1042-1172).
/// </summary>
public sealed class SystemRetryPolicyService(ISystemStore systems) : IRetryPolicyProvider
{
    /// <summary>biz.SystemKeyRetryPolicy.</summary>
    public const string RetryPolicyKey = "retry_policy";

    /// <summary>biz.maxRetryResponseTimeoutSeconds.</summary>
    private const int MaxRetryResponseTimeoutSeconds = 600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Go matches JSON keys to struct tags case-insensitively.
        PropertyNameCaseInsensitive = true,
    };

    public async Task<RetryPolicy> GetRetryPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(RetryPolicyKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RetryPolicyOrDefault swallows every lookup error.
            return RetryPolicy.Default;
        }

        if (row is null)
        {
            // RetryPolicy normalizes the default before returning it.
            return RetryPolicy.Default;
        }

        RetryPolicyJson? stored;
        try
        {
            stored = JsonSerializer.Deserialize<RetryPolicyJson>(row.Value, JsonOptions);
        }
        catch (JsonException)
        {
            // RetryPolicy returns the unmarshal error; RetryPolicyOrDefault
            // logs it and falls back to the default policy.
            return RetryPolicy.Default;
        }

        // The literal "null" unmarshals into Go's zero struct without error.
        return Normalize(stored ?? new RetryPolicyJson());
    }

    /// <summary>Port of normalizeRetryPolicy.</summary>
    private static RetryPolicy Normalize(RetryPolicyJson stored)
    {
        var strategy = NormalizeStrategy(stored.LoadBalancerStrategy);
        var stickyMode = stored.TraceStickyMode is RetryPolicy.TraceStickyDisabled
            or RetryPolicy.TraceStickyPreferPreviousChannel
                ? stored.TraceStickyMode!
                : RetryPolicy.Default.TraceStickyMode;

        return new RetryPolicy
        {
            Enabled = stored.Enabled ?? false,
            MaxChannelRetries = stored.MaxChannelRetries ?? 0,
            MaxSingleChannelRetries = stored.MaxSingleChannelRetries ?? 0,
            RetryDelayMs = stored.RetryDelayMs ?? 0,
            StreamFirstEventTimeoutSeconds = NormalizeTimeout(stored.StreamFirstEventTimeoutSeconds),
            NonStreamResponseTimeoutSeconds = NormalizeTimeout(stored.NonStreamResponseTimeoutSeconds),
            LoadBalancerStrategy = strategy,
            TraceStickyMode = stickyMode,
            AutoDisableChannel = NormalizeAutoDisable(stored.AutoDisableChannel),
            UpstreamErrorPolicy = NormalizeUpstreamErrorPolicy(stored.UpstreamErrorPolicy),
            EmptyResponseDetection = stored.EmptyResponseDetection ?? false,
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

    private static int NormalizeTimeout(int? seconds)
    {
        var value = seconds ?? 0;
        return Math.Clamp(value, 0, MaxRetryResponseTimeoutSeconds);
    }

    private static AutoDisableChannel NormalizeAutoDisable(AutoDisableChannelJson? stored)
    {
        var rules = stored?.Rules;
        if ((rules is null || rules.Count == 0) && stored?.Statuses is { Count: > 0 } statuses)
        {
            // Legacy retry_policy JSON: statuses are migrated into rules and
            // never written back.
            rules = statuses
                .Select(static status => (AutoDisableRuleJson?)new AutoDisableRuleJson
                {
                    StatusCodes = [status?.Status ?? 0],
                    Times = status?.Times ?? 0,
                    Action = RetryPolicy.AutoDisableActionPermanent,
                })
                .ToList();
        }

        return new AutoDisableChannel(
            stored?.Enabled ?? false,
            rules?.Select(ToRule).ToArray() ?? []);
    }

    private static AutoDisableRule ToRule(AutoDisableRuleJson? rule)
        => new(
            rule?.StatusCodes?.Select(static code => code ?? 0).ToArray() ?? [],
            rule?.KeywordPatterns?.Select(static pattern => pattern ?? string.Empty).ToArray() ?? [],
            rule?.Times ?? 0,
            rule?.Action ?? string.Empty,
            rule?.DisableDurationMinutes,
            rule?.DisableUntilCron ?? string.Empty,
            rule?.DisableUntilTimezone ?? string.Empty);

    private static UpstreamErrorPolicy NormalizeUpstreamErrorPolicy(UpstreamErrorPolicyJson? stored)
    {
        var mode = stored?.Mode is RetryPolicy.UpstreamErrorModePassthrough
            or RetryPolicy.UpstreamErrorModeHidden
            or RetryPolicy.UpstreamErrorModeCustom
                ? stored.Mode!
                : RetryPolicy.UpstreamErrorModePassthrough;

        var message = stored?.CustomMessage ?? string.Empty;
        if (mode == RetryPolicy.UpstreamErrorModeCustom && string.IsNullOrWhiteSpace(message))
        {
            mode = RetryPolicy.UpstreamErrorModeHidden;
        }

        return new UpstreamErrorPolicy(mode, message);
    }

    /// <summary>
    /// Go's json.Unmarshal accepts <c>null</c> for scalar members and leaves the
    /// zero value, so nullable DTO members distinguish "absent/null" from a
    /// wrong type (which fails the whole document, like Go).
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
        public string? TraceStickyMode { get; set; }

        [JsonPropertyName("auto_disable_channel")]
        public AutoDisableChannelJson? AutoDisableChannel { get; set; }

        [JsonPropertyName("upstream_error_policy")]
        public UpstreamErrorPolicyJson? UpstreamErrorPolicy { get; set; }

        [JsonPropertyName("empty_response_detection")]
        public bool? EmptyResponseDetection { get; set; }
    }

    /// <summary>
    /// Modeled with Go-compatible member types (including null elements in
    /// slices) so a type error anywhere in the document falls back to the
    /// default policy; the rules' effect is C017.
    /// </summary>
    private sealed class AutoDisableChannelJson
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("rules")]
        public List<AutoDisableRuleJson?>? Rules { get; set; }

        [JsonPropertyName("statuses")]
        public List<AutoDisableStatusJson?>? Statuses { get; set; }
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
}
