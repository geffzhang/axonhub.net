using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>Named parameters for a dynamically composed Dapper command.</summary>
internal sealed class SqlArguments
{
    private readonly DynamicParameters _parameters = new();
    private int _next;

    public DynamicParameters Parameters => _parameters;

    public string Add(object? value)
    {
        var name = "p" + _next++;
        _parameters.Add(name, value);
        return "@" + name;
    }
}

/// <summary>
/// SQL fragments shared by the Ent-style connection queries. Entgql paginates
/// with a keyset on (order field, id) and trims the extra row fetched to detect
/// the next/previous page.
/// </summary>
/// <remarks>
/// The <c>Append*In</c> helpers model entgql's generated WhereInput, which
/// only installs In/NotIn when the list is non-empty. Direct ent queries
/// (<c>ent.Channel.Query().Where(channel.IDIn(ids...))</c>) instead render an
/// always-false predicate for an empty IN list; those call sites return early.
/// </remarks>
internal static class ConnectionSql
{
    public static void AppendCursor(
        StringBuilder sql,
        string alias,
        string idColumn,
        string? valueColumn,
        ListCursor? after,
        ListCursor? before,
        SortDirection direction,
        SqlArguments args)
    {
        if (after is not null)
        {
            AppendTupleComparison(sql, alias, idColumn, valueColumn, after, direction, args);
        }

        if (before is not null)
        {
            AppendTupleComparison(
                sql,
                alias,
                idColumn,
                valueColumn,
                before,
                direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc,
                args);
        }
    }

    private static void AppendTupleComparison(
        StringBuilder sql,
        string alias,
        string idColumn,
        string? valueColumn,
        ListCursor cursor,
        SortDirection direction,
        SqlArguments args)
    {
        // Entgql ignores a zero id cursor (Cursor.Valid && Cursor.ID != 0).
        if (cursor.Id == 0)
        {
            return;
        }

        var op = direction == SortDirection.Asc ? ">" : "<";

        // entgql's CursorsPredicate falls back to the id column alone when the
        // cursor carries no ordering value (a cursor issued for another order
        // field, or an unresolved legacy cursor).
        if (valueColumn is null || cursor.Value is null)
        {
            var id = args.Add(cursor.Id);
            sql.Append(" AND ").Append(alias).Append(".\"").Append(idColumn).Append("\" ")
                .Append(op).Append(' ').Append(id);
            return;
        }

        var value = args.Add(cursor.Value);
        var cursorId = args.Add(cursor.Id);
        sql.Append(" AND (")
            .Append(alias).Append(".\"").Append(valueColumn).Append("\" ")
            .Append(op).Append(' ').Append(value)
            .Append(" OR (")
            .Append(alias).Append(".\"").Append(valueColumn).Append("\" = ").Append(value)
            .Append(" AND ")
            .Append(alias).Append(".\"").Append(idColumn).Append("\" ")
            .Append(op).Append(' ').Append(cursorId)
            .Append("))");
    }

    public static string OrderBy(
        string alias,
        string idColumn,
        string? valueColumn,
        SortDirection direction)
    {
        var dir = direction == SortDirection.Asc ? "ASC" : "DESC";
        return valueColumn is null
            ? alias + ".\"" + idColumn + "\" " + dir
            : alias + ".\"" + valueColumn + "\" " + dir + ", "
                + alias + ".\"" + idColumn + "\" " + dir;
    }

    public static void AppendStringFilter(
        List<string?> predicates,
        string alias,
        string column,
        string op,
        string value,
        SqlArguments args)
    {
        var parameter = args.Add("%" + value + "%");
        predicates.Add("LOWER(" + alias + ".\"" + column + "\") " + op + " LOWER(" + parameter + ")");
    }

    /// <summary>
    /// Ent's IDIn/IDNotIn predicate. Entgql only installs the predicate when
    /// the list is non-empty, so an empty list adds nothing to the filter.
    /// </summary>
    public static void AppendIdIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<long>? values,
        SqlArguments args,
        bool negate = false)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count == 0)
        {
            // entgql's generated WhereInput only installs In/NotIn when the
            // list is non-empty (len(...) > 0), so an empty list is a no-op.
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" " + (negate ? "NOT IN" : "IN")
            + " (" + string.Join(", ", parameters) + ")");
    }

    /// <summary>Ent's StringIn/StringNotIn predicate.</summary>
    public static void AppendStringIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<string>? values,
        SqlArguments args,
        bool negate = false)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count == 0)
        {
            // entgql's generated WhereInput only installs In/NotIn when the
            // list is non-empty (len(...) > 0), so an empty list is a no-op.
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" " + (negate ? "NOT IN" : "IN")
            + " (" + string.Join(", ", parameters) + ")");
    }

    /// <summary>Ent's TimeIn/TimeNotIn predicate.</summary>
    public static void AppendTimeIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<DateTime>? values,
        SqlArguments args,
        bool negate = false)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count == 0)
        {
            // entgql's generated WhereInput only installs In/NotIn when the
            // list is non-empty (len(...) > 0), so an empty list is a no-op.
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" " + (negate ? "NOT IN" : "IN")
            + " (" + string.Join(", ", parameters) + ")");
    }

    public static void AppendComparison(
        List<string?> predicates,
        string alias,
        string column,
        long? value,
        string op,
        SqlArguments args)
    {
        if (value is not null)
        {
            predicates.Add(
                alias + ".\"" + column + "\" " + op + " " + args.Add(value.Value));
        }
    }

    public static void AppendComparison(
        List<string?> predicates,
        string alias,
        string column,
        string? value,
        string op,
        SqlArguments args)
    {
        if (value is not null)
        {
            predicates.Add(alias + ".\"" + column + "\" " + op + " " + args.Add(value));
        }
    }

    public static void AppendComparison(
        List<string?> predicates,
        string alias,
        string column,
        DateTime? value,
        string op,
        SqlArguments args)
    {
        if (value is not null)
        {
            predicates.Add(
                alias + ".\"" + column + "\" " + op + " " + args.Add(value.Value));
        }
    }

    public static void AppendComparison(
        List<string?> predicates,
        string alias,
        string column,
        bool? value,
        string op,
        SqlArguments args)
    {
        if (value is not null)
        {
            predicates.Add(
                alias + ".\"" + column + "\" " + op + " " + args.Add(value.Value));
        }
    }

    public static void AppendComparison(
        List<string?> predicates,
        string alias,
        string column,
        double? value,
        string op,
        SqlArguments args)
    {
        if (value is not null)
        {
            predicates.Add(
                alias + ".\"" + column + "\" " + op + " " + args.Add(value.Value));
        }
    }

    /// <summary>Ent's FloatIn/FloatNotIn predicate (same empty-list semantics).</summary>
    public static void AppendDoubleIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<double>? values,
        SqlArguments args,
        bool negate = false)
    {
        if (values is null)
        {
            return;
        }

        if (values.Count == 0)
        {
            // entgql's generated WhereInput only installs In/NotIn when the
            // list is non-empty (len(...) > 0), so an empty list is a no-op.
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" " + (negate ? "NOT IN" : "IN")
            + " (" + string.Join(", ", parameters) + ")");
    }

    /// <summary>
    /// The full ent string predicate set for one nullable string column:
    /// equality/ordering comparisons, IN/NOT IN, Contains/HasPrefix/HasSuffix,
    /// EqualFold and ContainsFold.
    /// </summary>
    public static void AppendStringPredicates(
        List<string?> predicates,
        string alias,
        string column,
        string? equal,
        string? notEqual,
        IReadOnlyList<string>? @in,
        IReadOnlyList<string>? notIn,
        string? greater,
        string? greaterOrEqual,
        string? less,
        string? lessOrEqual,
        string? contains,
        string? hasPrefix,
        string? hasSuffix,
        string? equalFold,
        string? containsFold,
        SqlArguments args)
    {
        AppendComparison(predicates, alias, column, equal, "=", args);
        AppendComparison(predicates, alias, column, notEqual, "<>", args);
        if (@in is not null)
        {
            AppendStringIn(predicates, alias, column, @in, args);
        }

        if (notIn is not null)
        {
            AppendStringIn(predicates, alias, column, notIn, args, negate: true);
        }

        AppendComparison(predicates, alias, column, greater, ">", args);
        AppendComparison(predicates, alias, column, greaterOrEqual, ">=", args);
        AppendComparison(predicates, alias, column, less, "<", args);
        AppendComparison(predicates, alias, column, lessOrEqual, "<=", args);

        if (contains is not null)
        {
            predicates.Add(
                alias + ".\"" + column + "\" LIKE " + args.Add("%" + contains + "%"));
        }

        if (hasPrefix is not null)
        {
            predicates.Add(alias + ".\"" + column + "\" LIKE " + args.Add(hasPrefix + "%"));
        }

        if (hasSuffix is not null)
        {
            predicates.Add(alias + ".\"" + column + "\" LIKE " + args.Add("%" + hasSuffix));
        }

        if (equalFold is not null)
        {
            var parameter = args.Add(equalFold);
            predicates.Add(
                "LOWER(" + alias + ".\"" + column + "\") = LOWER(" + parameter + ")");
        }

        if (containsFold is not null)
        {
            // ent.ContainsFold: LOWER(col) LIKE LOWER('%value%').
            AppendStringFilter(predicates, alias, column, "LIKE", containsFold, args);
        }
    }

    /// <summary>
    /// The full ent int predicate set for one column: equality/ordering
    /// comparisons, IN/NOT IN and the optional IsNil/NotNil pair.
    /// </summary>
    public static void AppendLongPredicates(
        List<string?> predicates,
        string alias,
        string column,
        long? value,
        long? notEqual,
        IReadOnlyList<long>? inList,
        IReadOnlyList<long>? notInList,
        long? greaterThan,
        long? greaterThanOrEqual,
        long? lessThan,
        long? lessThanOrEqual,
        bool? isNil,
        bool? notNil,
        SqlArguments args)
    {
        AppendComparison(predicates, alias, column, value, "=", args);
        AppendComparison(predicates, alias, column, notEqual, "<>", args);
        AppendIdIn(predicates, alias, column, inList, args);
        AppendIdIn(predicates, alias, column, notInList, args, negate: true);
        AppendComparison(predicates, alias, column, greaterThan, ">", args);
        AppendComparison(predicates, alias, column, greaterThanOrEqual, ">=", args);
        AppendComparison(predicates, alias, column, lessThan, "<", args);
        AppendComparison(predicates, alias, column, lessThanOrEqual, "<=", args);

        if (isNil == true)
        {
            predicates.Add(alias + ".\"" + column + "\" IS NULL");
        }

        if (notNil == true)
        {
            predicates.Add(alias + ".\"" + column + "\" IS NOT NULL");
        }
    }

    /// <summary>Ent's BooleanEQ/BooleanNEQ predicate pair.</summary>
    public static void AppendBoolPredicates(
        List<string?> predicates,
        string alias,
        string column,
        bool? value,
        bool? notEqual,
        SqlArguments args)
    {
        AppendComparison(predicates, alias, column, value, "=", args);
        AppendComparison(predicates, alias, column, notEqual, "<>", args);
    }

    /// <summary>
    /// The full ent float predicate set for one column, including the optional
    /// IsNil/NotNil pair.
    /// </summary>
    public static void AppendDoublePredicates(
        List<string?> predicates,
        string alias,
        string column,
        double? value,
        double? notEqual,
        IReadOnlyList<double>? inList,
        IReadOnlyList<double>? notInList,
        double? greaterThan,
        double? greaterThanOrEqual,
        double? lessThan,
        double? lessThanOrEqual,
        bool? isNil,
        bool? notNil,
        SqlArguments args)
    {
        AppendComparison(predicates, alias, column, value, "=", args);
        AppendComparison(predicates, alias, column, notEqual, "<>", args);
        AppendDoubleIn(predicates, alias, column, inList, args);
        AppendDoubleIn(predicates, alias, column, notInList, args, negate: true);
        AppendComparison(predicates, alias, column, greaterThan, ">", args);
        AppendComparison(predicates, alias, column, greaterThanOrEqual, ">=", args);
        AppendComparison(predicates, alias, column, lessThan, "<", args);
        AppendComparison(predicates, alias, column, lessThanOrEqual, "<=", args);

        if (isNil == true)
        {
            predicates.Add(alias + ".\"" + column + "\" IS NULL");
        }

        if (notNil == true)
        {
            predicates.Add(alias + ".\"" + column + "\" IS NOT NULL");
        }
    }

    public static string? Combine(IReadOnlyList<string?> predicates)
    {
        var nonEmpty = predicates.Where(static predicate => predicate is not null).ToArray();
        if (nonEmpty.Length == 0)
        {
            return null;
        }

        return nonEmpty.Length == 1
            ? nonEmpty[0]
            : "(" + string.Join(" AND ", nonEmpty) + ")";
    }
}
