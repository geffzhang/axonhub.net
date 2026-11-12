using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

internal sealed record SnapshotWrite(
    long Id,
    string Status,
    string ExternalId,
    string ResponseBody,
    DateTime UpdatedAt);

internal sealed record StatusWrite(long Id, string Status, DateTime UpdatedAt);

/// <summary>
/// In-memory port of the <c>requests</c> store for the video task flow. The
/// reference fixture (biz/video_test.go) runs VideoService over a real ent
/// client; the port's service reads the plain store seams, so these fakes keep
/// the flow tests database-free (the store SQL stays locked by
/// AxonHub.Persistence.Tests).
/// </summary>
internal sealed class FakeRequestStore : IRequestStore
{
    private long _nextId = 1000;

    public List<RequestRecord> Rows { get; } = [];

    public List<SnapshotWrite> Snapshots { get; } = [];

    public List<StatusWrite> StatusUpdates { get; } = [];

    /// <summary>Simulates a failed snapshot write (biz/video.go keeps the data).</summary>
    public bool ThrowOnSnapshot { get; set; }

    /// <summary>
    /// Simulates a failed status write; fatal for the ZenMux local-cancel path
    /// (biz/video.go:126-131), ignored for the best-effort cancel.
    /// </summary>
    public bool ThrowOnStatusUpdate { get; set; }

    public RequestRecord Add(RequestRecord row)
    {
        if (row.Id == 0)
        {
            row.Id = _nextId++;
        }

        Rows.Add(row);
        return row;
    }

    public Task<long> CreateAsync(RequestRecord request, CancellationToken cancellationToken = default)
        => Task.FromResult(Add(request).Id);

    /// <summary>Simulates a failed row read (ent returns a non-NotFound error).</summary>
    public Exception? GetByIdFailure { get; set; }

    public Task<RequestRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => GetByIdFailure is null
            ? Task.FromResult(Rows.FirstOrDefault(row => row.Id == id))
            : Task.FromException<RequestRecord?>(GetByIdFailure);

    public Task<RequestRecord?> GetByExternalIdAndProjectAsync(
        string externalId,
        long projectId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Rows.FirstOrDefault(
            row => row.ExternalId == externalId && row.ProjectId == projectId));

    public Task<bool> UpdateStatusExternalIdAndResponseBodyAsync(
        long id,
        string status,
        string externalId,
        string? responseBody,
        bool setResponseBody,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnSnapshot)
        {
            throw new InvalidOperationException("snapshot write failed");
        }

        var row = Rows.FirstOrDefault(row => row.Id == id);
        Snapshots.Add(new SnapshotWrite(
            id,
            status,
            externalId,
            setResponseBody ? responseBody ?? "{}" : row?.ResponseBody ?? string.Empty,
            updatedAt));

        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.Status = status;
        row.ExternalId = externalId;
        if (setResponseBody)
        {
            row.ResponseBody = responseBody;
        }

        row.UpdatedAt = updatedAt;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateStatusAsync(
        long id,
        string status,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnStatusUpdate)
        {
            throw new InvalidOperationException("status update failed");
        }

        StatusUpdates.Add(new StatusWrite(id, status, updatedAt));

        var row = Rows.FirstOrDefault(row => row.Id == id);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.Status = status;
        row.UpdatedAt = updatedAt;
        return Task.FromResult(true);
    }

    public Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? responseBody,
        bool setResponseBody,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<bool> UpdateChannelAsync(
        long id,
        long? channelId,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    /// <summary>The rows the video-storage scan would pick up, oldest first.</summary>
    public List<RequestRecord> UnsavedVideoRows(int limit)
        => Rows
            .Where(row => row.Status is "processing" or "completed")
            .Where(row => row.Format is "openai/video" or "seedance/video")
            .Where(row => !row.ContentSaved)
            .OrderBy(row => row.Id)
            .Take(limit)
            .ToList();

    /// <summary>Records of the content-columns writes the scan performed.</summary>
    public List<ContentSavedWrite> ContentSavedWrites { get; } = [];

    public Exception? MarkContentSavedFailure { get; set; }

    /// <summary>Simulates a failed scan query (worker.go wraps it).</summary>
    public Exception? ListUnsavedVideoRequestsFailure { get; set; }

    public Task<IReadOnlyList<RequestRecord>> ListUnsavedVideoRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default)
        => ListUnsavedVideoRequestsFailure is null
            ? Task.FromResult<IReadOnlyList<RequestRecord>>(UnsavedVideoRows(limit))
            : Task.FromException<IReadOnlyList<RequestRecord>>(ListUnsavedVideoRequestsFailure);

    public Task<bool> MarkContentSavedAsync(
        long id,
        long dataStorageId,
        string storageKey,
        DateTime savedAt,
        CancellationToken cancellationToken = default)
    {
        if (MarkContentSavedFailure is not null)
        {
            return Task.FromException<bool>(MarkContentSavedFailure);
        }

        ContentSavedWrites.Add(new ContentSavedWrite(id, dataStorageId, storageKey, savedAt));

        var row = Rows.FirstOrDefault(row => row.Id == id);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.ContentSaved = true;
        row.ContentStorageId = dataStorageId;
        row.ContentStorageKey = storageKey;
        row.ContentSavedAt = savedAt;
        row.UpdatedAt = savedAt;
        return Task.FromResult(true);
    }
}

internal sealed record ContentSavedWrite(
    long Id,
    long DataStorageId,
    string StorageKey,
    DateTime SavedAt);

/// <summary>In-memory port of the <c>request_executions</c> store.</summary>
internal sealed class FakeRequestExecutionStore : IRequestExecutionStore
{
    private long _nextId = 1;

    public List<RequestExecutionRecord> Rows { get; } = [];

    /// <summary>Simulates a failed execution query (biz/video.go wraps it).</summary>
    public Exception? ListError { get; set; }

    public RequestExecutionRecord Add(long requestId, string format, string? externalId)
    {
        var row = new RequestExecutionRecord
        {
            Id = _nextId++,
            RequestId = requestId,
            Format = format,
            ExternalId = externalId,
            Status = "completed",
        };

        Rows.Add(row);
        return row;
    }

    public Task<ListPage<RequestExecutionRecord>> ListAsync(
        RequestExecutionListQuery query,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<long> CreateAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        if (execution.Id == 0)
        {
            execution.Id = _nextId++;
        }

        Rows.Add(execution);
        return Task.FromResult(execution.Id);
    }

    public Task<RequestExecutionRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(Rows.FirstOrDefault(row => row.Id == id));

    public Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RequestExecutionRecord>>(
            Rows.Where(row => row.RequestId == requestId).OrderBy(row => row.Id).ToList());

    public Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdDescAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        if (ListError is { } error)
        {
            throw error;
        }

        return Task.FromResult<IReadOnlyList<RequestExecutionRecord>>(
            Rows.Where(row => row.RequestId == requestId).OrderByDescending(row => row.Id).ToList());
    }

    public Task<bool> UpdateStatusAsync(
        long id,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseBody,
        bool setResponseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");

    public Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");
}

/// <summary>In-memory port of the <c>channels</c> store.</summary>
internal sealed class FakeChannelStore : IChannelStore
{
    public List<ChannelRecord> Rows { get; } = [];

    public ChannelRecord Add(ChannelRecord channel)
    {
        Rows.Add(channel);
        return channel;
    }

    public Task<long> CreateAsync(ChannelRecord channel, CancellationToken cancellationToken = default)
        => Task.FromResult(Add(channel).Id);

    public Task<ChannelRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => Task.FromResult(Rows.FirstOrDefault(row => row.Id == id && row.DeletedAt == 0));

    /// <summary>Fails the selector's enabled-channel lookup only.</summary>
    public Exception? ListEnabledFailure { get; set; }

    /// <summary>Number of enabled-channel lookups (the probe writer's gate).</summary>
    public int ListEnabledCalls { get; private set; }

    public Task<IReadOnlyList<ChannelRecord>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        ListEnabledCalls++;
        if (ListEnabledFailure is not null)
        {
            return Task.FromException<IReadOnlyList<ChannelRecord>>(ListEnabledFailure);
        }

        return Task.FromResult<IReadOnlyList<ChannelRecord>>(Rows
            .Where(row => row.DeletedAt == 0
                && string.Equals(row.Status, "enabled", StringComparison.Ordinal))
            .OrderByDescending(static row => row.OrderingWeight)
            .ThenBy(static row => row.Id)
            .ToList());
    }

    /// <summary>
    /// The enabled-channel cache fingerprint: the largest updated_at across
    /// every row, soft-deleted ones included (null for an empty table).
    /// </summary>
    public Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Rows.Count == 0
            ? (DateTime?)null
            : Rows.Max(static row => row.UpdatedAt));

    public int ListEnabledByTypesCalls { get; private set; }

    public Task<IReadOnlyList<ChannelRecord>> ListEnabledByTypesAsync(
        IReadOnlyCollection<string> types,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ListEnabledByTypesCalls++;
        return Task.FromResult<IReadOnlyList<ChannelRecord>>(Rows
            .Where(row => row.DeletedAt == 0
                && string.Equals(row.Status, "enabled", StringComparison.Ordinal)
                && types.Contains(row.Type, StringComparer.Ordinal))
            .OrderBy(static row => row.Id)
            .ToList());
    }

    public Task<bool> UpdateStatusAsync(
        long id,
        string status,
        string? errorMessage,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the video task flow");
}
