using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>In-memory systems table mirroring the Dapper store's live-row predicate.</summary>
internal sealed class FakeSystemStore : ISystemStore
{
    private long _nextId = 1;

    public List<SystemRecord> Rows { get; } = [];

    public Exception? GetFailure { get; set; }

    public Task<long> CreateAsync(SystemRecord system, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        system.Id = _nextId++;
        Rows.Add(system);
        return Task.FromResult(system.Id);
    }

    public Task<SystemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<SystemRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetFailure is not null)
        {
            return Task.FromException<SystemRecord?>(GetFailure);
        }

        return Task.FromResult(Rows.SingleOrDefault(row => row.Key == key && row.DeletedAt == 0));
    }

    public SystemRecord Add(string key, string value, Action<SystemRecord>? configure = null)
    {
        var row = new SystemRecord { Key = key, Value = value };
        configure?.Invoke(row);
        row.Id = _nextId++;
        Rows.Add(row);
        return row;
    }
}

/// <summary>Scripted security settings for the IP blocklist middleware tests.</summary>
internal sealed class FakeSecuritySettingsProvider : ISecuritySettingsProvider
{
    public SecuritySettings Settings { get; set; } = SecuritySettings.Default;

    public int Calls { get; private set; }

    public Task<SecuritySettings> GetSecuritySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.FromResult(Settings);
    }
}

/// <summary>Fixed retry policy for tests that do not exercise the settings row.</summary>
internal sealed class StaticRetryPolicyProvider(RetryPolicy policy) : IRetryPolicyProvider
{
    public int Calls { get; private set; }

    public Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.FromResult(policy);
    }
}
