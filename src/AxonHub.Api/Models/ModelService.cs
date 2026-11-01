using AxonHub.Abstractions;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Models;

/// <summary>
/// Port of biz.ModelFacade: one model exposed to clients. The reference builds
/// it from either a configured Model entity or a channel's model entry; the
/// port has no Model entity (C088), so the display name is always the request
/// model and the owner is the channel type.
/// </summary>
public sealed record ModelFacade(
    string Id,
    string DisplayName,
    DateTime CreatedAt,
    long Created,
    string OwnedBy);

/// <summary>
/// Port of biz.ModelService.ListEnabledModels
/// (internal/server/biz/model.go:646).
/// </summary>
/// <remarks>
/// The reference first collects configured Model facades and then, when
/// <c>QueryAllChannelModels</c> is set, merges the models exposed by the enabled
/// channels, applying <c>ModelBlacklistRegex</c> to channel-derived IDs only.
/// The port has no <c>models</c> table yet (C088), so the configured set is
/// always empty and the result is exactly the channel-derived set.
/// </remarks>
public sealed class ModelService(
    IEnabledChannelProvider channels,
    IModelSettingsProvider settings)
{
    /// <summary>Port of ModelService.ListEnabledModels.</summary>
    public async Task<IReadOnlyList<ModelFacade>> ListEnabledModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var modelSettings = await settings.GetModelSettingsOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var enabled = await channels.ListEnabledAsync(cancellationToken).ConfigureAwait(false);

        // QueryAllChannelModels=false lists configured models only; the port has
        // none, so the list is empty (C088).
        if (!modelSettings.QueryAllChannelModels)
        {
            return [];
        }

        var blacklist = modelSettings.ModelBlacklistRegex;
        var result = new List<ModelFacade>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The reference iterates the enabled channels in slice order and each
        // channel's model entries from a Go map, so its order is unspecified;
        // the port keeps channel-query order and sorts each channel's entries,
        // which only makes the unspecified order deterministic (C088).
        foreach (var channel in enabled)
        {
            // ch.GetModelEntries() is the same derivation the load balancer uses.
            var entries = ChannelModelEntries.Build(channel).Values
                .OrderBy(static entry => entry.RequestModel, StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                var modelId = entry.RequestModel;

                // The blacklist applies only to channel-derived models. An empty
                // pattern never matches, and xregexp treats a metacharacter-free
                // pattern as an exact id comparison.
                if (blacklist.Length > 0 && XRegexp.MatchString(blacklist, modelId))
                {
                    continue;
                }

                if (!seen.Add(modelId))
                {
                    continue;
                }

                result.Add(new ModelFacade(
                    modelId,
                    modelId,
                    channel.CreatedAt,
                    GoTime.UnixSeconds(channel.CreatedAt),
                    channel.Type));
            }
        }

        return result;
    }
}
