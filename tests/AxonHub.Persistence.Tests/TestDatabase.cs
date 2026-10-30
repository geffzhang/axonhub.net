using Dapper;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// A dedicated PostgreSQL schema for a single test. The connection string pins
/// <c>search_path</c> to that schema, so unqualified ported DDL and queries never
/// touch the shared container's public schema.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly PostgresFixture _fixture;

    public TestDatabase(PostgresFixture fixture)
    {
        _fixture = fixture;
        Schema = "test_" + Guid.NewGuid().ToString("N");

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            SearchPath = Schema,
        }.ConnectionString;

        Factory = new PostgresConnectionFactory(connectionString);
    }

    public string Schema { get; }

    public PostgresConnectionFactory Factory { get; }

    /// <summary>Creates the schema, applies the DDL and closes the connection again.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenMigratedAsync(cancellationToken);
    }

    public async Task<NpgsqlConnection> OpenMigratedAsync(CancellationToken cancellationToken = default)
    {
        var connection = await Factory.OpenAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "CREATE SCHEMA IF NOT EXISTS \"" + Schema + "\";",
                cancellationToken: cancellationToken));

            await DatabaseMigrator.EnsureCreatedAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("DROP SCHEMA IF EXISTS \"" + Schema + "\" CASCADE;");
    }
}
