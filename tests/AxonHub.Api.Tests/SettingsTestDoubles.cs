using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>In-memory systems table mirroring the Dapper store's live-row predicate.</summary>
internal sealed class FakeSystemStore : ISystemStore
{
    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private long _nextId = 1;

    public List<SystemRecord> Rows { get; } = [];

    public Exception? GetFailure { get; set; }

    public Exception? SetFailure { get; set; }

    public Exception? CreateIfAbsentFailure { get; set; }

    /// <summary>Fails the read of one key only, leaving the claim/marker reads intact.</summary>
    public Dictionary<string, Exception> GetFailuresByKey { get; } = [];

    /// <summary>Fails the write of one key only.</summary>
    public Dictionary<string, Exception> SetFailuresByKey { get; } = [];

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

        if (GetFailuresByKey.TryGetValue(key, out var failure))
        {
            return Task.FromException<SystemRecord?>(failure);
        }

        return Task.FromResult(Rows.SingleOrDefault(row => row.Key == key && row.DeletedAt == 0));
    }

    /// <summary>ent's OnConflict(key).UpdateNewValues(), including resurrecting a soft-deleted row.</summary>
    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (SetFailure is not null)
        {
            return Task.FromException(SetFailure);
        }

        if (SetFailuresByKey.TryGetValue(key, out var failure))
        {
            return Task.FromException(failure);
        }

        var existing = Rows.SingleOrDefault(row => row.Key == key);
        if (existing is null)
        {
            Rows.Add(new SystemRecord { Id = _nextId++, Key = key, Value = value });
        }
        else
        {
            existing.Value = value;
            existing.DeletedAt = 0;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// ent's OnConflict(key) + ResolveWithIgnore: the unique index on key
    /// covers the soft-deleted row too, so any existing row suppresses the
    /// insert.
    /// </summary>
    public Task CreateIfAbsentAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateIfAbsentFailure is not null)
        {
            return Task.FromException(CreateIfAbsentFailure);
        }

        if (!Rows.Any(row => string.Equals(row.Key, key, StringComparison.Ordinal)))
        {
            Rows.Add(new SystemRecord { Id = _nextId++, Key = key, Value = value });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// AbstractService.RunInTransaction over the in-memory table: serialize
    /// competing migrations the way PostgreSQL serializes the claim insert,
    /// and restore the table when the callback throws.
    /// </summary>
    public async Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await _transactionLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = Rows.Select(Clone).ToList();
            try
            {
                await action(cancellationToken);
            }
            catch
            {
                Rows.Clear();
                Rows.AddRange(snapshot);
                _nextId = Rows.Count == 0 ? 1 : Rows.Max(row => row.Id) + 1;
                throw;
            }
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    public SystemRecord Add(string key, string value, Action<SystemRecord>? configure = null)
    {
        var row = new SystemRecord { Key = key, Value = value };
        configure?.Invoke(row);
        row.Id = _nextId++;
        Rows.Add(row);
        return row;
    }

    private static SystemRecord Clone(SystemRecord row) => new()
    {
        Id = row.Id,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        Key = row.Key,
        Value = row.Value,
    };
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

/// <summary>Fixed model settings for tests that do not exercise the settings row.</summary>
internal sealed class StaticModelSettingsProvider(ModelSettings settings) : IModelSettingsProvider
{
    public int Calls { get; private set; }

    public Task<ModelSettings> GetModelSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.FromResult(settings);
    }
}
