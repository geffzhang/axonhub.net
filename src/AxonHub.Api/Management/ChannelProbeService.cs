using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;

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
/// Port of biz.ChannelProbeService
/// (internal/server/biz/channel_probe.go): the <c>channelProbeData</c> batch
/// query over an interval-aligned window and the probe writer
/// (<c>runProbe</c>). The per-minute scheduler lives in the Orleans
/// <c>ChannelProbeGrain</c>; the reference's unused <c>RunProbeNow</c> has no
/// caller and is not exposed.
/// </summary>
/// <remarks>
/// The read side reads the effective probe setting through
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
    IChannelStore channels,
    ISystemSettingsAdminService settings,
    AuthorizationScope scope,
    ChannelProbeRunState runState,
    TimeProvider timeProvider,
    ILogger<ChannelProbeService> logger)
{
    private static readonly Action<ILogger, Exception?> LogChannelSettingFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "ChannelProbeSettingFailed"),
            "failed to get channel setting");

    private static readonly Action<ILogger, Exception?> LogProbeDisabled =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(0, "ChannelProbeDisabled"),
            "Channel probe is disabled, skipping");

    private static readonly Action<ILogger, long, Exception?> LogProbeAlreadyExecuted =
        LoggerMessage.Define<long>(
            LogLevel.Debug,
            new EventId(0, "ChannelProbeAlreadyExecuted"),
            "Skipping probe, already executed for this interval timestamp={Timestamp}");

    private static readonly Action<ILogger, int, long, Exception?> LogProbeStarting =
        LoggerMessage.Define<int, long>(
            LogLevel.Debug,
            new EventId(0, "ChannelProbeStarting"),
            "Starting channel probe interval_minutes={IntervalMinutes} timestamp={Timestamp}");

    private static readonly Action<ILogger, Exception?> LogQueryEnabledChannelsFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ChannelProbeEnabledChannelsFailed"),
            "Failed to query enabled channels");

    private static readonly Action<ILogger, Exception?> LogNoEnabledChannels =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(0, "ChannelProbeNoEnabledChannels"),
            "No enabled channels to probe");

    private static readonly Action<ILogger, Exception?> LogComputeStatsFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ChannelProbeStatsFailed"),
            "Failed to compute channel probe stats");

    private static readonly Action<ILogger, Exception?> LogNoProbeData =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(0, "ChannelProbeNoData"),
            "No probe data to store (all channels have 0 requests)");

    private static readonly Action<ILogger, Exception?> LogCreateProbesFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ChannelProbeCreateFailed"),
            "Failed to create channel probes");

    private static readonly Action<ILogger, int, long, Exception?> LogProbeCompleted =
        LoggerMessage.Define<int, long>(
            LogLevel.Debug,
            new EventId(0, "ChannelProbeCompleted"),
            "Channel probe completed channels_probed={ChannelsProbed} timestamp={Timestamp}");

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

    /// <summary>
    /// Port of ChannelProbeService.runProbe (channel_probe.go:237-335): one
    /// probe pass for the aligned interval. Every failure is logged and the
    /// pass returns; the aligned timestamp is consumed before the database
    /// work, so a failing pass still blocks a rerun in the same interval.
    /// </summary>
    public async Task RunProbeAsync(CancellationToken cancellationToken = default)
    {
        // runProbePeriodically wraps the pass in a system bypass
        // (channel_probe_internal.go:8-12): the scheduled task carries no
        // principal and reads the stored setting as system.
        var setting = await ChannelSettingOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!setting.Probe.Enabled)
        {
            LogProbeDisabled(logger, null);
            return;
        }

        var intervalMinutes = GetIntervalMinutes(setting.Probe.Frequency);
        var now = timeProvider.GetUtcNow();
        var alignedUnix = TruncateToInterval(now.ToUnixTimeSeconds(), intervalMinutes);

        if (!runState.TryBeginRun(alignedUnix))
        {
            LogProbeAlreadyExecuted(logger, alignedUnix, null);
            return;
        }

        LogProbeStarting(logger, intervalMinutes, alignedUnix, null);

        IReadOnlyList<ChannelRecord> enabledChannels;
        try
        {
            enabledChannels = await channels.ListEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogQueryEnabledChannelsFailed(logger, ex);
            return;
        }

        if (enabledChannels.Count == 0)
        {
            LogNoEnabledChannels(logger, null);
            return;
        }

        var channelIds = new List<long>(enabledChannels.Count);
        foreach (var channel in enabledChannels)
        {
            channelIds.Add(channel.Id);
        }

        // start = aligned - interval; end = aligned (the reference uses a
        // half-open [start, aligned) window over created_at).
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(alignedUnix).UtcDateTime;
        var startUtc = endUtc.AddMinutes(-intervalMinutes);

        IReadOnlyList<ChannelProbeStatsRow> stats;
        try
        {
            stats = await probes.ComputeStatsAsync(channelIds, startUtc, endUtc, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogComputeStatsFailed(logger, ex);
            return;
        }

        // The reference scans effective_latency_ms into int64; a NULL sample
        // makes database/sql fail the whole pass.
        var statsByChannel = new Dictionary<long, ChannelProbeStatsRow>(stats.Count);
        foreach (var row in stats)
        {
            if (row.EffectiveLatencyMs is null)
            {
                LogComputeStatsFailed(
                    logger,
                    new InvalidOperationException(
                        "failed to scan probe result: converting NULL to int64 is unsupported"));
                return;
            }

            statsByChannel[row.ChannelId] = row;
        }

        var probesToCreate = new List<ChannelProbeRecord>();
        foreach (var channel in enabledChannels)
        {
            if (!statsByChannel.TryGetValue(channel.Id, out var row) || row.TotalCount == 0)
            {
                continue;
            }

            // Streaming: tokens / ((latency - first_token_latency) / 1000);
            // non-streaming: tokens / (latency / 1000).
            double? tokensPerSecond = null;
            if (row.TotalTokens > 0 && row.EffectiveLatencyMs is > 0)
            {
                tokensPerSecond = (double)row.TotalTokens
                    / (row.EffectiveLatencyMs.Value / 1000.0);
            }

            // Only streaming requests carry a first-token sample.
            double? timeToFirstTokenMs = null;
            if (row.TotalFirstTokenLatency > 0 && row.StreamingRequestCount > 0)
            {
                timeToFirstTokenMs = (double)row.TotalFirstTokenLatency
                    / row.StreamingRequestCount;
            }

            probesToCreate.Add(new ChannelProbeRecord
            {
                ChannelId = channel.Id,
                TotalRequestCount = row.TotalCount,
                SuccessRequestCount = row.SuccessCount,
                AvgTokensPerSecond = tokensPerSecond,
                AvgTimeToFirstTokenMs = timeToFirstTokenMs,
                Timestamp = alignedUnix,
            });
        }

        if (probesToCreate.Count == 0)
        {
            LogNoProbeData(logger, null);
            return;
        }

        try
        {
            await probes.CreateRangeAsync(probesToCreate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogCreateProbesFailed(logger, ex);
            return;
        }

        LogProbeCompleted(logger, probesToCreate.Count, alignedUnix, null);
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
        var timestamps = new List<long>((int)(((endUnix - startUnix) / step) + 1));
        for (var timestamp = startUnix; timestamp <= endUnix; timestamp += step)
        {
            timestamps.Add(timestamp);
        }

        return timestamps;
    }

    /// <summary>
    /// The effective probe setting for a <c>read_channels</c> principal: the
    /// stored value, or the default on a denied scope or any read failure
    /// (ChannelSettingOrDefault, system.go:1355-1370).
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

        var stored = await ChannelSettingOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return stored.Probe;
    }

    /// <summary>
    /// Port of SystemService.ChannelSettingOrDefault (system.go:1355-1370):
    /// every read/decode failure is logged and returns defaultChannelSetting.
    /// </summary>
    private async Task<SystemChannelSettings> ChannelSettingOrDefaultAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetChannelSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogChannelSettingFailed(logger, ex);
            return SystemSettingsDefaults.Channel;
        }
    }
}
