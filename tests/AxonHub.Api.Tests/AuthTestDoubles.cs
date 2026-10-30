using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory stores for the auth tests. They mirror the Dapper stores' visible
/// predicates: reads only return live rows (<c>deleted_at = 0</c>) and the
/// no-auth bootstrap resolves the lowest live project and the lowest live owner.
/// </summary>
internal sealed class FakeApiKeyStore : IApiKeyStore
{
    private long _nextId = 1;

    public List<ApiKeyRecord> Rows { get; } = [];

    public Task<long> CreateAsync(ApiKeyRecord apiKey, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        apiKey.Id = _nextId++;
        Rows.Add(apiKey);
        return Task.FromResult(apiKey.Id);
    }

    public Task<ApiKeyRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<ApiKeyRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.SingleOrDefault(row => row.Key == key && row.DeletedAt == 0));
    }

    public ApiKeyRecord Add(Action<ApiKeyRecord>? configure = null)
    {
        var row = new ApiKeyRecord
        {
            Key = "sk-user",
            Name = "user key",
            Type = "user",
            Status = "enabled",
            ProjectId = 1,
        };
        configure?.Invoke(row);
        row.Id = _nextId++;
        Rows.Add(row);
        return row;
    }
}

internal sealed class FakeProjectStore : IProjectStore
{
    private long _nextId = 1;

    public List<ProjectRecord> Rows { get; } = [];

    public Task<long> CreateAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        project.Id = _nextId++;
        Rows.Add(project);
        return Task.FromResult(project.Id);
    }

    public Task<ProjectRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<ProjectRecord?> GetFirstAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(
            Rows.Where(row => row.DeletedAt == 0).OrderBy(row => row.Id).FirstOrDefault());
    }

    public ProjectRecord Add(Action<ProjectRecord>? configure = null)
    {
        var row = new ProjectRecord
        {
            Name = "default",
            Description = "Default project",
            Status = "active",
        };
        configure?.Invoke(row);
        row.Id = _nextId++;
        Rows.Add(row);
        return row;
    }
}

internal sealed class FakeUserStore : IUserStore
{
    private long _nextId = 1;

    public List<UserRecord> Rows { get; } = [];

    public Task<long> CreateAsync(UserRecord user, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        user.Id = _nextId++;
        Rows.Add(user);
        return Task.FromResult(user.Id);
    }

    public Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<UserRecord?> FindOwnerAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(
            Rows.Where(row => row.DeletedAt == 0 && row.IsOwner)
                .OrderBy(row => row.Id)
                .FirstOrDefault());
    }

    public UserRecord Add(Action<UserRecord>? configure = null)
    {
        var row = new UserRecord
        {
            Email = "owner@example.com",
            Status = "activated",
            PreferLanguage = "en",
            Password = "hashed",
            IsOwner = true,
        };
        configure?.Invoke(row);
        row.Id = _nextId++;
        Rows.Add(row);
        return row;
    }
}
