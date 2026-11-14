using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/responses/protocol_codec_test.go: the shared
/// codec changes only the Responses transport envelope and keeps provider item
/// IDs, unknown fields and raw item bytes verbatim. The rejection cases also
/// pin Go's <c>encoding/json</c> error wording, because the reference surfaces
/// those messages to callers.
/// </summary>
public sealed class ResponsesProtocolCodecTests
{
    private const string ProtocolRequest =
        """{"model":"synthetic","previous_response_id":"resp_provider","input":[{"type":"message","id":"msg_provider","role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,c3ludGhldGlj","detail":"original","future_image":{"canary":1}}]},{"type":"function_call","id":"fc_provider","call_id":"call_provider","name":"synthetic","arguments":"{}","future_call":true},{"type":"function_call_output","call_id":"call_provider","output":"tool-canary","future_result":2},{"type":"reasoning","id":"rs_provider","encrypted_content":"synthetic-encrypted","summary":[],"future_reasoning":true},{"type":"image_generation_call","id":"ig_provider","action":"edit","result":"synthetic-image","future_action":true}],"tools":[{"type":"function","name":"synthetic","parameters":{"type":"object"},"future_tool":true}],"additional_tools":[{"type":"custom","name":"extra","format":{"type":"text"}}],"future_request":{"canary":"raw"}}""";

    private const string ProtocolResponse =
        """{"id":"resp_provider","object":"response","model":"synthetic","status":"completed","output":[{"type":"message","id":"msg_provider","role":"assistant","content":[{"type":"output_text","text":"canary","annotations":[],"future_content":true}]},{"type":"function_call","id":"fc_provider","call_id":"call_provider","name":"synthetic","arguments":"{}"},{"type":"image_generation_call","id":"ig_provider","action":"edit","result":"synthetic-image","future_image":true}],"future_response":{"canary":42}}""";

    private static readonly string[] ResponsesFormats =
    [
        ApiFormats.OpenAIResponse,
        ApiFormats.OpenAIResponseWebSocket,
    ];

    public static TheoryData<string, string> TransportPairs()
    {
        var data = new TheoryData<string, string>();
        foreach (var source in ResponsesFormats)
        {
            foreach (var target in ResponsesFormats)
            {
                data.Add(source, target);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TransportPairs))]
    public void TransportConversionPreservesItems(string source, string target)
    {
        // Given: identical provider content under either transport envelope.
        var request = source == ApiFormats.OpenAIResponseWebSocket
            ? Encoding.UTF8.GetBytes(
                """{"type":"response.create","stream_id":"lane","generate":true,""" + ProtocolRequest[1..])
            : Encoding.UTF8.GetBytes(ProtocolRequest);
        var original = Encoding.UTF8.GetString(request);

        // When: the shared production codec changes only the transport.
        var converted = ResponsesProtocolCodec.AdaptRequestBody(request, source, target);

        // Then: all raw items, IDs, tools and unknown fields survive.
        var expected = target == ApiFormats.OpenAIResponseWebSocket
            ? """{"type":"response.create",""" + ProtocolRequest[1..]
            : ProtocolRequest;
        ResponsesStreamTestSupport.AssertJsonEquivalent(expected, Encoding.UTF8.GetString(converted));

        // The input buffer is never mutated.
        Assert.Equal(original, Encoding.UTF8.GetString(request));

        var responseBody = ResponsesProtocolCodec.AdaptResponseBody(
            Encoding.UTF8.GetBytes(ProtocolResponse),
            source,
            target);
        Assert.Equal(ProtocolResponse, Encoding.UTF8.GetString(responseBody));
    }

    [Theory]
    [MemberData(nameof(TransportPairs))]
    public void TransportConversionPreservesItemsEvents(string source, string target)
    {
        string[] events =
        [
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"image_generation_call","id":"ig_provider","action":"edit","result":"synthetic-image","future_item":true},"future_event":true}""",
            $$"""{"type":"response.completed","response":{{ProtocolResponse}},"future_event":true}""",
        ];

        foreach (var data in events)
        {
            var @event = new StreamEvent
            {
                Data = Encoding.UTF8.GetBytes(data),
                LastEventId = "provider-event",
            };

            var converted = ResponsesProtocolCodec.AdaptStreamEvent(@event, source, target);

            Assert.NotNull(converted);
            Assert.Equal(data, Encoding.UTF8.GetString(converted.Data));
            Assert.Equal("provider-event", converted.LastEventId);
            Assert.NotEmpty(converted.Type);
        }
    }

    [Theory]
    [InlineData("""{""", TransformerErrorKind.InvalidRequest, "invalid request: invalid Responses object: unexpected end of JSON input")]
    [InlineData("""null""", TransformerErrorKind.InvalidRequest, "invalid request: model is required")]
    [InlineData("""[]""", TransformerErrorKind.InvalidRequest, "invalid request: invalid Responses object: json: cannot unmarshal array into Go value of type map[string]json.RawMessage")]
    [InlineData("""{}""", TransformerErrorKind.InvalidRequest, "invalid request: model is required")]
    [InlineData("""{"model":2}""", TransformerErrorKind.InvalidRequest, "invalid request: invalid Responses fields: json: cannot unmarshal number into Go struct field .model of type string")]
    [InlineData("""{"model":"synthetic","type":"response.cancel"}""", TransformerErrorKind.UnsupportedConversion, "unsupported conversion: response.create is required")]
    public void TransportConversionRejectsMalformedRequest(
        string body,
        TransformerErrorKind kind,
        string message)
    {
        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptRequestBody(
                Encoding.UTF8.GetBytes(body),
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));

        Assert.Equal(kind, error.Kind);
        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData(ApiFormats.OpenAIImageGeneration)]
    [InlineData(ApiFormats.OpenAIResponseCompact)]
    public void TransportConversionRequiresResponsesFormats(string format)
    {
        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptRequestBody(
                Encoding.UTF8.GetBytes(ProtocolRequest),
                format,
                ApiFormats.OpenAIResponseWebSocket));

        Assert.Equal(TransformerErrorKind.UnsupportedConversion, error.Kind);
        Assert.Equal("unsupported conversion: Responses transport formats required", error.Message);
    }

    [Theory]
    [InlineData("""{""", "invalid response: invalid Responses response: unexpected end of JSON input")]
    [InlineData("""null""", "invalid response: provider response ID is required")]
    [InlineData("""[]""", "invalid response: invalid Responses response: json: cannot unmarshal array into Go value of type responses.protocolResponseBody")]
    [InlineData("""{"status":"completed"}""", "invalid response: provider response ID is required")]
    [InlineData("""{"id":2,"status":"completed"}""", "invalid response: invalid Responses response: json: cannot unmarshal number into Go struct field protocolResponseBody.id of type string")]
    [InlineData("""{"id":"resp_provider","status":"completed","output":"invalid"}""", "invalid response: invalid Responses response: json: cannot unmarshal string into Go struct field protocolResponseBody.output of type []json.RawMessage")]
    public void TransportConversionRejectsMisleadingResponseSuccess(string body, string message)
    {
        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptResponseBody(
                Encoding.UTF8.GetBytes(body),
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));

        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void TransportConversionRejectsIncompleteCompletedEvent()
    {
        // A completed label without a provider response is insufficient.
        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                new StreamEvent { Data = """{"type":"response.completed"}"""u8.ToArray() },
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(
            "invalid response: completed event requires a completed provider response",
            error.Message);

        // The terminal label disagrees with the provider status.
        error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                new StreamEvent
                {
                    Data = """{"type":"response.completed","response":{"id":"resp_provider","status":"failed"}}"""u8.ToArray(),
                },
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void TransportConversionRejectsMalformedEvent()
    {
        var nilEvent = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                null,
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal("invalid response: event is nil", nilEvent.Message);

        var rootArray = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                new StreamEvent { Data = "[]"u8.ToArray() },
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal(
            "invalid response: invalid Responses event: json: cannot unmarshal array into Go value of type "
            + "struct { Type string \"json:\\\"type\\\"\"; Response *responses.protocolResponseBody \"json:\\\"response\\\"\" }",
            rootArray.Message);

        var nestedField = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                new StreamEvent { Data = """{"type":"response.completed","response":{"id":2}}"""u8.ToArray() },
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal(
            "invalid response: invalid Responses event: json: cannot unmarshal number into Go struct field "
            + "protocolResponseBody.response.id of type string",
            nestedField.Message);

        var mismatchedType = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                new StreamEvent
                {
                    Type = "response.completed",
                    Data = """{"type":"response.failed","response":{"id":"resp_provider","status":"failed"}}"""u8.ToArray(),
                },
                ApiFormats.OpenAIResponse,
                ApiFormats.OpenAIResponseWebSocket));
        Assert.Equal(
            "invalid response: event type is missing or inconsistent",
            mismatchedType.Message);
    }

    [Fact]
    public void TransportConversionStateIsolation()
    {
        // Given: borrowed input bytes and a stale SSE label.
        var body = Encoding.UTF8.GetBytes(ProtocolResponse);
        var @event = new StreamEvent
        {
            Type = "response.completed",
            Data = """{"type":"response.failed","response":{"id":"resp_provider","status":"failed"}}"""u8.ToArray(),
        };

        // When
        var converted = ResponsesProtocolCodec.AdaptResponseBody(
            body,
            ApiFormats.OpenAIResponse,
            ApiFormats.OpenAIResponseWebSocket);

        // Then: returned payload ownership is independent of the source.
        body[0] = (byte)'!';
        Assert.Equal(ProtocolResponse, Encoding.UTF8.GetString(converted));

        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptStreamEvent(
                @event,
                ApiFormats.OpenAIResponse,
                ApiFormats.OpenAIResponseWebSocket));
        Assert.Equal(TransformerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void TransportConversionPreservesItemsWarmup()
    {
        var body = """{"type":"response.create","model":"synthetic","generate":false,"input":"canary"}"""u8.ToArray();

        // A warmup cannot silently become an HTTP generation.
        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptRequestBody(
                body,
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal(TransformerErrorKind.UnsupportedConversion, error.Kind);
        Assert.Equal("unsupported conversion: HTTP cannot perform WebSocket warmup", error.Message);

        // WS to WS keeps the warmup flag (and sorts the envelope like Go).
        var converted = ResponsesProtocolCodec.AdaptRequestBody(
            body,
            ApiFormats.OpenAIResponseWebSocket,
            ApiFormats.OpenAIResponseWebSocket);
        Assert.Equal(
            """{"generate":false,"input":"canary","model":"synthetic","type":"response.create"}""",
            Encoding.UTF8.GetString(converted));
    }

    [Fact]
    public void RequestMarshalSortsKeysCompactsAndEscapesHtml()
    {
        var body = """{ "model" : "synthetic" , "input" : [ 1 , 2 ] , "zeta" : "<x>" }"""u8.ToArray();

        var converted = ResponsesProtocolCodec.AdaptRequestBody(
            body,
            ApiFormats.OpenAIResponse,
            ApiFormats.OpenAIResponseWebSocket);

        // Go's compact pass HTML-escapes the replayed raw values.
        var backslash = (char)92;
        var expected = "{\"input\":[1,2],\"model\":\"synthetic\",\"type\":\"response.create\","
            + "\"zeta\":\"" + backslash + "u003cx" + backslash + "u003e\"}";
        Assert.Equal(expected, Encoding.UTF8.GetString(converted));
    }

    [Fact]
    public void RequestEnvelopeMatchesFoldedFieldNamesAndNullZeroValues()
    {
        // Go matches struct fields case-insensitively after an exact-tag miss.
        var converted = ResponsesProtocolCodec.AdaptRequestBody(
            """{"MODEL":"synthetic"}"""u8.ToArray(),
            ApiFormats.OpenAIResponseWebSocket,
            ApiFormats.OpenAIResponse);
        Assert.Equal("""{"MODEL":"synthetic"}""", Encoding.UTF8.GetString(converted));

        // A JSON null leaves the Go string field at its zero value.
        var error = Assert.Throws<TransformerException>(
            () => ResponsesProtocolCodec.AdaptRequestBody(
                """{"model":null}"""u8.ToArray(),
                ApiFormats.OpenAIResponseWebSocket,
                ApiFormats.OpenAIResponse));
        Assert.Equal("invalid request: model is required", error.Message);

        // A normalized WS body without an envelope type is accepted.
        converted = ResponsesProtocolCodec.AdaptRequestBody(
            """{"model":"synthetic","input":"canary"}"""u8.ToArray(),
            ApiFormats.OpenAIResponseWebSocket,
            ApiFormats.OpenAIResponse);
        Assert.Equal("""{"input":"canary","model":"synthetic"}""", Encoding.UTF8.GetString(converted));
    }
}
