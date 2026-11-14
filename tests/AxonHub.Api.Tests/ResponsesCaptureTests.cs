using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of orchestrator/responses_capture_test.go and
/// responses_capture_stream_test.go: the completion observer commits the raw
/// provider identity, the 16 MiB aggregate boundary never certifies a prefix,
/// snapshot reservations are shared per lane budget, and closing or failing a
/// turn releases every reservation exactly once.
/// </summary>
public sealed class ResponsesCaptureTests
{
    private const string Scope = "api-key:1";

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static void AssertJsonEqual(string expected, byte[] actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "expected JSON " + expected + " but got " + Encoding.UTF8.GetString(actual));
    }

    /// <summary>
    /// A root turn (no previous_response_id) prepared against a Responses
    /// attempt, so <c>complete</c>/<c>retain</c> and the retained input match
    /// what the reference tests set by hand.
    /// </summary>
    private static ResponsesContinuation RootContinuation(
        string body,
        ResponsesLane? lane,
        bool webSocket = false,
        ResponsesAttempt? attempt = null)
    {
        var continuation = new ResponsesContinuation(
            store: null,
            original: Bytes(body),
            scope: Scope,
            sessionId: null,
            lane: lane,
            attempt: attempt,
            webSocket: webSocket);

        _ = continuation.PrepareAttemptBody(new ResponsesAttemptTarget(
            new ResponsesOrigin(
                1,
                "http://channel-1.test/v1/responses",
                webSocket ? ApiFormats.OpenAIResponseWebSocket : ApiFormats.OpenAIResponse,
                "gpt-5",
                string.Empty,
                0),
            true));

        return continuation;
    }

    [Fact]
    public void CommitsActualProviderIdentity()
    {
        var lane = new ResponsesLane("lane", new ResponsesBudget(64 << 20));
        var continuation = RootContinuation("""{"input":"root"}""", lane);

        continuation.Capture.Observe(new StreamEvent
        {
            Data = Bytes(
                """{"type":"response.completed","response":{"id":"actual_provider_id","status":"completed","output":[{"id":"actual_item_id","type":"message"}]}}"""),
        });

        continuation.Accept();

        var record = lane.Record();
        Assert.Equal("actual_provider_id", record.Id);
        Assert.NotNull(record.Snapshot);
        Assert.Single(record.Snapshot!.Output);
        AssertJsonEqual("""{"id":"actual_item_id","type":"message"}""", record.Snapshot.Output[0]);
    }

    [Theory]
    [InlineData((16 << 20) - 1)]
    [InlineData((16 << 20) + 1)]
    public void SixteenMiBBoundary(int size)
    {
        var terminal = Bytes(
            """{"type":"response.completed","response":{"id":"provider_id","status":"completed","output":[]}}""");
        var capture = new ResponsesCompletion();

        var prefix = Bytes("{\"type\":\"response.output_text.delta\",\"delta\":\"");
        var delta = new byte[size - terminal.Length];
        prefix.CopyTo(delta, 0);
        delta.AsSpan(prefix.Length, delta.Length - prefix.Length - 2).Fill((byte)'x');
        delta[^2] = (byte)'"';
        delta[^1] = (byte)'}';

        capture.Observe(new StreamEvent { Data = delta });
        capture.Observe(new StreamEvent { Data = terminal });

        Assert.Equal(size, capture.Bytes);
        Assert.Equal(size > 16 << 20, capture.Overflow);
    }

    [Fact]
    public void OverflowNeverCertifiesPrefix()
    {
        var lane = new ResponsesLane("lane", new ResponsesBudget(64 << 20));
        var continuation = RootContinuation("""{"input":"root"}""", lane);

        continuation.Capture.Bytes = 16 << 20;
        continuation.Capture.Observe(new StreamEvent
        {
            Data = Bytes(
                """{"type":"response.completed","response":{"id":"provider_id","status":"completed","output":[]}}"""),
        });

        continuation.Accept();

        Assert.Equal("provider_id", lane.Record().Id);
        Assert.Null(lane.Record().Snapshot);
    }

    [Fact]
    public void RetainsFullSnapshotAtAggregateLimit()
    {
        var body = """{"id":"provider_id","status":"completed","output":[{"type":"message"}]}""";
        var bodyBytes = Bytes(body);
        const string inputJson = """[{"type":"message","content":"input"}]""";
        var inputBytes = Encoding.UTF8.GetByteCount(inputJson);
        var budget = new ResponsesBudget(inputBytes + bodyBytes.Length);
        var lane = new ResponsesLane("lane", budget);
        var continuation = RootContinuation("""{"input":""" + inputJson + "}", lane);
        Assert.True(budget.Replace(0, bodyBytes.Length));
        continuation.Capture.Body = bodyBytes;
        continuation.Capture.Retained = bodyBytes.Length;

        continuation.Accept();

        var record = lane.Record();
        Assert.NotNull(record.Snapshot);
        Assert.Single(record.Snapshot!.Input);
        Assert.Single(record.Snapshot.Output);
    }

    [Fact]
    public void SharedBudgetRetainsMultipleLanes()
    {
        var body = """{"id":"provider_id","status":"completed","output":[{"type":"message"}]}""";
        var bodyBytes = Bytes(body);
        const string inputJson = """[{"type":"message","content":"input"}]""";
        var inputBytes = Encoding.UTF8.GetByteCount(inputJson);
        var reservation = inputBytes + bodyBytes.Length;
        var budget = new ResponsesBudget(2 * reservation);

        foreach (var lane in new[]
                 {
                     new ResponsesLane("lane-1", budget),
                     new ResponsesLane("lane-2", budget),
                 })
        {
            var continuation = RootContinuation("""{"input":""" + inputJson + "}", lane);
            Assert.True(budget.Replace(0, bodyBytes.Length));
            continuation.Capture.Body = bodyBytes;
            continuation.Capture.Retained = bodyBytes.Length;

            continuation.Accept();

            Assert.NotNull(lane.Record().Snapshot);
        }
    }

    [Fact]
    public void ErrorCleanupReleasesReservationsOnce()
    {
        const string original = """{"input":[]}""";
        var body = Bytes("not-json");
        var retention = Encoding.UTF8.GetByteCount("[]");
        var budget = new ResponsesBudget(retention + body.Length);
        var lane = new ResponsesLane("lane", budget);
        var continuation = RootContinuation(original, lane);
        continuation.Capture.Body = body;
        continuation.Capture.Retained = body.Length;
        Assert.True(budget.Replace(0, body.Length));

        continuation.Accept();
        continuation.ReleaseRetention();
        continuation.ReleaseCapture();
        continuation.ReleaseRetention();
        continuation.ReleaseCapture();

        Assert.True(budget.Replace(0, retention + body.Length));
        Assert.True(budget.Replace(retention + body.Length, 0));
    }

    [Fact]
    public void CloseReleasesReservationsOnce()
    {
        var body = Bytes("""{"id":"provider_id","status":"completed"}""");
        var retention = Encoding.UTF8.GetByteCount("[]");
        var budget = new ResponsesBudget(retention + body.Length);
        var lane = new ResponsesLane("lane", budget);
        var continuation = RootContinuation("""{"input":[]}""", lane);
        continuation.Capture.Body = body;
        continuation.Capture.Retained = body.Length;
        Assert.True(budget.Replace(0, body.Length));
        var stream = new ResponsesAcceptedStream(new SliceStream<StreamEvent>([]), continuation);

        stream.Dispose();
        stream.Dispose();

        Assert.True(budget.Replace(0, retention + body.Length));
        Assert.True(budget.Replace(retention + body.Length, 0));
    }

    [Fact]
    public void PreservesConsumedEvents()
    {
        StreamEvent[] events =
        [
            new() { Type = "response.created", Data = Bytes("""{"type":"response.created"}""") },
            new() { Type = "response.output_item.added", Data = Bytes("""{"type":"response.output_item.added"}""") },
            new() { Type = "response.completed", Data = Bytes("""{"type":"response.completed"}""") },
        ];
        var stream = new ResponsesProviderStream(
            new SliceStream<StreamEvent>(events),
            new ResponsesCompletion());

        var received = new List<StreamEvent>();
        while (stream.Next())
        {
            received.Add(stream.Current!);
        }

        Assert.Equal(events.Length, received.Count);
        for (var index = 0; index < events.Length; index++)
        {
            Assert.Same(events[index], received[index]);
        }
    }
}
