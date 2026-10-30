namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.RetryPolicy (internal/server/biz/system.go:297-336) after
/// normalizeRetryPolicy. The reference reads this from the <c>retry_policy</c>
/// system setting; <see cref="SystemRetryPolicyService"/> owns the JSON
/// decoding and normalization.
/// </summary>
public sealed record RetryPolicy
{
    public const string AdaptiveStrategy = "adaptive";
    public const string FailoverStrategy = "failover";
    public const string CircuitBreakerStrategy = "circuit-breaker";
    public const string RoundRobinStrategy = "round-robin";

    public const string TraceStickyDisabled = "disabled";
    public const string TraceStickyPreferPreviousChannel = "prefer_previous_channel";

    public const string UpstreamErrorModePassthrough = "passthrough";
    public const string UpstreamErrorModeHidden = "hidden";
    public const string UpstreamErrorModeCustom = "custom";

    public const string AutoDisableActionPermanent = "permanent_disable";

    /// <summary>Port of biz.defaultRetryPolicy (internal/server/biz/system_default.go:79-89).</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Controls whether the retry policy is active.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum number of different channels to retry.</summary>
    public int MaxChannelRetries { get; init; } = 3;

    /// <summary>Maximum number of retries for a single channel.</summary>
    public int MaxSingleChannelRetries { get; init; } = 2;

    /// <summary>Delay between retries in milliseconds. Not clamped by the reference.</summary>
    public int RetryDelayMs { get; init; } = 1000;

    /// <summary>First streaming event timeout in seconds; 0 disables it, values above 600 are clamped.</summary>
    public int StreamFirstEventTimeoutSeconds { get; init; }

    /// <summary>Non-streaming response timeout in seconds; 0 disables it, values above 600 are clamped.</summary>
    public int NonStreamResponseTimeoutSeconds { get; init; }

    /// <summary>Selected load balancer strategy; the adaptive chain itself is C012.</summary>
    public string LoadBalancerStrategy { get; init; } = AdaptiveStrategy;

    /// <summary>Trace/thread stickiness mode; the provider itself is C013.</summary>
    public string TraceStickyMode { get; init; } = TraceStickyPreferPreviousChannel;

    /// <summary>Auto-disable rules; the effect on channels/API keys is C017.</summary>
    public AutoDisableChannel AutoDisableChannel { get; init; } = new(false, []);

    /// <summary>Upstream error exposure policy; applying it is C031.</summary>
    public UpstreamErrorPolicy UpstreamErrorPolicy { get; init; } =
        new(UpstreamErrorModePassthrough, string.Empty);

    public bool EmptyResponseDetection { get; init; }

    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(RetryDelayMs);

    public TimeSpan StreamFirstEventTimeout => TimeSpan.FromSeconds(StreamFirstEventTimeoutSeconds);

    public TimeSpan NonStreamTimeout => TimeSpan.FromSeconds(NonStreamResponseTimeoutSeconds);
}

/// <summary>Port of biz.AutoDisableChannel.</summary>
public sealed record AutoDisableChannel(bool Enabled, IReadOnlyList<AutoDisableRule> Rules);

/// <summary>Port of objects.APIKeyAutoDisableRule as stored in the retry policy.</summary>
public sealed record AutoDisableRule(
    IReadOnlyList<int> StatusCodes,
    IReadOnlyList<string> KeywordPatterns,
    int Times,
    string Action,
    int? DisableDurationMinutes,
    string DisableUntilCron,
    string DisableUntilTimezone);

/// <summary>Port of biz.UpstreamErrorPolicy.</summary>
public sealed record UpstreamErrorPolicy(string Mode, string CustomMessage);
