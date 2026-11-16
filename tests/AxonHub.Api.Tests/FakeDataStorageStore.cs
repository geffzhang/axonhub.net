using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory IDataStorageStore with ent-like live-row predicates, SET
/// semantics and keyset paging. It mirrors DataStorageStore closely enough for
/// the data storage management service and GraphQL endpoint tests; the SQL
/// rendering itself is covered by DataStorageStoreTests in
/// AxonHub.Persistence.Tests.
/// </summary>
internal sealed class FakeDataStorageStore : IDataStorageStore
{
    private readonly List<DataStorageRecord> _rows = [];
    private long _nextId = 1;

    public IReadOnlyList<DataStorageRecord> Rows => _rows;

    public List<DataStorageListQuery> Queries { get; } = [];

    public Exception? ListFailure { get; set; }

    public Exception? GetByIdFailure { get; set; }

    public Exception? ExistsFailure { get; set; }

    public Exception? CreateFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public DataStorageRecord Seed(Action<DataStorageRecord>? configure = null)
    {
        var record = new DataStorageRecord();
        configure?.Invoke(record);
        return Add(record);
    }

    /// <summary>Inserts an already-built row, like the reference's ent Create.</summary>
    public DataStorageRecord Add(DataStorageRecord record)
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
        DataStorageRecord storage,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CreateFailure is not null)
        {
            return Task.FromException<long>(CreateFailure);
        }

        Add(storage);
        return Task.FromResult(storage.Id);
    }

    public Task<DataStorageRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetByIdFailure is not null)
        {
            return Task.FromException<DataStorageRecord?>(GetByIdFailure);
        }

        return Task.FromResult(_rows.SingleOrDefault(row =>
            row.Id == id && row.DeletedAt == 0));
    }

    public Task<DataStorageRecord?> GetByIdIncludingDeletedAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rows.SingleOrDefault(row => row.Id == id));
    }

    public Task<DataStorageRecord?> GetPrimaryAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(
            _rows
                .Where(static row => row.Primary && row.DeletedAt == 0)
                .OrderBy(static row => row.Id)
                .FirstOrDefault());
    }

    public Task<DataStorageRecord?> GetPrimaryIncludingDeletedAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(
            _rows
                .Where(static row => row.Primary)
                .OrderBy(static row => row.Id)
                .FirstOrDefault());
    }

    public Task<bool> ExistsByNameAsync(
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
            && row.Name == name
            && (excludeId is null || row.Id != excludeId)));
    }

    public Task<ListPage<DataStorageRecord>> ListAsync(
        DataStorageListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        if (ListFailure is not null)
        {
            return Task.FromException<ListPage<DataStorageRecord>>(ListFailure);
        }

        var filtered = _rows
            .Where(row => row.DeletedAt == 0 && Matches(query.Where, row))
            .ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != DataStorageSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                DataStorageSortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<DataStorageRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<DataStorageRecord?> UpdateAsync(
        DataStorageUpdate update,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (UpdateFailure is not null)
        {
            return Task.FromException<DataStorageRecord?>(UpdateFailure);
        }

        // The generated UPDATE has no deleted_at predicate: only the preceding
        // Get (live rows only) keeps the service away from soft-deleted rows.
        var row = _rows.SingleOrDefault(item => item.Id == update.Id);
        if (row is null)
        {
            return Task.FromResult<DataStorageRecord?>(null);
        }

        row.UpdatedAt = DateTime.UtcNow;
        row.Name = update.Name ?? row.Name;
        row.Description = update.Description ?? row.Description;
        row.Settings = update.Settings ?? row.Settings;
        row.Status = update.Status ?? row.Status;
        return Task.FromResult<DataStorageRecord?>(row);
    }

    private static void ApplyCreateDefaults(DataStorageRecord row)
    {
        if (row.CreatedAt == default)
        {
            row.CreatedAt = DateTime.UtcNow;
        }

        if (row.UpdatedAt == default)
        {
            row.UpdatedAt = row.CreatedAt;
        }

        // Ent schema defaults: primary=false, status=active.
        row.Status = string.IsNullOrEmpty(row.Status) ? "active" : row.Status;
    }

    /// <summary>
    /// Ent's generated P() renders the predicates in schema order and rejects a
    /// nested where input without predicates; empty *In lists install nothing
    /// and the top-level empty input is swallowed by entgql.
    /// </summary>
    private static bool Matches(DataStorageListFilter? filter, DataStorageRecord row)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Not is not null)
        {
            if (IsEmpty(filter.Not))
            {
                throw EmptyWhere("not");
            }

            if (Matches(filter.Not, row))
            {
                return false;
            }
        }

        if (filter.Or is { Count: > 0 } orFilters)
        {
            if (orFilters.Any(IsEmpty))
            {
                throw EmptyWhere("or");
            }

            if (!orFilters.Any(nested => Matches(nested, row)))
            {
                return false;
            }
        }

        if (filter.And is { Count: > 0 } andFilters)
        {
            if (andFilters.Any(IsEmpty))
            {
                throw EmptyWhere("and");
            }

            if (!andFilters.All(nested => Matches(nested, row)))
            {
                return false;
            }
        }

        if (filter.Id is { } id)
        {
            if (row.Id != id)
            {
                return false;
            }
        }

        if (filter.IdNEQ is { } idNeq)
        {
            if (row.Id == idNeq)
            {
                return false;
            }
        }

        if (filter.IdIn is { Count: > 0 } ids)
        {
            if (!ids.Contains(row.Id))
            {
                return false;
            }
        }

        if (filter.IdNotIn is { Count: > 0 } excludedIds)
        {
            if (excludedIds.Contains(row.Id))
            {
                return false;
            }
        }

        if (filter.Name is { } name)
        {
            if (row.Name != name)
            {
                return false;
            }
        }

        if (filter.NameNEQ is { } nameNeq)
        {
            if (row.Name == nameNeq)
            {
                return false;
            }
        }

        if (filter.NameIn is { Count: > 0 } names)
        {
            if (!names.Contains(row.Name))
            {
                return false;
            }
        }

        if (filter.NameNotIn is { Count: > 0 } excludedNames)
        {
            if (excludedNames.Contains(row.Name))
            {
                return false;
            }
        }

        if (filter.NameContainsFold is { } nameFragment)
        {
            if (!row.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // GraphQL Boolean binds to *bool, so false is a predicate too.
        if (filter.Primary is { } primary)
        {
            if (row.Primary != primary)
            {
                return false;
            }
        }

        if (filter.PrimaryNEQ is { } primaryNeq)
        {
            if (row.Primary == primaryNeq)
            {
                return false;
            }
        }

        if (filter.Type is { } type)
        {
            if (row.Type != type)
            {
                return false;
            }
        }

        if (filter.TypeNEQ is { } typeNeq)
        {
            if (row.Type == typeNeq)
            {
                return false;
            }
        }

        if (filter.TypeIn is { Count: > 0 } types)
        {
            if (!types.Contains(row.Type))
            {
                return false;
            }
        }

        if (filter.TypeNotIn is { Count: > 0 } excludedTypes)
        {
            if (excludedTypes.Contains(row.Type))
            {
                return false;
            }
        }

        if (filter.Status is { } status)
        {
            if (row.Status != status)
            {
                return false;
            }
        }

        if (filter.StatusNEQ is { } statusNeq)
        {
            if (row.Status == statusNeq)
            {
                return false;
            }
        }

        if (filter.StatusIn is { Count: > 0 } statuses)
        {
            if (!statuses.Contains(row.Status))
            {
                return false;
            }
        }

        if (filter.StatusNotIn is { Count: > 0 } excludedStatuses)
        {
            if (excludedStatuses.Contains(row.Status))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEmpty(DataStorageListFilter filter)
        => filter.Not is null
            && filter.Id is null
            && filter.IdNEQ is null
            && filter.IdIn is not { Count: > 0 }
            && filter.IdNotIn is not { Count: > 0 }
            && filter.Name is null
            && filter.NameNEQ is null
            && filter.NameIn is not { Count: > 0 }
            && filter.NameNotIn is not { Count: > 0 }
            && filter.NameContainsFold is null
            && filter.Primary is null
            && filter.PrimaryNEQ is null
            && filter.Type is null
            && filter.TypeNEQ is null
            && filter.TypeIn is not { Count: > 0 }
            && filter.TypeNotIn is not { Count: > 0 }
            && filter.Status is null
            && filter.StatusNEQ is null
            && filter.StatusIn is not { Count: > 0 }
            && filter.StatusNotIn is not { Count: > 0 }
            && filter.And is not { Count: > 0 }
            && filter.Or is not { Count: > 0 };

    private static InvalidOperationException EmptyWhere(string field)
        => new("ent: empty predicate DataStorageWhereInput: field '" + field + "'");
}
