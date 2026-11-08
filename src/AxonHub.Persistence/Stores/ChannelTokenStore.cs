using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>One channel row as the token refresher needs it.</summary>
public sealed record ChannelTokenRow(long Id, string Type, string Credentials, DateTime UpdatedAt);

/// <summary>
/// Outcome of the optimistic credentials write
/// (<c>Channel.UpdateOneID(ch.ID).Where(channel.UpdatedAt(ch.UpdatedAt)).SetCredentials(...).Save</c>,
/// channel_llm.go:1655-1702). <see cref="Current"/> carries the reloaded row
/// only when the predicate matched nothing.
/// </summary>
public sealed record ChannelTokenUpdate(bool Updated, ChannelTokenRow? Current);

/// <summary>
/// Persistence seam for the OAuth token refresher. Unlike <see cref="IChannelStore"/>,
/// the consumer is a singleton grain-facing service, so each call owns a
/// short-lived unit of work.
/// </summary>
public interface IChannelTokenStore
{
    /// <summary>
    /// Reads the live row's type, credentials document and <c>updated_at</c>;
    /// null when the row does not exist or is soft-deleted
    /// (<c>client.Channel.Query().Where(channel.ID(id)).Only(ctx)</c>).
    /// </summary>
    Task<ChannelTokenRow?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the reference's optimistic refresh write
    /// (channel_llm.go:1655-1667): set <c>credentials</c> and <c>updated_at</c>
    /// only when the row still carries <paramref name="expectedUpdatedAt"/>.
    /// A zero-row update reloads the current row, mirroring ent's NotFound
    /// branch, and reports it through <see cref="ChannelTokenUpdate.Current"/>.
    /// </summary>
    /// <exception cref="StoreFailureException">
    /// The reload fails or the row no longer exists, matching the reference's
    /// <c>client.Channel.Query().Only</c> error.
    /// </exception>
    Task<ChannelTokenUpdate> UpdateCredentialsIfUnchangedAsync(
        long id,
        string credentials,
        DateTime expectedUpdatedAt,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the credential refresh path (channel_llm.go:1642-1702). EF Core
/// performs the conditional write; the failure reload uses a tracked read like
/// the reference's <c>Only(ctx)</c>.
/// </summary>
public sealed class ChannelTokenStore(IAxonHubDbContextFactory dbFactory) : IChannelTokenStore
{
    public async Task<ChannelTokenRow?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = dbFactory.CreateDbContext();
        try
        {
            return await db.Channels
                .AsNoTracking()
                .Where(row => row.Id == id && row.DeletedAt == 0)
                .Select(row => new ChannelTokenRow(row.Id, row.Type, row.Credentials, row.UpdatedAt))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                $"failed to get current channel {id}: {ex.Message}",
                ex);
        }
    }

    public async Task<ChannelTokenUpdate> UpdateCredentialsIfUnchangedAsync(
        long id,
        string credentials,
        DateTime expectedUpdatedAt,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        await using var db = dbFactory.CreateDbContext();

        int updated;
        try
        {
            // UpdateOneID applies the SoftDeleteMixin predicate; the Where
            // clause is the reference's optimistic concurrency guard.
            updated = await db.Channels
                .Where(row => row.Id == id
                    && row.DeletedAt == 0
                    && row.UpdatedAt == expectedUpdatedAt)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.Credentials, credentials)
                        .SetProperty(row => row.UpdatedAt, updatedAt),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                $"failed to update credentials for channel {id}: {ex.Message}",
                ex);
        }

        if (updated > 0)
        {
            return new ChannelTokenUpdate(true, null);
        }

        // ent's NotFound branch: the caller compares the current row with the
        // snapshot it tried to write and either retries or reports a conflict.
        var current = await GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new StoreFailureException(
                $"failed to get current channel {id}: ent: channel not found",
                new InvalidOperationException("ent: channel not found"));

        return new ChannelTokenUpdate(false, current);
    }
}
