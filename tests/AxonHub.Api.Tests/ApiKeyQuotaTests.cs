using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.QuotaService.CheckAPIKeyQuota's observable surface
/// (internal/server/biz/quota.go:50-395): the three windows, the aggregate
/// messages and the timezone resolution used by calendar periods.
/// </summary>
public sealed class ApiKeyQuotaTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo UtcPlus8 = TimeZoneInfo.CreateCustomTimeZone(
        "Test/UTC+08",
        TimeSpan.FromHours(8),
        "Test UTC+08",
        "Test UTC+08");

    // ------------------------------------------------------------------
    // quotaWindow (biz/quota.go:193-256)
    // ------------------------------------------------------------------

    [Fact]
    public void AllTimeWindowEndsNowInclusive()
    {
        var window = ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel { Type = "all_time" });

        Assert.Null(window.Start);
        Assert.Equal(Now.UtcDateTime, window.End);
        Assert.True(window.EndInclusive);
    }

    [Fact]
    public void PastDurationWindowStartsBeforeNow()
    {
        (string Unit, long Value, TimeSpan Expected)[] cases =
        [
            ("minute", 30, TimeSpan.FromMinutes(30)),
            ("hour", 2, TimeSpan.FromHours(2)),
            ("day", 3, TimeSpan.FromDays(3)),
        ];

        foreach (var one in cases)
        {
            var window = ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
            {
                Type = "past_duration",
                PastDuration = new ApiKeyQuotaPastDurationModel
                {
                    Value = one.Value,
                    Unit = one.Unit,
                },
            });

            Assert.Equal(Now.UtcDateTime - one.Expected, window.Start);
            Assert.Equal(Now.UtcDateTime, window.End);
            Assert.True(window.EndInclusive);
        }
    }

    [Fact]
    public void PastDurationValidationMatchesTheReferenceMessages()
    {
        var missing = Assert.Throws<InvalidOperationException>(
            () => ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
            {
                Type = "past_duration",
            }));
        Assert.Equal("pastDuration is required", missing.Message);

        var nonPositive = Assert.Throws<InvalidOperationException>(
            () => ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
            {
                Type = "past_duration",
                PastDuration = new ApiKeyQuotaPastDurationModel { Value = 0, Unit = "minute" },
            }));
        Assert.Equal("pastDuration.value must be positive", nonPositive.Message);

        var unknownUnit = Assert.Throws<InvalidOperationException>(
            () => ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
            {
                Type = "past_duration",
                PastDuration = new ApiKeyQuotaPastDurationModel { Value = 1, Unit = "week" },
            }));
        Assert.Equal("unknown pastDuration.unit: week", unknownUnit.Message);
    }

    [Fact]
    public void CalendarDayWindowUsesLocalMidnight()
    {
        var window = ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
        {
            Type = "calendar_duration",
            CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "day" },
        });

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), window.Start);
        Assert.Equal(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), window.End);
        Assert.False(window.EndInclusive);
    }

    [Fact]
    public void CalendarMonthWindowUsesTheFirstLocalMidnight()
    {
        var window = ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
        {
            Type = "calendar_duration",
            CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "month" },
        }, UtcPlus8);

        // 2026-05-01T12:00Z is 2026-05-01T20:00+08; the month starts at
        // 2026-05-01T00:00+08 = 2026-04-30T16:00Z.
        Assert.Equal(new DateTime(2026, 4, 30, 16, 0, 0, DateTimeKind.Utc), window.Start);
        Assert.Equal(new DateTime(2026, 5, 31, 16, 0, 0, DateTimeKind.Utc), window.End);
        Assert.False(window.EndInclusive);
    }

    [Fact]
    public void CalendarDayWindowCanSpillIntoThePreviousUtcDay()
    {
        var late = new DateTimeOffset(2026, 5, 1, 20, 0, 0, TimeSpan.Zero);

        var window = ApiKeyQuotaWindows.Compute(late, new ApiKeyQuotaPeriodModel
        {
            Type = "calendar_duration",
            CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "day" },
        }, UtcPlus8);

        Assert.Equal(new DateTime(2026, 5, 1, 16, 0, 0, DateTimeKind.Utc), window.Start);
        Assert.Equal(new DateTime(2026, 5, 2, 16, 0, 0, DateTimeKind.Utc), window.End);
    }

    [Fact]
    public void CalendarValidationMatchesTheReferenceMessages()
    {
        var missing = Assert.Throws<InvalidOperationException>(
            () => ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
            {
                Type = "calendar_duration",
            }));
        Assert.Equal("calendarDuration is required", missing.Message);

        var unknownUnit = Assert.Throws<InvalidOperationException>(
            () => ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel
            {
                Type = "calendar_duration",
                CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "year" },
            }));
        Assert.Equal("unknown calendarDuration.unit: year", unknownUnit.Message);
    }

    [Fact]
    public void UnknownPeriodTypeIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ApiKeyQuotaWindows.Compute(Now, new ApiKeyQuotaPeriodModel { Type = "fortnight" }));

        Assert.Equal("unknown period.type: fortnight", error.Message);
    }

    // ------------------------------------------------------------------
    // CheckAPIKeyQuota (biz/quota.go:50-113)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestsQuotaReportsTheAggregatedCount()
    {
        var logs = new InMemoryUsageLogStore();
        logs.Rows.Add(Log(2, Now.AddSeconds(-30), totalTokens: 7, totalCost: 0.01));
        var service = Service(logs, Now);

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 1,
            Period = MinutePeriod(),
        });

        Assert.False(result.Allowed);
        Assert.Equal("requests quota exceeded: 1/1", result.Message);
        Assert.Equal(Now.AddMinutes(-1).UtcDateTime, result.Window.Start);
        Assert.Equal(Now.UtcDateTime, result.Window.End);
        Assert.True(result.Window.EndInclusive);
    }

    [Fact]
    public async Task RowsOutsideTheWindowDoNotCount()
    {
        var logs = new InMemoryUsageLogStore();
        logs.Rows.Add(Log(2, Now.AddMinutes(-2), totalTokens: 7, totalCost: 0.01));
        logs.Rows.Add(Log(3, Now.AddSeconds(-10), totalTokens: 7, totalCost: 0.01));
        var service = Service(logs, Now);

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 1,
            Period = MinutePeriod(),
        });

        Assert.True(result.Allowed);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public async Task TotalTokensQuotaReportsTheAggregate()
    {
        var logs = new InMemoryUsageLogStore();
        logs.Rows.Add(Log(2, Now.AddSeconds(-30), totalTokens: 100, totalCost: 0.01));
        logs.Rows.Add(Log(2, Now.AddSeconds(-20), totalTokens: 50, totalCost: 0.02));
        var service = Service(logs, Now);

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            TotalTokens = 100,
            Period = MinutePeriod(),
        });

        Assert.False(result.Allowed);
        Assert.Equal("total_tokens quota exceeded: 150/100", result.Message);
    }

    [Fact]
    public async Task CostQuotaFormatsBothDecimalsLikeShopspring()
    {
        var logs = new InMemoryUsageLogStore();
        logs.Rows.Add(Log(2, Now.AddSeconds(-30), totalTokens: 1, totalCost: 1.50));
        var service = Service(logs, Now);

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Cost = 1.5m,
            Period = MinutePeriod(),
        });

        Assert.False(result.Allowed);
        Assert.Equal("cost quota exceeded: 1.5/1.5", result.Message);
    }

    [Fact]
    public async Task AllowedWhenEveryLimitedAggregateIsBelowItsLimit()
    {
        var logs = new InMemoryUsageLogStore();
        logs.Rows.Add(Log(2, Now.AddSeconds(-30), totalTokens: 10, totalCost: 0.5));
        var service = Service(logs, Now);

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 5,
            TotalTokens = 100,
            Cost = 1m,
            Period = MinutePeriod(),
        });

        Assert.True(result.Allowed);
        Assert.Equal(string.Empty, result.Message);
    }

    [Fact]
    public async Task EmptyUsageIsBelowEveryLimit()
    {
        var service = Service(new InMemoryUsageLogStore(), Now);

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 1,
            TotalTokens = 1,
            Cost = 0.01m,
            Period = MinutePeriod(),
        });

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task CalendarWindowReadsTheSystemTimezone()
    {
        var systems = new FakeSystemStore();
        systems.Add("system_general_settings", """{"currency_code":"USD","timezone":"UTC"}""");
        var service = new ApiKeyQuotaService(
            new InMemoryUsageLogStore(),
            systems,
            new TestTimeProvider(Now));

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 1,
            Period = new ApiKeyQuotaPeriodModel
            {
                Type = "calendar_duration",
                CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "day" },
            },
        });

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), result.Window.Start);
        Assert.Equal(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc), result.Window.End);
    }

    [Fact]
    public async Task UnknownTimezoneFallsBackToUtc()
    {
        var systems = new FakeSystemStore();
        systems.Add("system_general_settings", """{"timezone":"Not/AZone"}""");
        var service = new ApiKeyQuotaService(
            new InMemoryUsageLogStore(),
            systems,
            new TestTimeProvider(Now));

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 1,
            Period = new ApiKeyQuotaPeriodModel
            {
                Type = "calendar_duration",
                CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "day" },
            },
        });

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), result.Window.Start);
    }

    [Fact]
    public async Task GeneralSettingsLookupFailureFallsBackToUtc()
    {
        var systems = new FakeSystemStore
        {
            GetFailure = new InvalidOperationException("boom"),
        };
        var service = new ApiKeyQuotaService(
            new InMemoryUsageLogStore(),
            systems,
            new TestTimeProvider(Now));

        var result = await service.CheckApiKeyQuotaAsync(2, new ApiKeyQuotaModel
        {
            Requests = 1,
            Period = new ApiKeyQuotaPeriodModel
            {
                Type = "calendar_duration",
                CalendarDuration = new ApiKeyQuotaCalendarDurationModel { Unit = "day" },
            },
        });

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), result.Window.Start);
    }

    // ------------------------------------------------------------------
    // GeneralSettings decoding (biz/system.go:1544-1568)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"timezone":"Asia/Tokyo"}""", "Asia/Tokyo")]
    [InlineData("""{"timezone":""}""", "")]
    [InlineData("""{}""", "")]
    [InlineData("""null""", "")]
    [InlineData("""{"timezone":null}""", "")]
    [InlineData("""{"TimeZone":"Asia/Tokyo"}""", "Asia/Tokyo")]
    public void GeneralSettingsTimezoneDecodesLikeEncodingJson(string json, string expected)
        => Assert.Equal(expected, ApiKeyQuotaService.GeneralSettingsTimezone(json));

    [Theory]
    [InlineData("""{"currency_code":5,"timezone":"UTC"}""")]
    [InlineData("""[]""")]
    [InlineData("\"text\"")]
    [InlineData("{")]
    public void MalformedGeneralSettingsThrows(string json)
        => Assert.ThrowsAny<JsonException>(() => ApiKeyQuotaService.GeneralSettingsTimezone(json));

    [Fact]
    public void GoFloatDecimalMirrorsNewFromFloat()
    {
        Assert.Equal(0.1m, GoFloatDecimal.FromFloat(0.1));
        Assert.Equal(1.5m, GoFloatDecimal.FromFloat(1.5));
        Assert.Equal(0m, GoFloatDecimal.FromFloat(double.NaN));
        Assert.Equal(0m, GoFloatDecimal.FromFloat(double.PositiveInfinity));
        Assert.Equal(0m, GoFloatDecimal.FromFloat(double.NegativeInfinity));
    }

    // ------------------------------------------------------------------
    // ProfileQuotaUsages / GetQuota (biz/quota.go:129-191)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProfileQuotaUsagesReturnsQuotaCarryingProfilesInOrder()
    {
        var logs = new InMemoryUsageLogStore();
        logs.Rows.Add(Log(2, Now.AddMinutes(-5), totalTokens: 10, totalCost: 0.5));
        logs.Rows.Add(Log(2, Now.AddMinutes(-4), totalTokens: 20, totalCost: 0.25));
        logs.Rows.Add(Log(2, Now.AddSeconds(-30), totalTokens: 5, totalCost: 0.125));
        var service = Service(logs, Now);

        var usages = await service.ProfileQuotaUsagesAsync(new ApiKeyRecord
        {
            Id = 2,
            Profiles = """
                {"activeProfile":"paid","profiles":[
                  {"name":"paid","quota":{"requests":10,"totalTokens":100,"cost":"9","period":{"type":"all_time"}}},
                  {"name":"free"},
                  {"name":"trial","quota":{"requests":1,"period":{"type":"past_duration","pastDuration":{"value":1,"unit":"minute"}}}}
                ]}
                """,
        });

        Assert.Equal(2, usages.Count);

        Assert.Equal("paid", usages[0].ProfileName);
        Assert.Equal("all_time", usages[0].Quota.Period.Type);
        Assert.Null(usages[0].Window.Start);
        Assert.Equal(Now.UtcDateTime, usages[0].Window.End);
        Assert.True(usages[0].Window.EndInclusive);
        Assert.Equal(3, usages[0].Usage.RequestCount);
        Assert.Equal(35, usages[0].Usage.TotalTokens);
        Assert.Equal(0.875m, usages[0].Usage.TotalCost);

        // The past-duration window excludes the two older rows.
        Assert.Equal("trial", usages[1].ProfileName);
        Assert.Equal(new DateTime(2026, 5, 1, 11, 59, 0, DateTimeKind.Utc), usages[1].Window.Start);
        Assert.Equal(1, usages[1].Usage.RequestCount);
        Assert.Equal(5, usages[1].Usage.TotalTokens);
        Assert.Equal(0.125m, usages[1].Usage.TotalCost);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"profiles":null}""")]
    [InlineData("""{"profiles":[]}""")]
    public async Task ProfileQuotaUsagesWithoutProfilesIsEmpty(string? json)
    {
        var service = Service(new InMemoryUsageLogStore(), Now);

        var usages = await service.ProfileQuotaUsagesAsync(new ApiKeyRecord
        {
            Id = 2,
            Profiles = json,
        });

        Assert.Empty(usages);
    }

    [Fact]
    public async Task ProfileQuotaUsagesPropagatesTheWindowError()
    {
        var service = Service(new InMemoryUsageLogStore(), Now);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ProfileQuotaUsagesAsync(new ApiKeyRecord
            {
                Id = 2,
                Profiles = """
                    {"profiles":[{"name":"p","quota":{"period":{"type":"weekly"}}}]}
                    """,
            }));

        Assert.Equal("unknown period.type: weekly", error.Message);
    }

    [Fact]
    public async Task GetQuotaWithANilQuotaReturnsTheZeroResult()
    {
        var service = Service(new InMemoryUsageLogStore(), Now);

        var result = await service.GetQuotaAsync(2, null);

        Assert.Null(result.Window.Start);
        Assert.Null(result.Window.End);
        Assert.False(result.Window.EndInclusive);
        Assert.Equal(0, result.Usage.RequestCount);
        Assert.Equal(0, result.Usage.TotalTokens);
        Assert.Equal(0m, result.Usage.TotalCost);
    }

    // -- Helpers -------------------------------------------------------------

    private static ApiKeyQuotaService Service(InMemoryUsageLogStore logs, DateTimeOffset now)
        => new(logs, new FakeSystemStore(), new TestTimeProvider(now));

    private static ApiKeyQuotaPeriodModel MinutePeriod() => new()
    {
        Type = "past_duration",
        PastDuration = new ApiKeyQuotaPastDurationModel { Value = 1, Unit = "minute" },
    };

    private static UsageLogRecord Log(
        long apiKeyId,
        DateTimeOffset createdAt,
        long totalTokens,
        double totalCost) => new()
    {
        ApiKeyId = apiKeyId,
        CreatedAt = createdAt.UtcDateTime,
        UpdatedAt = createdAt.UtcDateTime,
        ModelId = "test-model",
        TotalTokens = totalTokens,
        TotalCost = totalCost,
    };
}

/// <summary>In-memory usage_logs read model with the store's window predicate.</summary>
internal sealed class InMemoryUsageLogStore : IUsageLogStore
{
    public List<UsageLogRecord> Rows { get; } = [];

    public Task<ListPage<UsageLogRecord>> ListAsync(
        UsageLogListQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the quota path");

    public Task<long> CreateAsync(
        UsageLogRecord usageLog,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        usageLog.Id = Rows.Count + 1;
        Rows.Add(usageLog);
        return Task.FromResult(usageLog.Id);
    }

    public Task<UsageLogRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the quota path");

    public Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<UsageLogRecord>>(
            [.. Rows.Where(row => row.RequestId == requestId)]);
    }

    public Task<long> CountByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult((long)Rows.Count(row => Matches(row, apiKeyId, start, end, endInclusive)));
    }

    public Task<UsageAggregate> AggregateByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        bool needTokens,
        bool needCost,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var rows = Rows.Where(row => Matches(row, apiKeyId, start, end, endInclusive)).ToList();
        var tokens = needTokens ? rows.Sum(row => row.TotalTokens) : 0;
        var cost = needCost ? rows.Sum(row => row.TotalCost ?? 0) : 0;
        return Task.FromResult(new UsageAggregate(tokens, cost));
    }

    public Task<double> SumCostByChannelAsync(
        long channelId,
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows
            .Where(row => row.ChannelId == channelId
                && row.CreatedAt >= since
                && row.CreatedAt < until)
            .Sum(row => row.TotalCost ?? 0));
    }

    private static bool Matches(
        UsageLogRecord row,
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive)
    {
        if (row.ApiKeyId != apiKeyId)
        {
            return false;
        }

        if (start is not null && row.CreatedAt < start.Value)
        {
            return false;
        }

        if (end is not null)
        {
            return endInclusive ? row.CreatedAt <= end.Value : row.CreatedAt < end.Value;
        }

        return true;
    }
}
