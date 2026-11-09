using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the <c>channelProbeData</c> service semantics
/// (internal/server/biz/channel_probe.go:345-465): the interval-aligned window,
/// the read_channels scope decision that selects stored vs default probe
/// settings, the zero-filled gaps, the input-order/duplicate projection and the
/// unwrapped store error.
/// </summary>
public sealed class ChannelProbeServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 10, 10, 7, 31, TimeSpan.Zero);

    [Fact]
    public async Task EmptyInputReturnsEmptyWithoutQuerying()
    {
        var (service, probes, _) = CreateService();

        var data = await service.BatchQueryChannelProbesAsync([]);

        Assert.Empty(data);
        Assert.Empty(probes.Queries);
    }

    [Fact]
    public async Task DefaultWindowSpansSixtyMinutesInFiveMinuteSteps()
    {
        var (service, probes, _) = CreateService();

        var data = await service.BatchQueryChannelProbesAsync([7L]);

        var channel = Assert.Single(data);
        Assert.Equal(7L, channel.ChannelId);
        Assert.Equal(13, channel.Points.Count);

        // end = truncate(10:07:31, 5m) = 10:05; start = end - 60m.
        var end = new DateTimeOffset(2026, 3, 10, 10, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
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
        var (service, probes, _) = CreateService(
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
        var (service, probes, _) = CreateService(
            owner: false,
            settingsJson: """{"probe":{"enabled":false,"frequency":"1h"}}""");

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        var end = new DateTimeOffset(2026, 3, 10, 10, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(13, Assert.Single(data).Points.Count);
        Assert.Equal(end - 3600, Assert.Single(probes.Queries).StartUnix);
        Assert.Equal(end, Assert.Single(probes.Queries).EndUnix);
    }

    [Fact]
    public async Task UnknownFrequencyUsesOneMinuteStepsOverTenMinutes()
    {
        var (service, probes, _) = CreateService(
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
        var (service, _, _) = CreateService(settingsJson: "{not-json");

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        Assert.Equal(13, Assert.Single(data).Points.Count);
    }

    [Fact]
    public async Task SettingsLookupFailuresFallBackToDefaults()
    {
        var (service, _, _) = CreateService(
            settingsFailure: new InvalidOperationException("boom"));

        var data = await service.BatchQueryChannelProbesAsync([1L]);

        Assert.Equal(13, Assert.Single(data).Points.Count);
    }

    [Fact]
    public async Task MapsRowsAndFillsMissingBuckets()
    {
        var end = new DateTimeOffset(2026, 3, 10, 10, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var start = end - 3600;
        var (service, _, _) = CreateService(
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
        var (service, _, _) = CreateService();

        var data = await service.BatchQueryChannelProbesAsync([5L, 3L, 5L]);

        Assert.Equal([5L, 3L, 5L], data.Select(static item => item.ChannelId));
    }

    [Fact]
    public async Task StoreErrorsPropagateUnwrapped()
    {
        var (service, probes, _) = CreateService();
        probes.Failure = new InvalidOperationException("probe query failed");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BatchQueryChannelProbesAsync([1L]));

        Assert.Equal("probe query failed", error.Message);
    }

    private static (ChannelProbeService Service, RecordingChannelProbeStore Probes, FakeSystemStore Systems)
        CreateService(
            bool owner = true,
            string? settingsJson = null,
            Exception? settingsFailure = null,
            IReadOnlyList<ChannelProbeRecord>? rows = null)
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

        return (
            new ChannelProbeService(
                probes,
                new SystemSettingsAdminService(systems),
                scope,
                new TestTimeProvider(Now)),
            probes,
            systems);
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

    public Exception? Failure { get; set; }

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
}
