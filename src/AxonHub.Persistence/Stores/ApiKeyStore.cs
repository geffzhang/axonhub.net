using Dapper;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>api_keys</c> table.</summary>
public interface IApiKeyStore
{
    Task<long> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default);

    Task<ApiKeyRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of APIKeyService.GetAPIKey's database lookup (live rows only).</summary>
    Task<ApiKeyRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);
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

    public async Task<long> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKey);

        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return apiKey.Id;
    }

    public async Task<ApiKeyRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"api_keys\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ApiKeyRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
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
}
