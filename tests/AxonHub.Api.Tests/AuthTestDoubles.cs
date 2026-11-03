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

    public Exception? GetByKeyFailure { get; set; }

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
        if (GetByKeyFailure is not null)
        {
            return Task.FromException<ApiKeyRecord?>(GetByKeyFailure);
        }

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
        AssignId(row);
        Rows.Add(row);
        return row;
    }

    /// <summary>
    /// Seeds rows keep a preset Id (the admin GraphQL gate fixtures key on
    /// fixed ids); otherwise the next auto id is assigned.
    /// </summary>
    private void AssignId(ApiKeyRecord row)
    {
        row.Id = row.Id > 0 ? row.Id : _nextId++;
        if (row.Id >= _nextId)
        {
            _nextId = row.Id + 1;
        }
    }
}

internal sealed class FakeProjectStore : IProjectStore
{
    private long _nextId = 1;

    public List<ProjectRecord> Rows { get; } = [];

    /// <summary>user_projects rows touched by CreateProject / UpdateProject.</summary>
    public List<UserProjectRecord> ProjectUsers { get; } = [];

    public List<ProjectListQuery> Queries { get; } = [];

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

    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Rows.Any(row => row.DeletedAt == 0 && row.Name == name));
    }

    public Task<ListPage<ProjectRecord>> ListAsync(
        ProjectListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        Queries.Add(query);

        var filtered = Rows
            .Where(row => row.DeletedAt == 0 && Matches(query.Where, row))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField == ProjectSortField.UpdatedAt,
            static row => row.Id,
            static row => row.UpdatedAt,
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<ProjectRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<ProjectRecord?> UpdateAsync(
        ProjectUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var row = Rows.SingleOrDefault(item => item.Id == update.Id && item.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult<ProjectRecord?>(null);
        }

        // Ent runs the node update, the edge clears and the edge adds in one
        // transaction: a duplicate add rolls everything back. Validate the
        // resulting membership before mutating so the fake keeps that shape.
        if (update.AddedUserIds is { Count: > 0 })
        {
            var remaining = ProjectUsers
                .Where(item => item.ProjectId == update.Id)
                .Select(static item => item.UserId);

            if (update.ClearUsers)
            {
                remaining = [];
            }
            else if (update.RemovedUserIds is { Count: > 0 })
            {
                remaining = remaining.Where(id => !update.RemovedUserIds.Contains(id));
            }

            var seen = new HashSet<long>(remaining);
            foreach (var userId in update.AddedUserIds)
            {
                if (!seen.Add(userId))
                {
                    throw new StoreFailureException(
                        "add m2m edge for table user_projects: duplicate key value violates "
                        + "unique constraint \"user_projects_by_user_id_project_id\"",
                        new InvalidOperationException("duplicate user_project"));
                }
            }
        }

        if (update.Name is not null)
        {
            row.Name = update.Name;
        }

        if (update.Description is not null)
        {
            row.Description = update.Description;
        }

        row.UpdatedAt = DateTime.UtcNow;

        if (update.ClearUsers)
        {
            ProjectUsers.RemoveAll(item => item.ProjectId == update.Id);
        }
        else if (update.RemovedUserIds is { Count: > 0 })
        {
            ProjectUsers.RemoveAll(item => item.ProjectId == update.Id
                && update.RemovedUserIds.Contains(item.UserId));
        }

        if (update.AddedUserIds is { Count: > 0 })
        {
            var now = DateTime.UtcNow;
            foreach (var userId in update.AddedUserIds)
            {
                ProjectUsers.Add(new UserProjectRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsOwner = false,
                    Scopes = "[]",
                    UserId = userId,
                    ProjectId = update.Id,
                });
            }
        }

        return Task.FromResult<ProjectRecord?>(row);
    }

    public Task<ProjectRecord?> UpdateProfilesAsync(
        long id,
        string profilesJson,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var row = Rows.SingleOrDefault(item => item.Id == id && item.DeletedAt == 0);
        if (row is not null)
        {
            row.Profiles = profilesJson;
            row.UpdatedAt = DateTime.UtcNow;
        }

        return Task.FromResult(row);
    }

    public Task<ProjectRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var row = Rows.SingleOrDefault(item => item.Id == id && item.DeletedAt == 0);
        if (row is not null)
        {
            row.Status = status;
            row.UpdatedAt = DateTime.UtcNow;
        }

        return Task.FromResult(row);
    }

    public Task AssignOwnerAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        if (ProjectUsers.Any(item => item.ProjectId == projectId && item.UserId == userId))
        {
            throw new StoreFailureException(
                "failed to assign user to project: duplicate key value violates "
                + "unique constraint \"user_projects_by_user_id_project_id\"",
                new InvalidOperationException("duplicate user_project"));
        }

        var now = DateTime.UtcNow;
        ProjectUsers.Add(new UserProjectRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            IsOwner = true,
            Scopes = "[]",
            UserId = userId,
            ProjectId = projectId,
        });

        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        var row = Rows.SingleOrDefault(item => item.Id == id && item.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        ProjectUsers.RemoveAll(item => item.ProjectId == id);
        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        row.UpdatedAt = DateTime.UtcNow;

        return Task.FromResult(true);
    }

    public ProjectRecord Add(Action<ProjectRecord>? configure = null)
    {
        // Seeded rows are older than any mutation, mirroring an ent row that
        // was created before the test ran.
        var seededAt = DateTime.UtcNow.AddHours(-1);
        var row = new ProjectRecord
        {
            CreatedAt = seededAt,
            UpdatedAt = seededAt,
            Name = "default",
            Description = "Default project",
            Status = "active",
        };
        configure?.Invoke(row);
        row.Id = row.Id > 0 ? row.Id : _nextId++;
        if (row.Id >= _nextId)
        {
            _nextId = row.Id + 1;
        }

        Rows.Add(row);
        return row;
    }

    private static bool Matches(ProjectListFilter? filter, ProjectRecord row)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id.Value)
        {
            return false;
        }

        if (filter.IdIn is not null && !filter.IdIn.Contains(row.Id))
        {
            return false;
        }

        if (filter.Name is not null
            && !string.Equals(row.Name, filter.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.NameContainsFold is not null
            && !row.Name.Contains(filter.NameContainsFold, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Status is not null
            && !string.Equals(row.Status, filter.Status, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.StatusIn is not null && !filter.StatusIn.Contains(row.Status))
        {
            return false;
        }

        if (filter.Not is not null && Matches(filter.Not, row))
        {
            return false;
        }

        if (filter.And is not null && !filter.And.All(nested => Matches(nested, row)))
        {
            return false;
        }

        if (filter.Or is not null && !filter.Or.Any(nested => Matches(nested, row)))
        {
            return false;
        }

        return true;
    }
}

internal sealed class FakeUserStore : IUserStore
{
    private long _nextId = 1;

    public List<UserRecord> Rows { get; } = [];

    public Exception? GetByIdFailure { get; set; }

    public Exception? GetActivatedByEmailFailure { get; set; }

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
        if (GetByIdFailure is not null)
        {
            return Task.FromException<UserRecord?>(GetByIdFailure);
        }

        return Task.FromResult(Rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<UserRecord?> GetActivatedByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetActivatedByEmailFailure is not null)
        {
            return Task.FromException<UserRecord?>(GetActivatedByEmailFailure);
        }

        return Task.FromResult(Rows.SingleOrDefault(
            row => row.DeletedAt == 0 && row.Status == "activated" && row.Email == email));
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
        row.Id = row.Id > 0 ? row.Id : _nextId++;
        if (row.Id >= _nextId)
        {
            _nextId = row.Id + 1;
        }

        Rows.Add(row);
        return row;
    }
}
