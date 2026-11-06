using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Tests;

/// <summary>
/// A controllable clock whose timers fire immediately. The Antigravity project
/// discovery sleeps 1-2 seconds between onboarding attempts (antigravity.go:376-401);
/// the reference tests do not exist, so the port exercises those branches with
/// zero wall time while keeping the retry sequence intact.
/// </summary>
internal sealed class AntigravityTestClock(DateTimeOffset? utcNow = null) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow ?? DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
        => new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
}

/// <summary>
/// Records the proxy the exchange handler asks for and returns the scripted
/// executor, so a proxied request never leaves the test.
/// </summary>
internal sealed class ScriptedProxyExecutorFactory : IProxyExecutorFactory
{
    public List<ProxyConfig> Requests { get; } = [];

    public IExecutor? Executor { get; set; }

    public IExecutor Create(ProxyConfig proxyConfig)
    {
        ArgumentNullException.ThrowIfNull(proxyConfig);

        Requests.Add(proxyConfig);
        return Executor
            ?? throw new InvalidOperationException("proxy executor is not configured");
    }
}
