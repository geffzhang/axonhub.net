using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory IPromptStore with ent-like live-row predicates, SET semantics and
/// keyset paging. It mirrors the Dapper store closely enough for the prompt
/// management service and GraphQL endpoint tests; the SQL rendering itself is
/// covered by PromptStoreTests in AxonHub.Persistence.Tests.
/// </summary>
internal sealed class FakePromptStore : IPromptStore
{
    private readonly List<PromptRecord> _rows = [];
    private long _nextId = 1;

    public IReadOnlyList<PromptRecord> Rows => _rows;

    public List<PromptListQuery> Queries { get; } = [];

    public long? LastProjectId { get; private set; }

    public Exception? ListFailure { get; set; }

    public Exception? GetByIdFailure { get; set; }

    public Exception? ExistsFailure { get; set; }

    public Exception? CreateFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? BulkFailure { get; set; }

    public PromptRecord Seed(Action<PromptRecord>? configure = null)
    {
        var record = new PromptRecord();
        configure?.Invoke(record);
        return Add(record);
    }

    /// <summary>Inserts an already-built row, like the reference's ent Create.</summary>
    public PromptRecord Add(PromptRecord record)
    {
        record.Id = record.Id > 0 ? record.Id : _nextId++;
        if (record.Id >= _nextId)
        {
            _nextId = record.Id + 1;
        }

        ApplyCreateDefaults(record);
        _rows.Add(record);
        return record;
    }

    public Task<long> CreateAsync(PromptRecord prompt, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateFailure is not null)
        {
            return Task.FromException<long>(CreateFailure);
        }

        Add(prompt);
        return Task.FromResult(prompt.Id);
    }

    public Task<IReadOnlyList<PromptRecord>> ListEnabledByProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        LastProjectId = projectId;
        return Task.FromResult<IReadOnlyList<PromptRecord>>(_rows
            .Where(row => row.DeletedAt == 0
                && row.ProjectId == projectId
                && row.Status == "enabled")
            .ToList());
    }

    public Task<PromptRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetByIdFailure is not null)
        {
            return Task.FromException<PromptRecord?>(GetByIdFailure);
        }

        return Task.FromResult(_rows.SingleOrDefault(row =>
            row.Id == id
            && row.DeletedAt == 0
            && (projectScope is null || row.ProjectId == projectScope)));
    }

    public Task<bool> ExistsByNameAsync(
        long projectId,
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (ExistsFailure is not null)
        {
            return Task.FromException<bool>(ExistsFailure);
        }

        return Task.FromResult(_rows.Any(row =>
            row.DeletedAt == 0
            && row.ProjectId == projectId
            && row.Name == name
            && (excludeId is null || row.Id != excludeId)));
    }

    public Task<ListPage<PromptRecord>> ListAsync(
        PromptListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        if (ListFailure is not null)
        {
            return Task.FromException<ListPage<PromptRecord>>(ListFailure);
        }

        var filtered = _rows
            .Where(row => row.DeletedAt == 0
                && (query.ProjectScope is null || row.ProjectId == query.ProjectScope)
                && Matches(query.Where, row))
            .ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != PromptSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                PromptSortField.CreatedAt => row.CreatedAt,
                PromptSortField.UpdatedAt => row.UpdatedAt,
                PromptSortField.Order => row.Order,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<PromptRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<int> UpdateAsync(
        PromptUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateFailure is not null)
        {
            return Task.FromException<int>(UpdateFailure);
        }

        var row = _rows.SingleOrDefault(item =>
            item.Id == update.Id && item.ProjectId == update.ProjectId);
        if (row is null)
        {
            return Task.FromResult(0);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.Name = update.Name ?? row.Name;
        row.Description = update.Description ?? row.Description;
        row.Role = update.Role ?? row.Role;
        row.Content = update.Content ?? row.Content;
        row.Status = update.Status ?? row.Status;
        row.Order = update.Order ?? row.Order;
        row.Settings = update.Settings ?? row.Settings;
        return Task.FromResult(1);
    }

    public Task<int> UpdateStatusAsync(
        long id,
        long projectId,
        string status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateFailure is not null)
        {
            return Task.FromException<int>(UpdateFailure);
        }

        var row = _rows.SingleOrDefault(item =>
            item.Id == id && item.ProjectId == projectId);
        if (row is null)
        {
            return Task.FromResult(0);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.Status = status;
        return Task.FromResult(1);
    }

    public Task<int> DeleteAsync(
        long id,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (DeleteFailure is not null)
        {
            return Task.FromException<int>(DeleteFailure);
        }

        var row = _rows.SingleOrDefault(item =>
            item.Id == id && item.ProjectId == projectId && item.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult(0);
        }

        SoftDelete(row);
        return Task.FromResult(1);
    }

    public Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (BulkFailure is not null)
        {
            return Task.FromException<int>(BulkFailure);
        }

        var affected = 0;
        foreach (var row in _rows.Where(row =>
                     row.DeletedAt == 0
                     && row.ProjectId == projectId
                     && ids.Contains(row.Id)))
        {
            SoftDelete(row);
            affected++;
        }

        return Task.FromResult(affected);
    }

    public Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        long projectId,
        string status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (BulkFailure is not null)
        {
            return Task.FromException<int>(BulkFailure);
        }

        var affected = 0;
        foreach (var row in _rows.Where(row =>
                     row.ProjectId == projectId && ids.Contains(row.Id)))
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.Status = status;
            affected++;
        }

        return Task.FromResult(affected);
    }

    private static void SoftDelete(PromptRecord row)
    {
        row.UpdatedAt = DateTime.UtcNow;
        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static void ApplyCreateDefaults(PromptRecord row)
    {
        if (row.CreatedAt == default)
        {
            row.CreatedAt = DateTime.UtcNow;
        }

        if (row.UpdatedAt == default)
        {
            row.UpdatedAt = row.CreatedAt;
        }

        row.Status = string.IsNullOrEmpty(row.Status) ? "disabled" : row.Status;
    }

    private static bool Matches(PromptListFilter? filter, PromptRecord row)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is { } id && row.Id != id)
        {
            return false;
        }

        if (filter.IdIn is { } ids && !ids.Contains(row.Id))
        {
            return false;
        }

        if (filter.ProjectId is { } projectId && row.ProjectId != projectId)
        {
            return false;
        }

        if (filter.ProjectIdIn is { } projectIds && !projectIds.Contains(row.ProjectId))
        {
            return false;
        }

        if (filter.Name is { } name && row.Name != name)
        {
            return false;
        }

        if (filter.NameContainsFold is { } nameFragment
            && !row.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Status is { } status && row.Status != status)
        {
            return false;
        }

        if (filter.StatusIn is { } statuses && !statuses.Contains(row.Status))
        {
            return false;
        }

        if (filter.StatusNotIn is { } excludedStatuses
            && excludedStatuses.Contains(row.Status))
        {
            return false;
        }

        if (filter.Not is not null && Matches(filter.Not, row))
        {
            return false;
        }

        if (filter.And is { } andFilters
            && !andFilters.All(nested => Matches(nested, row)))
        {
            return false;
        }

        if (filter.Or is { } orFilters)
        {
            // The port store renders an empty or-list as an always-false
            // predicate (C177).
            if (orFilters.Count == 0 || !orFilters.Any(nested => Matches(nested, row)))
            {
                return false;
            }
        }

        return true;
    }
}
