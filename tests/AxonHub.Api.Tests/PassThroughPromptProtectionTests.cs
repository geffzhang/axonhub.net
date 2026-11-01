using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.PromptProtection;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/pass_through_prompt_protection_test.go:
/// provider-native text paths, role scopes, and the fail-safe fallbacks that
/// stop protected content from being replayed unmodified.
/// </summary>
public sealed class PassThroughPromptProtectionTests
{
    internal static PromptProtectionRule MaskRule(
        string pattern,
        string replacement = "[MASKED]",
        params string[] scopes)
        => new()
        {
            Pattern = pattern,
            Settings = new PromptProtectionSettings
            {
                Action = PromptProtectionActions.Mask,
                Replacement = replacement,
                Scopes = scopes.Length == 0 ? null : [.. scopes],
            },
        };

    private static PromptProtectionRule UserSecretRule()
        => MaskRule("secret-[0-9]+", scopes: PromptProtectionScopes.User);

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    internal static string JsonString(byte[] body, string path)
        => PromptProtectionJson.String(body, path);

    internal static string JsonToken(byte[] body, string path)
        => OverrideJson.Get(body, path).Node!.Raw;

    private static string Patched(
        string body,
        string apiFormat,
        params PromptProtectionRule[] rules)
    {
        Assert.True(
            PassThroughPromptProtection.TryPatch(Bytes(body), apiFormat, rules, out var patched, out var error),
            error);
        Assert.Null(error);
        return Encoding.UTF8.GetString(patched);
    }

    [Theory]
    [InlineData(
        ApiFormats.OpenAIResponse,
        """{"instructions":"secret-001","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"secret-002"}]},{"type":"function_call_output","output":"secret-003"}]}""",
        "input.0.content.0.text",
        "instructions,input.1.output")]
    [InlineData(
        ApiFormats.AnthropicMessage,
        """{"system":"secret-001","messages":[{"role":"user","content":"secret-002"},{"role":"assistant","content":[{"type":"text","text":"secret-003"}]}]}""",
        "messages.0.content",
        "system,messages.1.content.0.text")]
    [InlineData(
        ApiFormats.GeminiContents,
        """{"systemInstruction":{"parts":[{"text":"secret-001"}]},"contents":[{"role":"user","parts":[{"text":"secret-002"}]},{"role":"model","parts":[{"text":"secret-003"}]}]}""",
        "contents.0.parts.0.text",
        "systemInstruction.parts.0.text,contents.1.parts.0.text")]
    public void PatchesProviderNativeTextPaths(
        string apiFormat,
        string body,
        string maskedPath,
        string unchangedPaths)
    {
        // Port of TestPatchPassThroughPromptProtection.
        var patched = Patched(body, apiFormat, UserSecretRule());

        Assert.Equal("[MASKED]", JsonString(Bytes(patched), maskedPath));

        foreach (var path in unchangedPaths.Split(','))
        {
            Assert.Contains("secret-", JsonString(Bytes(patched), path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MasksOpenAIResponsesCompact()
    {
        // Port of TestPatchPassThroughPromptProtectionMasksOpenAIResponsesCompact.
        const string body =
            """{"model":"gpt-5","instructions":"secret-001","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"secret-002"}]}],"provider_option":{"keep":true}}""";

        var patched = Bytes(Patched(body, ApiFormats.OpenAIResponseCompact, UserSecretRule()));

        Assert.Equal("[MASKED]", JsonString(patched, "input.0.content.0.text"));
        Assert.Equal("secret-001", JsonString(patched, "instructions"));
        Assert.Equal("true", JsonToken(patched, "provider_option.keep"));
    }

    [Fact]
    public void RejectsUnsupportedFormat()
    {
        // Port of TestPatchPassThroughPromptProtectionRejectsUnsupportedFormat.
        Assert.False(PassThroughPromptProtection.TryPatch(
            Bytes("""{"messages":[{"role":"user","content":"secret"}]}"""),
            "aisdk/text",
            [MaskRule("secret")],
            out _,
            out var error));

        Assert.Contains(
            "does not support raw prompt protection patches",
            error,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MasksGeminiFunctionResponseWithoutTouchingKeysOrNumbers()
    {
        // Port of TestPatchPassThroughPromptProtectionMasksGeminiFunctionResponse.
        const string body = """
            {
                "contents":[
                    {"role":"user","parts":[{"text":"secret-user"}]},
                    {"role":"user","parts":[{"functionResponse":{"name":"lookup","response":{"token":"secret-tool","secret-property":"provider-key","sequence":9007199254740993,"nested":{"token":"secret-nested"},"provider_meta":{"keep":true}}}}]}
                ]
            }
            """;
        var rules = new[]
        {
            MaskRule(
                "secret-[a-z]+",
                scopes: [PromptProtectionScopes.User, PromptProtectionScopes.Tool]),
        };

        var patched = Bytes(Patched(body, ApiFormats.GeminiContents, rules));

        Assert.Equal("[MASKED]", JsonString(patched, "contents.0.parts.0.text"));
        Assert.Equal("[MASKED]", JsonString(patched, "contents.1.parts.0.functionResponse.response.token"));
        Assert.Equal("[MASKED]", JsonString(patched, "contents.1.parts.0.functionResponse.response.nested.token"));

        // A rule cannot alter property names.
        Assert.Equal("provider-key", JsonString(patched, "contents.1.parts.0.functionResponse.response.secret-property"));

        // Numbers keep their exact tokens.
        Assert.Equal("9007199254740993", JsonToken(patched, "contents.1.parts.0.functionResponse.response.sequence"));
        Assert.Equal("true", JsonToken(patched, "contents.1.parts.0.functionResponse.response.provider_meta.keep"));
    }

    [Fact]
    public void PatchesOllamaChatLayout()
    {
        // Port of TestPromptProtectedOllamaPayload's raw-layout half.
        const string body =
            """{"messages":[{"role":"user","content":"secret"},{"role":"tool","content":"secret"}],"options":{"num_ctx":8192}}""";

        var patched = Bytes(Patched(body, ApiFormats.OllamaChat, MaskRule("secret")));

        Assert.Equal("[MASKED]", JsonString(patched, "messages.0.content"));
        Assert.Equal("[MASKED]", JsonString(patched, "messages.1.content"));
        Assert.Equal("8192", JsonToken(patched, "options.num_ctx"));
    }

    [Fact]
    public void FailsWhenAProtectedRuleMatchesNoRawField()
    {
        // The fail-safe: a rule that matched the unified prompt but cannot be
        // located in the raw layout must not let the body be replayed.
        const string body = """{"messages":[{"role":"tool","content":"secret-user"}]}""";

        Assert.False(PassThroughPromptProtection.TryPatch(
            Bytes(body),
            ApiFormats.OpenAIChatCompletion,
            [MaskRule("secret-[a-z]+", scopes: PromptProtectionScopes.User)],
            out _,
            out var error));

        Assert.Equal(
            "matched prompt protection rules did not match a raw request text field",
            error);
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user"}]}""")]
    [InlineData("""{"messages":[{"role":"user","content":{"text":"secret"}}]}""")]
    public void FailsWhenAChatContentLayoutCannotBePatched(string body)
    {
        // A missing or non-text content has no native field to patch, so the
        // fail-safe no-match error keeps the generated body instead.
        Assert.False(PassThroughPromptProtection.TryPatch(
            Bytes(body),
            ApiFormats.OpenAIChatCompletion,
            [MaskRule("secret")],
            out _,
            out var error));

        Assert.Equal(
            "matched prompt protection rules did not match a raw request text field",
            error);
    }

    [Fact]
    public void FailsOnAnInvalidJsonBody()
    {
        Assert.False(PassThroughPromptProtection.TryPatch(
            Bytes("not json"),
            ApiFormats.OpenAIChatCompletion,
            [MaskRule("secret")],
            out _,
            out var error));

        Assert.Equal("invalid pass-through JSON body", error);
    }

    // ------------------------------------------------------------------
    // mergePassThroughRequestBodyWithPromptProtection
    // ------------------------------------------------------------------

    [Fact]
    public void MergeAppliesTheMappedModelAndMaskRules()
    {
        Assert.True(PassThrough.TryMergeRequestBodyWithPromptProtection(
            Bytes("""{"model":"alias","messages":[{"role":"user","content":"secret-user"}]}"""),
            ApiFormats.OpenAIChatCompletion,
            "provider-model",
            [MaskRule("secret-[a-z]+")],
            out var body,
            out var error));

        Assert.Null(error);
        Assert.Equal("provider-model", JsonString(body, "model"));
        Assert.Equal("[MASKED]", JsonString(body, "messages.0.content"));
    }

    [Fact]
    public void MergeWithoutRulesOnlyPatchesTheModel()
    {
        Assert.True(PassThrough.TryMergeRequestBodyWithPromptProtection(
            Bytes("""{"model":"alias","messages":[{"role":"user","content":"secret-user"}]}"""),
            ApiFormats.OpenAIChatCompletion,
            "provider-model",
            [],
            out var body,
            out var error));

        Assert.Null(error);
        Assert.Equal("provider-model", JsonString(body, "model"));
        Assert.Equal("secret-user", JsonString(body, "messages.0.content"));
    }
}

/// <summary>
/// Port of pass_through_prompt_protection_payload_test.go: real inbound and
/// outbound codecs, so a passing native patch cannot hide unprotected content
/// in the emitted provider payload.
/// </summary>
public sealed class PassThroughPromptProtectionPayloadTests
{
    private static PromptProtectionRule[] PayloadRules(params string[] scopes)
        =>
        [
            PassThroughPromptProtectionTests.MaskRule("secret-[a-z]+", "redacted", scopes),
            PassThroughPromptProtectionTests.MaskRule("redacted", "[MASKED]", scopes),
        ];

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed record Emitted(
        PersistentOutboundTransformer Outbound,
        HttpRequestData Request,
        byte[] Generated);

    /// <summary>
    /// Port of promptProtectionPayload: inbound transform, prompt protection,
    /// provider transform, then the pass-through body middleware.
    /// </summary>
    private static Emitted Run(
        IInboundTransformer inbound,
        string channelType,
        string body,
        IPromptProtecter protector)
    {
        var raw = new HttpRequestData
        {
            Method = "POST",
            Url = "http://gateway.test/v1",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json; charset=utf-8"],
            },
            ContentType = "application/json; charset=utf-8",
            Body = Bytes(body),
        };

        var request = inbound.TransformRequest(raw);
        // inbound.go:413 — the orchestration layer attaches the raw request.
        request.RawRequest = raw;

        var candidate = new ChannelCandidate
        {
            ChannelId = 1,
            ChannelName = "protected-payload",
            ActualModel = "mapped-model",
            Type = channelType,
            BaseUrl = "https://provider.example",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            PassThroughSettings = ChannelPassThroughSettings.Parse("{\"passThroughBody\":true}"),
        };

        var outbound = new PersistentOutboundTransformer(
            [candidate],
            TimeProvider.System,
            new RecordingMetricsRecorder());

        var protectedRequest = new ProtectPromptsMiddleware(outbound, inbound, protector)
            .TransformInboundLlmRequest(request);

        var generated = outbound.TransformRequest(protectedRequest);
        var fallback = (byte[])generated.Body!.Clone();

        var emitted = new PassThroughRequestBodyMiddleware(outbound, PassThroughSettings.Disabled)
            .TransformOutboundRawRequest(generated);

        return new Emitted(outbound, emitted, fallback);
    }

    private static IInboundTransformer InboundFor(string apiFormat) => apiFormat switch
    {
        ApiFormats.OpenAIChatCompletion => new OpenAiInboundTransformer(),
        ApiFormats.OpenAIResponse => new ResponsesInboundTransformer(),
        ApiFormats.OpenAIResponseCompact => new ResponsesCompactInboundTransformer(),
        ApiFormats.OpenAIDecisions => new DecisionsInboundTransformer(),
        ApiFormats.AnthropicMessage => new AnthropicInboundTransformer(),
        _ => throw new InvalidOperationException("unsupported payload test format: " + apiFormat),
    };

    private static string ChannelTypeFor(string apiFormat)
        => apiFormat == ApiFormats.AnthropicMessage ? "anthropic" : "openai";

    private sealed class PayloadCase
    {
        public string Name { get; init; } = string.Empty;

        public string Format { get; init; } = ApiFormats.OpenAIChatCompletion;

        public string Body { get; init; } = string.Empty;

        public string? Pattern { get; init; }

        public string[] Scopes { get; init; } = [];

        public string[] Masked { get; init; } = [];

        public Dictionary<string, string> Unchanged { get; init; } = [];
    }

    public static TheoryData<string> PayloadCaseNames => new()
    {
        "OpenAI chat strings and multimodal parts",
        "OpenAI Responses string input and instructions",
        "OpenAI Responses message text fallbacks",
        "OpenAI Responses tool outputs",
        "Responses Compact protects input and instructions",
        "Responses Compact role isolation",
        "Anthropic system and tool result arrays",
        "Anthropic tool scope leaves user text unchanged",
        "Decisions string input",
        "Decisions input text and image",
    };

    private static PayloadCase Case(string name) => name switch
    {
        "OpenAI chat strings and multimodal parts" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIChatCompletion,
            Body = """{"model":"alias","messages":[{"role":"system","content":"secret-system"},{"role":"user","content":"secret-user"},{"role":"user","content":[{"type":"text","text":"secret-part"},{"type":"image_url","image_url":{"url":"https://example.com/image.png"}}]}],"provider_option":{"keep":true}}""",
            Scopes = [PromptProtectionScopes.User],
            Masked = ["messages.1.content", "messages.2.content.0.text"],
            Unchanged = new Dictionary<string, string>
            {
                ["messages.0.content"] = "secret-system",
                ["messages.2.content.1.image_url.url"] = "https://example.com/image.png",
            },
        },
        "OpenAI Responses string input and instructions" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIResponse,
            Body = """{"model":"alias","instructions":"secret-system","input":"secret-user","provider_option":{"keep":true}}""",
            Masked = ["instructions", "input"],
        },
        "OpenAI Responses message text fallbacks" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIResponse,
            Body = """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"secret-content"}]},{"type":"message","role":"user","text":"secret-message"},{"role":"user","text":"secret-default"},{"type":"input_text","role":"user","text":"secret-flat"},{"type":"message","role":"user","content":null,"text":"secret-null"}],"provider_option":{"keep":true}}""",
            Masked = ["input.0.content.0.text", "input.1.text", "input.2.text", "input.3.text", "input.4.text"],
        },
        "OpenAI Responses tool outputs" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIResponse,
            Body = """{"model":"alias","input":[{"role":"user","content":"secret-user"},{"type":"function_call_output","call_id":"call_1","output":[{"type":"input_text","text":"secret-tool"}]},{"type":"custom_tool_call_output","call_id":"call_2","output":"secret-custom"}],"provider_option":{"keep":true}}""",
            Scopes = [PromptProtectionScopes.Tool],
            Masked = ["input.1.output.0.text", "input.2.output"],
            Unchanged = new Dictionary<string, string> { ["input.0.content"] = "secret-user" },
        },
        "Responses Compact protects input and instructions" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIResponseCompact,
            Body = """{"model":"alias","instructions":"secret-system","input":[{"role":"user","content":[{"type":"input_text","text":"secret-user"}]},{"role":"assistant","content":[{"type":"output_text","text":"secret-assistant"}]}],"provider_option":{"keep":true}}""",
            Masked = ["instructions", "input.0.content.0.text", "input.1.content.0.text"],
        },
        "Responses Compact role isolation" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIResponseCompact,
            Body = """{"model":"alias","instructions":"secret-system","input":[{"role":"user","content":"secret-user"},{"role":"assistant","content":"secret-assistant"}],"provider_option":{"keep":true}}""",
            Scopes = [PromptProtectionScopes.User],
            Masked = ["input.0.content"],
            Unchanged = new Dictionary<string, string>
            {
                ["instructions"] = "secret-system",
                ["input.1.content"] = "secret-assistant",
            },
        },
        "Anthropic system and tool result arrays" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.AnthropicMessage,
            Body = """{"model":"alias","max_tokens":64,"system":[{"type":"text","text":"secret-system"}],"messages":[{"role":"user","content":[{"type":"text","text":"secret-user"},{"type":"tool_result","tool_use_id":"call_1","content":[{"type":"text","text":"secret-tool"}]}]}],"provider_option":{"keep":true}}""",
            Masked = ["system.0.text", "messages.0.content.0.text", "messages.0.content.1.content.0.text"],
        },
        "Anthropic tool scope leaves user text unchanged" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.AnthropicMessage,
            Body = """{"model":"alias","max_tokens":64,"messages":[{"role":"user","content":[{"type":"text","text":"secret-user"},{"type":"tool_result","tool_use_id":"call_1","content":"secret-tool"}]}],"provider_option":{"keep":true}}""",
            Scopes = [PromptProtectionScopes.Tool],
            Masked = ["messages.0.content.1.content"],
            Unchanged = new Dictionary<string, string> { ["messages.0.content.0.text"] = "secret-user" },
        },
        "Decisions string input" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIDecisions,
            Body = """{"model":"alias","input":"secret-user","questions":[{"type":"choice","name":"kind"}],"x_beta":{"keep":true},"provider_option":{"keep":true}}""",
            Masked = ["input"],
        },
        "Decisions input text and image" => new PayloadCase
        {
            Name = name,
            Format = ApiFormats.OpenAIDecisions,
            Body = """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"secret-user"},{"type":"input_image","image_url":"data:image/png;base64,secret"}]}],"questions":[{}],"provider_option":{"keep":true}}""",
            Masked = ["input.0.content.0.text"],
            Unchanged = new Dictionary<string, string>
            {
                ["input.0.content.1.image_url"] = "data:image/png;base64,secret",
            },
        },
        _ => throw new InvalidOperationException("unknown payload case: " + name),
    };

    [Theory]
    [MemberData(nameof(PayloadCaseNames))]
    public void RealCodecsEmitOnlyProtectedPrompts(string caseName)
    {
        // Port of TestPromptProtectedPassThroughPayload.
        var testCase = Case(caseName);
        var rules = PayloadRules(testCase.Scopes);
        var protector = new PromptProtectionRuleProtector(rules);

        var emitted = Run(
            InboundFor(testCase.Format),
            ChannelTypeFor(testCase.Format),
            testCase.Body,
            protector);

        Assert.True(emitted.Outbound.PassThroughApplied);

        foreach (var path in testCase.Masked)
        {
            Assert.Equal(
                "[MASKED]",
                PassThroughPromptProtectionTests.JsonString(emitted.Request.Body!, path));
        }

        foreach (var (path, expected) in testCase.Unchanged)
        {
            Assert.Equal(
                expected,
                PassThroughPromptProtectionTests.JsonString(emitted.Request.Body!, path));
        }

        Assert.Equal(
            "true",
            PassThroughPromptProtectionTests.JsonToken(emitted.Request.Body!, "provider_option.keep"));
        Assert.Equal(
            "mapped-model",
            PassThroughPromptProtectionTests.JsonString(emitted.Request.Body!, "model"));
        Assert.Equal("application/json; charset=utf-8", emitted.Request.ContentType);
        Assert.Equal(
            "application/json; charset=utf-8",
            HttpClientUtils.HeaderGet(emitted.Request.Headers, "Content-Type"));

        // The stored raw request keeps the client's original body.
        Assert.Equal(testCase.Body, Encoding.UTF8.GetString(emitted.Outbound.CurrentLlmRequest!.RawRequest!.Body!));
    }

    // ------------------------------------------------------------------
    // Fallbacks: partial native matches and legacy protectors
    // ------------------------------------------------------------------

    public static TheoryData<string> FallbackCaseNames => new()
    {
        "Legacy Protect changes content without reporting rules",
    };

    private sealed class LegacyProtecter(PromptProtectionRule[] rules) : IPromptProtecter
    {
        /// <summary>
        /// Port of legacyPayloadPromptProtecter: a replacement request without
        /// raw metadata or matched rules.
        /// </summary>
        public LlmRequest? Protect(LlmRequest request)
        {
            var cloned = request.ShallowClone();
            cloned.RawRequest = null;
            cloned.Messages = [.. request.Messages.Select(static message => message.ShallowClone())];

            var result = PromptProtectionRules.Apply(cloned, rules);
            if (result.Rejected)
            {
                throw new PromptProtectionRejectedException();
            }

            return result.Request;
        }
    }

    [Theory]
    [MemberData(nameof(FallbackCaseNames))]
    public void LegacyProtectorCannotRestorePlaintext(string caseName)
    {
        // Port of TestPromptProtectedPassThroughFallback's legacy case.
        _ = caseName;
        const string body = """{"model":"alias","messages":[{"role":"user","content":"secret-user"}]}""";
        var rules = new[] { PassThroughPromptProtectionTests.MaskRule("secret-user") };

        var emitted = Run(
            new OpenAiInboundTransformer(),
            "openai",
            body,
            new LegacyProtecter(rules));

        Assert.False(emitted.Outbound.PassThroughApplied);
        Assert.Equal(emitted.Generated, emitted.Request.Body);

        var emittedText = Encoding.UTF8.GetString(emitted.Request.Body!);
        Assert.DoesNotContain("secret-user", emittedText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-", emittedText, StringComparison.Ordinal);
        Assert.Contains("[MASKED]", emittedText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PromptProtectionScopes.System)]
    [InlineData(PromptProtectionScopes.User)]
    public void CompactRejectBlocksBeforeAProviderPayloadIsBuilt(string scope)
    {
        // Port of TestPromptProtectedCompactReject.
        var protector = new PromptProtectionRuleProtector(
        [
            new PromptProtectionRule
            {
                Pattern = "secret",
                Settings = new PromptProtectionSettings
                {
                    Action = PromptProtectionActions.Reject,
                    Scopes = [scope],
                },
            },
        ]);
        var inbound = new ResponsesCompactInboundTransformer();
        var raw = new HttpRequestData
        {
            Body = """{"model":"alias","instructions":"secret-system","input":[{"role":"user","content":"secret-user"}]}"""u8.ToArray(),
        };
        var request = inbound.TransformRequest(raw);
        request.RawRequest = raw;

        var error = Assert.Throws<TransformerException>(
            () => new ProtectPromptsMiddleware(
                    new PersistentOutboundTransformer([], TimeProvider.System, new RecordingMetricsRecorder()),
                    inbound,
                    protector)
                .TransformInboundLlmRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Contains(ProtectPromptsMiddleware.RejectedMessage, error.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// Port of orchestrator.promptProtectionBodyCheck (prompt_protection.go): the
/// replay must decode back to exactly the prompts prompt protection produced.
/// </summary>
public sealed class PromptProtectionBodyCheckTests
{
    private sealed class EchoInbound(Func<LlmRequest> replayed) : IInboundTransformer
    {
        public LlmRequest TransformRequest(HttpRequestData? request)
        {
            _ = request;
            return replayed();
        }

        public HttpResponseData TransformResponse(LlmResponse? response)
            => throw new NotSupportedException();

        public IStream<StreamEvent> TransformStream(
            IStream<LlmResponse> stream)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();

        public HttpResponseData TransformError(Exception? error)
            => throw new NotSupportedException();
    }

    private static HttpRequestData Raw() => new() { Body = "{}"u8.ToArray() };

    [Fact]
    public void MissingInboundMappingIsReported()
    {
        var check = new PromptProtectionBodyCheck(
            inbound: null,
            raw: Raw(),
            expected: []);

        Assert.Equal(
            "protected pass-through body has no inbound mapping",
            check.Validate("{}"u8.ToArray()));
    }

    [Fact]
    public void DecodeFailureIsSanitized()
    {
        var inbound = new ThrowingInbound();
        var check = new PromptProtectionBodyCheck(inbound, Raw(), []);

        Assert.Equal(
            "protected pass-through body cannot be decoded by inbound mapping",
            check.Validate("secret-user"u8.ToArray()));
    }

    [Fact]
    public void MatchingPromptsPassValidation()
    {
        var replayed = Replay("[MASKED]");
        var check = new PromptProtectionBodyCheck(
            new EchoInbound(() => replayed),
            Raw(),
            PromptProtectionTexts.Capture(replayed));

        Assert.Null(check.Validate("{}"u8.ToArray()));
    }

    [Fact]
    public void DifferentPromptsFailValidation()
    {
        var replayed = Replay("secret-user");
        var check = new PromptProtectionBodyCheck(
            new EchoInbound(() => replayed),
            Raw(),
            PromptProtectionTexts.Capture(Replay("[MASKED]")));

        Assert.Equal(
            "pass-through prompt values differ from protected request",
            check.Validate("{}"u8.ToArray()));
    }

    private static LlmRequest Replay(string content)
        => new()
        {
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText(content) },
            ],
        };

    private sealed class ThrowingInbound : IInboundTransformer
    {
        public LlmRequest TransformRequest(HttpRequestData? request)
            => throw new InvalidOperationException("decode secret-user");

        public HttpResponseData TransformResponse(LlmResponse? response)
            => throw new NotSupportedException();

        public IStream<StreamEvent> TransformStream(
            IStream<LlmResponse> stream)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();

        public HttpResponseData TransformError(Exception? error)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// The prompt-protection path through the real orchestrator: the rules load
/// once per request, the inbound middleware masks the unified prompt, and the
/// pass-through replay is patched before the request reaches the transport.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class PromptProtectionOrchestrationTests(GrainClusterFixture fixture)
{
    private static RetryPolicy Policy()
        => new()
        {
            Enabled = true,
            MaxChannelRetries = 3,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
        };

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder recorder,
        IReadOnlyList<PromptProtectionRule> rules)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(Policy()),
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            passThroughSettings: new StaticPassThroughSettingsProvider(PassThroughSettings.Disabled),
            promptProtectionRules: new StaticPromptProtectionRuleProvider(rules));

    private static ChannelDescriptor MappedChannel(long id)
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-mapped\"}",
            SupportedModels = "[\"upstream-pass\"]",
            Settings = "{\"modelMappings\":[{\"from\":\"client-pass\",\"to\":\"upstream-pass\"}],"
                + "\"passThroughBody\":true}",
        };

    private static HttpRequestData ClientRequest()
    {
        var request = TestChannels.ChatRequest("client-pass");
        request.Body = Encoding.UTF8.GetBytes(
            """{"model":"client-pass","messages":[{"role":"user","content":"token is secret-user"}],"future_provider_field":{"enabled":true}}""");
        return request;
    }

    [Fact]
    public async Task MaskedPassThroughBodyReachesTheTransport()
    {
        fixture.Channels.Set(MappedChannel(5401));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var rules = new[]
        {
            PassThroughPromptProtectionTests.MaskRule("secret-[a-z]+"),
        };

        await NewOrchestrator(executor, recorder, rules).ProcessAsync(ClientRequest());

        var sent = Assert.Single(executor.Requests);
        Assert.Equal(
            "token is [MASKED]",
            PassThroughPromptProtectionTests.JsonString(sent.Body!, "messages.0.content"));
        Assert.Equal("upstream-pass", PassThroughPromptProtectionTests.JsonString(sent.Body!, "model"));

        // The replayed body still carries the client's provider fields.
        Assert.Equal(
            "true",
            PassThroughPromptProtectionTests.JsonToken(sent.Body!, "future_provider_field.enabled"));

        var execution = Assert.Single(recorder.Executions);
        Assert.True(execution.PassThroughApplied);
    }

    [Fact]
    public async Task RejectedPromptNeverReachesTheTransport()
    {
        fixture.Channels.Set(MappedChannel(5402));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };
        var rules = new[]
        {
            new PromptProtectionRule
            {
                Pattern = "secret-[a-z]+",
                Settings = new PromptProtectionSettings
                {
                    Action = PromptProtectionActions.Reject,
                    Scopes = [PromptProtectionScopes.User],
                },
            },
        };

        var error = await Assert.ThrowsAsync<TransformerException>(
            () => NewOrchestrator(executor, new RecordingRequestChainRecorder(), rules)
                .ProcessAsync(ClientRequest()));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Contains(ProtectPromptsMiddleware.RejectedMessage, error.Message, StringComparison.Ordinal);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task ProviderLoadFailureFailsOpen()
    {
        fixture.Channels.Set(MappedChannel(5403));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };

        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(Policy()),
            TimeProvider.System,
            new RecordingRequestChainRecorder(),
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            passThroughSettings: new StaticPassThroughSettingsProvider(PassThroughSettings.Disabled),
            promptProtectionRules: new FailingRuleProvider());

        await orchestrator.ProcessAsync(ClientRequest());

        // A rule load failure is logged in the reference and the request
        // continues unprotected (biz.PromptProtectionRuleService.Protect).
        var sent = Assert.Single(executor.Requests);
        Assert.Equal(
            "token is secret-user",
            PassThroughPromptProtectionTests.JsonString(sent.Body!, "messages.0.content"));
    }

    private sealed class FailingRuleProvider : IPromptProtectionRuleProvider
    {
        public Task<IReadOnlyList<PromptProtectionRule>> ListEnabledAsync(
            CancellationToken cancellationToken = default)
            => Task.FromException<IReadOnlyList<PromptProtectionRule>>(
                new InvalidOperationException("rules unavailable"));
    }
}
