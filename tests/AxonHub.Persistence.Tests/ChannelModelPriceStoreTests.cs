using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Behavioural tests for the ported <c>channel_model_prices</c> store and the
/// synchronous pricing seam (internal/server/biz/channel_price.go:
/// preloadModelPrices and the enabled-channel lookup in computeUsageCost).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelModelPriceStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string PriceJson =
        """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"0.03"}}]}""";

    [Fact]
    public async Task CreateAndListByChannelRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-roundtrip"));
        var price = NewPrice(channelId, "gpt-4o", "ref-roundtrip");
        var id = await store.CreateAsync(price);

        Assert.True(id > 0);

        price.Id = id;
        var actual = Assert.Single(await store.ListByChannelAsync(channelId));

        RecordAssert.Equivalent(price, actual);
    }

    [Fact]
    public async Task ListByChannelSkipsSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-deleted"));
        var liveId = await store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-live"));
        var deletedId = await store.CreateAsync(NewPrice(channelId, "gpt-4o-mini", "ref-deleted"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channel_model_prices\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var rows = await store.ListByChannelAsync(channelId);

        Assert.Equal(new[] { liveId }, rows.Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task EnabledPricesRequireALiveEnabledChannel()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var enabledId = await channels.CreateAsync(NewChannel("price-enabled"));
        var enabledPrice = await store.CreateAsync(NewPrice(enabledId, "gpt-4o", "ref-enabled"));

        var disabled = NewChannel("price-disabled");
        disabled.Status = "disabled";
        var disabledId = await channels.CreateAsync(disabled);
        await store.CreateAsync(NewPrice(disabledId, "gpt-4o", "ref-disabled"));

        var deletedChannel = NewChannel("price-deleted-channel");
        var deletedChannelId = await channels.CreateAsync(deletedChannel);
        await store.CreateAsync(NewPrice(deletedChannelId, "gpt-4o", "ref-deleted-channel"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedChannelId });
        }

        Assert.Equal(
            new[] { enabledPrice },
            (await store.ListEnabledChannelPricesAsync(enabledId)).Select(row => row.Id).ToArray());
        Assert.Empty(await store.ListEnabledChannelPricesAsync(disabledId));
        Assert.Empty(await store.ListEnabledChannelPricesAsync(deletedChannelId));
        Assert.Empty(await store.ListEnabledChannelPricesAsync(999_999));
    }

    [Fact]
    public async Task ReferenceIdIsGloballyUnique()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var firstChannel = await channels.CreateAsync(NewChannel("price-ref-1"));
        var secondChannel = await channels.CreateAsync(NewChannel("price-ref-2"));

        await store.CreateAsync(NewPrice(firstChannel, "gpt-4o", "ref-shared"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewPrice(secondChannel, "gpt-4o", "ref-shared")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    [Fact]
    public async Task LiveModelPricePerChannelIsUnique()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-unique-model"));

        await store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-1"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-2")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    [Fact]
    public async Task SyncSeamReadsPricesAndSystemValues()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        long channelId;

        await using (var context = db.Factory.CreateDbContext())
        {
            var channels = new ChannelStore(context);
            var store = new ChannelModelPriceStore(context);
            var systems = new SystemStore(context);

            channelId = await channels.CreateAsync(NewChannel("price-seam"));
            await store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-seam"));
            await systems.CreateAsync(new SystemRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                Key = "system_inject_usage_cost",
                Value = "true",
            });
        }

        var source = new PostgresChannelPricingSource(db.Factory);

        var price = Assert.Single(source.ListEnabledChannelPrices(channelId));
        Assert.Equal("ref-seam", price.ReferenceId);
        Assert.Equal("true", source.GetSystemValue("system_inject_usage_cost"));
        Assert.Null(source.GetSystemValue("system_missing"));
    }

    private static ChannelRecord NewChannel(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Type = "openai",
        BaseUrl = "https://api.example.com/v1",
        Name = name,
        Status = "enabled",
        Credentials = """{"api_key":"sk-test"}""",
        DisabledApiKeys = "[]",
        SupportedModels = """["gpt-4o"]""",
        ManualModels = "[]",
        Tags = "[]",
        Settings = "{}",
        Endpoints = """["chat"]""",
    };

    private static ChannelModelPriceRecord NewPrice(long channelId, string modelId, string referenceId)
        => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ModelId = modelId,
            Price = PriceJson,
            ReferenceId = referenceId,
            ChannelId = channelId,
        };
}
