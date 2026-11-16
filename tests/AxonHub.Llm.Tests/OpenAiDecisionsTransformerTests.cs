using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the decisions slice: llm/transformer/openai/decisions/
/// {inbound,outbound,decisions_test,transform_error_test,offline_test}.go.
/// The Go decoder's exact error strings and ordering are part of the contract.
/// </summary>
public sealed class OpenAiDecisionsTransformerTests
{
    private const string GoRequestStructType =
        """struct { Model string "json:\"model\""; Input json.RawMessage "json:\"input\""; Questions []json.RawMessage "json:\"questions\""; Stream *bool "json:\"stream\"" }""";

    private const string GoResponseStructType =
        """struct { Model string "json:\"model\""; Answers []json.RawMessage "json:\"answers\""; Usage *struct { InputTokens int64 "json:\"input_tokens\"" } "json:\"usage\"" }""";

    private const string GoUsageStructType =
        """struct { InputTokens int64 "json:\"input_tokens\"" }""";

    private const string ValidBody =
        """{"model":"gpt-6-luna","input":"text","questions":[{}]}""";

    // ------------------------------------------------ reference round-trip test

    // Port of TestDecisionsClientShapedRoundTripPreservesRawFieldsAndUsage.
    [Fact]
    public void ClientShapedRoundTripPreservesRawFieldsAndUsage()
    {
        var rawRequest = File.ReadAllBytes(TestData("client_request.json"));
        var rawResponse = File.ReadAllBytes(TestData("provider_response.json"));

        var request = Inbound().TransformRequest(JsonRequest(rawRequest));

        Assert.Equal(RequestTypes.Decisions, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIDecisions, request.ApiFormat);
        Assert.Equal("gpt-6-luna", request.Decisions!.Model);
        Assert.Equal(rawRequest, request.Decisions.Body);
        Assert.Single(request.Decisions.Questions!);

        var question = request.Decisions.Questions![0];
        var choices = question.GetProperty("choices").EnumerateArray().ToArray();
        Assert.Equal("billing", choices[0].GetProperty("value").GetString());
        Assert.True(choices[1].GetProperty("value").GetBoolean());

        var outbound = Outbound();
        var providerRequest = outbound.TransformRequest(request);
        AssertJsonEqual(
            Encoding.UTF8.GetString(rawRequest),
            Encoding.UTF8.GetString(providerRequest.Body!));

        var providerResponse = outbound.TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = rawResponse,
            Request = providerRequest,
        });

        Assert.Equal(120L, providerResponse.Usage!.PromptTokens);
        Assert.Equal(0L, providerResponse.Usage.CompletionTokens);
        Assert.Equal(0L, providerResponse.Usage.TotalTokens);
        Assert.Null(providerResponse.Usage.PromptTokensDetails);
        Assert.Null(providerResponse.Usage.CompletionTokensDetails);
        Assert.Equal(2, providerResponse.Decisions!.Answers!.Count);
        Assert.Equal(
            "refusal",
            providerResponse.Decisions.Answers[1].GetProperty("type").GetString());
        Assert.Equal(120L, providerResponse.Decisions.Usage!.InputTokens);

        var clientResponse = Inbound().TransformResponse(providerResponse);
        AssertJsonEqual(
            Encoding.UTF8.GetString(rawResponse),
            Encoding.UTF8.GetString(clientResponse.Body!));
    }

    // Port of TestDecisionsInvalidRequests.
    [Theory]
    [InlineData("""{"model":""")] // malformed json
    [InlineData("""{"input":"text","questions":[{}]}""")] // missing model
    [InlineData("""{"model":"gpt-6-luna","input":"text"}""")] // missing questions
    [InlineData("""{"model":"gpt-6-luna","input":null,"questions":[{}]}""")] // null input
    [InlineData("""{"model":"gpt-6-luna","input":"","questions":[{}]}""")] // empty string input
    [InlineData("""{"model":"gpt-6-luna","input":[],"questions":[{}]}""")] // empty array input
    [InlineData("""{"model":"gpt-6-luna","input":{},"questions":[{}]}""")] // empty object input
    [InlineData("""{"model":"gpt-6-luna","input":42,"questions":[{}]}""")] // numeric input
    public void InvalidRequestsAreInvalidRequest(string body)
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(body)));

        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
    }

    // Port of TestDecisionsAcceptsSupportedInputForms.
    [Theory]
    [InlineData("\"text\"")]
    [InlineData("\"   \"")]
    [InlineData("""[{"type":"message","role":"user","content":[{"type":"input_text","text":"text"}]}]""")]
    [InlineData("""[{"type":"message","role":"user","content":[{"type":"input_image","image_url":"data:image/png;base64,AAAA"}]}]""")]
    [InlineData("""[{"type":"input_text","text":"text"}]""")]
    [InlineData("""[{"type":"input_image","image_url":"https://example.com/image.png"}]""")]
    public void SupportedInputFormsAreAccepted(string input)
    {
        var body = """{"model":"gpt-6-luna","input":""" + input + ""","questions":[{}]}""";

        var request = Inbound().TransformRequest(RawRequest(body));

        Assert.NotNull(request.Decisions);
        Assert.NotNull(request.Decisions!.Input);
    }

    // Port of TestDecisionsStreamIsRejected.
    [Fact]
    public void StreamIsRejected()
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(
            """{"model":"gpt-6-luna","input":"text","questions":[{}],"stream":true}""")));

        Assert.Equal(
            "invalid request: streaming is not supported for decisions requests",
            ex.Message);
    }

    [Theory]
    [InlineData("""{"model":"gpt-6-luna","input":"text","questions":[{}],"stream":false}""")]
    [InlineData("""{"model":"gpt-6-luna","input":"text","questions":[{}],"stream":null}""")]
    public void StreamFalseAndNullAreAccepted(string body)
    {
        _ = Inbound().TransformRequest(RawRequest(body));
    }

    // Port of TestDecisionsRejectsTrailingJSONData (testdata fixture).
    [Fact]
    public void RejectsTrailingJsonData()
    {
        var rawRequest = File.ReadAllBytes(TestData("trailing_data.json"));

        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(JsonRequest(rawRequest)));

        Assert.Equal(
            "invalid request: invalid trailing data after decisions request: "
            + "invalid character 'a' in literal true (expecting 'u')",
            ex.Message);
    }

    // Port of TestDecisionsUsageCanBeNull.
    [Fact]
    public void UsageCanBeNull()
    {
        var response = Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"model":"gpt-6-luna","answers":[],"usage":null}"""),
        });

        Assert.Null(response.Usage);
        Assert.Null(response.Decisions!.Usage);
    }

    // Port of TestDecisionsEmptySuccessfulResponsesReachEmptyResponseDetection.
    [Theory]
    [InlineData(204)]
    [InlineData(200)]
    public void EmptySuccessfulResponsesCarryAnEmptyDecisionsResponse(int status)
    {
        var response = Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = status,
            Body = null,
        });

        Assert.NotNull(response.Decisions);
        Assert.True(response.Decisions!.Answers is null or { Count: 0 });
        Assert.Equal(RequestTypes.Decisions, response.RequestType);
        Assert.Equal(ApiFormats.OpenAIDecisions, response.ApiFormat);
    }

    // Port of TestDecisionsRedirectIsAnUpstreamStatusError.
    [Fact]
    public void RedirectIsAnUpstreamStatusError()
    {
        var rawBody = Encoding.UTF8.GetBytes(
            """{"error":{"message":"redirect body","type":"redirect_error"}}""");

        var ex = Assert.Throws<ResponseError>(() => Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 307,
            Body = rawBody,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/problem+json"],
            },
        }));

        Assert.Equal(307, ex.StatusCode);
        Assert.Equal("redirect body", ex.Detail.Message);

        // The reference keeps the transport error reachable through Unwrap.
        var httpError = GoErrors.As<HttpClientError>(ex);
        Assert.NotNull(httpError);
        Assert.Equal(rawBody, httpError!.Body);
        Assert.Equal(
            "application/problem+json",
            HttpClientUtils.HeaderGet(httpError.Headers!, "Content-Type"));
        Assert.Same(httpError, ex.CauseException);
    }

    // Port of TestDecisionsErrorFixturePreservesClassification.
    [Fact]
    public void ErrorFixturePreservesClassification()
    {
        var rawError = File.ReadAllBytes(TestData("provider_error.json"));

        var ex = Assert.Throws<ResponseError>(() => Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 429,
            Body = rawError,
        }));

        Assert.Equal(429, ex.StatusCode);
        Assert.Equal("decision rate limit", ex.Detail.Message);
        Assert.Equal("rate_limit_error", ex.Detail.Type);
        Assert.Equal("rate_limit", ex.Detail.Code);
    }

    // Port of TestDecisionsOutboundURLRules.
    [Theory]
    [InlineData("https://api.openai.com", null, "https://api.openai.com/v1/decisions")]
    [InlineData("https://api.openai.com/v1", null, "https://api.openai.com/v1/decisions")]
    [InlineData("https://api.openai.com/", null, "https://api.openai.com/v1/decisions")]
    [InlineData("https://relay.example/api#", null, "https://relay.example/api/decisions")]
    [InlineData("https://relay.example/api/v2", "/custom/decisions", "https://relay.example/api/v2/custom/decisions")]
    [InlineData("https://relay.example/decisions##", null, "https://relay.example/decisions")]
    public void OutboundUrlRules(string baseUrl, string? path, string expected)
    {
        var outbound = new DecisionsOutboundTransformer(new DecisionsOutboundConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = path ?? string.Empty,
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-6-luna",
            Decisions = new DecisionsRequest { Body = Encoding.UTF8.GetBytes("""{"model":"gpt-6-luna"}""") },
        });

        Assert.Equal(expected, request.Url);
    }

    // ------------------------------------------------ wire decode / validation

    [Fact]
    public void DecoderReportsGoStructTypeForRootTypeErrors()
    {
        AssertDecodeError(
            "[]",
            "json: cannot unmarshal array into Go value of type " + GoRequestStructType);
        AssertDecodeError(
            "\"s\"",
            "json: cannot unmarshal string into Go value of type " + GoRequestStructType);
        AssertDecodeError(
            "123",
            "json: cannot unmarshal number into Go value of type " + GoRequestStructType);
        AssertDecodeError(
            "true",
            "json: cannot unmarshal bool into Go value of type " + GoRequestStructType);
        AssertDecodeError(
            "1.5",
            "json: cannot unmarshal number into Go value of type " + GoRequestStructType);
    }

    [Fact]
    public void RootNullDecodesToTheZeroRequest()
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest("null")));

        // Decoding succeeded, so the failure is the model check.
        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void DecoderReportsFieldTypeErrors()
    {
        AssertDecodeError(
            """{"model":1}""",
            """json: cannot unmarshal number into Go struct field .model of type string""");
        AssertDecodeError(
            """{"questions":{}}""",
            """json: cannot unmarshal object into Go struct field .questions of type []json.RawMessage""");
        AssertDecodeError(
            """{"stream":1}""",
            """json: cannot unmarshal number into Go struct field .stream of type bool""");
        AssertDecodeError(
            """{"stream":"x"}""",
            """json: cannot unmarshal string into Go struct field .stream of type bool""");
    }

    [Fact]
    public void DecoderReportsTruncatedAndEmptyInput()
    {
        AssertDecodeError("""{"model":""", "unexpected EOF");
        AssertDecodeError("\"abc", "unexpected EOF");
        AssertDecodeError("tru", "unexpected EOF");
        AssertDecodeError("x", "invalid character 'x' looking for beginning of value");
        AssertDecodeError("   ", "EOF");
    }

    [Fact]
    public void SyntaxErrorInTheFirstValueWinsOverTypeErrors()
    {
        AssertDecodeError(
            """{"model":1,"bad":}""",
            """invalid character '}' looking for beginning of value""");
    }

    [Theory]
    [InlineData(" 123", "trailing data after decisions request")]
    [InlineData(" 12x", "trailing data after decisions request")]
    [InlineData(" true!", "trailing data after decisions request")]
    [InlineData(" nullx", "trailing data after decisions request")]
    [InlineData(" x", "invalid trailing data after decisions request: invalid character 'x' looking for beginning of value")]
    [InlineData(" \"abc", "invalid trailing data after decisions request: unexpected EOF")]
    [InlineData(" {", "invalid trailing data after decisions request: unexpected EOF")]
    public void TrailingDataUsesTheSecondDecodeRules(string tail, string expectedDetail)
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(ValidBody + tail)));

        Assert.Equal("invalid request: " + expectedDetail, ex.Message);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\n\t\r ")]
    public void TrailingWhitespaceIsAccepted(string tail)
    {
        var request = Inbound().TransformRequest(RawRequest(ValidBody + tail));

        Assert.NotNull(request.Decisions);
    }

    [Fact]
    public void StructFieldsMatchCaseInsensitivelyAndDuplicatesTakeTheLastValue()
    {
        var request = Inbound().TransformRequest(RawRequest(
            """{"MODEL":"first","model":"second","input":"text","questions":[{}]}"""));
        Assert.Equal("second", request.Model);

        // null into a string is a no-op, keeping the previous value.
        var kept = Inbound().TransformRequest(RawRequest(
            """{"model":"kept","model":null,"input":"text","questions":[{}]}"""));
        Assert.Equal("kept", kept.Model);

        // null into a slice resets it.
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(
            """{"model":"m","input":"text","questions":[{}],"questions":null}""")));
        Assert.Equal("invalid request: questions are required", ex.Message);
    }

    [Fact]
    public void DecodeTypeErrorIsRaisedBeforeTrailingData()
    {
        AssertDecodeError(
            """{"model":1} garbage""",
            """json: cannot unmarshal number into Go struct field .model of type string""");
    }

    [Theory]
    [InlineData("""{"model":"m","input":"text","questions":[{}]}""", null)]
    [InlineData("""{"input":"text","questions":[{}]}""", "model is required")]
    [InlineData("""{"model":"   ","input":"text","questions":[{}]}""", "model is required")]
    [InlineData("""{"model":"m","questions":[{}]}""", "input is required")]
    [InlineData("""{"model":"m","input":"","questions":[{}]}""", "input string cannot be empty")]
    [InlineData("""{"model":"m","input":[],"questions":[{}]}""", "input array cannot be empty")]
    [InlineData("""{"model":"m","input":{},"questions":[{}]}""", "input must be a string or supported message structure")]
    [InlineData("""{"model":"m","input":true,"questions":[{}]}""", "input must be a string or supported message structure")]
    [InlineData("""{"model":"m","input":[null],"questions":[{}]}""", "input[0]: item must be an object")]
    [InlineData("""{"model":"m","input":[[]],"questions":[{}]}""", "input[0]: item must be an object")]
    [InlineData("""{"model":"m","input":[1],"questions":[{}]}""", "input[0]: item must be an object")]
    [InlineData("""{"model":"m","input":[{}],"questions":[{}]}""", "input[0]: type is required")]
    [InlineData("""{"model":"m","input":[{"type":1}],"questions":[{}]}""", "input[0]: type is required")]
    [InlineData("""{"model":"m","input":[{"type":null}],"questions":[{}]}""", "input[0]: unsupported item type \"\"")]
    [InlineData("""{"model":"m","input":[{"type":"bogus"}],"questions":[{}]}""", "input[0]: unsupported item type \"bogus\"")]
    [InlineData("""{"model":"m","input":[{"type":"message"}],"questions":[{}]}""", "input[0]: message content is required")]
    [InlineData("""{"model":"m","input":[{"type":"message","content":null}],"questions":[{}]}""", "input[0]: message content is required")]
    [InlineData("""{"model":"m","input":[{"type":"message","content":""}],"questions":[{}]}""", "input[0]: content must be a non-empty string")]
    [InlineData("""{"model":"m","input":[{"type":"message","content":42}],"questions":[{}]}""", "input[0]: message content must be a string or content array")]
    [InlineData("""{"model":"m","input":[{"type":"message","content":[]}],"questions":[{}]}""", "input[0]: message content array cannot be empty")]
    [InlineData("""{"model":"m","input":[{"type":"message","content":[1]}],"questions":[{}]}""", "input[0]: content[0]: item must be an object")]
    [InlineData("""{"model":"m","input":[{"type":"message","content":[{"type":"input_text"}]}],"questions":[{}]}""", "input[0]: content[0]: text must be a non-empty string")]
    [InlineData("""{"model":"m","input":[{"type":"input_text"}],"questions":[{}]}""", "input[0]: text must be a non-empty string")]
    [InlineData("""{"model":"m","input":[{"type":"input_text","text":5}],"questions":[{}]}""", "input[0]: text must be a non-empty string")]
    [InlineData("""{"model":"m","input":[{"type":"input_image"}],"questions":[{}]}""", "input[0]: image_url must be a non-empty string")]
    [InlineData("""{"model":"m","input":"text"}""", "questions are required")]
    [InlineData("""{"model":"m","input":"text","questions":null}""", "questions are required")]
    public void ValidationMessagesMatchTheReference(string body, string? expectedDetail)
    {
        if (expectedDetail is null)
        {
            _ = Inbound().TransformRequest(RawRequest(body));
            return;
        }

        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(body)));
        Assert.Equal("invalid request: " + expectedDetail, ex.Message);
    }

    [Fact]
    public void ItemKeysAreMatchedCaseSensitively()
    {
        // A Go map lookup is exact: "Type" does not satisfy the "type" key.
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(
            """{"model":"m","input":[{"Type":"input_text","text":"x"}],"questions":[{}]}""")));

        Assert.Equal("invalid request: input[0]: type is required", ex.Message);
    }

    // ------------------------------------------------- outbound TransformRequest

    [Fact]
    public void OutboundRequiresTheRawDecisionsRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Outbound().TransformRequest(new LlmRequest
        {
            Model = "gpt-6-luna",
        }));

        Assert.Equal("decisions request is nil", ex.Message);
    }

    [Fact]
    public void OutboundRequiresAModel()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Outbound().TransformRequest(new LlmRequest
        {
            Model = "   ",
            Decisions = new DecisionsRequest { Body = Encoding.UTF8.GetBytes(ValidBody) },
        }));

        Assert.Equal("model is required", ex.Message);
    }

    [Fact]
    public void OutboundPatchesAndAppendsTheRoutedModel()
    {
        var outbound = Outbound();

        var replaced = outbound.TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            Decisions = new DecisionsRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"model":"client-model","input":"x"}"""),
            },
        });
        AssertJsonEqual(
            """{"model":"routed-model","input":"x"}""",
            Encoding.UTF8.GetString(replaced.Body!));

        var appended = outbound.TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            Decisions = new DecisionsRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"input":"x"}"""),
            },
        });
        AssertJsonEqual(
            """{"input":"x","model":"routed-model"}""",
            Encoding.UTF8.GetString(appended.Body!));
    }

    [Fact]
    public void OutboundSplicesTheModelPastDeeplyNestedFields()
    {
        // sjson skips over earlier values without a nesting limit; the port's
        // Utf8JsonReader must accept what Go accepts (up to 10000 levels).
        var nested = new string('[', 65) + new string(']', 65);
        var body = "{\"payload\":" + nested + ",\"model\":\"client-model\"}";

        var request = Outbound().TransformRequest(new LlmRequest
        {
            Model = "routed-model",
            Decisions = new DecisionsRequest { Body = Encoding.UTF8.GetBytes(body) },
        });

        Assert.Equal(
            "{\"payload\":" + nested + ",\"model\":\"routed-model\"}",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public void OutboundModelPatchUsesGoMarshalEscaping()
    {
        // sjson.SetBytes writes the model without HTML escaping; the port
        // splices Go json.Marshal output, so '<', '>' and '&' become the
        // lowercase \u003c, \u003e and \u0026 sequences (C078). The parsed
        // JSON value is identical.
        var request = Outbound().TransformRequest(new LlmRequest
        {
            Model = "a<b&c",
            Decisions = new DecisionsRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"model":"client"}"""),
            },
        });

        Assert.Equal(
            """{"model":"a\u003cb\u0026c"}""",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public void OutboundRequestShapeMatchesTheReference()
    {
        var outbound = DecisionsOutboundTransformer.Create(
            "https://api.openai.com",
            new StaticApiKeyProvider("test-key"),
            2);

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "gpt-6-luna",
            Decisions = new DecisionsRequest { Body = Encoding.UTF8.GetBytes(ValidBody) },
        });

        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(RequestTypes.Decisions, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIDecisions, request.ApiFormat);
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("test-key", request.Auth.ApiKey);
        Assert.Equal("test-key", outbound.SelectedApiKey);
        Assert.Equal(2, outbound.SelectedApiKeyIndex);
        Assert.Equal(ApiFormats.OpenAIDecisions, outbound.ApiFormat);
    }

    [Fact]
    public void ConfigValidationMatchesTheReference()
    {
        var nil = Assert.Throws<InvalidOperationException>(() => new DecisionsOutboundTransformer(null));
        Assert.Equal("config cannot be nil", nil.Message);

        var noKey = Assert.Throws<InvalidOperationException>(() =>
            new DecisionsOutboundTransformer(new DecisionsOutboundConfig { BaseUrl = "https://x" }));
        Assert.Equal("API key provider is required", noKey.Message);

        var noUrl = Assert.Throws<InvalidOperationException>(() =>
            new DecisionsOutboundTransformer(new DecisionsOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("k"),
            }));
        Assert.Equal("base URL is required", noUrl.Message);
    }

    // ------------------------------------------------ outbound TransformResponse

    [Fact]
    public void OutboundInvalidResponseBodiesMatchTheReference()
    {
        var invalid = Assert.Throws<TransformerException>(() => Outbound().TransformResponse(
            new HttpResponseData
            {
                StatusCode = 200,
                Body = Encoding.UTF8.GetBytes("{not-json"),
            }));
        Assert.Equal("invalid response: invalid decisions response body", invalid.Message);

        var nilResponse = Assert.Throws<InvalidOperationException>(() => Outbound().TransformResponse(null));
        Assert.Equal("http response is nil", nilResponse.Message);
    }

    [Fact]
    public void ResponseStructTypeErrorsMatchTheReference()
    {
        AssertResponseDecodeError(
            "[]",
            "json: cannot unmarshal array into Go value of type " + GoResponseStructType);
        AssertResponseDecodeError(
            """{"model":5}""",
            "json: cannot unmarshal number into Go struct field .model of type string");
        AssertResponseDecodeError(
            """{"answers":{}}""",
            "json: cannot unmarshal object into Go struct field .answers of type []json.RawMessage");
        AssertResponseDecodeError(
            """{"usage":[]}""",
            "json: cannot unmarshal array into Go struct field .usage of type " + GoUsageStructType);
        AssertResponseDecodeError(
            """{"usage":{"input_tokens":"x"}}""",
            "json: cannot unmarshal string into Go struct field .usage.input_tokens of type int64");
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("1.0")]
    [InlineData("99999999999999999999")]
    public void UsageTokenLiteralErrorsUseTheRawLiteral(string literal)
    {
        AssertResponseDecodeError(
            """{"usage":{"input_tokens":""" + literal + "}}",
            "json: cannot unmarshal number " + literal
            + " into Go struct field .usage.input_tokens of type int64");
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("-2", -2L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void UsageTokenIntegersAreAccepted(string literal, long expected)
    {
        var response = Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"usage":{"input_tokens":""" + literal + "}}"),
        });

        Assert.Equal(expected, response.Usage!.PromptTokens);
        Assert.Equal(expected, response.Decisions!.Usage!.InputTokens);
    }

    [Fact]
    public void ResponseFieldsMatchCaseInsensitivelyAndDuplicatesMerge()
    {
        var response = Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"MODEL":"m","answers":[1],"answers":null,"usage":{"InputTokens":1},"Usage":{"input_tokens":2}}"""),
        });

        Assert.Equal("m", response.Model);
        Assert.True(response.Decisions!.Answers is null or { Count: 0 });
        Assert.Equal(2L, response.Usage!.PromptTokens);
    }

    // ----------------------------------------------- inbound TransformResponse

    [Fact]
    public void InboundResponseIsTheRawProviderBody()
    {
        var body = Encoding.UTF8.GetBytes("""{"model":"m","answers":[{"x":1}]}""");

        var httpResponse = Inbound().TransformResponse(new LlmResponse
        {
            Decisions = new DecisionsResponse { Body = body },
        });

        Assert.Equal(200, httpResponse.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpResponse.Headers, "Content-Type"));
        Assert.Equal(body, httpResponse.Body);
        Assert.NotSame(body, httpResponse.Body);
    }

    [Fact]
    public void InboundEmptyResponseBodyThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Inbound().TransformResponse(new LlmResponse { Decisions = new DecisionsResponse() }));

        Assert.Equal("decisions response is empty", ex.Message);
    }

    // ------------------------------------------------------- inbound TransformError

    // Port of TestInboundTransformer_TransformErrorPreservesResponseError.
    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    public void TransformErrorPreservesResponseError(int status)
    {
        var detail = new ErrorDetail { Code = "provider_error", Message = "provider failed", Type = "api_error" };

        var result = Inbound().TransformError(new ResponseError { StatusCode = status, Detail = detail });

        Assert.Equal(status, result.StatusCode);
        using var body = JsonDocument.Parse(result.Body!);
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("provider_error", error.GetProperty("code").GetString());
        Assert.Equal("provider failed", error.GetProperty("message").GetString());
        Assert.Equal("api_error", error.GetProperty("type").GetString());
        Assert.False(error.TryGetProperty("param", out _));
        Assert.False(error.TryGetProperty("request_id", out _));
    }

    // Port of TestInboundTransformer_TransformErrorPassesThroughHTTPError.
    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    public void TransformErrorPassesThroughHttpError(int status)
    {
        var rawBody = Encoding.UTF8.GetBytes("""{"error":{"message":"provider failed"}}""");
        var httpError = new HttpClientError("POST", "https://upstream.example/v1/decisions", status, "status", rawBody)
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/problem+json"],
            },
        };

        var result = Inbound().TransformError(httpError);

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(rawBody, result.Body);
        Assert.Equal(
            "application/problem+json",
            HttpClientUtils.HeaderGet(result.Headers, "Content-Type"));
    }

    // Port of TestInboundTransformer_TransformErrorMapsInvalidRequest.
    [Fact]
    public void TransformErrorMapsInvalidRequest()
    {
        var result = Inbound().TransformError(TransformerErrors.InvalidRequest("malformed input"));

        Assert.Equal(400, result.StatusCode);
        using var body = JsonDocument.Parse(result.Body!);
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("invalid request: malformed input", error.GetProperty("message").GetString());
    }

    // Port of TestInboundTransformer_TransformErrorMapsUnknownErrorToInternalError.
    [Fact]
    public void TransformErrorMapsUnknownErrorToInternalError()
    {
        var result = Inbound().TransformError(new InvalidOperationException("database unavailable"));

        Assert.Equal(500, result.StatusCode);
        using var body = JsonDocument.Parse(result.Body!);
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("database unavailable", error.GetProperty("message").GetString());
        Assert.Equal("internal_server_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public void TransformErrorNilUsesTheLiteralBody()
    {
        var result = Inbound().TransformError(null);

        Assert.Equal(500, result.StatusCode);
        Assert.Equal(
            """{"error":{"message":"Internal server error","type":"api_error"}}""",
            Encoding.UTF8.GetString(result.Body!));
    }

    // ------------------------------------------------------ outbound TransformError

    [Fact]
    public void OutboundTransformErrorNilMatchesTheReference()
    {
        var error = Outbound().TransformError(null);

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Internal Server Error", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
        Assert.Null(error.CauseException);
    }

    [Fact]
    public void OutboundTransformErrorDecodesTheProviderEnvelopeLeniently()
    {
        var httpError = HttpError(
            429,
            """{"error":{"message":"boom","type":"provider","code":"quota"}}""");

        var error = Outbound().TransformError(httpError);

        Assert.Equal(429, error.StatusCode);
        Assert.Equal("boom", error.Detail.Message);
        Assert.Equal("provider", error.Detail.Type);
        Assert.Equal("quota", error.Detail.Code);
        Assert.Same(httpError, error.CauseException);
        Assert.Same(httpError, GoErrors.As<HttpClientError>(error));
    }

    [Theory]
    [InlineData("""{"error":{"message":"","type":""}}""", """{"error":{"message":"","type":""}}""")]
    [InlineData("  <html>oops</html>  ", "<html>oops</html>")]
    [InlineData("[]", "[]")]
    [InlineData("null", "null")]
    public void OutboundTransformErrorFallsBackToTheTrimmedBody(string body, string expectedMessage)
    {
        var error = Outbound().TransformError(HttpError(500, body));

        Assert.Equal(expectedMessage, error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void OutboundTransformErrorKeepsValuesDecodedBeforeAFieldError()
    {
        var error = Outbound().TransformError(
            HttpError(500, """{"error":{"code":123,"message":"kept"}}"""));

        Assert.Equal("kept", error.Detail.Message);
        Assert.Null(error.Detail.Code);
        Assert.Equal("api_error", error.Detail.Type);
    }

    // ------------------------------------------------------- streaming / handlers

    [Fact]
    public void StreamingIsUnsupported()
    {
        var inbound = Inbound();
        var stream = Assert.Throws<TransformerException>(() => inbound.TransformStream(new SliceStream<LlmResponse>([])));
        Assert.Equal("invalid request: decisions does not support streaming", stream.Message);
        var aggregate = Assert.Throws<InvalidOperationException>(() => inbound.AggregateStreamChunks([], out _));
        Assert.Equal("decisions does not support streaming", aggregate.Message);

        var outbound = Outbound();
        var outboundStream = Assert.Throws<TransformerException>(() =>
            outbound.TransformStream(null, new SliceStream<StreamEvent>([])));
        Assert.Equal("invalid request: decisions does not support streaming", outboundStream.Message);
        var outboundAggregate = Assert.Throws<InvalidOperationException>(() =>
            outbound.AggregateStreamChunks(null, [], out _));
        Assert.Equal("decisions does not support streaming", outboundAggregate.Message);
    }

    // ------------------------------------------------------------- helpers

    private static DecisionsInboundTransformer Inbound() => new();

    private static DecisionsOutboundTransformer Outbound()
        => DecisionsOutboundTransformer.Create("https://api.openai.com/v1", "test-key");

    private static HttpClientError HttpError(int status, string body)
        => new("POST", "https://api.openai.com/v1/decisions", status, status + " status", Encoding.UTF8.GetBytes(body));

    private static HttpRequestData JsonRequest(byte[] body, string contentType = "application/json")
        => new()
        {
            Body = body,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
        };

    private static HttpRequestData RawRequest(string body)
        => new() { Body = Encoding.UTF8.GetBytes(body) };

    private static string TestData(string name)
        => Path.Combine(AppContext.BaseDirectory, "testdata", "decisions", name);

    private static void AssertDecodeError(string body, string expectedDetail)
    {
        var ex = Assert.Throws<TransformerException>(() => Inbound().TransformRequest(RawRequest(body)));
        Assert.Equal("invalid request: failed to decode decisions request: " + expectedDetail, ex.Message);
    }

    private static void AssertResponseDecodeError(string body, string expectedDetail)
    {
        var ex = Assert.Throws<TransformerException>(() => Outbound().TransformResponse(new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(body),
        }));
        Assert.Equal("invalid response: failed to decode decisions response: " + expectedDetail, ex.Message);
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "expected " + expected + " but got " + actual);
    }
}
