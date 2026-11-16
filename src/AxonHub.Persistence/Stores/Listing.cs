namespace AxonHub.Persistence;

/// <summary>Order direction shared by the Ent-style connection queries.</summary>
public enum SortDirection
{
    Asc,
    Desc,
}

/// <summary>
/// Keyset cursor of an Entgql connection: the row id plus the ordering value
/// when the order is not the default id order. The value is a
/// <see cref="DateTime"/> for time columns, a <see cref="string"/> for text
/// columns and a <see cref="long"/> for integer columns, exactly like the
/// reference ent.Cursor.Value.
/// </summary>
public sealed record ListCursor(long Id, object? Value);

/// <summary>One page of an Entgql connection after <c>Connection.build</c>.</summary>
public sealed record ListPage<T>(
    IReadOnlyList<T> Nodes,
    bool HasNextPage,
    bool HasPreviousPage,
    int TotalCount);
