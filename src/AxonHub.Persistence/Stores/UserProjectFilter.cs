namespace AxonHub.Persistence;

/// <summary>
/// Port of <c>UserProjectWhereInput</c> (ent.graphql:7377-7412). The
/// reference GraphQL input exposes the id, the two non-null timestamps and
/// <c>is_owner</c>; <c>user_id</c> / <c>project_id</c> / <c>scopes</c> are not
/// part of the generated input, so they have no predicates here.
/// </summary>
public sealed record UserProjectListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public long? IdGT { get; init; }

    public long? IdGTE { get; init; }

    public long? IdLT { get; init; }

    public long? IdLTE { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? CreatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; init; }

    public DateTime? CreatedAtGT { get; init; }

    public DateTime? CreatedAtGTE { get; init; }

    public DateTime? CreatedAtLT { get; init; }

    public DateTime? CreatedAtLTE { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? UpdatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; init; }

    public DateTime? UpdatedAtGT { get; init; }

    public DateTime? UpdatedAtGTE { get; init; }

    public DateTime? UpdatedAtLT { get; init; }

    public DateTime? UpdatedAtLTE { get; init; }

    public bool? IsOwner { get; init; }

    public bool? IsOwnerNEQ { get; init; }

    public UserProjectListFilter? Not { get; init; }

    public IReadOnlyList<UserProjectListFilter>? And { get; init; }

    public IReadOnlyList<UserProjectListFilter>? Or { get; init; }
}

/// <summary>
/// Builds the <c>UserProjectWhereInput</c> predicate against the alias used by
/// a <c>user_projects</c> EXISTS subquery.
/// </summary>
internal static class UserProjectFilterSql
{
    public static string? Build(
        UserProjectListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        ConnectionSql.AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        if (filter.IdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            ConnectionSql.AppendIdIn(
                predicates,
                alias,
                "id",
                filter.IdNotIn,
                args,
                negate: true);
        }

        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        AppendTimestampPredicates(
            predicates,
            alias,
            "created_at",
            filter.CreatedAt,
            filter.CreatedAtNEQ,
            filter.CreatedAtIn,
            filter.CreatedAtNotIn,
            filter.CreatedAtGT,
            filter.CreatedAtGTE,
            filter.CreatedAtLT,
            filter.CreatedAtLTE,
            args);

        AppendTimestampPredicates(
            predicates,
            alias,
            "updated_at",
            filter.UpdatedAt,
            filter.UpdatedAtNEQ,
            filter.UpdatedAtIn,
            filter.UpdatedAtNotIn,
            filter.UpdatedAtGT,
            filter.UpdatedAtGTE,
            filter.UpdatedAtLT,
            filter.UpdatedAtLTE,
            args);

        ConnectionSql.AppendComparison(predicates, alias, "is_owner", filter.IsOwner, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "is_owner", filter.IsOwnerNEQ, "<>", args);

        if (filter.Not is not null)
        {
            var not = Build(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => Build(nested, alias, args))
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
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }

    private static void AppendTimestampPredicates(
        List<string?> predicates,
        string alias,
        string column,
        DateTime? equal,
        DateTime? notEqual,
        IReadOnlyList<DateTime>? @in,
        IReadOnlyList<DateTime>? notIn,
        DateTime? greater,
        DateTime? greaterOrEqual,
        DateTime? less,
        DateTime? lessOrEqual,
        SqlArguments args)
    {
        ConnectionSql.AppendComparison(predicates, alias, column, equal, "=", args);
        ConnectionSql.AppendComparison(predicates, alias, column, notEqual, "<>", args);
        ConnectionSql.AppendTimeIn(predicates, alias, column, @in, args);
        ConnectionSql.AppendTimeIn(predicates, alias, column, notIn, args, negate: true);
        ConnectionSql.AppendComparison(predicates, alias, column, greater, ">", args);
        ConnectionSql.AppendComparison(predicates, alias, column, greaterOrEqual, ">=", args);
        ConnectionSql.AppendComparison(predicates, alias, column, less, "<", args);
        ConnectionSql.AppendComparison(predicates, alias, column, lessOrEqual, "<=", args);
    }
}
