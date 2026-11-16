using Testcontainers.PostgreSql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// One PostgreSQL container shared by every persistence test. Tests isolate
/// themselves with a dedicated schema (see <see cref="TestDatabase"/>), so the
/// container startup cost is paid once per test run.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
