using System.Collections.Concurrent;
using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Api.PromptProtection;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the Responses transport adaptation and identity preservation paths in
/// internal/server/orchestrator/pass_through.go: when the client and provider
/// speak different Responses transports (HTTP vs WebSocket) the raw request and
/// response envelopes are replayed across the boundary, and when both sides
/// speak the same transport only the echoed model is restored to the client's
/// value. Provider item IDs and unknown fields must survive both paths.
/// </summary>
public sealed class ResponsesTransportPassThroughTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    private sealed class StubOutboundTransformer(string apiFormat) : IOutboundTransformer
    {
        public string ApiFormat { get; } = apiFormat;

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            _ = request;
            return new HttpRequestData { ApiFormat = ApiFormat };
        }

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }

    /// <summary>Vetoes both raw request replay and raw response replay.</summary>
    private sealed class VetoingOutboundTransformer(string apiFormat)
        : IOutboundTransformer, IPassThroughBodyPolicy, IPassThroughResponsePolicy
    {
        public string ApiFormat { get; } = apiFormat;

        public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
        {
            _ = llmRequest;
            _ = providerRequest;
            return false;
        }

        public bool AllowPassThroughResponse(LlmRequest? llmRequest, HttpRequestData? providerRequest)
        {
            _ = llmRequest;
            _ = providerRequest;
            return false;
        }

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            _ = request;
            return new HttpRequestData { ApiFormat = ApiFormat };
        }

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] body) => Encoding.UTF8.GetString(body);

    private static LlmRequest UnifiedRequest(
        string apiFormat,
        string model,
        string rawBody,
        string? jsonBody = null)
        => new()
        {
            Model = model,
            ApiFormat = apiFormat,
            Stream = false,
            RawRequest = new HttpRequestData
            {
                Method = "POST",
                Url = "http://gateway.test/v1",
                ApiFormat = apiFormat,
                Body = Bytes(rawBody),
                JsonBody = jsonBody is null ? null : Bytes(jsonBody),
            },
        };

    /// <summary>
    /// Builds the transformer after one outbound transform and stores the
    /// provider request the way applyPassThroughRequestBody does.
    /// </summary>
    private static PersistentOutboundTransformer Outbound(
        LlmRequest llmRequest,
        string? providerFormat = null,
        string? channelSettings = null,
        IOutboundTransformer? transformer = null)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = 1,
            ChannelName = "test-channel",
            ActualModel = llmRequest.Model,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            PassThroughSettings = ChannelPassThroughSettings.Parse(channelSettings),
        };

        var format = providerFormat ?? llmRequest.ApiFormat ?? ApiFormats.OpenAIResponse;
        var outbound = new PersistentOutboundTransformer(
            [candidate],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            (_, _, _) => transformer ?? new StubOutboundTransformer(format));

        outbound.TransformRequest(llmRequest);
        outbound.RawProviderRequest = new HttpRequestData
        {
            Method = "POST",
            Url = "http://upstream.test/v1",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            ApiFormat = format,
            Body = Bytes("""{"model":"generated-by-transformer"}"""),
        };

        return outbound;
    }

    private static HttpRequestData ProviderRequest(string apiFormat)
        => new()
        {
            Method = "POST",
            Url = "http://upstream.test/v1",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            ApiFormat = apiFormat,
            Body = Bytes("""{"model":"generated-by-transformer"}"""),
        };

    private static HttpResponseData RawResponse(string body, HttpRequestData? request = null)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["X-Provider"] = ["one"],
            },
            Body = Bytes(body),
            Request = request,
        };

    private static PassThroughStreamState State(
        BlockingCollection<StreamEvent> channel,
        RawStreamBacklog? backlog = null)
        => new()
        {
            RawChannel = channel,
            Backlog = backlog ?? new RawStreamBacklog(),
            ErrorRef = new PassThroughErrorRef(),
            Cancel = () => { },
        };

    private static BlockingCollection<StreamEvent> Channel(params StreamEvent[] events)
    {
        var channel = new BlockingCollection<StreamEvent>(new ConcurrentQueue<StreamEvent>(), 8);
        foreach (var @event in events)
        {
            channel.Add(@event);
        }

        channel.CompleteAdding();
        return channel;
    }

    private static List<StreamEvent> Drain(IStream<StreamEvent> stream, int limit)
    {
        var events = new List<StreamEvent>();
        while (events.Count < limit && stream.Next())
        {
            events.Add(stream.Current!);
        }

        return events;
    }

    private static StreamEvent Data(string data, string? lastEventId = null)
        => new() { Data = Bytes(data), LastEventId = lastEventId };

    // ------------------------------------------------------------------
    // Format predicates
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(ApiFormats.OpenAIResponse, true)]
    [InlineData(ApiFormats.OpenAIResponseWebSocket, true)]
    [InlineData(ApiFormats.OpenAIResponseCompact, false)]
    [InlineData(ApiFormats.OpenAIImageGeneration, false)]
    [InlineData(ApiFormats.OpenAIChatCompletion, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsResponsesFormatExcludesCompactAndImages(string? format, bool expected)
        => Assert.Equal(expected, PassThrough.IsResponsesFormat(format));

    public static TheoryData<string, string, bool> AdaptationPairs => new()
    {
        { ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponseWebSocket, true },
        { ApiFormats.OpenAIResponseWebSocket, ApiFormats.OpenAIResponse, true },
        { ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponse, false },
        { ApiFormats.OpenAIResponseWebSocket, ApiFormats.OpenAIResponseWebSocket, false },
        { ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponseCompact, false },
        { ApiFormats.OpenAIChatCompletion, ApiFormats.OpenAIResponseWebSocket, false },
    };

    [Theory]
    [MemberData(nameof(AdaptationPairs))]
    public void TransportAdaptationNeedsTwoDifferentResponsesFormats(
        string source,
        string target,
        bool expected)
    {
        var outbound = Outbound(
            UnifiedRequest(source, "client-alias", """{"model":"client-alias"}"""),
            providerFormat: target);

        Assert.Equal(expected, PassThrough.TransportAdaptationEnabled(outbound));
    }

    [Theory]
    [InlineData(ApiFormats.OpenAIResponse, true)]
    [InlineData(ApiFormats.OpenAIResponseWebSocket, true)]
    [InlineData(ApiFormats.OpenAIResponseCompact, false)]
    [InlineData(ApiFormats.OpenAIChatCompletion, false)]
    public void IdentityPreservationNeedsTheSameResponsesFormat(string format, bool expected)
    {
        var outbound = Outbound(
            UnifiedRequest(format, "client-alias", """{"model":"client-alias"}"""),
            providerFormat: format);

        Assert.Equal(expected, PassThrough.IdentityPreservationEnabled(outbound));
    }

    [Fact]
    public void PredicatesAreFalseWithoutAnAttempt()
    {
        var outbound = new PersistentOutboundTransformer(
            [],
            TimeProvider.System,
            new RecordingMetricsRecorder());

        Assert.False(PassThrough.TransportAdaptationEnabled(outbound));
        Assert.False(PassThrough.IdentityPreservationEnabled(outbound));
        Assert.Equal(string.Empty, PassThrough.ResponsesClientModel(outbound));
    }

    [Fact]
    public void ClientModelFallsBackToTheParsedJsonBody()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "mapped-model",
                rawBody: string.Empty,
                jsonBody: """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponse);

        Assert.Equal("client-alias", PassThrough.ResponsesClientModel(outbound));
    }

    // ------------------------------------------------------------------
    // applyResponsesTransportRequestBody
    // ------------------------------------------------------------------

    [Fact]
    public void TransportRequestBodyAdaptsHttpToWebSocketAndPatchesTheModel()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "upstream-model",
                """{"model":"client-alias","input":[{"type":"message","id":"msg_provider"}],"future_request":true}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket);
        var request = ProviderRequest(ApiFormats.OpenAIResponseWebSocket);

        var processed = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        // Provider item IDs and unknown fields survive; the mapped model is
        // written back for the target transport; keys are ordinal-sorted.
        Assert.Equal(
            """{"future_request":true,"input":[{"type":"message","id":"msg_provider"}],"model":"upstream-model","type":"response.create"}""",
            Text(processed.Body!));
        Assert.Equal("application/json", processed.ContentType);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(processed.Headers, "Content-Type"));
        Assert.True(outbound.PassThroughApplied);
    }

    [Fact]
    public void TransportRequestBodyAdaptsWebSocketToHttpAndDropsTheEnvelope()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponseWebSocket,
                "upstream-model",
                """{"type":"response.create","stream_id":"lane","generate":true,"model":"client-alias","input":"hi"}"""),
            providerFormat: ApiFormats.OpenAIResponse);
        var request = ProviderRequest(ApiFormats.OpenAIResponse);

        var processed = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        Assert.Equal("""{"input":"hi","model":"upstream-model"}""", Text(processed.Body!));
        Assert.True(outbound.PassThroughApplied);
    }

    [Fact]
    public void TransportRequestBodyRejectsAWebSocketWarmupHeadingToHttp()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponseWebSocket,
                "upstream-model",
                """{"type":"response.create","generate":false,"model":"client-alias","input":"hi"}"""),
            providerFormat: ApiFormats.OpenAIResponse);

        var error = Assert.Throws<TransformerException>(
            () => new ResponsesTransportRequestBodyMiddleware(outbound)
                .TransformOutboundRawRequest(ProviderRequest(ApiFormats.OpenAIResponse)));

        Assert.Equal(TransformerErrorKind.UnsupportedConversion, error.Kind);
        Assert.Equal("unsupported conversion: HTTP cannot perform WebSocket warmup", error.Message);
        Assert.False(outbound.PassThroughApplied);
    }

    [Fact]
    public void TransportRequestBodyKeepsTheOutboundBodyWhenAdaptationDoesNotApply()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "upstream-model", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponse);
        var request = ProviderRequest(ApiFormats.OpenAIResponse);

        var processed = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        Assert.Same(request, processed);
        Assert.Equal("""{"model":"generated-by-transformer"}""", Text(processed.Body!));
        Assert.False(outbound.PassThroughApplied);
    }

    [Fact]
    public void TransportRequestBodyHonoursTheOutboundBodyPolicy()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "upstream-model", """{"model":"client-alias","input":"hi"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket,
            transformer: new VetoingOutboundTransformer(ApiFormats.OpenAIResponseWebSocket));
        var request = ProviderRequest(ApiFormats.OpenAIResponseWebSocket);

        var processed = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        Assert.Same(request, processed);
        Assert.False(outbound.PassThroughApplied);
    }

    [Fact]
    public void TransportRequestBodyAppliesPromptProtectionBeforeAdapting()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponseWebSocket,
                "upstream-model",
                """{"type":"response.create","model":"client-alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"secret-001"}]}]}"""),
            providerFormat: ApiFormats.OpenAIResponse);
        outbound.PromptProtectionMaskRules =
            [PassThroughPromptProtectionTests.MaskRule("secret-[0-9]+", scopes: PromptProtectionScopes.User)];

        var processed = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(ProviderRequest(ApiFormats.OpenAIResponse));

        Assert.Equal(
            """{"input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"[MASKED]"}]}],"model":"upstream-model"}""",
            Text(processed.Body!));
    }

    [Fact]
    public void TransportRequestBodyFallsBackWhenPromptProtectionCannotPatch()
    {
        // A rule that matched the unified prompt but cannot be located in the
        // raw layout must not let the unprotected body through.
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "upstream-model",
                """{"model":"client-alias","input":[{"type":"function_call_output","output":"secret-001"}]}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket);
        outbound.PromptProtectionMaskRules =
            [PassThroughPromptProtectionTests.MaskRule("secret-[0-9]+", scopes: PromptProtectionScopes.User)];

        var request = ProviderRequest(ApiFormats.OpenAIResponseWebSocket);
        var processed = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        // The reference logs and keeps the outbound body.
        Assert.Same(request, processed);
        Assert.False(outbound.PassThroughApplied);
    }

    [Fact]
    public void TransportRequestBodyRunsAfterThePassThroughBodyMiddleware()
    {
        // orchestrator.go:279-287: pass-through replay stores the provider
        // request first, then the transport adaptation rewrites the body.
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "upstream-model",
                """{"model":"client-alias","input":"hi"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket,
            channelSettings: "{\"passThroughBody\":true}");
        var request = ProviderRequest(ApiFormats.OpenAIResponseWebSocket);

        request = new PassThroughRequestBodyMiddleware(
                outbound,
                new PassThroughSettings(true, false))
            .TransformOutboundRawRequest(request);
        request = new ResponsesTransportRequestBodyMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        Assert.Equal(
            """{"input":"hi","model":"upstream-model","type":"response.create"}""",
            Text(request.Body!));
        Assert.True(outbound.PassThroughApplied);
    }

    // ------------------------------------------------------------------
    // preserveResponsesModel
    // ------------------------------------------------------------------

    [Fact]
    public void PreserveModelPatchesTopLevelAndNestedPaths()
    {
        var body = Bytes(
            """{"id":"resp_1","model":"upstream","response":{"model":"upstream","x":1},"output":[]}""");

        var patched = PassThrough.PreserveResponsesModel(body, "client-alias");

        Assert.Equal(
            """{"id":"resp_1","model":"client-alias","response":{"model":"client-alias","x":1},"output":[]}""",
            Text(patched));
    }

    [Fact]
    public void PreserveModelSkipsMissingPathsAndKeepsTheOriginalCopy()
    {
        var body = Bytes("""{"id":"resp_1","status":"completed"}""");

        var patched = PassThrough.PreserveResponsesModel(body, "client-alias");

        Assert.Equal(Text(body), Text(patched));
        Assert.NotSame(body, patched);
    }

    [Fact]
    public void PreserveModelReturnsACopyForAnEmptyClientModel()
    {
        var body = Bytes("""{"model":"upstream"}""");

        var patched = PassThrough.PreserveResponsesModel(body, string.Empty);

        Assert.Equal(Text(body), Text(patched));
        Assert.NotSame(body, patched);
    }

    [Fact]
    public void PreserveModelEscapesQuotesAndKeepsUtf8Literal()
    {
        var body = Bytes("""{"model":"upstream"}""");

        var quoted = Text(PassThrough.PreserveResponsesModel(body, "a\"b"));
        Assert.Equal("{\"model\":\"a\\\"b\"}", quoted);

        var utf8 = Text(PassThrough.PreserveResponsesModel(body, "模型"));
        Assert.Equal("""{"model":"模型"}""", utf8);
    }

    [Fact]
    public void PreserveModelNeverMutatesTheSourceBody()
    {
        var body = Bytes("""{"model":"upstream"}""");

        _ = PassThrough.PreserveResponsesModel(body, "client-alias");

        Assert.Equal("""{"model":"upstream"}""", Text(body));
    }

    [Fact]
    public void PreserveStreamEventModelClonesTheEventAndOnlyReplacesTheData()
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["X-Event"] = ["one"],
        };
        var original = new StreamEvent
        {
            LastEventId = "7",
            Type = "response.output_item.done",
            Data = Bytes("""{"type":"response.output_item.done","model":"upstream","item":{"id":"msg_1"}}"""),
            CleanEofCompletionEvidence = true,
            Size = 42,
            Headers = headers,
        };

        var patched = PassThrough.PreserveResponsesStreamEventModel(original, "client-alias")!;

        Assert.NotSame(original, patched);
        Assert.Equal("7", patched.LastEventId);
        Assert.Equal("response.output_item.done", patched.Type);
        Assert.Equal(42, patched.Size);
        Assert.True(patched.CleanEofCompletionEvidence);
        Assert.Same(headers, patched.Headers);
        Assert.Equal(
            """{"type":"response.output_item.done","model":"client-alias","item":{"id":"msg_1"}}""",
            Text(patched.Data));
        Assert.Equal("upstream", OverrideJson.Get(original.Data, "model").StringValue);
    }

    // ------------------------------------------------------------------
    // applyPassThroughResponse
    // ------------------------------------------------------------------

    [Fact]
    public void IdentityResponsePatchesTheModelInAClone()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "upstream-model",
                """{"model":"client-alias","input":"hi"}"""),
            providerFormat: ApiFormats.OpenAIResponse,
            channelSettings: "{\"passThroughBody\":false}");
        var request = ProviderRequest(ApiFormats.OpenAIResponse);
        var raw = RawResponse(
            """{"id":"resp_provider","model":"upstream-model","output":[{"id":"msg_provider"}],"future":1}""",
            request);
        outbound.RawProviderResponse = raw;
        var transformed = RawResponse("transformed");

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.NotSame(raw, result);
        Assert.NotSame(transformed, result);
        Assert.Equal(200, result.StatusCode);
        Assert.Same(raw.Headers, result.Headers);
        Assert.Same(request, result.Request);
        Assert.Equal(
            """{"id":"resp_provider","model":"client-alias","output":[{"id":"msg_provider"}],"future":1}""",
            Text(result.Body!));
        Assert.Equal("upstream-model", OverrideJson.Get(raw.Body, "model").StringValue);
    }

    [Fact]
    public void PassThroughResponseAlsoPreservesIdentity()
    {
        // The reference applies identity preservation even when pass-through
        // already selected the raw body.
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "upstream-model",
                """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponse,
            channelSettings: "{\"passThroughBody\":true}");
        outbound.RawProviderResponse = RawResponse("""{"id":"resp_1","model":"upstream-model"}""");

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(RawResponse("transformed"));

        Assert.Equal("""{"id":"resp_1","model":"client-alias"}""", Text(result.Body!));
    }

    [Fact]
    public void AdaptedResponseKeepsTheTransformedRequestPlumbing()
    {
        var outbound = Outbound(
            UnifiedRequest(
                ApiFormats.OpenAIResponse,
                "upstream-model",
                """{"model":"client-alias","input":"hi"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket);
        var raw = RawResponse(
            """{"id":"resp_provider","model":"upstream-model","output":[{"id":"msg_provider"}],"future":1}""");
        outbound.RawProviderResponse = raw;
        var transformedRequest = ProviderRequest(ApiFormats.OpenAIResponse);
        var transformed = RawResponse("transformed", transformedRequest);

        var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformInboundRawResponse(transformed);

        Assert.NotSame(raw, result);
        Assert.Equal(
            """{"id":"resp_provider","model":"client-alias","output":[{"id":"msg_provider"}],"future":1}""",
            Text(result.Body!));
        Assert.Same(transformedRequest, result.Request);
        Assert.Same(raw.Headers, result.Headers);
    }

    [Fact]
    public void AdaptedResponseRequiresAProviderResponseId()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "upstream-model", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket);
        outbound.RawProviderResponse = RawResponse("""{"model":"upstream-model","output":[]}""");

        var error = Assert.Throws<TransformerException>(
            () => new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
                .TransformInboundRawResponse(RawResponse("transformed")));

        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
        Assert.Equal("invalid response: provider response ID is required", error.Message);
    }

    [Fact]
    public void ResponsePolicyVetoesIdentityAndAdaptation()
    {
        foreach (var (client, provider) in new[]
                 {
                     (ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponse),
                     (ApiFormats.OpenAIResponse, ApiFormats.OpenAIResponseWebSocket),
                 })
        {
            var outbound = Outbound(
                UnifiedRequest(client, "upstream-model", """{"model":"client-alias"}"""),
                providerFormat: provider,
                transformer: new VetoingOutboundTransformer(provider));
            outbound.RawProviderResponse = RawResponse("""{"id":"resp_1","model":"upstream-model"}""");
            var transformed = RawResponse("transformed");

            var result = new PassThroughResponseMiddleware(outbound, PassThroughSettings.Disabled)
                .TransformInboundRawResponse(transformed);

            Assert.Same(transformed, result);
        }
    }

    [Fact]
    public void CaptureStoresTheRawResponseForTransportAdaptationWithoutPassThrough()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "client-alias", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket,
            channelSettings: "{\"passThroughBody\":false}");
        var raw = RawResponse("""{"id":"resp_1"}""");

        var result = new CaptureRawProviderResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformOutboundRawResponse(raw);

        Assert.Same(raw, result);
        Assert.Same(raw, outbound.RawProviderResponse);
    }

    [Fact]
    public void CaptureStoresTheRawResponseForIdentityWithoutPassThrough()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "client-alias", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponse,
            channelSettings: "{\"passThroughBody\":false}");
        var raw = RawResponse("""{"id":"resp_1"}""");

        _ = new CaptureRawProviderResponseMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformOutboundRawResponse(raw);

        Assert.Same(raw, outbound.RawProviderResponse);
    }

    // ------------------------------------------------------------------
    // Streams
    // ------------------------------------------------------------------

    [Fact]
    public void TransportStreamAdaptsEveryEventAndKeepsTheProviderPayload()
    {
        var inner = new ListStream<StreamEvent>(
        [
            Data("""{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1"},"future":1}""", "7"),
        ]);

        var stream = new ResponsesTransportStream(
            inner,
            ApiFormats.OpenAIResponseWebSocket,
            ApiFormats.OpenAIResponse);

        var events = Drain(stream, 10);

        var @event = Assert.Single(events);
        Assert.Equal("response.output_item.done", @event.Type);
        Assert.Equal("7", @event.LastEventId);
        Assert.Equal(
            """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1"},"future":1}""",
            Text(@event.Data));
    }

    [Fact]
    public void TransportStreamStopsAtTheFirstMalformedEvent()
    {
        var inner = new ListStream<StreamEvent>(
        [
            Data("""{"type":"response.output_item.done","item":{"id":"msg_1"}}"""),
            Data("""{"type":"response.completed","response":{"id":"resp_1","status":"failed"}}"""),
            Data("""{"type":"response.output_item.done","item":{"id":"msg_2"}}"""),
        ]);

        var stream = new ResponsesTransportStream(
            inner,
            ApiFormats.OpenAIResponse,
            ApiFormats.OpenAIResponseWebSocket);

        Assert.True(stream.Next());
        Assert.False(stream.Next());

        var error = Assert.IsType<TransformerException>(stream.Err);
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(
            "invalid response: completed event requires a completed provider response",
            error.Message);

        // Delivery stops; the trailing valid event is never reached.
        Assert.False(stream.Next());
    }

    [Fact]
    public void TransportStreamFallsBackToTheInnerError()
    {
        var error = new IOException("stream error");
        var stream = new ResponsesTransportStream(
            new ErrorStream(error),
            ApiFormats.OpenAIResponse,
            ApiFormats.OpenAIResponseWebSocket);

        Assert.Same(error, stream.Err);
        Assert.False(stream.Next());
        Assert.Same(error, stream.Err);
    }

    [Fact]
    public void IdentityStreamPatchesTheModelOfEveryEvent()
    {
        var inner = new ListStream<StreamEvent>(
        [
            Data("""{"type":"response.output_item.done","model":"upstream","item":{"id":"msg_1"}}"""),
            Data("""{"type":"response.completed","response":{"id":"resp_1","model":"upstream","status":"completed"}}"""),
        ]);

        var stream = new ResponsesIdentityStream(inner, "client-alias");
        var events = Drain(stream, 10);

        Assert.Equal(2, events.Count);
        Assert.Equal("client-alias", OverrideJson.Get(events[0].Data, "model").StringValue);
        Assert.Equal("client-alias", OverrideJson.Get(events[1].Data, "response.model").StringValue);
        Assert.Equal("msg_1", OverrideJson.Get(events[0].Data, "item.id").StringValue);
        Assert.Equal("completed", OverrideJson.Get(events[1].Data, "response.status").StringValue);
    }

    [Fact]
    public void PassThroughStreamWrapsRawEventsForAdaptation()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "upstream-model", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket,
            channelSettings: "{\"passThroughBody\":false}");
        outbound.PassThroughStream = State(Channel(
            Data("""{"type":"response.output_item.done","model":"upstream-model","item":{"id":"msg_1"}}"""),
            Data("""{"type":"response.completed","response":{"id":"resp_1","model":"upstream-model","status":"completed"}}""")));

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        var events = Drain(result, 10);

        Assert.Equal(2, events.Count);
        Assert.Equal("client-alias", OverrideJson.Get(events[0].Data, "model").StringValue);
        Assert.Equal("msg_1", OverrideJson.Get(events[0].Data, "item.id").StringValue);
        Assert.Equal("client-alias", OverrideJson.Get(events[1].Data, "response.model").StringValue);
        Assert.Equal("resp_1", OverrideJson.Get(events[1].Data, "response.id").StringValue);
    }

    [Fact]
    public void PassThroughStreamWrapsRawEventsForIdentity()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "upstream-model", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponse,
            channelSettings: "{\"passThroughBody\":false}");
        outbound.PassThroughStream = State(Channel(
            Data("""{"type":"response.output_item.done","model":"upstream-model","item":{"id":"msg_1"}}""")));

        var result = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        var events = Drain(result, 10);

        var @event = Assert.Single(events);
        Assert.Equal("client-alias", OverrideJson.Get(@event.Data, "model").StringValue);
        Assert.Equal("msg_1", OverrideJson.Get(@event.Data, "item.id").StringValue);
    }

    [Fact]
    public void CaptureStreamFansOutForIdentityWithoutPassThrough()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "client-alias", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponse,
            channelSettings: "{\"passThroughBody\":false}");
        var original = new ListStream<StreamEvent>([Data("""{"type":"response.output_item.done"}""")]);

        var result = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(original);

        Assert.NotSame(original, result);
        Assert.NotNull(outbound.PassThroughStream);
        Assert.Single(Drain(result, 10));
    }

    [Fact]
    public void CaptureStreamFansOutForTransportAdaptation()
    {
        var outbound = Outbound(
            UnifiedRequest(ApiFormats.OpenAIResponse, "client-alias", """{"model":"client-alias"}"""),
            providerFormat: ApiFormats.OpenAIResponseWebSocket,
            channelSettings: "{\"passThroughBody\":false}");
        var events = new[]
        {
            Data("""{"type":"response.output_item.done","item":{"id":"msg_1"}}"""),
            Data("""{"type":"response.completed","response":{"id":"resp_1","model":"upstream-model","status":"completed"}}"""),
        };
        var source = new ListStream<StreamEvent>(events);

        var pipelineStream = new CaptureRawProviderStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformOutboundRawStream(source);

        Assert.Equal<StreamEvent>(events, Drain(pipelineStream, 10));

        var passThrough = new PassThroughStreamMiddleware(
                outbound,
                PassThroughSettings.Disabled,
                CancellationToken.None)
            .TransformInboundRawStream(new ListStream<StreamEvent>([]));

        var delivered = Drain(passThrough, 10);

        // The client sees both events through the transport adapter and then
        // the identity patch restores the requested model.
        Assert.Equal(2, delivered.Count);
        Assert.Equal("response.output_item.done", delivered[0].Type);
        Assert.Equal("msg_1", OverrideJson.Get(delivered[0].Data, "item.id").StringValue);
        Assert.Equal("client-alias", OverrideJson.Get(delivered[1].Data, "response.model").StringValue);
        Assert.Equal("resp_1", OverrideJson.Get(delivered[1].Data, "response.id").StringValue);
    }

    /// <summary>Yields a fixed event list, then reports a terminal error.</summary>
    private sealed class ErrorStream(Exception error) : IStream<StreamEvent>
    {
        public StreamEvent? Current => null;

        public Exception? Err => error;

        public bool Next() => false;

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
