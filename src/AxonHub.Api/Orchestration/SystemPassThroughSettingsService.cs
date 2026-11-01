using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Read side of the reference's SystemService.PassThrough and
/// UserAgentPassThrough (internal/server/biz/system.go:1743-1785).
/// </summary>
public interface IPassThroughSettingsProvider
{
    /// <summary>
    /// Reads both flags as one snapshot. The reference reads them lazily per
    /// middleware and caches them; the port's pipeline hooks are synchronous,
    /// so the orchestrator takes one snapshot per request (C091).
    /// </summary>
    Task<PassThroughSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The global body/response pass-through flag.</summary>
    Task<bool> GetPassThroughAsync(CancellationToken cancellationToken = default);

    /// <summary>The global User-Agent pass-through flag.</summary>
    Task<bool> GetUserAgentPassThroughAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads <c>system_pass_through</c> and <c>system_user_agent_pass_through</c>.
/// A missing row is false; a lookup failure is an error in the reference, which
/// the middlewares log and treat as disabled.
/// </summary>
public sealed class SystemPassThroughSettingsService(ISystemStore systems)
    : IPassThroughSettingsProvider
{
    /// <summary>biz.SystemKeyPassThrough.</summary>
    public const string PassThroughKey = "system_pass_through";

    /// <summary>biz.SystemKeyUserAgentPassThrough.</summary>
    public const string UserAgentPassThroughKey = "system_user_agent_pass_through";

    public async Task<PassThroughSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        // Each key degrades independently: getSystemValue returns the lookup
        // error, the middleware logs it and treats that flag as disabled.
        var passThrough = await ReadFlagAsync(PassThroughKey, cancellationToken)
            .ConfigureAwait(false);
        var userAgent = await ReadFlagAsync(UserAgentPassThroughKey, cancellationToken)
            .ConfigureAwait(false);

        return new PassThroughSettings(passThrough, userAgent);
    }

    public async Task<bool> GetPassThroughAsync(CancellationToken cancellationToken = default)
        => await ReadFlagAsync(PassThroughKey, cancellationToken).ConfigureAwait(false);

    public async Task<bool> GetUserAgentPassThroughAsync(
        CancellationToken cancellationToken = default)
        => await ReadFlagAsync(UserAgentPassThroughKey, cancellationToken).ConfigureAwait(false);

    private async Task<bool> ReadFlagAsync(string key, CancellationToken cancellationToken)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference distinguishes ent.IsNotFound (false, nil) from a
            // store error; the middlewares disable pass-through on either.
            return false;
        }

        return row is not null && row.Value == "true";
    }
}

/// <summary>The per-request snapshot of the two global pass-through flags.</summary>
public sealed record PassThroughSettings(bool PassThrough, bool UserAgentPassThrough)
{
    /// <summary>Both flags off, the reference default for a fresh install.</summary>
    public static PassThroughSettings Disabled { get; } = new(false, false);
}

/// <summary>
/// A provider that always returns the same snapshot, for orchestrators built
/// without a system store (tests and direct constructions).
/// </summary>
public sealed class StaticPassThroughSettingsProvider(PassThroughSettings settings)
    : IPassThroughSettingsProvider
{
    public Task<PassThroughSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(settings);
    }

    public Task<bool> GetPassThroughAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(settings.PassThrough);
    }

    public Task<bool> GetUserAgentPassThroughAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(settings.UserAgentPassThrough);
    }
}
