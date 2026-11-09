using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// One aligned probe bucket (port of biz.ChannelProbePoint, channel_probe.go:27-33).
/// The integer counters stay 64-bit here; the GraphQL Int projection narrows
/// them at the boundary (C190).
/// </summary>
public sealed record ChannelProbePointModel(
    long Timestamp,
    long TotalRequestCount,
    long SuccessRequestCount,
    double? AvgTokensPerSecond,
    double? AvgTimeToFirstTokenMs);

/// <summary>Probe series of one channel (port of biz.ChannelProbeData).</summary>
public sealed record ChannelProbeDataModel(
    long ChannelId,
    IReadOnlyList<ChannelProbePointModel> Points);

/// <summary>
/// Port of biz.ChannelProbeService's read side
/// (internal/server/biz/channel_probe.go:345-465): the <c>channelProbeData</c>
/// batch query over an interval-aligned window. The writer, the per-minute
/// scheduler and the manual trigger are not ported (C249).
/// </summary>
/// <remarks>
/// The reference reads the effective probe setting through
/// <c>authz.RunWithScopeDecision(ctx, scopes.ScopeReadChannels, ...)</c>:
/// principals with the scope read the stored <c>system_channel_settings</c> row
/// under an attached Allow decision, while a denied principal's read fails the
/// System privacy policy and <c>ChannelSettingOrDefault</c> swallows the error
/// and returns the default setting. The port has no System privacy hook, so the
/// denied branch skips the read and uses the default directly; the allowed
/// branch has the same "any error falls back to the default" behaviour.
/// </remarks>
public sealed class ChannelProbeService(
    IChannelProbeStore probes,
    ISystemSettingsAdminService settings,
    AuthorizationScope scope,
    TimeProvider timeProvider)
{
    /// <summary>Port of ChannelProbeService.BatchQueryChannelProbes.</summary>
    public async Task<IReadOnlyList<ChannelProbeDataModel>> BatchQueryChannelProbesAsync(
        IReadOnlyList<long> channelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        // The reference returns a non-nil empty slice before reading settings.
        if (channelIds.Count == 0)
        {
            return [];
        }

        return await QueryChannelProbesAsync(channelIds, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of ChannelProbeService.QueryChannelProbes.</summary>
    public async Task<IReadOnlyList<ChannelProbeDataModel>> QueryChannelProbesAsync(
        IReadOnlyList<long> channelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);

        var probeSetting = await EffectiveProbeSettingAsync(cancellationToken)
            .ConfigureAwait(false);
        var rangeMinutes = GetQueryRangeMinutes(probeSetting.Frequency);
        var intervalMinutes = GetIntervalMinutes(probeSetting.Frequency);

        // now := xtime.UTCNow(); end = now.Truncate(interval); start = end - range.
        var nowUnix = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var endTime = TruncateToInterval(nowUnix, intervalMinutes);
        var startTime = endTime - ((long)rangeMinutes * 60L);

        // The reference returns the ent query error untouched (no prefix).
        var rows = await probes
            .QueryAsync(channelIds, startTime, endTime, cancellationToken)
            .ConfigureAwait(false);

        // channel_id -> timestamp -> probe; a later row wins on a duplicate key.
        var probeMap = new Dictionary<long, Dictionary<long, ChannelProbeRecord>>();
        foreach (var row in rows)
        {
            if (!probeMap.TryGetValue(row.ChannelId, out var byTimestamp))
            {
                byTimestamp = [];
                probeMap[row.ChannelId] = byTimestamp;
            }

            byTimestamp[row.Timestamp] = row;
        }

        var timestamps = GenerateTimestamps(startTime, endTime, intervalMinutes);
        var result = new List<ChannelProbeDataModel>(channelIds.Count);
        foreach (var channelId in channelIds)
        {
            probeMap.TryGetValue(channelId, out var channelProbes);

            var points = new List<ChannelProbePointModel>(timestamps.Count);
            foreach (var timestamp in timestamps)
            {
                if (channelProbes is not null
                    && channelProbes.TryGetValue(timestamp, out var probe))
                {
                    points.Add(new ChannelProbePointModel(
                        timestamp,
                        probe.TotalRequestCount,
                        probe.SuccessRequestCount,
                        probe.AvgTokensPerSecond,
                        probe.AvgTimeToFirstTokenMs));
                }
                else
                {
                    // Fill the missing point with zeros and null averages.
                    points.Add(new ChannelProbePointModel(timestamp, 0, 0, null, null));
                }
            }

            result.Add(new ChannelProbeDataModel(channelId, points));
        }

        return result;
    }

    /// <summary>Port of biz.ChannelProbeSetting.GetQueryRangeMinutes (system.go:593-608).</summary>
    internal static int GetQueryRangeMinutes(string frequency) => frequency switch
    {
        ProbeFrequencies.OneMinute => 10,
        ProbeFrequencies.FiveMinutes => 60,
        ProbeFrequencies.ThirtyMinutes => 720,
        ProbeFrequencies.OneHour => 1440,
        _ => 10,
    };

    /// <summary>Port of biz.ChannelProbeSetting.GetIntervalMinutes (system.go:610-627).</summary>
    internal static int GetIntervalMinutes(string frequency) => frequency switch
    {
        ProbeFrequencies.OneMinute => 1,
        ProbeFrequencies.FiveMinutes => 5,
        ProbeFrequencies.ThirtyMinutes => 30,
        ProbeFrequencies.OneHour => 60,
        _ => 1,
    };

    /// <summary>
    /// Go's <c>time.Time.Truncate</c> rounds down since the zero time; the Unix
    /// epoch offset is an exact multiple of every supported interval, so floor
    /// division of the Unix second count lands on the same boundary.
    /// </summary>
    internal static long TruncateToInterval(long unixSeconds, int intervalMinutes)
    {
        var step = (long)intervalMinutes * 60L;
        var remainder = unixSeconds % step;
        if (remainder < 0)
        {
            remainder += step;
        }

        return unixSeconds - remainder;
    }

    /// <summary>
    /// Port of generateTimestamps (channel_probe.go:345-357): every aligned
    /// timestamp from start to end inclusive. Range and interval always come
    /// from the same switch, so the step is positive and the range divides it.
    /// </summary>
    internal static IReadOnlyList<long> GenerateTimestamps(
        long startUnix,
        long endUnix,
        int intervalMinutes)
    {
        var step = (long)intervalMinutes * 60L;
        var timestamps = new List<long>(((endUnix - startUnix) / step) + 1);
        for (var timestamp = startUnix; timestamp <= endUnix; timestamp += step)
        {
            timestamps.Add(timestamp);
        }

        return timestamps;
    }

    /// <summary>
    /// The effective probe setting: the stored value for a principal with
    /// <c>read_channels</c>, the default otherwise. Every read/decode failure
    /// falls back to the default, like ChannelSettingOrDefault
    /// (system.go:1355-1370).
    /// </summary>
    private async Task<ChannelProbeSetting> EffectiveProbeSettingAsync(
        CancellationToken cancellationToken)
    {
        if (!await scope.HasScopeAsync(AuthorizationScope.ReadChannels, cancellationToken)
                .ConfigureAwait(false))
        {
            // The reference's denied decision makes the ent read fail; the
            // orDefault wrapper then returns defaultChannelSetting.
            return SystemSettingsDefaults.Channel.Probe;
        }

        try
        {
            var stored = await settings.GetChannelSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            return stored.Probe;
        }
        catch (Exception)
        {
            // ChannelSettingOrDefault logs every non-NotFound error and falls
            // back to the default (store failure, privacy deny, corrupt JSON).
            return SystemSettingsDefaults.Channel.Probe;
        }
    }
}
