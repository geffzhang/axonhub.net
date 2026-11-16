namespace AxonHub.Persistence;

/// <summary>
/// Port of <c>UserRoleWhereInput</c> (ent.graphql:7453-7505). The reference
/// GraphQL input exposes only the id and the two nullable timestamp fields;
/// the <c>user_id</c>/<c>role_id</c> columns are edge fields on the Go struct
/// but are not part of the schema, so they have no predicates here.
/// </summary>
public sealed record UserRoleListFilter
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

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool CreatedAtIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool CreatedAtNotNil { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? UpdatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; init; }

    public DateTime? UpdatedAtGT { get; init; }

    public DateTime? UpdatedAtGTE { get; init; }

    public DateTime? UpdatedAtLT { get; init; }

    public DateTime? UpdatedAtLTE { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool UpdatedAtIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool UpdatedAtNotNil { get; init; }

    public UserRoleListFilter? Not { get; init; }

    public IReadOnlyList<UserRoleListFilter>? And { get; init; }

    public IReadOnlyList<UserRoleListFilter>? Or { get; init; }
}

/// <summary>
/// Builds the <c>UserRoleWhereInput</c> predicate against the alias used by a
/// <c>user_roles</c> EXISTS subquery, shared by the User and Role edge
/// predicates.
/// </summary>
internal static class UserRoleFilterSql
{
    public static string? Build(
        UserRoleListFilter? filter,
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
            filter.CreatedAtIsNil,
            filter.CreatedAtNotNil,
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
            filter.UpdatedAtIsNil,
            filter.UpdatedAtNotNil,
            args);

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
        bool isNil,
        bool notNil,
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

        if (isNil)
        {
            predicates.Add(alias + ".\"" + column + "\" IS NULL");
        }

        if (notNil)
        {
            predicates.Add(alias + ".\"" + column + "\" IS NOT NULL");
        }
    }
}
