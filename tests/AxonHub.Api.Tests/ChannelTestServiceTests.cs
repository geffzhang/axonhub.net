using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.PromptProtection;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the channel tester (internal/server/orchestrator/tester.go):
/// every public entrypoint drives the real chat pipeline with a scripted
/// transport, so format selection, prompt composition, prompt protection and
/// per-key batching are exercised end to end.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ChannelTestServiceTests(GrainClusterFixture fixture)
{
    private const string OpenAiCompletionBody =
        """{"id":"test","model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private const string AnthropicMessageBody =
        """{"id":"test","type":"message","role":"assistant","model":"test-model","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}""";

    private const string GeminiContentBody =
        """{"candidates":[{"content":{"role":"model","parts":[{"text":"hello"}]},"finishReason":"STOP","index":0}],"usageMetadata":{"promptTokenCount":1,"candidatesTokenCount":1,"totalTokenCount":2},"modelVersion":"test-model"}""";

    private const string SystemOneBody =
        """{"model":"test-model","answers":{"connection":{"type":"noul","noul":0.95}},"usage":{"input_tokens":10,"output_tokens":0}}""";

    private readonly FakeChannelWriteStore _channels = new();
    private readonly FakeSystemStore _systems = new();

    // -- Entrypoints --------------------------------------------------------

    [Fact]
    public async Task TestChannelSendsTheConfiguredPromptsAndReturnsTheAssistantMessage()
    {
        SeedPrompts("custom system", "custom user");
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);
        Assert.Null(result.Error);
        Assert.True(result.Latency >= 0);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/chat/completions", request.Url);

        // FinalizeAuthHeaders folds the AuthConfig into the wire headers before
        // the executor runs, exactly like httpclient.FinalizeAuthHeaders.
        Assert.Equal(["Bearer test-key-1"], request.Headers!["Authorization"]);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());

        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("custom system", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("custom user", messages[1].GetProperty("content").GetString());

        Assert.Equal(256, root.GetProperty("max_completion_tokens").GetInt64());
        Assert.False(root.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task TestChannelUsesTheRequestedModelOverride()
    {
        SeedPrompts();
        var channel = SeedChannel(supportedModels: """["test-model","other-model"]""");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, "other-model");

        Assert.True(result.Success);
        using var body = JsonDocument.Parse(Assert.Single(executor.Requests).Body!);
        Assert.Equal("other-model", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task TestChannelUsesTheDefaultPromptWhenTheSettingRowIsMissing()
    {
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        await service.TestChannelAsync(channel.Id, null);

        using var body = JsonDocument.Parse(Assert.Single(executor.Requests).Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(ChannelTestPrompts.DefaultSystemPrompt, messages[0].GetProperty("content").GetString());
        Assert.Equal(ChannelTestPrompts.DefaultUserPrompt, messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestChannelTestsAnAnthropicChannelThroughTheMessagesEndpoint()
    {
        SeedPrompts();
        var channel = SeedChannel(type: "anthropic");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(AnthropicMessageBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/messages", request.Url);
        // HeaderSet canonicalizes the key like textproto.CanonicalMIMEHeaderKey.
        Assert.Equal(["test-key-1"], request.Headers!["X-Api-Key"]);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("test-model", root.GetProperty("model").GetString());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt64());
        Assert.NotEqual(JsonValueKind.Null, root.GetProperty("system").ValueKind);
        Assert.True(root.TryGetProperty("messages", out var messages));
        Assert.NotEqual(0, messages.GetArrayLength());
    }

    [Fact]
    public async Task TestChannelTestsAGeminiChannelThroughGenerateContent()
    {
        SeedPrompts();
        var channel = SeedChannel(type: "gemini", baseUrl: "http://upstream.test");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(GeminiContentBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);

        var request = Assert.Single(executor.Requests);
        Assert.Equal(
            "http://upstream.test/v1beta/models/test-model:generateContent",
            request.Url);
        Assert.Equal(["test-key-1"], request.Headers!["X-Goog-Api-Key"]);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.True(root.TryGetProperty("contents", out _));
        Assert.True(root.TryGetProperty("systemInstruction", out _));
        Assert.False(root.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task TestChannelTestsATypeSafeChannelThroughSystemOne()
    {
        SeedPrompts();
        var channel = SeedChannel(type: "typesafe");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(SystemOneBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Contains("\"noul\":0.95", result.Message);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/systemone", request.Url);

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        Assert.Equal("system prompt\n\nuser prompt", root.GetProperty("state").GetString());
        Assert.Equal("test-model", root.GetProperty("model").GetString());

        var connection = root.GetProperty("questions").GetProperty("connection");
        Assert.Equal("noul", connection.GetProperty("type").GetString());
        Assert.NotEqual(JsonValueKind.Null, connection.GetProperty("instructions").ValueKind);

        Assert.False(root.TryGetProperty("messages", out _));
        Assert.False(root.TryGetProperty("stream", out _));
        Assert.False(root.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task TestChannelForcedChatOverridesTheTypeSafeEndpoint()
    {
        SeedPrompts();
        var channel = SeedChannel(
            endpoints: """[{"api_format":"typesafe/systemone","path":"/systemone"}]""",
            settings: """{"modelProtocols":[{"model":"test-model","apiFormats":["openai/chat_completions"]}]}""");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);
        Assert.Equal(
            "http://upstream.test/v1/chat/completions",
            Assert.Single(executor.Requests).Url);
    }

    [Theory]
    [InlineData("typesafe", "systemone", false)]
    [InlineData("openai", "openai", false)]
    [InlineData("openai", "openai-forced-chat", false)]
    [InlineData("openai", "openai-stream", true)]
    [InlineData("anthropic", "anthropic", false)]
    [InlineData("gemini", "gemini", false)]
    public async Task TestChannelSupportsEveryProviderFormat(
        string channelType,
        string scenario,
        bool stream)
    {
        SeedPrompts();
        var channel = SeedChannel(
            type: channelType,
            baseUrl: channelType == "gemini" ? "http://upstream.test" : "http://upstream.test/v1",
            credentials: """{"apiKeys":["test-key-1","test-key-2"]}""",
            policies: stream ? """{"stream":"require"}""" : null,
            endpoints: scenario == "openai-forced-chat"
                ? """[{"api_format":"typesafe/systemone","path":"/systemone"}]"""
                : null,
            settings: scenario == "openai-forced-chat"
                ? """{"modelProtocols":[{"model":"test-model","apiFormats":["openai/chat_completions"]}]}"""
                : null);

        var responseBody = scenario switch
        {
            "systemone" => SystemOneBody,
            "anthropic" => AnthropicMessageBody,
            "gemini" => GeminiContentBody,
            _ => OpenAiCompletionBody,
        };

        var executor = new ScriptedExecutor();
        if (stream)
        {
            executor.OnStream = (_, _, _) => new ListStream<StreamEvent>(StreamChunks());
        }
        else
        {
            executor.OnDo = (_, _, _) => Json(responseBody);
        }

        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);
        Assert.True(result.Success, result.Error);
        if (scenario == "systemone")
        {
            Assert.Contains("\"noul\":0.95", result.Message);
        }
        else
        {
            Assert.Equal("hello", result.Message);
        }

        var keyResult = await service.TestSingleApiKeyAsync(channel.Id, "test-key-2", null);
        Assert.True(keyResult.Success, keyResult.Error);

        var batch = await service.TestChannelApiKeysAsync(channel.Id, null);
        Assert.Equal(2, batch.Total);
        Assert.Equal(2, batch.SuccessCount);
        Assert.Equal(0, batch.FailedCount);
        Assert.All(batch.Results, item => Assert.True(item.Success, item.Error));

        // One request per entrypoint plus one per batch key.
        Assert.Equal(4, executor.Calls);
        Assert.All(executor.Requests, request => AssertFormatRequest(request, channelType, stream));
        Assert.Equal("test-key-2", RequestKey(executor.Requests[1]));

        var batchKeys = executor.Requests
            .Skip(2)
            .Select(request => RequestKey(request)!)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["test-key-1", "test-key-2"], batchKeys);
    }

    [Fact]
    public async Task TestChannelAccumulatesTheStreamWhenThePolicyRequiresIt()
    {
        SeedPrompts();
        var channel = SeedChannel(policies: """{"stream":"require"}""");
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(StreamChunks()),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Equal("hello", result.Message);
        Assert.Null(result.Error);

        var request = Assert.Single(executor.Requests);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task TestChannelReportsAnEmptyStream()
    {
        SeedPrompts();
        var channel = SeedChannel(policies: """{"stream":"require"}""");
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>([Event("[DONE]")]),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal("No content in stream response", result.Error);
    }

    [Fact]
    public async Task TestChannelReportsATerminalStreamError()
    {
        SeedPrompts();
        var channel = SeedChannel(policies: """{"stream":"require"}""");
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(
                [],
                terminalError: new IOException("connection reset")),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal("connection reset", result.Error);
    }

    [Fact]
    public async Task TestChannelReportsTheUpstreamErrorMessage()
    {
        SeedPrompts();
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(
                401,
                """{"error":{"message":"upstream rejected test request","type":"invalid_api_key"}}"""),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal("upstream rejected test request", result.Error);
    }

    [Fact]
    public async Task TestChannelReportsAResponseWithoutAMessageAsANullMessage()
    {
        // Go dereferences choices[0].message and its content pointer, so this
        // response panics the reference tester; the port reports a successful
        // test with a null message instead (difference C156).
        SeedPrompts();
        var channel = SeedChannel();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(
                """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"finish_reason":"stop"}]}"""),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task TestChannelReportsAModelTheChannelDoesNotSupport()
    {
        SeedPrompts();
        var channel = SeedChannel(supportedModels: """["other-model"]""");
        var service = NewService(new ScriptedExecutor());

        var result = await service.TestChannelAsync(channel.Id, "test-model");

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
        Assert.Equal("model test-model not supported in channel Alpha", result.Error);
    }

    [Theory]
    [InlineData("empty answers", """{"model":"test-model","answers":{}}""", 200, "no answers in System One response", true)]
    [InlineData("unrelated answer", """{"model":"test-model","answers":{"other":{"type":"noul","noul":0.95}}}""", 200, "no connection answer in System One response", true)]
    [InlineData("invalid JSON", "not-json", 200, "invalid json", false)]
    [InlineData("upstream error", """{"error":{"message":"upstream rejected test request","type":"invalid_api_key"}}""", 401, "upstream rejected test request", true)]
    public async Task TestChannelReportsSystemOneFailures(
        string scenario,
        string responseBody,
        int statusCode,
        string expectedError,
        bool exact)
    {
        _ = scenario;
        SeedPrompts();
        var channel = SeedChannel(type: "typesafe");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => statusCode == 200
                ? Json(responseBody)
                : throw TestChannels.HttpClient(statusCode, responseBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelAsync(channel.Id, null);

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Message);
        if (exact)
        {
            Assert.Equal(expectedError, result.Error);
        }
        else
        {
            Assert.Contains(expectedError, result.Error);
        }
    }

    // -- Key batching -------------------------------------------------------

    [Fact]
    public async Task TestChannelAPIKeysTestsEveryConfiguredKeyAndReportsDisableState()
    {
        SeedPrompts();
        var channel = SeedChannel(
            credentials: """{"apiKeys":["test-key-1","test-key-2"]}""",
            disabledApiKeys: """[{"key":"test-key-2","disabledAt":"2026-01-01T00:00:00Z","errorCode":401,"reason":"auth"}]""");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelApiKeysAsync(channel.Id, null);

        Assert.Equal(channel.Id, result.ChannelId);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(2, result.Results.Count);

        Assert.Equal("test****ey-1", result.Results[0].KeyPrefix);
        Assert.True(result.Results[0].Success);
        Assert.False(result.Results[0].Disabled);

        Assert.Equal("test****ey-2", result.Results[1].KeyPrefix);
        Assert.True(result.Results[1].Success);
        Assert.True(result.Results[1].Disabled);

        Assert.Equal(2, executor.Calls);
        Assert.Equal(
            ["test-key-1", "test-key-2"],
            executor.Requests
                .Select(request => RequestKey(request)!)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray());
    }

    [Fact]
    public async Task TestChannelAPIKeysKeepsPerKeyFailuresAndSuccessesApart()
    {
        SeedPrompts();
        var channel = SeedChannel(credentials: """{"apiKeys":["test-key-1","test-key-2"]}""");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => RequestKey(request) == "test-key-1"
                ? throw TestChannels.HttpClient(
                    401,
                    """{"error":{"message":"invalid key","type":"invalid_api_key"}}""")
                : Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelApiKeysAsync(channel.Id, null);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);

        Assert.False(result.Results[0].Success);
        Assert.Equal("invalid key", result.Results[0].Error);
        Assert.Equal("test****ey-1", result.Results[0].KeyPrefix);

        Assert.True(result.Results[1].Success);
        Assert.Null(result.Results[1].Error);
    }

    [Fact]
    public async Task TestChannelAPIKeysReportsContextCanceledForEveryQueuedKey()
    {
        // Go's ctx.Err() text survives into per-key results; the port's
        // OperationCanceledException does not carry it, so the service
        // synthesizes the same wording (difference C155).
        SeedPrompts();
        var channel = SeedChannel(credentials: """{"apiKeys":["test-key-1","test-key-2"]}""");
        var executor = new ScriptedExecutor();
        var service = NewService(executor);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var result = await service.TestChannelApiKeysAsync(channel.Id, null, canceled.Token);

        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(2, result.FailedCount);
        Assert.All(result.Results, item =>
        {
            Assert.False(item.Success);
            Assert.Equal("context canceled", item.Error);
            Assert.Equal(0, item.Latency);
            Assert.False(item.Disabled);
        });
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task TestChannelAPIKeysTreatsMalformedPolicyAndDisabledColumnsAsAbsent()
    {
        // ent decodes policies / disabled_api_keys on load and fails the whole
        // channel; the port reads raw columns and only needs them for the
        // stream policy and disable markers, so bad JSON means "absent"
        // (difference C157).
        SeedPrompts();
        var channel = SeedChannel(
            credentials: """{"apiKeys":["test-key-1","test-key-2"]}""",
            policies: "not-json",
            disabledApiKeys: "not-json");
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Json(OpenAiCompletionBody),
        };
        var service = NewService(executor);

        var result = await service.TestChannelApiKeysAsync(channel.Id, null);

        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
        Assert.All(result.Results, item => Assert.False(item.Disabled));
        Assert.Equal(2, executor.Calls);
    }

    [Fact]
    public async Task TestChannelAPIKeysRejectsSystemOneStreamingPolicies()
    {
        SeedPrompts();
        var channel = SeedChannel(
            type: "typesafe",
            credentials: """{"apiKeys":["test-key-1","test-key-2"]}""",
            policies: """{"stream":"require"}""");
        var executor = new ScriptedExecutor();
        var service = NewService(executor);

        var channelError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TestChannelAsync(channel.Id, null));
        Assert.Equal("systemone does not support streaming", channelError.Message);

        var keyResult = await service.TestSingleApiKeyAsync(channel.Id, "test-key-1", null);
        Assert.False(keyResult.Success);
        Assert.Equal("systemone does not support streaming", keyResult.Error);

        var batch = await service.TestChannelApiKeysAsync(channel.Id, null);
        Assert.Equal(0, batch.SuccessCount);
        Assert.Equal(2, batch.FailedCount);
        Assert.All(
            batch.Results,
            item => Assert.Equal("systemone does not support streaming", item.Error));

        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task TestChannelAPIKeysRejectsChannelsWithoutKeys()
    {
        SeedPrompts();
        var channel = SeedChannel(credentials: "{}");
        var service = NewService(new ScriptedExecutor());

        var batchError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TestChannelApiKeysAsync(channel.Id, null));
        Assert.Equal("no API keys configured for channel", batchError.Message);

        var singleError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TestSingleApiKeyAsync(channel.Id, "test-key-1", null));
        Assert.Equal("no API keys configured for channel", singleError.Message);
    }

    [Fact]
    public async Task TestSingleAPIKeyRejectsAKeyTheChannelDoesNotOwn()
    {
        SeedPrompts();
        var channel = SeedChannel();
        var service = NewService(new ScriptedExecutor());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.TestSingleApiKeyAsync(channel.Id, "other-key", null));

        Assert.Equal(
            "the provided API key is not configured for this channel",
            error.Message);
    }

    [Fact]
    public async Task TestEntrypointsReportMissingChannelsWithTheReferenceWording()
    {
        var service = NewService(new ScriptedExecutor());

        foreach (var call in new Func<Task>[]
        {
            () => service.TestChannelAsync(424242, null),
            () => service.TestSingleApiKeyAsync(424242, "test-key-1", null),
            () => service.TestChannelApiKeysAsync(424242, null),
        })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Equal("channel not found: ent: channel not found", error.Message);
        }
    }

    [Fact]
    public async Task TestEntrypointsReportStoreFailuresWithTheReferenceWording()
    {
        _channels.GetFailure = new StoreFailureException(
            "failed to get channel: database is down",
            new InvalidOperationException("database is down"));
        var service = NewService(new ScriptedExecutor());

        foreach (var call in new Func<Task>[]
        {
            () => service.TestChannelAsync(7, null),
            () => service.TestSingleApiKeyAsync(7, "test-key-1", null),
            () => service.TestChannelApiKeysAsync(7, null),
        })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(call);
            Assert.Equal(
                "channel not found: failed to get channel: database is down",
                error.Message);
        }
    }

    // -- Prompt protection --------------------------------------------------

    [Theory]
    [InlineData(PromptProtectionScopes.System, "masked\n\nsecret")]
    [InlineData(PromptProtectionScopes.User, "secret\n\nmasked")]
    public async Task SystemOneTestsMaskPromptsBeforeCombiningState(
        string scope,
        string expectedState)
    {
        SeedPrompts("secret", "secret");
        var channel = SeedChannel(type: "typesafe");
        var states = new List<string>();
        var gate = new object();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                using var body = JsonDocument.Parse(request.Body!);
                lock (gate)
                {
                    states.Add(body.RootElement.GetProperty("state").GetString()!);
                }

                return Json(SystemOneBody);
            },
        };
        var service = NewService(
            executor,
            Rules(PromptProtectionActions.Mask, scope));

        var result = await service.TestChannelAsync(channel.Id, null);
        var keyResult = await service.TestSingleApiKeyAsync(channel.Id, "test-key-1", null);
        var batch = await service.TestChannelApiKeysAsync(channel.Id, null);

        Assert.True(result.Success, result.Error);
        Assert.True(keyResult.Success, keyResult.Error);
        Assert.Equal(1, batch.SuccessCount);
        Assert.Equal(0, batch.FailedCount);

        Assert.Equal(3, executor.Calls);
        Assert.Equal(3, states.Count);
        Assert.All(states, state => Assert.Equal(expectedState, state));
    }

    [Theory]
    [InlineData(PromptProtectionScopes.System)]
    [InlineData(PromptProtectionScopes.User)]
    public async Task SystemOneTestsRejectProtectedPromptsBeforeAnyUpstreamCall(string scope)
    {
        SeedPrompts("secret", "secret");
        var channel = SeedChannel(type: "typesafe");
        var executor = new ScriptedExecutor();
        var service = NewService(
            executor,
            Rules(PromptProtectionActions.Reject, scope));

        var channelError = await Assert.ThrowsAsync<PromptProtectionRejectedException>(
            () => service.TestChannelAsync(channel.Id, null));
        Assert.Equal("prompt protection rejected request", channelError.Message);

        var keyResult = await service.TestSingleApiKeyAsync(channel.Id, "test-key-1", null);
        Assert.False(keyResult.Success);
        Assert.Equal("prompt protection rejected request", keyResult.Error);

        var batch = await service.TestChannelApiKeysAsync(channel.Id, null);
        Assert.Equal(0, batch.SuccessCount);
        Assert.Equal(1, batch.FailedCount);
        Assert.All(
            batch.Results,
            item => Assert.Contains("prompt protection rejected request", item.Error));

        Assert.Equal(0, executor.Calls);
    }

    // -- Ported unit tests --------------------------------------------------

    [Fact]
    public void BuildChannelTestRequestUsesConfiguredPrompts()
    {
        var request = ChannelTestService.BuildChannelTestRequest(
            "test-model",
            useStream: true,
            "system prompt",
            "user prompt",
            responsesWebSocket: false,
            ApiFormats.OpenAIChatCompletion);

        Assert.Equal("test-model", request.Model);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("system prompt", request.Messages[0].Content!.Content);
        Assert.Equal("user", request.Messages[1].Role);
        Assert.Equal("user prompt", request.Messages[1].Content!.Content);
        Assert.Equal(256, request.MaxCompletionTokens);
        Assert.True(request.Stream);
    }

    [Fact]
    public void BuildChannelTestRequestUsesPingForResponsesWebSocket()
    {
        var request = ChannelTestService.BuildChannelTestRequest(
            "test-model",
            useStream: false,
            "system prompt",
            "user prompt",
            responsesWebSocket: true,
            ApiFormats.OpenAIChatCompletion);

        Assert.Equal("test-model", request.Model);
        var message = Assert.Single(request.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal(ChannelTestService.ResponsesWebSocketTestPrompt, message.Content!.Content);
        Assert.Null(request.MaxCompletionTokens);
        Assert.True(request.Stream);
    }

    [Fact]
    public void BuildChannelTestRequestUsesTheDecisionsFormat()
    {
        var request = ChannelTestService.BuildChannelTestRequest(
            "gpt-6-luna",
            useStream: true,
            "ignored",
            "classify this",
            responsesWebSocket: false,
            ApiFormats.OpenAIDecisions);

        Assert.Equal(RequestTypes.Decisions, request.RequestType);
        Assert.Equal(ApiFormats.OpenAIDecisions, request.ApiFormat);
        Assert.NotNull(request.Decisions);
        Assert.False(request.Stream);
        Assert.Null(request.Messages);

        using var body = JsonDocument.Parse(request.Decisions!.Body);
        Assert.Equal("gpt-6-luna", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("classify this", body.RootElement.GetProperty("input").GetString());

        var questions = body.RootElement.GetProperty("questions");
        Assert.Equal(1, questions.GetArrayLength());
        Assert.Equal("answer", questions[0].GetProperty("name").GetString());
        Assert.Equal("predicate", questions[0].GetProperty("type").GetString());
    }

    [Fact]
    public void ChannelTestApiFormatPrefersDecisionsEndpoint()
    {
        var channel = Descriptor(
            type: string.Empty,
            endpoints: """[{"api_format":"openai/chat_completions"},{"api_format":"openai/decisions"}]""");

        Assert.Equal(
            ApiFormats.OpenAIDecisions,
            ChannelTestService.ChannelTestApiFormat(channel, "gpt-6-luna"));
    }

    [Fact]
    public void ChannelTestApiFormatFallsBackToChat()
    {
        Assert.Equal(
            ApiFormats.OpenAIChatCompletion,
            ChannelTestService.ChannelTestApiFormat(Descriptor(type: "anthropic"), "test-model"));
        Assert.Equal(
            ApiFormats.TypeSafeSystemOne,
            ChannelTestService.ChannelTestApiFormat(Descriptor(type: "typesafe"), "test-model"));
    }

    [Theory]
    [InlineData("""["openai/chat_completions","typesafe/systemone"]""", ApiFormats.OpenAIChatCompletion)]
    [InlineData("""["typesafe/systemone","openai/chat_completions"]""", ApiFormats.TypeSafeSystemOne)]
    public void ChannelTestApiFormatUsesModelProtocolPriority(string formats, string expected)
    {
        var channel = Descriptor(
            endpoints: """[{"api_format":"typesafe/systemone","path":"/systemone"}]""",
            settings: "{\"extraModelPrefix\":\"alias\",\"modelProtocols\":"
                + "[{\"model\":\"test-model\",\"apiFormats\":" + formats + "}]}");

        Assert.Equal(expected, ChannelTestService.ChannelTestApiFormat(channel, "test-model"));
        Assert.Equal(expected, ChannelTestService.ChannelTestApiFormat(channel, "alias/test-model"));
    }

    [Fact]
    public void ParseDecisionsTestResponseRequiresAnswers()
    {
        const string body = """{"model":"gpt-6-luna","answers":[{"name":"answer","probability":1}]}""";

        var message = ChannelTestService.ParseDecisionsTestResponse(Encoding.UTF8.GetBytes(body));
        Assert.Equal(body, message);

        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelTestService.ParseDecisionsTestResponse(
                Encoding.UTF8.GetBytes("""{"model":"gpt-6-luna","answers":[]}""")));
        Assert.Equal("no answers in Decisions response", error.Message);
    }

    [Fact]
    public void UsesResponsesWebSocketDetectsExplicitAndInferredTransports()
    {
        Assert.True(ChannelTestService.UsesResponsesWebSocket(
            Descriptor(type: "openai_responses", baseUrl: "wss://api.openai.com/v1")));

        Assert.True(ChannelTestService.UsesResponsesWebSocket(
            Descriptor(
                type: "openai",
                baseUrl: "https://api.example.com/v1",
                endpoints: """[{"api_format":"openai/responses","transport":"websocket"}]""")));

        Assert.False(ChannelTestService.UsesResponsesWebSocket(
            Descriptor(type: "openai_responses", baseUrl: "https://api.openai.com/v1")));
    }

    [Theory]
    [InlineData("", "****")]
    [InlineData("12345678", "****")]
    [InlineData("123456789", "1234****6789")]
    [InlineData("ééééé", "éé****éé")]
    public void MaskApiKeyKeepsTheReferenceByteWidths(string key, string expected)
        => Assert.Equal(expected, ChannelTestService.MaskApiKey(key));

    // -- Helpers ------------------------------------------------------------

    private ChannelTestService NewService(
        ScriptedExecutor executor,
        IPromptProtectionRuleProvider? rules = null)
    {
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            promptProtectionRules: rules);

        return new ChannelTestService(
            _channels,
            new SystemChannelSettingsService(_systems),
            orchestrator,
            TimeProvider.System,
            rules);
    }

    private void SeedPrompts(
        string systemPrompt = "system prompt",
        string userPrompt = "user prompt")
        => _systems.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["test_system_prompt"] = systemPrompt,
                ["test_user_prompt"] = userPrompt,
            }));

    private ChannelRecord SeedChannel(
        string type = "openai",
        string baseUrl = "http://upstream.test/v1",
        string credentials = """{"apiKey":"test-key-1"}""",
        string supportedModels = """["test-model"]""",
        string defaultTestModel = "test-model",
        string? policies = null,
        string? settings = null,
        string? endpoints = null,
        string? disabledApiKeys = null,
        string name = "Alpha")
    {
        var record = new ChannelRecord
        {
            Type = type,
            BaseUrl = baseUrl,
            Name = name,
            Status = "enabled",
            Credentials = credentials,
            SupportedModels = supportedModels,
            DefaultTestModel = defaultTestModel,
            Policies = policies,
            Settings = settings,
            Endpoints = endpoints,
            DisabledApiKeys = disabledApiKeys,
        };

        _channels.Seed(record);
        return record;
    }

    private static ChannelDescriptor Descriptor(
        string type = "openai",
        string? baseUrl = "https://api.example.com/v1",
        string? endpoints = null,
        string? settings = null,
        string supportedModels = """["test-model"]""")
        => new()
        {
            Id = 1,
            Name = "Alpha",
            OrderingWeight = 0,
            Type = type,
            BaseUrl = baseUrl,
            Credentials = "{}",
            SupportedModels = supportedModels,
            Endpoints = endpoints,
            Settings = settings,
            DefaultTestModel = "test-model",
        };

    /// <summary>
    /// FinalizeAuthHeaders folds the AuthConfig into provider-specific headers
    /// before the executor runs, so assertions read the header the reference
    /// would put on the wire.
    /// </summary>
    private static string? RequestKey(HttpRequestData request)
    {
        if (request.Headers is null)
        {
            return null;
        }

        if (request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization[0];
            return value.StartsWith("Bearer ", StringComparison.Ordinal)
                ? value["Bearer ".Length..]
                : value;
        }

        if (request.Headers.TryGetValue("X-Goog-Api-Key", out var google))
        {
            return google[0];
        }

        return request.Headers.TryGetValue("X-Api-Key", out var anthropic)
            ? anthropic[0]
            : null;
    }

    private static StaticPromptProtectionRuleProvider Rules(string action, string scope)
        => new(
        [
            new PromptProtectionRule
            {
                Id = 1,
                Name = action + "-" + scope,
                Pattern = "secret",
                Settings = new PromptProtectionSettings
                {
                    Action = action,
                    Replacement = "masked",
                    Scopes = [scope],
                },
            },
        ]);

    private static void AssertFormatRequest(
        HttpRequestData request,
        string channelType,
        bool stream)
    {
        Assert.True(RequestKey(request) is "test-key-1" or "test-key-2");

        using var body = JsonDocument.Parse(request.Body!);
        var root = body.RootElement;
        var path = new Uri(request.Url!).AbsolutePath;

        switch (channelType)
        {
            case "typesafe":
                Assert.Equal("/v1/systemone", path);
                Assert.Equal("test-model", root.GetProperty("model").GetString());
                Assert.Equal(
                    "system prompt\n\nuser prompt",
                    root.GetProperty("state").GetString());
                Assert.Equal(
                    "noul",
                    root.GetProperty("questions").GetProperty("connection")
                        .GetProperty("type").GetString());
                Assert.False(root.TryGetProperty("messages", out _));
                Assert.False(root.TryGetProperty("stream", out _));
                Assert.False(root.TryGetProperty("max_tokens", out _));
                return;

            case "gemini":
                Assert.Equal("/v1beta/models/test-model:generateContent", path);
                Assert.True(root.TryGetProperty("contents", out _));
                Assert.True(root.TryGetProperty("systemInstruction", out _));
                Assert.False(root.TryGetProperty("messages", out _));
                return;

            case "anthropic":
                Assert.Equal("/v1/messages", path);
                Assert.Equal("test-model", root.GetProperty("model").GetString());
                Assert.NotEqual(JsonValueKind.Null, root.GetProperty("system").ValueKind);
                Assert.Equal(256, root.GetProperty("max_tokens").GetInt64());
                Assert.True(root.TryGetProperty("messages", out _));
                return;

            default:
                Assert.Equal("/v1/chat/completions", path);
                Assert.Equal("test-model", root.GetProperty("model").GetString());
                Assert.True(root.TryGetProperty("messages", out _));
                Assert.Equal(stream, root.GetProperty("stream").GetBoolean());
                return;
        }
    }

    private static IReadOnlyList<StreamEvent> StreamChunks() =>
    [
        Event("""{"id":"test","model":"test-model","choices":[{"index":0,"delta":{"content":"hello"}}]}"""),
        Event("""{"id":"test","model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""),
        Event("[DONE]"),
    ];

    private static StreamEvent Event(string data)
        => new() { Data = Encoding.UTF8.GetBytes(data) };

    private static HttpResponseData Json(string body)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };
}
