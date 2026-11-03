using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory IChannelModelCatalogStore for the model fetcher: the reference
/// tests query a real channel row, so the fake returns the seeded row and
/// persists credential refreshes in place.
/// </summary>
internal sealed class FakeChannelModelCatalogStore : IChannelModelCatalogStore
{
    public Dictionary<long, ChannelRecord> Rows { get; } = [];

    public List<(long ChannelId, string CredentialsJson)> CredentialUpdates { get; } = [];

    public Exception? GetFailure { get; set; }

    public void Seed(ChannelRecord row) => Rows[row.Id] = row;

    public ChannelRecord? Row(long id) => Rows.GetValueOrDefault(id);

    public Task<ChannelRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        if (GetFailure is not null)
        {
            throw GetFailure;
        }

        return Task.FromResult(Rows.GetValueOrDefault(id));
    }

    public void UpdateCredentials(long id, string credentialsJson)
    {
        CredentialUpdates.Add((id, credentialsJson));
        if (Rows.TryGetValue(id, out var row))
        {
            row.Credentials = credentialsJson;
        }
    }
}

/// <summary>
/// In-memory IChannelModelSyncStore mirroring ChannelModelSyncStore: re-read in
/// the transaction, build the plan from that row, apply the supported
/// models/settings write, then run the price ensure. Every failure seam is
/// injectable so the sync wrapper mappings can be asserted.
/// </summary>
internal sealed class FakeChannelModelSyncStore : IChannelModelSyncStore
{
    private readonly Dictionary<long, ChannelRecord> _rows = [];

    public List<ChannelModelSyncPlan> Plans { get; } = [];

    public List<long> SyncCalls { get; } = [];

    public List<(long ChannelId, string CredentialsJson)> CredentialUpdates { get; } = [];

    public Exception? GetFailure { get; set; }

    public Exception? ListFailure { get; set; }

    public Exception? ReloadFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public bool ChannelMissingAtReload { get; set; }

    public void Seed(ChannelRecord row) => _rows[row.Id] = Clone(row);

    public ChannelRecord? Row(long id) => _rows.TryGetValue(id, out var row) ? row : null;

    /// <summary>Simulates a concurrent writer between the fetch and the reload.</summary>
    public void UpdateRow(long id, Action<ChannelRecord> mutate)
    {
        if (_rows.TryGetValue(id, out var row))
        {
            mutate(row);
        }
    }

    public Task<ChannelRecord?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        if (GetFailure is not null)
        {
            throw GetFailure;
        }

        return Task.FromResult(_rows.TryGetValue(id, out var row) ? Clone(row) : null);
    }

    public void UpdateCredentials(long id, string credentialsJson)
    {
        CredentialUpdates.Add((id, credentialsJson));
        if (_rows.TryGetValue(id, out var row))
        {
            row.Credentials = credentialsJson;
        }
    }

    public Task<IReadOnlyList<ChannelRecord>> ListAutoSyncEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        IReadOnlyList<ChannelRecord> rows = _rows.Values
            .Where(row => row.DeletedAt == 0
                && row.Status == "enabled"
                && row.AutoSyncSupportedModels)
            .OrderBy(row => row.Id)
            .Select(Clone)
            .ToList();
        return Task.FromResult(rows);
    }

    public async Task<(ChannelRecord? Channel, bool Changed)> SyncAsync(
        long channelId,
        Func<ChannelRecord, ChannelModelSyncPlan?> buildPlan,
        Func<long, IReadOnlyList<string>, CancellationToken, Task<bool>> ensurePrices,
        CancellationToken cancellationToken = default)
    {
        SyncCalls.Add(channelId);

        ChannelRecord current;
        try
        {
            if (ReloadFailure is not null)
            {
                throw ReloadFailure;
            }

            if (ChannelMissingAtReload || !_rows.TryGetValue(channelId, out var stored))
            {
                throw new StoreFailureException(
                    "failed to reload channel for model sync: ent: channel not found",
                    new InvalidOperationException("channel not found"));
            }

            current = Clone(stored);
        }
        catch (StoreFailureException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new StoreFailureException(
                "failed to reload channel for model sync: " + ex.Message,
                ex);
        }

        var plan = buildPlan(current);
        if (plan is null)
        {
            return (current, false);
        }

        Plans.Add(plan);

        if (plan.ModelsChanged)
        {
            try
            {
                if (UpdateFailure is not null)
                {
                    throw UpdateFailure;
                }

                var stored = _rows[channelId];
                stored.SupportedModels = plan.SupportedModelsJson;
                if (plan.SettingsJson is not null)
                {
                    stored.Settings = plan.SettingsJson;
                }

                stored.UpdatedAt = DateTime.UtcNow;
            }
            catch (StoreFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new StoreFailureException(
                    "failed to update channel supported models: " + ex.Message,
                    ex);
            }
        }

        var pricesChanged = await ensurePrices(channelId, plan.MergedModels, cancellationToken)
            .ConfigureAwait(false);

        if (plan.ModelsChanged)
        {
            current.SupportedModels = plan.SupportedModelsJson;
            current.Settings = plan.SettingsJson ?? current.Settings;
        }

        var changed = plan.ModelsChanged || plan.ModelProtocolsChanged || pricesChanged;
        return (current, changed);
    }

    internal static ChannelRecord Clone(ChannelRecord source) => new()
    {
        Id = source.Id,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        DeletedAt = source.DeletedAt,
        Type = source.Type,
        BaseUrl = source.BaseUrl,
        Name = source.Name,
        Status = source.Status,
        Credentials = source.Credentials,
        DisabledApiKeys = source.DisabledApiKeys,
        SupportedModels = source.SupportedModels,
        ManualModels = source.ManualModels,
        AutoSyncSupportedModels = source.AutoSyncSupportedModels,
        AutoSyncModelPattern = source.AutoSyncModelPattern,
        Tags = source.Tags,
        DefaultTestModel = source.DefaultTestModel,
        Policies = source.Policies,
        Settings = source.Settings,
        OrderingWeight = source.OrderingWeight,
        ErrorMessage = source.ErrorMessage,
        AutoDisabledAt = source.AutoDisabledAt,
        AutoDisableExpiresAt = source.AutoDisableExpiresAt,
        Remark = source.Remark,
        Endpoints = source.Endpoints,
    };
}

/// <summary>Settable clock for the provider-conf cache.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed class FakeAutoSyncFrequencyProvider(AutoSyncFrequency frequency)
    : IChannelAutoSyncFrequencyProvider
{
    public AutoSyncFrequency Frequency { get; set; } = frequency;

    public Task<AutoSyncFrequency> GetAutoSyncFrequencyAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(Frequency);
}

/// <summary>Counts the reload notifications the sync pass emits.</summary>
internal sealed class RecordingModelSyncReloadHook : IChannelModelSyncReloadHook
{
    public int Count { get; private set; }

    public void ReloadAfterModelSync() => Count++;
}

internal static class ModelFetchTestResponses
{
    public static HttpResponseData Json(
        string body,
        int statusCode = 200,
        string reasonPhrase = "OK")
        => new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
            RawResponse = new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
            {
                ReasonPhrase = reasonPhrase,
            },
        };
}
