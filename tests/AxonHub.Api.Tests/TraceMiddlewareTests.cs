using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenCode;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/middleware/trace_test.go: trace id resolution from
/// headers, OpenCode/Codex client markers, Claude Code metadata and body fields;
/// persistence through biz.TraceService.GetOrCreateTrace; response aliases; the
/// non-message endpoint carve-out; and the fail-open persistence behavior.
/// </summary>
public sealed class TraceMiddlewareTests
{
    private const long ProjectId = 7L;

    [Fact]
    public async Task TestTryExtractTraceIdFromClaudeCodeRequestLegacyUserId()
    {
        var context = NewContext("/v1/messages");
        SetJsonBody(
            context,
            new
            {
                metadata = new
                {
                    user_id = LegacyUserId("7581b58b-1234-5678-9abc-def012345678"),
                },
            });

        Assert.Equal(
            "7581b58b-1234-5678-9abc-def012345678",
            await TraceMiddleware.TryExtractTraceIdFromClaudeCodeRequestAsync(context));

        // The body is rewound for the endpoint.
        Assert.Equal(0, context.Request.Body.Position);
    }

    [Fact]
    public async Task TestTryExtractTraceIdFromClaudeCodeRequestAnthropicPathAndV2Json()
    {
        var context = NewContext("/anthropic/v1/messages");
        var userId = JsonSerializer.Serialize(new
        {
            device_id = "abc",
            account_uuid = string.Empty,
            session_id = "sess-v2",
        });
        SetJsonBody(context, new { metadata = new { user_id = userId } });

        Assert.Equal(
            "sess-v2",
            await TraceMiddleware.TryExtractTraceIdFromClaudeCodeRequestAsync(context));
    }

    [Fact]
    public async Task TestTryExtractTraceIdFromClaudeCodeRequestIgnoresOtherRequests()
    {
        var getMessages = NewContext("/v1/messages", method: "GET");
        SetJsonBody(getMessages, new { metadata = new { user_id = LegacyUserId("7581b58b-1234-5678-9abc-def012345678") } });

        var otherPath = NewContext("/v1/chat/completions");
        SetJsonBody(otherPath, new { metadata = new { user_id = LegacyUserId("7581b58b-1234-5678-9abc-def012345678") } });

        var noMetadata = NewContext("/v1/messages");
        SetJsonBody(noMetadata, new { model = "claude" });

        var invalidUser = NewContext("/v1/messages");
        SetJsonBody(invalidUser, new { metadata = new { user_id = "not-a-user-id" } });

        Assert.Equal(string.Empty, await TraceMiddleware.TryExtractTraceIdFromClaudeCodeRequestAsync(getMessages));
        Assert.Equal(string.Empty, await TraceMiddleware.TryExtractTraceIdFromClaudeCodeRequestAsync(otherPath));
        Assert.Equal(string.Empty, await TraceMiddleware.TryExtractTraceIdFromClaudeCodeRequestAsync(noMetadata));
        Assert.Equal(string.Empty, await TraceMiddleware.TryExtractTraceIdFromClaudeCodeRequestAsync(invalidUser));
    }

    [Fact]
    public async Task TestClaudeCodeTraceDisabledDoesNotResolve()
    {
        var context = NewContext("/v1/messages");
        SetJsonBody(
            context,
            new { metadata = new { user_id = LegacyUserId("7581b58b-1234-5678-9abc-def012345678") } });
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { ClaudeCodeTraceEnabled = false },
            store);

        Assert.Empty(store.Calls);
        Assert.Null(TraceContext.GetTraceId(context));
        Assert.Null(TraceContext.GetTrace(context));
    }

    [Fact]
    public async Task TestOpenCodeSessionHeaderResolvesTrace()
    {
        var context = NewContext();
        context.Request.Headers["x-opencode-session"] = "ses-opencode";
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("ses-opencode", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestOpenCodeAffinityHeaderResolvesTrace()
    {
        var context = NewContext();
        context.Request.Headers["X-Session-Affinity"] = "ses-affinity";
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("ses-affinity", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestOpenCodeTraceDisabledDoesNotResolve()
    {
        var context = NewContext();
        context.Request.Headers[OpenCodeHeaders.SessionHeader] = "ses-opencode";
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { OpenCodeTraceEnabled = false },
            store);

        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task TestCodexSessionHeaderResolvesTrace()
    {
        var context = NewContext();
        context.Request.Headers["session_id"] = "  codex-session  ";
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("codex-session", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestCodexTurnMetadataResolvesTrace()
    {
        var context = NewContext();
        context.Request.Headers[CodexHeaders.TurnMetadataHeader] =
            """{"session_id":"codex-turn"}""";
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("codex-turn", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestCodexSessionHeaderTakesPriorityOverTurnMetadata()
    {
        var context = NewContext();
        context.Request.Headers["Session-Id"] = "codex-header";
        context.Request.Headers[CodexHeaders.TurnMetadataHeader] =
            """{"session_id":"codex-turn"}""";
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("codex-header", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestCodexTraceDisabledDoesNotResolve()
    {
        var context = NewContext();
        context.Request.Headers["Session-Id"] = "codex-header";
        var store = new FakeTraceStore();

        await RunAsync(context, new TracingOptions { CodexTraceEnabled = false }, store);

        Assert.Empty(store.Calls);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"session_id":""}""")]
    [InlineData("""{"session_id":"   "}""")]
    public async Task TestCodexInvalidTurnMetadataIsIgnored(string turnMetadata)
    {
        var context = NewContext();
        context.Request.Headers[CodexHeaders.TurnMetadataHeader] = turnMetadata;
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task TestTraceHeaderWinsOverClientMarkers()
    {
        var context = NewContext();
        context.Request.Headers[TracingOptions.DefaultTraceHeader] = "primary-trace";
        context.Request.Headers[OpenCodeHeaders.SessionHeader] = "ses-opencode";
        context.Request.Headers["Session-Id"] = "codex-header";
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal("primary-trace", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestExtraTraceHeadersAreFallbacks()
    {
        var options = new TracingOptions { ExtraTraceHeaders = ["X-Alt-Trace"] };

        var fallback = NewContext();
        fallback.Request.Headers["X-Alt-Trace"] = "alt-trace";
        var fallbackStore = new FakeTraceStore();
        await RunAsync(fallback, options, fallbackStore);
        Assert.Equal("alt-trace", Assert.Single(fallbackStore.Calls).TraceId);

        var both = NewContext();
        both.Request.Headers[TracingOptions.DefaultTraceHeader] = "primary-trace";
        both.Request.Headers["X-Alt-Trace"] = "alt-trace";
        var bothStore = new FakeTraceStore();
        await RunAsync(both, options, bothStore);
        Assert.Equal("primary-trace", Assert.Single(bothStore.Calls).TraceId);
    }

    [Fact]
    public async Task TestNoTraceSourceDoesNotPersist()
    {
        var context = NewContext();
        var store = new FakeTraceStore();
        var nextCalled = false;

        await RunAsync(
            context,
            TracingOptions.Default,
            store,
            () => nextCalled = true);

        Assert.True(nextCalled);
        Assert.Empty(store.Calls);
        Assert.Null(TraceContext.GetTraceId(context));
        Assert.Null(TraceContext.GetTrace(context));
        Assert.Null(TraceContext.GetSessionId(context));
        Assert.DoesNotContain(TraceContext.TraceIdItemKey, context.Items.Keys);
    }

    [Fact]
    public async Task TestLoggingOnlyTraceIdIsNotPersisted()
    {
        var context = NewContext();
        TraceContext.SetTraceId(context, "at-log-correlation");
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        // WithLoggingTracing may mint an id, but only an explicit trace source
        // opts the request into persisted tracing.
        Assert.Empty(store.Calls);
        Assert.Equal("at-log-correlation", TraceContext.GetTraceId(context));
        Assert.Null(TraceContext.GetTrace(context));
    }

    [Fact]
    public async Task TestResolvedTraceIsPersistedAndPublished()
    {
        var context = NewContext();
        context.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-1";
        var store = new FakeTraceStore();
        var options = new TracingOptions
        {
            ResponseTraceHeaders = ["X-Trace-Alias", "   "],
        };

        await RunAsync(context, options, store);

        var call = Assert.Single(store.Calls);
        Assert.Equal(ProjectId, call.ProjectId);
        Assert.Equal("trace-1", call.TraceId);
        Assert.Null(call.ThreadId);

        var trace = TraceContext.GetTrace(context);
        Assert.NotNull(trace);
        Assert.Equal("trace-1", trace.TraceId);
        Assert.Equal(trace.Id, Assert.IsType<RequestIdentity>(
            context.Items[RequestIdentity.HttpContextItemKey]).TraceId);
        Assert.Equal(3L, Assert.IsType<RequestIdentity>(
            context.Items[RequestIdentity.HttpContextItemKey]).ApiKeyId);

        Assert.Equal("trace-1", TraceContext.GetSessionId(context));
        Assert.Equal("trace-1", context.Response.Headers["X-Trace-Alias"].ToString());
        Assert.False(context.Response.Headers.TryGetValue(string.Empty, out _));
    }

    [Fact]
    public async Task TestThreadIdIsPassedToTheStore()
    {
        var context = NewContext();
        context.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-thread";
        ThreadContext.SetThreadId(context, 42L);
        var store = new FakeTraceStore();

        await RunAsync(context, TracingOptions.Default, store);

        Assert.Equal(42L, Assert.Single(store.Calls).ThreadId);
    }

    [Fact]
    public async Task TestNoProjectIdentitySkipsPersistenceButWritesAliases()
    {
        var context = NewContext(authenticated: false);
        context.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-anonymous";
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { ResponseTraceHeaders = ["X-Trace-Alias"] },
            store);

        Assert.Empty(store.Calls);
        Assert.Equal("trace-anonymous", TraceContext.GetTraceId(context));
        Assert.Null(TraceContext.GetTrace(context));
        Assert.Null(TraceContext.GetSessionId(context));
        Assert.Equal("trace-anonymous", context.Response.Headers["X-Trace-Alias"].ToString());
    }

    [Theory]
    [InlineData("/v1/embeddings")]
    [InlineData("/v1beta/models/gemini-pro:embedContent")]
    public async Task TestNonMessageEndpointSkipsPersistence(string path)
    {
        var context = NewContext(path);
        context.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-embedding";
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { ResponseTraceHeaders = ["X-Trace-Alias"] },
            store);

        Assert.Empty(store.Calls);
        Assert.Equal("trace-embedding", TraceContext.GetTraceId(context));
        Assert.Null(TraceContext.GetTrace(context));
        Assert.Equal("trace-embedding", context.Response.Headers["X-Trace-Alias"].ToString());
    }

    [Fact]
    public async Task TestIdempotentSameTraceIdReusesTheRow()
    {
        var store = new FakeTraceStore();

        var first = NewContext();
        first.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-same";
        await RunAsync(first, TracingOptions.Default, store);

        var second = NewContext();
        second.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-same";
        await RunAsync(second, TracingOptions.Default, store);

        Assert.Equal(2, store.Calls.Count);
        Assert.Equal(
            TraceContext.GetTrace(first)?.Id,
            TraceContext.GetTrace(second)?.Id);
    }

    [Fact]
    public async Task TestPersistenceFailureStillWritesAliasesAndContinues()
    {
        var context = NewContext();
        context.Request.Headers[TracingOptions.DefaultTraceHeader] = "trace-failing";
        var store = new FakeTraceStore { Failure = new InvalidOperationException("db down") };
        var nextCalled = false;

        await RunAsync(
            context,
            new TracingOptions { ResponseTraceHeaders = ["X-Trace-Alias"] },
            store,
            () => nextCalled = true);

        Assert.True(nextCalled);
        Assert.Equal("trace-failing", context.Response.Headers["X-Trace-Alias"].ToString());
        Assert.Null(TraceContext.GetTrace(context));
        Assert.Null(TraceContext.GetSessionId(context));
    }

    [Fact]
    public async Task TestExtraTraceBodyFieldFlat()
    {
        var context = NewContext();
        SetJsonBody(context, new { session_id = "body-flat" });
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { ExtraTraceBodyFields = ["session_id"] },
            store);

        Assert.Equal("body-flat", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestExtraTraceBodyFieldNested()
    {
        var context = NewContext();
        SetJsonBody(context, new { metadata = new { session_id = "body-nested" } });
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { ExtraTraceBodyFields = ["metadata.session_id"] },
            store);

        Assert.Equal("body-nested", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestExtraTraceBodyFieldsUseFirstMatch()
    {
        var context = NewContext();
        SetJsonBody(context, new { first = "", second = "body-second" });
        var store = new FakeTraceStore();

        await RunAsync(
            context,
            new TracingOptions { ExtraTraceBodyFields = ["first", "second"] },
            store);

        Assert.Equal("body-second", Assert.Single(store.Calls).TraceId);
    }

    [Fact]
    public async Task TestMalformedBodyMeansNoTrace()
    {
        var context = NewContext();
        SetRawBody(context, "{invalid json");
        var store = new FakeTraceStore();
        var nextCalled = false;

        await RunAsync(
            context,
            new TracingOptions { ExtraTraceBodyFields = ["session_id"] },
            store,
            () => nextCalled = true);

        Assert.True(nextCalled);
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task TestBodyIsRewoundForTheEndpoint()
    {
        const string body = """{"session_id":"body-rewind","model":"gpt-5"}""";
        var context = NewContext();
        SetRawBody(context, body);
        string? seenByEndpoint = null;

        await RunAsync(
            context,
            new TracingOptions { ExtraTraceBodyFields = ["session_id"] },
            new FakeTraceStore(),
            () =>
            {
                using var reader = new StreamReader(
                    context.Request.Body,
                    Encoding.UTF8,
                    leaveOpen: true);
                seenByEndpoint = reader.ReadToEnd();
            });

        Assert.Equal(body, seenByEndpoint);
    }

    [Fact]
    public async Task TestBodyReadErrorAbortsWith400()
    {
        var context = NewContext();
        context.Request.Body = new ThrowingStream("boom");
        var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        var store = new FakeTraceStore();
        var nextCalled = false;

        await RunAsync(
            context,
            new TracingOptions { ExtraTraceBodyFields = ["session_id"] },
            store,
            () => nextCalled = true);

        Assert.False(nextCalled);
        Assert.Empty(store.Calls);
        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);

        responseBody.Position = 0;
        using var document = JsonDocument.Parse(responseBody);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("Bad Request", error.GetProperty("type").GetString());
        Assert.StartsWith(
            "failed to read request body: ",
            error.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/v1/embeddings", true)]
    [InlineData("/jina/v1/embeddings", true)]
    [InlineData("/embeddings", true)]
    [InlineData("/v1beta/models/gemini-pro:embedContent", true)]
    [InlineData("/v1beta/models/gemini-pro:batchEmbedContents", true)]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1beta/models/gemini-pro:generateContent", false)]
    public void TestIsNonMessageEndpoint(string path, bool expected)
        => Assert.Equal(expected, TraceMiddleware.IsNonMessageEndpoint(path));

    private static async Task RunAsync(
        HttpContext context,
        TracingOptions options,
        ITraceStore store,
        Action? onNext = null)
    {
        var middleware = new TraceMiddleware(
            _ =>
            {
                onNext?.Invoke();
                return Task.CompletedTask;
            },
            options);

        await middleware.InvokeAsync(context, store);
    }

    private static DefaultHttpContext NewContext(
        string path = "/v1/chat/completions",
        string method = "POST",
        bool authenticated = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Body = new MemoryStream();
        if (authenticated)
        {
            context.Items[RequestIdentity.HttpContextItemKey] = new RequestIdentity(ProjectId, 3L);
        }

        return context;
    }

    private static void SetJsonBody(HttpContext context, object body)
        => SetRawBody(context, JsonSerializer.Serialize(body));

    private static void SetRawBody(HttpContext context, string body)
        => context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));

    private static string LegacyUserId(string sessionId)
        => "user_aabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccddaabbccdd"
            + "_account__session_" + sessionId;

    private sealed class FakeTraceStore : ITraceStore
    {
        private readonly Dictionary<string, TraceRecord> _rows = new(StringComparer.Ordinal);

        public List<TraceCall> Calls { get; } = [];

        public Exception? Failure { get; init; }

        public Task<TraceRecord> GetOrCreateAsync(
            long projectId,
            string traceId,
            long? threadId,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new TraceCall(projectId, traceId, threadId));
            if (Failure is not null)
            {
                throw Failure;
            }

            if (!_rows.TryGetValue(traceId, out var row))
            {
                row = new TraceRecord
                {
                    Id = _rows.Count + 100,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    TraceId = traceId,
                    Status = "active",
                    ProjectId = projectId,
                    ThreadId = threadId,
                };
                _rows[traceId] = row;
            }

            return Task.FromResult(row);
        }
    }

    private sealed record TraceCall(long ProjectId, string TraceId, long? ThreadId);

    /// <summary>A non-seekable stream that fails on the first read.</summary>
    private sealed class ThrowingStream(string message) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException(message);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => throw new IOException(message);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();
    }
}
