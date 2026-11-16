using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Prompts;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/biz/prompt_matcher_test.go: activation conditions are
/// an AND of composites, each composite an OR of conditions, and an empty list
/// matches everything.
/// </summary>
public sealed class PromptMatcherTests
{
    private static PromptActivationConditionComposite Composite(
        params PromptActivationCondition[] conditions)
        => new() { Conditions = [.. conditions] };

    private static PromptActivationCondition Condition(
        string? type = null,
        string? modelId = null,
        string? modelPattern = null,
        long? apiKeyId = null)
        => new()
        {
            Type = type,
            ModelId = modelId,
            ModelPattern = modelPattern,
            ApiKeyId = apiKeyId,
        };

    public static TheoryData<string, List<PromptActivationConditionComposite>?, string, long, bool>
        MatchConditionsCases => new()
        {
            { "empty conditions should always match", null, "gpt-4", 0L, true },
            { "empty composite list should match", [], "gpt-4", 0L, true },
            {
                "model_id exact match",
                [Composite(Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-4"))],
                "gpt-4", 0L, true
            },
            {
                "model_id mismatch",
                [Composite(Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-4"))],
                "gpt-3.5-turbo", 0L, false
            },
            {
                "model_pattern match",
                [Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: "gpt-4.*"))],
                "gpt-4-turbo", 0L, true
            },
            {
                "model_pattern mismatch",
                [Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: "gpt-4.*"))],
                "claude-3-opus", 0L, false
            },
            {
                "api_key_id match",
                [Composite(Condition(PromptActivationConditionTypes.ApiKey, apiKeyId: 1))],
                "gpt-4", 1L, true
            },
            {
                "api_key_id mismatch",
                [Composite(Condition(PromptActivationConditionTypes.ApiKey, apiKeyId: 1))],
                "gpt-4", 2L, false
            },
            {
                "composite OR - one matches",
                [
                    Composite(
                        Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-4"),
                        Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-3.5-turbo")),
                ],
                "gpt-3.5-turbo", 0L, true
            },
            {
                "composite OR - none matches",
                [
                    Composite(
                        Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-4"),
                        Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-3.5-turbo")),
                ],
                "claude-3-opus", 0L, false
            },
            {
                "multiple composites AND - all match",
                [
                    Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: "gpt-.*")),
                    Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: ".*-4")),
                ],
                "gpt-4", 0L, true
            },
            {
                "multiple composites AND - one fails",
                [
                    Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: "gpt-.*")),
                    Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: ".*-turbo")),
                ],
                "gpt-4", 0L, false
            },
            {
                "nil model_id should not match",
                [Composite(Condition(PromptActivationConditionTypes.ModelId))],
                "gpt-4", 0L, false
            },
            {
                "empty model_pattern should not match",
                [Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: ""))],
                "gpt-4", 0L, false
            },
            {
                "nil api_key_id should not match",
                [Composite(Condition(PromptActivationConditionTypes.ApiKey))],
                "gpt-4", 1L, false
            },
            {
                "unknown condition type should not match",
                [Composite(Condition("unknown_type"))],
                "gpt-4", 0L, false
            },
        };

    [Theory]
    [MemberData(nameof(MatchConditionsCases))]
    public void MatchConditions(
        string name,
        List<PromptActivationConditionComposite>? conditions,
        string model,
        long apiKeyId,
        bool expected)
    {
        _ = name;

        Assert.Equal(expected, PromptMatcher.MatchConditions(conditions, model, apiKeyId));
    }

    [Fact]
    public void NullCompositeElementMatchesLikeGoZeroValue()
    {
        // Go decodes `conditions: [null]` into a zero PromptActivationConditionComposite,
        // whose empty condition list matches everything.
        var conditions = new List<PromptActivationConditionComposite> { null! };

        Assert.True(PromptMatcher.MatchConditions(conditions, "gpt-4", 0));
    }

    [Fact]
    public void CompositeWithNullConditionsMatchesEverything()
    {
        var conditions = new List<PromptActivationConditionComposite>
        {
            new() { Conditions = null },
        };

        Assert.True(PromptMatcher.MatchConditions(conditions, "gpt-4", 0));
    }

    private static Prompt NewPrompt(
        long id,
        string content,
        string? action,
        params PromptActivationConditionComposite[] conditions)
        => new()
        {
            Id = id,
            Role = "system",
            Content = content,
            Settings = new PromptSettings
            {
                Action = action is null ? null : new PromptAction { Type = action },
                Conditions = conditions.Length == 0 ? null : [.. conditions],
            },
        };

    [Fact]
    public void MatchPromptRequiresAnActualPrompt()
    {
        // Port of TestPromptMatcher_MatchPrompt.
        Assert.False(PromptMatcher.MatchPrompt(null, "gpt-4", 0));

        Assert.True(PromptMatcher.MatchPrompt(
            NewPrompt(1, "You are a helpful assistant.", PromptActionTypes.Prepend),
            "gpt-4",
            0));

        var matching = NewPrompt(
            2,
            "You are a coding assistant.",
            PromptActionTypes.Prepend,
            Composite(Condition(PromptActivationConditionTypes.ModelId, modelId: "gpt-4")));
        Assert.True(PromptMatcher.MatchPrompt(matching, "gpt-4", 0));

        var notMatching = NewPrompt(
            3,
            "You are a coding assistant.",
            PromptActionTypes.Prepend,
            Composite(Condition(PromptActivationConditionTypes.ModelId, modelId: "claude-3-opus")));
        Assert.False(PromptMatcher.MatchPrompt(notMatching, "gpt-4", 0));
    }

    [Fact]
    public void FilterMatchingPromptsPreservesProviderOrder()
    {
        // Port of TestPromptMatcher_FilterMatchingPrompts.
        var prompts = new List<Prompt>
        {
            NewPrompt(1, "Prompt 1 - no conditions", PromptActionTypes.Prepend),
            NewPrompt(
                2,
                "Prompt 2 - GPT only",
                PromptActionTypes.Prepend,
                Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: "gpt-.*"))),
            NewPrompt(
                3,
                "Prompt 3 - Claude only",
                PromptActionTypes.Prepend,
                Composite(Condition(PromptActivationConditionTypes.ModelPattern, modelPattern: "claude-.*"))),
        };

        Assert.Equal([1L, 2L], PromptMatcher.FilterMatchingPrompts(prompts, "gpt-4", 0).Select(p => p.Id));
        Assert.Equal([1L, 3L], PromptMatcher.FilterMatchingPrompts(prompts, "claude-3-opus", 0).Select(p => p.Id));
        Assert.Equal([1L], PromptMatcher.FilterMatchingPrompts(prompts, "unknown-model", 0).Select(p => p.Id));
    }

    private static LlmRequest Request(string content)
        => new()
        {
            Model = "gpt-4",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText(content) }],
        };

    [Fact]
    public void ApplyPromptsWithoutPromptsKeepsTheRequest()
    {
        // Port of TestPromptMatcher_ApplyPrompts' "no prompts" case.
        var request = Request("Hello, how are you?");

        var result = PromptMatcher.ApplyPrompts(request, []);

        Assert.Same(request, result);
        var message = Assert.Single(result.Messages);
        Assert.Equal("user", message.Role);
    }

    [Fact]
    public void ApplyPromptsPrepends()
    {
        var result = PromptMatcher.ApplyPrompts(
            Request("Hello, how are you?"),
            [NewPrompt(1, "You are a helpful assistant.", PromptActionTypes.Prepend)]);

        Assert.Equal(["system", "user"], result.Messages.Select(message => message.Role));
        Assert.Equal("You are a helpful assistant.", result.Messages[0].Content!.Content);
    }

    [Fact]
    public void ApplyPromptsAppends()
    {
        var result = PromptMatcher.ApplyPrompts(
            Request("Hello, how are you?"),
            [NewPrompt(1, "Remember to be concise.", PromptActionTypes.Append)]);

        Assert.Equal(["user", "system"], result.Messages.Select(message => message.Role));
        Assert.Equal("Remember to be concise.", result.Messages[^1].Content!.Content);
    }

    [Fact]
    public void ApplyPromptsPrependsBeforeAppends()
    {
        var result = PromptMatcher.ApplyPrompts(
            Request("Hello, how are you?"),
            [
                NewPrompt(1, "You are a helpful assistant.", PromptActionTypes.Prepend),
                NewPrompt(2, "Be concise.", PromptActionTypes.Append),
            ]);

        Assert.Equal(["system", "user", "system"], result.Messages.Select(message => message.Role));
        Assert.Equal("You are a helpful assistant.", result.Messages[0].Content!.Content);
        Assert.Equal("Be concise.", result.Messages[^1].Content!.Content);
    }

    [Fact]
    public void ApplyPromptsMultiplePrependsMaintainOrder()
    {
        var result = PromptMatcher.ApplyPrompts(
            Request("Hello, how are you?"),
            [
                NewPrompt(1, "First prepend.", PromptActionTypes.Prepend),
                NewPrompt(2, "Second prepend.", PromptActionTypes.Prepend),
            ]);

        Assert.Equal(["system", "system", "user"], result.Messages.Select(message => message.Role));
        Assert.Equal("First prepend.", result.Messages[0].Content!.Content);
        Assert.Equal("Second prepend.", result.Messages[1].Content!.Content);
    }

    [Fact]
    public void ApplyPromptsDefaultActionIsPrepend()
    {
        // The Go switch's default branch (including an empty action type).
        var result = PromptMatcher.ApplyPrompts(
            Request("Hello, how are you?"),
            [NewPrompt(1, "Default action prompt.", action: null)]);

        Assert.Equal(["system", "user"], result.Messages.Select(message => message.Role));
        Assert.Equal("Default action prompt.", result.Messages[0].Content!.Content);
    }

    [Fact]
    public void ApplyPromptsSortsByOrderThenCreatedAt()
    {
        var shared = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var prompts = new List<Prompt>
        {
            new()
            {
                Id = 1,
                Role = "system",
                Content = "order 1, later",
                Order = 1,
                CreatedAt = shared.AddMinutes(1),
                Settings = new PromptSettings { Action = new PromptAction { Type = PromptActionTypes.Prepend } },
            },
            new()
            {
                Id = 2,
                Role = "system",
                Content = "order 1, earlier",
                Order = 1,
                CreatedAt = shared,
                Settings = new PromptSettings { Action = new PromptAction { Type = PromptActionTypes.Prepend } },
            },
            new()
            {
                Id = 3,
                Role = "system",
                Content = "order 0",
                Order = 0,
                CreatedAt = shared.AddHours(1),
                Settings = new PromptSettings { Action = new PromptAction { Type = PromptActionTypes.Prepend } },
            },
        };

        var result = PromptMatcher.ApplyPrompts(Request("Hello"), prompts);

        Assert.Equal(
            ["order 0", "order 1, earlier", "order 1, later"],
            result.Messages.Take(3).Select(message => message.Content!.Content));
    }
}

/// <summary>
/// Port of internal/server/orchestrator/prompt_test.go: the injection middleware
/// prepends/appends matching prompts to the unified messages and splices them
/// into decisions raw bodies.
/// </summary>
public sealed class InjectPromptsMiddlewareTests
{
    private static Prompt NewPrompt(
        string content,
        string? action = PromptActionTypes.Prepend,
        string role = "system",
        long order = 0,
        params PromptActivationConditionComposite[] conditions)
        => new()
        {
            Role = role,
            Content = content,
            Order = order,
            Settings = new PromptSettings
            {
                Action = action is null ? null : new PromptAction { Type = action },
                Conditions = conditions.Length == 0 ? null : [.. conditions],
            },
        };

    private static PromptActivationConditionComposite ModelId(string model)
        => new()
        {
            Conditions = [new PromptActivationCondition
            {
                Type = PromptActivationConditionTypes.ModelId,
                ModelId = model,
            }],
        };

    private static PromptActivationConditionComposite ModelPattern(string pattern)
        => new()
        {
            Conditions = [new PromptActivationCondition
            {
                Type = PromptActivationConditionTypes.ModelPattern,
                ModelPattern = pattern,
            }],
        };

    private static LlmRequest Request(string model, string content)
        => new()
        {
            Model = model,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText(content) }],
        };

    private static LlmRequest Decisions(string body)
        => new()
        {
            ApiFormat = ApiFormats.OpenAIDecisions,
            Decisions = new DecisionsRequest { Body = Encoding.UTF8.GetBytes(body) },
        };

    private static string BodyOf(LlmRequest request)
        => Encoding.UTF8.GetString(request.Decisions!.Body);

    [Fact]
    public void EmptyPromptListIsANoOp()
    {
        var middleware = new InjectPromptsMiddleware([], 0);
        var request = Request("gpt-4", "Hello");

        Assert.Same(request, middleware.TransformInboundLlmRequest(request));
    }

    [Fact]
    public void InjectsMatchingPrompts()
    {
        // Port of TestInjectPrompts_WithMatchingPrompts.
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("You are a helpful assistant.")],
            apiKeyId: 0);
        var request = Request("gpt-4", "Hello");

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("system", result.Messages[0].Role);
        Assert.Equal("You are a helpful assistant.", result.Messages[0].Content!.Content);
        Assert.Equal("user", result.Messages[1].Role);
    }

    [Theory]
    [InlineData("gpt-4", "GPT-4 specific prompt", 2)]
    [InlineData("claude-3-opus", "Claude specific prompt", 2)]
    [InlineData("unknown-model", null, 1)]
    public void ModelConditionsSelectPrompts(string model, string? expectedFirst, int expectedCount)
    {
        // Port of TestInjectPrompts_WithModelCondition.
        var middleware = new InjectPromptsMiddleware(
            [
                NewPrompt("GPT-4 specific prompt", conditions: [ModelId("gpt-4")]),
                NewPrompt("Claude specific prompt", conditions: [ModelPattern("claude-.*")]),
            ],
            apiKeyId: 0);
        var request = Request(model, "Hello");

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Equal(expectedCount, result.Messages.Count);
        if (expectedFirst is not null)
        {
            Assert.Equal(expectedFirst, result.Messages[0].Content!.Content);
        }
    }

    [Fact]
    public void PrependsBeforeAppends()
    {
        // Port of TestInjectPrompts_PrependAndAppend.
        var middleware = new InjectPromptsMiddleware(
            [
                NewPrompt("Prepend prompt", PromptActionTypes.Prepend, order: 0),
                NewPrompt("Append prompt", PromptActionTypes.Append, order: 1),
            ],
            apiKeyId: 0);
        var request = Request("gpt-4", "Hello");

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("Prepend prompt", result.Messages[0].Content!.Content);
        Assert.Equal("Hello", result.Messages[1].Content!.Content);
        Assert.Equal("Append prompt", result.Messages[2].Content!.Content);
    }

    [Fact]
    public void ApiKeyConditionsSeeTheCallerKey()
    {
        static IReadOnlyList<Prompt> KeyPrompt()
            => [NewPrompt(
                "Key 7 prompt",
                conditions:
                [
                    new PromptActivationConditionComposite
                    {
                        Conditions =
                        [
                            new PromptActivationCondition
                            {
                                Type = PromptActivationConditionTypes.ApiKey,
                                ApiKeyId = 7,
                            },
                        ],
                    },
                ])];

        var matching = new InjectPromptsMiddleware(KeyPrompt(), apiKeyId: 7);
        var result = matching.TransformInboundLlmRequest(Request("gpt-4", "Hello"));
        Assert.Equal(2, result.Messages.Count);

        var other = new InjectPromptsMiddleware(KeyPrompt(), apiKeyId: 8);
        var unchanged = Request("gpt-4", "Hello");
        Assert.Same(unchanged, other.TransformInboundLlmRequest(unchanged));
    }

    [Fact]
    public void DecisionsStringInputGetsEveryPrompt()
    {
        // Port of TestInjectPrompts_DecisionsMutatesRawInput: the string input
        // has no role, so prompt scopes are not consulted.
        var middleware = new InjectPromptsMiddleware(
            [
                NewPrompt("Policy.", PromptActionTypes.Prepend, role: "system", order: 0),
                NewPrompt("Be concise.", PromptActionTypes.Append, role: "assistant", order: 1),
            ],
            apiKeyId: 0);
        var request = Decisions(
            """{"model":"gpt-6-luna","input":"customer question","questions":[{}]}""");

        middleware.TransformInboundLlmRequest(request);

        Assert.Equal(
            """{"model":"gpt-6-luna","input":"Policy.\ncustomer question\nBe concise.","questions":[{}]}""",
            BodyOf(request));
    }

    [Fact]
    public void DecisionsArrayInputPatchesTheFirstInputTextPartByRole()
    {
        var middleware = new InjectPromptsMiddleware(
            [
                NewPrompt("Policy.", PromptActionTypes.Prepend, role: "system", order: 0),
                NewPrompt("Assistant only.", PromptActionTypes.Prepend, role: "assistant", order: 1),
            ],
            apiKeyId: 0);
        var request = Decisions(
            """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"},{"type":"input_text","text":"second"}]},{"type":"message","role":"assistant","content":[{"type":"input_text","text":"reply"}]}]}""");

        middleware.TransformInboundLlmRequest(request);

        Assert.Equal(
            """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"Policy.\nhello"},{"type":"input_text","text":"second"}]},{"type":"message","role":"assistant","content":[{"type":"input_text","text":"reply"}]}]}""",
            BodyOf(request));
    }

    [Fact]
    public void DecisionsRoleScopingSkipsPromptsThatDoNotMatchTheMessageRole()
    {
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Assistant only.", PromptActionTypes.Prepend, role: "assistant")],
            apiKeyId: 0);
        var request = Decisions(
            """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}]}""");
        var original = BodyOf(request);

        middleware.TransformInboundLlmRequest(request);

        Assert.Equal(original, BodyOf(request));
    }

    [Fact]
    public void DecisionsPatchStopsAtTheFirstInputTextPart()
    {
        // Go returns after the first input_text part even when no prompt matched
        // its role, so later messages stay untouched.
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Policy.", PromptActionTypes.Prepend, role: "user")],
            apiKeyId: 0);
        var request = Decisions(
            """{"model":"alias","input":[{"type":"message","role":"assistant","content":[{"type":"input_text","text":"assistant text"}]},{"type":"message","role":"user","content":[{"type":"input_text","text":"user text"}]}]}""");
        var original = BodyOf(request);

        middleware.TransformInboundLlmRequest(request);

        Assert.Equal(original, BodyOf(request));
    }

    [Fact]
    public void DecisionsInputTextTypeMatchIsCaseInsensitive()
    {
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Policy.", PromptActionTypes.Prepend)],
            apiKeyId: 0);
        var request = Decisions(
            """{"model":"alias","input":[{"type":"message","role":"user","content":[{"type":"INPUT_TEXT","text":"hello"}]}]}""");

        middleware.TransformInboundLlmRequest(request);

        Assert.Contains("\"text\":\"Policy.\\nhello\"", BodyOf(request), StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionsMissingRoleDefaultsToUser()
    {
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Policy.", PromptActionTypes.Prepend, role: "user")],
            apiKeyId: 0);
        var request = Decisions(
            """{"model":"alias","input":[{"type":"message","content":[{"type":"input_text","text":"hello"}]}]}""");

        middleware.TransformInboundLlmRequest(request);

        Assert.Contains("\"text\":\"Policy.\\nhello\"", BodyOf(request), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"model":"alias","questions":[{}]}""")]
    [InlineData("""{"model":"alias","input":42}""")]
    [InlineData("""{"model":"alias","input":[{"type":"message","role":"user","content":"plain"}]}""")]
    public void DecisionsWithoutAPatchableInputAreUnchanged(string body)
    {
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Policy.", PromptActionTypes.Prepend)],
            apiKeyId: 0);
        var request = Decisions(body);

        middleware.TransformInboundLlmRequest(request);

        Assert.Equal(body, BodyOf(request));
    }

    [Fact]
    public void DecisionsWithoutABodyAreUnchanged()
    {
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Policy.", PromptActionTypes.Prepend)],
            apiKeyId: 0);
        var request = new LlmRequest
        {
            ApiFormat = ApiFormats.OpenAIDecisions,
            Decisions = new DecisionsRequest(),
        };

        middleware.TransformInboundLlmRequest(request);

        Assert.Empty(request.Decisions!.Body);
    }

    [Fact]
    public void NonDecisionsRequestsUseTheUnifiedMessages()
    {
        var middleware = new InjectPromptsMiddleware(
            [NewPrompt("Policy.", PromptActionTypes.Prepend)],
            apiKeyId: 0);
        var request = Request("gpt-4", "Hello");

        var result = middleware.TransformInboundLlmRequest(request);

        Assert.Same(request, result);
        Assert.Equal(2, result.Messages.Count);
    }

    [Theory]
    [InlineData("", "user", true)]
    [InlineData("system", "user", true)]
    [InlineData("system", "assistant", true)]
    [InlineData("user", "user", true)]
    [InlineData("user", "assistant", false)]
    [InlineData("assistant", "user", false)]
    public void PromptScopeMatches(string promptRole, string inputRole, bool expected)
        => Assert.Equal(expected, DecisionsPrompts.PromptScopeMatches(promptRole, inputRole));
}

/// <summary>
/// The project gate and the fail-open provider read
/// (orchestrator.injectPrompts, prompt.go:37-87).
/// </summary>
public sealed class LoadPromptsTests
{
    private sealed class StubProvider(IReadOnlyList<Prompt>? prompts, Exception? error = null)
        : IPromptProvider
    {
        public long? LastProjectId { get; private set; }

        public Task<IReadOnlyList<Prompt>> GetEnabledPromptsAsync(
            long projectId,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            LastProjectId = projectId;

            return error is not null
                ? Task.FromException<IReadOnlyList<Prompt>>(error)
                : Task.FromResult(prompts ?? (IReadOnlyList<Prompt>)[]);
        }
    }

    [Fact]
    public async Task NoProviderSkipsInjection()
    {
        Assert.Null(await ChatCompletionOrchestrator.LoadPromptsAsync(
            null,
            new RequestIdentity(1, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task NoProjectIdSkipsTheProvider()
    {
        // contexts.GetProjectID's !ok branch.
        var provider = new StubProvider([new Prompt()]);

        Assert.Null(await ChatCompletionOrchestrator.LoadPromptsAsync(
            provider,
            new RequestIdentity(0, null),
            CancellationToken.None));
        Assert.Null(provider.LastProjectId);
    }

    [Fact]
    public async Task ProviderErrorFailsOpen()
    {
        var provider = new StubProvider([], new InvalidOperationException("boom"));

        Assert.Null(await ChatCompletionOrchestrator.LoadPromptsAsync(
            provider,
            new RequestIdentity(1, null),
            CancellationToken.None));
    }

    [Fact]
    public async Task ProviderReceivesTheContextProjectId()
    {
        var prompts = new List<Prompt> { new() { Id = 1 } };
        var provider = new StubProvider(prompts);

        var result = await ChatCompletionOrchestrator.LoadPromptsAsync(
            provider,
            new RequestIdentity(42, null),
            CancellationToken.None);

        Assert.Same(prompts, result);
        Assert.Equal(42, provider.LastProjectId);
    }
}

/// <summary>
/// Port of biz.PromptService.GetEnabledPrompts' read side: the jsonb settings
/// column decoded per row, with malformed settings failing the whole load
/// (which the orchestrator then treats as a provider error and skips).
/// </summary>
public sealed class PromptServiceTests
{
    private sealed class FakePromptStore : IPromptStore
    {
        public List<PromptRecord> Prompts { get; } = [];

        public Exception? Failure { get; set; }

        public long? LastProjectId { get; private set; }

        public Task<long> CreateAsync(PromptRecord prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PromptRecord>> ListEnabledByProjectAsync(
            long projectId,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            LastProjectId = projectId;

            return Failure is not null
                ? Task.FromException<IReadOnlyList<PromptRecord>>(Failure)
                : Task.FromResult<IReadOnlyList<PromptRecord>>(Prompts);
        }

        // The management-surface members are unused here; FakePromptStore
        // covers them for the service and endpoint tests.

        public Task<PromptRecord?> GetByIdAsync(
            long id,
            long? projectScope,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> ExistsByNameAsync(
            long projectId,
            string name,
            long? excludeId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ListPage<PromptRecord>> ListAsync(
            PromptListQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> UpdateAsync(
            PromptUpdate update,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> UpdateStatusAsync(
            long id,
            long projectId,
            string status,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> DeleteAsync(
            long id,
            long projectId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> BulkDeleteAsync(
            IReadOnlyList<long> ids,
            long projectId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> BulkUpdateStatusAsync(
            IReadOnlyList<long> ids,
            long projectId,
            string status,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static PromptRecord Record(long id, string settings, long order = 0)
        => new()
        {
            Id = id,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Name = "prompt-" + id,
            Description = "description",
            Role = "system",
            Content = "content-" + id,
            Status = "enabled",
            Order = order,
            Settings = settings,
            ProjectId = 1,
        };

    [Fact]
    public async Task MapsRecordsAndTheirSettings()
    {
        var store = new FakePromptStore();
        store.Prompts.Add(Record(
            7,
            """{"action":{"type":"append"},"conditions":[{"conditions":[{"type":"model_id","model_id":"gpt-4"}]}]}""",
            order: 3));

        var prompts = await new PromptService(store).GetEnabledPromptsAsync(1);

        var prompt = Assert.Single(prompts);
        Assert.Equal(7, prompt.Id);
        Assert.Equal("prompt-7", prompt.Name);
        Assert.Equal("description", prompt.Description);
        Assert.Equal("system", prompt.Role);
        Assert.Equal("content-7", prompt.Content);
        Assert.Equal("enabled", prompt.Status);
        Assert.Equal(3, prompt.Order);
        Assert.Equal(1, prompt.ProjectId);
        Assert.Equal(PromptActionTypes.Append, prompt.Settings.Action!.Type);
        Assert.Equal(
            "gpt-4",
            Assert.Single(Assert.Single(prompt.Settings.Conditions!).Conditions!).ModelId);
        Assert.Equal(1, store.LastProjectId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public async Task BlankOrNullSettingsDecodeToTheZeroValue(string settings)
    {
        var store = new FakePromptStore();
        store.Prompts.Add(Record(1, settings));

        var prompt = Assert.Single(await new PromptService(store).GetEnabledPromptsAsync(1));

        Assert.Null(prompt.Settings.Action);
        Assert.Null(prompt.Settings.Conditions);
    }

    [Fact]
    public async Task SettingsDecodeIsCaseInsensitiveAndIgnoresUnknownKeys()
    {
        var store = new FakePromptStore();
        store.Prompts.Add(Record(1, """{"ACTION":{"Type":"append"},"unknown":true}"""));

        var prompt = Assert.Single(await new PromptService(store).GetEnabledPromptsAsync(1));

        Assert.Equal(PromptActionTypes.Append, prompt.Settings.Action!.Type);
    }

    [Fact]
    public async Task NullConditionElementIsKeptAndMatchesEverything()
    {
        var store = new FakePromptStore();
        store.Prompts.Add(Record(1, """{"conditions":[null]}"""));

        var prompt = Assert.Single(await new PromptService(store).GetEnabledPromptsAsync(1));

        Assert.Null(Assert.Single(prompt.Settings.Conditions!));
        Assert.True(PromptMatcher.MatchPrompt(prompt, "any-model", 0));
    }

    [Fact]
    public async Task InvalidSettingsFailTheLoad()
    {
        var store = new FakePromptStore();
        store.Prompts.Add(Record(1, "not json"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => new PromptService(store).GetEnabledPromptsAsync(1));
    }

    [Fact]
    public async Task StoreFailureFailsTheLoad()
    {
        var store = new FakePromptStore { Failure = new InvalidOperationException("boom") };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PromptService(store).GetEnabledPromptsAsync(1));
    }
}
