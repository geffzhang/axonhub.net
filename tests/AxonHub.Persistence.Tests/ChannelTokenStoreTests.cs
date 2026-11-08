using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the refresh path's persistence seam: the credentials read, the
/// optimistic write guarded by <c>updated_at</c>
/// (<c>UpdateOneID(...).Where(UpdatedAtEQ(...)).SetCredentials(...)</c>,
/// channel_llm.go:1669-1698), the reload on a stale predicate, and the
/// SoftDeleteMixin predicate.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelTokenStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task GetReturnsTypeCredentialsAndTimestamp()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        var id = await CreateChannelAsync(db, """{"apiKey":"legacy"}""");

        var store = new ChannelTokenStore(db.Factory);
        var row = await store.GetAsync(id);

        Assert.NotNull(row);
        Assert.Equal(id, row.Id);
        Assert.Equal("codex", row.Type);
        Assert.Equal("""{"apiKey":"legacy"}""", row.Credentials);
        Assert.Equal(UpdatedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task UpdateWritesWhenTheTimestampStillMatches()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        var id = await CreateChannelAsync(db, """{"apiKey":"legacy"}""");

        var store = new ChannelTokenStore(db.Factory);
        var changedAt = UpdatedAt.AddMinutes(1);

        var result = await store.UpdateCredentialsIfUnchangedAsync(
            id,
            """{"apiKey":"rotated","oauth":{"access_token":"access-1"}}""",
            UpdatedAt,
            changedAt);

        Assert.True(result.Updated);
        Assert.Null(result.Current);

        var row = await store.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal("""{"apiKey":"rotated","oauth":{"access_token":"access-1"}}""", row.Credentials);
        Assert.Equal(changedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task StaleTimestampReloadsTheCurrentRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        var id = await CreateChannelAsync(db, """{"apiKey":"legacy"}""");

        var store = new ChannelTokenStore(db.Factory);

        var result = await store.UpdateCredentialsIfUnchangedAsync(
            id,
            """{"apiKey":"rotated"}""",
            UpdatedAt.AddMinutes(-1),
            UpdatedAt.AddMinutes(1));

        Assert.False(result.Updated);
        Assert.NotNull(result.Current);
        Assert.Equal(id, result.Current!.Id);
        Assert.Equal("codex", result.Current.Type);
        Assert.Equal("""{"apiKey":"legacy"}""", result.Current.Credentials);
        Assert.Equal(UpdatedAt, result.Current.UpdatedAt);

        // The stale write left the row untouched.
        var row = await store.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal("""{"apiKey":"legacy"}""", row.Credentials);
        Assert.Equal(UpdatedAt, row.UpdatedAt);
    }

    [Fact]
    public async Task SoftDeletedRowsAreInvisibleAndUntouchable()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        var id = await CreateChannelAsync(db, """{"apiKey":"legacy"}""");

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        var store = new ChannelTokenStore(db.Factory);
        Assert.Null(await store.GetAsync(id));

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateCredentialsIfUnchangedAsync(
                id,
                """{"apiKey":"rotated"}""",
                UpdatedAt,
                UpdatedAt.AddMinutes(1)));
        Assert.Contains("ent: channel not found", error.Message, StringComparison.Ordinal);
    }

    private static async Task<long> CreateChannelAsync(TestDatabase db, string credentials)
    {
        await using var context = db.Factory.CreateDbContext();
        return await new ChannelStore(context).CreateAsync(new ChannelRecord
        {
            CreatedAt = UpdatedAt.AddHours(-1),
            UpdatedAt = UpdatedAt,
            Type = "codex",
            BaseUrl = "https://api.example.com",
            Name = "token-" + Guid.NewGuid().ToString("N"),
            Status = "enabled",
            Credentials = credentials,
            SupportedModels = "[]",
            Tags = "[]",
        });
    }
}
