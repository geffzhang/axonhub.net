using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the dashboard GraphQL surface over /admin/graphql
/// (internal/server/gql/dashboard.graphql, dashboard.resolvers.go,
/// dashboard_helpers.go, dashboard_usage_aggregates.go and
/// channel_performance_helpers.go): the calendar windows, the shared usage
/// aggregate cache, the two cache TTLs, the confidence ranking, the tolerant
/// resolvers, the owner-only usage-by-user projection and the API key
/// validation messages.
/// </summary>
public sealed class AdminDashboardEndpointTests : IAsyncLifetime
{
    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeDashboardStore _dashboard = new();
    private readonly FakeDashboardIdentity _identity = new();
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(
        2026,
        3,
        10,
        0,
        0,
        0,
        TimeSpan.Zero));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<IDashboardStore>(_dashboard);
        builder.Services.AddSingleton<DashboardCache>();
        builder.Services.AddSingleton<DashboardTokenStatsCache>();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddScoped<IManagementIdentity>(_ => _identity);
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddScoped<DashboardService>();

        // The gate user (id 9) is the JWT every fixture client sends; the fake
        // identity mirrors it so the service-level principal is controllable.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);
        _identity.User = new ManagementUser(AdminGraphqlGateServices.UserId, IsOwner: true);

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task DashboardOverviewAggregatesStatusCountsAndToleratesDenial()
    {
        _dashboard.StatusCounts.AddRange(
        [
            new DashboardStatusCountRow { Status = "success", Count = 10 },
            new DashboardStatusCountRow { Status = "failed", Count = 2 },
            new DashboardStatusCountRow { Status = "processing", Count = 1 },
        ]);
        _dashboard.SinceCounts[new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc)] = 7;
        _dashboard.SinceCounts[new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc)] = 9;
        _dashboard.SinceCounts[new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)] = 11;
        _dashboard.BetweenCount = 4;

        var data = await QueryDataAsync(
            """
            query {
              dashboardOverview {
                totalRequests
                failedRequests
                averageResponseTime
                requestStats {
                  requestsToday
                  requestsThisWeek
                  requestsLastWeek
                  requestsThisMonth
                }
              }
            }
            """);

        var overview = data.GetProperty("dashboardOverview");
        Assert.Equal(13, overview.GetProperty("totalRequests").GetInt32());
        Assert.Equal(2, overview.GetProperty("failedRequests").GetInt32());
        Assert.Equal(
            JsonValueKind.Null,
            overview.GetProperty("averageResponseTime").ValueKind);

        var stats = overview.GetProperty("requestStats");
        Assert.Equal(7, stats.GetProperty("requestsToday").GetInt32());
        Assert.Equal(9, stats.GetProperty("requestsThisWeek").GetInt32());
        Assert.Equal(4, stats.GetProperty("requestsLastWeek").GetInt32());
        Assert.Equal(11, stats.GetProperty("requestsThisMonth").GetInt32());

        // A member without read_dashboard sees the tolerant zero result and
        // the resolver never touches the store.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 11,
                Email = "plain@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _identity.User = new ManagementUser(11, IsOwner: false);
        var statusCalls = _dashboard.StatusCountCalls;
        var countCalls = _dashboard.CountCalls;

        var denied = await QueryDataAsync(
            """
            query {
              dashboardOverview { totalRequests failedRequests requestStats { requestsToday } }
            }
            """);

        var deniedOverview = denied.GetProperty("dashboardOverview");
        Assert.Equal(0, deniedOverview.GetProperty("totalRequests").GetInt32());
        Assert.Equal(0, deniedOverview.GetProperty("failedRequests").GetInt32());
        Assert.Equal(
            0,
            deniedOverview.GetProperty("requestStats").GetProperty("requestsToday").GetInt32());
        Assert.Equal(statusCalls, _dashboard.StatusCountCalls);
        Assert.Equal(countCalls, _dashboard.CountCalls);
    }

    [Fact]
    public async Task TokenStatsReportsAllTimeFailureAndKeepsTheWindowSums()
    {
        _dashboard.WindowTokenSums = new DashboardTokenSumRow
        {
            InputTokens = 5,
            OutputTokens = 6,
            CachedTokens = 7,
            RowCount = 1,
        };
        // usage_logs is empty: the reference scans SQL NULL into int and fails.
        _dashboard.AllTimeTokenSums = new DashboardTokenSumRow { RowCount = 0 };

        var root = await ExecuteAsync(
            """
            query {
              tokenStats {
                totalInputTokensToday
                totalOutputTokensToday
                totalCachedTokensToday
                totalInputTokensAllTime
                totalLastUpdated: lastUpdated
              }
            }
            """,
            null);

        var data = root.GetProperty("data").GetProperty("tokenStats");
        Assert.Equal(5, data.GetProperty("totalInputTokensToday").GetInt32());
        Assert.Equal(6, data.GetProperty("totalOutputTokensToday").GetInt32());
        Assert.Equal(7, data.GetProperty("totalCachedTokensToday").GetInt32());
        Assert.Equal(0, data.GetProperty("totalInputTokensAllTime").GetInt32());
        Assert.Equal(
            JsonValueKind.Null,
            data.GetProperty("totalLastUpdated").ValueKind);

        var error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "Failed to aggregate all-time token statistics",
            error.GetProperty("message").GetString());
        Assert.Equal(
            "TOKEN_STATS_ALL_TIME_AGG_FAILED",
            error.GetProperty("extensions").GetProperty("code").GetString());

        // Failures are not cached: the next request retries the aggregation.
        var retried = await ExecuteAsync(
            """
            query { tokenStats { totalInputTokensAllTime } }
            """,
            null);
        Assert.True(retried.TryGetProperty("errors", out _));
        Assert.Equal(2, _dashboard.AllTimeTokenSumQueries);
    }

    [Fact]
    public async Task TokenStatsServesTheAllTimeSnapshotUntilTheHardTtl()
    {
        _dashboard.AllTimeTokenSums = new DashboardTokenSumRow
        {
            InputTokens = 100,
            OutputTokens = 200,
            CachedTokens = 50,
            RowCount = 1,
        };

        var first = await QueryDataAsync(
            """
            query { tokenStats { totalInputTokensAllTime totalOutputTokensAllTime totalCachedTokensAllTime lastUpdated } }
            """);
        var stats = first.GetProperty("tokenStats");
        Assert.Equal(100, stats.GetProperty("totalInputTokensAllTime").GetInt32());
        Assert.Equal(200, stats.GetProperty("totalOutputTokensAllTime").GetInt32());
        Assert.Equal(50, stats.GetProperty("totalCachedTokensAllTime").GetInt32());
        Assert.Equal("2026-03-10T00:00:00Z", stats.GetProperty("lastUpdated").GetString());
        Assert.Equal(1, _dashboard.AllTimeTokenSumQueries);

        // A new value in the store is invisible while the snapshot is alive.
        _dashboard.AllTimeTokenSums = new DashboardTokenSumRow
        {
            InputTokens = 999,
            OutputTokens = 888,
            CachedTokens = 777,
            RowCount = 1,
        };
        _clock.Advance(TimeSpan.FromMinutes(30));

        var cached = await QueryDataAsync(
            """
            query { tokenStats { totalInputTokensAllTime lastUpdated } }
            """);
        Assert.Equal(
            100,
            cached.GetProperty("tokenStats").GetProperty("totalInputTokensAllTime").GetInt32());
        Assert.Equal(
            "2026-03-10T00:00:00Z",
            cached.GetProperty("tokenStats").GetProperty("lastUpdated").GetString());
        Assert.Equal(1, _dashboard.AllTimeTokenSumQueries);

        // Past the hard TTL the resolver refreshes synchronously.
        _clock.Advance(TimeSpan.FromHours(25));

        var refreshed = await QueryDataAsync(
            """
            query { tokenStats { totalInputTokensAllTime lastUpdated } }
            """);
        Assert.Equal(
            999,
            refreshed.GetProperty("tokenStats").GetProperty("totalInputTokensAllTime").GetInt32());
        Assert.Equal(
            "2026-03-11T01:30:00Z",
            refreshed.GetProperty("tokenStats").GetProperty("lastUpdated").GetString());
        Assert.Equal(2, _dashboard.AllTimeTokenSumQueries);
    }

    [Fact]
    public async Task ChannelSuccessRatesNormalizeTheWindowLimitAndDetails()
    {
        _dashboard.ChannelExecutions.AddRange(
        [
            new DashboardChannelExecutionRow
            {
                ChannelId = 1,
                SuccessCount = 8,
                FailedCount = 2,
            },
            new DashboardChannelExecutionRow
            {
                ChannelId = 2,
                SuccessCount = 5,
                FailedCount = 4,
            },
            new DashboardChannelExecutionRow
            {
                ChannelId = 3,
                SuccessCount = 1,
                FailedCount = 0,
            },
        ]);
        _dashboard.Channels[1] = new DashboardChannelRow
        {
            Id = 1,
            Name = "primary",
            Type = "openai",
            Status = "enabled",
        };
        _dashboard.Channels[2] = new DashboardChannelRow
        {
            Id = 2,
            Name = "secondary",
            Type = "anthropic",
            Status = "disabled",
        };
        _dashboard.Channels[3] = new DashboardChannelRow
        {
            Id = 3,
            Name = "archived",
            Type = "openai",
            Status = "disabled",
        };
        _dashboard.SoftDeletedChannelIds.Add(3);

        var data = await QueryDataAsync(
            """
            query Rates($timeWindow: String, $limit: Int) {
              channelSuccessRates(timeWindow: $timeWindow, limit: $limit) {
                channelId
                channelName
                channelType
                channelDisabled
                successCount
                failedCount
                totalCount
                successRate
              }
            }
            """,
            new { timeWindow = "week", limit = 1 });

        var rates = data.GetProperty("channelSuccessRates").EnumerateArray().ToArray();
        var top = Assert.Single(rates);
        Assert.Equal("gid://axonhub/Channel/1", top.GetProperty("channelId").GetString());
        Assert.Equal("primary", top.GetProperty("channelName").GetString());
        Assert.Equal("openai", top.GetProperty("channelType").GetString());
        Assert.False(top.GetProperty("channelDisabled").GetBoolean());
        Assert.Equal(8, top.GetProperty("successCount").GetInt32());
        Assert.Equal(2, top.GetProperty("failedCount").GetInt32());
        Assert.Equal(10, top.GetProperty("totalCount").GetInt32());
        Assert.Equal(80, top.GetProperty("successRate").GetDouble());

        // The week window starts on Monday; soft-deleted channels are still
        // resolved for the details, and the limit is part of the cache key.
        var window = Assert.Single(_dashboard.ChannelExecutionWindows);
        Assert.Equal(new DateTime(2026, 3, 9, 0, 0, 0, DateTimeKind.Utc), window!.Value);
        var details = Assert.Single(_dashboard.ChannelQueries);
        Assert.True(details.IncludeDeleted);
        Assert.Equal(new long[] { 1 }, details.Ids);

        // An unknown window is canonicalized to all-time (no filter), and
        // limit 0 means unlimited.
        var all = await QueryDataAsync(
            """
            query Rates($timeWindow: String) {
              channelSuccessRates(timeWindow: $timeWindow) {
                channelId
                channelName
                channelDisabled
                totalCount
              }
            }
            """,
            new { timeWindow = " week " });

        var allRates = all.GetProperty("channelSuccessRates").EnumerateArray().ToArray();
        Assert.Equal(3, allRates.Length);
        Assert.Null(_dashboard.ChannelExecutionWindows[^1]);
        Assert.Equal(
            new[] { "gid://axonhub/Channel/1", "gid://axonhub/Channel/2", "gid://axonhub/Channel/3" },
            allRates.Select(static item => item.GetProperty("channelId").GetString()).ToArray());
        Assert.Equal("archived", allRates[2].GetProperty("channelName").GetString());
        Assert.True(allRates[2].GetProperty("channelDisabled").GetBoolean());
    }

    [Fact]
    public async Task FastestChannelsRankByConfidenceAndOversampleTheSqlLimit()
    {
        _dashboard.FastestChannels.AddRange(
        [
            FastestChannel(1, "one", "openai", requestCount: 600, throughput: 10),
            FastestChannel(2, "two", "anthropic", requestCount: 400, throughput: 20),
            FastestChannel(3, "three", "openai", requestCount: 10, throughput: 30),
        ]);

        // The omitted limit defaults to 5 and the SQL limit is max(5*4, 20).
        var data = await QueryDataAsync(
            """
            query Fastest($input: FastestChannelsInput!) {
              fastestChannels(input: $input) {
                channelId
                channelName
                confidenceLevel
                requestCount
              }
            }
            """,
            new { input = new { timeWindow = "day" } });

        var channels = data.GetProperty("fastestChannels").EnumerateArray().ToArray();
        Assert.Equal(3, channels.Length);
        Assert.Equal("high", channels[0].GetProperty("confidenceLevel").GetString());
        Assert.Equal(600, channels[0].GetProperty("requestCount").GetInt32());
        Assert.Equal("medium", channels[1].GetProperty("confidenceLevel").GetString());
        Assert.Equal("low", channels[2].GetProperty("confidenceLevel").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/1",
            channels[0].GetProperty("channelId").GetString());

        var first = Assert.Single(_dashboard.FastestChannelQueries);
        Assert.Equal(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), first.Since);
        Assert.Equal(20, first.Limit);

        // limit 2 keeps only the high/medium performers and the month window.
        var filtered = await QueryDataAsync(
            """
            query Fastest($input: FastestChannelsInput!) {
              fastestChannels(input: $input) { channelId }
            }
            """,
            new { input = new { timeWindow = "month", limit = 2 } });

        var kept = filtered.GetProperty("fastestChannels").EnumerateArray().ToArray();
        Assert.Equal(2, kept.Length);
        Assert.Equal("gid://axonhub/Channel/1", kept[0].GetProperty("channelId").GetString());
        Assert.Equal("gid://axonhub/Channel/2", kept[1].GetProperty("channelId").GetString());
        Assert.Equal(
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            _dashboard.FastestChannelQueries[^1].Since);
    }

    [Fact]
    public async Task UsageAggregateWindowsAreCanonicalizedAndSharedByWidgets()
    {
        _dashboard.Aggregates[DashboardUsageDimension.Channel] =
        [
            new DashboardUsageAggregateRow
            {
                Id = 2,
                Name = "b",
                RequestCount = 5,
                InputTokens = 100,
                OutputTokens = 0,
                ReasoningTokens = 4,
                TotalCost = 0.2,
            },
            new DashboardUsageAggregateRow
            {
                Id = 1,
                Name = "a",
                RequestCount = 5,
                InputTokens = 10,
                OutputTokens = 20,
                ReasoningTokens = 3,
                TotalCost = 0.9,
            },
            new DashboardUsageAggregateRow
            {
                Id = 3,
                Name = "c",
                RequestCount = 9,
                InputTokens = 5,
                OutputTokens = 5,
                ReasoningTokens = 1,
                TotalCost = 0.1,
            },
        ];
        _dashboard.Aggregates[DashboardUsageDimension.ApiKey] =
        [
            new DashboardUsageAggregateRow
            {
                Id = 7,
                Name = "key-7",
                RequestCount = 3,
                TotalCost = 0.4,
            },
        ];

        var requests = await QueryDataAsync(
            """
            query Stats($timeWindow: String) {
              requestStatsByChannel(timeWindow: $timeWindow) { channelName count }
            }
            """,
            new { timeWindow = " day " });

        var byChannel = requests.GetProperty("requestStatsByChannel")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(
            new[] { "c", "a", "b" },
            byChannel.Select(static item => item.GetProperty("channelName").GetString()).ToArray());
        Assert.Equal(
            new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            Assert.Single(_dashboard.UsageAggregateQueries).Since!.Value);

        // The canonical window, not the raw spelling, keys the cache; the token
        // widget shares the same grouped scan.
        var tokens = await QueryDataAsync(
            """
            query Stats($timeWindow: String) {
              tokenStatsByChannel(timeWindow: $timeWindow) {
                channelId
                channelName
                inputTokens
                outputTokens
                reasoningTokens
                totalTokens
              }
            }
            """,
            new { timeWindow = "day" });

        var byTokens = tokens.GetProperty("tokenStatsByChannel").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "b", "a", "c" },
            byTokens.Select(static item => item.GetProperty("channelName").GetString()).ToArray());
        Assert.Equal(100, byTokens[0].GetProperty("totalTokens").GetInt32());
        Assert.Equal(4, byTokens[0].GetProperty("reasoningTokens").GetInt32());
        Assert.Equal(
            "gid://axonhub/Channel/2",
            byTokens[0].GetProperty("channelId").GetString());

        Assert.Single(_dashboard.UsageAggregateQueries);

        var costs = await QueryDataAsync(
            """
            query { costStatsByAPIKey { apiKeyId apiKeyName cost } }
            """);

        var byCost = Assert.Single(
            costs.GetProperty("costStatsByAPIKey").EnumerateArray());
        Assert.Equal("gid://axonhub/APIKey/7", byCost.GetProperty("apiKeyId").GetString());
        Assert.Equal("key-7", byCost.GetProperty("apiKeyName").GetString());
        Assert.Equal(0.4, byCost.GetProperty("cost").GetDouble());
        Assert.Null(_dashboard.UsageAggregateQueries[^1].Since);
    }

    [Fact]
    public async Task DailyRequestStatsFillTheLastThirtyLocalDays()
    {
        _gate.Systems.Add(
            SystemTimeLocations.GeneralSettingsKey,
            """{"timezone":"Asia/Tokyo"}""");
        _dashboard.DailyRows.Add(new DashboardDailyRequestRow
        {
            Date = "2026-03-10",
            TotalCount = 3,
            TotalTokens = 120,
            TotalCost = 1.25,
        });

        var data = await QueryDataAsync(
            """
            query { dailyRequestStats { date count tokens cost } }
            """);

        var daily = data.GetProperty("dailyRequestStats").EnumerateArray().ToArray();
        Assert.Equal(30, daily.Length);
        Assert.Equal("2026-02-09", daily[0].GetProperty("date").GetString());
        Assert.Equal(0, daily[0].GetProperty("count").GetInt32());
        Assert.Equal("2026-03-10", daily[^1].GetProperty("date").GetString());
        Assert.Equal(3, daily[^1].GetProperty("count").GetInt32());
        Assert.Equal(120, daily[^1].GetProperty("tokens").GetInt32());
        Assert.Equal(1.25, daily[^1].GetProperty("cost").GetDouble());

        var query = Assert.Single(_dashboard.DailyQueries);
        Assert.Equal("Asia/Tokyo", query.TimeZone);
        Assert.Equal(new DateTime(2026, 2, 8, 15, 0, 0, DateTimeKind.Utc), query.StartUtc);
        Assert.Equal(new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc), query.EndUtc);
    }

    [Fact]
    public async Task ModelPerformanceStatsRankModelsByRequestVolume()
    {
        _dashboard.ModelPerformanceRows.AddRange(
        [
            new DashboardModelPerformanceRow
            {
                Date = "2026-03-09",
                ModelId = "model-a",
                Throughput = 10,
                FirstTokenMs = 0,
                RequestCount = 400,
            },
            new DashboardModelPerformanceRow
            {
                Date = "2026-03-09",
                ModelId = "model-b",
                Throughput = 20,
                FirstTokenMs = 50,
                RequestCount = 600,
            },
            new DashboardModelPerformanceRow
            {
                Date = "2026-03-08",
                ModelId = "model-a",
                Throughput = 30,
                FirstTokenMs = 100,
                RequestCount = 400,
            },
        ]);

        var data = await QueryDataAsync(
            """
            query {
              modelPerformanceStats { date modelId throughput ttftMs requestCount }
            }
            """);

        var stats = data.GetProperty("modelPerformanceStats").EnumerateArray().ToArray();
        Assert.Equal(3, stats.Length);
        Assert.Equal("model-a", stats[0].GetProperty("modelId").GetString());
        Assert.Equal("2026-03-09", stats[0].GetProperty("date").GetString());
        Assert.Equal(JsonValueKind.Null, stats[0].GetProperty("ttftMs").ValueKind);
        Assert.Equal(10, stats[0].GetProperty("throughput").GetDouble());
        Assert.Equal("2026-03-08", stats[1].GetProperty("date").GetString());
        Assert.Equal(100, stats[1].GetProperty("ttftMs").GetDouble());
        Assert.Equal("model-b", stats[2].GetProperty("modelId").GetString());

        var query = Assert.Single(_dashboard.ModelPerformanceQueries);
        Assert.Equal("UTC", query.TimeZone);
        Assert.Equal(new DateTime(2026, 2, 9, 0, 0, 0, DateTimeKind.Utc), query.StartUtc);
    }

    [Fact]
    public async Task ChannelPerformanceStatsFilterToTopChannelsAndEmitAscendingIds()
    {
        _dashboard.ChannelPerformanceRows.AddRange(
        [
            new DashboardChannelPerformanceRow
            {
                Date = "2026-03-10",
                ChannelId = 2,
                Throughput = 0,
                FirstTokenMs = 0,
                RequestCount = 600,
            },
            new DashboardChannelPerformanceRow
            {
                Date = "2026-03-10",
                ChannelId = 1,
                Throughput = 50,
                FirstTokenMs = 100,
                RequestCount = 300,
            },
            new DashboardChannelPerformanceRow
            {
                Date = "2026-03-09",
                ChannelId = 1,
                Throughput = 20,
                FirstTokenMs = 30,
                RequestCount = 100,
            },
        ]);
        _dashboard.Channels[1] = new DashboardChannelRow
        {
            Id = 1,
            Name = "One",
            Type = "openai",
            Status = "enabled",
        };

        var data = await QueryDataAsync(
            """
            query {
              channelPerformanceStats {
                date
                channelId
                channelName
                throughput
                ttftMs
                requestCount
              }
            }
            """);

        var stats = data.GetProperty("channelPerformanceStats").EnumerateArray().ToArray();
        Assert.Equal(3, stats.Length);
        Assert.Equal("2026-03-09", stats[0].GetProperty("date").GetString());
        Assert.Equal("1", stats[0].GetProperty("channelId").GetString());
        Assert.Equal("One", stats[0].GetProperty("channelName").GetString());
        Assert.Equal("2026-03-10", stats[1].GetProperty("date").GetString());
        Assert.Equal("1", stats[1].GetProperty("channelId").GetString());
        Assert.Equal(50, stats[1].GetProperty("throughput").GetDouble());
        Assert.Equal(100, stats[1].GetProperty("ttftMs").GetDouble());
        Assert.Equal("2", stats[2].GetProperty("channelId").GetString());
        // A missing channel falls back to the id placeholder; zero throughput
        // and ttft are reported as null.
        Assert.Equal("channel-2", stats[2].GetProperty("channelName").GetString());
        Assert.Equal(JsonValueKind.Null, stats[2].GetProperty("throughput").ValueKind);
        Assert.Equal(JsonValueKind.Null, stats[2].GetProperty("ttftMs").ValueKind);

        var query = Assert.Single(_dashboard.ChannelQueries);
        Assert.False(query.IncludeDeleted);
        Assert.Equal(new long[] { 1, 2 }, query.Ids);

        // Probe rows were empty, so the execution fallback ran exactly once.
        Assert.Single(_dashboard.ChannelPerformanceQueries);
        Assert.Single(_dashboard.ChannelPerformanceProbeQueries);
    }

    [Fact]
    public async Task ChannelPerformanceStatsPreferProbeBuckets()
    {
        _dashboard.ChannelPerformanceProbeRows.AddRange(
        [
            new DashboardChannelPerformanceRow
            {
                Date = "2026-03-10",
                ChannelId = 1,
                Throughput = 42.5,
                FirstTokenMs = 210,
                RequestCount = 120,
            },
            new DashboardChannelPerformanceRow
            {
                Date = "2026-03-09",
                ChannelId = 2,
                Throughput = 0,
                FirstTokenMs = 0,
                RequestCount = 40,
            },
        ]);
        // These would surface only if the execution fallback ran.
        _dashboard.ChannelPerformanceRows.Add(new DashboardChannelPerformanceRow
        {
            Date = "2026-03-10",
            ChannelId = 9,
            Throughput = 1,
            FirstTokenMs = 1,
            RequestCount = 1,
        });
        _dashboard.Channels[1] = new DashboardChannelRow
        {
            Id = 1,
            Name = "One",
            Type = "openai",
            Status = "enabled",
        };

        var data = await QueryDataAsync(
            """
            query {
              channelPerformanceStats {
                date
                channelId
                channelName
                throughput
                ttftMs
                requestCount
              }
            }
            """);

        var stats = data.GetProperty("channelPerformanceStats").EnumerateArray().ToArray();
        Assert.Equal(2, stats.Length);
        Assert.Equal("2026-03-09", stats[0].GetProperty("date").GetString());
        Assert.Equal("2", stats[0].GetProperty("channelId").GetString());
        Assert.Equal("channel-2", stats[0].GetProperty("channelName").GetString());
        Assert.Equal(JsonValueKind.Null, stats[0].GetProperty("throughput").ValueKind);
        Assert.Equal(JsonValueKind.Null, stats[0].GetProperty("ttftMs").ValueKind);
        Assert.Equal(40, stats[0].GetProperty("requestCount").GetInt32());

        Assert.Equal("2026-03-10", stats[1].GetProperty("date").GetString());
        Assert.Equal("1", stats[1].GetProperty("channelId").GetString());
        Assert.Equal("One", stats[1].GetProperty("channelName").GetString());
        Assert.Equal(42.5, stats[1].GetProperty("throughput").GetDouble());
        Assert.Equal(210, stats[1].GetProperty("ttftMs").GetDouble());
        Assert.Equal(120, stats[1].GetProperty("requestCount").GetInt32());

        // A non-empty probe result replaces the execution fallback entirely.
        Assert.Empty(_dashboard.ChannelPerformanceQueries);
        var probeQuery = Assert.Single(_dashboard.ChannelPerformanceProbeQueries);
        Assert.Equal("UTC", probeQuery.TimeZone);
        Assert.Equal(new DateTime(2026, 2, 9, 0, 0, 0, DateTimeKind.Utc), probeQuery.StartUtc);
    }

    [Fact]
    public async Task ChannelPerformanceStatsProbeFailuresDoNotFallBack()
    {
        _dashboard.ChannelPerformanceProbeFailure =
            new InvalidOperationException("probe query failed");
        _dashboard.ChannelPerformanceRows.Add(new DashboardChannelPerformanceRow
        {
            Date = "2026-03-10",
            ChannelId = 9,
            Throughput = 1,
            FirstTokenMs = 1,
            RequestCount = 1,
        });

        var errors = await QueryErrorsAsync(
            """
            query {
              channelPerformanceStats { date channelId }
            }
            """);

        Assert.NotEmpty(errors);
        Assert.Empty(_dashboard.ChannelPerformanceQueries);
        Assert.Single(_dashboard.ChannelPerformanceProbeQueries);
    }

    [Fact]
    public async Task UsageStatsByUserRequiresProjectOwnerAndParsesTheWindow()
    {
        _identity.ProjectId = 42;
        _dashboard.UserUsageRows.AddRange(
        [
            new DashboardUserUsageRow
            {
                UserId = 5,
                FirstName = " Ada ",
                LastName = " Lovelace ",
                Email = "ada@example.com",
                RequestCount = 2,
                TotalTokens = 100,
                TotalCost = 0.5,
            },
            new DashboardUserUsageRow
            {
                UserId = 6,
                FirstName = "",
                LastName = "",
                Email = "empty@example.com",
                RequestCount = 3,
                TotalTokens = 40,
                TotalCost = 0.1,
            },
        ]);

        var data = await QueryDataAsync(
            """
            query Stats($timeWindow: String) {
              usageStatsByUser(timeWindow: $timeWindow) {
                userId
                userName
                requestCount
                totalTokens
                totalCost
              }
            }
            """,
            new
            {
                timeWindow =
                    "custom:2026-03-01T00:00:00Z,2026-03-05T12:00:00+09:00",
            });

        var stats = data.GetProperty("usageStatsByUser").EnumerateArray().ToArray();
        Assert.Equal(2, stats.Length);
        Assert.Equal("gid://axonhub/User/5", stats[0].GetProperty("userId").GetString());
        // Go trims the composed name: the inner run of spaces survives.
        Assert.Equal("Ada   Lovelace", stats[0].GetProperty("userName").GetString());
        Assert.Equal("empty@example.com", stats[1].GetProperty("userName").GetString());

        var query = Assert.Single(_dashboard.UserUsageQueries);
        Assert.Equal(42L, query.ProjectId);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), query.CreatedAtGte!.Value);
        Assert.Equal(new DateTime(2026, 3, 5, 3, 0, 0, DateTimeKind.Utc), query.CreatedAtLte!.Value);

        // A member who is not a project owner is refused before the query.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 11,
                Email = "member@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _users.AssignProject(11, 42, isOwner: false, scopes: ["read_dashboard"]);
        _identity.User = new ManagementUser(11, IsOwner: false);

        var errors = await QueryErrorsAsync(
            """
            query { usageStatsByUser { userId } }
            """);
        Assert.Equal(
            "permission denied: only project owners can view usage statistics",
            errors[0].GetProperty("message").GetString());

        // A project owner membership is enough.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 12,
                Email = "owner@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _users.AssignProject(12, 42, isOwner: true);
        _identity.User = new ManagementUser(12, IsOwner: false);

        await QueryDataAsync(
            """
            query { usageStatsByUser(timeWindow: "day") { userId } }
            """);
        Assert.Equal(
            new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc),
            _dashboard.UserUsageQueries[^1].CreatedAtGte!.Value);
        Assert.Null(_dashboard.UserUsageQueries[^1].CreatedAtLte);
    }

    [Fact]
    public async Task ApiKeyTokenUsageStatsValidatesInputsAndFiltersAccess()
    {
        var key1 = _gate.ApiKeys.Add(key =>
        {
            key.Id = 1;
            key.Name = "key-1";
            key.ProjectId = 1;
        });
        var key2 = _gate.ApiKeys.Add(key =>
        {
            key.Id = 2;
            key.Name = "key-2";
            key.ProjectId = 1;
        });
        Assert.Equal(1L, key1.Id);
        Assert.Equal(2L, key2.Id);

        _dashboard.ApiKeyUsage.AddRange(
        [
            new DashboardApiKeyUsageRow
            {
                ApiKeyId = 1,
                InputTokens = 10,
                OutputTokens = 20,
                CachedTokens = 3,
                ReasoningTokens = 2,
            },
            new DashboardApiKeyUsageRow
            {
                ApiKeyId = 2,
                InputTokens = 5,
                OutputTokens = 1,
                CachedTokens = 0,
                ReasoningTokens = 0,
            },
        ]);
        _dashboard.ApiKeyModelUsage.AddRange(
        [
            ModelUsage(1, "m1", 40),
            ModelUsage(1, "m2", 30),
            ModelUsage(1, "m3", 20),
            ModelUsage(1, "m4", 10),
            ModelUsage(2, "m1", 6),
        ]);

        var data = await QueryDataAsync(
            """
            query Usage($input: APIKeyTokenUsageStatsInput) {
              apiKeyTokenUsageStats(input: $input) {
                apiKeyId
                inputTokens
                outputTokens
                topModels { modelId inputTokens }
              }
            }
            """,
            new
            {
                input = new
                {
                    apiKeyIds = new[]
                    {
                        "gid://axonhub/APIKey/1",
                        "gid://axonhub/APIKey/2",
                    },
                },
            });

        var stats = data.GetProperty("apiKeyTokenUsageStats").EnumerateArray().ToArray();
        Assert.Equal(2, stats.Length);
        Assert.Equal("gid://axonhub/APIKey/1", stats[0].GetProperty("apiKeyId").GetString());
        Assert.Equal(
            new[] { "m1", "m2", "m3" },
            stats[0].GetProperty("topModels")
                .EnumerateArray()
                .Select(static model => model.GetProperty("modelId").GetString())
                .ToArray());
        var query = Assert.Single(_dashboard.ApiKeyUsageQueries);
        Assert.Equal(new long[] { 1, 2 }, query.Ids);
        Assert.Null(query.CreatedAtGte);

        var missing = await QueryErrorsAsync(
            """
            query Usage($input: APIKeyTokenUsageStatsInput) {
              apiKeyTokenUsageStats(input: $input) { apiKeyId }
            }
            """,
            new { input = (object?)null });
        Assert.Equal(
            "apiKeyIds is required and must contain at least one API key",
            missing[0].GetProperty("message").GetString());

        var empty = await QueryErrorsAsync(
            """
            query Usage($input: APIKeyTokenUsageStatsInput) {
              apiKeyTokenUsageStats(input: $input) { apiKeyId }
            }
            """,
            new { input = new { apiKeyIds = Array.Empty<string>() } });
        Assert.Equal(
            "apiKeyIds is required and must contain at least one API key",
            empty[0].GetProperty("message").GetString());

        var tooMany = await QueryErrorsAsync(
            """
            query Usage($input: APIKeyTokenUsageStatsInput) {
              apiKeyTokenUsageStats(input: $input) { apiKeyId }
            }
            """,
            new
            {
                input = new
                {
                    apiKeyIds = Enumerable.Range(1, 101)
                        .Select(static id => "gid://axonhub/APIKey/" + id)
                        .ToArray(),
                },
            });
        Assert.Equal(
            "apiKeyIds cannot exceed 100 items",
            tooMany[0].GetProperty("message").GetString());

        var wrongType = await QueryErrorsAsync(
            """
            query Usage($input: APIKeyTokenUsageStatsInput) {
              apiKeyTokenUsageStats(input: $input) { apiKeyId }
            }
            """,
            new
            {
                input = new
                {
                    apiKeyIds = new[] { "gid://axonhub/Channel/1" },
                },
            });
        Assert.Equal(
            "invalid GUID type: expected APIKey, got Channel",
            wrongType[0].GetProperty("message").GetString());

        // A project-scoped member only sees the keys of their own project.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 11,
                Email = "member@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _users.AssignProject(11, 42, scopes: ["read_api_keys"]);
        _identity.User = new ManagementUser(11, IsOwner: false);
        _identity.ProjectId = 42;
        _gate.ApiKeys.Add(key =>
        {
            key.Id = 100;
            key.Name = "mine";
            key.ProjectId = 42;
        });
        _gate.ApiKeys.Add(key =>
        {
            key.Id = 101;
            key.Name = "theirs";
            key.ProjectId = 43;
        });
        _dashboard.ApiKeyUsage.Clear();
        _dashboard.ApiKeyUsage.Add(new DashboardApiKeyUsageRow { ApiKeyId = 100 });
        _dashboard.ApiKeyModelUsage.Clear();

        var scoped = await QueryDataAsync(
            """
            query Usage($input: APIKeyTokenUsageStatsInput) {
              apiKeyTokenUsageStats(input: $input) { apiKeyId }
            }
            """,
            new
            {
                input = new
                {
                    apiKeyIds = new[]
                    {
                        "gid://axonhub/APIKey/100",
                        "gid://axonhub/APIKey/101",
                    },
                },
            });

        var scopedStats = Assert.Single(
            scoped.GetProperty("apiKeyTokenUsageStats").EnumerateArray());
        Assert.Equal("gid://axonhub/APIKey/100", scopedStats.GetProperty("apiKeyId").GetString());
        Assert.Equal(new long[] { 100 }, _dashboard.ApiKeyUsageQueries[^1].Ids);
    }

    [Fact]
    public async Task TopRequestsProjectsSkipsMissingProjectsAndDeniesWithoutScope()
    {
        _dashboard.ProjectRows.AddRange(
        [
            new DashboardProjectRequestRow { ProjectId = 7, RequestCount = 5 },
            new DashboardProjectRequestRow { ProjectId = 9, RequestCount = 2 },
            new DashboardProjectRequestRow { ProjectId = 8, RequestCount = 1 },
        ]);
        _gate.Projects.Rows.Add(new ProjectRecord
        {
            Id = 7,
            Name = "P7",
            Description = "D7",
            Status = "active",
        });
        _gate.Projects.Rows.Add(new ProjectRecord
        {
            Id = 8,
            Name = "P8",
            Description = "D8",
            Status = "active",
        });

        var data = await QueryDataAsync(
            """
            query {
              topRequestsProjects { projectId projectName projectDescription requestCount }
            }
            """);

        var projects = data.GetProperty("topRequestsProjects").EnumerateArray().ToArray();
        Assert.Equal(2, projects.Length);
        Assert.Equal("gid://axonhub/Project/7", projects[0].GetProperty("projectId").GetString());
        Assert.Equal("P7", projects[0].GetProperty("projectName").GetString());
        Assert.Equal("D7", projects[0].GetProperty("projectDescription").GetString());
        Assert.Equal(5, projects[0].GetProperty("requestCount").GetInt32());
        Assert.Equal("gid://axonhub/Project/8", projects[1].GetProperty("projectId").GetString());
        Assert.Equal(10, Assert.Single(_dashboard.ProjectQueries));

        await _users.CreateAsync(
            new UserRecord
            {
                Id = 11,
                Email = "plain@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _identity.User = new ManagementUser(11, IsOwner: false);
        var projectCalls = _dashboard.ProjectQueries.Count;

        var errors = await QueryErrorsAsync(
            """
            query { topRequestsProjects { projectId } }
            """);
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "FORBIDDEN",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Equal(projectCalls, _dashboard.ProjectQueries.Count);
    }

    [Fact]
    public async Task MissingReadDashboardScopeFailsTheCachedResolvers()
    {
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 11,
                Email = "plain@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _identity.User = new ManagementUser(11, IsOwner: false);

        var errors = await QueryErrorsAsync(
            """
            query { dailyRequestStats { date } }
            """);
        Assert.Equal(
            "authz: principal user:11 does not have required scope read_dashboard",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_dashboard.DailyQueries);

        var fastest = await QueryErrorsAsync(
            """
            query { fastestModels(input: { timeWindow: "day" }) { modelId } }
            """);
        Assert.Equal(
            "authz: principal user:11 does not have required scope read_dashboard",
            fastest[0].GetProperty("message").GetString());
        Assert.Empty(_dashboard.FastestModelQueries);
    }

    private static DashboardFastestChannelRow FastestChannel(
        long id,
        string name,
        string type,
        long requestCount,
        double throughput)
        => new()
        {
            ChannelId = id,
            ChannelName = name,
            ChannelType = type,
            TokensCount = requestCount * 10,
            LatencyMs = 1_000,
            RequestCount = requestCount,
            Throughput = throughput,
        };

    private static DashboardApiKeyModelUsageRow ModelUsage(
        long apiKeyId,
        string modelId,
        long totalTokens)
        => new()
        {
            ApiKeyId = apiKeyId,
            ModelId = modelId,
            InputTokens = totalTokens,
            TotalTokens = totalTokens,
        };

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>Controllable service-level principal for the dashboard tests.</summary>
internal sealed class FakeDashboardIdentity : IManagementIdentity
{
    public ManagementUser? User { get; set; }

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

/// <summary>
/// Recording dashboard store with canned rows. The recorders double as the
/// assertions for window starts, cache hits and oversampling limits.
/// </summary>
internal sealed class FakeDashboardStore : IDashboardStore
{
    public List<DashboardStatusCountRow> StatusCounts { get; } = [];

    public Dictionary<DateTime, long> SinceCounts { get; } = [];

    public long BetweenCount { get; set; }

    public int StatusCountCalls { get; private set; }

    public int CountCalls { get; private set; }

    public Dictionary<DashboardUsageDimension, List<DashboardUsageAggregateRow>> Aggregates { get; } = [];

    public List<(DashboardUsageDimension Dimension, DateTime? Since)> UsageAggregateQueries { get; } = [];

    public DashboardTokenSumRow WindowTokenSums { get; set; } = new();

    public DashboardTokenSumRow AllTimeTokenSums { get; set; } = new();

    public int AllTimeTokenSumQueries { get; private set; }

    public List<DashboardApiKeyUsageRow> ApiKeyUsage { get; } = [];

    public List<DashboardApiKeyModelUsageRow> ApiKeyModelUsage { get; } = [];

    public List<(IReadOnlyList<long> Ids, DateTime? CreatedAtGte, DateTime? CreatedAtLte)> ApiKeyUsageQueries { get; } = [];

    public List<DashboardDailyRequestRow> DailyRows { get; } = [];

    public List<(DateTime StartUtc, DateTime EndUtc, string TimeZone)> DailyQueries { get; } = [];

    public List<DashboardProjectRequestRow> ProjectRows { get; } = [];

    public List<int> ProjectQueries { get; } = [];

    public List<DashboardChannelExecutionRow> ChannelExecutions { get; } = [];

    public List<DateTime?> ChannelExecutionWindows { get; } = [];

    public Dictionary<long, DashboardChannelRow> Channels { get; } = [];

    public HashSet<long> SoftDeletedChannelIds { get; } = [];

    public List<(IReadOnlyList<long> Ids, bool IncludeDeleted)> ChannelQueries { get; } = [];

    public List<DashboardFastestChannelRow> FastestChannels { get; } = [];

    public List<(DateTime Since, int Limit)> FastestChannelQueries { get; } = [];

    public List<DashboardFastestModelRow> FastestModels { get; } = [];

    public List<(DateTime Since, int Limit)> FastestModelQueries { get; } = [];

    public List<DashboardModelPerformanceRow> ModelPerformanceRows { get; } = [];

    public List<(DateTime StartUtc, string TimeZone)> ModelPerformanceQueries { get; } = [];

    public List<DashboardChannelPerformanceRow> ChannelPerformanceRows { get; } = [];

    public List<(DateTime StartUtc, string TimeZone)> ChannelPerformanceQueries { get; } = [];

    /// <summary>
    /// channel_performance_helpers.go:1327-1405 prefers these probe buckets;
    /// the execution fallback only runs when this list is empty.
    /// </summary>
    public List<DashboardChannelPerformanceRow> ChannelPerformanceProbeRows { get; } = [];

    public List<(DateTime StartUtc, string TimeZone)> ChannelPerformanceProbeQueries { get; } = [];

    public Exception? ChannelPerformanceProbeFailure { get; set; }

    public List<DashboardUserUsageRow> UserUsageRows { get; } = [];

    public List<(long ProjectId, DateTime? CreatedAtGte, DateTime? CreatedAtLte)> UserUsageQueries { get; } = [];

    public Task<IReadOnlyList<DashboardStatusCountRow>> GetRequestStatusCountsAsync(
        CancellationToken cancellationToken = default)
    {
        StatusCountCalls++;
        return Task.FromResult<IReadOnlyList<DashboardStatusCountRow>>(StatusCounts);
    }

    public Task<long> CountUsageLogsSinceAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
    {
        CountCalls++;
        return Task.FromResult(SinceCounts.GetValueOrDefault(since));
    }

    public Task<long> CountUsageLogsBetweenAsync(
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default)
    {
        CountCalls++;
        return Task.FromResult(BetweenCount);
    }

    public Task<IReadOnlyList<DashboardUsageAggregateRow>> GetUsageAggregatesAsync(
        DashboardUsageDimension dimension,
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        UsageAggregateQueries.Add((dimension, since));
        IReadOnlyList<DashboardUsageAggregateRow> result =
            Aggregates.TryGetValue(dimension, out var rows) ? rows : [];
        return Task.FromResult(result);
    }

    public Task<DashboardTokenSumRow> GetTokenSumsAsync(
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        if (since is null)
        {
            AllTimeTokenSumQueries++;
            return Task.FromResult(AllTimeTokenSums);
        }

        return Task.FromResult(WindowTokenSums);
    }

    public Task<IReadOnlyList<DashboardApiKeyUsageRow>> GetApiKeyTokenUsageAsync(
        IReadOnlyList<long> apiKeyIds,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
    {
        ApiKeyUsageQueries.Add((apiKeyIds, createdAtGte, createdAtLte));
        return Task.FromResult<IReadOnlyList<DashboardApiKeyUsageRow>>(
            ApiKeyUsage.Where(row => apiKeyIds.Contains(row.ApiKeyId)).ToList());
    }

    public Task<IReadOnlyList<DashboardApiKeyModelUsageRow>> GetApiKeyModelTokenUsageAsync(
        IReadOnlyList<long> apiKeyIds,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<DashboardApiKeyModelUsageRow>>(
            ApiKeyModelUsage.Where(row => apiKeyIds.Contains(row.ApiKeyId)).ToList());

    public Task<IReadOnlyList<DashboardDailyRequestRow>> GetDailyRequestStatsAsync(
        DateTime startUtc,
        DateTime endUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        DailyQueries.Add((startUtc, endUtc, timeZone));
        return Task.FromResult<IReadOnlyList<DashboardDailyRequestRow>>(DailyRows);
    }

    public Task<IReadOnlyList<DashboardProjectRequestRow>> GetTopProjectRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ProjectQueries.Add(limit);
        return Task.FromResult<IReadOnlyList<DashboardProjectRequestRow>>(ProjectRows);
    }

    public Task<IReadOnlyList<DashboardChannelExecutionRow>> GetChannelExecutionStatsAsync(
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        ChannelExecutionWindows.Add(since);
        return Task.FromResult<IReadOnlyList<DashboardChannelExecutionRow>>(ChannelExecutions);
    }

    public Task<IReadOnlyList<DashboardChannelRow>> GetChannelsByIdsAsync(
        IReadOnlyList<long> channelIds,
        bool includeDeleted,
        CancellationToken cancellationToken = default)
    {
        ChannelQueries.Add((channelIds, includeDeleted));
        return Task.FromResult<IReadOnlyList<DashboardChannelRow>>(
            channelIds
                .Where(id => includeDeleted || !SoftDeletedChannelIds.Contains(id))
                .Select(id => Channels.GetValueOrDefault(id))
                .Where(static row => row is not null)
                .Select(static row => row!)
                .ToList());
    }

    public Task<IReadOnlyList<DashboardFastestChannelRow>> GetFastestChannelsAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        FastestChannelQueries.Add((sinceUtc, limit));
        return Task.FromResult<IReadOnlyList<DashboardFastestChannelRow>>(FastestChannels);
    }

    public Task<IReadOnlyList<DashboardFastestModelRow>> GetFastestModelsAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        FastestModelQueries.Add((sinceUtc, limit));
        return Task.FromResult<IReadOnlyList<DashboardFastestModelRow>>(FastestModels);
    }

    public Task<IReadOnlyList<DashboardModelPerformanceRow>> GetDailyModelPerformanceAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        ModelPerformanceQueries.Add((startUtc, timeZone));
        return Task.FromResult<IReadOnlyList<DashboardModelPerformanceRow>>(ModelPerformanceRows);
    }

    public Task<IReadOnlyList<DashboardChannelPerformanceRow>> GetDailyChannelPerformanceAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        ChannelPerformanceQueries.Add((startUtc, timeZone));
        return Task.FromResult<IReadOnlyList<DashboardChannelPerformanceRow>>(ChannelPerformanceRows);
    }

    public Task<IReadOnlyList<DashboardChannelPerformanceRow>> GetDailyChannelPerformanceFromProbesAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        ChannelPerformanceProbeQueries.Add((startUtc, timeZone));
        return ChannelPerformanceProbeFailure is { } failure
            ? Task.FromException<IReadOnlyList<DashboardChannelPerformanceRow>>(failure)
            : Task.FromResult<IReadOnlyList<DashboardChannelPerformanceRow>>(
                ChannelPerformanceProbeRows);
    }

    public Task<IReadOnlyList<DashboardUserUsageRow>> GetUsageByUserAsync(
        long projectId,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
    {
        UserUsageQueries.Add((projectId, createdAtGte, createdAtLte));
        return Task.FromResult<IReadOnlyList<DashboardUserUsageRow>>(UserUsageRows);
    }
}
