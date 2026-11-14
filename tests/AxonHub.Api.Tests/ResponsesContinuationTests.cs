using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of orchestrator/responses_continuation_test.go and
/// responses_origin_test.go: an id the upstream cannot forward fails before
/// dispatch, a persisted-but-unrestorable continuation is a recovery rather than
/// a fresh upstream miss, the first native HTTP origin pins later attempts, and
/// a native WebSocket lane never keeps an adapter snapshot.
/// </summary>
public sealed class ResponsesContinuationTests
{
    private const string Scope = "api-key:1";

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static readonly ResponsesOrigin HttpOrigin = new(
        1,
        string.Empty,
        ApiFormats.OpenAIResponse,
        "gpt-5",
        string.Empty,
        0);

    private static ResponsesContinuation Continuation(
        ResponsesSessionStore? store,
        string body,
        string? scope = Scope,
        ResponsesLane? lane = null,
        ResponsesAttempt? attempt = null,
        bool webSocket = false,
        bool known = false,
        bool knownFailed = false)
        => new(
            store,
            Bytes(body),
            scope,
            sessionId: null,
            lane,
            attempt,
            webSocket,
            preloadedRecord: null,
            known,
            knownFailed);

    private static void AssertRecovery(Exception error)
    {
        Assert.True(GoErrors.Is(error, ResponsesRecovery.Sentinel));

        var wire = Assert.IsType<HttpClientError>(GoErrors.As<HttpClientError>(error));
        Assert.Equal(400, wire.StatusCode);

        using var body = JsonDocument.Parse(wire.Body);
        Assert.Equal(
            "previous_response_not_found",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static ChannelCandidate Candidate(long channelId)
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = "gpt-5",
            Type = "openai",
            ApiFormat = ApiFormats.OpenAIResponse,
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            Models =
            [
                new ChannelCandidateModel
                {
                    RequestModel = "gpt-5",
                    ActualModel = "gpt-5",
                    Source = "direct",
                },
            ],
        };

    /// <summary>
    /// Port of TestResponsesContinuationSelectsEligibleOwnerBeforeDispatch
    /// (responses_origin_test.go:17-40): an incomplete persisted continuation
    /// owned by candidate B is pinned there even though A is ranked first.
    /// </summary>
    [Fact]
    public void SelectsEligibleOwnerBeforeDispatch()
    {
        var store = new ResponsesSessionStore();
        store.ByResponse[new ResponsesSessionKey(Scope, "delta")] = new ResponsesSessionRecord
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Origin = new ResponsesOrigin(2, string.Empty, ApiFormats.OpenAIResponse, "gpt-5", string.Empty, 0),
        };
        var continuation = new ResponsesContinuation(
            store,
            Bytes("""{"previous_response_id":"delta","input":"next"}"""),
            Scope,
            sessionId: null,
            lane: null,
            attempt: null,
            webSocket: false,
            preloadedRecord: store.Lookup("delta", Scope));
        var outbound = new PersistentOutboundTransformer(
            [Candidate(1), Candidate(2)],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            (candidate, _, _) => new RecordingOutbound(candidate.ChannelId))
        {
            ResponsesContinuation = continuation,
        };

        var sent = outbound.TransformRequest(new LlmRequest { Model = "gpt-5", Messages = [] });

        Assert.Equal(2, outbound.CurrentChannelId);
        Assert.StartsWith("http://channel-2.test", sent.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownIdFailsBeforeDispatch()
    {
        var continuation = Continuation(
            new ResponsesSessionStore(),
            """{"model":"gpt-5","previous_response_id":"unknown","input":"delta"}""");

        var error = Assert.ThrowsAny<Exception>(() => continuation.PrepareAttemptBody(
            new ResponsesAttemptTarget(HttpOrigin, ForwardsId: false)));

        AssertRecovery(error);
    }

    [Fact]
    public void PersistedDeltaIsNotExternalUnknownId()
    {
        var store = new ResponsesSessionStore(
            known: (_, _) => Task.FromResult(true));
        var continuation = Continuation(
            store,
            """{"previous_response_id":"persisted_delta","input":"delta"}""",
            known: true);

        var error = Assert.ThrowsAny<Exception>(() => continuation.PrepareAttemptBody(
            new ResponsesAttemptTarget(HttpOrigin, ForwardsId: true)));

        AssertRecovery(error);
        Assert.Empty(store.ByResponse);
    }

    [Fact]
    public void KnownMissingOwnerFailsWithoutRecoveryWrite()
    {
        var store = new ResponsesSessionStore();
        var continuation = Continuation(
            store,
            """{"previous_response_id":"missing","input":"delta"}""",
            knownFailed: true);

        var error = Assert.ThrowsAny<Exception>(() => continuation.PrepareAttemptBody(
            new ResponsesAttemptTarget(HttpOrigin, ForwardsId: true)));

        AssertRecovery(error);
        Assert.Empty(store.ByResponse);
    }

    [Fact]
    public void HttpNativeIdStaysOnOrigin()
    {
        var continuation = Continuation(
            new ResponsesSessionStore(),
            """{"previous_response_id":"external","input":"delta"}""");

        var body = continuation.PrepareAttemptBody(
            new ResponsesAttemptTarget(HttpOrigin, ForwardsId: true));
        Assert.Equal(
            """{"previous_response_id":"external","input":"delta"}""",
            Encoding.UTF8.GetString(body));

        continuation.FirstOrigin = HttpOrigin;

        var error = Assert.ThrowsAny<Exception>(() => continuation.PrepareAttemptBody(
            new ResponsesAttemptTarget(HttpOrigin with { ChannelId = 2 }, ForwardsId: true)));

        AssertRecovery(error);
    }

    [Fact]
    public void NativeLaneHasNoAdapterSnapshot()
    {
        var lane = new ResponsesLane("native", new ResponsesBudget(64 << 20));
        var attempt = new ResponsesAttempt();
        var continuation = Continuation(
            store: null,
            body: """{"input":"root"}""",
            lane: lane,
            attempt: attempt,
            webSocket: true);

        _ = continuation.PrepareAttemptBody(new ResponsesAttemptTarget(
            HttpOrigin with { Format = ApiFormats.OpenAIResponseWebSocket },
            ForwardsId: true));
        attempt.SetOrigin(HttpOrigin with
        {
            Format = ApiFormats.OpenAIResponseWebSocket,
            Generation = 1,
        });
        continuation.Capture.Body = Bytes("""{"id":"native_id","status":"completed","output":[]}""");

        continuation.Accept();

        Assert.Equal("native_id", lane.Record().Id);
        Assert.Null(lane.Record().Snapshot);
    }

    /// <summary>
    /// A provider stand-in that names its channel in the wire URL and forwards
    /// <c>previous_response_id</c> untouched, so the selection tests can assert
    /// which candidate the attempt was pinned to.
    /// </summary>
    private sealed class RecordingOutbound(long channelId) : IOutboundTransformer, ITransportRequestFinalizer
    {
        public string ApiFormat => ApiFormats.OpenAIResponse;

        public HttpRequestData TransformRequest(LlmRequest? request)
            => new()
            {
                Method = "POST",
                Url = "http://channel-" + channelId + ".test/v1/responses",
            };

        public HttpRequestData? FinalizeTransportRequest(HttpRequestData? request) => request;

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }
}
