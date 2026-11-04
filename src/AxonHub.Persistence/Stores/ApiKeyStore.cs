using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

public enum ApiKeySortField
{
    Id,
    UpdatedAt,
    Name,
}

/// <summary>
/// Port of the ent privacy rules' project/personal-key predicates
/// (internal/scopes/rule_personal_apikey.go, rule_apikey_scope.go). A null
/// filter is the system-bypass path: no predicate is added.
/// </summary>
/// <remarks>
/// <see cref="ProjectId"/> renders <c>project_id = @p</c>.
/// <see cref="PersonalUserId"/> renders the rule's
/// <c>(type != 'personal' OR user_id = @u)</c> — only set for a user who does
/// not own the context project, or for a personal API-key principal.
/// <see cref="ExcludePersonal"/> renders <c>type != 'personal'</c> for
/// non-personal API-key principals.
/// </remarks>
public sealed record ApiKeyAccessFilter
{
    public long? ProjectId { get; init; }

    public long? PersonalUserId { get; init; }

    public bool ExcludePersonal { get; init; }

    public bool IsEmpty => ProjectId is null && PersonalUserId is null && !ExcludePersonal;
}

public sealed record ApiKeyListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? Name { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Key { get; init; }

    public string? KeyContainsFold { get; init; }

    public string? Type { get; init; }

    public IReadOnlyList<string>? TypeIn { get; init; }

    public IReadOnlyList<string>? TypeNotIn { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public long? ProjectId { get; init; }

    public long? UserId { get; init; }

    public ApiKeyListFilter? Not { get; init; }

    public IReadOnlyList<ApiKeyListFilter>? And { get; init; }

    public IReadOnlyList<ApiKeyListFilter>? Or { get; init; }
}

public sealed record ApiKeyListQuery
{
    public ApiKeyListFilter? Where { get; init; }

    /// <summary>Explicit project predicate (entgql @project filter).</summary>
    public long? ProjectId { get; init; }

    /// <summary>Privacy filter (see <see cref="ApiKeyAccessFilter"/>).</summary>
    public ApiKeyAccessFilter? Access { get; init; }

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ApiKeySortField SortField { get; init; } = ApiKeySortField.Id;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Field set of an ent <c>APIKey.UpdateOneID(...)</c> call. The store renders
/// the SET list in the generated builder's fixed order (updated_at first, then
/// key/name/type/status/scopes/profiles/allowed_ips), with cleared JSON columns
/// emitted before the assignments exactly like ent's <c>SetNull</c> list
/// (internal/ent/apikey_update.go:273-330, ent builder writeSetter).
/// </summary>
public sealed record ApiKeyUpdate
{
    public required long Id { get; init; }

    /// <summary>SetKey (rotateAPIKey).</summary>
    public string? Key { get; init; }

    public string? Name { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<string>? AppendScopes { get; init; }

    public bool ClearScopes { get; init; }

    public string? Profiles { get; init; }

    public bool SetProfiles { get; init; }

    public IReadOnlyList<string>? AllowedIps { get; init; }

    public IReadOnlyList<string>? AppendAllowedIps { get; init; }

    public bool ClearAllowedIps { get; init; }

    /// <summary>
    /// Mutation policy predicate (ent <c>WhereP(project_id = ...)</c> from
    /// UserProjectScopeWriteRule / APIKeyProjectScopeWriteRule).
    /// </summary>
    public long? ProjectWritePredicate { get; init; }
}

/// <summary>Persistence seam for the reference <c>api_keys</c> table.</summary>
public interface IApiKeyStore
{
    /// <summary>
    /// Port of ent's RunInTransaction: joins the ambient transaction when one
    /// is open and commits/rolls back its own otherwise.
    /// </summary>
    Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);

    Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>APIKey.Create().Save()</c>: applies the schema defaults
    /// (status enabled, default scopes, empty profiles object, empty IP list)
    /// and inserts through EF Core.
    /// </summary>
    Task<long> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default);

    /// <summary>Privacy-filtered live read (soft-delete predicate included).</summary>
    Task<ApiKeyRecord?> GetByIdAsync(
        long id,
        ApiKeyAccessFilter? access = null,
        CancellationToken cancellationToken = default);

    /// <summary>Port of APIKeyService.GetAPIKey's database lookup (live rows only).</summary>
    Task<ApiKeyRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    Task<int> CountAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        CancellationToken cancellationToken = default);

    /// <summary>ent <c>Query().Where(...).All(ctx)</c>: live rows, id ascending.</summary>
    Task<IReadOnlyList<ApiKeyRecord>> ListAllAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        CancellationToken cancellationToken = default);

    Task<ListPage<ApiKeyRecord>> ListAsync(
        ApiKeyListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Single-row update in ent's SET order, then a full re-read by id (no
    /// soft-delete predicate, no privacy — like the generated UpdateOneID).
    /// Returns null when no row matched (ent NotFound).
    /// </summary>
    Task<ApiKeyRecord?> UpdateAsync(
        ApiKeyUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate update (ent <c>APIKey.Update().Where(...).SetStatus().Save()</c>):
    /// sets updated_at and status, has no soft-delete predicate, and returns the
    /// affected row count.
    /// </summary>
    Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        long? projectWritePredicate,
        CancellationToken cancellationToken = default);

    Task LockProjectForNameAsync(long projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>api_keys</c> table. Writes go through EF Core;
/// reads use Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ApiKeyStore(AxonHubDbContext db) : IApiKeyStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "key", "name", "type",
        "status", "scopes", "profiles", "allowed_ips", "project_id", "user_id"
        """;

    /// <summary>api_keys.scopes default: read_channels, write_requests.</summary>
    private const string DefaultScopes = """["read_channels","write_requests"]""";

    /// <summary>api_keys.profiles default: &amp;objects.APIKeyProfiles{}.</summary>
    private const string DefaultProfiles = """{"activeProfile":"","profiles":null}""";

    /// <summary>api_keys.allowed_ips default: []string{}.</summary>
    private const string DefaultAllowedIps = "[]";

    public async Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // ent's RunInTransaction reuses the enclosing logical transaction and
        // only opens one for a standalone call.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        return RunInTransactionAsync(
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<long> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        if (apiKey.CreatedAt == default)
        {
            apiKey.CreatedAt = DateTime.UtcNow;
        }

        if (apiKey.UpdatedAt == default)
        {
            apiKey.UpdatedAt = apiKey.CreatedAt;
        }

        apiKey.Status = string.IsNullOrEmpty(apiKey.Status) ? "enabled" : apiKey.Status;
        apiKey.Scopes ??= DefaultScopes;
        apiKey.Profiles ??= DefaultProfiles;
        apiKey.AllowedIps ??= DefaultAllowedIps;

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return apiKey.Id;
    }

    public async Task<ApiKeyRecord?> GetByIdAsync(
        long id,
        ApiKeyAccessFilter? access = null,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"api_keys\" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"deleted_at\" = 0");
        AppendAccessFilter(sql, access, args, prefix: " AND ");
        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ApiKeyRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"api_keys\" WHERE \"key\" = @Key AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            new CommandDefinition(
                sql,
                new { Key = key },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE ");
        AppendLivePredicate(conditions, includeDeleted);

        var where = BuildFilter(filter, "a", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        AppendAccessFilter(conditions, access, args, prefix: " AND ");

        var sql = "SELECT COUNT(*) FROM \"api_keys\" a" + conditions + ";";
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE \"deleted_at\" = 0");

        var where = BuildFilter(filter, "a", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        AppendAccessFilter(conditions, access, args, prefix: " AND ");

        var sql = "SELECT EXISTS (SELECT 1 FROM \"api_keys\" a" + conditions + ");";
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ApiKeyRecord>> ListAllAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE a.\"deleted_at\" = 0");

        var where = BuildFilter(filter, "a", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        AppendAccessFilter(conditions, access, args, prefix: " AND ");

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "a." + column.Trim())));
        nodesSql.Append(" FROM \"api_keys\" a").Append(conditions)
            .Append(" ORDER BY a.\"id\" ASC;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ApiKeyRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<ListPage<ApiKeyRecord>> ListAsync(
        ApiKeyListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "a", args);
        var conditions = new StringBuilder(" WHERE a.\"deleted_at\" = 0");
        if (query.ProjectId is not null)
        {
            conditions.Append(" AND a.\"project_id\" = ").Append(args.Add(query.ProjectId.Value));
        }

        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        AppendAccessFilter(conditions, query.Access, args, prefix: " AND ");

        var sortColumn = query.SortField switch
        {
            ApiKeySortField.UpdatedAt => "updated_at",
            ApiKeySortField.Name => "name",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"api_keys\" a";
        var countSql = "SELECT COUNT(*) " + from + conditions + ";";
        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "a." + column.Trim())));
        nodesSql.Append(from).Append(conditions);
        ConnectionSql.AppendCursor(
            nodesSql,
            "a",
            "id",
            sortColumn,
            query.After,
            query.Before,
            direction,
            args);
        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("a", "id", sortColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        var nodes = (await connection.QueryAsync<ApiKeyRecord>(
                new CommandDefinition(
                    nodesSql.ToString(),
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

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

        return new ListPage<ApiKeyRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<ApiKeyRecord?> UpdateAsync(
        ApiKeyUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();
        var nulls = new List<string>(2);
        if (update.ClearScopes)
        {
            nulls.Add("\"scopes\" = NULL");
        }

        if (update.ClearAllowedIps)
        {
            nulls.Add("\"allowed_ips\" = NULL");
        }

        // Ent's generated sqlSave applies the fields in schema order:
        // updated_at, key, name, type, status, scopes, profiles, allowed_ips.
        var sets = new List<SetAssignment>(8)
        {
            new("\"updated_at\"", args.Add(DateTime.UtcNow)),
        };
        if (update.Key is not null)
        {
            sets.Add(new("\"key\"", args.Add(update.Key)));
        }

        if (update.Name is not null)
        {
            sets.Add(new("\"name\"", args.Add(update.Name)));
        }

        if (update.Status is not null)
        {
            sets.Add(new("\"status\"", args.Add(update.Status)));
        }

        if (update.Scopes is not null)
        {
            sets.Add(new("\"scopes\"", JsonCast(args.Add(SerializeStringArray(update.Scopes)))));
        }

        if (update.SetProfiles)
        {
            sets.Add(new("\"profiles\"", JsonCast(args.Add(update.Profiles))));
        }

        if (update.AllowedIps is not null)
        {
            sets.Add(new("\"allowed_ips\"", JsonCast(args.Add(SerializeStringArray(update.AllowedIps)))));
        }

        // ent renders json Append as a modifier that calls UpdateBuilder.Set
        // with a CASE expression: it replaces a plain Set of the same column in
        // place (dropping that assignment) or appends after the other columns.
        if (update.AppendScopes is not null)
        {
            ApplyAppend(sets, "\"scopes\"", update.AppendScopes, args);
        }

        if (update.AppendAllowedIps is not null)
        {
            ApplyAppend(sets, "\"allowed_ips\"", update.AppendAllowedIps, args);
        }

        var assignments = new List<string>(nulls.Count + sets.Count);
        assignments.AddRange(nulls);
        assignments.AddRange(sets.Select(static set => set.Column + " = " + set.Expression));

        var sql = new StringBuilder("UPDATE \"api_keys\" SET ")
            .Append(string.Join(", ", assignments))
            .Append(" WHERE \"id\" = ").Append(args.Add(update.Id));
        if (update.ProjectWritePredicate is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(update.ProjectWritePredicate.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            return null;
        }

        // The generated UpdateOneID re-selects every column by id: no privacy
        // predicate and no soft-delete predicate (C-range difference note).
        var reread = "SELECT " + Columns + " FROM \"api_keys\" WHERE \"id\" = @Id;";
        return await connection.QuerySingleAsync<ApiKeyRecord>(
            new CommandDefinition(
                reread,
                new { Id = update.Id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        long? projectWritePredicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }

        var args = new SqlArguments();
        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = args.Add(ids[i]);
        }

        // Aggregate updates have no soft-delete predicate (the interceptor only
        // touches queries) and default updated_at before status.
        var sql = new StringBuilder("UPDATE \"api_keys\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(status))
            .Append(" WHERE \"id\" IN (").Append(string.Join(", ", parameters)).Append(')');
        if (projectWritePredicate is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectWritePredicate.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task LockProjectForNameAsync(long projectId, CancellationToken cancellationToken = default)
    {
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT \"id\" FROM \"projects\" WHERE \"id\" = @ProjectId FOR UPDATE;",
            new { ProjectId = projectId },
            transaction: db.CurrentDbTransaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private sealed record SetAssignment(string Column, string Expression);

    private static string JsonCast(string parameter) => "CAST(" + parameter + " AS jsonb)";

    private static void ApplyAppend(
        List<SetAssignment> sets,
        string column,
        IReadOnlyList<string> elems,
        SqlArguments args)
    {
        // sqljson.Append (Postgres): CASE WHEN "col" IS NULL OR
        // "col" = 'null'::jsonb THEN $1 ELSE "col" || $2 END, where both
        // arguments are the marshaled elems array.
        var path = column.Trim('"');
        var take = JsonCast(args.Add(SerializeStringArray(elems)));
        var concat = JsonCast(args.Add(SerializeStringArray(elems)));
        var expression =
            "CASE WHEN \"" + path + "\" IS NULL OR \"" + path + "\" = 'null'::jsonb THEN "
            + take + " ELSE \"" + path + "\" || " + concat + " END";

        for (var i = 0; i < sets.Count; i++)
        {
            if (string.Equals(sets[i].Column, column, StringComparison.Ordinal))
            {
                // UpdateBuilder.Set overwrites the value in the original slot.
                sets[i] = new SetAssignment(column, expression);
                return;
            }
        }

        sets.Add(new SetAssignment(column, expression));
    }

    private static string SerializeStringArray(IReadOnlyList<string> values)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(System.Text.Json.JsonSerializer.Serialize(values[i]));
        }

        return builder.Append(']').ToString();
    }

    private static void AppendLivePredicate(StringBuilder sql, bool includeDeleted)
    {
        if (includeDeleted)
        {
            sql.Append("1 = 1");
        }
        else
        {
            sql.Append("a.\"deleted_at\" = 0");
        }
    }

    private static void AppendAccessFilter(
        StringBuilder sql,
        ApiKeyAccessFilter? access,
        SqlArguments args,
        string prefix)
    {
        if (access is null || access.IsEmpty)
        {
            return;
        }

        var predicates = new List<string>(3);
        if (access.ProjectId is not null)
        {
            predicates.Add("\"project_id\" = " + args.Add(access.ProjectId.Value));
        }

        if (access.PersonalUserId is not null)
        {
            predicates.Add("(\"type\" <> 'personal' OR \"user_id\" = "
                + args.Add(access.PersonalUserId.Value) + ")");
        }

        if (access.ExcludePersonal)
        {
            predicates.Add("\"type\" <> 'personal'");
        }

        foreach (var predicate in predicates)
        {
            sql.Append(prefix).Append(predicate);
        }
    }

    private static string? BuildFilter(ApiKeyListFilter? filter, string alias, SqlArguments args)
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

        if (filter.NameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(predicates, alias, "name", "LIKE", filter.NameContainsFold, args);
        }

        if (filter.Key is not null)
        {
            predicates.Add(alias + ".\"key\" = " + args.Add(filter.Key));
        }

        if (filter.KeyContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(predicates, alias, "key", "LIKE", filter.KeyContainsFold, args);
        }

        if (filter.Type is not null)
        {
            predicates.Add(alias + ".\"type\" = " + args.Add(filter.Type));
        }

        if (filter.TypeIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "type", filter.TypeIn, args);
        }

        if (filter.TypeNotIn is not null)
        {
            if (filter.TypeNotIn.Count == 0)
            {
                predicates.Add("TRUE");
            }
            else
            {
                var parameters = filter.TypeNotIn
                    .Select(value => args.Add(value))
                    .ToArray();
                predicates.Add(
                    alias + ".\"type\" NOT IN (" + string.Join(", ", parameters) + ")");
            }
        }

        if (filter.Status is not null)
        {
            predicates.Add(alias + ".\"status\" = " + args.Add(filter.Status));
        }

        if (filter.StatusIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.StatusNotIn is not null)
        {
            if (filter.StatusNotIn.Count == 0)
            {
                predicates.Add("TRUE");
            }
            else
            {
                var parameters = filter.StatusNotIn
                    .Select(value => args.Add(value))
                    .ToArray();
                predicates.Add(
                    alias + ".\"status\" NOT IN (" + string.Join(", ", parameters) + ")");
            }
        }

        if (filter.ProjectId is not null)
        {
            predicates.Add(alias + ".\"project_id\" = " + args.Add(filter.ProjectId.Value));
        }

        if (filter.UserId is not null)
        {
            predicates.Add(alias + ".\"user_id\" = " + args.Add(filter.UserId.Value));
        }

        if (filter.Not is not null && BuildFilter(filter.Not, alias, args) is { } not)
        {
            predicates.Add("NOT (" + not + ")");
        }

        if (filter.And is not null)
        {
            var and = filter.And.Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null).ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or.Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null).ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }
}
