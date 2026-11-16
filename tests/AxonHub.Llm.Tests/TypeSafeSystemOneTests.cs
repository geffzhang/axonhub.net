using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.TypeSafe;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the TypeSafe SystemOne surface
/// (llm/systemone.go, llm/transformer/typesafe/{model,inbound,outbound}.go).
/// The request/response round-trip tests are ports of llm/systemone_test.go and
/// the typesafe transformer tests; the rest lock the Go <c>any</c> JSON
/// semantics, validation order and error envelopes the port reproduces.
/// </summary>
public sealed class TypeSafeSystemOneTests
{
    // ------------------------------------------------ unified SystemOne model

    [Fact]
    public void SystemOneRequest_DeserializesStateAndQuestions()
    {
        // Port of TestSystemOneSerialization's request half.
        var req = LlmJson.Deserialize<SystemOneRequest>(
            """
            {
                "state": {"ticket_id": "123", "text": "Stripe payment failed"},
                "questions": {
                    "is_urgent": {"type": "noul", "instructions": "Is urgent?"},
                    "dept": {
                        "type": "choice",
                        "instructions": "Which dept?",
                        "criteria": {"billing": "Billing issues", "tech": "Bugs"}
                    }
                }
            }
            """)!;

        Assert.Equal(JsonValueKind.Object, req.State.ValueKind);
        Assert.Equal("123", req.State.GetProperty("ticket_id").GetString());
        Assert.Equal(2, req.Questions!.Count);
        Assert.Equal("noul", req.Questions["is_urgent"].Type);
        Assert.Equal("Which dept?", req.Questions["dept"].Instructions.GetString());
        Assert.Equal(
            "Bugs",
            req.Questions["dept"].Criteria.GetProperty("tech").GetString());
    }

    [Fact]
    public void SystemOneResponse_MarshalIncludesChoice()
    {
        // Port of TestSystemOneSerialization's response half.
        var resp = new SystemOneResponse
        {
            Answers = new Dictionary<string, SystemOneAnswer>
            {
                ["dept"] = new()
                {
                    Type = "choice",
                    Choice = "billing",
                    Confidence = 0.95,
                },
            },
        };

        var json = Encoding.UTF8.GetString(GoJson.Marshal(resp));

        Assert.Equal(
            """{"answers":{"dept":{"type":"choice","choice":"billing","confidence":0.95}}}""",
            json);
    }

    [Fact]
    public void SystemOneAnswer_PreservesExtraFields()
    {
        // Port of TestSystemOneAnswer_PreserveExtraFields.
        const string rawJson =
            """{"answers":{"fraud":{"type":"choice","choice":"yes","confidence":0.99,"custom_flag":true,"debug_info":{"step":"evaluate"}}}}""";

        var resp = LlmJson.Deserialize<SystemOneResponse>(rawJson)!;
        var answer = resp.Answers!["fraud"];

        Assert.Equal("choice", answer.Type);
        Assert.Equal("yes", answer.Choice);
        Assert.Equal(0.99, answer.Confidence!.Value, 12);
        Assert.NotNull(answer.Extra);
        Assert.Equal("true", answer.Extra!["custom_flag"].GetRawText());
        Assert.Equal("""{"step":"evaluate"}""", answer.Extra["debug_info"].GetRawText());

        // A non-empty extra map re-serializes the answer as a Go map: keys sorted.
        Assert.Equal(
            """{"answers":{"fraud":{"choice":"yes","confidence":0.99,"custom_flag":true,"debug_info":{"step":"evaluate"},"type":"choice"}}}""",
            Encoding.UTF8.GetString(GoJson.Marshal(resp)));
    }

    [Fact]
    public void SystemOneAnswer_WithoutExtraKeepsStructFieldOrder()
    {
        var resp = new SystemOneResponse
        {
            Answers = new Dictionary<string, SystemOneAnswer>
            {
                ["q"] = new()
                {
                    Type = "score",
                    Noul = 0.1,
                    Score = 4.5,
                    Legend = Json("""{"level_id": 9007199254740999}"""),
                    Probabilities = Json("""{"1": 0.05, "2": 0.95}"""),
                    Confidence = 0.9,
                },
            },
        };

        Assert.Equal(
            """{"answers":{"q":{"type":"score","noul":0.1,"score":4.5,"legend":{"level_id":9007199254740999},"probabilities":{"1":0.05,"2":0.95},"confidence":0.9}}}""",
            Encoding.UTF8.GetString(GoJson.Marshal(resp)));
    }

    [Fact]
    public void SystemOneAnswer_CaseInsensitiveMatchKeepsExactExtraKey()
    {
        // Go matches struct fields case-insensitively but deletes only the exact
        // lowercase names from the raw map, so "TYPE" sets Type *and* survives.
        var answer = LlmJson.Deserialize<SystemOneAnswer>(
            """{"TYPE":"noul","type":"score","Noul":0.5}""")!;

        Assert.Equal("score", answer.Type);
        Assert.Equal(0.5, answer.Noul!.Value, 12);
        Assert.Equal("noul", answer.Extra!["TYPE"].GetString());
        // "Noul" is another differently-cased spelling, so it stays too.
        Assert.Equal("0.5", answer.Extra["Noul"].GetRawText());

        // The merged map marshal keeps both extras under ordinal key order.
        Assert.Equal(
            """{"Noul":0.5,"TYPE":"noul","noul":0.5,"type":"score"}""",
            Encoding.UTF8.GetString(GoJson.Marshal(answer)));
    }

    [Fact]
    public void SystemOneRequest_GoAnyStateSortsKeysAndKeepsNumberLiterals()
    {
        var req = LlmJson.Deserialize<SystemOneRequest>(
            """{"state":{"z":1,"a":{"y":2,"x":9007199254740993,"f":9007199254740999}},"questions":{}}""")!;

        Assert.Equal(
            """{"state":{"a":{"f":9007199254740999,"x":9007199254740993,"y":2},"z":1},"questions":{}}""",
            Encoding.UTF8.GetString(GoJson.Marshal(req)));
    }

    [Fact]
    public void SystemOneQuestion_IgnoresUnknownFieldsAndAppliesZeroValues()
    {
        var question = LlmJson.Deserialize<SystemOneQuestion>(
            """{"type":null,"instructions":null,"criteria":null,"unknown":1}""")!;

        Assert.Equal(string.Empty, question.Type);
        Assert.Equal(JsonValueKind.Undefined, question.Instructions.ValueKind);
        Assert.Equal(JsonValueKind.Undefined, question.Criteria.ValueKind);

        // type and instructions are always written; criteria has omitempty.
        Assert.Equal(
            """{"type":"","instructions":null}""",
            Encoding.UTF8.GetString(GoJson.Marshal(question)));
    }

    [Fact]
    public void SystemOneAnswer_NullMapValueCoalescesToZeroAnswer()
    {
        var resp = LlmJson.Deserialize<SystemOneResponse>("""{"answers":{"q":null}}""")!;

        // STJ's map reader does not invoke the class converter for a null value.
        SystemOneJson.CoalesceAnswers(resp.Answers);

        Assert.NotNull(resp.Answers!["q"]);
        Assert.Equal(string.Empty, resp.Answers["q"].Type);
        Assert.Equal("""{"answers":{"q":{}}}""", Encoding.UTF8.GetString(GoJson.Marshal(resp)));
    }

    // ------------------------------------------------ inbound TransformRequest

    [Fact]
    public void InboundTransformRequest_ValidRequestPreservesLargeInt()
    {
        // Port of TestSystemOneInboundTransformer_TransformRequest.
        var req = new SystemOneInboundTransformer().TransformRequest(JsonRequest(
            """
            {
                "model": "jev-latest",
                "state": {"large_int": 9007199254740993},
                "questions": {
                    "urgent": {"type": "noul", "instructions": "Is this urgent?"}
                }
            }
            """));

        Assert.Equal("jev-latest", req.Model);
        Assert.Equal(RequestTypes.SystemOne, req.RequestType);
        Assert.Equal(ApiFormats.TypeSafeSystemOne, req.ApiFormat);
        Assert.NotNull(req.RawRequest);
        Assert.NotNull(req.SystemOne);

        // json.Number survives: the literal is not round-tripped through a double.
        Assert.Equal(
            "9007199254740993",
            req.SystemOne!.State.GetProperty("large_int").GetRawText());
        Assert.True(req.SystemOne.Questions!.ContainsKey("urgent"));
        Assert.Equal(
            "Is this urgent?",
            req.SystemOne.Questions["urgent"].Instructions.GetString());
    }

    [Fact]
    public void InboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(
                JsonRequest("""{"state": "foo"}""")));

        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_StreamTrueIsRejected()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest(
                """
                {
                    "model": "jev-latest",
                    "stream": true,
                    "state": "foo",
                    "questions": {"urgent": {"type": "noul", "instructions": "?"}}
                }
                """)));

        Assert.Equal(
            "invalid request: streaming is not supported for systemone requests",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_MissingState()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest(
                """{"model":"m","questions":{"q":{"type":"noul","instructions":"?"}}}""")));

        Assert.Equal("invalid request: state is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_JsonNullStateIsMissingState()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest(
                """{"model":"m","state":null,"questions":{"q":{"type":"noul","instructions":"?"}}}""")));

        Assert.Equal("invalid request: state is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_MissingQuestions()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"m","state":"s"}""")));

        Assert.Equal("invalid request: questions are required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_EmptyQuestions()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(
                JsonRequest("""{"model":"m","state":"s","questions":{}}""")));

        Assert.Equal("invalid request: questions are required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_JsonNullBodyDecodesToZeroRequest()
    {
        // Go: Decoder.Decode("null", &wireReq) is a no-op, then model is missing.
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest("null")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_NilRequest()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(null));

        Assert.Equal("invalid request: http request is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_EmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest(string.Empty)));

        Assert.Equal("invalid request: request body is empty", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_WhitespaceBodyIsEof()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest("   ")));

        Assert.Equal(
            "invalid request: failed to decode systemone request: EOF",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_TruncatedBodyIsUnexpectedEof()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest("""{"model":""")));

        Assert.Equal(
            "invalid request: failed to decode systemone request: unexpected EOF",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_TrailingDataIsIgnored()
    {
        // json.Decoder.Decode reads one value; the reference never asks for a
        // second one, so trailing garbage does not fail the request.
        var req = new SystemOneInboundTransformer().TransformRequest(JsonRequest(
            """{"model":"m","state":"s","questions":{"q":{"type":"noul","instructions":"?"}}} trailing"""));

        Assert.Equal("m", req.Model);
    }

    [Fact]
    public void InboundTransformRequest_RootArrayIsATypeError()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(JsonRequest("[]")));

        Assert.Equal(
            "invalid request: failed to decode systemone request: "
            + "json: cannot unmarshal array into Go value of type typesafe.systemOneWireRequest",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_FieldTypeMismatchIsReported()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new SystemOneInboundTransformer().TransformRequest(
                JsonRequest("""{"model":123,"state":"s","questions":{}}""")));

        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.StartsWith(
            "invalid request: failed to decode systemone request: ",
            ex.Message,
            StringComparison.Ordinal);
    }

    // ----------------------------------------------- inbound TransformResponse

    [Fact]
    public void InboundTransformResponse_EmitsNoulAndUsage()
    {
        // Port of TestSystemOneInboundTransformer_TransformResponse.
        var httpResp = new SystemOneInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "jev-latest",
            SystemOne = new SystemOneResponse
            {
                Answers = new Dictionary<string, SystemOneAnswer>
                {
                    ["urgent"] = new() { Type = "noul", Noul = 0.98 },
                },
            },
            Usage = new Usage { PromptTokens = 10, CompletionTokens = 1 },
        });

        Assert.Equal(200, httpResp.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpResp.Headers, "Content-Type"));
        Assert.Equal(
            """{"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.98}},"usage":{"input_tokens":10,"output_tokens":1}}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_WithoutUsageOmitsUsage()
    {
        var httpResp = new SystemOneInboundTransformer().TransformResponse(new LlmResponse
        {
            Model = "jev-latest",
            SystemOne = new SystemOneResponse(),
        });

        Assert.Equal(
            """{"model":"jev-latest","answers":null}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_NilResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SystemOneInboundTransformer().TransformResponse(null));

        Assert.Equal("llm response is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformResponse_NilSystemOne()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new SystemOneInboundTransformer().TransformResponse(new LlmResponse()));

        Assert.Equal("systemone response is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformError_NilIsInternalServerError()
    {
        var httpResp = new SystemOneInboundTransformer().TransformError(null);

        Assert.Equal(500, httpResp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"Internal server error","type":"api_error"}}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformError_ResponseErrorKeepsStatusAndDetail()
    {
        var httpResp = new SystemOneInboundTransformer().TransformError(new ResponseError
        {
            StatusCode = 429,
            Detail = new ErrorDetail
            {
                Message = "Rate limit exceeded",
                Type = "rate_limit_error",
                Code = "rate_limit",
            },
        });

        Assert.Equal(429, httpResp.StatusCode);
        Assert.Equal(
            """{"error":{"code":"rate_limit","message":"Rate limit exceeded","type":"rate_limit_error"}}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformError_GenericErrorIsGoQuoted()
    {
        var httpResp = new SystemOneInboundTransformer().TransformError(
            new InvalidOperationException("bad \"thing\"\nline"));

        Assert.Equal(500, httpResp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"bad \"thing\"\nline","type":"api_error"}}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformStreamAndAggregateAreUnsupported()
    {
        var transformer = new SystemOneInboundTransformer();

        Assert.Equal(
            "systemone does not support streaming",
            Assert.Throws<InvalidOperationException>(() =>
                transformer.TransformStream(new EmptyStream())).Message);

        Assert.Equal(
            "systemone does not support streaming",
            Assert.Throws<InvalidOperationException>(() =>
                transformer.AggregateStreamChunks([], out _)).Message);
    }

    // ----------------------------------------------- outbound TransformRequest

    [Fact]
    public void OutboundTransformRequest_WireShape()
    {
        // Port of TestOutboundTransformer_TransformRequest.
        var outbound = TypeSafeOutboundTransformer.Create(
            "https://api.typesafe.ai/v1",
            "sk-test-typesafe-key");

        var httpReq = outbound.TransformRequest(new LlmRequest
        {
            Model = "jev-latest",
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
            SystemOne = new SystemOneRequest
            {
                State = Json("\"Sample state\""),
                Questions = new Dictionary<string, SystemOneQuestion>
                {
                    ["check"] = new()
                    {
                        Type = "noul",
                        Instructions = Json("\"True?\""),
                    },
                },
            },
        });

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", httpReq.Url);
        Assert.Equal("sk-test-typesafe-key", httpReq.Auth!.ApiKey);
        Assert.Equal(AuthTypes.Bearer, httpReq.Auth.Type);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Accept"));
        Assert.Equal(RequestTypes.SystemOne, httpReq.RequestType);
        Assert.Equal(ApiFormats.TypeSafeSystemOne, httpReq.ApiFormat);
        Assert.Equal(
            """{"model":"jev-latest","state":"Sample state","questions":{"check":{"type":"noul","instructions":"True?"}}}""",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformRequest_BaseUrlWithoutVersionGetsV1()
    {
        var outbound = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai", "k");

        Assert.Equal("https://api.typesafe.ai/v1/systemone", outbound.BuildUrl());
    }

    [Fact]
    public void OutboundTransformRequest_DefaultBaseUrl()
    {
        var outbound = new TypeSafeOutboundTransformer(new TypeSafeOutboundConfig
        {
            ApiKeyProvider = new StaticApiKeyProvider("k"),
        });

        Assert.Equal("https://api.typesafe.ai/v1/systemone", outbound.BuildUrl());
    }

    [Fact]
    public void OutboundTransformRequest_EndpointPathOverridesDefaultPath()
    {
        var outbound = TypeSafeOutboundTransformer.Create(
            "https://proxy.test/base/",
            new StaticApiKeyProvider("k"),
            endpointPath: "/custom/systemone");

        // With an endpoint path the version suffix is not appended.
        Assert.Equal("https://proxy.test/base/custom/systemone", outbound.BuildUrl());
    }

    [Fact]
    public void OutboundTransformRequest_NilRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformRequest(null));

        Assert.Equal("llm request is nil", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_NilSystemOne()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformRequest(new LlmRequest { Model = "m" }));

        Assert.Equal("systemone request is nil", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformRequest(new LlmRequest { SystemOne = new SystemOneRequest() }));

        Assert.Equal("model is required", ex.Message);
    }

    [Fact]
    public void OutboundTransformer_ConfigValidation()
    {
        Assert.Equal(
            "config cannot be nil",
            Assert.Throws<InvalidOperationException>(() =>
                new TypeSafeOutboundTransformer(null!)).Message);

        Assert.Equal(
            "API key provider is required",
            Assert.Throws<InvalidOperationException>(() =>
                new TypeSafeOutboundTransformer(new TypeSafeOutboundConfig
                {
                    BaseUrl = "https://api.typesafe.ai/v1",
                })).Message);
    }

    [Fact]
    public void OutboundTransformer_ApiFormat()
    {
        Assert.Equal(
            ApiFormats.TypeSafeSystemOne,
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k").ApiFormat);
    }

    // ---------------------------------------------- outbound TransformResponse

    [Fact]
    public void OutboundTransformResponse_MapsModelUsageAndAnswers()
    {
        // Port of TestOutboundTransformer_TransformResponse.
        var llmResp = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformResponse(JsonResponse(
                """
                {
                    "model": "jev-latest",
                    "answers": {"check": {"type": "noul", "noul": 0.945}},
                    "usage": {"input_tokens": 120, "output_tokens": 0}
                }
                """));

        Assert.Equal("jev-latest", llmResp.Model);
        Assert.Equal(RequestTypes.SystemOne, llmResp.RequestType);
        Assert.Equal(ApiFormats.TypeSafeSystemOne, llmResp.ApiFormat);
        Assert.NotNull(llmResp.SystemOne);
        Assert.NotNull(llmResp.Usage);
        Assert.Equal(120L, llmResp.Usage!.PromptTokens);
        Assert.Equal(0L, llmResp.Usage.CompletionTokens);
        Assert.Equal(120L, llmResp.Usage.TotalTokens);
        Assert.Equal(0.945, llmResp.SystemOne!.Answers!["check"].Noul!.Value, 12);
    }

    [Fact]
    public void OutboundTransformResponse_PreservesNumericPrecision()
    {
        // Port of TestOutboundTransformer_TransformResponse_NumericPrecision.
        var llmResp = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformResponse(JsonResponse(
                """
                {
                    "model": "jev-latest",
                    "answers": {
                        "quality": {
                            "type": "score",
                            "score": 4.5,
                            "probabilities": {"1": 0.05, "2": 0.95},
                            "legend": {"level_id": 9007199254740999}
                        }
                    },
                    "usage": {"input_tokens": 100, "output_tokens": 0}
                }
                """));

        var answer = llmResp.SystemOne!.Answers!["quality"];
        Assert.Equal("score", answer.Type);
        Assert.Equal(4.5, answer.Score!.Value, 12);

        // `any` fields keep the exact number literal (json.Number).
        Assert.Equal("0.95", answer.Probabilities.GetProperty("2").GetRawText());
        Assert.Equal(
            "9007199254740999",
            answer.Legend.GetProperty("level_id").GetRawText());

        // And they survive a re-marshal unchanged (struct order, no extras).
        Assert.Equal(
            """{"type":"score","score":4.5,"legend":{"level_id":9007199254740999},"probabilities":{"1":0.05,"2":0.95}}""",
            Encoding.UTF8.GetString(GoJson.Marshal(answer)));
    }

    [Fact]
    public void OutboundTransformResponse_HttpErrorBecomesResponseError()
    {
        var ex = Assert.Throws<ResponseError>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(new HttpResponseData
                {
                    StatusCode = 429,
                    Body = Encoding.UTF8.GetBytes(
                        """{"error":{"message":"RPM limit exceeded","type":"rate_limit_error","code":"rate_limit"}}"""),
                }));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("RPM limit exceeded", ex.Detail.Message);
        Assert.Equal("rate_limit_error", ex.Detail.Type);
        Assert.Equal("rate_limit", ex.Detail.Code);
    }

    [Fact]
    public void OutboundTransformResponse_EmptyBody()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(new HttpResponseData { StatusCode = 200, Body = [] }));

        Assert.Equal("response body is empty", ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_InvalidJson()
    {
        // Port of TestOutboundTransformer_TransformResponse_InvalidJSON.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(JsonResponse(
                    """{"model": "jev-latest"} trailing malformed data""")));

        Assert.Equal("failed to unmarshal systemone response: invalid json", ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_JsonNullLeavesZeroResponse()
    {
        var llmResp = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformResponse(JsonResponse("null"));

        Assert.Equal(string.Empty, llmResp.Model);
        Assert.NotNull(llmResp.SystemOne);
        Assert.Null(llmResp.SystemOne!.Answers);
        Assert.Null(llmResp.Usage);
    }

    [Fact]
    public void OutboundTransformResponse_RootArrayIsATypeError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(JsonResponse("[]")));

        Assert.Equal(
            "failed to unmarshal systemone response envelope: "
            + "json: cannot unmarshal array into Go value of type typesafe.systemOneWireEnvelope",
            ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_NullAnswersStayNull()
    {
        var llmResp = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformResponse(JsonResponse("""{"model":"m","answers":null}"""));

        Assert.Null(llmResp.SystemOne!.Answers);
    }

    [Fact]
    public void OutboundTransformResponse_UnwrapsTheCloudflareEnvelope()
    {
        var llmResp = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformResponse(JsonResponse(
                """
                {
                    "result": {
                        "model": "clef-flash",
                        "answers": {"approve": {"type": "noul", "noul": 0.9328}},
                        "usage": {"input_tokens": 158, "output_tokens": 0}
                    },
                    "success": true,
                    "errors": [],
                    "messages": []
                }
                """));

        Assert.Equal("clef-flash", llmResp.Model);
        Assert.Equal(158L, llmResp.Usage!.PromptTokens);
        var answer = llmResp.SystemOne!.Answers!["approve"];
        Assert.Equal("noul", answer.Type);
        Assert.Equal(0.9328, answer.Noul!.Value, 9);
    }

    [Fact]
    public void OutboundTransformResponse_EnvelopeFailureCarriesTheUpstreamMessage()
    {
        var ex = Assert.Throws<ResponseError>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(JsonResponse(
                    """
                    {
                        "result": null,
                        "success": false,
                        "errors": [{"code": 7000, "message": "No route for that URI"}],
                        "messages": []
                    }
                    """)));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal("api_error", ex.Detail.Type);
        Assert.Contains("No route for that URI", ex.Detail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_EnvelopeMissingResult()
    {
        var ex = Assert.Throws<ResponseError>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(JsonResponse(
                    """{"success": true, "errors": [], "messages": []}""")));

        Assert.Equal(502, ex.StatusCode);
        Assert.Contains("result", ex.Detail.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        """{"success": false, "result": null, "error": "upstream boom"}""",
        "upstream boom")]
    [InlineData(
        """{"success": false, "result": null, "error": {"code": 7000, "message": "object boom"}}""",
        "object boom")]
    [InlineData(
        """{"success": false, "result": null, "error": {"detail": "detail boom"}}""",
        "detail boom")]
    [InlineData(
        """{"success": false, "result": null, "errors": [{"message": "first"}, {"message": "second"}]}""",
        "first; second")]
    [InlineData(
        """{"success": false, "result": null, "messages": ["msg one", "msg two"]}""",
        "msg one; msg two")]
    [InlineData(
        """{"success": false, "result": null, "errors": [], "messages": []}""",
        "upstream request failed")]
    public void OutboundTransformResponse_EnvelopeErrorShapes(string body, string expected)
    {
        var ex = Assert.Throws<ResponseError>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(JsonResponse(body)));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(expected, ex.Detail.Message);
    }

    [Theory]
    [InlineData(
        """{"success": null, "result": {"model": "clef-flash", "answers": {"approve": {"type": "noul", "noul": 0.9328}}}}""",
        "systemone response envelope has an invalid success flag")]
    [InlineData(
        """{"success": "yes", "result": {"model": "clef-flash"}}""",
        "systemone response envelope has an invalid success flag")]
    [InlineData(
        """{"success": true, "result": {"model": "", "answers": null}}""",
        "systemone response envelope result is empty")]
    [InlineData(
        """{"success": true, "result": {"unexpected": true}}""",
        "systemone response envelope result is empty")]
    [InlineData(
        """{"success": true, "result": null}""",
        "systemone response envelope is missing result")]
    public void OutboundTransformResponse_EnvelopeMalformed(string body, string expected)
    {
        var ex = Assert.Throws<ResponseError>(() =>
            TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
                .TransformResponse(JsonResponse(body)));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(expected, ex.Detail.Message);
    }

    // --------------------------------------------------- outbound TransformError

    [Fact]
    public void OutboundTransformError_ParsesEnvelopeAndPreservesCause()
    {
        // Port of TestOutboundTransformer_TransformError_PreservesCause.
        var original = new HttpClientError(
            "POST",
            string.Empty,
            429,
            "429 Too Many Requests",
            Encoding.UTF8.GetBytes(
                """{"error":{"message":"RPM limit exceeded","type":"rate_limit_error","code":"rate_limit"}}"""));

        var respErr = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformError(original);

        Assert.Equal(429, respErr.StatusCode);
        Assert.Equal("RPM limit exceeded", respErr.Detail.Message);
        Assert.Equal("rate_limit_error", respErr.Detail.Type);
        Assert.Equal("rate_limit", respErr.Detail.Code);

        // errors.As semantics: the original *httpclient.Error is still reachable.
        Assert.Same(original, GoErrors.As<HttpClientError>(respErr));
    }

    [Fact]
    public void OutboundTransformError_NilIsInternalServerError()
    {
        var respErr = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformError(null);

        Assert.Equal(500, respErr.StatusCode);
        Assert.Equal("Internal Server Error", respErr.Detail.Message);
        Assert.Equal("api_error", respErr.Detail.Type);
        Assert.Null(respErr.Detail.Code);
    }

    [Fact]
    public void OutboundTransformError_NonJsonBodyUsesRawBody()
    {
        var respErr = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformError(new HttpClientError(
                "POST",
                string.Empty,
                502,
                "502 Bad Gateway",
                Encoding.UTF8.GetBytes("upstream exploded")));

        Assert.Equal(502, respErr.StatusCode);
        Assert.Equal("upstream exploded", respErr.Detail.Message);
        Assert.Equal("api_error", respErr.Detail.Type);
    }

    [Fact]
    public void OutboundTransformError_EnvelopeWithoutMessageFallsBackToRawBody()
    {
        const string body = """{"error":{"type":"some_error"}}""";
        var respErr = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformError(new HttpClientError(
                "POST",
                string.Empty,
                500,
                "500 Internal Server Error",
                Encoding.UTF8.GetBytes(body)));

        Assert.Equal(body, respErr.Detail.Message);
        Assert.Equal("api_error", respErr.Detail.Type);
    }

    [Fact]
    public void OutboundTransformError_EnvelopeWithoutCodeOmitsCode()
    {
        var respErr = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k")
            .TransformError(new HttpClientError(
                "POST",
                string.Empty,
                401,
                "401 Unauthorized",
                Encoding.UTF8.GetBytes(
                    """{"error":{"message":"Invalid API key","type":"authentication_error"}}""")));

        Assert.Null(respErr.Detail.Code);
        Assert.Equal(
            """{"error":{"message":"Invalid API key","type":"authentication_error"}}""",
            Encoding.UTF8.GetString(GoJson.Marshal(respErr)));
    }

    [Fact]
    public void OutboundTransformStreamAndAggregateAreUnsupported()
    {
        var transformer = TypeSafeOutboundTransformer.Create("https://api.typesafe.ai/v1", "k");

        Assert.Equal(
            "systemone does not support streaming",
            Assert.Throws<InvalidOperationException>(() =>
                transformer.TransformStream(null, null!)).Message);

        Assert.Equal(
            "systemone does not support streaming",
            Assert.Throws<InvalidOperationException>(() =>
                transformer.AggregateStreamChunks(null, [], out _)).Message);
    }

    // -------------------------------------------- empty-response content probes

    [Fact]
    public void HasResponseContent_SystemOneDependsOnAnswers()
    {
        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            SystemOne = new SystemOneResponse(),
        }));
        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            SystemOne = new SystemOneResponse
            {
                Answers = new Dictionary<string, SystemOneAnswer>(),
            },
        }));

        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            SystemOne = new SystemOneResponse
            {
                Answers = new Dictionary<string, SystemOneAnswer>
                {
                    ["q"] = new(),
                },
            },
        }));
    }

    [Fact]
    public void HasResponseContent_CompactAndCompletionBranches()
    {
        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            Compact = new CompactResponse(),
        }));
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Compact = new CompactResponse { Output = [new Message { Role = "assistant" }] },
        }));

        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            Completion = new CompletionResponse(),
        }));
        Assert.False(ResponseContent.HasResponseContent(new LlmResponse
        {
            Completion = new CompletionResponse
            {
                Choices = [new CompletionChoice { Text = string.Empty }],
            },
        }));
        Assert.True(ResponseContent.HasResponseContent(new LlmResponse
        {
            Completion = new CompletionResponse
            {
                Choices = [new CompletionChoice { Text = "hi" }],
            },
        }));
    }

    // ------------------------------------------------------------------ helpers

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpRequestData JsonRequest(string body) => new()
    {
        Body = Encoding.UTF8.GetBytes(body),
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
    };

    private static HttpResponseData JsonResponse(string json) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(json),
    };

    private sealed class EmptyStream : IStream<LlmResponse>
    {
        public LlmResponse? Current => null;

        public Exception? Err => null;

        public bool Next() => false;

        public void Dispose()
        {
        }
    }
}
