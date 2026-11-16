using System.Text.Json;
using Dapper;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks ProviderQuotaStatusStore against the reference ent behaviour
/// (internal/ent/schema/provider_quota_status.go, provider_quota.go upsert /
/// error / hard-delete paths): the SoftDeleteMixin read predicate, the
/// conflict upsert that rewrites only the inserted columns, the plain create
/// that trips the unique channel constraint, the two error updates and the
/// hard delete that also removes soft-deleted rows.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ProviderQuotaStatusStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime NextCheckAt = new(2026, 5, 1, 12, 5, 0, DateTimeKind.Utc);
    private static readonly DateTime NextResetAt = new(2026, 5, 1, 16, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldStamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task UpsertInsertsTheRowAndTheConflictUpdateRewritesOnlyTheInsertColumns()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProviderQuotaStatusStore(context);

        await store.UpsertAsync(NewStatus(channelId: 7, accountKey: "acct-1"));

        var inserted = await store.GetByChannelIdAsync(7);
        Assert.NotNull(inserted);
        Assert.True(inserted.Id > 0);
        Assert.Equal("codex", inserted.ProviderType);
        Assert.Equal("warning", inserted.Status);
        Assert.Equal("""{"_limits":[]}""", NormalizeJson(inserted.QuotaData));
        Assert.Equal(NextResetAt, inserted.NextResetAt);
        Assert.True(inserted.Ready);
        Assert.Equal(NextCheckAt, inserted.NextCheckAt);
        Assert.Equal("acct-1", inserted.AccountKey);
        Assert.Equal(7, inserted.ChannelId);
        Assert.True(inserted.CreatedAt > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(inserted.CreatedAt, inserted.UpdatedAt);

        var updatedAt = inserted.UpdatedAt;

        await store.UpsertAsync(new ProviderQuotaStatusRecord
        {
            ChannelId = 7,
            ProviderType = "claudecode",
            Status = "unknown",
            QuotaData = """{"error_count":1}""",
            NextResetAt = null,
            Ready = false,
            NextCheckAt = NextCheckAt.AddMinutes(5),
            AccountKey = null,
        });

        var conflict = await store.GetByChannelIdAsync(7);
        Assert.NotNull(conflict);
        Assert.Equal(inserted.Id, conflict.Id);
        Assert.Equal("claudecode", conflict.ProviderType);
        Assert.Equal("unknown", conflict.Status);
        Assert.Equal("""{"error_count":1}""", NormalizeJson(conflict.QuotaData));
        Assert.Null(conflict.NextResetAt);
        Assert.False(conflict.Ready);
        Assert.Equal(NextCheckAt.AddMinutes(5), conflict.NextCheckAt);
        Assert.Null(conflict.AccountKey);

        // Ent's UpdateNewValues never touches created_at / updated_at, and the
        // port declares no trigger that would.
        Assert.Equal(inserted.CreatedAt, conflict.CreatedAt);
        Assert.Equal(updatedAt, conflict.UpdatedAt);

        var count = await CountRowsAsync(db);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ReadsHonourTheSoftDeletePredicateAndOrderById()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProviderQuotaStatusStore(context);

        await store.UpsertAsync(NewStatus(channelId: 1));
        await store.UpsertAsync(NewStatus(channelId: 2));
        await store.UpsertAsync(NewStatus(channelId: 3));
        await SoftDeleteAsync(db, channelId: 2);

        var rows = await store.ListAllAsync();
        Assert.Equal([1L, 3L], rows.Select(static row => row.ChannelId).ToArray());

        Assert.NotNull(await store.GetByChannelIdAsync(1));
        Assert.Null(await store.GetByChannelIdAsync(2));
        Assert.Null(await store.GetByChannelIdAsync(404));
    }

    [Fact]
    public async Task CreateTripsTheUniqueChannelConstraintEvenForASoftDeletedRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProviderQuotaStatusStore(context);

        await store.CreateAsync(NewStatus(channelId: 5));

        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => store.CreateAsync(NewStatus(channelId: 5)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

        // The unique index covers deleted rows too; the reference create path
        // relies on that to reject a second row after a soft delete.
        await SoftDeleteAsync(db, channelId: 5);

        var afterSoftDelete = await Assert.ThrowsAsync<PostgresException>(
            () => store.CreateAsync(NewStatus(channelId: 5)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, afterSoftDelete.SqlState);
    }

    [Fact]
    public async Task ResetForProviderErrorClearsTheResetAndFlipsTheStatus()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProviderQuotaStatusStore(context);

        await store.UpsertAsync(NewStatus(channelId: 9));
        await SetUpdatedAtAsync(db, channelId: 9, OldStamp);
        var before = await store.GetByChannelIdAsync(9);
        Assert.NotNull(before);
        Assert.Equal(OldStamp, before.UpdatedAt);

        await store.ResetForProviderErrorAsync(
            before.Id,
            "commandcode",
            accountKey: null,
            """{"error_code":"invalid_credentials","error_count":1}""",
            NextCheckAt.AddMinutes(5));

        var after = await store.GetByChannelIdAsync(9);
        Assert.NotNull(after);
        Assert.Equal("commandcode", after.ProviderType);
        Assert.Null(after.AccountKey);
        Assert.Equal("unknown", after.Status);
        Assert.False(after.Ready);
        Assert.Null(after.NextResetAt);
        Assert.Equal(NextCheckAt.AddMinutes(5), after.NextCheckAt);
        Assert.Equal(
            """{"error_code":"invalid_credentials","error_count":1}""",
            NormalizeJson(after.QuotaData));
        Assert.True(after.UpdatedAt > OldStamp);
    }

    [Fact]
    public async Task UpdateErrorKeepsTheStatusAndRewritesTheErrorFields()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProviderQuotaStatusStore(context);

        await store.UpsertAsync(NewStatus(channelId: 11));
        await SetUpdatedAtAsync(db, channelId: 11, OldStamp);
        var before = await store.GetByChannelIdAsync(11);
        Assert.NotNull(before);
        Assert.Equal(OldStamp, before.UpdatedAt);

        await store.UpdateErrorAsync(
            before.Id,
            "acct-2",
            """{"error_code":"check_failed","error_count":2}""",
            NextCheckAt.AddMinutes(10));

        var after = await store.GetByChannelIdAsync(11);
        Assert.NotNull(after);
        Assert.Equal("acct-2", after.AccountKey);
        Assert.Equal(NextCheckAt.AddMinutes(10), after.NextCheckAt);
        Assert.Equal("""{"error_code":"check_failed","error_count":2}""",
            NormalizeJson(after.QuotaData));
        // The ordinary error update touches neither the overall state nor the
        // reset time.
        Assert.Equal(before.ProviderType, after.ProviderType);
        Assert.Equal(before.Status, after.Status);
        Assert.True(after.Ready);
        Assert.Equal(before.NextResetAt, after.NextResetAt);
        Assert.True(after.UpdatedAt > OldStamp);
    }

    [Fact]
    public async Task HardDeleteRemovesLiveAndSoftDeletedRowsForTheChannel()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ProviderQuotaStatusStore(context);

        await store.UpsertAsync(NewStatus(channelId: 21));
        await store.UpsertAsync(NewStatus(channelId: 22));
        await SoftDeleteAsync(db, channelId: 21);

        Assert.Equal(1, await store.HardDeleteByChannelIdAsync(21));
        Assert.Equal(1, await store.HardDeleteByChannelIdAsync(22));
        Assert.Equal(0, await store.HardDeleteByChannelIdAsync(404));
        Assert.Empty(await store.ListAllAsync());
    }

    private static ProviderQuotaStatusRecord NewStatus(
        long channelId,
        string accountKey = "") => new()
        {
            ChannelId = channelId,
            ProviderType = "codex",
            Status = "warning",
            QuotaData = """{"_limits":[]}""",
            NextResetAt = NextResetAt,
            Ready = true,
            NextCheckAt = NextCheckAt,
            AccountKey = accountKey,
        };

    private static async Task SoftDeleteAsync(TestDatabase db, long channelId)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE \"provider_quota_status\" SET \"deleted_at\" = @DeletedAt "
            + "WHERE \"channel_id\" = @ChannelId;",
            new { DeletedAt = 1_700_000_000L, ChannelId = channelId });
    }

    private static async Task SetUpdatedAtAsync(
        TestDatabase db,
        long channelId,
        DateTime updatedAt)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE \"provider_quota_status\" SET \"updated_at\" = @UpdatedAt "
            + "WHERE \"channel_id\" = @ChannelId;",
            new { UpdatedAt = updatedAt, ChannelId = channelId });
    }

    private static async Task<long> CountRowsAsync(TestDatabase db)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"provider_quota_status\";");
    }

    /// <summary>jsonb round-trips through Npgsql's canonical text form.</summary>
    private static string NormalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement);
    }
}
