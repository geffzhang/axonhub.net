using System.Collections.Concurrent;
using System.Text;

namespace AxonHub.Api.Persistence;

/// <summary>
/// Port of biz.LiveStreamRegistry (internal/server/biz/stream_preview.go): the
/// process-wide map of in-flight request and execution chunk buffers. The
/// orchestrator's live-preview middleware registers buffers, the preview
/// endpoint and the chunk loaders read them, and the sweeper closes the ones
/// whose stream went away without finalizing.
/// </summary>
/// <remarks>
/// The reference registers this as a Singleton through fx and shares it with
/// the request service; the port does the same through DI, which also means a
/// single instance per process (the SSE design is explicitly single-instance,
/// request_live.go:66-87).
/// </remarks>
public sealed class LiveStreamRegistry
{
    private readonly ConcurrentDictionary<long, ChunkBuffer> _requests = new();
    private readonly ConcurrentDictionary<long, ChunkBuffer> _executions = new();
    private readonly TimeProvider _timeProvider;

    public LiveStreamRegistry(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates a buffer on the registry's clock (the sweeper compares them).</summary>
    public ChunkBuffer CreateBuffer() => new(_timeProvider);

    /// <summary>Port of RegisterRequest.</summary>
    public void RegisterRequest(long requestId, ChunkBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _requests[requestId] = buffer;
    }

    /// <summary>Port of RegisterExecution.</summary>
    public void RegisterExecution(long executionId, ChunkBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _executions[executionId] = buffer;
    }

    /// <summary>Port of UnregisterRequest.</summary>
    public void UnregisterRequest(long requestId) => _requests.TryRemove(requestId, out _);

    /// <summary>Port of UnregisterExecution.</summary>
    public void UnregisterExecution(long executionId) => _executions.TryRemove(executionId, out _);

    /// <summary>Port of GetRequestBuffer.</summary>
    public ChunkBuffer? GetRequestBuffer(long requestId)
        => _requests.TryGetValue(requestId, out var buffer) ? buffer : null;

    /// <summary>Port of GetExecutionBuffer.</summary>
    public ChunkBuffer? GetExecutionBuffer(long executionId)
        => _executions.TryGetValue(executionId, out var buffer) ? buffer : null;

    /// <summary>
    /// Port of GetRequestChunks: the live chunks in the same shape as
    /// SaveRequestChunks (terminal markers skipped, binary payloads summarized).
    /// </summary>
    public IReadOnlyList<string> GetRequestChunks(long requestId)
        => MarshalPreviewChunks(GetRequestBuffer(requestId));

    /// <summary>Port of GetExecutionChunks.</summary>
    public IReadOnlyList<string> GetExecutionChunks(long executionId)
        => MarshalPreviewChunks(GetExecutionBuffer(executionId));

    /// <summary>
    /// Port of sweepStaleEntries: closed buffers and buffers idle for more than
    /// ten minutes are closed and dropped. Returns the number evicted.
    /// </summary>
    public int SweepStaleEntries(DateTimeOffset now)
    {
        var threshold = TimeSpan.FromMinutes(10);
        var evicted = 0;

        foreach (var (id, buffer) in _requests)
        {
            if (SweepEntry(id, buffer, _requests, now, threshold))
            {
                evicted++;
            }
        }

        foreach (var (id, buffer) in _executions)
        {
            if (SweepEntry(id, buffer, _executions, now, threshold))
            {
                evicted++;
            }
        }

        return evicted;
    }

    private static bool SweepEntry(
        long id,
        ChunkBuffer buffer,
        ConcurrentDictionary<long, ChunkBuffer> entries,
        DateTimeOffset now,
        TimeSpan threshold)
    {
        if (buffer.IsClosed)
        {
            return entries.TryRemove(id, out _);
        }

        if (now - buffer.LastAppendedAt > threshold)
        {
            // A stream that stopped appending for ten minutes is treated as a
            // zombie: close it so any preview reader sees the completed end,
            // then drop the reference.
            buffer.Close();
            return entries.TryRemove(id, out _);
        }

        return false;
    }

    /// <summary>Port of marshalPreviewChunks (stream_preview.go:82-108).</summary>
    private static IReadOnlyList<string> MarshalPreviewChunks(ChunkBuffer? buffer)
    {
        if (buffer is null)
        {
            return [];
        }

        var chunks = buffer.Slice();
        if (chunks.Count == 0)
        {
            return [];
        }

        var result = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (RequestChunkStorage.ShouldSkipStoredStreamChunk(chunk))
            {
                continue;
            }

            if (RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json))
            {
                result.Add(Encoding.UTF8.GetString(json));
            }
        }

        return result;
    }
}
