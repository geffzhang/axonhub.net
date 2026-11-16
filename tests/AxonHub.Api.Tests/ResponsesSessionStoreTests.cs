using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of orchestrator/responses_session_test.go: continuation storage,
/// normalization, stream buffering and eviction boundaries.
/// </summary>
public sealed class ResponsesSessionStoreTests
{
    private const string Scope = "api-key:1";

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(byte[]? value) => value is null ? string.Empty : Encoding.UTF8.GetString(value);

    private static void AssertJsonEqual(string expected, byte[]? actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual ?? []);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "expected JSON " + expected + " but got " + Text(actual));
    }

    private static Task<ResponsesSessionPrepared> PrepareAsync(
        ResponsesSessionStore store,
        string body,
        string? sessionId = null,
        string? scope = Scope)
        => store.PrepareAsync(Bytes(body), sessionId, scope, RequestIdentity.Default);

    [Fact]
    public async Task ExpandsPreviousResponseForAnyUpstreamTransport()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","instructions":"old instructions","stream":true,"input":[{"type":"message","role":"user","content":"hello"}]}"""),
            Bytes("""{"id":"resp_1","status":"completed","output":[{"id":"fc_1","type":"function_call","call_id":"call_1","name":"run","arguments":"{}"}]}"""),
            sessionId: null,
            Scope);

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","instructions":"new instructions","stream":false,"previous_response_id":"resp_1","input":[{"type":"function_call_output","call_id":"call_1","output":"ok"}]}""");

        AssertJsonEqual(
            """{"model":"gpt-5","instructions":"new instructions","stream":false,"input":[{"type":"message","role":"user","content":"hello"},{"id":"fc_1","type":"function_call","call_id":"call_1","name":"run","arguments":"{}"},{"type":"function_call_output","call_id":"call_1","output":"ok"}]}""",
            prepared.Body);
    }

    [Fact]
    public async Task RestoresPersistedCustomToolCallOnMemoryMiss()
    {
        var loadCalls = 0;
        var store = new ResponsesSessionStore(
            load: (responseId, _, _) =>
            {
                loadCalls++;
                Assert.Equal("resp_persisted", responseId);
                return Task.FromResult(new ResponsesSessionLoaded(
                    Bytes("""{"model":"gpt-5","input":[{"type":"message","role":"user","content":"run the tool"}]}"""),
                    Bytes("""{"id":"resp_persisted","status":"completed","output":[{"id":"ctc_1","type":"custom_tool_call","status":"completed","call_id":"call_1","name":"exec","input":"{}"}]}"""),
                    true));
            });

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_persisted","stream":true,"input":[{"type":"custom_tool_call_output","call_id":"call_1","output":"ok"}]}""");
        AssertJsonEqual(
            """{"model":"gpt-5","stream":true,"input":[{"type":"message","role":"user","content":"run the tool"},{"id":"ctc_1","type":"custom_tool_call","call_id":"call_1","name":"exec","input":"{}"},{"type":"custom_tool_call_output","call_id":"call_1","output":"ok"}]}""",
            prepared.Body);
        Assert.Equal(1, loadCalls);

        _ = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_persisted","input":"again"}""");
        Assert.Equal(1, loadCalls);
    }

    [Fact]
    public async Task DoesNotCrossScopes()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","input":"hello"}"""),
            Bytes("""{"id":"resp_private","status":"completed","output":[]}"""),
            sessionId: null,
            "api-key:1");

        var body = """{"model":"gpt-5","previous_response_id":"resp_private","input":"world"}""";
        var prepared = await PrepareAsync(store, body, scope: "api-key:2");

        Assert.Equal(body, Text(prepared.Body));
    }

    [Fact]
    public async Task IgnoresMissingScope()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","input":"hello"}"""),
            Bytes("""{"id":"resp_unscoped","status":"completed","output":[]}"""),
            sessionId: null,
            scope: null);

        var body = """{"model":"gpt-5","previous_response_id":"resp_unscoped","input":"world"}""";
        var prepared = await PrepareAsync(store, body, scope: null);

        Assert.Equal(body, Text(prepared.Body));
        Assert.Empty(store.ByResponse);
    }

    [Fact]
    public async Task ContinuesAcrossMultipleUpstreamSwitches()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","input":"one"}"""),
            Bytes("""{"id":"resp_sse","status":"completed","output":[{"type":"message","role":"assistant","content":"two"}]}"""),
            sessionId: null,
            Scope);

        var second = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_sse","input":"three"}""");
        store.Record(
            second.Body!,
            Bytes("""{"id":"resp_websocket","status":"completed","output":[{"type":"message","role":"assistant","content":"four"}]}"""),
            second.SessionId,
            Scope);

        var third = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_websocket","input":"five"}""");

        AssertJsonEqual(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"one"},{"type":"message","role":"assistant","content":"two"},{"type":"message","role":"user","content":"three"},{"type":"message","role":"assistant","content":"four"},{"type":"message","role":"user","content":"five"}]}""",
            third.Body);
    }

    [Fact]
    public void BudgetsLargeHistory()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes(
                """{"input":[{"type":"function_call_output","call_id":"call_canary","output":"ACTUAL"}]}"""
                    .Replace("ACTUAL", new string('x', 4 << 20), StringComparison.Ordinal)),
            Bytes("""{"id":"large","status":"completed","output":[]}"""),
            sessionId: null,
            Scope);

        var record = store.Lookup("large", Scope);

        Assert.NotNull(record);
        Assert.True(record!.Size > 4 << 20);
    }

    [Fact]
    public void SnapshotRejectsUnresolvedAncestor()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"previous_response_id":"missing","input":"delta"}"""),
            Bytes("""{"id":"delta","status":"completed","output":[]}"""),
            sessionId: null,
            Scope);

        var record = store.Lookup("delta", Scope);

        Assert.NotNull(record);
        Assert.False(record!.Complete);
    }

    [Fact]
    public async Task RecordsCompletedStream()
    {
        var store = new ResponsesSessionStore();
        var stream = store.WrapStream(
            new SliceStream<StreamEvent>(
            [
                new()
                {
                    Type = "response.created",
                    Data = Bytes("""{"type":"response.created","response":{"id":"resp_stream","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
                },
                new()
                {
                    Type = "response.output_item.added",
                    Data = Bytes("""{"type":"response.output_item.added","output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"in_progress","content":[]}}"""),
                },
                new()
                {
                    Type = "response.output_item.done",
                    Data = Bytes("""{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"two","annotations":[]}]}}"""),
                },
                new()
                {
                    Type = "response.completed",
                    Data = Bytes("""{"type":"response.completed","response":{"id":"resp_stream","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
                },
            ]),
            Bytes("""{"model":"gpt-5","input":"one"}"""),
            sessionId: null,
            Scope);

        while (stream.Next())
        {
        }

        Assert.Null(stream.Err);
        stream.Dispose();

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_stream","input":"three"}""");

        AssertJsonEqual(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"one"},{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"output_text","text":"two"}]},{"type":"message","role":"user","content":"three"}]}""",
            prepared.Body);
    }

    [Fact]
    public void PreservesEveryEventForConsumer()
    {
        StreamEvent[] events =
        [
            new() { Type = "response.created", Data = Bytes("""{"type":"response.created"}""") },
            new() { Type = "response.output_item.added", Data = Bytes("""{"type":"response.output_item.added"}""") },
            new() { Type = "response.completed", Data = Bytes("""{"type":"response.completed"}""") },
        ];
        var store = new ResponsesSessionStore();
        var stream = store.WrapStream(
            new SliceStream<StreamEvent>(events),
            Bytes("""{"model":"gpt-5","input":"one"}"""),
            sessionId: null,
            Scope);

        var received = new List<StreamEvent>();
        while (stream.Next())
        {
            received.Add(stream.Current!);
        }

        Assert.Equal(events.Length, received.Count);
        for (var i = 0; i < events.Length; i++)
        {
            Assert.Same(events[i], received[i]);
        }
    }

    [Fact]
    public async Task RecordsBeforeStreamClose()
    {
        var store = new ResponsesSessionStore();
        var stream = store.WrapStream(
            new SliceStream<StreamEvent>(
            [
                new()
                {
                    Type = "response.created",
                    Data = Bytes("""{"type":"response.created","response":{"id":"resp_tool","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
                },
                new()
                {
                    Type = "response.output_item.added",
                    Data = Bytes("""{"type":"response.output_item.added","output_index":0,"item":{"id":"ctc_1","type":"custom_tool_call","call_id":"call_1","name":"exec_command","input":""}}"""),
                },
                new()
                {
                    Type = "response.custom_tool_call_input.done",
                    Data = Bytes("""{"type":"response.custom_tool_call_input.done","output_index":0,"item_id":"ctc_1","input":"{}"}"""),
                },
                new()
                {
                    Type = "response.output_item.done",
                    Data = Bytes("""{"type":"response.output_item.done","output_index":0,"item":{"id":"ctc_1","type":"custom_tool_call","status":"completed","call_id":"call_1","name":"exec_command","input":"{}"}}"""),
                },
                new()
                {
                    Type = "response.completed",
                    Data = Bytes("""{"type":"response.completed","response":{"id":"resp_tool","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
                },
            ]),
            Bytes("""{"model":"gpt-5","input":[{"type":"message","role":"user","content":"run the tool"}]}"""),
            sessionId: null,
            Scope);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(stream.Next());
        }

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_tool","input":[{"type":"custom_tool_call_output","call_id":"call_1","output":"ok"}]}""");
        AssertJsonEqual(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"run the tool"},{"id":"ctc_1","type":"custom_tool_call","call_id":"call_1","name":"exec_command","input":"{}"},{"type":"custom_tool_call_output","call_id":"call_1","output":"ok"}]}""",
            prepared.Body);

        stream.Dispose();
    }

    [Fact]
    public async Task NormalizesClientProvidedHistory()
    {
        var store = new ResponsesSessionStore();

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","input":[{"id":"ctc_1","type":"custom_tool_call","status":"completed","call_id":"call_1","name":"exec","input":"{}","metadata":{"status":"preserved"}},{"type":"custom_tool_call_output","call_id":"call_1","output":"ok"}]}""");

        AssertJsonEqual(
            """{"model":"gpt-5","input":[{"id":"ctc_1","type":"custom_tool_call","call_id":"call_1","name":"exec","input":"{}","metadata":{"status":"preserved"}},{"type":"custom_tool_call_output","call_id":"call_1","output":"ok"}]}""",
            prepared.Body);
    }

    [Fact]
    public async Task PreservesWebSearchCallStatusWithoutPreviousResponse()
    {
        var store = new ResponsesSessionStore();
        var body = """{"model":"gpt-5","input":[{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"example"}}]}""";

        var prepared = await PrepareAsync(store, body);

        Assert.Equal(body, Text(prepared.Body));
    }

    [Fact]
    public async Task PreservesWebSearchCallStatusInPreviousResponse()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","input":"search docs"}"""),
            Bytes("""{"id":"resp_search","status":"completed","output":[{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"example"}},{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":"found it"}]}"""),
            sessionId: null,
            Scope);

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_search","input":"continue"}""");

        AssertJsonEqual(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"search docs"},{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"example"}},{"id":"msg_1","type":"message","role":"assistant","content":"found it"},{"type":"message","role":"user","content":"continue"}]}""",
            prepared.Body);
    }

    [Fact]
    public async Task PreservesWebSearchCallStatusOnMemoryMiss()
    {
        var store = new ResponsesSessionStore(
            load: (responseId, _, _) =>
            {
                Assert.Equal("resp_search", responseId);
                return Task.FromResult(new ResponsesSessionLoaded(
                    Bytes("""{"model":"gpt-5","input":"search docs"}"""),
                    Bytes("""{"id":"resp_search","status":"completed","output":[{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"example"}}]}"""),
                    true));
            });

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_search","input":"continue"}""");

        AssertJsonEqual(
            """{"model":"gpt-5","input":[{"type":"message","role":"user","content":"search docs"},{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"example"}},{"type":"message","role":"user","content":"continue"}]}""",
            prepared.Body);
    }

    [Fact]
    public async Task NormalizesClientHistoryOnCacheMiss()
    {
        var store = new ResponsesSessionStore();

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"missing","input":[{"type":"message","role":"assistant","status":"completed","content":"hello"}]}""");

        AssertJsonEqual(
            """{"model":"gpt-5","previous_response_id":"missing","input":[{"type":"message","role":"assistant","content":"hello"}]}""",
            prepared.Body);
    }

    [Fact]
    public async Task LeavesStatusFreeInputUnchanged()
    {
        var store = new ResponsesSessionStore();
        var body = """{ "model": "gpt-5", "input": "hello" }""";

        var prepared = await PrepareAsync(store, body);

        Assert.Equal(body, Text(prepared.Body));
    }

    [Fact]
    public async Task RestoresSessionIdForSseToWebSocketCache()
    {
        var store = new ResponsesSessionStore();

        var first = await PrepareAsync(store, """{"model":"gpt-5","input":"one"}""");
        Assert.NotEmpty(first.SessionId);
        store.Record(
            first.Body!,
            Bytes("""{"id":"resp_1","status":"completed","output":[]}"""),
            first.SessionId,
            Scope);

        var second = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_1","input":"two"}""");

        Assert.Equal(first.SessionId, second.SessionId);
    }

    [Fact]
    public async Task PreservesContextSessionIdWhenRecordHasNone()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","input":"one"}"""),
            Bytes("""{"id":"resp_1","status":"completed","output":[]}"""),
            sessionId: null,
            Scope);

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_1","input":"two"}""",
            sessionId: "context-session");

        Assert.Equal("context-session", prepared.SessionId);
    }

    [Fact]
    public void StreamStopsBufferingOversizedResponse()
    {
        var store = new ResponsesSessionStore();
        var wrapped = store.WrapStream(
            new SliceStream<StreamEvent>(
            [
                new()
                {
                    Type = "response.completed",
                    Data = Encoding.UTF8.GetBytes(new string('x', ResponsesSessionStore.MaxResponse)),
                },
            ]),
            Bytes("""{"model":"gpt-5","input":"one"}"""),
            sessionId: null,
            scope: null);

        Assert.True(wrapped.Next());
        Assert.False(wrapped.Next());
        wrapped.Dispose();
        Assert.True(Assert.IsType<ResponsesSessionStream>(wrapped).Overflow);
    }

    [Fact]
    public void EvictsExpiredRecords()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ResponsesSessionStore(new ManualTimeProvider(now));
        store.ByResponse[new ResponsesSessionKey("scope", "expired")] = new ResponsesSessionRecord
        {
            UpdatedAt = now - ResponsesSessionStore.Ttl - TimeSpan.FromMinutes(1),
            Size = 11,
        };
        store.ByResponse[new ResponsesSessionKey("scope", "fresh")] = new ResponsesSessionRecord
        {
            UpdatedAt = now,
            Size = 13,
        };
        store.TotalBytes = 24;

        Assert.NotNull(store.Lookup("fresh", "scope"));
        Assert.Null(store.Lookup("expired", "scope"));
        Assert.Equal(13, store.TotalBytes);
    }

    [Fact]
    public void EnforcesMaximumRecordCount()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ResponsesSessionStore(new ManualTimeProvider(now));
        for (var i = 0; i < ResponsesSessionStore.MaxRecords; i++)
        {
            store.ByResponse[new ResponsesSessionKey("scope", "resp_" + i)] = new ResponsesSessionRecord
            {
                UpdatedAt = now - TimeSpan.FromMinutes(1),
                Size = 1,
            };
        }

        store.TotalBytes = ResponsesSessionStore.MaxRecords;

        store.Record(
            Bytes("""{"model":"gpt-5","input":"latest"}"""),
            Bytes("""{"id":"resp_latest","status":"completed","output":[]}"""),
            sessionId: null,
            "scope");

        Assert.True(store.ByResponse.Count <= ResponsesSessionStore.MaxRecords);
        Assert.NotNull(store.Lookup("resp_latest", "scope"));
    }

    [Fact]
    public void EnforcesMaximumBytes()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ResponsesSessionStore(new ManualTimeProvider(now));
        var perRecord = ResponsesSessionStore.MaxBytes / 64;
        for (var i = 0; i < 64; i++)
        {
            store.ByResponse[new ResponsesSessionKey("scope", "resp_" + i)] = new ResponsesSessionRecord
            {
                UpdatedAt = now - TimeSpan.FromMinutes(1),
                Size = perRecord,
            };
        }

        store.TotalBytes = perRecord * 64;

        store.Record(
            Bytes("""{"model":"gpt-5","input":"latest"}"""),
            Bytes("""{"id":"resp_latest","status":"completed","output":[]}"""),
            sessionId: null,
            "scope");

        Assert.True(store.TotalBytes <= ResponsesSessionStore.MaxBytes);
        Assert.NotNull(store.Lookup("resp_latest", "scope"));
    }

    [Fact]
    public void WrapStreamPreservesResponseHeaders()
    {
        var store = new ResponsesSessionStore();
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [ResponseHeaders.ReasoningIncludedHeader] = ["true"],
        };
        var stream = ResponseHeaders.WithResponseHeaders(
            new SliceStream<StreamEvent>(
            [
                new() { Data = Bytes("""{"type":"response.completed"}""") },
            ]),
            headers);

        var wrapped = store.WrapStream(
            stream!,
            Bytes("""{"model":"gpt-5"}"""),
            sessionId: null,
            scope: null);

        var attached = ResponseHeaders.GetResponseHeaders(wrapped);
        Assert.NotNull(attached);
        Assert.Equal(
            new List<string> { "true" },
            attached[ResponseHeaders.ReasoningIncludedHeader]);
    }

    [Fact]
    public async Task LoaderFailureFallsBackToTheClientBody()
    {
        var store = new ResponsesSessionStore(
            load: (_, _, _) => throw new InvalidOperationException("store unavailable"));

        var prepared = await PrepareAsync(
            store,
            """{"model":"gpt-5","previous_response_id":"resp_missing","input":"hello"}""");

        AssertJsonEqual(
            """{"model":"gpt-5","previous_response_id":"resp_missing","input":"hello"}""",
            prepared.Body);
    }

    [Fact]
    public async Task SkipsFailedAndCancelledResponses()
    {
        var store = new ResponsesSessionStore();
        store.Record(
            Bytes("""{"model":"gpt-5","input":"hello"}"""),
            Bytes("""{"id":"resp_failed","status":"failed","output":[]}"""),
            sessionId: null,
            Scope);
        store.Record(
            Bytes("""{"model":"gpt-5","input":"hello"}"""),
            Bytes("""{"id":"resp_cancelled","status":"cancelled","output":[]}"""),
            sessionId: null,
            Scope);

        Assert.Empty(store.ByResponse);
    }
}
