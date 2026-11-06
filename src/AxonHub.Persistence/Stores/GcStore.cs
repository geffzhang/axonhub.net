using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>The payload column group a GC body pass rewrites.</summary>
public enum GcPayloadKind
{
    RequestBodies,
    ResponseBodies,
    ResponseChunks,
}

/// <summary>Row of <c>requests</c> touched by the GC worker.</summary>
public sealed class GcRequestRef
{
    public long Id { get; set; }

    public long ProjectId { get; set; }

    public long? DataStorageId { get; set; }
}

/// <summary>Row of <c>request_executions</c> touched by the GC worker.</summary>
public sealed class GcExecutionRef
{
    public long Id { get; set; }

    public long RequestId { get; set; }

    public long ProjectId { get; set; }

    public long? DataStorageId { get; set; }
}

/// <summary>
/// Persistence seam for internal/server/gc (gc.go, gc_bodies.go). Every query
/// mirrors the reference's ent query shape at f272549f: the retained-trace
/// exclusion, ascending id batching and the body-payload placeholder predicate
/// (gc_bodies.go:314-397). The GC worker runs with a system bypass and
/// <c>SkipSoftDelete</c>; these tables carry no soft-delete column in the port
/// schema, so plain SQL is equivalent.
/// </summary>
public interface IGcStore
{
    /// <summary>
    /// gc.go:265-283: old executions whose request has no retained trace,
    /// ordered by id ascending.
    /// </summary>
    Task<IReadOnlyList<GcExecutionRef>> ListOldExecutionsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:312-330: old requests with no retained trace.</summary>
    Task<IReadOnlyList<GcRequestRef>> ListOldRequestsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:501-527: old usage logs whose request has no retained trace.</summary>
    Task<IReadOnlyList<long>> ListOldUsageLogIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:558-585: old threads whose status is not retained.</summary>
    Task<IReadOnlyList<long>> ListOldThreadIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:613-640: old traces whose status is not retained.</summary>
    Task<IReadOnlyList<long>> ListOldTraceIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int> DeleteExecutionsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    Task<int> DeleteRequestsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    Task<int> DeleteUsageLogsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    Task<int> DeleteThreadsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    Task<int> DeleteTracesAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:684-700 preview count (retained traces excluded).</summary>
    Task<long> CountOldRequestsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>gc_bodies.go:61-76 preview execution count.</summary>
    Task<long> CountOldExecutionsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:731-750 preview usage-log count.</summary>
    Task<long> CountOldUsageLogsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// gc_bodies.go:142-187: request rows inside the window that still carry a
    /// payload for <paramref name="kind"/>, skipping ids whose external delete
    /// failed earlier in the pass.
    /// </summary>
    Task<IReadOnlyList<GcRequestRef>> ListRequestPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryStorageId,
        IReadOnlyList<long> skipIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>gc_bodies.go:189-235: execution counterpart of the above.</summary>
    Task<IReadOnlyList<GcExecutionRef>> ListExecutionPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryStorageId,
        IReadOnlyList<long> skipIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// gc_bodies.go:237-257: one UPDATE per batch; request bodies become
    /// <c>{}</c> and headers are cleared, response bodies become <c>{}</c> with
    /// cleared headers, chunks become <c>[]</c>. TimeMixin bumps updated_at.
    /// </summary>
    Task<int> ClearRequestPayloadsAsync(
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>gc_bodies.go:259-279: execution counterpart of the above.</summary>
    Task<int> ClearExecutionPayloadsAsync(
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>gc.go:710-746: VACUUM / VACUUM FULL on PostgreSQL.</summary>
    Task VacuumAsync(bool full, CancellationToken cancellationToken = default);
}

/// <summary>PostgreSQL implementation of <see cref="IGcStore"/>.</summary>
public sealed class GcStore(AxonHubDbContext db) : IGcStore
{
    private const string RetainedTraceOfRequest = """
        NOT EXISTS (
            SELECT 1 FROM "traces" t
            WHERE t."id" = r."trace_id" AND t."status" = 'retained'
        )
        """;

    private const string RetainedTraceOfChild = """
        NOT EXISTS (
            SELECT 1 FROM "requests" r
            JOIN "traces" t ON t."id" = r."trace_id"
            WHERE r."id" = {0}."request_id" AND t."status" = 'retained'
        )
        """;

    public async Task<IReadOnlyList<GcExecutionRef>> ListOldExecutionsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT e."id", e."request_id", e."project_id", e."data_storage_id"
            FROM "request_executions" e
            WHERE e."created_at" < @Cutoff
              AND {string.Format(RetainedTraceOfChild, "e")}
            ORDER BY e."id" ASC
            LIMIT @Limit;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GcExecutionRef>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff, Limit = limit },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GcRequestRef>> ListOldRequestsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT r."id", r."project_id", r."data_storage_id"
            FROM "requests" r
            WHERE r."created_at" < @Cutoff
              AND {RetainedTraceOfRequest}
            ORDER BY r."id" ASC
            LIMIT @Limit;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GcRequestRef>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff, Limit = limit },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<long>> ListOldUsageLogIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT u."id"
            FROM "usage_logs" u
            WHERE u."created_at" < @Cutoff
              AND {string.Format(RetainedTraceOfChild, "u")}
            ORDER BY u."id" ASC
            LIMIT @Limit;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<long>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff, Limit = limit },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<long>> ListOldThreadIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT m."id"
            FROM "threads" m
            WHERE m."created_at" < @Cutoff AND m."status" <> 'retained'
            ORDER BY m."id" ASC
            LIMIT @Limit;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<long>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff, Limit = limit },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<long>> ListOldTraceIdsAsync(
        DateTime cutoff,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT m."id"
            FROM "traces" m
            WHERE m."created_at" < @Cutoff AND m."status" <> 'retained'
            ORDER BY m."id" ASC
            LIMIT @Limit;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<long>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff, Limit = limit },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public Task<int> DeleteExecutionsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => DeleteByIdsAsync("request_executions", ids, cancellationToken);

    public Task<int> DeleteRequestsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => DeleteByIdsAsync("requests", ids, cancellationToken);

    public Task<int> DeleteUsageLogsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => DeleteByIdsAsync("usage_logs", ids, cancellationToken);

    public Task<int> DeleteThreadsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => DeleteByIdsAsync("threads", ids, cancellationToken);

    public Task<int> DeleteTracesAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => DeleteByIdsAsync("traces", ids, cancellationToken);

    public async Task<long> CountOldRequestsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT COUNT(*)::bigint
            FROM "requests" r
            WHERE r."created_at" < @Cutoff AND {RetainedTraceOfRequest};
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<long> CountOldExecutionsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT COUNT(*)::bigint
            FROM "request_executions" e
            WHERE e."created_at" < @Cutoff AND {string.Format(RetainedTraceOfChild, "e")};
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<long> CountOldUsageLogsAsync(
        DateTime cutoff,
        CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT COUNT(*)::bigint
            FROM "usage_logs" u
            WHERE u."created_at" < @Cutoff AND {string.Format(RetainedTraceOfChild, "u")};
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                new { Cutoff = cutoff },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GcRequestRef>> ListRequestPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryStorageId,
        IReadOnlyList<long> skipIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder()
            .Append("""
                SELECT r."id", r."project_id", r."data_storage_id"
                FROM "requests" r
                WHERE r."created_at" < @Cutoff
                  AND
                """)
            .Append(RetainedTraceOfRequest)
            .Append("  AND ").Append(BuildPayloadPredicate(kind, "r", primaryStorageId, args));
        AppendSkipIds(sql, "r", skipIds, args);
        sql.Append(" ORDER BY r.\"id\" ASC LIMIT @Limit;");

        args.Parameters.Add("Cutoff", cutoff);
        args.Parameters.Add("Limit", limit);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GcRequestRef>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GcExecutionRef>> ListExecutionPayloadsAsync(
        GcPayloadKind kind,
        DateTime cutoff,
        long primaryStorageId,
        IReadOnlyList<long> skipIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder()
            .Append("""
                SELECT e."id", e."request_id", e."project_id", e."data_storage_id"
                FROM "request_executions" e
                WHERE e."created_at" < @Cutoff
                  AND
                """)
            .Append(string.Format(RetainedTraceOfChild, "e"))
            .Append("  AND ").Append(BuildPayloadPredicate(kind, "e", primaryStorageId, args));
        AppendSkipIds(sql, "e", skipIds, args);
        sql.Append(" ORDER BY e.\"id\" ASC LIMIT @Limit;");

        args.Parameters.Add("Cutoff", cutoff);
        args.Parameters.Add("Limit", limit);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<GcExecutionRef>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public Task<int> ClearRequestPayloadsAsync(
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => ClearPayloadsAsync("requests", kind, ids, cancellationToken);

    public Task<int> ClearExecutionPayloadsAsync(
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => ClearPayloadsAsync("request_executions", kind, ids, cancellationToken);

    public async Task VacuumAsync(bool full, CancellationToken cancellationToken = default)
    {
        // The reference only runs VACUUM on SQLite/PostgreSQL; the port's
        // persistence layer is PostgreSQL-only.
        var sql = full ? "VACUUM FULL" : "VACUUM";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                transaction: null,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> DeleteByIdsAsync(
        string table,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        var sql = "DELETE FROM \"" + table + "\" WHERE \"id\" = ANY(@Ids);";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Ids = ids.ToArray() },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static void AppendSkipIds(
        StringBuilder sql,
        string alias,
        IReadOnlyList<long> skipIds,
        SqlArguments args)
    {
        if (skipIds.Count == 0)
        {
            return;
        }

        sql.Append(" AND NOT (").Append(alias).Append(".\"id\" = ANY(")
            .Append(args.Add(skipIds.ToArray())).Append("))");
    }

    private static string BuildPayloadPredicate(
        GcPayloadKind kind,
        string alias,
        long primaryStorageId,
        SqlArguments args)
    {
        var builder = new StringBuilder("(");
        switch (kind)
        {
            case GcPayloadKind.RequestBodies:
                builder.Append(JsonNotPlaceholder(alias, "request_body", args, "{}", "null", ""));
                builder.Append(" OR ");
                builder.Append(JsonNotPlaceholder(alias, "request_headers", args, "{}", "null", ""));
                if (primaryStorageId > 0)
                {
                    // Production external storage: the DB body is {} and the
                    // leftover headers are the signal that the object was not
                    // stripped yet (gc_bodies.go:348-357).
                    builder.Append(" OR (").Append(alias).Append(".\"data_storage_id\" IS NOT NULL AND ")
                        .Append(alias).Append(".\"data_storage_id\" <> ").Append(args.Add(primaryStorageId))
                        .Append(" AND ").Append(alias).Append(".\"request_headers\" IS NOT NULL)");
                }

                break;
            case GcPayloadKind.ResponseBodies:
                builder.Append(JsonNotPlaceholder(alias, "response_body", args, "{}", "null", ""));
                builder.Append(" OR ");
                builder.Append(JsonNotPlaceholder(alias, "response_headers", args, "{}", "null", ""));
                AppendExternalNullPayload(builder, alias, "response_body", primaryStorageId, args);
                break;
            case GcPayloadKind.ResponseChunks:
                builder.Append(JsonNotPlaceholder(
                    alias, "response_chunks", args, "[]", "{}", "null", ""));
                AppendExternalNullPayload(builder, alias, "response_chunks", primaryStorageId, args);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return builder.Append(')').ToString();
    }

    private static void AppendExternalNullPayload(
        StringBuilder builder,
        string alias,
        string payloadColumn,
        long primaryStorageId,
        SqlArguments args)
    {
        if (primaryStorageId <= 0)
        {
            return;
        }

        // Historical external rows: the object lives in S3/FS and the DB column
        // is NULL. One strip pass turns it into {} / [] so it stops matching.
        builder.Append(" OR (").Append(alias).Append(".\"data_storage_id\" IS NOT NULL AND ")
            .Append(alias).Append(".\"data_storage_id\" <> ").Append(args.Add(primaryStorageId))
            .Append(" AND ").Append(alias).Append(".\"").Append(payloadColumn)
            .Append("\" IS NULL)");
    }

    private static string JsonNotPlaceholder(
        string alias,
        string column,
        SqlArguments args,
        params string[] placeholders)
    {
        var values = string.Join(",", placeholders.Select(args.Add));

        return "(" + alias + ".\"" + column + "\" IS NOT NULL AND CAST("
            + alias + ".\"" + column + "\" AS TEXT) NOT IN (" + values + "))";
    }

    private async Task<int> ClearPayloadsAsync(
        string table,
        GcPayloadKind kind,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        // The request body column is immutable in ent, so the reference emits a
        // Modify() update with a dialect JSON literal instead of binding bytes
        // (gc_bodies.go:237-279). PostgreSQL jsonb is the only port dialect.
        var set = kind switch
        {
            GcPayloadKind.RequestBodies =>
                "\"request_body\" = CAST(@Empty AS jsonb), \"request_headers\" = NULL",
            GcPayloadKind.ResponseBodies =>
                "\"response_body\" = CAST(@Empty AS jsonb), \"response_headers\" = NULL",
            GcPayloadKind.ResponseChunks =>
                "\"response_chunks\" = CAST(@Empty AS jsonb)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        var empty = kind == GcPayloadKind.ResponseChunks ? "[]" : "{}";

        var sql = "UPDATE \"" + table + "\" SET " + set
            + ", \"updated_at\" = CURRENT_TIMESTAMP WHERE \"id\" = ANY(@Ids);";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Empty = empty, Ids = ids.ToArray() },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
