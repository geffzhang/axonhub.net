using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory IModelStore with ent-like live-row predicates, SET semantics and
/// keyset paging. It mirrors the Dapper store closely enough for the model
/// management service and GraphQL endpoint tests; the SQL rendering itself is
/// covered by ModelStoreTests in AxonHub.Persistence.Tests.
/// </summary>
internal sealed class FakeModelStore : IModelStore
{
    private readonly List<ModelRecord> _rows = [];
    private long _nextId = 1;

    public IReadOnlyList<ModelRecord> Rows => _rows;

    public List<ModelListQuery> Queries { get; } = [];

    public Exception? ListAllFailure { get; set; }

    /// <summary>
    /// Per-filter failure hook, evaluated before <see cref="ListAllFailure"/>.
    /// Lets a test fail only the endpoint's full-data lookup (ModelIdIn set)
    /// while the initial ListEnabledModels query succeeds.
    /// </summary>
    public Func<ModelListFilter, Exception?>? ListAllFailureFor { get; set; }

    public Exception? CreateFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public ModelRecord Seed(Action<ModelRecord>? configure = null)
    {
        var record = new ModelRecord();
        configure?.Invoke(record);
        return Add(record);
    }

    /// <summary>Inserts an already-built row, like the reference's ent Create.</summary>
    public ModelRecord Add(ModelRecord record)
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

    public Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
        => action(cancellationToken);

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => action(cancellationToken);

    public Task<long> CreateAsync(ModelRecord model, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateFailure is not null)
        {
            return Task.FromException<long>(CreateFailure);
        }

        AssignId(model);
        ApplyCreateDefaults(model);
        _rows.Add(model);
        return Task.FromResult(model.Id);
    }

    public Task<IReadOnlyList<ModelRecord>> CreateBulkAsync(
        IReadOnlyList<ModelRecord> models,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        foreach (var model in models)
        {
            AssignId(model);
            ApplyCreateDefaults(model);
            _rows.Add(model);
        }

        return Task.FromResult<IReadOnlyList<ModelRecord>>(models.ToList());
    }

    public Task<ModelRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<ModelRecord?> GetFirstByModelIdAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rows
            .Where(row => row.DeletedAt == 0 && row.ModelId == modelId)
            .OrderBy(static row => row.Id)
            .FirstOrDefault());
    }

    public Task<IReadOnlyList<ModelRecord>> ListByDeveloperModelIdPairsAsync(
        IReadOnlyList<ModelDeveloperModelId> pairs,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (pairs.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        }

        return Task.FromResult<IReadOnlyList<ModelRecord>>(_rows
            .Where(row => row.DeletedAt == 0
                && pairs.Contains(new ModelDeveloperModelId(row.Developer, row.ModelId)))
            .OrderBy(static row => row.Id)
            .ToList());
    }

    public Task<IReadOnlyList<ModelRecord>> ListAllAsync(
        ModelListFilter filter,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var failure = ListAllFailureFor?.Invoke(filter) ?? ListAllFailure;
        if (failure is not null)
        {
            return Task.FromException<IReadOnlyList<ModelRecord>>(failure);
        }

        return Task.FromResult<IReadOnlyList<ModelRecord>>(_rows
            .Where(row => row.DeletedAt == 0 && Matches(filter, row))
            .OrderBy(static row => row.Id)
            .ToList());
    }

    public Task<ListPage<ModelRecord>> ListAsync(
        ModelListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        if (ListAllFailure is not null)
        {
            return Task.FromException<ListPage<ModelRecord>>(ListAllFailure);
        }

        var filtered = _rows
            .Where(row => row.DeletedAt == 0 && Matches(query.Where, row))
            .ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != ModelSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                ModelSortField.UpdatedAt => row.UpdatedAt,
                ModelSortField.Name => row.Name,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<ModelRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<ModelRecord?> UpdateAsync(
        ModelUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateFailure is not null)
        {
            return Task.FromException<ModelRecord?>(UpdateFailure);
        }

        var row = _rows.SingleOrDefault(item => item.Id == update.Id);
        if (row is null)
        {
            return Task.FromResult<ModelRecord?>(null);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.Developer = update.Developer ?? row.Developer;
        row.ModelId = update.ModelId ?? row.ModelId;
        row.Type = update.Type ?? row.Type;
        row.Name = update.Name ?? row.Name;
        row.Icon = update.Icon ?? row.Icon;
        row.Group = update.Group ?? row.Group;
        row.ModelCard = update.ModelCard ?? row.ModelCard;
        row.Settings = update.Settings ?? row.Settings;
        row.Status = update.Status ?? row.Status;
        row.Remark = update.Remark ?? row.Remark;
        if (update.ClearRemark)
        {
            row.Remark = null;
        }

        return Task.FromResult<ModelRecord?>(row);
    }

    public Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var affected = 0;
        foreach (var row in _rows.Where(row => ids.Contains(row.Id)))
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.Status = status;
            affected++;
        }

        return Task.FromResult(affected);
    }

    public Task<int> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var row = _rows.SingleOrDefault(item => item.Id == id && item.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult(0);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Task.FromResult(1);
    }

    public Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        var affected = 0;
        foreach (var row in _rows.Where(row =>
                     row.DeletedAt == 0 && ids.Contains(row.Id)))
        {
            row.UpdatedAt = DateTime.UtcNow;
            row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            affected++;
        }

        return Task.FromResult(affected);
    }

    private void AssignId(ModelRecord model)
    {
        if (model.Id > 0)
        {
            if (model.Id >= _nextId)
            {
                _nextId = model.Id + 1;
            }

            return;
        }

        model.Id = _nextId++;
    }

    private static void ApplyCreateDefaults(ModelRecord model)
    {
        if (model.CreatedAt == default)
        {
            model.CreatedAt = DateTime.UtcNow;
        }

        if (model.UpdatedAt == default)
        {
            model.UpdatedAt = model.CreatedAt;
        }

        model.Type = string.IsNullOrEmpty(model.Type) ? "chat" : model.Type;
        model.Status = string.IsNullOrEmpty(model.Status) ? "disabled" : model.Status;
    }

    private static bool Matches(ModelListFilter? filter, ModelRecord row)
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

        if (filter.Developer is { } developer && row.Developer != developer)
        {
            return false;
        }

        if (filter.DeveloperContainsFold is { } developerFragment
            && !row.Developer.Contains(developerFragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.ModelId is { } modelId && row.ModelId != modelId)
        {
            return false;
        }

        if (filter.ModelIdIn is { } modelIds && !modelIds.Contains(row.ModelId))
        {
            return false;
        }

        if (filter.ModelIdContainsFold is { } modelIdFragment
            && !row.ModelId.Contains(modelIdFragment, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Type is { } type && row.Type != type)
        {
            return false;
        }

        if (filter.TypeIn is { } types && !types.Contains(row.Type))
        {
            return false;
        }

        if (filter.TypeNotIn is { } excludedTypes && excludedTypes.Contains(row.Type))
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

        if (filter.RemarkIsNil && row.Remark is not null)
        {
            return false;
        }

        if (filter.RemarkNotNil && row.Remark is null)
        {
            return false;
        }

        if (filter.Not is not null && Matches(filter.Not, row))
        {
            return false;
        }

        if (filter.And is { Count: > 0 } andFilters
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
