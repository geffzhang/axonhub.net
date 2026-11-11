using System.Diagnostics.CodeAnalysis;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>In-memory systems table mirroring the Dapper store's live-row predicate.</summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The in-memory semaphore owns no unmanaged state and the store lives for the test process.")]
internal sealed class FakeSystemStore : ISystemStore
{
    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private long _nextId = 1;

    public List<SystemRecord> Rows { get; } = [];

    /// <summary>The connection queries the root systems resolver issued.</summary>
    public List<SystemListQuery> ListQueries { get; } = [];

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

    /// <summary>Entgql connection read behind the root systems query.</summary>
    public Task<ListPage<SystemRecord>> ListAsync(
        SystemListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ListQueries.Add(query);

        var filtered = Rows
            .Where(row => row.DeletedAt == 0 && Matches(row, query.Where))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField != SystemSortField.Id,
            row => row.Id,
            row => query.SortField == SystemSortField.CreatedAt
                ? row.CreatedAt
                : (object?)row.UpdatedAt,
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<SystemRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    private static bool Matches(SystemRecord row, SystemListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id)
        {
            return false;
        }

        if (filter.IdIn is not null && !filter.IdIn.Contains(row.Id))
        {
            return false;
        }

        if (filter.Key is not null
            && !string.Equals(row.Key, filter.Key, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.KeyIn is not null
            && !filter.KeyIn.Contains(row.Key, StringComparer.Ordinal))
        {
            return false;
        }

        if (filter.KeyHasPrefix is not null
            && !row.Key.StartsWith(filter.KeyHasPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.KeyContainsFold is not null
            && !row.Key.Contains(filter.KeyContainsFold, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.ValueContainsFold is not null
            && !row.Value.Contains(
                filter.ValueContainsFold,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Not is not null && Matches(row, filter.Not))
        {
            return false;
        }

        if (filter.And is not null && !filter.And.All(nested => Matches(row, nested)))
        {
            return false;
        }

        if (filter.Or is not null && !filter.Or.Any(nested => Matches(row, nested)))
        {
            return false;
        }

        return true;
    }

    /// <summary>ent's plain System.Create() for the channel-settings CAS loop.</summary>
    public Task<bool> TryCreateAsync(
        SystemRecord system,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (TryCreateFailure is not null)
        {
            return Task.FromException<bool>(TryCreateFailure);
        }

        // The unique index on key covers soft-deleted rows too.
        if (Rows.Any(row => string.Equals(row.Key, system.Key, StringComparison.Ordinal)))
        {
            return Task.FromResult(false);
        }

        system.Id = _nextId++;
        Rows.Add(system);
        return Task.FromResult(true);
    }

    /// <summary>
    /// ent's System.Update().Where(id, updated_at, value).SetValue().Save():
    /// the fake applies the same predicates and bumps updated_at on a match.
    /// </summary>
    public Task<int> UpdateValueIfUnchangedAsync(
        long id,
        DateTime expectedUpdatedAt,
        string expectedValue,
        string value,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        UpdateValueIfUnchangedCalls++;
        if (UpdateValueIfUnchangedFailure is not null)
        {
            return Task.FromException<int>(UpdateValueIfUnchangedFailure);
        }

        var row = Rows.SingleOrDefault(candidate => candidate.Id == id && candidate.DeletedAt == 0);
        if (row is null
            || row.UpdatedAt != expectedUpdatedAt
            || !string.Equals(row.Value, expectedValue, StringComparison.Ordinal))
        {
            return Task.FromResult(0);
        }

        if (UpdateValueIfUnchangedOverride is { } script)
        {
            return Task.FromResult(script(row, value));
        }

        row.Value = value;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(1);
    }

    /// <summary>Scripts the CAS result; used to simulate losing writers.</summary>
    public Func<SystemRecord, string, int>? UpdateValueIfUnchangedOverride { get; set; }

    public Exception? UpdateValueIfUnchangedFailure { get; set; }

    public Exception? TryCreateFailure { get; set; }

    public int UpdateValueIfUnchangedCalls { get; private set; }

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

/// <summary>
/// Records the backup re-arm signal that updateSystemGeneralSettings sends
/// (BackupService.Reschedule, system.resolvers.go:159).
/// </summary>
internal sealed class RecordingAutoBackupRescheduleSignal : IAutoBackupRescheduleSignal
{
    public int Calls { get; private set; }

    public Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records the video storage scan signal that updateVideoStorageSettings sends
/// (video_storage.Worker.Reschedule, system.resolvers.go:171).
/// </summary>
internal sealed class RecordingVideoStorageRescheduleSignal : IVideoStorageRescheduleSignal
{
    public int Calls { get; private set; }

    public Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.CompletedTask;
    }
}
