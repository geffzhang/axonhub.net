using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference OIDCIdentityOrder type.</summary>
public enum OidcIdentitySortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Entgql <c>OIDCIdentityWhereInput</c>: every scalar predicate group plus the
/// <c>user</c> edge predicates.
/// </summary>
public sealed record OidcIdentityListFilter
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

    public string? Issuer { get; init; }

    public string? IssuerNEQ { get; init; }

    public IReadOnlyList<string>? IssuerIn { get; init; }

    public IReadOnlyList<string>? IssuerNotIn { get; init; }

    public string? IssuerGT { get; init; }

    public string? IssuerGTE { get; init; }

    public string? IssuerLT { get; init; }

    public string? IssuerLTE { get; init; }

    public string? IssuerContains { get; init; }

    public string? IssuerHasPrefix { get; init; }

    public string? IssuerHasSuffix { get; init; }

    public string? IssuerEqualFold { get; init; }

    public string? IssuerContainsFold { get; init; }

    public string? Subject { get; init; }

    public string? SubjectNEQ { get; init; }

    public IReadOnlyList<string>? SubjectIn { get; init; }

    public IReadOnlyList<string>? SubjectNotIn { get; init; }

    public string? SubjectGT { get; init; }

    public string? SubjectGTE { get; init; }

    public string? SubjectLT { get; init; }

    public string? SubjectLTE { get; init; }

    public string? SubjectContains { get; init; }

    public string? SubjectHasPrefix { get; init; }

    public string? SubjectHasSuffix { get; init; }

    public string? SubjectEqualFold { get; init; }

    public string? SubjectContainsFold { get; init; }

    public string? Email { get; init; }

    public string? EmailNEQ { get; init; }

    public IReadOnlyList<string>? EmailIn { get; init; }

    public IReadOnlyList<string>? EmailNotIn { get; init; }

    public string? EmailGT { get; init; }

    public string? EmailGTE { get; init; }

    public string? EmailLT { get; init; }

    public string? EmailLTE { get; init; }

    public string? EmailContains { get; init; }

    public string? EmailHasPrefix { get; init; }

    public string? EmailHasSuffix { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? EmailIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? EmailNotNil { get; init; }

    public string? EmailEqualFold { get; init; }

    public string? EmailContainsFold { get; init; }

    public string? IdpName { get; init; }

    public string? IdpNameNEQ { get; init; }

    public IReadOnlyList<string>? IdpNameIn { get; init; }

    public IReadOnlyList<string>? IdpNameNotIn { get; init; }

    public string? IdpNameGT { get; init; }

    public string? IdpNameGTE { get; init; }

    public string? IdpNameLT { get; init; }

    public string? IdpNameLTE { get; init; }

    public string? IdpNameContains { get; init; }

    public string? IdpNameHasPrefix { get; init; }

    public string? IdpNameHasSuffix { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? IdpNameIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? IdpNameNotNil { get; init; }

    public string? IdpNameEqualFold { get; init; }

    public string? IdpNameContainsFold { get; init; }

    public DateTime? LastLoginAt { get; init; }

    public DateTime? LastLoginAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? LastLoginAtIn { get; init; }

    public IReadOnlyList<DateTime>? LastLoginAtNotIn { get; init; }

    public DateTime? LastLoginAtGT { get; init; }

    public DateTime? LastLoginAtGTE { get; init; }

    public DateTime? LastLoginAtLT { get; init; }

    public DateTime? LastLoginAtLTE { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? LastLoginAtIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? LastLoginAtNotNil { get; init; }

    /// <summary>The owner predicate the UserOwnedQueryRule installs.</summary>
    public long? UserId { get; init; }

    public long? UserIdNEQ { get; init; }

    public IReadOnlyList<long>? UserIdIn { get; init; }

    public IReadOnlyList<long>? UserIdNotIn { get; init; }

    public bool? HasUser { get; init; }

    public IReadOnlyList<UserListFilter>? HasUserWith { get; init; }

    public OidcIdentityListFilter? Not { get; init; }

    public IReadOnlyList<OidcIdentityListFilter>? And { get; init; }

    public IReadOnlyList<OidcIdentityListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for oidc_identities.</summary>
public sealed record OidcIdentityListQuery
{
    public OidcIdentityListFilter? Where { get; init; }

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public OidcIdentitySortField SortField { get; init; } = OidcIdentitySortField.Id;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Persistence seam for the management-side reads of the reference
/// <c>oidc_identities</c> table (the root <c>oidcIdentities</c> connection,
/// ent.resolvers.go:427-436). Kept apart from <see cref="IOidcLoginStore"/>,
/// which models the login flow.
/// </summary>
public interface IOidcIdentityAdminStore
{
    Task<ListPage<OidcIdentityRecord>> ListAsync(
        OidcIdentityListQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>oidc_identities</c> table. Reads use Dapper and
/// honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class OidcIdentityAdminStore(AxonHubDbContext db) : IOidcIdentityAdminStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "issuer", "subject",
        "email", "idp_name", "last_login_at", "user_id"
        """;

    public async Task<ListPage<OidcIdentityRecord>> ListAsync(
        OidcIdentityListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "oi", args);

        var conditions = new StringBuilder(" WHERE oi.\"deleted_at\" = 0");
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        // entgql reverses the scan for a `last` page and build() restores the
        // requested direction afterwards (RoleStore does the same).
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) FROM \"oidc_identities\" oi" + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "oi." + column.Trim())));
        nodesSql.Append(" FROM \"oidc_identities\" oi").Append(conditions);

        var valueColumn = query.SortField switch
        {
            OidcIdentitySortField.CreatedAt => "created_at",
            OidcIdentitySortField.UpdatedAt => "updated_at",
            _ => null,
        };

        ConnectionSql.AppendCursor(
            nodesSql,
            "oi",
            "id",
            valueColumn,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("oi", "id", valueColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);

        var totalCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var nodes = (await connection.QueryAsync<OidcIdentityRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    private static ListPage<OidcIdentityRecord> BuildPage(
        List<OidcIdentityRecord> nodes,
        OidcIdentityListQuery query,
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

        return new ListPage<OidcIdentityRecord>(
            nodes,
            hasNextPage,
            hasPreviousPage,
            totalCount);
    }

    internal static string? BuildFilter(
        OidcIdentityListFilter? filter,
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
        ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAt, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "created_at", filter.CreatedAtIn, args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "issuer",
            filter.Issuer,
            filter.IssuerNEQ,
            filter.IssuerIn,
            filter.IssuerNotIn,
            filter.IssuerGT,
            filter.IssuerGTE,
            filter.IssuerLT,
            filter.IssuerLTE,
            filter.IssuerContains,
            filter.IssuerHasPrefix,
            filter.IssuerHasSuffix,
            filter.IssuerEqualFold,
            filter.IssuerContainsFold,
            args);

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "subject",
            filter.Subject,
            filter.SubjectNEQ,
            filter.SubjectIn,
            filter.SubjectNotIn,
            filter.SubjectGT,
            filter.SubjectGTE,
            filter.SubjectLT,
            filter.SubjectLTE,
            filter.SubjectContains,
            filter.SubjectHasPrefix,
            filter.SubjectHasSuffix,
            filter.SubjectEqualFold,
            filter.SubjectContainsFold,
            args);

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "email",
            filter.Email,
            filter.EmailNEQ,
            filter.EmailIn,
            filter.EmailNotIn,
            filter.EmailGT,
            filter.EmailGTE,
            filter.EmailLT,
            filter.EmailLTE,
            filter.EmailContains,
            filter.EmailHasPrefix,
            filter.EmailHasSuffix,
            filter.EmailEqualFold,
            filter.EmailContainsFold,
            args);
        if (filter.EmailIsNil == true)
        {
            predicates.Add(alias + ".\"email\" IS NULL");
        }

        if (filter.EmailNotNil == true)
        {
            predicates.Add(alias + ".\"email\" IS NOT NULL");
        }

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "idp_name",
            filter.IdpName,
            filter.IdpNameNEQ,
            filter.IdpNameIn,
            filter.IdpNameNotIn,
            filter.IdpNameGT,
            filter.IdpNameGTE,
            filter.IdpNameLT,
            filter.IdpNameLTE,
            filter.IdpNameContains,
            filter.IdpNameHasPrefix,
            filter.IdpNameHasSuffix,
            filter.IdpNameEqualFold,
            filter.IdpNameContainsFold,
            args);
        if (filter.IdpNameIsNil == true)
        {
            predicates.Add(alias + ".\"idp_name\" IS NULL");
        }

        if (filter.IdpNameNotNil == true)
        {
            predicates.Add(alias + ".\"idp_name\" IS NOT NULL");
        }

        ConnectionSql.AppendComparison(
            predicates, alias, "last_login_at", filter.LastLoginAt, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "last_login_at", filter.LastLoginAtNEQ, "<>", args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "last_login_at", filter.LastLoginAtIn, args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "last_login_at", filter.LastLoginAtNotIn, args, negate: true);
        ConnectionSql.AppendComparison(
            predicates, alias, "last_login_at", filter.LastLoginAtGT, ">", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "last_login_at", filter.LastLoginAtGTE, ">=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "last_login_at", filter.LastLoginAtLT, "<", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "last_login_at", filter.LastLoginAtLTE, "<=", args);
        if (filter.LastLoginAtIsNil == true)
        {
            predicates.Add(alias + ".\"last_login_at\" IS NULL");
        }

        if (filter.LastLoginAtNotNil == true)
        {
            predicates.Add(alias + ".\"last_login_at\" IS NOT NULL");
        }

        ConnectionSql.AppendComparison(
            predicates, alias, "user_id", filter.UserId, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "user_id", filter.UserIdNEQ, "<>", args);
        ConnectionSql.AppendIdIn(predicates, alias, "user_id", filter.UserIdIn, args);
        ConnectionSql.AppendIdIn(
            predicates, alias, "user_id", filter.UserIdNotIn, args, negate: true);

        if (filter.HasUser is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"users\" u2 WHERE u2.\"id\" = "
                + alias + ".\"user_id\")";
            predicates.Add(filter.HasUser.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasUserWith is { Count: > 0 } userFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the User soft-delete predicate.
            var userPredicates = userFilters
                .Select(user => UserAdminStore.BuildFilter(user, "u2", args))
                .Where(static user => user is not null)
                .ToArray();
            var userCondition = ConnectionSql.Combine(userPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"users\" u2"
                + " WHERE u2.\"id\" = " + alias + ".\"user_id\""
                + (userCondition is null ? string.Empty : " AND " + userCondition)
                + ")");
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
}
