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

        if (valueColumn is null)
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

    public static void AppendIdIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<long> ids,
        SqlArguments args)
    {
        if (ids.Count == 0)
        {
            predicates.Add("FALSE");
            return;
        }

        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = args.Add(ids[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" IN (" + string.Join(", ", parameters) + ")");
    }

    public static void AppendStringIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<string> values,
        SqlArguments args)
    {
        if (values.Count == 0)
        {
            predicates.Add("FALSE");
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" IN (" + string.Join(", ", parameters) + ")");
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
