using System.Text.Json.Nodes;
using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Behavioural tests for the stored-channel read used by the same-channel retry
/// guard (internal/server/orchestrator/retry_channel_refresh.go, #2545): only a
/// live enabled row is returned; disabled, soft-deleted and unknown ids are
/// confirmed misses, while a failed read is reported separately.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelRefreshSourceTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [Fact]
    public async Task ReadsOnlyLiveEnabledChannels()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        long enabledId;
        long disabledId;
        long deletedId;

        await using (var context = db.Factory.CreateDbContext())
        {
            var channels = new ChannelStore(context);
            enabledId = await channels.CreateAsync(NewChannel("refresh-enabled"));
            disabledId = await channels.CreateAsync(NewChannel("refresh-disabled"));
            deletedId = await channels.CreateAsync(NewChannel("refresh-deleted"));

            await channels.UpdateStatusAsync(
                disabledId,
                "disabled",
                null,
                CreatedAt);
        }

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var source = new PostgresChannelRefreshSource(db.Factory);

        var enabled = source.GetEnabledChannel(enabledId);
        Assert.False(enabled.Failed);
        Assert.NotNull(enabled.Channel);
        Assert.Equal(enabledId, enabled.Channel.Id);
        Assert.Equal("refresh-enabled", enabled.Channel.Name);
        Assert.Equal("openai", enabled.Channel.Type);
        Assert.Equal(
            "sk-refresh",
            JsonNode.Parse(enabled.Channel.Credentials)!["api_key"]!.GetValue<string>());

        foreach (var missingId in new[] { disabledId, deletedId, 404_404 })
        {
            var missing = source.GetEnabledChannel(missingId);
            Assert.False(missing.Failed);
            Assert.Null(missing.Channel);
        }
    }

    private static ChannelRecord NewChannel(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        Type = "openai",
        BaseUrl = "https://api.example.com/v1",
        Name = name,
        Status = "enabled",
        Credentials = """{"api_key":"sk-refresh"}""",
        DisabledApiKeys = "[]",
        SupportedModels = """["gpt-4o"]""",
        ManualModels = "[]",
        Tags = "[]",
        Settings = "{}",
        Endpoints = """[{"api_format":"openai/chat_completions"}]""",
    };
}
