using System.Globalization;
using Dapper;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the SQL semantics of the analytics aggregates
/// (internal/server/gql/analytics.resolvers.go / analytics_helpers.go) against
/// a real PostgreSQL schema, including the time-range test from
/// analytics_time_range_test.go.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AnalyticsStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RangeBoundsAreInclusiveAndSecondPrecision()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new AnalyticsStore(context);

        var channel = await InsertChannelAsync(connection, "channel-9");
        await InsertUsageLogAsync(
            connection,
            requestId: 1,
            channelId: channel,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day.AddHours(10),
            prompt: 10,
            cached: 2,
            completion: 3,
            total: 13,
            cost: 0.1);
        await InsertUsageLogAsync(
            connection,
            requestId: 2,
            channelId: channel,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day.AddHours(12),
            prompt: 20,
            cached: 5,
            completion: 4,
            total: 24,
            cost: 0.2);
        await InsertUsageLogAsync(
            connection,
            requestId: 3,
            channelId: channel,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day.AddHours(14),
            prompt: 30,
            cached: 8,
            completion: 6,
            total: 36,
            cost: 0.3);

        Assert.Equal(Day.AddHours(10), await store.GetEarliestCreatedAtAsync(null, null));

        var narrow = await store.GetOverviewAsync(
            new AnalyticsQueryFilter
            {
                StartTime = Day.AddHours(12),
                EndTime = Day.AddHours(13),
            });
        Assert.Equal(1, narrow.TotalRequests);
        Assert.Equal(20, narrow.TotalInputTokens);
        Assert.Equal(5, narrow.TotalCachedTokens);
        Assert.Equal(4, narrow.TotalOutputTokens);
        Assert.Equal(24, narrow.TotalTokens);
        Assert.Equal(0.2, narrow.Cost);

        // Both bounds are inclusive: the 14:00 row is inside the window.
        var inclusive = await store.GetOverviewAsync(
            new AnalyticsQueryFilter
            {
                StartTime = Day.AddHours(12),
                EndTime = Day.AddHours(14),
            });
        Assert.Equal(2, inclusive.TotalRequests);

        // The scalar carries milliseconds, so a .500 start excludes 12:00:00.
        var subsecond = await store.GetOverviewAsync(
            new AnalyticsQueryFilter
            {
                StartTime = Day.AddHours(12).AddMilliseconds(500),
                EndTime = Day.AddHours(15),
            });
        Assert.Equal(1, subsecond.TotalRequests);

        var daily = await store.GetDailyAsync(
            new AnalyticsQueryFilter
            {
                StartTime = Day.AddHours(12),
                EndTime = Day.AddHours(13),
            },
            "UTC");
        var day = Assert.Single(daily);
        Assert.Equal("2026-03-10", day.Date);
        Assert.Equal(1, day.RequestCount);
        Assert.Equal(24, day.TotalTokens);
        Assert.Equal(0.2, day.Cost);

        var channels = await store.GetDimensionAsync(
            new AnalyticsQueryFilter
            {
                StartTime = Day.AddHours(13),
                EndTime = Day.AddHours(15),
            },
            AnalyticsDimension.Channel);
        var channelRow = Assert.Single(channels);
        Assert.Equal(channel.ToString(CultureInfo.InvariantCulture), channelRow.Id);
        Assert.Equal("channel-9", channelRow.Name);
        Assert.Equal(1, channelRow.RequestCount);
        Assert.Equal(36, channelRow.TotalTokens);
    }

    [Fact]
    public async Task EmptyOverviewReturnsZeroCounts()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new AnalyticsStore(context);

        var overview = await store.GetOverviewAsync(
            new AnalyticsQueryFilter { StartTime = Day, EndTime = Day.AddDays(1) });
        Assert.Equal(0, overview.TotalRequests);
        Assert.Equal(0, overview.TotalTokens);
        Assert.Equal(0, overview.Cost);
    }

    [Fact]
    public async Task DimensionQueriesJoinActiveChannelsLiveKeysAndUsers()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new AnalyticsStore(context);

        var activeChannel = await InsertChannelAsync(connection, "channel-9");
        var deletedChannel = await InsertChannelAsync(connection, "channel-10", deletedAt: 123);
        var ada = await InsertUserAsync(connection, "ada@example.com", " Ada ", "Lovelace ");
        var empty = await InsertUserAsync(connection, "empty@example.com", "", "");
        var deletedKey = await InsertApiKeyAsync(connection, "deleted-key", ada, deletedAt: 123);
        var liveKey = await InsertApiKeyAsync(connection, "live-key", empty);

        await InsertUsageLogAsync(
            connection,
            requestId: 1,
            channelId: activeChannel,
            apiKeyId: deletedKey,
            modelId: "gpt-4o",
            createdAt: Day,
            prompt: 6,
            cached: 1,
            completion: 4,
            total: 10,
            cost: 0.1);
        await InsertUsageLogAsync(
            connection,
            requestId: 2,
            channelId: activeChannel,
            apiKeyId: liveKey,
            modelId: "gpt-4o",
            createdAt: Day,
            prompt: 10,
            cached: 2,
            completion: 10,
            total: 20,
            cost: 0.2);
        await InsertUsageLogAsync(
            connection,
            requestId: 3,
            channelId: deletedChannel,
            apiKeyId: liveKey,
            modelId: "claude",
            createdAt: Day,
            prompt: 15,
            cached: 5,
            completion: 15,
            total: 30,
            cost: 0.3);
        await InsertUsageLogAsync(
            connection,
            requestId: 4,
            channelId: activeChannel,
            apiKeyId: null,
            modelId: "claude",
            createdAt: Day,
            prompt: 20,
            cached: 10,
            completion: 20,
            total: 40,
            cost: 0.4);

        // Channels: the join drops soft-deleted channels.
        var channels = await store.GetDimensionAsync(
            new AnalyticsQueryFilter(),
            AnalyticsDimension.Channel);
        var channelRow = Assert.Single(channels);
        Assert.Equal(activeChannel.ToString(CultureInfo.InvariantCulture), channelRow.Id);
        Assert.Equal(3, channelRow.RequestCount);
        Assert.Equal(70, channelRow.TotalTokens);

        // Models: id and name are both the model_id, ordered by total_tokens desc.
        var models = await store.GetDimensionAsync(
            new AnalyticsQueryFilter(),
            AnalyticsDimension.Model);
        Assert.Equal(["claude", "gpt-4o"], models.Select(static row => row.Id).ToArray());
        Assert.Equal(["claude", "gpt-4o"], models.Select(static row => row.Name!).ToArray());
        Assert.Equal(70, models[0].TotalTokens);
        Assert.Equal(30, models[1].TotalTokens);

        // API keys: null keys are excluded, deleted keys still aggregate.
        var apiKeys = await store.GetDimensionAsync(
            new AnalyticsQueryFilter(),
            AnalyticsDimension.ApiKey);
        Assert.Equal(
            [liveKey.ToString(CultureInfo.InvariantCulture), deletedKey.ToString(CultureInfo.InvariantCulture)],
            apiKeys.Select(static row => row.Id).ToArray());
        Assert.All(apiKeys, static row => Assert.Null(row.Name));
        Assert.Equal(50, apiKeys[0].TotalTokens);
        Assert.Equal(10, apiKeys[1].TotalTokens);

        var names = await store.GetApiKeyNamesAsync([liveKey, deletedKey]);
        Assert.Equal("live-key", names[liveKey]);
        // The reference reads names without a deleted_at predicate.
        Assert.Equal("deleted-key", names[deletedKey]);

        // Users: only live keys join; the deleted key's rows disappear.
        var users = await store.GetDimensionAsync(
            new AnalyticsQueryFilter(),
            AnalyticsDimension.User);
        var userRow = Assert.Single(users);
        Assert.Equal(empty.ToString(CultureInfo.InvariantCulture), userRow.Id);
        Assert.Equal("", userRow.FirstName);
        Assert.Equal("", userRow.LastName);
        Assert.Equal("empty@example.com", userRow.Email);
        Assert.Equal(2, userRow.RequestCount);
        Assert.Equal(50, userRow.TotalTokens);

        // User resolution keeps soft-deleted keys, like the reference query.
        Assert.Equal([deletedKey], await store.ResolveApiKeyIdsByUserIdsAsync([ada]));
    }

    [Fact]
    public async Task PerformanceUsesNewestCompletedAttemptAndClampsLatency()
    {
        await using var db = new TestDatabase(fixture);
        await using var connection = await db.OpenMigratedAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new AnalyticsStore(context);

        var channel9 = await InsertChannelAsync(connection, "channel-9");
        var channel10 = await InsertChannelAsync(connection, "channel-10");
        var channel11 = await InsertChannelAsync(connection, "channel-11");
        var channel12 = await InsertChannelAsync(connection, "channel-12");

        // Streaming request with two completed attempts: only the newest counts.
        await InsertUsageLogAsync(
            connection,
            requestId: 100,
            channelId: channel9,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day,
            prompt: 1,
            cached: 0,
            completion: 500,
            total: 501,
            cost: 0.5);
        await InsertExecutionAsync(connection, 100, channel9, "completed", stream: true, latency: 1000, ttft: 400);
        await InsertExecutionAsync(connection, 100, channel9, "completed", stream: true, latency: 500, ttft: 100);
        await InsertExecutionAsync(connection, 100, channel9, "failed", stream: true, latency: 50, ttft: 10);
        await InsertExecutionAsync(connection, 100, channel9, "completed", stream: true, latency: null, ttft: 10);

        // Non-streaming: the whole latency counts, and TTFT stays null.
        await InsertUsageLogAsync(
            connection,
            requestId: 200,
            channelId: channel10,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day,
            prompt: 1,
            cached: 0,
            completion: 100,
            total: 101,
            cost: 0.5);
        await InsertExecutionAsync(connection, 200, channel10, "completed", stream: false, latency: 200, ttft: null);

        // Generation latency below MinLatencyMs is clamped to 10.
        await InsertUsageLogAsync(
            connection,
            requestId: 300,
            channelId: channel11,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day,
            prompt: 1,
            cached: 0,
            completion: 7,
            total: 8,
            cost: 0.5);
        await InsertExecutionAsync(connection, 300, channel11, "completed", stream: true, latency: 5, ttft: 4);

        // apiKey performance excludes rows without a key.
        await InsertUsageLogAsync(
            connection,
            requestId: 400,
            channelId: channel12,
            apiKeyId: null,
            modelId: "gpt-4o",
            createdAt: Day,
            prompt: 1,
            cached: 0,
            completion: 1,
            total: 2,
            cost: 0.5);
        await InsertExecutionAsync(connection, 400, channel12, "completed", stream: false, latency: 100, ttft: null);

        var channels = await store.GetPerformanceAsync(
            new AnalyticsQueryFilter(),
            AnalyticsDimension.Channel);
        Assert.Equal(4, channels.Count);

        var streaming = channels[channel9.ToString(CultureInfo.InvariantCulture)];
        Assert.Equal(1250d, streaming.TokensPerSecond!.Value, 6);
        Assert.Equal(100d, streaming.TtftMs!.Value, 6);

        var nonStreaming = channels[channel10.ToString(CultureInfo.InvariantCulture)];
        Assert.Equal(500d, nonStreaming.TokensPerSecond!.Value, 6);
        Assert.Null(nonStreaming.TtftMs);

        var clamped = channels[channel11.ToString(CultureInfo.InvariantCulture)];
        Assert.Equal(700d, clamped.TokensPerSecond!.Value, 6);
        Assert.Equal(4d, clamped.TtftMs!.Value, 6);

        // The API key aggregate filters the null-key row away.
        var apiKeys = await store.GetPerformanceAsync(
            new AnalyticsQueryFilter(),
            AnalyticsDimension.ApiKey);
        Assert.Empty(apiKeys);
    }

    private static Task<long> InsertChannelAsync(
        NpgsqlConnection connection,
        string name,
        long deletedAt = 0)
        => connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO "channels"
                ("type", "name", "status", "credentials", "supported_models",
                 "default_test_model", "deleted_at")
            VALUES ('openai', @name, 'enabled', '{}'::jsonb, '[]'::jsonb, 'gpt-4o', @deletedAt)
            RETURNING "id";
            """,
            new { name, deletedAt });

    private static Task<long> InsertUserAsync(
        NpgsqlConnection connection,
        string email,
        string firstName,
        string lastName)
        => connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO "users"
                ("email", "password", "first_name", "last_name", "is_owner")
            VALUES (@email, '', @firstName, @lastName, false)
            RETURNING "id";
            """,
            new { email, firstName, lastName });

    private static Task<long> InsertApiKeyAsync(
        NpgsqlConnection connection,
        string name,
        long userId,
        long deletedAt = 0)
        => connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO "api_keys"
                ("key", "name", "type", "status", "project_id", "user_id", "deleted_at")
            VALUES (@name, @name, 'user', 'enabled', 1, @userId, @deletedAt)
            RETURNING "id";
            """,
            new { name, userId, deletedAt });

    private static Task<long> InsertUsageLogAsync(
        NpgsqlConnection connection,
        long requestId,
        long channelId,
        long? apiKeyId,
        string modelId,
        DateTime createdAt,
        long prompt,
        long cached,
        long completion,
        long total,
        double? cost)
        => connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO "usage_logs"
                ("created_at", "updated_at", "request_id", "channel_id", "api_key_id",
                 "project_id", "model_id", "prompt_tokens", "prompt_cached_tokens",
                 "completion_tokens", "total_tokens", "total_cost")
            VALUES (@createdAt, @createdAt, @requestId, @channelId, @apiKeyId,
                    1, @modelId, @prompt, @cached, @completion, @total, @cost)
            RETURNING "id";
            """,
            new
            {
                createdAt,
                requestId,
                channelId,
                apiKeyId,
                modelId,
                prompt,
                cached,
                completion,
                total,
                cost,
            });

    private static Task<long> InsertExecutionAsync(
        NpgsqlConnection connection,
        long requestId,
        long channelId,
        string status,
        bool stream,
        long? latency,
        long? ttft)
        => connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO "request_executions"
                ("request_id", "channel_id", "model_id", "status", "stream",
                 "metrics_latency_ms", "metrics_first_token_latency_ms", "request_body")
            VALUES (@requestId, @channelId, 'gpt-4o', @status, @stream,
                    @latency, @ttft, '{}'::jsonb)
            RETURNING "id";
            """,
            new { requestId, channelId, status, stream, latency, ttft });
}
