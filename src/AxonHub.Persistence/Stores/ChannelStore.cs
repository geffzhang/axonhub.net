using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>channels</c> table.</summary>
public interface IChannelStore
{
    Task<long> CreateAsync(ChannelRecord channel, CancellationToken cancellationToken = default);

    Task<ChannelRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Live (not soft-deleted) channels whose status is enabled.</summary>
    Task<IReadOnlyList<ChannelRecord>> ListEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The largest <c>updated_at</c> across every row, including soft-deleted
    /// ones, or <see langword="null"/> when the table is empty
    /// (<c>Channel.Query().Order(ent.Desc(FieldUpdatedAt)).First(SkipSoftDelete)</c>,
    /// channel.go:242-247). This is the enabled-channel cache fingerprint: a
    /// soft delete bumps <c>updated_at</c>, so deletions are detected too.
    /// </summary>
    Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the quota collection query
    /// (<c>channel.StatusEQ(enabled)</c> AND <c>channel.TypeIn(types...)</c>).
    /// The reference leaves ordering unspecified; id ASC keeps the port
    /// deterministic without changing which rows are returned.
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> ListEnabledByTypesAsync(
        IReadOnlyCollection<string> types,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateStatusAsync(
        long id,
        string status,
        string? errorMessage,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>channels</c> table. Writes go through EF Core
/// (change tracking + <c>SaveChanges</c>); reads use Dapper. Both honour the
/// reference's SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ChannelStore(AxonHubDbContext db) : IChannelStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "type", "base_url", "name",
        "status", "credentials", "disabled_api_keys", "supported_models", "manual_models",
        "auto_sync_supported_models", "auto_sync_model_pattern", "tags", "default_test_model",
        "policies", "settings", "ordering_weight", "error_message", "auto_disabled_at",
        "auto_disable_expires_at", "remark", "endpoints"
        """;

    public async Task<long> CreateAsync(ChannelRecord channel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        db.Channels.Add(channel);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return channel.Id;
    }

    public async Task<ChannelRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"channels\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ChannelRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        // Mirrors biz.reloadEnabledChannels: status = enabled (Ent adds the
        // SoftDeleteMixin predicate) ordered by ordering_weight DESC. The
        // reference leaves ties unspecified; id ASC keeps the port deterministic
        // without changing which rows are returned.
        var sql = "SELECT " + Columns
            + " FROM \"channels\" WHERE \"deleted_at\" = 0 AND \"status\" = 'enabled'"
            + " ORDER BY \"ordering_weight\" DESC, \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default)
    {
        // No soft-delete predicate: the reference deliberately asks for the
        // latest row including deleted ones so a deletion invalidates the
        // cache (channel.go:242-247). Equal timestamps tie arbitrarily; the
        // value returned is the same either way.
        const string Sql =
            "SELECT \"updated_at\" FROM \"channels\" ORDER BY \"updated_at\" DESC LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DateTime?>(
            new CommandDefinition(
                Sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListEnabledByTypesAsync(
        IReadOnlyCollection<string> types,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(types);
        if (types.Count == 0)
        {
            return [];
        }

        var sql = "SELECT " + Columns
            + " FROM \"channels\" WHERE \"deleted_at\" = 0 AND \"status\" = 'enabled'"
            + " AND \"type\" = ANY(@Types) ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                sql,
                new { Types = types.ToArray() },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<bool> UpdateStatusAsync(
        long id,
        string status,
        string? errorMessage,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var channel = await db.Channels
            .FirstOrDefaultAsync(channel => channel.Id == id && channel.DeletedAt == 0, cancellationToken)
            .ConfigureAwait(false);

        if (channel is null)
        {
            return false;
        }

        channel.Status = status;
        channel.ErrorMessage = errorMessage;
        channel.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
