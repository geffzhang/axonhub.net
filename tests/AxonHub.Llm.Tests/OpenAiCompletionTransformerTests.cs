using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour lock for the legacy /v1/completions surface
/// (llm/transformer/openai/completion_inbound.go, completion_outbound.go,
/// completion_aggregator.go). The two <c>IncludesUsageCost</c> tests are
/// one-for-one ports of completion_inbound_cost_test.go; the rest lock the wire
/// shape, validation order and error envelope of the reference.
/// </summary>
public sealed class OpenAiCompletionTransformerTests
{
    // ------------------------------------------------ inbound TransformRequest

    [Fact]
    public void InboundTransformRequest_ValidRequest()
    {
        var req = new CompletionInboundTransformer().TransformRequest(
            JsonRequest(
                """{"model":"gpt-3.5-turbo-instruct","prompt":"Say hello","max_tokens":16,"temperature":0.5,"top_p":0.9,"n":2,"stream":true,"stream_options":{"include_usage":true},"logprobs":3,"echo":true,"stop":["END"],"presence_penalty":0.1,"frequency_penalty":0.2,"best_of":2,"logit_bias":{"50256":-100},"seed":42,"user":"u-1"}"""));

        Assert.Equal("gpt-3.5-turbo-instruct", req.Model);
        Assert.Empty(req.Messages!);
        Assert.Equal(RequestTypes.Completion, req.RequestType);
        Assert.Equal(ApiFormats.OpenAICompletion, req.ApiFormat);
        Assert.True(req.Stream);
        Assert.NotNull(req.StreamOptions);
        Assert.True(req.StreamOptions!.IncludeUsage);
        Assert.NotNull(req.RawRequest);

        // Common fields are copied to the unified parent as well.
        Assert.Equal(0.5, req.Temperature!.Value, 12);
        Assert.Equal(0.9, req.TopP!.Value, 12);
        Assert.Equal(0.1, req.PresencePenalty!.Value, 12);
        Assert.Equal(0.2, req.FrequencyPenalty!.Value, 12);
        Assert.Equal(16L, req.MaxTokens);
        Assert.Equal("u-1", req.User);
        Assert.Equal(42L, req.Seed);

        var completion = Assert.IsType<CompletionRequest>(req.Completion);
        Assert.Equal("Say hello", completion.Prompt);
        Assert.Equal(16L, completion.MaxTokens);
        Assert.Equal(2L, completion.N);
        Assert.Equal(3L, completion.Logprobs);
        Assert.True(completion.Echo);
        Assert.Equal(2L, completion.BestOf);
        Assert.Equal(-100L, completion.LogitBias!["50256"]);
        Assert.Equal("END", Assert.Single(completion.Stop!.Multiple!));
    }

    [Fact]
    public void InboundTransformRequest_StopStringBecomesSingle()
    {
        var req = new CompletionInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"m","prompt":"p","stop":"END"}"""));

        Assert.Equal("END", req.Completion!.Stop!.Single);
        Assert.Null(req.Completion.Stop.Multiple);
    }

    [Fact]
    public void InboundTransformRequest_NilRequest()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(null));

        Assert.Equal(TransformerErrorKind.InvalidRequest, ex.Kind);
        Assert.Equal("invalid request: http request is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_EmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest(string.Empty)));

        Assert.Equal("invalid request: request body is empty", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_UnsupportedContentType()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("test", "text/plain")));

        Assert.Equal("invalid request: unsupported content type: text/plain", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_ContentTypeIsCaseInsensitive()
    {
        var req = new CompletionInboundTransformer().TransformRequest(
            JsonRequest("""{"model":"m","prompt":"p"}""", "Application/JSON; charset=utf-8"));

        Assert.Equal("m", req.Model);
    }

    [Fact]
    public void InboundTransformRequest_MissingContentTypeDefaultsToJson()
    {
        var req = new CompletionInboundTransformer().TransformRequest(new HttpRequestData
        {
            Body = Encoding.UTF8.GetBytes("""{"model":"m","prompt":"p"}"""),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        });

        Assert.Equal("p", req.Completion!.Prompt);
    }

    [Fact]
    public void InboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("""{"prompt":"p"}""")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_MissingPrompt()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("""{"model":"m"}""")));

        Assert.Equal("invalid request: prompt is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_JsonNullDecodesToZeroRequest()
    {
        // Go: json.Unmarshal("null", &compReq) is a no-op, so validation then
        // reports the missing model.
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("null")));

        Assert.Equal("invalid request: model is required", ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_InvalidJsonUsesEncodingJsonWording()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("not valid json")));

        Assert.StartsWith("invalid request: failed to decode completion request: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformRequest_FieldTypeMismatchUsesGoStructFieldWording()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("""{"model":123}""")));

        Assert.Equal(
            "invalid request: failed to decode completion request: "
            + "json: cannot unmarshal number into Go struct field CompletionRequest.model of type string",
            ex.Message);
    }

    [Fact]
    public void InboundTransformRequest_RootArrayIsATypeError()
    {
        var ex = Assert.Throws<TransformerException>(() =>
            new CompletionInboundTransformer().TransformRequest(JsonRequest("[]")));

        Assert.Equal(
            "invalid request: failed to decode completion request: "
            + "json: cannot unmarshal array into Go value of type openai.CompletionRequest",
            ex.Message);
    }

    // ----------------------------------------------- inbound TransformResponse

    [Fact]
    public void InboundTransformResponse_IncludesUsageCost()
    {
        // Port of TestCompletionInboundTransformer_TransformResponse_IncludesUsageCost.
        var httpResp = new CompletionInboundTransformer().TransformResponse(new LlmResponse
        {
            Id = "cmpl-123",
            Object = "text_completion",
            Created = 1677652288,
            Model = "gpt-3.5-turbo-instruct",
            Completion = new CompletionResponse
            {
                Choices =
                [
                    new CompletionChoice { Text = "Hello", Index = 0, FinishReason = "stop" },
                ],
            },
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
                Cost = 0.000005,
            },
        });

        Assert.Equal(200, httpResp.StatusCode);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpResp.Headers, "Content-Type"));
        Assert.Equal("no-cache", HttpClientUtils.HeaderGet(httpResp.Headers, "Cache-Control"));

        var completionResp = LlmJson.Deserialize<OpenAiCompletionResponse>(httpResp.Body!)!;
        Assert.NotNull(completionResp.Usage.Cost);
        Assert.Equal(0.000005, completionResp.Usage.Cost!.Value, 12);
        Assert.Equal("cmpl-123", completionResp.Id);
        Assert.Equal("text_completion", completionResp.Object);
        Assert.Equal(1677652288L, completionResp.Created);
        Assert.Equal("gpt-3.5-turbo-instruct", completionResp.Model);
        Assert.Equal("Hello", Assert.Single(completionResp.Choices).Text);
    }

    [Fact]
    public void InboundTransformResponse_WithoutUsageStillEmitsZeroUsage()
    {
        var httpResp = new CompletionInboundTransformer().TransformResponse(new LlmResponse
        {
            Id = "cmpl-1",
            Object = "text_completion",
            Completion = new CompletionResponse
            {
                Choices = [new CompletionChoice { Text = "Hi", Index = 0 }],
            },
        });

        // Go's CompletionUsage is a value struct: the wire always carries it.
        Assert.Equal(
            """{"id":"cmpl-1","object":"text_completion","created":0,"model":"","choices":[{"text":"Hi","index":0}],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_EmptyChoicesMarshalAsArray()
    {
        var httpResp = new CompletionInboundTransformer().TransformResponse(new LlmResponse
        {
            Completion = new CompletionResponse(),
        });

        Assert.Contains("\"choices\":[]", Encoding.UTF8.GetString(httpResp.Body!), StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformResponse_ErrorEnvelopeKeepsUpstreamStatus()
    {
        var httpResp = new CompletionInboundTransformer().TransformResponse(new LlmResponse
        {
            Error = new ResponseError
            {
                StatusCode = 429,
                Detail = new ErrorDetail { Message = "rate limited", Type = "rate_limit_error" },
            },
        });

        Assert.Equal(429, httpResp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"rate limited","type":"rate_limit_error"}}""",
            Encoding.UTF8.GetString(httpResp.Body!));
    }

    [Fact]
    public void InboundTransformResponse_NilResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CompletionInboundTransformer().TransformResponse(null));

        Assert.Equal("completion response is nil", ex.Message);
    }

    [Fact]
    public void InboundTransformResponse_MissingCompletionData()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CompletionInboundTransformer().TransformResponse(new LlmResponse()));

        Assert.Equal("completion response missing completion data", ex.Message);
    }

    // ---------------------------------------------- inbound TransformStreamChunk

    [Fact]
    public void InboundTransformStreamChunk_DoneIsPassedThrough()
    {
        var eventData = new CompletionInboundTransformer().TransformStreamChunk(
            LlmResponse.Done());

        Assert.Equal("[DONE]", Encoding.UTF8.GetString(eventData!.Data));
        Assert.Equal(string.Empty, eventData.Type);
    }

    [Fact]
    public void InboundTransformStreamChunk_UsageOnlyIncludesCost()
    {
        // Port of TestCompletionInboundTransformer_TransformStreamChunk_UsageOnlyIncludesCost.
        var streamEvent = new CompletionInboundTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "cmpl-123",
            Object = "text_completion",
            Created = 1677652288,
            Model = "gpt-3.5-turbo-instruct",
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
                Cost = 0.000005,
            },
        });

        Assert.NotNull(streamEvent);

        var completionResp = LlmJson.Deserialize<OpenAiCompletionResponse>(streamEvent!.Data)!;
        Assert.NotNull(completionResp.Usage.Cost);
        Assert.Equal(0.000005, completionResp.Usage.Cost!.Value, 12);
        Assert.NotNull(completionResp.Choices);
        Assert.Empty(completionResp.Choices);
        Assert.Contains("\"choices\":[]", Encoding.UTF8.GetString(streamEvent.Data), StringComparison.Ordinal);
    }

    [Fact]
    public void InboundTransformStreamChunk_FiltersResponsesWithoutCompletionOrUsage()
    {
        // Go returns nil here and streams.NoNil drops the event.
        var streamEvent = new CompletionInboundTransformer().TransformStreamChunk(new LlmResponse
        {
            Id = "cmpl-1",
            Object = "text_completion",
        });

        Assert.Null(streamEvent);
    }

    [Fact]
    public void InboundTransformStream_AppliesNoNilFiltering()
    {
        var stream = new CompletionInboundTransformer().TransformStream(
            new SliceStream<LlmResponse>(new List<LlmResponse>
            {
                new() { Id = "cmpl-1", Object = "text_completion" },
                new()
                {
                    Id = "cmpl-1",
                    Object = "text_completion",
                    Completion = new CompletionResponse
                    {
                        Choices = [new CompletionChoice { Text = "Hi", Index = 0 }],
                    },
                },
                LlmResponse.Done(),
            }));

        var events = Drain(stream);
        Assert.Equal(2, events.Count);
        Assert.Contains("\"text\":\"Hi\"", Encoding.UTF8.GetString(events[0].Data), StringComparison.Ordinal);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(events[1].Data));
    }

    [Fact]
    public void InboundTransformStreamChunk_NilResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CompletionInboundTransformer().TransformStreamChunk(null));

        Assert.Equal("completion response is nil", ex.Message);
    }

    // ------------------------------------------------------ inbound TransformError

    [Fact]
    public void InboundTransformError_NilError()
    {
        var resp = new CompletionInboundTransformer().TransformError(null);

        Assert.Equal(500, resp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"An unexpected error occurred","type":"unexpected_error"}}""",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformError_InvalidModel()
    {
        var ex = TransformerErrors.InvalidModel("gpt-nope");

        var resp = new CompletionInboundTransformer().TransformError(ex);

        Assert.Equal(422, resp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"model not found: gpt-nope","type":"invalid_model_error"}}""",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformError_InvalidRequest()
    {
        var ex = TransformerErrors.InvalidRequest("prompt is required");

        var resp = new CompletionInboundTransformer().TransformError(ex);

        Assert.Equal(400, resp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"invalid request: prompt is required","type":"invalid_request_error"}}""",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformError_HttpClientErrorIsPassedThroughWithoutNormalization()
    {
        // Unlike the chat transformer (normalizeHTTPErrorStatus), the completion
        // transformer returns the transport error untouched, status included.
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":"upstream boom","type":"api_error"}}""");

        var resp = new CompletionInboundTransformer().TransformError(
            new HttpClientError("POST", "http://upstream.test/v1/completions", 302, "302 Found", body));

        Assert.Equal(302, resp.StatusCode);
        Assert.Equal(body, resp.Body);
    }

    [Fact]
    public void InboundTransformError_ResponseErrorKeepsStatusAndDetail()
    {
        var resp = new CompletionInboundTransformer().TransformError(new ResponseError
        {
            StatusCode = 503,
            Detail = new ErrorDetail
            {
                Message = "overloaded",
                Type = "server_error",
                Code = "overloaded_error",
                RequestId = "req-9",
            },
        });

        Assert.Equal(503, resp.StatusCode);
        Assert.Equal(
            """{"error":{"code":"overloaded_error","message":"overloaded","type":"server_error","request_id":"req-9"}}""",
            Encoding.UTF8.GetString(resp.Body!));
    }

    [Fact]
    public void InboundTransformError_GenericError()
    {
        var resp = new CompletionInboundTransformer().TransformError(
            new InvalidOperationException("kaboom"));

        Assert.Equal(500, resp.StatusCode);
        Assert.Equal(
            """{"error":{"message":"kaboom","type":"internal_server_error"}}""",
            Encoding.UTF8.GetString(resp.Body!));
    }

    // ----------------------------------------------- outbound TransformRequest

    [Fact]
    public void OutboundTransformRequest_BuildsWireBody()
    {
        var httpReq = NewOutbound().TransformRequest(new LlmRequest
        {
            Model = "gpt-3.5-turbo-instruct",
            RequestType = RequestTypes.Completion,
            Stream = false,
            Completion = new CompletionRequest
            {
                Prompt = "Say hello",
                MaxTokens = 16,
                Stop = new Stop { Single = "END" },
            },
        });

        Assert.Equal("POST", httpReq.Method);
        Assert.Equal("https://api.openai.com/v1/completions", httpReq.Url);
        Assert.Equal(RequestTypes.Completion, httpReq.RequestType);
        Assert.Equal(ApiFormats.OpenAICompletion, httpReq.ApiFormat);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpReq.Headers, "Accept"));
        Assert.NotNull(httpReq.Auth);
        Assert.Equal("bearer", httpReq.Auth!.Type);
        Assert.Equal("test-key", httpReq.Auth.ApiKey);

        // Go field order and omitempty behaviour of openai.CompletionRequest.
        Assert.Equal(
            """{"model":"gpt-3.5-turbo-instruct","prompt":"Say hello","max_tokens":16,"stream":false,"stop":"END"}""",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformRequest_CopiesEveryCompletionField()
    {
        var httpReq = NewOutbound().TransformRequest(new LlmRequest
        {
            Model = "m",
            Completion = new CompletionRequest
            {
                Prompt = "p",
                Suffix = "s",
                MaxTokens = 1,
                Temperature = 0.5,
                TopP = 0.9,
                N = 2,
                Logprobs = 3,
                Echo = true,
                Stop = new Stop { Multiple = ["a", "b"] },
                PresencePenalty = 0.1,
                FrequencyPenalty = 0.2,
                BestOf = 2,
                LogitBias = new Dictionary<string, long> { ["7"] = -5 },
                Seed = 42,
                User = "u",
            },
        });

        Assert.Equal(
            """{"model":"m","prompt":"p","suffix":"s","max_tokens":1,"temperature":0.5,"top_p":0.9,"n":2,"logprobs":3,"echo":true,"stop":["a","b"],"presence_penalty":0.1,"frequency_penalty":0.2,"best_of":2,"logit_bias":{"7":-5},"seed":42,"user":"u"}""",
            Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Fact]
    public void OutboundTransformRequest_EmptySuffixAndUserAreOmitted()
    {
        var httpReq = NewOutbound().TransformRequest(new LlmRequest
        {
            Model = "m",
            Completion = new CompletionRequest { Prompt = "p", Suffix = "", User = "" },
        });

        Assert.Equal("""{"model":"m","prompt":"p"}""", Encoding.UTF8.GetString(httpReq.Body!));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/completions")]
    [InlineData("https://api.openai.com", "https://api.openai.com/v1/completions")]
    [InlineData("https://api.openai.com/", "https://api.openai.com/v1/completions")]
    [InlineData("https://api.siliconflow.cn/v1", "https://api.siliconflow.cn/v1/completions")]
    public void OutboundTransformRequest_UrlBuilding(string baseUrl, string expectedUrl)
    {
        var httpReq = CompletionOutboundTransformer.Create(baseUrl, "test-key")
            .TransformRequest(MinimalRequest());

        Assert.Equal(expectedUrl, httpReq.Url);
    }

    [Theory]
    // A single '#' only suppresses the version; the endpoint path still applies.
    [InlineData("https://custom.api.com/v100#", false, "https://custom.api.com/v100/completions")]
    // '##' sets RawURL and the base URL is used verbatim.
    [InlineData("https://custom.api.com/anything##", false, "https://custom.api.com/anything")]
    [InlineData("https://api.openai.com", true, "https://api.openai.com")]
    public void OutboundTransformRequest_RawUrl(string baseUrl, bool rawUrl, string expectedUrl)
    {
        var transformer = new CompletionOutboundTransformer(new OpenAiOutboundConfig
        {
            BaseUrl = baseUrl,
            RawUrl = rawUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        Assert.Equal(expectedUrl, transformer.TransformRequest(MinimalRequest()).Url);
    }

    [Fact]
    public void OutboundTransformRequest_CustomEndpointPath()
    {
        var transformer = new CompletionOutboundTransformer(new OpenAiOutboundConfig
        {
            BaseUrl = "https://custom.api.com",
            EndpointPath = "/custom/completions",
            ApiKeyProvider = new StaticApiKeyProvider("test-key"),
        });

        Assert.Equal(
            "https://custom.api.com/custom/completions",
            transformer.TransformRequest(MinimalRequest()).Url);
    }

    [Fact]
    public void OutboundConfig_NilConfig()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CompletionOutboundTransformer(null!));

        Assert.Equal("config cannot be nil", ex.Message);
    }

    [Fact]
    public void OutboundConfig_MissingApiKeyProvider()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CompletionOutboundTransformer(new OpenAiOutboundConfig
            {
                BaseUrl = "https://api.openai.com",
            }));

        Assert.Equal("API key provider is required", ex.Message);
    }

    [Fact]
    public void OutboundConfig_MissingBaseUrl()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new CompletionOutboundTransformer(new OpenAiOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("k"),
            }));

        Assert.Equal("base URL is required", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_NilRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformRequest(null));

        Assert.Equal("llm request is nil", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_MissingCompletionSubRequest()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformRequest(new LlmRequest { Model = "m" }));

        Assert.Equal("completion request is nil in llm.Request", ex.Message);
    }

    [Fact]
    public void OutboundTransformRequest_MissingModel()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformRequest(new LlmRequest
            {
                Completion = new CompletionRequest { Prompt = "p" },
            }));

        Assert.Equal("model is required", ex.Message);
    }

    // ---------------------------------------------- outbound TransformResponse

    [Fact]
    public void OutboundTransformResponse_ValidResponse()
    {
        var resp = NewOutbound().TransformResponse(JsonResponse(
            """{"id":"cmpl-1","object":"text_completion","created":1677652288,"model":"gpt-3.5-turbo-instruct","choices":[{"text":"Hello","index":0,"logprobs":null,"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""));

        Assert.Equal("cmpl-1", resp.Id);
        Assert.Equal("text_completion", resp.Object);
        Assert.Equal(1677652288L, resp.Created);
        Assert.Equal("gpt-3.5-turbo-instruct", resp.Model);
        Assert.Equal(RequestTypes.Completion, resp.RequestType);
        Assert.Equal(ApiFormats.OpenAICompletion, resp.ApiFormat);

        var choice = Assert.Single(resp.Completion!.Choices);
        Assert.Equal("Hello", choice.Text);
        Assert.Equal(0, choice.Index);
        Assert.Equal("stop", choice.FinishReason);

        Assert.NotNull(resp.Usage);
        Assert.Equal(10L, resp.Usage!.PromptTokens);
        Assert.Equal(5L, resp.Usage.CompletionTokens);
        Assert.Equal(15L, resp.Usage.TotalTokens);
    }

    [Fact]
    public void OutboundTransformResponse_ZeroUsageLeavesUnifiedUsageNil()
    {
        // completionResponseToLLM only copies usage when a token counter is set.
        var resp = NewOutbound().TransformResponse(JsonResponse(
            """{"id":"cmpl-1","choices":[],"usage":{"prompt_tokens":0,"completion_tokens":0,"total_tokens":0}}"""));

        Assert.Null(resp.Usage);
        Assert.Empty(resp.Completion!.Choices);
    }

    [Fact]
    public void OutboundTransformResponse_NullBodyLeavesZeroStruct()
    {
        var resp = NewOutbound().TransformResponse(JsonResponse("null"));

        Assert.Equal(string.Empty, resp.Id);
        Assert.Empty(resp.Completion!.Choices);
        Assert.Null(resp.Usage);
    }

    [Fact]
    public void OutboundTransformResponse_NilHttpResponse()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformResponse(null));

        Assert.Equal("http response is nil", ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_EmptyBody()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformResponse(new HttpResponseData { StatusCode = 200, Body = [] }));

        Assert.Equal("response body is empty", ex.Message);
    }

    [Fact]
    public void OutboundTransformResponse_UpstreamErrorUsesTransformError()
    {
        // The completion outbound maps >= 400 through TransformOpenAIError, so
        // the provider detail wins over the status text.
        var error = Assert.Throws<ResponseError>(() =>
            NewOutbound().TransformResponse(new HttpResponseData
            {
                StatusCode = 429,
                Body = Encoding.UTF8.GetBytes(
                    """{"error":{"message":"slow down","type":"rate_limit_error"}}"""),
            }));

        Assert.Equal(429, error.StatusCode);
        Assert.Equal("slow down", error.Detail.Message);
        Assert.Equal("rate_limit_error", error.Detail.Type);
    }

    [Fact]
    public void OutboundTransformResponse_UpstreamErrorWithNonJsonBody()
    {
        var error = Assert.Throws<ResponseError>(() =>
            NewOutbound().TransformResponse(new HttpResponseData
            {
                StatusCode = 502,
                Body = Encoding.UTF8.GetBytes("bad gateway"),
            }));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("Bad Gateway", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void OutboundTransformResponse_InvalidJson()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformResponse(JsonResponse("not valid json")));

        Assert.StartsWith("failed to unmarshal completion response: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboundTransformResponse_RootArrayIsATypeError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformResponse(JsonResponse("[]")));

        Assert.Equal(
            "failed to unmarshal completion response: "
            + "json: cannot unmarshal array into Go value of type openai.CompletionResponse",
            ex.Message);
    }

    // --------------------------------------------- outbound TransformStreamChunk

    [Fact]
    public void OutboundTransformStreamChunk_Done()
    {
        var resp = NewOutbound().TransformStreamChunk(new StreamEvent
        {
            Data = Encoding.UTF8.GetBytes("[DONE]"),
        });

        Assert.Equal("[DONE]", resp!.Object);
    }

    [Fact]
    public void OutboundTransformStreamChunk_EmptyDataIsFiltered()
    {
        var resp = NewOutbound().TransformStreamChunk(new StreamEvent { Data = [] });

        Assert.Null(resp);
    }

    [Fact]
    public void OutboundTransformStreamChunk_ParsesChunk()
    {
        var resp = NewOutbound().TransformStreamChunk(new StreamEvent
        {
            Data = Encoding.UTF8.GetBytes(
                """{"id":"cmpl-1","choices":[{"text":"Hel","index":0}]}"""),
        });

        Assert.Equal("Hel", Assert.Single(resp!.Completion!.Choices).Text);
        Assert.Null(resp.Usage);
    }

    [Fact]
    public void OutboundTransformStreamChunk_NilEvent()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NewOutbound().TransformStreamChunk(null));

        Assert.Equal("stream event is nil", ex.Message);
    }

    // ----------------------------------------------------------- aggregation

    [Fact]
    public void AggregateStreamChunks_EmptyChunks()
    {
        var body = NewOutbound().AggregateStreamChunks(null, [], out var meta);

        Assert.Equal(
            """{"id":"","choices":null,"object":"","created":0,"model":""}""",
            Encoding.UTF8.GetString(body));
        Assert.Equal(string.Empty, meta.Id);
        Assert.Null(meta.Usage);
        Assert.False(meta.Completed);
    }

    [Fact]
    public void AggregateStreamChunks_ConcatenatesTextAndKeepsFirstMetadata()
    {
        var chunks = new List<StreamEvent>
        {
            Event("""{"id":"cmpl-1","object":"text_completion","created":1677652288,"model":"gpt-3.5-turbo-instruct","choices":[{"text":"Hello","index":0}]}"""),
            Event("[DONE]"),
            Event("""{"id":"cmpl-2","object":"text_completion","created":1,"model":"other","choices":[{"text":" world","index":0,"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}"""),
        };

        var body = CompletionOutboundTransformer.Create("https://api.openai.com", "k")
            .AggregateStreamChunks(null, chunks, out var meta);

        // The unified llm.Response shape, Go field order, choices:null and the
        // usage struct's always-emitted detail fields.
        Assert.Equal(
            """{"id":"cmpl-1","choices":null,"object":"text_completion","created":1677652288,"model":"gpt-3.5-turbo-instruct","usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15,"prompt_tokens_details":null,"completion_tokens_details":null},"completion":{"choices":[{"text":"Hello world","index":0,"finish_reason":"stop"}]},"request_type":"completion","api_format":"openai/completions"}""",
            Encoding.UTF8.GetString(body));

        Assert.Equal("cmpl-1", meta.Id);
        Assert.True(meta.Completed);
        Assert.NotNull(meta.Usage);
        Assert.Equal(10L, meta.Usage!.PromptTokens);
    }

    [Fact]
    public void AggregateStreamChunks_DefaultsFinishReasonToStop()
    {
        var chunks = new List<StreamEvent>
        {
            Event("""{"id":"cmpl-1","choices":[{"text":"Hi","index":0}]}"""),
        };

        var body = CompletionOutboundTransformer.Create("https://api.openai.com", "k")
            .AggregateStreamChunks(null, chunks, out var meta);

        // completed is computed before the default kicker.
        Assert.False(meta.Completed);

        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("completion").GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());
    }

    [Fact]
    public void AggregateStreamChunks_OnlyDoneChunksProduceZeroResponse()
    {
        var chunks = new List<StreamEvent> { Event("[DONE]") };

        var body = CompletionOutboundTransformer.Create("https://api.openai.com", "k")
            .AggregateStreamChunks(null, chunks, out var meta);

        Assert.Equal(
            """{"id":"","choices":null,"object":"","created":0,"model":""}""",
            Encoding.UTF8.GetString(body));
        Assert.False(meta.Completed);
    }

    [Fact]
    public void AggregateStreamChunks_SkipsUndecodableChunks()
    {
        var chunks = new List<StreamEvent>
        {
            Event("not json"),
            Event("""{"id":"cmpl-1","choices":[{"text":"Hi","index":0,"finish_reason":"length"}]}"""),
        };

        var body = CompletionOutboundTransformer.Create("https://api.openai.com", "k")
            .AggregateStreamChunks(null, chunks, out var meta);

        Assert.Equal("cmpl-1", meta.Id);
        Assert.True(meta.Completed);

        using var doc = JsonDocument.Parse(body);
        var choice = doc.RootElement.GetProperty("completion").GetProperty("choices")[0];
        Assert.Equal("length", choice.GetProperty("finish_reason").GetString());
    }

    // ------------------------------------------------------------------ helpers

    private static CompletionOutboundTransformer NewOutbound()
        => CompletionOutboundTransformer.Create("https://api.openai.com/v1", "test-key");

    private static LlmRequest MinimalRequest() => new()
    {
        Model = "m",
        Completion = new CompletionRequest { Prompt = "p" },
    };

    private static StreamEvent Event(string data)
        => new() { Data = Encoding.UTF8.GetBytes(data) };

    private static List<StreamEvent> Drain(IStream<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        while (stream.Next())
        {
            events.Add(stream.Current!);
        }

        return events;
    }

    private static HttpResponseData JsonResponse(string json) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(json),
    };

    private static HttpRequestData JsonRequest(string body, string contentType = "application/json")
        => new()
        {
            Body = Encoding.UTF8.GetBytes(body),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
        };
}
