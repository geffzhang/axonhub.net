using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Scripted transport. The reference tests substitute the pipeline executor the
/// same way, which keeps channel retries and request rewriting testable without
/// an upstream server.
/// </summary>
internal sealed class ScriptedExecutor : IExecutor
{
    // testChannelAPIKeys fans out (bounded by 8), so recording must be safe to
    // call concurrently; the handlers themselves still run outside the lock.
    private readonly object _gate = new();
    private int _calls;

    public Func<int, HttpRequestData, CancellationToken, HttpResponseData>? OnDo { get; set; }

    public Func<int, HttpRequestData, CancellationToken, IStream<StreamEvent>>? OnStream { get; set; }

    public List<HttpRequestData> Requests { get; } = [];

    public int Calls => Volatile.Read(ref _calls);

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        var call = Record(request);
        if (OnDo is not { } handler)
        {
            throw new InvalidOperationException("OnDo is not configured");
        }

        var response = handler(call, request, cancellationToken);

        // HttpExecutor attaches the request it sent; response transformers read
        // Request.ApiFormat to choose a protocol-specific decoder (images).
        response.Request ??= request;
        return response;
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        var call = Record(request);
        return OnStream is { } handler
            ? handler(call, request, cancellationToken)
            : throw new InvalidOperationException("OnStream is not configured");
    }

    private int Record(HttpRequestData request)
    {
        lock (_gate)
        {
            Requests.Add(request);
            return _calls++;
        }
    }
}

internal sealed class RecordingMetricsRecorder : IChannelMetricsRecorder
{
    // The tester's API-key batch records metrics from concurrent attempts.
    private readonly object _gate = new();

    public List<long> Selections { get; } = [];

    public List<long> Successes { get; } = [];

    public List<long> Failures { get; } = [];

    public List<ChannelLatencySample> Latencies { get; } = [];

    public void RecordSelection(long channelId, DateTimeOffset selectedAt)
    {
        _ = selectedAt;
        lock (_gate)
        {
            Selections.Add(channelId);
        }
    }

    public void RecordSuccess(long channelId, DateTimeOffset completedAt)
    {
        _ = completedAt;
        lock (_gate)
        {
            Successes.Add(channelId);
        }
    }

    public void RecordFailure(long channelId, DateTimeOffset completedAt)
    {
        _ = completedAt;
        lock (_gate)
        {
            Failures.Add(channelId);
        }
    }

    public void RecordLatency(ChannelLatencySample sample)
    {
        lock (_gate)
        {
            Latencies.Add(sample);
        }
    }
}

internal sealed record RequestResultUpdate(
    long RequestId,
    string Status,
    string? ExternalId,
    string? ResponseBody,
    long? LatencyMs,
    long? FirstTokenLatencyMs);

internal sealed record RequestStatusUpdate(long RequestId, string Status);

internal sealed record RequestHeaderUpdate(long RequestId, string HeadersJson);

internal sealed record ExecutionResultUpdate(
    long ExecutionId,
    string Status,
    string? ExternalId,
    string? UpstreamModelId,
    string? ResponseBody,
    string? ErrorMessage,
    long? LatencyMs,
    long? FirstTokenLatencyMs);

internal sealed record ExecutionStatusUpdate(
    long ExecutionId,
    string Status,
    long? ResponseStatusCode,
    string? UpstreamModelId,
    string? ErrorMessage,
    long? LatencyMs,
    long? FirstTokenLatencyMs);

internal sealed record ExecutionHeaderUpdate(long ExecutionId, string HeadersJson);

internal sealed record ChunkSave(long Id, IReadOnlyList<StreamEvent> Chunks);

/// <summary>
/// In-memory port of the request-chain write seam. Tests assert the rows the
/// middleware would write, without a database.
/// </summary>
internal sealed class RecordingRequestChainRecorder : IRequestChainRecorder
{
    private long _nextId = 1000;

    public List<RequestRecord> Requests { get; } = [];

    public List<RequestExecutionRecord> Executions { get; } = [];

    public List<UsageLogRecord> UsageLogs { get; } = [];

    public List<long> RequestChannels { get; } = [];

    public List<RequestStatusUpdate> RequestStatuses { get; } = [];

    public List<RequestResultUpdate> RequestResults { get; } = [];

    public List<RequestHeaderUpdate> RequestHeaders { get; } = [];

    public List<ExecutionStatusUpdate> ExecutionStatuses { get; } = [];

    public List<ExecutionResultUpdate> ExecutionResults { get; } = [];

    public List<ExecutionHeaderUpdate> ExecutionHeaders { get; } = [];

    public List<ChunkSave> RequestChunkSaves { get; } = [];

    public List<ChunkSave> ExecutionChunkSaves { get; } = [];

    public long CreateRequest(RequestRecord request)
    {
        request.Id = _nextId++;
        Requests.Add(request);
        return request.Id;
    }

    public void UpdateRequestChannel(long requestId, long channelId)
    {
        _ = requestId;
        RequestChannels.Add(channelId);
    }

    public void UpdateRequestStatus(long requestId, string status, DateTime updatedAt)
    {
        _ = updatedAt;
        RequestStatuses.Add(new RequestStatusUpdate(requestId, status));
    }

    public void UpdateRequestResult(
        long requestId,
        string status,
        string? externalId,
        string? responseBody,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt)
    {
        _ = updatedAt;
        RequestResults.Add(new RequestResultUpdate(
            requestId,
            status,
            externalId,
            responseBody,
            latencyMs,
            firstTokenLatencyMs));
    }

    public void UpdateRequestResponseHeaders(long requestId, string responseHeaders, DateTime updatedAt)
    {
        _ = updatedAt;
        RequestHeaders.Add(new RequestHeaderUpdate(requestId, responseHeaders));
    }

    public void SaveRequestChunks(long requestId, IReadOnlyList<StreamEvent> chunks)
        => RequestChunkSaves.Add(new ChunkSave(requestId, chunks));

    public long CreateExecution(RequestExecutionRecord execution)
    {
        execution.Id = _nextId++;
        Executions.Add(execution);
        return execution.Id;
    }

    public void UpdateExecutionStatus(
        long executionId,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt)
    {
        _ = updatedAt;
        ExecutionStatuses.Add(new ExecutionStatusUpdate(
            executionId,
            status,
            responseStatusCode,
            upstreamModelId,
            errorMessage,
            latencyMs,
            firstTokenLatencyMs));
    }

    public void UpdateExecutionResult(
        long executionId,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        DateTime updatedAt)
    {
        _ = updatedAt;
        ExecutionResults.Add(new ExecutionResultUpdate(
            executionId,
            status,
            externalId,
            upstreamModelId,
            responseBody,
            errorMessage,
            latencyMs,
            firstTokenLatencyMs));
    }

    public void UpdateExecutionResponseHeaders(long executionId, string responseHeaders, DateTime updatedAt)
    {
        _ = updatedAt;
        ExecutionHeaders.Add(new ExecutionHeaderUpdate(executionId, responseHeaders));
    }

    public void SaveExecutionChunks(long executionId, IReadOnlyList<StreamEvent> chunks)
        => ExecutionChunkSaves.Add(new ChunkSave(executionId, chunks));

    public void CreateUsageLog(UsageLogRecord usageLog)
    {
        usageLog.Id = _nextId++;
        UsageLogs.Add(usageLog);
    }
}

/// <summary>In-memory stream used to drive the SSE writer.</summary>
internal sealed class ListStream<T>(IReadOnlyList<T> items, Exception? terminalError = null) : IStream<T>
    where T : class
{
    private int _index;

    public T? Current { get; private set; }

    public Exception? Err => _index >= items.Count ? terminalError : null;

    public bool Next()
    {
        if (_index >= items.Count)
        {
            return false;
        }

        Current = items[_index];
        _index++;
        return true;
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}

/// <summary>
/// A stream whose first read never produces an event: it parks on the token the
/// pipeline attached to the attempt until the first-event guard cancels it, then
/// ends without an event. This is what a stalled upstream looks like from the
/// pipeline's point of view.
/// </summary>
internal sealed class BlockingFirstEventStream(CancellationToken cancellationToken) : IStream<StreamEvent>
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(10);

    private bool _read;

    public StreamEvent? Current => null;

    public Exception? Err => null;

    public bool Next()
    {
        if (_read)
        {
            return false;
        }

        _read = true;

        // The guard fires long before the safety timeout; the fallback only keeps
        // a broken test from hanging the suite.
        _ = cancellationToken.WaitHandle.WaitOne(SafetyTimeout);
        return false;
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}

internal static class TestChannels
{
    public static ChannelDescriptor OpenAi(
        long id,
        string model,
        string? actualModel = null,
        string apiKey = "sk-test",
        string baseUrl = "http://upstream.test",
        string? settings = null,
        string? endpoints = null)
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = baseUrl,
            Credentials = "{\"apiKey\":\"" + apiKey + "\"}",
            SupportedModels = "[\"" + (actualModel ?? model) + "\"]",
            Settings = settings,
            Endpoints = endpoints,
        };

    public static ChannelDescriptor Jina(
        long id,
        string model,
        string? actualModel = null,
        string apiKey = "sk-test",
        string baseUrl = "http://upstream.test")
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "jina",
            BaseUrl = baseUrl,
            Credentials = "{\"apiKey\":\"" + apiKey + "\"}",
            SupportedModels = "[\"" + (actualModel ?? model) + "\"]",
        };

    /// <summary>
    /// A <c>typesafe</c> channel (channel.TypeTypesafe): one SystemOne endpoint,
    /// so ChannelOutboundFactory always builds the TypeSafe outbound for it.
    /// </summary>
    public static ChannelDescriptor TypeSafe(
        long id,
        string model,
        string apiKey = "sk-typesafe-test-key",
        string baseUrl = "https://api.typesafe.ai/v1")
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "typesafe",
            BaseUrl = baseUrl,
            Credentials = "{\"apiKey\":\"" + apiKey + "\"}",
            SupportedModels = "[\"" + model + "\"]",
        };

    public static HttpRequestData ChatRequest(string model, bool stream = false, int? n = null)
    {
        var streamField = stream ? ",\"stream\":true" : string.Empty;
        var nField = n is { } count ? ",\"n\":" + count : string.Empty;
        var body = "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]"
            + streamField + nField + "}";

        return new HttpRequestData
        {
            Method = "POST",
            Url = "http://gateway.test/v1/chat/completions",
            Path = "/v1/chat/completions",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
            Auth = new AuthConfig(),
        };
    }

    public static HttpResponseData Completion(string model, string content = "hello")
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion\",\"created\":1,"
                + "\"model\":\"" + model + "\","
                + "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\""
                + content + "\"},\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}"),
        };

    public static HttpResponseData UpstreamError(int statusCode, string body = "{\"error\":{\"message\":\"boom\",\"type\":\"server_error\"}}")
        => new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    public static HttpClientError HttpClient(int statusCode, string body = "{\"error\":{\"message\":\"boom\",\"type\":\"server_error\"}}")
        => new("POST", "http://upstream.test/v1/chat/completions", statusCode, statusCode + " status", Encoding.UTF8.GetBytes(body));

    public static StreamEvent Data(string data, string type = "")
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(data) };
}
