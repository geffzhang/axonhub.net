using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Diagnostics;

/// <summary>
/// Candidate selection used only by the cache diagnostics export
/// (<c>buildModelDiagnostics</c>, channel_model_cache_diagnostics.go:288-333).
/// </summary>
/// <remarks>
/// The reference's owner-gated resolver runs the ordinary
/// <c>orchestrator.DefaultSelector</c>; the model query policy admits owner
/// users through <c>OwnerRule</c>. The port's selector only skips
/// <c>EvaluateQueryPrivacy</c> when constructed with
/// <c>bypassModelPrivacy: true</c>, so the diagnostics export builds its own
/// instance. The seam is unreachable without the owner gate in the resolver.
/// </remarks>
public interface ICacheDiagnosticsCandidateSelector
{
    Task<IReadOnlyList<ChannelCandidate>> SelectAsync(
        string model,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>CandidateSelectorDiagnostics.ReadAssociationCache</c>
    /// (diagnostics.go:29-56). The export calls this after building the model
    /// previews, so the model views' selections populate the entries first.
    /// </summary>
    IReadOnlyList<AssociationCacheEntrySnapshot> ReadAssociationCache();
}

/// <inheritdoc cref="ICacheDiagnosticsCandidateSelector"/>
public sealed class CacheDiagnosticsCandidateSelector : ICacheDiagnosticsCandidateSelector
{
    private readonly ModelCandidateSelector _selector;
    private readonly ModelAssociationCache _associationCache;

    public CacheDiagnosticsCandidateSelector(
        IModelStore models,
        IModelSettingsProvider settings,
        IChannelStore channels,
        IEnabledChannelProvider enabledChannels,
        IEnabledChannelCacheSnapshotProvider enabledChannelSnapshots,
        ModelAssociationCache associationCache,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(enabledChannels);
        ArgumentNullException.ThrowIfNull(enabledChannelSnapshots);
        ArgumentNullException.ThrowIfNull(associationCache);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _associationCache = associationCache;
        _selector = new ModelCandidateSelector(
            models,
            settings,
            channels,
            enabledChannels,
            timeProvider,
            bypassModelPrivacy: true,
            enabledChannelSnapshots: enabledChannelSnapshots,
            associationCache: associationCache);
    }

    public async Task<IReadOnlyList<ChannelCandidate>> SelectAsync(
        string model,
        CancellationToken cancellationToken = default)
    {
        var selection = await _selector
            .SelectAsync(new LlmRequest { Model = model }, new RequestIdentity(0, null), cancellationToken)
            .ConfigureAwait(false);

        return selection.Candidates;
    }

    public IReadOnlyList<AssociationCacheEntrySnapshot> ReadAssociationCache()
        => _associationCache.ReadAssociationCache();
}
