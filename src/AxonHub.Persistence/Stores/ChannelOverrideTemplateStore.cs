using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference ChannelOverrideTemplateOrder type.</summary>
public enum ChannelOverrideTemplateSortField
{
    Id,
    UpdatedAt,
}

/// <summary>
/// Subset of ent.ChannelOverrideTemplateWhereInput used by the management
/// frontend (id/idIn, name/nameIn/nameContains/nameContainsFold, and/or/not).
/// The remaining generated predicates are a registered gap (C162).
/// </summary>
public sealed record ChannelOverrideTemplateListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<string>? NameIn { get; init; }

    /// <summary>Port of ent's case-sensitive <c>NameContains</c>.</summary>
    public string? NameContains { get; init; }

    public string? NameContainsFold { get; init; }

    public ChannelOverrideTemplateListFilter? Not { get; init; }

    public IReadOnlyList<ChannelOverrideTemplateListFilter>? And { get; init; }

    public IReadOnlyList<ChannelOverrideTemplateListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for channel override templates.</summary>
public sealed record ChannelOverrideTemplateListQuery
{
    public ChannelOverrideTemplateListFilter? Where { get; init; }

    public ChannelOverrideTemplateSortField SortField { get; init; } =
        ChannelOverrideTemplateSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }

    /// <summary>
    /// Privacy UserOwnedQueryRule: a non-owner only sees rows whose user_id is
    /// their own; the owner (null) sees every template.
    /// </summary>
    public long? ScopeUserId { get; init; }
}

/// <summary>
/// Fields of ent.ChannelOverrideTemplateUpdateOne that the service sets. The
/// operation strings carry the exact JSON ent stores: "null" for an explicitly
/// set nil slice, an array literal otherwise. A null C# value means the field
/// is absent from the input and is not written.
/// </summary>
public sealed record ChannelOverrideTemplateUpdate
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public bool ClearDescription { get; init; }

    public string? Description { get; init; }

    public string? HeaderOverrideOperations { get; init; }

    public string? AppendHeaderOverrideOperations { get; init; }

    public string? BodyOverrideOperations { get; init; }

    public string? AppendBodyOverrideOperations { get; init; }

    /// <summary>Privacy UserOwnedMutationRule; null for the owner.</summary>
    public long? ScopeUserId { get; init; }
}

/// <summary>One channel's settings rewrite inside Apply/Clear's transaction.</summary>
public sealed record ChannelSettingsWrite(long Id, string Name, string SettingsJson);

/// <summary>
/// Persistence seam for the reference <c>channel_override_templates</c> table
/// and the channel settings writes of ApplyTemplate/ClearTemplates.
/// </summary>
public interface IChannelOverrideTemplateStore
{
    /// <summary>Port of <c>ChannelOverrideTemplate.Get</c> under the query privacy rule.</summary>
    Task<ChannelOverrideTemplateRecord?> GetAsync(
        long id,
        long? scopeUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Port of entgql's ChannelOverrideTemplateQuery.Paginate.</summary>
    Task<ListPage<ChannelOverrideTemplateRecord>> ListAsync(
        ChannelOverrideTemplateListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the ent create builder: every operation column is written, so a
    /// JSON <c>null</c> suppresses the schema default.
    /// </summary>
    /// <exception cref="ChannelOverrideTemplateNameConflictException">
    /// The (user_id, name, deleted_at) unique index rejected the insert.
    /// </exception>
    Task<ChannelOverrideTemplateRecord> CreateAsync(
        ChannelOverrideTemplateRecord template,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateOneID(...).Save: absent fields keep their value, an append
    /// wins over a simultaneous set, and the saved row is returned.
    /// </summary>
    /// <exception cref="ChannelOverrideTemplateNameConflictException">
    /// A rename collided with the (user_id, name, deleted_at) unique index.
    /// </exception>
    Task<ChannelOverrideTemplateRecord?> UpdateAsync(
        ChannelOverrideTemplateUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>Soft delete: sets deleted_at (Unix seconds) and bumps updated_at.</summary>
    Task<bool> DeleteAsync(
        long id,
        long? scopeUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>db.Channel.Query().Where(channel.IDIn(ids...)).All(ctx)</c>,
    /// ordered by id so the response order is deterministic (the reference has
    /// no ORDER BY).
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> ListChannelsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies every settings write in one transaction. Fails the whole batch
    /// with the reference's per-channel wording when a row vanished.
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> UpdateChannelSettingsAsync(
        IReadOnlyList<ChannelSettingsWrite> updates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference maps every PostgreSQL SQLSTATE class 23 failure on template
/// insert/rename to <c>xerrors.DuplicateNameError("Template", name)</c>.
/// </summary>
public sealed class ChannelOverrideTemplateNameConflictException : Exception
{
    public ChannelOverrideTemplateNameConflictException(Exception innerException)
        : base("channel override template name already exists", innerException)
    {
    }
}

/// <summary>
/// Store for the reference <c>channel_override_templates</c> table plus the
/// channel rows ApplyTemplate/ClearTemplates touch. Writes go through EF Core
/// (or Dapper when ent's JSON <c>||</c> append cannot be expressed as an EF
/// setter); reads use Dapper and honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ChannelOverrideTemplateStore(AxonHubDbContext db) : IChannelOverrideTemplateStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "override_parameters", "override_headers", "header_override_operations",
        "body_override_operations", "user_id"
        """;

    private const string ChannelColumns = """
        "id", "created_at", "updated_at", "deleted_at", "type", "base_url", "name",
        "status", "credentials", "disabled_api_keys", "supported_models", "manual_models",
        "auto_sync_supported_models", "auto_sync_model_pattern", "tags", "default_test_model",
        "policies", "settings", "ordering_weight", "error_message", "auto_disabled_at",
        "auto_disable_expires_at", "remark", "endpoints"
        """;

    public async Task<ChannelOverrideTemplateRecord?> GetAsync(
        long id,
        long? scopeUserId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = "SELECT " + Columns
                + " FROM \"channel_override_templates\""
                + " WHERE \"id\" = @Id AND \"deleted_at\" = 0";
            if (scopeUserId is not null)
            {
                sql += " AND \"user_id\" = @ScopeUserId";
            }

            sql += ";";

            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return await connection.QuerySingleOrDefaultAsync<ChannelOverrideTemplateRecord>(
                new CommandDefinition(
                    sql,
                    new { Id = id, ScopeUserId = scopeUserId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<ListPage<ChannelOverrideTemplateRecord>> ListAsync(
        ChannelOverrideTemplateListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = BuildConditions(query, args);

        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) FROM \"channel_override_templates\" t"
            + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "t." + column.Trim())));
        nodesSql.Append(" FROM \"channel_override_templates\" t").Append(conditions);

        var valueColumn = SortValueColumn(query.SortField);

        ConnectionSql.AppendCursor(
            nodesSql,
            "t",
            "id",
            valueColumn,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("t", "id", valueColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var totalCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Entgql returns an empty connection without touching the rows when the
        // requested page size is zero; only the count-based flags survive.
        if (query.First is 0 || query.Last is 0)
        {
            return new ListPage<ChannelOverrideTemplateRecord>(
                [],
                query.First is not null && totalCount > 0,
                query.Last is not null && totalCount > 0,
                totalCount);
        }

        var nodes = (await connection.QueryAsync<ChannelOverrideTemplateRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    public async Task<ChannelOverrideTemplateRecord> CreateAsync(
        ChannelOverrideTemplateRecord template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        db.ChannelOverrideTemplates.Add(template);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return template;
        }
        catch (DbUpdateException ex)
        {
            db.Entry(template).State = EntityState.Detached;

            if (IsConstraintViolation(ex))
            {
                throw new ChannelOverrideTemplateNameConflictException(ex);
            }

            // ent's Save returns the driver error verbatim from Create; the
            // service adds the "failed to create ..." prefix.
            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task<ChannelOverrideTemplateRecord?> UpdateAsync(
        ChannelOverrideTemplateUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();
        var assignments = new List<string>();

        if (update.Name is not null)
        {
            assignments.Add("\"name\" = " + args.Add(update.Name));
        }

        if (update.ClearDescription)
        {
            assignments.Add("\"description\" = NULL");
        }
        else if (update.Description is not null)
        {
            assignments.Add("\"description\" = " + args.Add(update.Description));
        }

        AddOperationsAssignment(
            assignments,
            "header_override_operations",
            update.HeaderOverrideOperations,
            update.AppendHeaderOverrideOperations,
            args);
        AddOperationsAssignment(
            assignments,
            "body_override_operations",
            update.BodyOverrideOperations,
            update.AppendBodyOverrideOperations,
            args);

        assignments.Add("\"updated_at\" = " + args.Add(DateTime.UtcNow));

        var sql = "UPDATE \"channel_override_templates\" SET "
            + string.Join(", ", assignments)
            + " WHERE \"id\" = " + args.Add(update.Id)
            + " AND \"deleted_at\" = 0";
        if (update.ScopeUserId is not null)
        {
            sql += " AND \"user_id\" = " + args.Add(update.ScopeUserId);
        }

        sql += " RETURNING " + Columns + ";";

        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return await connection.QuerySingleOrDefaultAsync<ChannelOverrideTemplateRecord>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsConstraintSqlState(ex.SqlState))
        {
            throw new ChannelOverrideTemplateNameConflictException(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<bool> DeleteAsync(
        long id,
        long? scopeUserId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            // The soft-delete hook rewrites DELETE into an update: the time
            // mixin's UpdateDefault bumps updated_at as well.
            var query = db.ChannelOverrideTemplates
                .Where(row => row.Id == id && row.DeletedAt == 0);
            if (scopeUserId is not null)
            {
                var scope = scopeUserId.Value;
                query = query.Where(row => row.UserId == scope);
            }

            var affected = await query
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.DeletedAt, deletedAt)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);

            return affected > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListChannelsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var args = new SqlArguments();
        var predicates = new List<string?>();
        ConnectionSql.AppendIdIn(predicates, "c", "id", ids, args);

        var sql = "SELECT " + string.Join(", ", ChannelColumns
                .Split(',')
                .Select(static column => "c." + column.Trim()))
            + " FROM \"channels\" c WHERE c.\"deleted_at\" = 0 AND "
            + ConnectionSql.Combine(predicates)
            + " ORDER BY c.\"id\" ASC;";

        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = await connection.QueryAsync<ChannelRecord>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<ChannelRecord>> UpdateChannelSettingsAsync(
        IReadOnlyList<ChannelSettingsWrite> updates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var results = new List<ChannelRecord>(updates.Count);
        var now = DateTime.UtcNow;

        foreach (var update in updates)
        {
            ChannelRecord? row;
            try
            {
                // UpdateOneID(...).SetSettings(...).Save: the SoftDeleteMixin
                // predicate applies and the time mixin stamps updated_at. ent
                // re-selects the saved row, so RETURNING returns the entity
                // the resolver reports.
                row = await connection.QuerySingleOrDefaultAsync<ChannelRecord>(
                    new CommandDefinition(
                        "UPDATE \"channels\" c SET \"settings\" = CAST(@Settings AS jsonb),"
                            + " \"updated_at\" = @Now"
                            + " WHERE c.\"id\" = @Id AND c.\"deleted_at\" = 0"
                            + " RETURNING " + string.Join(", ", ChannelColumns
                                .Split(',')
                                .Select(static column => "c." + column.Trim()))
                            + ";",
                        new { update.Id, Settings = update.SettingsJson, Now = now },
                        transaction: db.CurrentDbTransaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    $"failed to update channel {update.Name}: {ex.Message}",
                    ex);
            }

            if (row is null)
            {
                throw new StoreFailureException(
                    $"failed to update channel {update.Name}: ent: channel not found",
                    new InvalidOperationException("ent: channel not found"));
            }

            results.Add(row);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static string BuildConditions(
        ChannelOverrideTemplateListQuery query,
        SqlArguments args)
    {
        var conditions = new StringBuilder(" WHERE t.\"deleted_at\" = 0");

        if (query.ScopeUserId is { } scope)
        {
            conditions.Append(" AND t.\"user_id\" = ").Append(args.Add(scope));
        }

        var where = BuildFilter(query.Where, "t", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        return conditions.ToString();
    }

    private static string? SortValueColumn(ChannelOverrideTemplateSortField field) => field switch
    {
        ChannelOverrideTemplateSortField.UpdatedAt => "updated_at",
        _ => null,
    };

    private static void AddOperationsAssignment(
        List<string> assignments,
        string column,
        string? setJson,
        string? appendJson,
        SqlArguments args)
    {
        if (appendJson is not null)
        {
            // Generated sqlSave registers SetField first and the sqljson
            // append modifier after it; the modifier's CASE replaces the
            // assigned value, so a simultaneous append wins. Postgres:
            // CASE WHEN col IS NULL OR col::jsonb = 'null'::jsonb
            //      THEN $appended ELSE col || $appended END.
            var appended = args.Add(appendJson);
            assignments.Add(
                "\"" + column + "\" = CASE WHEN \"" + column + "\" IS NULL"
                + " OR \"" + column + "\"::jsonb = 'null'::jsonb"
                + " THEN CAST(" + appended + " AS jsonb)"
                + " ELSE \"" + column + "\" || CAST(" + appended + " AS jsonb) END");
            return;
        }

        if (setJson is not null)
        {
            assignments.Add("\"" + column + "\" = CAST(" + args.Add(setJson) + " AS jsonb)");
        }
    }

    private static ListPage<ChannelOverrideTemplateRecord> BuildPage(
        List<ChannelOverrideTemplateRecord> nodes,
        ChannelOverrideTemplateListQuery query,
        int totalCount)
    {
        var hasNextPage = query.Before is not null;
        var hasPreviousPage = query.After is not null;

        if (query.First is not null && query.First + 1 == nodes.Count)
        {
            hasNextPage = true;
            nodes.RemoveAt(nodes.Count - 1);
        }
        else if (query.Last is not null && query.Last + 1 == nodes.Count)
        {
            hasPreviousPage = true;
            nodes.RemoveAt(nodes.Count - 1);
        }

        if (query.Last is not null)
        {
            nodes.Reverse();
        }

        return new ListPage<ChannelOverrideTemplateRecord>(
            nodes,
            hasNextPage,
            hasPreviousPage,
            totalCount);
    }

    private static string? BuildFilter(
        ChannelOverrideTemplateListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        if (filter.Id is not null)
        {
            predicates.Add(alias + ".\"id\" = " + args.Add(filter.Id.Value));
        }

        if (filter.IdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.Name is not null)
        {
            predicates.Add(alias + ".\"name\" = " + args.Add(filter.Name));
        }

        if (filter.NameIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "name", filter.NameIn, args);
        }

        if (filter.NameContains is not null)
        {
            // ent.Contains is a case-sensitive LIKE without escaping.
            predicates.Add(alias + ".\"name\" LIKE " + args.Add("%" + filter.NameContains + "%"));
        }

        if (filter.NameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "name",
                "LIKE",
                filter.NameContainsFold,
                args);
        }

        if (filter.Not is not null)
        {
            var not = BuildFilter(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or
                .Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }

    private static bool IsConstraintViolation(DbUpdateException exception)
        => exception.GetBaseException() is PostgresException { SqlState: { } sqlState }
            && IsConstraintSqlState(sqlState);

    private static bool IsConstraintSqlState(string? sqlState)
        => sqlState?.StartsWith("23", StringComparison.Ordinal) == true;
}
