using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Diagnostics;

/// <summary>The getCacheDiagnostics payload (system.graphql:471-475).</summary>
public sealed record CacheDiagnosticsPayload(
    string FileName,
    string Content,
    IReadOnlyList<DiagnosticsTargetValue> Targets);

/// <summary>
/// Port of the export builder behind getCacheDiagnostics
/// (internal/server/gql/channel_model_cache_diagnostics.go). The resolver keeps
/// the owner gate and the reference's error prefixes; this service performs the
/// two unbounded row reads, the enabled-cache snapshot and the candidate
/// selection each enabled model runs.
/// </summary>
public sealed class CacheDiagnosticsService(
    IChannelAdminStore channelStore,
    IModelStore modelStore,
    IEnabledChannelCacheSnapshotProvider enabledCache,
    ICacheDiagnosticsCandidateSelector candidateSelector,
    TimeProvider timeProvider)
{
    /// <summary>File name prefix of the downloaded export.</summary>
    public const string FileNamePrefix = "axonhub-channel-model-cache-diagnostics-";

    /// <summary>Go layout <c>20060102T150405Z</c>.</summary>
    public const string FileTimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    public async Task<CacheDiagnosticsPayload> GetCacheDiagnosticsAsync(
        IReadOnlyList<DiagnosticsTargetValue>? targets,
        CancellationToken cancellationToken = default)
    {
        var normalized = DiagnosticsTargets.Normalize(targets);

        GoValue export;
        try
        {
            export = await BuildExportAsync(normalized, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to build cache diagnostics export: " + ex.Message,
                ex);
        }

        string content;
        try
        {
            content = GoJsonIndent.Format(export.WriteJson(), "  ");
        }
        catch (Exception ex)
        {
            // The reference returns the marshal error without the build prefix
            // (system.resolvers.go:713-716).
            throw new InvalidOperationException(
                "failed to marshal diagnostics export: " + ex.Message,
                ex);
        }

        // time.Now() is read a second time here (system.resolvers.go:721), so
        // the file name is not tied to the export's exportedAt value.
        var fileName = FileNamePrefix
            + timeProvider.GetUtcNow().ToString(FileTimestampFormat, CultureInfo.InvariantCulture)
            + ".json";

        return new CacheDiagnosticsPayload(fileName, content, normalized);
    }

    private async Task<GoValue> BuildExportAsync(
        IReadOnlyList<DiagnosticsTargetValue> targets,
        CancellationToken cancellationToken)
    {
        var channels = await QueryChannelsAsync(cancellationToken).ConfigureAwait(false);
        var models = await QueryModelsAsync(cancellationToken).ConfigureAwait(false);

        // exportedAt is read after both queries and before the cache snapshot
        // (channel_model_cache_diagnostics.go:163-164).
        var exportedAt = GoTime.Rfc3339(timeProvider.GetUtcNow());
        var enabledCacheSnapshot = await enabledCache
            .GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);

        return await CacheDiagnosticsExport.BuildAsync(
                targets,
                exportedAt,
                channels,
                models,
                enabledCacheSnapshot,
                candidateSelector,
                timeProvider,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <c>client.Channel.Query().Order(Desc(ordering_weight)).Order(Asc(id)).All(ctx)</c>
    /// (channel_model_cache_diagnostics.go:148-154).
    /// </summary>
    private async Task<IReadOnlyList<ChannelDiagnosticsRow>> QueryChannelsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ChannelRecord> records;
        try
        {
            records = await channelStore.ListAllAsync(
                    new ChannelListQuery
                    {
                        Ordered = true,
                        SortField = ChannelSortField.OrderingWeight,
                        Direction = SortDirection.Desc,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query channels: " + ex.Message,
                ex);
        }

        var rows = new List<ChannelDiagnosticsRow>(records.Count);
        foreach (var record in records)
        {
            ChannelSettingsSnapshot? settings;
            try
            {
                settings = ChannelModelEntries.ParseSettingsSnapshot(record.Settings);
            }
            catch (JsonException ex)
            {
                // Ent decodes objects.ChannelSettings while scanning the row, so
                // a malformed document fails the query, not the value shaping.
                throw new InvalidOperationException(
                    "failed to query channels: " + ex.Message,
                    ex);
            }

            rows.Add(new ChannelDiagnosticsRow(record, settings));
        }

        return rows;
    }

    /// <summary>
    /// <c>client.Model.Query().Order(Asc(id)).All(ctx)</c>
    /// (channel_model_cache_diagnostics.go:156-161).
    /// </summary>
    private async Task<IReadOnlyList<ModelDiagnosticsRow>> QueryModelsAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelRecord> records;
        try
        {
            records = await modelStore
                .ListAllAsync(new ModelListFilter(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query models: " + ex.Message,
                ex);
        }

        var rows = new List<ModelDiagnosticsRow>(records.Count);
        foreach (var record in records)
        {
            ModelEntitySettings? settings;
            try
            {
                settings = ModelSettingsCodec.DeserializeEntitySettings(record.Settings);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to query models: " + ex.Message,
                    ex);
            }

            rows.Add(new ModelDiagnosticsRow(record, settings));
        }

        return rows;
    }
}
