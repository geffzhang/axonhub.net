namespace AxonHub.Api.Models;

/// <summary>
/// Port of <c>objects/routing.go</c> (internal/objects/routing.go:1-51): the
/// per-model routing policy values stored in <c>models.settings</c>.
/// </summary>
public static class RoutingPolicy
{
    public const string Default = "default";

    public const string LoadBalancerAdaptive = "adaptive";

    public const string LoadBalancerFailover = "failover";

    public const string LoadBalancerCircuitBreaker = "circuit-breaker";

    public const string LoadBalancerRoundRobin = "round-robin";

    public const string TraceStickyDisabled = "disabled";

    public const string TraceStickyPreferPreviousChannel = "prefer_previous_channel";

    /// <summary>Port of NormalizeRoutingPolicyValue.</summary>
    public static string NormalizeValue(string value) => value switch
    {
        "" or "system_default" => Default,
        _ => value,
    };

    /// <summary>Port of IsValidLoadBalancerStrategy.</summary>
    public static bool IsValidLoadBalancerStrategy(string value)
        => NormalizeValue(value) is
            Default
            or LoadBalancerAdaptive
            or LoadBalancerFailover
            or LoadBalancerCircuitBreaker
            or LoadBalancerRoundRobin;

    /// <summary>Port of IsValidTraceStickyMode.</summary>
    public static bool IsValidTraceStickyMode(string value)
        => NormalizeValue(value) is
            Default
            or TraceStickyDisabled
            or TraceStickyPreferPreviousChannel;
}
