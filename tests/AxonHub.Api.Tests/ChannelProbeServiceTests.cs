using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the <c>channelProbeData</c> service semantics
/// (internal/server/biz/channel_probe.go:345-465): the interval-aligned window,
/// the read_channels scope decision that selects stored vs default probe
/// settings, the zero-filled gaps, the input-order/duplicate projection and the
/// unwrapped store error. The writer half locks runProbe
/// (channel_probe.go:237-335): setting fallback, enabled gate, interval
/// alignment, the last-execution guard, the TPS/TTFT formulas and the
/// never-throw error policy.
/// </summary>
public sealed class ChannelProbeServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 10, 10, 7, 31, TimeSpan.Zero);

    private static readonly DateTimeOffset FiveMinuteEnd =
        new(2026, 3, 10, 10, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task EmptyInputReturnsEmptyWithoutQuerying()
    {
        var (service, probes, _, _, _) = CreateService();

        var data = await service.BatchQueryChannelProbesAsync([]);

        Assert.Empty(data);
        Assert.Empty(probes.Queries);
    }

    [Fact]
    public async Task DefaultWindowSpansSixtyMinutesInFiveMinuteSteps()
    {
        var (service, probes, _, _, _) = CreateService();

        var data = await service.BatchQueryChannelProbesAsync([7L]);

        var channel = Assert.Single(data);
        Assert.Equal(7L, channel.ChannelId);
        Assert.Equal(13, channel.Points.Count);

        // end = truncate(10:07:31, 5m) = 10:05; start = end - 60m.
        var end = FiveMinuteEnd.ToUnixTimeSeconds();
        var start = end - 3600;
        var query = Assert.Single(probes.Queries);
        Assert.Equal([7L], query.ChannelIds);
        Assert.Equal(start, query.StartUnix);
        Assert.Equal(end, query.EndUnix);

        Assert.Equal(start, channel.Points[0].Timestamp);
        Assert.Equal(end, channel.Points[^1].Timestamp);
        for (var i = 1; i < channel.Points.Count; i++)
        {
            Assert.Equal(300, channel.Points[i].Timestamp - channel.Points[i - 1].Timestamp);
        }

        Assert.All(channel.Points, point =>
        {
            Assert.Equal(0, point.TotalRequestCount);
            Assert.Equal(0, point.SuccessRequestCount);
            Assert.Null(point.AvgTokensPerSecond);
            Assert.Null(point.AvgTimeToFirstTokenMs);
        });
    }

    [Fact]
    public async Task StoredFrequencyDrivesTheWindowForReadChannelsCallers()
    {
        var (service, probes, _, _, _) = CreateService(
            settingsJson: """{"probe":{"enabled":false,"frequency":"1h"}}""");

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        // end = truncate(10:07:31, 60m) = 10:00; start = end - 1440m.
        var end = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var channel = Assert.Single(data);
        Assert.Equal(25, channel.Points.Count);
        Assert.Equal(end - 1440 * 60, channel.Points[0].Timestamp);
        Assert.Equal(end, channel.Points[^1].Timestamp);
        Assert.Equal(3600, channel.Points[1].Timestamp - channel.Points[0].Timestamp);
        Assert.Equal(end - 1440 * 60, Assert.Single(probes.Queries).StartUnix);
    }

    [Fact]
    public async Task CallersWithoutReadChannelsSeeTheDefaultWindow()
    {
        // The stored setting is ignored: the reference's scope decision denies
        // the ent read and ChannelSettingOrDefault falls back to the default.
        var (service, probes, _, _, _) = CreateService(
            owner: false,
            settingsJson: """{"probe":{"enabled":false,"frequency":"1h"}}""");

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        var end = FiveMinuteEnd.ToUnixTimeSeconds();
        Assert.Equal(13, Assert.Single(data).Points.Count);
        Assert.Equal(end - 3600, Assert.Single(probes.Queries).StartUnix);
        Assert.Equal(end, Assert.Single(probes.Queries).EndUnix);
    }

    [Fact]
    public async Task UnknownFrequencyUsesOneMinuteStepsOverTenMinutes()
    {
        var (service, probes, _, _, _) = CreateService(
            settingsJson: """{"probe":{"frequency":"2m"}}""");

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        var end = new DateTimeOffset(2026, 3, 10, 10, 7, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var channel = Assert.Single(data);
        Assert.Equal(11, channel.Points.Count);
        Assert.Equal(60, channel.Points[1].Timestamp - channel.Points[0].Timestamp);
        Assert.Equal(end - 600, Assert.Single(probes.Queries).StartUnix);
        Assert.Equal(end, Assert.Single(probes.Queries).EndUnix);
    }

    [Fact]
    public async Task StoredSettingsDecodeFailuresFallBackToDefaults()
    {
        var (service, _, _, _, _) = CreateService(settingsJson: "{not-json");

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        Assert.Equal(13, Assert.Single(data).Points.Count);
    }

    [Fact]
    public async Task SettingsLookupFailuresFallBackToDefaults()
    {
        var (service, _, _, _, _) = CreateService(
            settingsFailure: new InvalidOperationException("boom"));

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        Assert.Equal(13, Assert.Single(data).Points.Count);
    }

    [Fact]
    public async Task MapsRowsAndFillsMissingBuckets()
    {
        var end = FiveMinuteEnd.ToUnixTimeSeconds();
        var start = end - 3600;
        var (service, _, _, _, _) = CreateService(
            rows:
            [
                // Two rows on the same bucket: the later row wins.
                new ChannelProbeRecord
                {
                    ChannelId = 1,
                    Timestamp = start,
                    TotalRequestCount = 10,
                    SuccessRequestCount = 9,
                    AvgTokensPerSecond = 12.5,
                    AvgTimeToFirstTokenMs = 300,
                },
                new ChannelProbeRecord
                {
                    ChannelId = 1,
                    Timestamp = start,
                    TotalRequestCount = 20,
                    SuccessRequestCount = 19,
                    AvgTokensPerSecond = 20.5,
                    AvgTimeToFirstTokenMs = 400,
                },
                new ChannelProbeRecord
                {
                    ChannelId = 1,
                    Timestamp = end,
                    TotalRequestCount = 3,
                    SuccessRequestCount = 3,
                },
                // Outside the generated timestamps: ignored even if returned.
                new ChannelProbeRecord
                {
                    ChannelId = 1,
                    Timestamp = start - 300,
                    TotalRequestCount = 99,
                    SuccessRequestCount = 99,
                },
            ]);

        var channel = Assert.Single(await service.BatchQueryChannelProbesAsync([1L]));

        Assert.Equal(13, channel.Points.Count);
        var first = channel.Points[0];
        Assert.Equal(start, first.Timestamp);
        Assert.Equal(20, first.TotalRequestCount);
        Assert.Equal(19, first.SuccessRequestCount);
        Assert.Equal(20.5, first.AvgTokensPerSecond);
        Assert.Equal(400, first.AvgTimeToFirstTokenMs);

        var gap = channel.Points[1];
        Assert.Equal(0, gap.TotalRequestCount);
        Assert.Equal(0, gap.SuccessRequestCount);
        Assert.Null(gap.AvgTokensPerSecond);
        Assert.Null(gap.AvgTimeToFirstTokenMs);

        var last = channel.Points[^1];
        Assert.Equal(end, last.Timestamp);
        Assert.Equal(3, last.TotalRequestCount);
        Assert.Equal(3, last.SuccessRequestCount);
        Assert.Null(last.AvgTokensPerSecond);
        Assert.Null(last.AvgTimeToFirstTokenMs);
    }

    [Fact]
    public async Task DuplicateInputIdsRepeatTheSeriesInInputOrder()
    {
        var (service, _, _, _, _) = CreateService();

        var data = await service.BatchQueryChannelProbesAsync([5L, 3L, 5L]);

        Assert.Equal([5L, 3L, 5L], data.Select(static item => item.ChannelId));
    }

    [Fact]
    public async Task StoreErrorsPropagateUnwrapped()
    {
        var (service, probes, _, _, _) = CreateService();
        probes.Failure = new InvalidOperationException("probe query failed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BatchQueryChannelProbesAsync([1L]));

        Assert.Equal("probe query failed", error.Message);
    }

    // ---- runProbe (channel_probe.go:237-335) ----

    [Fact]
    public async Task DisabledProbeSkipsTheWholePass()
    {
        var (service, probes, _, channels, _) = CreateService(
            settingsJson: """{"probe":{"enabled":false,"frequency":"5m"}}""",
            enabledChannelIds: [1L]);

        await service.RunProbeAsync();

        Assert.Equal(0, channels.ListEnabledCalls);
        Assert.Empty(probes.ComputeQueries);
        Assert.Empty(probes.CreatedBatches);
    }

    [Fact]
    public async Task FailedSettingsReadFallsBackToTheEnabledDefault()
    {
        // defaultChannelSetting has probe.enabled = true and 5m
        // (system_default.go:101-111); a corrupt row therefore still probes.
        var (service, probes, _, _, _) = CreateService(
            settingsFailure: new InvalidOperationException("boom"),
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);

        await service.RunProbeAsync();

        var batch = Assert.Single(probes.CreatedBatches);
        Assert.Equal(FiveMinuteEnd.ToUnixTimeSeconds(), Assert.Single(batch).Timestamp);
    }

    [Fact]
    public async Task WritesOneAlignedBucketPerChannelWithTheProbeFormulas()
    {
        // now 10:07:31 aligns to 10:05:00 at the 5m interval; the window is
        // [10:00:00, 10:05:00).
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L, 2L],
            stats:
            [
                Stats(
                    1,
                    total: 10,
                    success: 9,
                    tokens: 1000,
                    effectiveLatencyMs: 2000,
                    ttft: 500,
                    requestCount: 10,
                    streaming: 5),
                Stats(
                    2,
                    total: 3,
                    success: 2,
                    tokens: 0,
                    effectiveLatencyMs: 0,
                    ttft: 0,
                    requestCount: 3,
                    streaming: 0),
            ]);

        await service.RunProbeAsync();

        var query = Assert.Single(probes.ComputeQueries);
        Assert.Equal([1L, 2L], query.ChannelIds);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc), query.StartUtc);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 5, 0, DateTimeKind.Utc), query.EndUtc);

        var batch = Assert.Single(probes.CreatedBatches);
        Assert.Equal([1L, 2L], batch.Select(static row => row.ChannelId));

        var first = batch[0];
        Assert.Equal(FiveMinuteEnd.ToUnixTimeSeconds(), first.Timestamp);
        Assert.Equal(10, first.TotalRequestCount);
        Assert.Equal(9, first.SuccessRequestCount);
        // 1000 tokens over 2s of post-first-token generation.
        Assert.Equal(500, first.AvgTokensPerSecond);
        // 500ms of first-token latency over 5 streaming requests.
        Assert.Equal(100, first.AvgTimeToFirstTokenMs);

        var second = batch[1];
        Assert.Equal(3, second.TotalRequestCount);
        Assert.Equal(2, second.SuccessRequestCount);
        // No tokens/latency samples: both averages stay NULL.
        Assert.Null(second.AvgTokensPerSecond);
        Assert.Null(second.AvgTimeToFirstTokenMs);
    }

    [Fact]
    public async Task ChannelsWithoutAttemptsAreNotWritten()
    {
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L, 2L],
            stats: [Stats(1, total: 0, success: 0, requestCount: 0)]);

        await service.RunProbeAsync();

        Assert.Empty(probes.CreatedBatches);
    }

    [Fact]
    public async Task UnknownFrequencyAlignsToOneMinute()
    {
        var (service, probes, _, _, _) = CreateService(
            settingsJson: """{"probe":{"enabled":true,"frequency":"2m"}}""",
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);

        await service.RunProbeAsync();

        var query = Assert.Single(probes.ComputeQueries);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 6, 0, DateTimeKind.Utc), query.StartUtc);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 7, 0, DateTimeKind.Utc), query.EndUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 10, 10, 7, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            Assert.Single(Assert.Single(probes.CreatedBatches)).Timestamp);
    }

    [Fact]
    public async Task ASecondRunInTheSameIntervalIsSkipped()
    {
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);

        await service.RunProbeAsync();
        await service.RunProbeAsync();

        Assert.Single(probes.ComputeQueries);
        Assert.Single(probes.CreatedBatches);
    }

    [Fact]
    public async Task AFailedPassStillConsumesTheInterval()
    {
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);
        probes.ComputeFailure = new InvalidOperationException("stats failed");

        await service.RunProbeAsync();

        // The reference updates lastExecutionTime before the database work, so
        // the retry inside the same interval is skipped even after a failure.
        probes.ComputeFailure = null;
        await service.RunProbeAsync();

        Assert.Single(probes.ComputeQueries);
        Assert.Empty(probes.CreatedBatches);
    }

    [Fact]
    public async Task ANullEffectiveLatencyAbortsTheWholePass()
    {
        // database/sql cannot Scan NULL into int64: the reference aborts before
        // creating any probe row.
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L, 2L],
            stats:
            [
                Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000),
                Stats(2, total: 1, success: 1, tokens: 100, effectiveLatencyMs: null),
            ]);

        await service.RunProbeAsync();

        Assert.Empty(probes.CreatedBatches);
    }

    [Fact]
    public async Task ChannelLookupFailuresAreLoggedAndNeverThrown()
    {
        var (service, probes, _, channels, _) = CreateService(
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);
        channels.ListEnabledFailure = new InvalidOperationException("channels failed");

        await service.RunProbeAsync();

        Assert.Equal(1, channels.ListEnabledCalls);
        Assert.Empty(probes.ComputeQueries);
    }

    [Fact]
    public async Task StatsFailuresAreLoggedAndNeverThrown()
    {
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);
        probes.ComputeFailure = new InvalidOperationException("stats failed");

        await service.RunProbeAsync();

        Assert.Single(probes.ComputeQueries);
        Assert.Empty(probes.CreatedBatches);
    }

    [Fact]
    public async Task InsertFailuresAreLoggedAndNeverThrown()
    {
        var (service, probes, _, _, _) = CreateService(
            enabledChannelIds: [1L],
            stats: [Stats(1, total: 1, success: 1, tokens: 100, effectiveLatencyMs: 1000)]);
        probes.CreateFailure = new InvalidOperationException("insert failed");

        await service.RunProbeAsync();

        Assert.Single(probes.CreatedBatches);
    }

    [Fact]
    public async Task NoEnabledChannelsSkipsTheStatsQuery()
    {
        var (service, probes, _, _, _) = CreateService();

        await service.RunProbeAsync();

        Assert.Empty(probes.ComputeQueries);
        Assert.Empty(probes.CreatedBatches);
    }

    private static ChannelProbeStatsRow Stats(
        long channelId,
        long total,
        long success,
        long tokens = 0,
        long? effectiveLatencyMs = 0,
        long ttft = 0,
        long requestCount = 0,
        long streaming = 0)
        => new()
        {
            ChannelId = channelId,
            TotalCount = total,
            SuccessCount = success,
            TotalTokens = tokens,
            EffectiveLatencyMs = effectiveLatencyMs,
            TotalFirstTokenLatency = ttft,
            RequestCount = requestCount,
            StreamingRequestCount = streaming,
        };

    private static (
        ChannelProbeService Service,
        RecordingChannelProbeStore Probes,
        FakeSystemStore Systems,
        FakeChannelStore Channels,
        ChannelProbeRunState State)
        CreateService(
            bool owner = true,
            string? settingsJson = null,
            Exception? settingsFailure = null,
            IReadOnlyList<ChannelProbeRecord>? rows = null,
            IReadOnlyList<ChannelProbeStatsRow>? stats = null,
            IReadOnlyList<long>? enabledChannelIds = null)
    {
        var systems = new FakeSystemStore();
        if (settingsJson is not null)
        {
            systems.Add(SystemChannelSettingsService.ChannelSettingsKey, settingsJson);
        }

        if (settingsFailure is not null)
        {
            systems.GetFailure = settingsFailure;
        }

        var users = new FakeUserAdminStore();
        users.CreateAsync(
                new UserRecord
                {
                    Id = 1,
                    Email = "probe-user@example.com",
                    Status = "activated",
                    IsOwner = owner,
                    Scopes = "[]",
                },
                roleIds: null)
            .GetAwaiter()
            .GetResult();

        var identity = new FakeChannelProbeIdentity(new ManagementUser(1, owner));
        var scope = new AuthorizationScope(identity, users);
        var probes = new RecordingChannelProbeStore();
        if (rows is not null)
        {
            probes.Rows.AddRange(rows);
        }

        if (stats is not null)
        {
            probes.Stats.AddRange(stats);
        }

        var channels = new FakeChannelStore();
        foreach (var id in enabledChannelIds ?? [])
        {
            channels.Add(new ChannelRecord
            {
                Id = id,
                Status = "enabled",
            });
        }

        var state = new ChannelProbeRunState();
        return (
            new ChannelProbeService(
                probes,
                channels,
                new SystemSettingsAdminService(systems),
                scope,
                state,
                new TestTimeProvider(Now),
                NullLogger<ChannelProbeService>.Instance),
            probes,
            systems,
            channels,
            state);
    }
}

internal sealed class FakeChannelProbeIdentity(ManagementUser? user) : IManagementIdentity
{
    public ManagementUser? User { get; set; } = user;

    public ApiKeyRecord? ApiKey { get; set; }

    public long? ProjectId { get; set; }

    public Task<ManagementUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(User);
    }

    public ApiKeyRecord? GetCurrentApiKey() => ApiKey;

    public long? GetCurrentProjectId() => ProjectId;
}

internal sealed class RecordingChannelProbeStore : IChannelProbeStore
{
    public List<(IReadOnlyList<long> ChannelIds, long StartUnix, long EndUnix)> Queries { get; } = [];

    public List<ChannelProbeRecord> Rows { get; } = [];

    public List<(IReadOnlyList<long> ChannelIds, DateTime StartUtc, DateTime EndUtc)> ComputeQueries { get; } = [];

    public List<ChannelProbeStatsRow> Stats { get; } = [];

    public List<IReadOnlyList<ChannelProbeRecord>> CreatedBatches { get; } = [];

    public Exception? Failure { get; set; }

    public Exception? ComputeFailure { get; set; }

    public Exception? CreateFailure { get; set; }

    public Task<ChannelProbeRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.FirstOrDefault(row => row.Id == id));
    }

    public Task<IReadOnlyList<ChannelProbeRecord>> QueryAsync(
        IReadOnlyList<long> channelIds,
        long startUnix,
        long endUnix,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add((channelIds, startUnix, endUnix));
        if (Failure is not null)
        {
            return Task.FromException<IReadOnlyList<ChannelProbeRecord>>(Failure);
        }

        return Task.FromResult<IReadOnlyList<ChannelProbeRecord>>(Rows);
    }

    public Task<IReadOnlyList<ChannelProbeStatsRow>> ComputeStatsAsync(
        IReadOnlyList<long> channelIds,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ComputeQueries.Add((channelIds, startUtc, endUtc));
        if (ComputeFailure is not null)
        {
            return Task.FromException<IReadOnlyList<ChannelProbeStatsRow>>(ComputeFailure);
        }

        return Task.FromResult<IReadOnlyList<ChannelProbeStatsRow>>(Stats);
    }

    public Task CreateRangeAsync(
        IReadOnlyList<ChannelProbeRecord> probes,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        CreatedBatches.Add(probes);
        return CreateFailure is null
            ? Task.CompletedTask
            : Task.FromException(CreateFailure);
    }
}
