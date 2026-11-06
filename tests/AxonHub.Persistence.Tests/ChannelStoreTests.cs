using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChannelStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelStore(context);

        var channel = NewChannel();
        var id = await store.CreateAsync(channel);

        Assert.True(id > 0);

        channel.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(channel, actual);
    }

    [Fact]
    public async Task ListEnabledReturnsOnlyLiveEnabledChannels()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelStore(context);

        var enabled = NewChannel();
        var enabledId = await store.CreateAsync(enabled);

        var disabled = NewChannel();
        disabled.Name = "disabled";
        disabled.Status = "disabled";
        await store.CreateAsync(disabled);

        var deleted = NewChannel();
        deleted.Name = "deleted";
        var deletedId = await store.CreateAsync(deleted);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var live = await store.ListEnabledAsync();

        Assert.Equal(new[] { enabledId }, live.Select(channel => channel.Id).ToArray());
        Assert.Null(await store.GetByIdAsync(deletedId));
    }

    [Fact]
    public async Task UpdateStatusChangesStatusAndErrorMessage()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelStore(context);

        var id = await store.CreateAsync(NewChannel());
        var changedAt = UpdatedAt.AddHours(1);

        var updated = await store.UpdateStatusAsync(id, "disabled", "upstream 500", changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("disabled", actual.Status);
        Assert.Equal("upstream 500", actual.ErrorMessage);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateStatusDoesNotTouchDeletedChannels()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelStore(context);

        var id = await store.CreateAsync(NewChannel());

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = id });
        }

        var updated = await store.UpdateStatusAsync(id, "disabled", "gone", UpdatedAt.AddHours(1));

        Assert.False(updated);
    }

    [Fact]
    public async Task LiveChannelNamesAreUniqueAndSoftDeletedNamesCanBeReused()
    {
        // channels_by_name is unique on (name, deleted_at), exactly like the
        // reference's Ent index: a live duplicate is rejected, but once the old
        // row is soft-deleted its name is free again.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelStore(context);

        var firstId = await store.CreateAsync(NewChannel());

        // EF Core wraps driver failures in DbUpdateException; the SQLSTATE is on
        // the Npgsql inner exception (registered in C011).
        var duplicate = NewChannel();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(duplicate));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = firstId });
        }

        var reusedId = await store.CreateAsync(duplicate);
        Assert.True(reusedId > firstId);
    }

    [Fact]
    public async Task ListEnabledByTypesReturnsOnlyLiveEnabledChannelsOfTheListedTypes()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelStore(context);

        var openAiId = await store.CreateAsync(NewChannel());

        var codex = NewChannel();
        codex.Name = "codex";
        codex.Type = "codex";
        var codexId = await store.CreateAsync(codex);

        var anthropic = NewChannel();
        anthropic.Name = "anthropic";
        anthropic.Type = "anthropic";
        await store.CreateAsync(anthropic);

        var disabled = NewChannel();
        disabled.Name = "disabled";
        disabled.Status = "disabled";
        await store.CreateAsync(disabled);

        var deleted = NewChannel();
        deleted.Name = "deleted";
        deleted.Type = "codex";
        var deletedId = await store.CreateAsync(deleted);

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var live = await store.ListEnabledByTypesAsync(["codex", "openai"]);

        Assert.Equal(
            [openAiId, codexId],
            live.Select(channel => channel.Id).ToArray());
        Assert.Empty(await store.ListEnabledByTypesAsync([]));
    }

    private static ChannelRecord NewChannel() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Type = "openai",
        BaseUrl = "https://api.example.com/v1",
        Name = "primary",
        Status = "enabled",
        Credentials = """{"api_key":"sk-test"}""",
        DisabledApiKeys = """[1,2]""",
        SupportedModels = """["gpt-4o"]""",
        ManualModels = """["gpt-4o-mini"]""",
        AutoSyncSupportedModels = true,
        AutoSyncModelPattern = "gpt-*",
        Tags = """["prod"]""",
        DefaultTestModel = "gpt-4o-mini",
        Policies = """{"rate_limit":10}""",
        Settings = """{"timeout":30}""",
        OrderingWeight = 7,
        ErrorMessage = "none",
        AutoDisabledAt = CreatedAt.AddMinutes(1),
        AutoDisableExpiresAt = CreatedAt.AddMinutes(2),
        Remark = "ported",
        Endpoints = """["chat"]""",
    };
}
