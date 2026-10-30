namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.RetryPolicy (internal/server/biz/system_default.go:76-90) for the
/// first slice. The reference reads the policy from system settings; the port
/// binds the same fields from the <c>RetryPolicy</c> configuration section and
/// falls back to the reference defaults.
/// </summary>
public sealed class RetryPolicy
{
    public bool Enabled { get; set; } = true;

    public int MaxChannelRetries { get; set; } = 3;

    public int MaxSingleChannelRetries { get; set; } = 2;

    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>Kept for parity with the reference policy; the adaptive chain is C012.</summary>
    public string LoadBalancerStrategy { get; set; } = "adaptive";

    public bool EmptyResponseDetection { get; set; }

    public int StreamFirstEventTimeoutSeconds { get; set; }

    public int NonStreamResponseTimeoutSeconds { get; set; }

    public TimeSpan RetryDelay => TimeSpan.FromMilliseconds(RetryDelayMs);

    public TimeSpan StreamFirstEventTimeout => TimeSpan.FromSeconds(StreamFirstEventTimeoutSeconds);

    public TimeSpan NonStreamTimeout => TimeSpan.FromSeconds(NonStreamResponseTimeoutSeconds);
}
