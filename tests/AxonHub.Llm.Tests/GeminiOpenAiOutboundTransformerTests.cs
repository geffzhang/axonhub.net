using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Gemini.OpenAI;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Gemini OpenAI-compatible outbound
/// (llm/transformer/gemini/openai/outbound.go): the v1beta/openai base URL,
/// metadata stripping, Google-native tool filtering, extra_body thinking
/// configuration, thought signature passthrough and the Gemini error envelope.
/// </summary>
public sealed class GeminiOpenAiOutboundTransformerTests
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com";

    // ------------------------------------------------------------------
    // Constructors
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com")]
    [InlineData("https://generativelanguage.googleapis.com/")]
    public void LegacyConstructorAcceptsAValidBaseUrl(string baseUrl)
    {
        var transformer = GeminiOpenAiOutboundTransformer.Create(baseUrl, "test-api-key");

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", transformer.BaseUrl);
    }

    [Fact]
    public void LegacyConstructorRequiresABaseUrl()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => GeminiOpenAiOutboundTransformer.Create("", "test-api-key"));

        Assert.Equal("base URL is required for Gemini OpenAI transformer", error.Message);
    }

    [Fact]
    public void LegacyConstructorRequiresAnApiKey()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => GeminiOpenAiOutboundTransformer.Create(BaseUrl, ""));

        Assert.Equal("API key provider is required for Gemini OpenAI transformer", error.Message);
    }

    [Theory]
    [InlineData("https://generativelanguage.googleapis.com")]
    [InlineData("https://generativelanguage.googleapis.com/")]
    public void ConfigConstructorNormalizesTheBaseUrlToV1BetaOpenAi(string baseUrl)
    {
        var provider = new StaticApiKeyProvider("test-api-key");

        var transformer = new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = provider,
        });

        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai", transformer.BaseUrl);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(ChatRequest());
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", httpRequest.Url);
        Assert.Equal("test-api-key", httpRequest.Auth!.ApiKey);
    }

    [Fact]
    public void ConfigConstructorRequiresABaseUrl()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
            {
                ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            }));

        Assert.Equal("base URL is required for Gemini OpenAI transformer", error.Message);
    }

    [Fact]
    public void ConfigConstructorRequiresAnApiKeyProvider()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
            {
                BaseUrl = BaseUrl,
            }));

        Assert.Equal("API key provider is required for Gemini OpenAI transformer", error.Message);
    }

    [Fact]
    public void NilConfigIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new GeminiOpenAiOutboundTransformer(null!));

        Assert.Equal(
            "invalid Gemini OpenAI transformer configuration: config cannot be nil",
            error.Message);
    }

    // ------------------------------------------------------------------
    // TransformRequest basics
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequestSendsTheChatCompletionWireShape()
    {
        var httpRequest = Create().TransformRequest(ChatRequest());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", httpRequest.Url);
        Assert.Equal("application/json", httpRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", httpRequest.Headers!["Accept"][0]);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal("test-api-key", httpRequest.Auth!.ApiKey);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, httpRequest.ApiFormat);
        Assert.True(httpRequest.SkipInboundQueryMerge);
        Assert.Null(httpRequest.Metadata);

        var body = Body(httpRequest);
        Assert.Equal("gemini-2.5-flash", body.GetProperty("model").GetString());
        Assert.Equal("user", body.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.False(body.TryGetProperty("metadata", out _));
    }

    [Fact]
    public void RequestMetadataIsStrippedBeforeMarshalling()
    {
        var request = ChatRequest();
        request.Metadata = new Dictionary<string, string> { ["account"] = "identity" };

        var httpRequest = Create().TransformRequest(request);

        Assert.Null(httpRequest.Metadata);
        Assert.False(Body(httpRequest).TryGetProperty("metadata", out _));

        // The caller's request is untouched: Go copies before clearing.
        Assert.NotNull(request.Metadata);
    }

    [Fact]
    public void NullRequestIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Create().TransformRequest(null));

        Assert.Equal("chat completion request is nil", error.Message);
    }

    [Theory]
    [InlineData(RequestTypes.Compact, "invalid request: compact is only supported by OpenAI Responses API")]
    [InlineData(RequestTypes.Image, "invalid request: image is not supported")]
    public void UnsupportedRequestTypesAreRejected(string requestType, string expectedMessage)
    {
        var request = ChatRequest();
        request.RequestType = requestType;

        var error = Assert.Throws<TransformerException>(() => Create().TransformRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void TheRequestTypeCheckRunsBeforeModelValidation()
    {
        var request = ChatRequest(model: "");
        request.RequestType = RequestTypes.Compact;

        var error = Assert.Throws<TransformerException>(() => Create().TransformRequest(request));

        Assert.Equal(
            "invalid request: compact is only supported by OpenAI Responses API",
            error.Message);
    }

    [Fact]
    public void AnEmptyModelIsRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Create().TransformRequest(ChatRequest(model: "")));

        Assert.Equal("model is required", error.Message);
    }

    [Fact]
    public void EmptyMessagesAreRejected()
    {
        var request = ChatRequest();
        request.Messages = [];

        var error = Assert.Throws<TransformerException>(() => Create().TransformRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Equal("invalid request: messages are required", error.Message);
    }

    [Fact]
    public void SelectedApiKeyIsTrackedForTheAuditRow()
    {
        var transformer = new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
        {
            BaseUrl = BaseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
            ChannelApiKeyIndex = 2,
        });

        Assert.Null(transformer.SelectedApiKey);
        Assert.Equal(2, transformer.SelectedApiKeyIndex);

        transformer.TransformRequest(ChatRequest());

        Assert.Equal("test-api-key", transformer.SelectedApiKey);
        Assert.Equal(2, transformer.SelectedApiKeyIndex);
    }

    // ------------------------------------------------------------------
    // ThinkingBudget
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1024L, null, "1024")]
    [InlineData(null, "low", "\"low\"")]
    [InlineData(null, null, "null")]
    public void ThinkingBudgetMarshalsStringFirstThenIntThenNull(
        long? intValue,
        string? stringValue,
        string expected)
    {
        var budget = new ThinkingBudget { IntValue = intValue, StringValue = stringValue };

        Assert.Equal(expected, GoJson.MarshalToString(budget));
    }

    [Theory]
    [InlineData("1024", 1024L, null)]
    [InlineData("\"low\"", null, "low")]
    [InlineData("\"high\"", null, "high")]
    public void ThinkingBudgetDecodesIntsAndStrings(string input, long? expectedInt, string? expectedString)
    {
        var budget = LlmJson.Deserialize<ThinkingBudget>(input);

        Assert.NotNull(budget);
        Assert.Equal(expectedInt, budget!.IntValue);
        Assert.Equal(expectedString, budget.StringValue);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("99999999999999999999")]
    [InlineData("{}")]
    public void ThinkingBudgetRejectsAnythingElse(string input)
    {
        var error = Assert.Throws<JsonException>(
            () => LlmJson.Deserialize<ThinkingBudget>(input));

        Assert.Contains("thinking_budget must be an int or string", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // thinking_config <-> reasoning_effort
    // ------------------------------------------------------------------

    [Fact]
    public void ANullThinkingConfigHasNoReasoningEffort()
    {
        Assert.Equal("", GeminiOpenAiOutboundTransformer.ThinkingConfigToReasoningEffort(null));
    }

    [Theory]
    [InlineData("high|1024", "high")]
    [InlineData("|1024", "low")]
    [InlineData("|8192", "medium")]
    [InlineData("|24576", "high")]
    [InlineData("|0", "none")]
    [InlineData("|\"low\"", "low")]
    [InlineData("minimal|", "minimal")]
    [InlineData("low|", "low")]
    [InlineData("medium|", "medium")]
    [InlineData("|", "")]
    [InlineData("|9999", "")]
    public void ThinkingConfigToReasoningEffortMatchesTheReferenceTable(
        string spec,
        string expectedEffort)
    {
        var parts = spec.Split('|');
        var config = new ThinkingConfig
        {
            ThinkingLevel = parts[0],
            ThinkingBudget = parts[1].Length == 0
                ? null
                : parts[1].StartsWith('"')
                    ? new ThinkingBudget { StringValue = parts[1].Trim('"') }
                    : new ThinkingBudget { IntValue = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) },
        };

        Assert.Equal(
            expectedEffort,
            GeminiOpenAiOutboundTransformer.ThinkingConfigToReasoningEffort(config));
    }

    [Theory]
    [InlineData("minimal", 1024L)]
    [InlineData("low", 1024L)]
    [InlineData("medium", 8192L)]
    [InlineData("high", 24576L)]
    [InlineData("none", 0L)]
    public void ReasoningEffortMapsToTheIntegerLadder(string effort, long expectedBudget)
    {
        var budget = GeminiOpenAiOutboundTransformer.ReasoningEffortToThinkingBudget(effort);

        Assert.NotNull(budget);
        Assert.Equal(expectedBudget, budget!.IntValue!.Value);
        Assert.Null(budget.StringValue);
    }

    [Fact]
    public void AnUnknownReasoningEffortHasNoBudget()
    {
        Assert.Null(GeminiOpenAiOutboundTransformer.ReasoningEffortToThinkingBudget("enormous"));
    }

    // ------------------------------------------------------------------
    // extra_body
    // ------------------------------------------------------------------

    [Fact]
    public void ReasoningEffortIsPreservedWithoutAnExtraBody()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "medium";

        var body = Body(Create().TransformRequest(request));

        Assert.Equal("medium", body.GetProperty("reasoning_effort").GetString());
        Assert.False(body.TryGetProperty("extra_body", out _));
    }

    [Fact]
    public void ExtraBodyThinkingConfigOverridesAndClearsReasoningEffort()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "high";
        request.ExtraBody = JsonDocument.Parse("""
            {"google":{"thinking_config":{"thinking_budget":1024,"include_thoughts":true}}}
            """).RootElement.Clone();

        var body = Body(Create().TransformRequest(request));

        Assert.False(body.TryGetProperty("reasoning_effort", out _));

        var thinking = body.GetProperty("extra_body").GetProperty("google").GetProperty("thinking_config");
        Assert.Equal(1024L, thinking.GetProperty("thinking_budget").GetInt64());
        Assert.True(thinking.GetProperty("include_thoughts").GetBoolean());
        Assert.False(thinking.TryGetProperty("thinking_level", out _));
    }

    [Fact]
    public void AThinkingConfigWithoutABudgetIsFilledFromReasoningEffort()
    {
        var request = ChatRequest("models/gemini-2.5-flash");
        request.ReasoningEffort = "low";
        request.ExtraBody = JsonDocument.Parse("""
            {"google":{"thinking_config":{"include_thoughts":true}}}
            """).RootElement.Clone();

        var body = Body(Create().TransformRequest(request));

        Assert.False(body.TryGetProperty("reasoning_effort", out _));

        var thinking = body.GetProperty("extra_body").GetProperty("google").GetProperty("thinking_config");
        Assert.Equal(1024L, thinking.GetProperty("thinking_budget").GetInt64());
        Assert.False(thinking.TryGetProperty("thinking_level", out _));
        Assert.True(thinking.GetProperty("include_thoughts").GetBoolean());
    }

    [Theory]
    // A supplied budget is kept verbatim; reasoning_effort is still cleared.
    [InlineData("gemini-2.5-flash", "", "{\"thinking_budget\":8192,\"include_thoughts\":true}", "8192", null)]
    [InlineData("gemini-2.5-flash", "", "{\"thinking_budget\":1024,\"include_thoughts\":true}", "1024", null)]
    [InlineData("gemini-2.5-flash", "", "{\"thinking_budget\":24576,\"include_thoughts\":true}", "24576", null)]
    [InlineData("gemini-2.5-flash", "", "{\"thinking_budget\":\"low\",\"include_thoughts\":true}", "\"low\"", null)]
    // thinking_level wins and drops the supplied budget.
    [InlineData("gemini-2.5-flash", "", "{\"thinking_level\":\"high\",\"thinking_budget\":1024,\"include_thoughts\":true}", null, "high")]
    // An explicit config overrides a top-level reasoning_effort.
    [InlineData("gemini-2.5-flash", "high", "{\"thinking_budget\":1024,\"include_thoughts\":true}", "1024", null)]
    public void ExtraBodyThinkingConfigRows(
        string model,
        string reasoningEffort,
        string thinkingConfig,
        string? expectedBudgetJson,
        string? expectedLevel)
    {
        var request = ChatRequest(model);
        request.ReasoningEffort = reasoningEffort.Length == 0 ? null : reasoningEffort;
        request.ExtraBody = JsonDocument.Parse(
            "{\"google\":{\"thinking_config\":" + thinkingConfig + "}}").RootElement.Clone();

        var body = Body(Create().TransformRequest(request));

        Assert.False(body.TryGetProperty("reasoning_effort", out _));

        var thinking = body.GetProperty("extra_body").GetProperty("google").GetProperty("thinking_config");
        if (expectedBudgetJson is null)
        {
            Assert.False(thinking.TryGetProperty("thinking_budget", out _));
        }
        else
        {
            Assert.Equal(expectedBudgetJson, thinking.GetProperty("thinking_budget").GetRawText());
        }

        if (expectedLevel is null)
        {
            Assert.False(thinking.TryGetProperty("thinking_level", out _));
        }
        else
        {
            Assert.Equal(expectedLevel, thinking.GetProperty("thinking_level").GetString());
        }

        Assert.True(thinking.GetProperty("include_thoughts").GetBoolean());
    }

    [Fact]
    public void AGemini3ModelReceivesTheEffortAsAThinkingLevel()
    {
        var request = ChatRequest("gemini-3-pro");
        request.ReasoningEffort = "Medium";
        request.ExtraBody = JsonDocument.Parse("""
            {"google":{"thinking_config":{"include_thoughts":true}}}
            """).RootElement.Clone();

        var body = Body(Create().TransformRequest(request));
        var thinking = body.GetProperty("extra_body").GetProperty("google").GetProperty("thinking_config");

        Assert.Equal("medium", thinking.GetProperty("thinking_level").GetString());
        Assert.False(thinking.TryGetProperty("thinking_budget", out _));
    }

    [Fact]
    public void AnEmptyExtraBodyIsForwardedAsAnEmptyObject()
    {
        var request = ChatRequest();
        request.ExtraBody = JsonDocument.Parse("{}").RootElement.Clone();

        var body = Body(Create().TransformRequest(request));

        var extra = body.GetProperty("extra_body");
        Assert.Equal(JsonValueKind.Object, extra.ValueKind);
        Assert.False(extra.TryGetProperty("google", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"not-an-object\"")]
    [InlineData("{\"google\":\"x\"}")]
    [InlineData("{\"google\":{\"thinking_config\":{\"thinking_budget\":true}}}")]
    public void AnUndecodableExtraBodyIsDropped(string extraBody)
    {
        var request = ChatRequest();
        request.ExtraBody = JsonDocument.Parse(extraBody).RootElement.Clone();

        var body = Body(Create().TransformRequest(request));

        Assert.False(body.TryGetProperty("extra_body", out _));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"google\":null}")]
    [InlineData("{\"google\":{}}")]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{\"google\":{\"thinking_config\":null}}")]
    public void ParseExtraBodyYieldsNoThinkingConfig(string input)
    {
        var extra = GeminiExtraBodyParser.ParseExtraBody(JsonDocument.Parse(input).RootElement);

        Assert.NotNull(extra);
        Assert.Null(extra!.Google?.ThinkingConfig);
    }

    [Fact]
    public void ParseExtraBodyDecodesIntBudgetAndLevel()
    {
        var extra = GeminiExtraBodyParser.ParseExtraBody(JsonDocument.Parse("""
            {"google":{"thinking_config":{"thinking_budget":1024,"thinking_level":"low","include_thoughts":true}}}
            """).RootElement);

        var thinking = extra!.Google!.ThinkingConfig!;
        Assert.Equal(1024L, thinking.ThinkingBudget!.IntValue!.Value);
        Assert.Null(thinking.ThinkingBudget.StringValue);
        Assert.Equal("low", thinking.ThinkingLevel);
        Assert.True(thinking.IncludeThoughts);
    }

    [Fact]
    public void ParseExtraBodyDecodesStringBudget()
    {
        var extra = GeminiExtraBodyParser.ParseExtraBody(JsonDocument.Parse("""
            {"google":{"thinking_config":{"thinking_budget":"high","include_thoughts":true}}}
            """).RootElement);

        var thinking = extra!.Google!.ThinkingConfig!;
        Assert.Null(thinking.ThinkingBudget!.IntValue);
        Assert.Equal("high", thinking.ThinkingBudget.StringValue);
        Assert.True(thinking.IncludeThoughts);
    }

    [Fact]
    public void ParseExtraBodyLetsALaterDuplicateKeyWin()
    {
        var extra = GeminiExtraBodyParser.ParseExtraBody(JsonDocument.Parse("""
            {"google":{"thinking_config":{"thinking_budget":1024,"thinking_budget":"high"}}}
            """).RootElement);

        var budget = extra!.Google!.ThinkingConfig!.ThinkingBudget!;
        Assert.Equal("high", budget.StringValue);
        Assert.Null(budget.IntValue);
    }

    [Fact]
    public void ParseExtraBodyNullIsNull()
    {
        Assert.Null(GeminiExtraBodyParser.ParseExtraBody(null));
        Assert.Null(GeminiExtraBodyParser.ParseExtraBody(default(JsonElement)));
        Assert.Null(GeminiExtraBodyParser.ParseExtraBody(
            JsonDocument.Parse("null").RootElement));
    }

    // ------------------------------------------------------------------
    // Thought signatures
    // ------------------------------------------------------------------

    [Fact]
    public void AReasoningSignatureSeedsTheFirstToolCall()
    {
        var request = new LlmRequest
        {
            Model = "gemini-3-pro",
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ReasoningSignature = "base64_signature",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call_1",
                            Type = "function",
                            Function = new FunctionCall { Name = "get_weather", Arguments = """{"city":"Shanghai"}""" },
                        },
                    ],
                },
            ],
        };

        var body = Body(Create().TransformRequest(request));
        var toolCall = body.GetProperty("messages")[0].GetProperty("tool_calls")[0];

        Assert.Equal(
            "base64_signature",
            toolCall.GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
    }

    [Fact]
    public void AMetadataSignatureStaysOnItsToolCallPosition()
    {
        var request = new LlmRequest
        {
            Model = "gemini-3-pro",
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ReasoningSignature = "sig_from_second",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call_1",
                            Type = "function",
                            Function = new FunctionCall { Name = "tool_a", Arguments = "{}" },
                        },
                        new ToolCall
                        {
                            Id = "call_2",
                            Type = "function",
                            Function = new FunctionCall { Name = "tool_b", Arguments = "{}" },
                            Index = 1,
                            TransformerMetadata = Metadata("sig_from_second"),
                        },
                    ],
                },
            ],
        };

        var body = Body(Create().TransformRequest(request));
        var toolCalls = body.GetProperty("messages")[0].GetProperty("tool_calls");

        Assert.False(toolCalls[0].TryGetProperty("extra_content", out _));
        Assert.Equal(
            "sig_from_second",
            toolCalls[1].GetProperty("extra_content").GetProperty("google").GetProperty("thought_signature").GetString());
    }

    [Fact]
    public void TheSignatureFollowsTheToolCallIdWhenTheOrderChanges()
    {
        var prefixed = "prefixed_signature";
        var source = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        ToolCall("call_1", "tool_a"),
                        WithMetadata(ToolCall("call_2", "tool_b"), prefixed),
                    ],
                },
            ],
        };

        var destination = new OpenAiRequest
        {
            Messages =
            [
                new OpenAiMessage
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        OpenAiToolCall("call_2", "tool_b"),
                        OpenAiToolCall("call_1", "tool_a"),
                    ],
                },
            ],
        };

        GeminiOpenAiOutboundTransformer.FillGeminiThoughtSignatureForGeminiOpenAIRequest(
            source,
            destination);

        Assert.Equal(
            prefixed,
            destination.Messages![0].ToolCalls![0].ExtraContent!.Google!.ThoughtSignature);
        Assert.Null(destination.Messages[0].ToolCalls![1].ExtraContent);
    }

    [Fact]
    public void AMetadataSignaturePassesThroughVerbatim()
    {
        var prefixed = "gemini_thought_signature:prefixed";
        var source = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ToolCalls = [WithMetadata(ToolCall("call_1", "tool_a"), prefixed)],
                },
            ],
        };

        var destination = new OpenAiRequest
        {
            Messages =
            [
                new OpenAiMessage
                {
                    Role = "assistant",
                    ToolCalls = [OpenAiToolCall("call_1", "tool_a")],
                },
            ],
        };

        GeminiOpenAiOutboundTransformer.FillGeminiThoughtSignatureForGeminiOpenAIRequest(
            source,
            destination);

        // The Gemini OpenAI direction carries raw values: no prefix decoding.
        Assert.Equal(
            prefixed,
            destination.Messages![0].ToolCalls![0].ExtraContent!.Google!.ThoughtSignature);
    }

    // ------------------------------------------------------------------
    // Google native tools
    // ------------------------------------------------------------------

    [Fact]
    public void GoogleNativeToolsAreFilteredOut()
    {
        var request = ChatRequest();
        request.Tools =
        [
            FunctionTool("get_weather"),
            new Tool { Type = ToolTypes.GoogleSearch, Google = new GoogleTools { Search = new GoogleSearch() } },
        ];

        var body = Body(Create().TransformRequest(request));
        var tools = body.GetProperty("tools");

        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("get_weather", tools[0].GetProperty("function").GetProperty("name").GetString());

        // Go copies the request before filtering; the caller's list is intact.
        Assert.Equal(2, request.Tools.Count);
    }

    [Fact]
    public void AnAllGoogleNativeToolListAlsoDropsToolChoice()
    {
        var request = ChatRequest();
        request.Tools =
        [
            new Tool { Type = ToolTypes.GoogleSearch, Google = new GoogleTools { Search = new GoogleSearch() } },
            new Tool { Type = ToolTypes.GoogleUrlContext, Google = new GoogleTools { UrlContext = new GoogleUrlContext() } },
        ];
        request.ToolChoice = new ToolChoice { ToolChoiceMode = "auto" };

        var body = Body(Create().TransformRequest(request));

        Assert.False(body.TryGetProperty("tools", out _));
        Assert.False(body.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public void FunctionOnlyToolsAreNotTouched()
    {
        var request = ChatRequest();
        request.Tools = [FunctionTool("get_weather")];

        var body = Body(Create().TransformRequest(request));

        Assert.Equal(1, body.GetProperty("tools").GetArrayLength());
    }

    // ------------------------------------------------------------------
    // Error envelopes
    // ------------------------------------------------------------------

    [Fact]
    public void TheArrayEnvelopeIsParsed()
    {
        var error = Create().TransformError(new HttpClientError(
            "POST",
            "https://example.test/chat/completions",
            400,
            "400 Bad Request",
            """
            [{"error":{"code":400,"message":"Expected one of either `reasoning_effort` or custom `thinking_config`; found both.","status":"INVALID_ARGUMENT"}}]
            """u8.ToArray()));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("INVALID_ARGUMENT", error.Detail.Type);
        Assert.Equal("400", error.Detail.Code);
        Assert.Contains("found both", error.Detail.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"error":{"code":400,"message":"bad request","status":"INVALID_ARGUMENT"}}""", "400", "bad request", "INVALID_ARGUMENT")]
    [InlineData("""{"error":{"message":"boom"}}""", "0", "boom", "api_error")]
    [InlineData("""{"ERROR":{"Code":403,"Message":"denied","Status":"PERMISSION_DENIED"}}""", "403", "denied", "PERMISSION_DENIED")]
    public void TheObjectEnvelopeIsParsed(
        string body,
        string expectedCode,
        string expectedMessage,
        string expectedType)
    {
        var error = Create().TransformError(HttpError(400, body));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(expectedCode, error.Detail.Code);
        Assert.Equal(expectedMessage, error.Detail.Message);
        Assert.Equal(expectedType, error.Detail.Type);
    }

    [Theory]
    [InlineData("""[{"error":{"code":"400","message":"bad"}}]""")]
    [InlineData("[]")]
    [InlineData("""[{"error":{"message":""}}]""")]
    [InlineData("gateway down")]
    [InlineData("\"string\"")]
    public void UnparsableBodiesBecomeTheRawMessage(string body)
    {
        var error = Create().TransformError(HttpError(400, body));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(body, error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void AnEmptyBodyFallsBackToTheOpenAiErrorConversion()
    {
        var error = Create().TransformError(new HttpClientError(
            "POST",
            "https://example.test/chat/completions",
            502,
            "502 Bad Gateway",
            []));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(GoHttpStatus.Text(502), error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    [Fact]
    public void ANullErrorIsAnInternalServerError()
    {
        var error = Create().TransformError(null);

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("Internal Server Error", error.Detail.Message);
        Assert.Equal("api_error", error.Detail.Type);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static GeminiOpenAiOutboundTransformer Create()
        => new(new GeminiOpenAiOutboundConfig
        {
            BaseUrl = BaseUrl,
            ApiKeyProvider = new StaticApiKeyProvider("test-api-key"),
        });

    private static LlmRequest ChatRequest(string model = "gemini-2.5-flash")
        => new()
        {
            Model = model,
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText("Hello, world!") },
            ],
        };

    private static Tool FunctionTool(string name)
        => new() { Type = "function", Function = new Function { Name = name } };

    private static Dictionary<string, JsonElement> Metadata(string signature)
        => new()
        {
            [OpenAiConvertToLlm.TransformerMetadataKeyGoogleThoughtSignature] =
                JsonSerializer.SerializeToElement(signature),
        };

    private static ToolCall ToolCall(string id, string name)
        => new()
        {
            Id = id,
            Type = "function",
            Function = new FunctionCall { Name = name, Arguments = "{}" },
        };

    private static ToolCall WithMetadata(ToolCall toolCall, string signature)
    {
        toolCall.TransformerMetadata = Metadata(signature);
        return toolCall;
    }

    private static OpenAiToolCall OpenAiToolCall(string id, string name)
        => new()
        {
            Id = id,
            Type = "function",
            Function = new OpenAiFunctionCall { Name = name, Arguments = "{}" },
        };

    private static HttpClientError HttpError(int statusCode, string body)
        => new("POST", "https://example.test/chat/completions", statusCode, statusCode + " Error", System.Text.Encoding.UTF8.GetBytes(body));

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }
}
