using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>
/// Creates short-lived <see cref="AxonHubDbContext"/> units of work. Singleton
/// hosts (Orleans grains, HTTP middleware) cannot own a scoped context, so they
/// create one per operation through this seam.
/// </summary>
public interface IAxonHubDbContextFactory
{
    AxonHubDbContext CreateDbContext();
}

/// <summary>
/// Central connection configuration for both persistence paths: Dapper opens
/// <see cref="NpgsqlConnection"/>s for reads and <see cref="CreateDbContext"/>
/// yields the EF Core unit of work used by the write side. The reference connects
/// with Ent's pgx driver (internal/server/db/ent.go) and relies on the driver
/// defaults for per-connection behavior, so this type only centralizes the
/// connection string, EF options and Dapper's snake_case column mapping.
/// </summary>
public sealed class PostgresConnectionFactory : IAxonHubDbContextFactory
{
    private readonly DbContextOptions<AxonHubDbContext> _options;

    static PostgresConnectionFactory()
    {
        // Dapper does not map snake_case columns to PascalCase properties by
        // default. The reference column names are the source of truth here, so
        // enable the mapping once for every type read through this layer.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresConnectionFactory(string connectionString)
    {
        ConnectionString = connectionString;
        _options = new DbContextOptionsBuilder<AxonHubDbContext>()
            .UseNpgsql(connectionString)
            .Options;
    }

    public string ConnectionString { get; }

    /// <summary>
    /// Creates the write-side unit of work. Dapper reads can enlist in the same
    /// connection and transaction via <c>Database.GetDbConnection()</c> and
    /// <c>Database.CurrentTransaction.GetDbTransaction()</c>.
    /// </summary>
    public AxonHubDbContext CreateDbContext() => new(_options);

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
