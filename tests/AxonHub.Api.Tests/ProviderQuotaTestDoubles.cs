using AxonHub.Api.Orchestration;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

internal sealed record QuotaErrorWrite(
    long Id,
    string? AccountKey,
    string QuotaData,
    DateTime NextCheckAt);

internal sealed record QuotaProviderResetWrite(
    long Id,
    string ProviderType,
    string? AccountKey,
    string QuotaData,
    DateTime NextCheckAt);

/// <summary>
/// In-memory port of the <c>provider_quota_status</c> store. The unique index
/// on <c>channel_id</c> only rejects plain inserts, exactly like the reference's
/// create-on-error path; the upsert rewrites the live row in place.
/// </summary>
internal sealed class FakeProviderQuotaStatusStore : IProviderQuotaStatusStore
{
    private readonly object _gate = new();
    private long _nextId = 1;

    public List<ProviderQuotaStatusRecord> Rows { get; } = [];

    public List<ProviderQuotaStatusRecord> Upserts { get; } = [];

    public List<ProviderQuotaStatusRecord> Creates { get; } = [];

    public List<QuotaErrorWrite> ErrorUpdates { get; } = [];

    public List<QuotaProviderResetWrite> ProviderResets { get; } = [];

    public List<long> HardDeletes { get; } = [];

    public Exception? ListAllFailure { get; set; }

    public Exception? UpsertFailure { get; set; }

    public Exception? ResetForProviderErrorFailure { get; set; }

    public Exception? UpdateErrorFailure { get; set; }

    public Exception? HardDeleteFailure { get; set; }

    public ProviderQuotaStatusRecord Add(ProviderQuotaStatusRecord row)
    {
        lock (_gate)
        {
            row.Id = _nextId++;
            Rows.Add(row);
            return row;
        }
    }

    public Task<IReadOnlyList<ProviderQuotaStatusRecord>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (ListAllFailure is not null)
        {
            return Task.FromException<IReadOnlyList<ProviderQuotaStatusRecord>>(ListAllFailure);
        }

        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ProviderQuotaStatusRecord>>(
                Rows.Where(static row => row.DeletedAt == 0)
                    .OrderBy(static row => row.Id)
                    .Select(Clone)
                    .ToList());
        }
    }

    public Task<ProviderQuotaStatusRecord?> GetByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            var row = Rows.FirstOrDefault(
                candidate => candidate.ChannelId == channelId && candidate.DeletedAt == 0);
            return Task.FromResult(row is null ? null : Clone(row));
        }
    }

    public Task UpsertAsync(
        ProviderQuotaStatusRecord status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpsertFailure is not null)
        {
            return Task.FromException(UpsertFailure);
        }

        lock (_gate)
        {
            Upserts.Add(Clone(status));

            var existing = Rows.FirstOrDefault(
                row => row.ChannelId == status.ChannelId && row.DeletedAt == 0);
            if (existing is null)
            {
                Rows.Add(new ProviderQuotaStatusRecord
                {
                    Id = _nextId++,
                    ProviderType = status.ProviderType,
                    Status = status.Status,
                    QuotaData = status.QuotaData,
                    NextResetAt = status.NextResetAt,
                    Ready = status.Ready,
                    NextCheckAt = status.NextCheckAt,
                    AccountKey = status.AccountKey,
                    ChannelId = status.ChannelId,
                });
                return Task.CompletedTask;
            }

            // ON CONFLICT (channel_id) DO UPDATE SET <inserted columns>: only the
            // columns the insert spec carries are rewritten.
            existing.ProviderType = status.ProviderType;
            existing.Status = status.Status;
            existing.QuotaData = status.QuotaData;
            existing.NextResetAt = status.NextResetAt;
            existing.Ready = status.Ready;
            existing.NextCheckAt = status.NextCheckAt;
            existing.AccountKey = status.AccountKey;
        }

        return Task.CompletedTask;
    }

    public Task CreateAsync(
        ProviderQuotaStatusRecord status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            if (Rows.Any(row => row.ChannelId == status.ChannelId && row.DeletedAt == 0))
            {
                return Task.FromException(
                    new InvalidOperationException(
                        "duplicate key value violates unique constraint "
                        + "\"provider_quota_status_channel_id_key\""));
            }

            Creates.Add(Clone(status));
            Rows.Add(new ProviderQuotaStatusRecord
            {
                Id = _nextId++,
                ProviderType = status.ProviderType,
                Status = status.Status,
                QuotaData = status.QuotaData,
                NextResetAt = status.NextResetAt,
                Ready = status.Ready,
                NextCheckAt = status.NextCheckAt,
                AccountKey = status.AccountKey,
                ChannelId = status.ChannelId,
            });
        }

        return Task.CompletedTask;
    }

    public Task ResetForProviderErrorAsync(
        long id,
        string providerType,
        string? accountKey,
        string quotaData,
        DateTime nextCheckAt,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (ResetForProviderErrorFailure is not null)
        {
            return Task.FromException(ResetForProviderErrorFailure);
        }

        lock (_gate)
        {
            ProviderResets.Add(
                new QuotaProviderResetWrite(id, providerType, accountKey, quotaData, nextCheckAt));
            var row = Rows.Single(row => row.Id == id);
            row.ProviderType = providerType;
            row.AccountKey = accountKey;
            row.Status = ProviderQuotaStatuses.Unknown;
            row.Ready = false;
            row.QuotaData = quotaData;
            row.NextResetAt = null;
            row.NextCheckAt = nextCheckAt;
        }

        return Task.CompletedTask;
    }

    public Task UpdateErrorAsync(
        long id,
        string? accountKey,
        string quotaData,
        DateTime nextCheckAt,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateErrorFailure is not null)
        {
            return Task.FromException(UpdateErrorFailure);
        }

        lock (_gate)
        {
            ErrorUpdates.Add(new QuotaErrorWrite(id, accountKey, quotaData, nextCheckAt));
            var row = Rows.Single(row => row.Id == id);
            row.AccountKey = accountKey;
            row.QuotaData = quotaData;
            row.NextCheckAt = nextCheckAt;
        }

        return Task.CompletedTask;
    }

    public Task<int> HardDeleteByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (HardDeleteFailure is not null)
        {
            return Task.FromException<int>(HardDeleteFailure);
        }

        lock (_gate)
        {
            HardDeletes.Add(channelId);
            var removed = Rows.RemoveAll(row => row.ChannelId == channelId);
            return Task.FromResult(removed);
        }
    }

    public static ProviderQuotaStatusRecord Clone(ProviderQuotaStatusRecord row) => new()
    {
        Id = row.Id,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        ProviderType = row.ProviderType,
        Status = row.Status,
        QuotaData = row.QuotaData,
        NextResetAt = row.NextResetAt,
        Ready = row.Ready,
        NextCheckAt = row.NextCheckAt,
        AccountKey = row.AccountKey,
        ChannelId = row.ChannelId,
    };
}

/// <summary>Scripted checker with no reset capability.</summary>
internal class ScriptedQuotaChecker : IQuotaChecker
{
    private readonly object _gate = new();
    private readonly List<ChannelRecord> _checkedChannels = [];

    public IReadOnlyList<ChannelRecord> CheckedChannels
    {
        get
        {
            lock (_gate)
            {
                return [.. _checkedChannels];
            }
        }
    }

    public int CheckCalls => CheckedChannels.Count;

    public QuotaData Result { get; set; } = new();

    /// <summary>Overrides <see cref="Result"/> per channel when set.</summary>
    public Func<ChannelRecord, QuotaData>? Handler { get; set; }

    public Exception? Error { get; set; }

    public bool SupportsChannel(ChannelRecord channel)
    {
        _ = channel;
        return true;
    }

    public Task<QuotaData> CheckQuotaAsync(
        ChannelRecord channel,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            _checkedChannels.Add(channel);
        }

        if (Error is not null)
        {
            return Task.FromException<QuotaData>(Error);
        }

        return Task.FromResult(Handler is null ? Result : Handler(channel));
    }
}

/// <summary>
/// Records the channel ids handed to the channel-service quota hook
/// (channel_provider_quota_hook.go). The reference hook never fails the
/// mutation, so <see cref="Error"/> exercises only the logged path.
/// </summary>
internal sealed class RecordingQuotaInvalidator : IProviderQuotaInvalidator
{
    private readonly object _gate = new();
    private readonly List<long> _channelIds = [];

    public IReadOnlyList<long> ChannelIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _channelIds];
            }
        }
    }

    public Exception? Error { get; set; }

    public Task InvalidateChannelQuotaAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        lock (_gate)
        {
            _channelIds.Add(channelId);
        }

        return Error is null ? Task.CompletedTask : Task.FromException(Error);
    }
}

/// <summary>Scripted checker that also implements the reset capability.</summary>
internal sealed class ScriptedQuotaResetter : ScriptedQuotaChecker, IQuotaResetter
{
    public ResetList ListResult { get; set; } = new() { Supported = true };

    public Exception? ListError { get; set; }

    public Exception? ResetError { get; set; }

    public int ResetCalls { get; private set; }

    public Task<ResetList> ListResetsAsync(
        ChannelRecord channel,
        CancellationToken cancellationToken)
    {
        _ = channel;
        _ = cancellationToken;
        if (ListError is not null)
        {
            return Task.FromException<ResetList>(ListError);
        }

        return Task.FromResult(ListResult);
    }

    public Task ResetAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        _ = channel;
        _ = cancellationToken;
        ResetCalls++;
        return ResetError is null ? Task.CompletedTask : Task.FromException(ResetError);
    }
}
