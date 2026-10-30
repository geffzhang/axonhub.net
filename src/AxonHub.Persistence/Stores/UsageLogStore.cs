using Dapper;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>usage_logs</c> table.</summary>
public interface IUsageLogStore
{
    Task<long> CreateAsync(UsageLogRecord usageLog, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>usage_logs</c> table. Writes go through EF Core;
/// reads use Dapper.
/// </summary>
public sealed class UsageLogStore(AxonHubDbContext db) : IUsageLogStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "api_key_id", "model_id", "prompt_tokens",
        "completion_tokens", "total_tokens", "prompt_audio_tokens",
        "prompt_cached_tokens", "prompt_write_cached_tokens",
        "prompt_write_cached_tokens_5m", "prompt_write_cached_tokens_1h",
        "completion_audio_tokens", "completion_reasoning_tokens",
        "completion_accepted_prediction_tokens",
        "completion_rejected_prediction_tokens", "source", "format", "total_cost",
        "cost_items", "cost_price_reference_id", "channel_id", "project_id",
        "request_id"
        """;

    public async Task<long> CreateAsync(UsageLogRecord usageLog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageLog);

        db.UsageLogs.Add(usageLog);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return usageLog.Id;
    }

    public async Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"usage_logs\" WHERE \"request_id\" = @RequestId ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UsageLogRecord>(
            new CommandDefinition(
                sql,
                new { RequestId = requestId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }
}
