using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory IPromptProtectionRuleStore with the reference's live-row
/// predicates, SET semantics and keyset paging. It mirrors the Dapper store
/// closely enough for the management service and GraphQL endpoint tests; the
/// SQL rendering itself is covered by PromptProtectionRuleStoreTests in
/// AxonHub.Persistence.Tests.
/// </summary>
internal sealed class FakePromptProtectionRuleStore : IPromptProtectionRuleStore
{
    private readonly List<PromptProtectionRuleRecord> _rows = [];
    private long _nextId = 1;

    public IReadOnlyList<PromptProtectionRuleRecord> Rows => _rows;

    public List<PromptProtectionRuleListQuery> Queries { get; } = [];

    public Exception? ListFailure { get; set; }

    public Exception? GetByIdFailure { get; set; }

    public Exception? ExistsFailure { get; set; }

    public Exception? CreateFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? BulkFailure { get; set; }

    public PromptProtectionRuleRecord Seed(Action<PromptProtectionRuleRecord>? configure = null)
    {
        var record = new PromptProtectionRuleRecord();
        configure?.Invoke(record);
        return Add(record);
    }

    /// <summary>Inserts an already-built row, like the reference's ent Create.</summary>
    public PromptProtectionRuleRecord Add(PromptProtectionRuleRecord record)
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

    public Task<long> CreateAsync(
        PromptProtectionRuleRecord rule,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateFailure is not null)
        {
            return Task.FromException<long>(CreateFailure);
        }

        Add(rule);
        return Task.FromResult(rule.Id);
    }

    public Task<IReadOnlyList<PromptProtectionRuleRecord>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<IReadOnlyList<PromptProtectionRuleRecord>>(_rows
            .Where(row => row.DeletedAt == 0 && row.Status == "enabled")
            .OrderBy(row => row.Id)
            .ToList());
    }

    public Task<PromptProtectionRuleRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetByIdFailure is not null)
        {
            return Task.FromException<PromptProtectionRuleRecord?>(GetByIdFailure);
        }

        return Task.FromResult(_rows.SingleOrDefault(row =>
            row.Id == id && row.DeletedAt == 0));
    }

    public Task<bool> ExistsByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (ExistsFailure is not null)
        {
            return Task.FromException<bool>(ExistsFailure);
        }

        return Task.FromResult(_rows.Any(row =>
            row.DeletedAt == 0 && row.Name == name));
    }

    public Task<ListPage<PromptProtectionRuleRecord>> ListAsync(
        PromptProtectionRuleListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        if (ListFailure is not null)
        {
            return Task.FromException<ListPage<PromptProtectionRuleRecord>>(ListFailure);
        }

        var filtered = _rows
            .Where(row => row.DeletedAt == 0 && Matches(query.Where, row))
            .ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != PromptProtectionRuleSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                PromptProtectionRuleSortField.UpdatedAt => row.UpdatedAt,
                PromptProtectionRuleSortField.Name => row.Name,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<PromptProtectionRuleRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<PromptProtectionRuleRecord?> UpdateAsync(
        PromptProtectionRuleUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateFailure is not null)
        {
            return Task.FromException<PromptProtectionRuleRecord?>(UpdateFailure);
        }

        // The aggregate update has no soft-delete predicate.
        var row = _rows.SingleOrDefault(item => item.Id == update.Id);
        if (row is null)
        {
            return Task.FromResult<PromptProtectionRuleRecord?>(null);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.Name = update.Name ?? row.Name;
        row.Description = update.Description ?? row.Description;
        row.Pattern = update.Pattern ?? row.Pattern;
        row.Status = update.Status ?? row.Status;
        row.Settings = update.Settings ?? row.Settings;
        return Task.FromResult<PromptProtectionRuleRecord?>(row);
    }

    public Task<PromptProtectionRuleRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateFailure is not null)
        {
            return Task.FromException<PromptProtectionRuleRecord?>(UpdateFailure);
        }

        // No live-row predicate, like the reference's UpdateOneID.
        var row = _rows.SingleOrDefault(item => item.Id == id);
        if (row is null)
        {
            return Task.FromResult<PromptProtectionRuleRecord?>(null);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.Status = status;
        return Task.FromResult<PromptProtectionRuleRecord?>(row);
    }

    public Task<int> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (DeleteFailure is not null)
        {
            return Task.FromException<int>(DeleteFailure);
        }

        var row = _rows.SingleOrDefault(item => item.Id == id && item.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult(0);
        }

        SoftDelete(row);
        return Task.FromResult(1);
    }

    public Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (BulkFailure is not null)
        {
            return Task.FromException<int>(BulkFailure);
        }

        var affected = 0;
        foreach (var row in _rows.Where(row =>
                     row.DeletedAt == 0 && ids.Contains(row.Id)))
        {
            SoftDelete(row);
            affected++;
        }

        return Task.FromResult(affected);
    }

    public Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (BulkFailure is not null)
        {
            return Task.FromException<int>(BulkFailure);
        }

        var affected = 0;
        foreach (var row in _rows.Where(row => ids.Contains(row.Id)))
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.Status = status;
            affected++;
        }

        return Task.FromResult(affected);
    }

    private static void SoftDelete(PromptProtectionRuleRecord row)
    {
        row.UpdatedAt = DateTime.UtcNow;
        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    private static void ApplyCreateDefaults(PromptProtectionRuleRecord row)
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

    private static bool Matches(PromptProtectionRuleListFilter? filter, PromptProtectionRuleRecord row)
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
