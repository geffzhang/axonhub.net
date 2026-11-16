using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/middleware/thread_test.go: the thread header is read
/// with gin's first-value semantics, the row is resolved for the authenticated
/// project, and the database id is published for the trace middleware. Missing
/// headers, missing identity and persistence failures are fail-open.
/// </summary>
public sealed class ThreadMiddlewareTests
{
    private const long ProjectId = 7L;

    [Fact]
    public async Task TestThreadHeaderCreatesThreadAndPublishesId()
    {
        var context = NewContext();
        context.Request.Headers["AH-Thread-Id"] = "thread-create";
        var store = new FakeThreadStore();
        var nextCalled = false;

        await RunAsync(context, TracingOptions.Default, store, (_, _) =>
        {
            nextCalled = true;
            Assert.Equal(42L, ThreadContext.GetThreadId(context));
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Equal(new ThreadCall(ProjectId, "thread-create"), Assert.Single(store.Calls));
        Assert.Equal(42L, ThreadContext.GetThreadId(context));

        // contexts.WithThread also publishes the loaded thread on the request
        // context; the trace sticky selector reads its id from the identity.
        Assert.Equal(
            42L,
            RequestIdentity.FromHttpContext(context).ThreadId);
    }

    [Fact]
    public async Task TestConfiguredThreadHeaderIsUsed()
    {
        var context = NewContext();
        context.Request.Headers["AH-Thread-Id"] = "default-header";
        context.Request.Headers["X-Thread"] = "configured-header";
        var store = new FakeThreadStore();

        await RunAsync(context, new TracingOptions { ThreadHeader = "X-Thread" }, store);

        Assert.Equal("configured-header", Assert.Single(store.Calls).ThreadId);
    }

    [Fact]
    public async Task TestMissingOrEmptyHeaderSkipsTheStore()
    {
        var missing = NewContext();
        var empty = NewContext();
        empty.Request.Headers["AH-Thread-Id"] = string.Empty;
        var store = new FakeThreadStore();

        var missingNext = false;
        await RunAsync(missing, TracingOptions.Default, store, (_, _) =>
        {
            missingNext = true;
            return Task.CompletedTask;
        });
        var emptyNext = false;
        await RunAsync(empty, TracingOptions.Default, store, (_, _) =>
        {
            emptyNext = true;
            return Task.CompletedTask;
        });

        Assert.True(missingNext);
        Assert.True(emptyNext);
        Assert.Empty(store.Calls);
        Assert.Null(ThreadContext.GetThreadId(missing));
        Assert.Null(ThreadContext.GetThreadId(empty));
    }

    [Fact]
    public async Task TestUnauthenticatedRequestSkipsTheStore()
    {
        var context = NewContext(authenticated: false);
        context.Request.Headers["AH-Thread-Id"] = "thread-no-project";
        var store = new FakeThreadStore();
        var nextCalled = false;

        await RunAsync(context, TracingOptions.Default, store, (_, _) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Empty(store.Calls);
        Assert.Null(ThreadContext.GetThreadId(context));
    }

    [Fact]
    public async Task TestPersistenceFailureIsFailOpen()
    {
        var context = NewContext();
        context.Request.Headers["AH-Thread-Id"] = "thread-boom";
        var store = new FakeThreadStore { Failure = new InvalidOperationException("boom") };
        var nextCalled = false;

        await RunAsync(context, TracingOptions.Default, store, (_, _) =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        Assert.True(nextCalled);
        Assert.Single(store.Calls);
        Assert.Null(ThreadContext.GetThreadId(context));
    }

    [Fact]
    public async Task TestMultipleHeaderValuesUseTheFirst()
    {
        var context = NewContext();
        context.Request.Headers["AH-Thread-Id"] = new StringValues(["first", "second"]);
        var store = new FakeThreadStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("first", Assert.Single(store.Calls).ThreadId);
    }

    [Fact]
    public async Task TestThreadMiddlewareFeedsTheTraceRow()
    {
        // routes.go mounts WithThread before WithTrace; the trace row must carry
        // the id of the thread the first middleware resolved.
        var context = NewContext();
        context.Request.Headers["AH-Thread-Id"] = "thread-chain";
        context.Request.Headers["AH-Trace-Id"] = "trace-chain";
        var threads = new FakeThreadStore();
        var traces = new FakeTraceStore();
        var options = TracingOptions.Default;
        var traceMiddleware = new TraceMiddleware(
            _ => Task.CompletedTask,
            options);
        var threadMiddleware = new ThreadMiddleware(
            nextContext => traceMiddleware.InvokeAsync(nextContext, traces),
            options);

        await threadMiddleware.InvokeAsync(context, threads);

        var call = Assert.Single(traces.Calls);
        Assert.Equal("trace-chain", call.TraceId);
        Assert.Equal(42L, call.ThreadId);
        Assert.Equal(42L, TraceContext.GetTrace(context)!.ThreadId);
    }

    private static async Task RunAsync(
        HttpContext context,
        TracingOptions options,
        IThreadStore store,
        Func<HttpContext, CancellationToken, Task>? onNext = null)
    {
        var middleware = new ThreadMiddleware(
            nextContext => onNext?.Invoke(nextContext, CancellationToken.None) ?? Task.CompletedTask,
            options);

        await middleware.InvokeAsync(context, store);
    }

    private static DefaultHttpContext NewContext(bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/v1/chat/completions";
        context.Request.Body = new MemoryStream();
        if (authenticated)
        {
            context.Items[RequestIdentity.HttpContextItemKey] = new RequestIdentity(ProjectId, 3L);
        }

        return context;
    }

    private sealed class FakeThreadStore : IThreadStore
    {
        private readonly Dictionary<(long ProjectId, string ThreadId), ThreadRecord> _rows = [];

        public List<ThreadCall> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task<ThreadRecord> GetOrCreateAsync(
            long projectId,
            string threadId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new ThreadCall(projectId, threadId));
            if (Failure is not null)
            {
                throw Failure;
            }

            if (!_rows.TryGetValue((projectId, threadId), out var row))
            {
                row = new ThreadRecord
                {
                    Id = 42L,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    ThreadId = threadId,
                    Status = "active",
                    ProjectId = projectId,
                };
                _rows[(projectId, threadId)] = row;
            }

            return Task.FromResult(row);
        }
    }

    private sealed record ThreadCall(long ProjectId, string ThreadId);

    private sealed class FakeTraceStore : ITraceStore
    {
        public List<TraceCall> Calls { get; } = [];

        public Task<TraceRecord> GetOrCreateAsync(
            long projectId,
            string traceId,
            long? threadId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new TraceCall(projectId, traceId, threadId));
            return Task.FromResult(new TraceRecord
            {
                Id = 100L,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                TraceId = traceId,
                Status = "active",
                ProjectId = projectId,
                ThreadId = threadId,
            });
        }
    }

    private sealed record TraceCall(long ProjectId, string TraceId, long? ThreadId);
}
