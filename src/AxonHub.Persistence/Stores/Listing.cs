namespace AxonHub.Persistence;

/// <summary>Order direction shared by the Ent-style connection queries.</summary>
public enum SortDirection
{
    Asc,
    Desc,
}

/// <summary>
/// Keyset cursor of an Entgql connection: the row id plus the ordering value
/// when the order is not the default id order.
/// </summary>
public sealed record ListCursor(long Id, DateTime? Value);

/// <summary>One page of an Entgql connection after <c>Connection.build</c>.</summary>
public sealed record ListPage<T>(
    IReadOnlyList<T> Nodes,
    bool HasNextPage,
    bool HasPreviousPage,
    int TotalCount);
