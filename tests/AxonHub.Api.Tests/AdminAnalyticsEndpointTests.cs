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
/// Locks the analytics GraphQL surface over /admin/graphql
/// (internal/server/gql/analytics.graphql and analytics.resolvers.go) against
/// the queries the frontend sends
/// (frontend/src/features/analytics/data/analytics.ts): the four root fields,
/// the filter mapping, the local-day fill, the dimension name composition and
/// the read_dashboard scope decision.
/// </summary>
public sealed class AdminAnalyticsEndpointTests : IAsyncLifetime
{
    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeAnalyticsStore _analytics = new();
    private readonly FakeAnalyticsIdentity _identity = new();
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
        builder.Services.AddSingleton<IAnalyticsStore>(_analytics);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddScoped<IManagementIdentity>(_ => _identity);
        builder.Services.AddScoped<TracePrivacy>();
        builder.Services.AddScoped<AnalyticsService>();

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
    public async Task AnalyticsMetadataReturnsTheEarliestInstant()
    {
        _analytics.Earliest = new DateTime(2026, 3, 10, 10, 0, 0, 123, DateTimeKind.Utc);

        var data = await QueryDataAsync(
            """
            query GetAnalyticsMetadata {
              analyticsMetadata {
                earliestTime
              }
            }
            """);

        Assert.Equal(
            "2026-03-10T10:00:00.123Z",
            data.GetProperty("analyticsMetadata").GetProperty("earliestTime").GetString());
        var query = Assert.Single(_analytics.EarliestQueries);
        Assert.Null(query.ProjectScope);
        Assert.Null(query.VisibilityUserId);

        _analytics.Earliest = null;
        var empty = await QueryDataAsync(
            """
            query { analyticsMetadata { earliestTime } }
            """);
        Assert.Equal(
            JsonValueKind.Null,
            empty.GetProperty("analyticsMetadata").GetProperty("earliestTime").ValueKind);
    }

    [Fact]
    public async Task AnalyticsMetadataRunsTheUsageLogPrivacyChain()
    {
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 10,
                Email = "member@example.com",
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        _users.AssignProject(
            10,
            42,
            scopes: ["read_dashboard", "read_requests"]);
        _identity.User = new ManagementUser(10, IsOwner: false);
        _identity.ProjectId = 42;

        await QueryDataAsync(
            """
            query { analyticsMetadata { earliestTime } }
            """);

        var query = Assert.Single(_analytics.EarliestQueries);
        Assert.Equal(42L, query.ProjectScope);
        Assert.Equal(10L, query.VisibilityUserId);

        // The overview runs under the read_dashboard decision, which bypasses
        // the UsageLog privacy chain: no project predicate is installed.
        await QueryDataAsync(
            """
            query { analyticsOverview { totalRequests } }
            """);
        Assert.Null(Assert.Single(_analytics.Queries).ProjectIds);
    }

    [Fact]
    public async Task AnalyticsOverviewMapsTheFrontendFilterAndTotals()
    {
        _analytics.Overview = new AnalyticsOverviewRow
        {
            TotalRequests = 2,
            TotalInputTokens = 100,
            TotalCachedTokens = 30,
            TotalOutputTokens = 50,
            TotalTokens = 150,
            Cost = 0.75,
        };
        _analytics.ResolvedUserKeyIds = [7, 9];

        var data = await QueryDataAsync(
            """
            query GetAnalyticsOverview($filter: AnalyticsFilter) {
              analyticsOverview(filter: $filter) {
                totalTokens
                totalInputTokens
                totalCachedInputTokens
                totalUncachedInputTokens
                totalOutputTokens
                totalRequests
                totalCost
              }
            }
            """,
            new
            {
                filter = new
                {
                    startTime = "2026-03-10T12:00:00.500+09:00",
                    endTime = "2026-03-10T14:00:00Z",
                    projectIDs = new[] { "gid://axonhub/Project/1" },
                    channelIDs = new[] { "gid://axonhub/Channel/9" },
                    modelIDs = new[] { "gpt-4o" },
                    apiKeyIDs = new[] { "gid://axonhub/APIKey/7", "gid://axonhub/APIKey/8" },
                    userIDs = new[] { "gid://axonhub/User/5" },
                },
            });

        var overview = data.GetProperty("analyticsOverview");
        Assert.Equal(2, overview.GetProperty("totalRequests").GetInt32());
        Assert.Equal(100, overview.GetProperty("totalInputTokens").GetInt32());
        Assert.Equal(30, overview.GetProperty("totalCachedInputTokens").GetInt32());
        Assert.Equal(70, overview.GetProperty("totalUncachedInputTokens").GetInt32());
        Assert.Equal(50, overview.GetProperty("totalOutputTokens").GetInt32());
        Assert.Equal(150, overview.GetProperty("totalTokens").GetInt32());
        Assert.Equal(0.75, overview.GetProperty("totalCost").GetDouble());

        var query = Assert.Single(_analytics.Queries);
        Assert.Equal(new DateTime(2026, 3, 10, 3, 0, 0, 500, DateTimeKind.Utc), query.StartTime);
        Assert.Equal(new DateTime(2026, 3, 10, 14, 0, 0, DateTimeKind.Utc), query.EndTime);
        Assert.Equal([1L], query.ProjectIds);
        Assert.Equal([9L], query.ChannelIds);
        Assert.Equal(["gpt-4o"], query.ModelIds);
        Assert.Equal([5L], _analytics.ResolvedUserIds);
        // Explicit keys ∩ the user's keys, in the explicit order.
        Assert.Equal([7L], query.ApiKeyIds!.ToArray());
        Assert.True(query.HasUserFilter);
    }

    [Fact]
    public async Task UserFilterWithNoKeysSendsTheFalsePredicate()
    {
        _analytics.ResolvedUserKeyIds = [];

        await QueryDataAsync(
            """
            query Overview($filter: AnalyticsFilter) {
              analyticsOverview(filter: $filter) { totalRequests }
            }
            """,
            new { filter = new { userIDs = new[] { "gid://axonhub/User/404" } } });

        var query = Assert.Single(_analytics.Queries);
        Assert.Empty(query.ApiKeyIds!);
        Assert.True(query.HasUserFilter);
    }

    [Fact]
    public async Task DailyStatsFillLocalCalendarDaysInTheConfiguredTimeZone()
    {
        _gate.Systems.Add(SystemTimeLocations.GeneralSettingsKey, """{"timezone":"Asia/Tokyo"}""");
        _analytics.Daily =
        [
            new AnalyticsDailyRow
            {
                Date = "2026-03-12",
                RequestCount = 3,
                InputTokens = 100,
                CachedTokens = 40,
                OutputTokens = 20,
                TotalTokens = 120,
                Cost = 1.25,
            },
        ];

        var data = await QueryDataAsync(
            """
            query GetAnalyticsDailyStats($filter: AnalyticsFilter) {
              analyticsDailyStats(filter: $filter) {
                date
                inputTokens
                cachedInputTokens
                uncachedInputTokens
                outputTokens
                totalTokens
                requestCount
                cost
              }
            }
            """,
            new
            {
                filter = new
                {
                    startTime = "2026-03-10T15:00:00Z",
                    endTime = "2026-03-12T14:00:00Z",
                },
            });

        var stats = data.GetProperty("analyticsDailyStats").EnumerateArray().ToArray();
        Assert.Equal(2, stats.Length);

        Assert.Equal("2026-03-11", stats[0].GetProperty("date").GetString());
        Assert.Equal(0, stats[0].GetProperty("requestCount").GetInt32());
        Assert.Equal(0, stats[0].GetProperty("totalTokens").GetInt32());
        Assert.Equal(0, stats[0].GetProperty("cost").GetDouble());

        Assert.Equal("2026-03-12", stats[1].GetProperty("date").GetString());
        Assert.Equal(3, stats[1].GetProperty("requestCount").GetInt32());
        Assert.Equal(100, stats[1].GetProperty("inputTokens").GetInt32());
        Assert.Equal(40, stats[1].GetProperty("cachedInputTokens").GetInt32());
        Assert.Equal(60, stats[1].GetProperty("uncachedInputTokens").GetInt32());
        Assert.Equal(20, stats[1].GetProperty("outputTokens").GetInt32());
        Assert.Equal(120, stats[1].GetProperty("totalTokens").GetInt32());
        Assert.Equal(1.25, stats[1].GetProperty("cost").GetDouble());

        // Go interpolates loc.String(); the port binds the configured name.
        Assert.Equal("Asia/Tokyo", _analytics.DailyTimeZone);
    }

    [Fact]
    public async Task DailyStatsDefaultToTheLastThirtyLocalDays()
    {
        var data = await QueryDataAsync(
            """
            query { analyticsDailyStats { date } }
            """);

        var stats = data.GetProperty("analyticsDailyStats").EnumerateArray().ToArray();
        Assert.Equal(30, stats.Length);
        Assert.Equal("2026-02-09", stats[0].GetProperty("date").GetString());
        Assert.Equal("2026-03-10", stats[^1].GetProperty("date").GetString());
        Assert.Equal("UTC", _analytics.DailyTimeZone);
    }

    [Fact]
    public async Task DimensionStatsComposeNamesAndMergePerformance()
    {
        _analytics.Dimensions[AnalyticsDimension.ApiKey] =
        [
            new AnalyticsDimensionRow
            {
                Id = "2",
                RequestCount = 4,
                InputTokens = 10,
                CachedTokens = 2,
                OutputTokens = 6,
                TotalTokens = 16,
                Cost = 0.5,
            },
            new AnalyticsDimensionRow
            {
                Id = "3",
                RequestCount = 1,
                InputTokens = 5,
                CachedTokens = 0,
                OutputTokens = 1,
                TotalTokens = 6,
                Cost = 0.1,
            },
        ];
        _analytics.ApiKeyNames[2] = "key-2";
        _analytics.Performances[AnalyticsDimension.ApiKey] = new Dictionary<string, AnalyticsPerformanceRow>
        {
            ["2"] = new() { Id = "2", TokensPerSecond = 12.5, TtftMs = 100 },
        };

        var data = await QueryDataAsync(
            """
            query GetAnalyticsDimensionStats($filter: AnalyticsFilter, $dimension: String!) {
              analyticsDimensionStats(filter: $filter, dimension: $dimension) {
                id
                name
                requestCount
                inputTokens
                cachedInputTokens
                outputTokens
                totalTokens
                cost
                tokensPerSecond
                ttftMs
              }
            }
            """,
            new { filter = (object?)null, dimension = "apiKey" });

        var stats = data.GetProperty("analyticsDimensionStats").EnumerateArray().ToArray();
        Assert.Equal(2, stats.Length);
        Assert.Equal("2", stats[0].GetProperty("id").GetString());
        Assert.Equal("key-2", stats[0].GetProperty("name").GetString());
        Assert.Equal(12.5, stats[0].GetProperty("tokensPerSecond").GetDouble());
        Assert.Equal(100, stats[0].GetProperty("ttftMs").GetDouble());
        Assert.Equal("3", stats[1].GetProperty("id").GetString());
        // Missing API key rows fall back to the id placeholder.
        Assert.Equal("API Key #3", stats[1].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, stats[1].GetProperty("tokensPerSecond").ValueKind);
        Assert.Equal(JsonValueKind.Null, stats[1].GetProperty("ttftMs").ValueKind);

        _analytics.Dimensions[AnalyticsDimension.User] =
        [
            new AnalyticsDimensionRow
            {
                Id = "7",
                FirstName = " Ada ",
                LastName = "Lovelace ",
                Email = "ada@example.com",
                RequestCount = 1,
            },
            new AnalyticsDimensionRow
            {
                Id = "8",
                FirstName = " ",
                LastName = "",
                Email = "empty@example.com",
                RequestCount = 2,
            },
        ];
        _analytics.Performances.Remove(AnalyticsDimension.User);

        var users = await QueryDataAsync(
            """
            query Stats($dimension: String!) {
              analyticsDimensionStats(dimension: $dimension) { id name requestCount tokensPerSecond }
            }
            """,
            new { dimension = "user" });

        var userStats = users.GetProperty("analyticsDimensionStats").EnumerateArray().ToArray();
        // Go composes "first last" and trims only the edges, so the inner double
        // space (first_name has a trailing space) survives.
        Assert.Equal("Ada  Lovelace", userStats[0].GetProperty("name").GetString());
        Assert.Equal("empty@example.com", userStats[1].GetProperty("name").GetString());

        Assert.Contains(AnalyticsDimension.ApiKey, _analytics.DimensionQueries);
        Assert.Contains(AnalyticsDimension.User, _analytics.DimensionQueries);
    }

    [Fact]
    public async Task UnknownDimensionReportsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(
            """
            query Stats($dimension: String!) {
              analyticsDimensionStats(dimension: $dimension) { id }
            }
            """,
            new { dimension = "foo" });

        Assert.Equal(
            "unsupported dimension: foo, must be one of: channel, model, apiKey, user",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task MissingReadDashboardScopeDeniesWithForbidden()
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
            query { analyticsOverview { totalRequests } }
            """);

        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "FORBIDDEN",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Empty(_analytics.Queries);
    }

    [Fact]
    public async Task StoreFailuresKeepTheReferencePrefix()
    {
        _analytics.OverviewFailure = new InvalidOperationException("boom");

        var errors = await QueryErrorsAsync(
            """
            query { analyticsOverview { totalRequests } }
            """);

        Assert.Equal(
            "failed to get analytics overview: boom",
            errors[0].GetProperty("message").GetString());
    }

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

/// <summary>Controllable service-level principal for the analytics tests.</summary>
internal sealed class FakeAnalyticsIdentity : IManagementIdentity
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

/// <summary>Recording analytics store with canned aggregate answers.</summary>
internal sealed class FakeAnalyticsStore : IAnalyticsStore
{
    public List<(long? ProjectScope, long? VisibilityUserId)> EarliestQueries { get; } = [];

    public List<AnalyticsQueryFilter> Queries { get; } = [];

    public List<AnalyticsDimension> DimensionQueries { get; } = [];

    public List<long> ResolvedUserIds { get; } = [];

    public DateTime? Earliest { get; set; }

    public AnalyticsOverviewRow Overview { get; set; } = new();

    public List<AnalyticsDailyRow> Daily { get; set; } = [];

    public string? DailyTimeZone { get; private set; }

    public Dictionary<AnalyticsDimension, List<AnalyticsDimensionRow>> Dimensions { get; } = [];

    public Dictionary<AnalyticsDimension, Dictionary<string, AnalyticsPerformanceRow>> Performances { get; } = [];

    public Dictionary<long, string> ApiKeyNames { get; set; } = [];

    public IReadOnlyList<long> ResolvedUserKeyIds { get; set; } = [];

    public Exception? OverviewFailure { get; set; }

    public Task<DateTime?> GetEarliestCreatedAtAsync(
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        EarliestQueries.Add((projectScope, visibilityUserId));
        return Task.FromResult(Earliest);
    }

    public Task<AnalyticsOverviewRow> GetOverviewAsync(
        AnalyticsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(filter);
        if (OverviewFailure is not null)
        {
            return Task.FromException<AnalyticsOverviewRow>(OverviewFailure);
        }

        return Task.FromResult(Overview);
    }

    public Task<IReadOnlyList<AnalyticsDailyRow>> GetDailyAsync(
        AnalyticsQueryFilter filter,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(filter);
        DailyTimeZone = timeZone;
        return Task.FromResult<IReadOnlyList<AnalyticsDailyRow>>(Daily);
    }

    public Task<IReadOnlyList<AnalyticsDimensionRow>> GetDimensionAsync(
        AnalyticsQueryFilter filter,
        AnalyticsDimension dimension,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(filter);
        DimensionQueries.Add(dimension);
        return Task.FromResult<IReadOnlyList<AnalyticsDimensionRow>>(
            Dimensions.TryGetValue(dimension, out var rows) ? rows : []);
    }

    public Task<IReadOnlyDictionary<string, AnalyticsPerformanceRow>> GetPerformanceAsync(
        AnalyticsQueryFilter filter,
        AnalyticsDimension dimension,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(filter);
        DimensionQueries.Add(dimension);
        return Task.FromResult<IReadOnlyDictionary<string, AnalyticsPerformanceRow>>(
            Performances.TryGetValue(dimension, out var rows)
                ? rows
                : new Dictionary<string, AnalyticsPerformanceRow>());
    }

    public Task<IReadOnlyDictionary<long, string>> GetApiKeyNamesAsync(
        IReadOnlyList<long> apiKeyIds,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        _ = apiKeyIds;
        return Task.FromResult<IReadOnlyDictionary<long, string>>(ApiKeyNames);
    }

    public Task<IReadOnlyList<long>> ResolveApiKeyIdsByUserIdsAsync(
        IReadOnlyList<long> userIds,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ResolvedUserIds.AddRange(userIds);
        return Task.FromResult(ResolvedUserKeyIds);
    }
}
