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
/// Subset of <c>OIDCIdentityWhereInput</c> used by the root
/// <c>oidcIdentities</c> query. The remaining entgql scalar predicates and the
/// <c>hasUser</c> edge predicates are not modelled (C269).
/// </summary>
public sealed record OidcIdentityListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? Issuer { get; init; }

    public IReadOnlyList<string>? IssuerIn { get; init; }

    public string? Subject { get; init; }

    public IReadOnlyList<string>? SubjectIn { get; init; }

    public string? EmailContainsFold { get; init; }

    public string? IdpNameContainsFold { get; init; }

    /// <summary>The owner predicate the UserOwnedQueryRule installs.</summary>
    public long? UserId { get; init; }

    public IReadOnlyList<long>? UserIdIn { get; init; }

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

    private static string? BuildFilter(
        OidcIdentityListFilter? filter,
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

        if (filter.Issuer is not null)
        {
            predicates.Add(alias + ".\"issuer\" = " + args.Add(filter.Issuer));
        }

        if (filter.IssuerIn is not null)
        {
            ConnectionSql.AppendStringIn(
                predicates,
                alias,
                "issuer",
                filter.IssuerIn,
                args);
        }

        if (filter.Subject is not null)
        {
            predicates.Add(alias + ".\"subject\" = " + args.Add(filter.Subject));
        }

        if (filter.SubjectIn is not null)
        {
            ConnectionSql.AppendStringIn(
                predicates,
                alias,
                "subject",
                filter.SubjectIn,
                args);
        }

        if (filter.EmailContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "email",
                "LIKE",
                filter.EmailContainsFold,
                args);
        }

        if (filter.IdpNameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "idp_name",
                "LIKE",
                filter.IdpNameContainsFold,
                args);
        }

        if (filter.UserId is not null)
        {
            predicates.Add(alias + ".\"user_id\" = " + args.Add(filter.UserId.Value));
        }

        if (filter.UserIdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "user_id", filter.UserIdIn, args);
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
