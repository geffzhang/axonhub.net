using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory <see cref="ITraceAdminStore"/> that mimics the entgql paging and
/// the predicates of the ported <see cref="TraceListFilter"/>. Every query is
/// recorded so the endpoint tests can assert the sort field and scope mapping
/// (root CREATED_AT→id rewrite, edge created_at, C189 project scopes).
/// </summary>
internal sealed class FakeTraceAdminStore : ITraceAdminStore
{
    private readonly List<TraceRecord> _rows = [];
    private long _nextId = 1;

    public List<TraceListQuery> Queries { get; } = [];

    public List<(long Id, long? ProjectScope, string From, string To)> Updates { get; } = [];

    public Exception? ListFailure { get; set; }

    public Exception? GetByIdFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public Exception? ThreadFirstTraceFailure { get; set; }

    public Exception? CountArchivedFailure { get; set; }

    public TraceRecord Seed(Action<TraceRecord>? configure = null)
    {
        var record = new TraceRecord();
        configure?.Invoke(record);
        record.Id = record.Id > 0 ? record.Id : _nextId++;
        if (record.Id >= _nextId)
        {
            _nextId = record.Id + 1;
        }

        _rows.Add(record);
        return record;
    }

    public Task<TraceRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetByIdFailure is not null)
        {
            throw GetByIdFailure;
        }

        return Task.FromResult(_rows.SingleOrDefault(
            row => row.Id == id
                && (projectScope is null || row.ProjectId == projectScope.Value)));
    }

    public Task<ListPage<TraceRecord>> ListAsync(
        TraceListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        var filtered = _rows
            .Where(row => query.ProjectScope is null || row.ProjectId == query.ProjectScope.Value)
            .Where(row => query.ThreadScope is null || row.ThreadId == query.ThreadScope.Value)
            .Where(row => Matches(row, query.Where))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField != TraceSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                TraceSortField.CreatedAt => row.CreatedAt,
                TraceSortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<TraceRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<int> UpdateStatusAsync(
        long id,
        long? projectScope,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Updates.Add((id, projectScope, fromStatus, toStatus));
        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        var row = _rows.SingleOrDefault(
            row => row.Id == id
                && row.Status == fromStatus
                && (projectScope is null || row.ProjectId == projectScope.Value));
        if (row is null)
        {
            return Task.FromResult(0);
        }

        row.Status = toStatus;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(1);
    }

    public Task<TraceRecord?> GetThreadFirstTraceAsync(
        long threadId,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (ThreadFirstTraceFailure is not null)
        {
            throw ThreadFirstTraceFailure;
        }

        return Task.FromResult(_rows
            .Where(row => row.ThreadId == threadId)
            .Where(row => projectScope is null || row.ProjectId == projectScope.Value)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .FirstOrDefault());
    }

    public Task<TraceRecord?> GetPreviousTraceAsync(
        long threadId,
        long currentTraceId,
        DateTime currentCreatedAt,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rows
            .Where(row => row.ThreadId == threadId)
            .Where(row => row.Id != currentTraceId && row.CreatedAt < currentCreatedAt)
            .Where(row => projectScope is null || row.ProjectId == projectScope.Value)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .FirstOrDefault());
    }

    public Task<long> CountArchivedByThreadAsync(
        long threadId,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (CountArchivedFailure is not null)
        {
            throw CountArchivedFailure;
        }

        return Task.FromResult((long)_rows.Count(
            row => row.ThreadId == threadId
                && row.Status == "archived"
                && (projectScope is null || row.ProjectId == projectScope.Value)));
    }

    private static bool Matches(TraceRecord row, TraceListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return MatchId(row, filter)
            && MatchTime(row.CreatedAt, filter.CreatedAt, filter.CreatedAtNEQ,
                filter.CreatedAtIn, filter.CreatedAtNotIn, filter.CreatedAtGT,
                filter.CreatedAtGTE, filter.CreatedAtLT, filter.CreatedAtLTE)
            && MatchTime(row.UpdatedAt, filter.UpdatedAt, filter.UpdatedAtNEQ,
                filter.UpdatedAtIn, filter.UpdatedAtNotIn, filter.UpdatedAtGT,
                filter.UpdatedAtGTE, filter.UpdatedAtLT, filter.UpdatedAtLTE)
            && MatchProject(row, filter)
            && MatchString(row.TraceId, filter.TraceId, filter.TraceIdNEQ,
                filter.TraceIdIn, filter.TraceIdNotIn, filter.TraceIdGT, filter.TraceIdGTE,
                filter.TraceIdLT, filter.TraceIdLTE, filter.TraceIdContains,
                filter.TraceIdContainsFold, filter.TraceIdPrefix, filter.TraceIdSuffix,
                filter.TraceIdEqualFold)
            && MatchThread(row, filter)
            && MatchStatus(row.Status, filter.Status, filter.StatusNEQ,
                filter.StatusIn, filter.StatusNotIn)
            && (filter.Not is null || !Matches(row, filter.Not))
            && (filter.And is null || filter.And.All(nested => Matches(row, nested)))
            && (filter.Or is null
                || filter.Or.Count == 0
                || filter.Or.Any(nested => Matches(row, nested)));
    }

    private static bool MatchId(TraceRecord row, TraceListFilter filter)
        => (filter.Id is null || row.Id == filter.Id.Value)
            && (filter.IdNEQ is null || row.Id != filter.IdNEQ.Value)
            && (filter.IdIn is null
                || filter.IdIn.Count == 0
                || filter.IdIn.Contains(row.Id))
            && (filter.IdNotIn is null
                || filter.IdNotIn.Count == 0
                || !filter.IdNotIn.Contains(row.Id))
            && (filter.IdGT is null || row.Id > filter.IdGT.Value)
            && (filter.IdGTE is null || row.Id >= filter.IdGTE.Value)
            && (filter.IdLT is null || row.Id < filter.IdLT.Value)
            && (filter.IdLTE is null || row.Id <= filter.IdLTE.Value);

    private static bool MatchProject(TraceRecord row, TraceListFilter filter)
        => (filter.ProjectId is null || row.ProjectId == filter.ProjectId.Value)
            && (filter.ProjectIdNEQ is null || row.ProjectId != filter.ProjectIdNEQ.Value)
            && (filter.ProjectIdIn is null
                || filter.ProjectIdIn.Count == 0
                || filter.ProjectIdIn.Contains(row.ProjectId))
            && (filter.ProjectIdNotIn is null
                || filter.ProjectIdNotIn.Count == 0
                || !filter.ProjectIdNotIn.Contains(row.ProjectId));

    private static bool MatchThread(TraceRecord row, TraceListFilter filter)
    {
        if (filter.ThreadIdIsNil == true && row.ThreadId is not null)
        {
            return false;
        }

        if (filter.ThreadIdNotNil == true && row.ThreadId is null)
        {
            return false;
        }

        return filter.ThreadId is null
            || (row.ThreadId is { } threadId
                && threadId == filter.ThreadId.Value
                && (filter.ThreadIdNEQ is null || threadId != filter.ThreadIdNEQ.Value)
                && (filter.ThreadIdIn is null
                    || filter.ThreadIdIn.Count == 0
                    || filter.ThreadIdIn.Contains(threadId))
                && (filter.ThreadIdNotIn is null
                    || filter.ThreadIdNotIn.Count == 0
                    || !filter.ThreadIdNotIn.Contains(threadId)));
    }

    private static bool MatchStatus(
        string status,
        string? value,
        string? notEqual,
        IReadOnlyList<string>? inList,
        IReadOnlyList<string>? notInList)
        => (value is null || status == value)
            && (notEqual is null || status != notEqual)
            && (inList is null || inList.Count == 0 || inList.Contains(status))
            && (notInList is null || notInList.Count == 0 || !notInList.Contains(status));

    private static bool MatchTime(
        DateTime value,
        DateTime? equal,
        DateTime? notEqual,
        IReadOnlyList<DateTime>? inList,
        IReadOnlyList<DateTime>? notInList,
        DateTime? greaterThan,
        DateTime? greaterThanOrEqual,
        DateTime? lessThan,
        DateTime? lessThanOrEqual)
        => (equal is null || value == equal.Value)
            && (notEqual is null || value != notEqual.Value)
            && (inList is null || inList.Count == 0 || inList.Contains(value))
            && (notInList is null || notInList.Count == 0 || !notInList.Contains(value))
            && (greaterThan is null || value > greaterThan.Value)
            && (greaterThanOrEqual is null || value >= greaterThanOrEqual.Value)
            && (lessThan is null || value < lessThan.Value)
            && (lessThanOrEqual is null || value <= lessThanOrEqual.Value);

    private static bool MatchString(
        string value,
        string? equal,
        string? notEqual,
        IReadOnlyList<string>? inList,
        IReadOnlyList<string>? notInList,
        string? greaterThan,
        string? greaterThanOrEqual,
        string? lessThan,
        string? lessThanOrEqual,
        string? contains,
        string? containsFold,
        string? prefix,
        string? suffix,
        string? equalFold)
        => (equal is null || string.Equals(value, equal, StringComparison.Ordinal))
            && (notEqual is null || !string.Equals(value, notEqual, StringComparison.Ordinal))
            && (inList is null || inList.Count == 0 || inList.Contains(value, StringComparer.Ordinal))
            && (notInList is null
                || notInList.Count == 0
                || !notInList.Contains(value, StringComparer.Ordinal))
            && (greaterThan is null || string.CompareOrdinal(value, greaterThan) > 0)
            && (greaterThanOrEqual is null
                || string.CompareOrdinal(value, greaterThanOrEqual) >= 0)
            && (lessThan is null || string.CompareOrdinal(value, lessThan) < 0)
            && (lessThanOrEqual is null || string.CompareOrdinal(value, lessThanOrEqual) <= 0)
            && (contains is null || value.Contains(contains, StringComparison.Ordinal))
            && (containsFold is null
                || value.Contains(containsFold, StringComparison.OrdinalIgnoreCase))
            && (prefix is null || value.StartsWith(prefix, StringComparison.Ordinal))
            && (suffix is null || value.EndsWith(suffix, StringComparison.Ordinal))
            && (equalFold is null || string.Equals(value, equalFold, StringComparison.OrdinalIgnoreCase));
}

/// <summary>In-memory <see cref="IThreadAdminStore"/> with cascade failure injection.</summary>
internal sealed class FakeThreadAdminStore : IThreadAdminStore
{
    private readonly List<ThreadRecord> _rows = [];
    private long _nextId = 1;

    public List<ThreadListQuery> Queries { get; } = [];

    public List<(long Id, long? ProjectScope, string From, string To)> Updates { get; } = [];

    public Exception? ListFailure { get; set; }

    public Exception? GetByIdFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    /// <summary>When set, UpdateStatusAndCascadeAsync throws it wrapped like a failed trace cascade.</summary>
    public Exception? CascadeFailure { get; set; }

    public ThreadRecord Seed(Action<ThreadRecord>? configure = null)
    {
        var record = new ThreadRecord();
        configure?.Invoke(record);
        record.Id = record.Id > 0 ? record.Id : _nextId++;
        if (record.Id >= _nextId)
        {
            _nextId = record.Id + 1;
        }

        _rows.Add(record);
        return record;
    }

    public Task<ThreadRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (GetByIdFailure is not null)
        {
            throw GetByIdFailure;
        }

        return Task.FromResult(_rows.SingleOrDefault(
            row => row.Id == id
                && (projectScope is null || row.ProjectId == projectScope.Value)));
    }

    public Task<ListPage<ThreadRecord>> ListAsync(
        ThreadListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        var filtered = _rows
            .Where(row => query.ProjectScope is null || row.ProjectId == query.ProjectScope.Value)
            .Where(row => Matches(row, query.Where))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField != ThreadSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                ThreadSortField.CreatedAt => row.CreatedAt,
                ThreadSortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<ThreadRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<int> UpdateStatusAndCascadeAsync(
        long id,
        long? projectScope,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Updates.Add((id, projectScope, fromStatus, toStatus));
        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        var row = _rows.SingleOrDefault(
            row => row.Id == id
                && row.Status == fromStatus
                && (projectScope is null || row.ProjectId == projectScope.Value));
        if (row is null)
        {
            return Task.FromResult(0);
        }

        if (CascadeFailure is not null)
        {
            throw new TraceCascadeException(CascadeFailure);
        }

        row.Status = toStatus;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(1);
    }

    private static bool Matches(ThreadRecord row, ThreadListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return (filter.Id is null || row.Id == filter.Id.Value)
            && (filter.IdNEQ is null || row.Id != filter.IdNEQ.Value)
            && (filter.IdIn is null || filter.IdIn.Count == 0 || filter.IdIn.Contains(row.Id))
            && (filter.IdNotIn is null
                || filter.IdNotIn.Count == 0
                || !filter.IdNotIn.Contains(row.Id))
            && (filter.IdGT is null || row.Id > filter.IdGT.Value)
            && (filter.IdGTE is null || row.Id >= filter.IdGTE.Value)
            && (filter.IdLT is null || row.Id < filter.IdLT.Value)
            && (filter.IdLTE is null || row.Id <= filter.IdLTE.Value)
            && (filter.ProjectId is null || row.ProjectId == filter.ProjectId.Value)
            && (filter.ProjectIdNEQ is null || row.ProjectId != filter.ProjectIdNEQ.Value)
            && (filter.ProjectIdIn is null
                || filter.ProjectIdIn.Count == 0
                || filter.ProjectIdIn.Contains(row.ProjectId))
            && (filter.ProjectIdNotIn is null
                || filter.ProjectIdNotIn.Count == 0
                || !filter.ProjectIdNotIn.Contains(row.ProjectId))
            && MatchString(row.ThreadId, filter.ThreadId, filter.ThreadIdNEQ,
                filter.ThreadIdIn, filter.ThreadIdNotIn, filter.ThreadIdGT,
                filter.ThreadIdGTE, filter.ThreadIdLT, filter.ThreadIdLTE,
                filter.ThreadIdContains, filter.ThreadIdContainsFold,
                filter.ThreadIdPrefix, filter.ThreadIdSuffix, filter.ThreadIdEqualFold)
            && MatchStatus(row.Status, filter.Status, filter.StatusNEQ,
                filter.StatusIn, filter.StatusNotIn)
            && (filter.Not is null || !Matches(row, filter.Not))
            && (filter.And is null || filter.And.All(nested => Matches(row, nested)))
            && (filter.Or is null
                || filter.Or.Count == 0
                || filter.Or.Any(nested => Matches(row, nested)));
    }

    private static bool MatchStatus(
        string status,
        string? value,
        string? notEqual,
        IReadOnlyList<string>? inList,
        IReadOnlyList<string>? notInList)
        => (value is null || status == value)
            && (notEqual is null || status != notEqual)
            && (inList is null || inList.Count == 0 || inList.Contains(status))
            && (notInList is null || notInList.Count == 0 || !notInList.Contains(status));

    private static bool MatchString(
        string value,
        string? equal,
        string? notEqual,
        IReadOnlyList<string>? inList,
        IReadOnlyList<string>? notInList,
        string? greaterThan,
        string? greaterThanOrEqual,
        string? lessThan,
        string? lessThanOrEqual,
        string? contains,
        string? containsFold,
        string? prefix,
        string? suffix,
        string? equalFold)
        => (equal is null || string.Equals(value, equal, StringComparison.Ordinal))
            && (notEqual is null || !string.Equals(value, notEqual, StringComparison.Ordinal))
            && (inList is null || inList.Count == 0 || inList.Contains(value, StringComparer.Ordinal))
            && (notInList is null
                || notInList.Count == 0
                || !notInList.Contains(value, StringComparer.Ordinal))
            && (greaterThan is null || string.CompareOrdinal(value, greaterThan) > 0)
            && (greaterThanOrEqual is null
                || string.CompareOrdinal(value, greaterThanOrEqual) >= 0)
            && (lessThan is null || string.CompareOrdinal(value, lessThan) < 0)
            && (lessThanOrEqual is null || string.CompareOrdinal(value, lessThanOrEqual) <= 0)
            && (contains is null || value.Contains(contains, StringComparison.Ordinal))
            && (containsFold is null
                || value.Contains(containsFold, StringComparison.OrdinalIgnoreCase))
            && (prefix is null || value.StartsWith(prefix, StringComparison.Ordinal))
            && (suffix is null || value.EndsWith(suffix, StringComparison.Ordinal))
            && (equalFold is null || string.Equals(value, equalFold, StringComparison.OrdinalIgnoreCase));
}

/// <summary>In-memory usage aggregates keyed by trace/thread database id.</summary>
internal sealed class FakeUsageMetadataStore : IUsageMetadataStore
{
    public Dictionary<long, UsageMetadataRow> Traces { get; } = [];

    public Dictionary<long, UsageMetadataRow> Threads { get; } = [];

    public Exception? TraceFailure { get; set; }

    public Exception? ThreadFailure { get; set; }

    public Task<UsageMetadataRow> AggregateForTraceAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (TraceFailure is not null)
        {
            throw TraceFailure;
        }

        return Task.FromResult(Traces.TryGetValue(traceDbId, out var row)
            ? row
            : new UsageMetadataRow(0, 0, 0, 0, 0, 0));
    }

    public Task<UsageMetadataRow> AggregateForThreadAsync(
        long threadDbId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        if (ThreadFailure is not null)
        {
            throw ThreadFailure;
        }

        return Task.FromResult(Threads.TryGetValue(threadDbId, out var row)
            ? row
            : new UsageMetadataRow(0, 0, 0, 0, 0, 0));
    }
}

/// <summary>In-memory request store for the Trace.requests edge and segment lookups.</summary>
internal sealed class FakeRequestSegmentStore : IRequestSegmentStore
{
    private readonly List<RequestRecord> _rows = [];

    public List<RequestListQuery> Queries { get; } = [];

    public List<(long TraceId, long? ProjectScope, long? VisibilityUserId)> CompletedListReads { get; } = [];

    public List<(long TraceId, long? ProjectScope, long? VisibilityUserId)> FirstReads { get; } = [];

    public List<(long TraceId, long? ProjectScope, long? VisibilityUserId)> LastReads { get; } = [];

    public Exception? ListFailure { get; set; }

    public Exception? CompletedListFailure { get; set; }

    public RequestRecord Seed(Action<RequestRecord>? configure = null)
    {
        var record = new RequestRecord();
        configure?.Invoke(record);
        _rows.Add(record);
        return record;
    }

    public Task<IReadOnlyList<RequestRecord>> ListCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        CompletedListReads.Add((traceDbId, projectScope, visibilityUserId));
        if (CompletedListFailure is not null)
        {
            throw CompletedListFailure;
        }

        IReadOnlyList<RequestRecord> rows = _rows
            .Where(row => row.TraceId == traceDbId && row.Status == "completed")
            .Where(row => projectScope is null || row.ProjectId == projectScope.Value)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<RequestRecord?> GetFirstCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        FirstReads.Add((traceDbId, projectScope, visibilityUserId));
        return Task.FromResult(_rows
            .Where(row => row.TraceId == traceDbId && row.Status == "completed")
            .Where(row => projectScope is null || row.ProjectId == projectScope.Value)
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .FirstOrDefault());
    }

    public Task<RequestRecord?> GetLastCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        LastReads.Add((traceDbId, projectScope, visibilityUserId));
        return Task.FromResult(_rows
            .Where(row => row.TraceId == traceDbId && row.Status == "completed")
            .Where(row => projectScope is null || row.ProjectId == projectScope.Value)
            .OrderByDescending(row => row.CreatedAt)
            .ThenByDescending(row => row.Id)
            .FirstOrDefault());
    }

    public Task<ListPage<RequestRecord>> ListAsync(
        RequestListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        var filtered = _rows
            .Where(row => query.TraceScope is null || row.TraceId == query.TraceScope.Value)
            .Where(row => query.ThreadScope is null)
            .Where(row => query.ProjectScope is null || row.ProjectId == query.ProjectScope.Value)
            .Where(row => Matches(row, query.Where))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField != RequestSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                RequestSortField.CreatedAt => row.CreatedAt,
                RequestSortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<RequestRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    private static bool Matches(RequestRecord row, RequestListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return (filter.Id is null || row.Id == filter.Id.Value)
            && (filter.IdNEQ is null || row.Id != filter.IdNEQ.Value)
            && (filter.IdIn is null || filter.IdIn.Count == 0 || filter.IdIn.Contains(row.Id))
            && (filter.IdNotIn is null
                || filter.IdNotIn.Count == 0
                || !filter.IdNotIn.Contains(row.Id))
            && (filter.IdGT is null || row.Id > filter.IdGT.Value)
            && (filter.IdGTE is null || row.Id >= filter.IdGTE.Value)
            && (filter.IdLT is null || row.Id < filter.IdLT.Value)
            && (filter.IdLTE is null || row.Id <= filter.IdLTE.Value)
            && (filter.CreatedAt is null || row.CreatedAt == filter.CreatedAt.Value)
            && (filter.CreatedAtNEQ is null || row.CreatedAt != filter.CreatedAtNEQ.Value)
            && (filter.CreatedAtIn is null
                || filter.CreatedAtIn.Count == 0
                || filter.CreatedAtIn.Contains(row.CreatedAt))
            && (filter.CreatedAtNotIn is null
                || filter.CreatedAtNotIn.Count == 0
                || !filter.CreatedAtNotIn.Contains(row.CreatedAt))
            && (filter.CreatedAtGT is null || row.CreatedAt > filter.CreatedAtGT.Value)
            && (filter.CreatedAtGTE is null || row.CreatedAt >= filter.CreatedAtGTE.Value)
            && (filter.CreatedAtLT is null || row.CreatedAt < filter.CreatedAtLT.Value)
            && (filter.CreatedAtLTE is null || row.CreatedAt <= filter.CreatedAtLTE.Value)
            && (filter.UpdatedAt is null || row.UpdatedAt == filter.UpdatedAt.Value)
            && (filter.UpdatedAtNEQ is null || row.UpdatedAt != filter.UpdatedAtNEQ.Value)
            && (filter.UpdatedAtIn is null
                || filter.UpdatedAtIn.Count == 0
                || filter.UpdatedAtIn.Contains(row.UpdatedAt))
            && (filter.UpdatedAtNotIn is null
                || filter.UpdatedAtNotIn.Count == 0
                || !filter.UpdatedAtNotIn.Contains(row.UpdatedAt))
            && (filter.UpdatedAtGT is null || row.UpdatedAt > filter.UpdatedAtGT.Value)
            && (filter.UpdatedAtGTE is null || row.UpdatedAt >= filter.UpdatedAtGTE.Value)
            && (filter.UpdatedAtLT is null || row.UpdatedAt < filter.UpdatedAtLT.Value)
            && (filter.UpdatedAtLTE is null || row.UpdatedAt <= filter.UpdatedAtLTE.Value)
            && (filter.ProjectId is null || row.ProjectId == filter.ProjectId.Value)
            && (filter.ProjectIdNEQ is null || row.ProjectId != filter.ProjectIdNEQ.Value)
            && (filter.Status is null || row.Status == filter.Status)
            && (filter.StatusNEQ is null || row.Status != filter.StatusNEQ)
            && (filter.StatusIn is null
                || filter.StatusIn.Count == 0
                || filter.StatusIn.Contains(row.Status))
            && (filter.StatusNotIn is null
                || filter.StatusNotIn.Count == 0
                || !filter.StatusNotIn.Contains(row.Status))
            && (filter.Not is null || !Matches(row, filter.Not))
            && (filter.And is null || filter.And.All(nested => Matches(row, nested)))
            && (filter.Or is null
                || filter.Or.Count == 0
                || filter.Or.Any(nested => Matches(row, nested)));
    }
}
