using AxonHub.Api.Orchestration;
using AxonHub.Api.PromptProtection;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/biz/prompt_protection_request_test.go: the ordered
/// rule loop shared by the unified request path and the raw body patcher.
/// </summary>
public sealed class PromptProtectionRuleApplicationTests
{
    private static PromptProtectionRule Rule(
        string name,
        string pattern,
        string action = PromptProtectionActions.Mask,
        string replacement = "[MASKED]",
        params string[] scopes)
        => new()
        {
            Name = name,
            Pattern = pattern,
            Settings = new PromptProtectionSettings
            {
                Action = action,
                Replacement = replacement,
                Scopes = scopes.Length == 0 ? null : [.. scopes],
            },
        };

    private static LlmRequest Request(params Message[] messages)
        => new() { Messages = [.. messages] };

    private static Message User(string content)
        => new() { Role = "user", Content = MessageContent.FromText(content) };

    [Fact]
    public void MaskContent()
    {
        // Port of TestApplyPromptProtectionRulesMaskContent.
        var request = Request(User("token is secret-123"));

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("mask-secret", "secret-[0-9]+", scopes: PromptProtectionScopes.User)]);

        Assert.False(result.Rejected);
        var matched = Assert.Single(result.MatchedRules);
        Assert.Same(request, result.Request);
        Assert.Equal("mask-secret", matched.Name);
        Assert.Equal("token is [MASKED]", request.Messages[0].Content!.Content);
        Assert.Equal("token is [MASKED]", result.Request!.Messages[0].Content!.Content);
    }

    [Fact]
    public void RejectContent()
    {
        // Port of TestApplyPromptProtectionRulesRejectContent.
        var request = Request(User("contains secret"));

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("reject-secret", "secret", PromptProtectionActions.Reject, scopes: PromptProtectionScopes.User)]);

        Assert.True(result.Rejected);
        var matched = Assert.Single(result.MatchedRules);
        Assert.Null(result.Request);
        Assert.Equal("reject-secret", matched.Name);
    }

    [Fact]
    public void ScopeFiltering()
    {
        // Port of TestApplyPromptProtectionRulesScopeFiltering.
        var request = Request(new Message
        {
            Role = "assistant",
            Content = MessageContent.FromText("contains secret"),
        });

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("user-only", "secret", scopes: PromptProtectionScopes.User)]);

        Assert.False(result.Rejected);
        Assert.Empty(result.MatchedRules);
        Assert.Same(request, result.Request);
        Assert.Equal("contains secret", request.Messages[0].Content!.Content);
    }

    [Fact]
    public void MaskMultipleContent()
    {
        // Port of TestApplyPromptProtectionRulesMaskMultipleContent.
        var request = Request(new Message
        {
            Role = "user",
            Content = MessageContent.FromParts([new MessageContentPart { Type = "text", Text = "secret text" }]),
        });

        var result = PromptProtectionRules.Apply(request, [Rule("mask-part", "secret")]);

        Assert.False(result.Rejected);
        Assert.Equal("mask-part", Assert.Single(result.MatchedRules).Name);
        var part = Assert.Single(request.Messages[0].Content!.MultipleContent!);
        Assert.Equal("[MASKED] text", part.Text);
    }

    [Fact]
    public void AppliesMultipleRulesInOrder()
    {
        // Port of TestApplyPromptProtectionRules_AppliesMultipleRulesInOrder.
        var request = Request(User("alice secret"));

        var result = PromptProtectionRules.Apply(
            request,
            [
                Rule("mask-name", "alice", replacement: "[USER]"),
                Rule("mask-secret", "secret"),
            ]);

        Assert.False(result.Rejected);
        Assert.Equal(2, result.MatchedRules.Count);
        Assert.Same(request, result.Request);
        Assert.Equal("[USER] [MASKED]", request.Messages[0].Content!.Content);
        Assert.Equal("mask-name", result.MatchedRules[0].Name);
        Assert.Equal("mask-secret", result.MatchedRules[1].Name);
    }

    [Fact]
    public void RejectAfterEarlierMask()
    {
        // Port of TestApplyPromptProtectionRules_RejectAfterEarlierMask.
        var request = Request(User("secret"));

        var result = PromptProtectionRules.Apply(
            request,
            [
                Rule("mask-secret", "secret"),
                Rule("reject-masked", @"\[MASKED\]", PromptProtectionActions.Reject),
            ]);

        Assert.True(result.Rejected);
        Assert.Equal("reject-masked", Assert.Single(result.MatchedRules).Name);
        Assert.Null(result.Request);
    }

    [Fact]
    public void NoMatchReturnsOriginalRequest()
    {
        // Port of TestApplyPromptProtectionRules_NoMatchReturnsOriginalRequest.
        var request = Request(User("hello world"));

        var result = PromptProtectionRules.Apply(request, [Rule("mask-secret", "secret")]);

        Assert.False(result.Rejected);
        Assert.Empty(result.MatchedRules);
        Assert.Same(request, result.Request);
    }

    [Fact]
    public void RuleWithoutSettingsIsSkipped()
    {
        // Ent stores a nil jsonb settings column as nil; such rules never apply.
        var request = Request(User("contains secret"));

        var result = PromptProtectionRules.Apply(
            request,
            [new PromptProtectionRule { Name = "no-settings", Pattern = "secret" }]);

        Assert.False(result.Rejected);
        Assert.Empty(result.MatchedRules);
        Assert.Same(request, result.Request);
    }

    [Fact]
    public void EmptyRequestOrRulesReturnRequest()
    {
        // The reference guards `req == nil || len(req.Messages) == 0 || len(rules) == 0`.
        var empty = new LlmRequest();

        Assert.Same(
            empty,
            PromptProtectionRules.Apply(empty, [Rule("mask-secret", "secret")]).Request);
        Assert.Null(PromptProtectionRules.Apply(null, [Rule("mask-secret", "secret")]).Request);

        var request = Request(User("secret"));
        var result = PromptProtectionRules.Apply(request, []);
        Assert.Same(request, result.Request);
        Assert.Empty(result.MatchedRules);
    }

    [Fact]
    public void UncompilablePatternNeverMatches()
    {
        // biz.MatchPromptProtectionRule returns false when regexp2 cannot compile.
        var request = Request(User("contains secret"));

        var result = PromptProtectionRules.Apply(request, [Rule("broken", "secret(")]);

        Assert.False(result.Rejected);
        Assert.Empty(result.MatchedRules);
        Assert.Equal("contains secret", request.Messages[0].Content!.Content);
    }

    // ------------------------------------------------------------------
    // Decisions (ApplyPromptProtectionRules' decisions branch)
    // ------------------------------------------------------------------

    private static LlmRequest Decisions(string body)
        => new()
        {
            ApiFormat = ApiFormats.OpenAIDecisions,
            Decisions = new DecisionsRequest
            {
                Body = System.Text.Encoding.UTF8.GetBytes(body),
            },
        };

    [Fact]
    public void DecisionsStringInputIsMasked()
    {
        var request = Decisions("""{"model":"alias","input":"secret-user"}""");

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("mask-secret", "secret-[a-z]+", scopes: PromptProtectionScopes.User)]);

        Assert.False(result.Rejected);
        Assert.Single(result.MatchedRules);
        var body = System.Text.Encoding.UTF8.GetString(request.Decisions!.Body);
        Assert.Equal("""{"model":"alias","input":"[MASKED]"}""", body);
    }

    [Fact]
    public void DecisionsInputTextPartsAreMaskedByRole()
    {
        var request = Decisions(
            """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"secret-user"},{"type":"input_image","image_url":"data:image/png;base64,secret"}]},{"type":"message","role":"assistant","content":[{"type":"input_text","text":"secret-assistant"}]}]}""");

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("mask-secret", "secret-[a-z]+", scopes: PromptProtectionScopes.User)]);

        Assert.False(result.Rejected);
        Assert.Single(result.MatchedRules);
        var body = System.Text.Encoding.UTF8.GetString(request.Decisions!.Body);
        Assert.Contains("\"text\":\"[MASKED]\"", body, StringComparison.Ordinal);
        Assert.Contains("\"image_url\":\"data:image/png;base64,secret\"", body, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"secret-assistant\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionsRejectKeepsTheRequest()
    {
        // ApplyDecisions, unlike the messages branch, returns the request
        // pointer alongside the rejecting rule.
        var request = Decisions("""{"model":"alias","input":"secret-user"}""");

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("reject-secret", "secret", PromptProtectionActions.Reject)]);

        Assert.True(result.Rejected);
        Assert.Equal("reject-secret", Assert.Single(result.MatchedRules).Name);
        Assert.Same(request, result.Request);
    }

    // ------------------------------------------------------------------
    // Compact (applyCompactPromptProtectionRules)
    // ------------------------------------------------------------------

    private static LlmRequest Compact(string? instructions, params Message[] input)
        => new()
        {
            ApiFormat = ApiFormats.OpenAIResponseCompact,
            Compact = new CompactRequest { Instructions = instructions, Input = [.. input] },
        };

    [Fact]
    public void CompactMasksInstructionsAndInput()
    {
        var request = Compact(
            "secret-system",
            new Message { Role = "user", Content = MessageContent.FromText("secret-user") },
            new Message { Role = "assistant", Content = MessageContent.FromText("secret-assistant") });

        var result = PromptProtectionRules.Apply(request, [Rule("mask-secret", "secret-[a-z]+")]);

        Assert.False(result.Rejected);
        Assert.Single(result.MatchedRules);
        Assert.Same(request, result.Request);
        Assert.Equal("[MASKED]", request.Compact!.Instructions);
        Assert.Equal("[MASKED]", request.Compact.Input![0].Content!.Content);
        Assert.Equal("[MASKED]", request.Compact.Input[1].Content!.Content);
        // The unified Messages slice stays empty: Compact carries the prompts.
        Assert.Empty(request.Messages);
    }

    [Fact]
    public void CompactRoleIsolation()
    {
        var request = Compact(
            "secret-system",
            new Message { Role = "user", Content = MessageContent.FromText("secret-user") },
            new Message { Role = "assistant", Content = MessageContent.FromText("secret-assistant") });

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("mask-secret", "secret-[a-z]+", scopes: PromptProtectionScopes.User)]);

        Assert.False(result.Rejected);
        Assert.Single(result.MatchedRules);
        Assert.Equal("secret-system", request.Compact!.Instructions);
        Assert.Equal("[MASKED]", request.Compact.Input![0].Content!.Content);
        Assert.Equal("secret-assistant", request.Compact.Input[1].Content!.Content);
    }

    [Theory]
    [InlineData(PromptProtectionScopes.System)]
    [InlineData(PromptProtectionScopes.User)]
    public void CompactRejectCoversInstructionsAndInput(string scope)
    {
        // Port of TestPromptProtectedCompactReject's rule shape: the system
        // scope reaches instructions, the user scope reaches the input.
        var request = Compact(
            "secret-system",
            new Message { Role = "user", Content = MessageContent.FromText("secret-user") });

        var result = PromptProtectionRules.Apply(
            request,
            [Rule("reject-secret", "secret", PromptProtectionActions.Reject, scopes: scope)]);

        Assert.True(result.Rejected);
        Assert.Equal("reject-secret", Assert.Single(result.MatchedRules).Name);
        Assert.Null(result.Request);
    }

    [Fact]
    public void CompactInstructionsOnly()
    {
        var request = Compact("secret-system");

        var result = PromptProtectionRules.Apply(request, [Rule("mask-secret", "secret-[a-z]+")]);

        Assert.False(result.Rejected);
        Assert.Equal("[MASKED]", request.Compact!.Instructions);
        Assert.Empty(request.Compact.Input!);
    }
}

/// <summary>
/// Port of internal/server/orchestrator/prompt_protection_test.go plus the
/// legacy-protector migration: rejection maps to the fixed invalid-request
/// error, mask rules are recorded for raw replay, and the body check is armed
/// whenever the prompt text changed without a matching rule set.
/// </summary>
public sealed class ProtectPromptsMiddlewareTests
{
    /// <summary>Port of stubPromptProtecter: both contracts, configurable result.</summary>
    private sealed class StubProtecter : IPromptProtecter, IPromptProtectionResultProvider
    {
        public LlmRequest? Result { get; set; }

        public IReadOnlyList<PromptProtectionRule> Rules { get; set; } = [];

        public bool Reject { get; set; }

        public Exception? Error { get; set; }

        public LlmRequest? Protect(LlmRequest request) => ProtectWithResult(request).Request;

        public PromptProtectionResult ProtectWithResult(LlmRequest request)
        {
            _ = request;
            if (Error is not null)
            {
                throw Error;
            }

            if (Reject)
            {
                throw new PromptProtectionRejectedException();
            }

            return new PromptProtectionResult { Request = Result, MatchedRules = Rules };
        }
    }

    /// <summary>A protector that predates the result-provider contract.</summary>
    private sealed class LegacyProtecter(Func<LlmRequest, LlmRequest?> protect) : IPromptProtecter
    {
        public LlmRequest? Protect(LlmRequest request) => protect(request);
    }

    /// <summary>The middleware only stores the inbound mapping; it is never replayed here.</summary>
    private sealed class StubInbound : IInboundTransformer
    {
        public LlmRequest TransformRequest(HttpRequestData? request)
        {
            _ = request;
            return new LlmRequest();
        }

        public HttpResponseData TransformResponse(LlmResponse? response)
            => throw new NotSupportedException();

        public IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
            => throw new NotSupportedException();

        public HttpResponseData TransformError(Exception? error)
            => throw new NotSupportedException();
    }

    private static PersistentOutboundTransformer Outbound()
        => new([], TimeProvider.System, new RecordingMetricsRecorder());

    private static Message User(string content)
        => new() { Role = "user", Content = MessageContent.FromText(content) };

    [Fact]
    public void MasksContent()
    {
        // Port of TestProtectPromptsMaskContent.
        var protecter = new StubProtecter
        {
            Result = new LlmRequest { Messages = [User("token is [MASKED]")] },
        };
        var middleware = new ProtectPromptsMiddleware(Outbound(), new StubInbound(), protecter);
        var request = new LlmRequest { Messages = [User("token is secret-123")] };

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.NotSame(request, result);
        Assert.Equal("token is [MASKED]", result.Messages[0].Content!.Content);
        Assert.Equal("token is secret-123", request.Messages[0].Content!.Content);
    }

    [Fact]
    public void RejectsContentWithTheFixedMessage()
    {
        // Port of TestProtectPromptsRejectContent.
        var protecter = new StubProtecter { Reject = true };
        var middleware = new ProtectPromptsMiddleware(Outbound(), new StubInbound(), protecter);
        var request = new LlmRequest { Messages = [User("contains secret")] };

        var error = Assert.Throws<TransformerException>(
            () => middleware.TransformInboundLlmRequest(request));

        Assert.Equal(TransformerErrorKind.InvalidRequest, error.Kind);
        Assert.Contains(ProtectPromptsMiddleware.RejectedMessage, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reject-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NilResultKeepsTheOriginalRequest()
    {
        // Port of TestProtectPromptsScopeFiltering: a nil result with no error
        // keeps the request the middleware was handed.
        var middleware = new ProtectPromptsMiddleware(
            Outbound(),
            new StubInbound(),
            new StubProtecter { Result = null });
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "assistant", Content = MessageContent.FromText("contains secret") }],
        };

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Same(request, result);
        Assert.Equal("contains secret", result.Messages[0].Content!.Content);
    }

    [Fact]
    public void MasksMultipleContent()
    {
        // Port of TestProtectPromptsMaskMultipleContent.
        var protecter = new StubProtecter
        {
            Result = new LlmRequest
            {
                Messages =
                [
                    new Message
                    {
                        Role = "user",
                        Content = MessageContent.FromParts(
                            [new MessageContentPart { Type = "text", Text = "[MASKED] text" }]),
                    },
                ],
            },
        };
        var middleware = new ProtectPromptsMiddleware(Outbound(), new StubInbound(), protecter);
        var request = new LlmRequest
        {
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                        [new MessageContentPart { Type = "text", Text = "secret text" }]),
                },
            ],
        };

        var result = middleware.TransformInboundLlmRequest(request);

        var part = Assert.Single(result.Messages[0].Content!.MultipleContent!);
        Assert.Equal("[MASKED] text", part.Text);
        Assert.Equal("secret text", request.Messages[0].Content!.MultipleContent![0].Text);
    }

    [Fact]
    public void RecordsMaskRulesForRawReplay()
    {
        // Port of TestProtectPromptsRecordsMaskRules.
        var rule = new PromptProtectionRule { Name = "mask-secret" };
        var outbound = Outbound();
        var protecter = new StubProtecter
        {
            Result = new LlmRequest(),
            Rules = [rule],
        };
        var middleware = new ProtectPromptsMiddleware(outbound, new StubInbound(), protecter);

        var result = middleware.TransformInboundLlmRequest(new LlmRequest());

        Assert.NotNull(result);
        Assert.Same(rule, Assert.Single(outbound.PromptProtectionMaskRules));
    }

    [Fact]
    public void KeepsTheInboundRawRequest()
    {
        // A replacement request without transport metadata keeps the inbound
        // source so the raw body remains replayable.
        var raw = new HttpRequestData
        {
            Method = "POST",
            Url = "http://gateway.test/v1",
            Body = "[1]"u8.ToArray(),
        };
        var protecter = new StubProtecter { Result = new LlmRequest() };
        var middleware = new ProtectPromptsMiddleware(Outbound(), new StubInbound(), protecter);
        var request = new LlmRequest { Messages = [User("secret")], RawRequest = raw };

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Same(raw, result.RawRequest);
    }

    [Fact]
    public void LegacyProtectorArmsTheRawReplayCheck()
    {
        // A protector that changes prompts without reporting rules must not let
        // the unmodified raw body be replayed.
        var outbound = Outbound();
        var protecter = new LegacyProtecter(request =>
        {
            var clone = request.ShallowClone();
            clone.RawRequest = null;
            clone.Messages = [.. request.Messages.Select(static message => message.ShallowClone())];
            clone.Messages[0].Content = MessageContent.FromText("token is [MASKED]");
            return clone;
        });
        var middleware = new ProtectPromptsMiddleware(outbound, new StubInbound(), protecter);
        var request = new LlmRequest
        {
            Messages = [User("token is secret-123")],
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            RawRequest = new HttpRequestData
            {
                Body = """{"messages":[{"role":"user","content":"token is secret-123"}]}"""u8.ToArray(),
            },
        };

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Empty(outbound.PromptProtectionMaskRules);
        Assert.NotNull(outbound.PromptProtectionBodyCheck);
        Assert.Equal("token is [MASKED]", result.Messages[0].Content!.Content);
        Assert.Same(request.RawRequest, result.RawRequest);
    }

    [Fact]
    public void UnchangedPromptsDoNotArmTheRawReplayCheck()
    {
        var outbound = Outbound();
        var protecter = new LegacyProtecter(static request => request);
        var middleware = new ProtectPromptsMiddleware(outbound, new StubInbound(), protecter);
        var request = new LlmRequest
        {
            Messages = [User("hello")],
            RawRequest = new HttpRequestData { Body = "{}"u8.ToArray() },
        };

        _ = middleware.TransformInboundLlmRequest(request);

        Assert.Null(outbound.PromptProtectionBodyCheck);
    }

    [Fact]
    public void DecisionsSkipTheRawReplayCheck()
    {
        // The decisions body is its own raw payload; the raw patcher guards it.
        var outbound = Outbound();
        var protecter = new StubProtecter
        {
            Result = new LlmRequest { ApiFormat = ApiFormats.OpenAIDecisions },
            Rules = [new PromptProtectionRule { Name = "mask-secret" }],
        };
        var middleware = new ProtectPromptsMiddleware(outbound, new StubInbound(), protecter);

        _ = middleware.TransformInboundLlmRequest(new LlmRequest { ApiFormat = ApiFormats.OpenAIDecisions });

        Assert.Single(outbound.PromptProtectionMaskRules);
        Assert.Null(outbound.PromptProtectionBodyCheck);
    }

    [Fact]
    public void NonRejectionFailureKeepsTheOriginalRequest()
    {
        // Provider failures are logged and the request continues unprotected.
        var protecter = new StubProtecter { Error = new InvalidOperationException("boom") };
        var middleware = new ProtectPromptsMiddleware(Outbound(), new StubInbound(), protecter);
        var request = new LlmRequest { Messages = [User("secret")] };

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Same(request, result);
    }

    [Fact]
    public void MissingProtecterIsANoOp()
    {
        var middleware = new ProtectPromptsMiddleware(Outbound(), new StubInbound(), protecter: null);
        var request = new LlmRequest { Messages = [User("secret")] };

        Assert.Same(request, middleware.TransformInboundLlmRequest(request));
    }
}

/// <summary>
/// Port of the rule service read side: enabled rules mapped from the jsonb
/// settings column, fail-open snapshot construction, and rejection semantics.
/// </summary>
public sealed class PromptProtectionRuleServiceTests
{
    private sealed class FakeRuleStore : IPromptProtectionRuleStore
    {
        public List<PromptProtectionRuleRecord> Rules { get; } = [];

        public Exception? Failure { get; set; }

        public Task<long> CreateAsync(PromptProtectionRuleRecord rule, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PromptProtectionRuleRecord>> ListEnabledAsync(
            CancellationToken cancellationToken = default)
            => Failure is not null
                ? Task.FromException<IReadOnlyList<PromptProtectionRuleRecord>>(Failure)
                : Task.FromResult<IReadOnlyList<PromptProtectionRuleRecord>>(Rules);
    }

    private static PromptProtectionRuleRecord Record(long id, string settings)
        => new()
        {
            Id = id,
            Name = "rule-" + id,
            Pattern = "secret",
            Status = "enabled",
            Settings = settings,
        };

    [Fact]
    public async Task MapsRecordsToEnabledRules()
    {
        var store = new FakeRuleStore();
        store.Rules.Add(Record(7, """{"action":"mask","replacement":"[MASKED]","scopes":["user"]}"""));

        var rules = await new PromptProtectionRuleService(store).ListEnabledAsync();

        var rule = Assert.Single(rules);
        Assert.Equal(7, rule.Id);
        Assert.Equal("rule-7", rule.Name);
        Assert.Equal(PromptProtectionActions.Mask, rule.Settings!.Action);
        Assert.Equal("[MASKED]", rule.Settings.Replacement);
        Assert.Equal(PromptProtectionScopes.User, Assert.Single(rule.Settings!.Scopes!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public async Task BlankOrNullSettingsMapToNil(string settings)
    {
        // Ent stores a nil settings column as null; the rule service keeps it
        // nil and ApplyPromptProtectionRules skips the rule.
        var store = new FakeRuleStore();
        store.Rules.Add(Record(1, settings));

        var rules = await new PromptProtectionRuleService(store).ListEnabledAsync();

        Assert.Null(Assert.Single(rules).Settings);
    }

    [Fact]
    public async Task InvalidSettingsFailTheLoad()
    {
        var store = new FakeRuleStore();
        store.Rules.Add(Record(1, "not json"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => new PromptProtectionRuleService(store).ListEnabledAsync());
    }

    [Fact]
    public async Task StoreFailureFailsTheLoad()
    {
        var store = new FakeRuleStore { Failure = new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PromptProtectionRuleService(store).ListEnabledAsync());
    }

    [Fact]
    public void EmptySnapshotReturnsTheRequestUnchanged()
    {
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("secret") }],
        };

        var result = new PromptProtectionRuleProtector([]).ProtectWithResult(request);

        Assert.Same(request, result.Request);
        Assert.Empty(result.MatchedRules);
        Assert.False(result.Rejected);
    }

    [Fact]
    public void AppliesMaskRules()
    {
        var rules = new List<PromptProtectionRule>
        {
            new()
            {
                Id = 3,
                Name = "mask-secret",
                Pattern = "secret-[0-9]+",
                Settings = new PromptProtectionSettings
                {
                    Action = PromptProtectionActions.Mask,
                    Replacement = "[MASKED]",
                    Scopes = [PromptProtectionScopes.User],
                },
            },
        };
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("token is secret-123") }],
        };

        var result = new PromptProtectionRuleProtector(rules).ProtectWithResult(request);

        Assert.Equal("token is [MASKED]", result.Request!.Messages[0].Content!.Content);
        Assert.Equal(3, Assert.Single(result.MatchedRules).Id);
    }

    [Fact]
    public void RejectionThrowsTheSentinel()
    {
        var rules = new List<PromptProtectionRule>
        {
            new()
            {
                Pattern = "secret",
                Settings = new PromptProtectionSettings
                {
                    Action = PromptProtectionActions.Reject,
                    Scopes = [PromptProtectionScopes.User],
                },
            },
        };
        var request = new LlmRequest
        {
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("contains secret") }],
        };

        var protector = new PromptProtectionRuleProtector(rules);

        Assert.Throws<PromptProtectionRejectedException>(
            () => protector.ProtectWithResult(request));
        Assert.Throws<PromptProtectionRejectedException>(() => protector.Protect(request));
    }
}
