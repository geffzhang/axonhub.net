using AxonHub.Api.Diagnostics;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Stands in for the enabled-channels grain read the export performs. The
/// snapshot is returned verbatim; <see cref="OnGet"/> lets a test move the
/// clock (or record the call) between the export's timestamp reads.
/// </summary>
internal sealed class FakeEnabledChannelCacheSnapshotProvider : IEnabledChannelCacheSnapshotProvider
{
    public EnabledChannelsSnapshot Snapshot { get; set; } = new();

    public Exception? Failure { get; set; }

    public Action? OnGet { get; set; }

    public int GetCount { get; private set; }

    public Task<EnabledChannelsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        GetCount++;
        OnGet?.Invoke();

        return Failure is null
            ? Task.FromResult(Snapshot)
            : Task.FromException<EnabledChannelsSnapshot>(Failure);
    }
}

/// <summary>
/// Stands in for the owner-gated candidate selection the export runs per
/// enabled model. It records every model it is asked about and lets a test
/// return per-model candidates or fail every call (the export swallows the
/// error and keeps an empty preview).
/// </summary>
internal sealed class FakeDiagnosticsCandidateSelector : ICacheDiagnosticsCandidateSelector
{
    public List<string> Calls { get; } = [];

    public Dictionary<string, IReadOnlyList<ChannelCandidate>> Results { get; } =
        new(StringComparer.Ordinal);

    public Exception? Failure { get; set; }

    public Func<string, IReadOnlyList<ChannelCandidate>>? OnSelect { get; set; }

    public Task<IReadOnlyList<ChannelCandidate>> SelectAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls.Add(model);

        if (OnSelect is not null)
        {
            return Task.FromResult(OnSelect(model));
        }

        if (Failure is not null)
        {
            return Task.FromException<IReadOnlyList<ChannelCandidate>>(Failure);
        }

        return Task.FromResult<IReadOnlyList<ChannelCandidate>>(
            Results.TryGetValue(model, out var result) ? result : []);
    }
}

/// <summary>Records the clearCache reload and can fail it with a given error.</summary>
internal sealed class DiagnosticsCacheRefresher : IEnabledChannelsCacheRefresher
{
    public int RequestCount { get; private set; }

    public int ReloadCount { get; private set; }

    public Exception? ReloadFailure { get; set; }

    public void RequestRefresh() => RequestCount++;

    public Task ReloadAsync()
    {
        ReloadCount++;
        return ReloadFailure is null
            ? Task.CompletedTask
            : Task.FromException(ReloadFailure);
    }
}
