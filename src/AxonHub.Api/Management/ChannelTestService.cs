using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Orchestration;
using AxonHub.Api.PromptProtection;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
// Orchestration also declares an internal GoJson helper (OverrideJson); the
// tester marshals wire bodies, so bind the LLM module's implementation.
using GoJson = AxonHub.Llm.Json.GoJson;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Llm.Transformer.TypeSafe;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of orchestrator.TestChannelResult (tester.go:262-268).</summary>
public sealed record TestChannelResult(
    double Latency,
    bool Success,
    string? Message,
    string? Error);

/// <summary>Port of orchestrator.TestAPIKeyResult (tester.go:466-473).</summary>
public sealed record TestApiKeyResult(
    string KeyPrefix,
    bool Success,
    double Latency,
    string? Error,
    bool Disabled);

/// <summary>Port of orchestrator.TestChannelAPIKeysResult (tester.go:475-482).</summary>
public sealed record TestChannelApiKeysResult(
    long ChannelId,
    int Total,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<TestApiKeyResult> Results);

/// <summary>
/// Port of orchestrator.TestChannelOrchestrator
/// (internal/server/orchestrator/tester.go): connectivity checks for one
/// channel, all of its API keys or a single key, run through the same chat
/// orchestration pipeline with a <c>SpecifiedChannelSelector</c> substitute.
/// </summary>
/// <remarks>
/// <para>
/// The reference builds a fresh <c>ChatCompletionOrchestrator</c> per test with
/// the tester's inbound transformer, a stub prompt provider, the prompt
/// protection rule service and <c>stream.EnsureUsage</c> as a global middleware.
/// The port reuses the scoped orchestrator through
/// <see cref="ChatCompletionOrchestrator.ProcessSpecifiedAsync"/>, which mounts
/// the same inbound transformer and <c>EnsureUsageMiddleware</c> and skips the
/// rate-limit admission/tracking pair the reference also leaves off for tests.
/// </para>
/// <para>
/// <see cref="TestChannelAsync"/> accepts the reference's per-request proxy
/// (<c>TestChannelInput.proxy</c>) and forwards it to the orchestrator, where it
/// overrides the channel's persisted <c>settings.proxy</c>. The key-test
/// entrypoints pass no proxy, exactly like the reference (the resolvers pass
/// nil for both API-key flows, axonhub.resolvers.go:333-372).
/// </para>
/// </remarks>
public sealed class ChannelTestService(
    IChannelWriteStore channels,
    IChannelTestPromptsProvider prompts,
    ChatCompletionOrchestrator orchestrator,
    TimeProvider timeProvider,
    IPromptProtectionRuleProvider? promptProtectionRules = null)
{
    /// <summary>tester.go:31 <c>testChannelAPIKeysMaxConcurrency</c>.</summary>
    public const int TestChannelApiKeysMaxConcurrency = 8;

    /// <summary>tester.go:33 <c>responsesWebSocketTestPrompt</c>.</summary>
    public const string ResponsesWebSocketTestPrompt = "ping";

    /// <summary>Port of the reference's <c>context.Canceled.Error()</c> text.</summary>
    private const string ContextCanceledMessage = "context canceled";

    /// <summary>
    /// Port of TestChannelOrchestrator.TestChannel (tester.go:271-378): test the
    /// channel with the admin-supplied or default test model.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The channel cannot be loaded or the test input cannot be built; the
    /// caller wraps the message exactly like the reference resolver.
    /// </exception>
    /// <param name="proxy">
    /// The admin-supplied proxy override (tester.go:271-309's <c>proxy</c>
    /// argument); null keeps the channel's persisted proxy setting.
    /// </param>
    public async Task<TestChannelResult> TestChannelAsync(
        long channelId,
        string? modelId,
        CancellationToken cancellationToken = default,
        ProxyConfig? proxy = null)
    {
        var channel = await LoadChannelAsync(channelId, cancellationToken).ConfigureAwait(false);

        var testModel = modelId ?? string.Empty;
        if (testModel.Length == 0)
        {
            testModel = channel.DefaultTestModel;
        }

        var testPrompts = await prompts
            .GetChannelTestPromptsAsync(cancellationToken)
            .ConfigureAwait(false);
        var descriptor = ChannelMapping.ToDescriptor(channel);
        var useStream = StreamRequired(channel.Policies);
        var apiFormat = ChannelTestApiFormat(descriptor, testModel);

        var (inbound, body) = await BuildChannelTestInputAsync(
                testModel,
                useStream,
                testPrompts.SystemPrompt,
                testPrompts.UserPrompt,
                UsesResponsesWebSocket(descriptor),
                apiFormat,
                cancellationToken)
            .ConfigureAwait(false);

        var startTimestamp = timeProvider.GetTimestamp();

        ChatCompletionResult result;
        try
        {
            result = await orchestrator
                .ProcessSpecifiedAsync(
                    inbound,
                    TestRequest(body),
                    (llmRequest, token) => BuildCandidateAsync(
                        channelId,
                        llmRequest,
                        apiKeyOverride: null,
                        token),
                    TestIdentity,
                    cancellationToken,
                    proxy)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailedTestResult(inbound, ex, startTimestamp);
        }
        catch (OperationCanceledException)
        {
            // Go's ctx.Err() carries "context canceled"; the .NET runtime's
            // OperationCanceledException message does not (C155).
            return FailedTestResult(inbound, ContextCanceledError(), startTimestamp);
        }

        if (result.Stream is not null)
        {
            return HandleStreamResponse(cancellationToken, result.Stream, startTimestamp);
        }

        var responseBody = result.Response?.Body ?? [];

        if (apiFormat == ApiFormats.OpenAIDecisions)
        {
            try
            {
                var message = ParseDecisionsTestResponse(responseBody);
                return new TestChannelResult(
                    ElapsedSeconds(startTimestamp),
                    Success: true,
                    Message: message,
                    Error: null);
            }
            catch (Exception ex)
            {
                return new TestChannelResult(
                    ElapsedSeconds(startTimestamp),
                    Success: false,
                    Message: string.Empty,
                    Error: ex.Message);
            }
        }

        var latency = ElapsedSeconds(startTimestamp);
        try
        {
            var responseMessage = ChannelTestResponseMessage(responseBody, apiFormat);
            return new TestChannelResult(latency, Success: true, responseMessage, Error: null);
        }
        catch (Exception ex)
        {
            return new TestChannelResult(latency, Success: false, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Port of TestChannelOrchestrator.TestChannelAPIKeys (tester.go:485-577):
    /// test every configured key concurrently, capped at eight in flight.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The channel cannot be loaded or has no API keys; the caller wraps the
    /// message exactly like the reference resolver.
    /// </exception>
    public async Task<TestChannelApiKeysResult> TestChannelApiKeysAsync(
        long channelId,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await LoadChannelAsync(channelId, cancellationToken).ConfigureAwait(false);

        var allKeys = ChannelCredentials.GetAllApiKeys(channel.Credentials);
        if (allKeys.Count == 0)
        {
            throw new InvalidOperationException("no API keys configured for channel");
        }

        // The reference reads every stored disable record without the expiry
        // filter the enabled-key query applies.
        var disabledSet = DisabledKeySet(channel.DisabledApiKeys);

        var testModel = modelId ?? string.Empty;
        if (testModel.Length == 0)
        {
            testModel = channel.DefaultTestModel;
        }

        var descriptor = ChannelMapping.ToDescriptor(channel);
        var useStream = StreamRequired(channel.Policies);
        var apiFormat = ChannelTestApiFormat(descriptor, testModel);
        var responsesWebSocket = UsesResponsesWebSocket(descriptor);
        var testPrompts = await prompts
            .GetChannelTestPromptsAsync(cancellationToken)
            .ConfigureAwait(false);

        var results = new TestApiKeyResult[allKeys.Count];
        var successCount = 0;
        var failedCount = 0;

        // errgroup.WithContext: the group context is only cancelled when the
        // caller's context is; per-key tasks never return errors.
        using var groupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var groupToken = groupCts.Token;
        using var semaphore = new SemaphoreSlim(
            Math.Min(TestChannelApiKeysMaxConcurrency, allKeys.Count));

        var tasks = new Task[allKeys.Count];
        for (var index = 0; index < allKeys.Count; index++)
        {
            var keyIndex = index;
            var apiKey = allKeys[index];

            tasks[index] = Task.Run(
                async () =>
                {
                    var acquired = false;
                    try
                    {
                        try
                        {
                            await semaphore.WaitAsync(groupToken).ConfigureAwait(false);
                            acquired = true;
                        }
                        catch (OperationCanceledException)
                        {
                            // A cancelled queue wait must still produce the
                            // reference's per-key cancellation result.
                        }

                        if (groupToken.IsCancellationRequested)
                        {
                            results[keyIndex] = new TestApiKeyResult(
                                MaskApiKey(apiKey),
                                Success: false,
                                Latency: 0,
                                ContextCanceledMessage,
                                Disabled: false);
                            Interlocked.Increment(ref failedCount);
                            return;
                        }

                        var result = await TestSingleKeyAsync(
                                channelId,
                                apiKey,
                                testModel,
                                useStream,
                                responsesWebSocket,
                                testPrompts.SystemPrompt,
                                testPrompts.UserPrompt,
                                apiFormat,
                                groupToken)
                            .ConfigureAwait(false);

                        results[keyIndex] = result with
                        {
                            Disabled = disabledSet.Contains(apiKey),
                        };

                        if (result.Success)
                        {
                            Interlocked.Increment(ref successCount);
                            return;
                        }

                        Interlocked.Increment(ref failedCount);
                    }
                    finally
                    {
                        if (acquired)
                        {
                            semaphore.Release();
                        }
                    }
                },
                CancellationToken.None);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return new TestChannelApiKeysResult(
            channelId,
            Total: allKeys.Count,
            SuccessCount: successCount,
            FailedCount: failedCount,
            Results: results);
    }

    /// <summary>
    /// Port of TestChannelOrchestrator.TestSingleAPIKey (tester.go:581-627): the
    /// key must belong to the channel before it is tested in isolation.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The channel cannot be loaded, has no API keys, or does not own the key;
    /// the caller wraps the message exactly like the reference resolver.
    /// </exception>
    public async Task<TestApiKeyResult> TestSingleApiKeyAsync(
        long channelId,
        string key,
        string? modelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await LoadChannelAsync(channelId, cancellationToken).ConfigureAwait(false);

        var channelKeys = ChannelCredentials.GetAllApiKeys(channel.Credentials);
        if (channelKeys.Count == 0)
        {
            throw new InvalidOperationException("no API keys configured for channel");
        }

        if (!channelKeys.Contains(key, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "the provided API key is not configured for this channel");
        }

        var testModel = modelId ?? string.Empty;
        if (testModel.Length == 0)
        {
            testModel = channel.DefaultTestModel;
        }

        var descriptor = ChannelMapping.ToDescriptor(channel);
        var useStream = StreamRequired(channel.Policies);
        var apiFormat = ChannelTestApiFormat(descriptor, testModel);
        var responsesWebSocket = UsesResponsesWebSocket(descriptor);
        var testPrompts = await prompts
            .GetChannelTestPromptsAsync(cancellationToken)
            .ConfigureAwait(false);

        var disabledSet = DisabledKeySet(channel.DisabledApiKeys);

        var result = await TestSingleKeyAsync(
                channelId,
                key,
                testModel,
                useStream,
                responsesWebSocket,
                testPrompts.SystemPrompt,
                testPrompts.UserPrompt,
                apiFormat,
                cancellationToken)
            .ConfigureAwait(false);

        return result with { Disabled = disabledSet.Contains(key) };
    }

    /// <summary>Port of testSingleKey (tester.go:630-748).</summary>
    private async Task<TestApiKeyResult> TestSingleKeyAsync(
        long channelId,
        string key,
        string testModel,
        bool useStream,
        bool responsesWebSocket,
        string systemPrompt,
        string userPrompt,
        string apiFormat,
        CancellationToken cancellationToken)
    {
        var keyPrefix = MaskApiKey(key);

        IInboundTransformer inbound;
        byte[] body;
        try
        {
            (inbound, body) = await BuildChannelTestInputAsync(
                    testModel,
                    useStream,
                    systemPrompt,
                    userPrompt,
                    responsesWebSocket,
                    apiFormat,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TestApiKeyResult(keyPrefix, Success: false, Latency: 0, ex.Message, Disabled: false);
        }
        catch (OperationCanceledException)
        {
            return new TestApiKeyResult(
                keyPrefix,
                Success: false,
                Latency: 0,
                ContextCanceledMessage,
                Disabled: false);
        }

        var startTimestamp = timeProvider.GetTimestamp();

        ChatCompletionResult result;
        try
        {
            result = await orchestrator
                .ProcessSpecifiedAsync(
                    inbound,
                    TestRequest(body),
                    (llmRequest, token) => BuildCandidateAsync(
                        channelId,
                        llmRequest,
                        key,
                        token),
                    TestIdentity,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var rawError = inbound.TransformError(ex);
            return new TestApiKeyResult(
                keyPrefix,
                Success: false,
                ElapsedSeconds(startTimestamp),
                PromptProtectionJson.String(rawError.Body ?? [], "error.message"),
                Disabled: false);
        }
        catch (OperationCanceledException)
        {
            var rawError = inbound.TransformError(ContextCanceledError());
            return new TestApiKeyResult(
                keyPrefix,
                Success: false,
                ElapsedSeconds(startTimestamp),
                PromptProtectionJson.String(rawError.Body ?? [], "error.message"),
                Disabled: false);
        }

        if (result.Stream is not null)
        {
            var streamResult = HandleStreamResponse(
                cancellationToken,
                result.Stream,
                startTimestamp);

            return new TestApiKeyResult(
                keyPrefix,
                streamResult.Success,
                streamResult.Latency,
                streamResult.Error,
                Disabled: false);
        }

        var responseBody = result.Response?.Body ?? [];

        if (apiFormat == ApiFormats.OpenAIDecisions)
        {
            try
            {
                ParseDecisionsTestResponse(responseBody);
                return new TestApiKeyResult(
                    keyPrefix,
                    Success: true,
                    ElapsedSeconds(startTimestamp),
                    Error: null,
                    Disabled: false);
            }
            catch (Exception ex)
            {
                return new TestApiKeyResult(
                    keyPrefix,
                    Success: false,
                    ElapsedSeconds(startTimestamp),
                    ex.Message,
                    Disabled: false);
            }
        }

        var latency = ElapsedSeconds(startTimestamp);
        try
        {
            ChannelTestResponseMessage(responseBody, apiFormat);
            return new TestApiKeyResult(keyPrefix, Success: true, latency, Error: null, Disabled: false);
        }
        catch (Exception ex)
        {
            return new TestApiKeyResult(keyPrefix, Success: false, latency, ex.Message, Disabled: false);
        }
    }

    /// <summary>Port of handleStreamResponse (tester.go:381-464).</summary>
    private TestChannelResult HandleStreamResponse(
        CancellationToken cancellationToken,
        IStream<StreamEvent> stream,
        long startTimestamp)
    {
        using (stream)
        {
            var accumulated = new StringBuilder();

            while (stream.Next())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new TestChannelResult(
                        ElapsedSeconds(startTimestamp),
                        Success: false,
                        accumulated.ToString(),
                        ContextCanceledMessage);
                }

                var @event = stream.Current;
                if (@event is null)
                {
                    continue;
                }

                // The stream may end with a "[DONE]" message which is not valid JSON.
                if (@event.Data.AsSpan().SequenceEqual("[DONE]"u8))
                {
                    continue;
                }

                LlmResponse? chunk;
                try
                {
                    chunk = LlmJson.Deserialize<LlmResponse>(@event.Data);
                }
                catch (JsonException)
                {
                    // The reference logs and skips undecodable events.
                    continue;
                }

                if (chunk is not null
                    && chunk.Choices.Count > 0
                    && chunk.Choices[0].Delta is { } delta
                    && delta.Content?.Content is { } content)
                {
                    accumulated.Append(content);
                }
            }

            var latency = ElapsedSeconds(startTimestamp);

            if (cancellationToken.IsCancellationRequested)
            {
                return new TestChannelResult(
                    latency,
                    Success: false,
                    accumulated.ToString(),
                    ContextCanceledMessage);
            }

            if (stream.Err is { } error)
            {
                return new TestChannelResult(latency, Success: false, string.Empty, error.Message);
            }

            if (accumulated.Length == 0)
            {
                return new TestChannelResult(
                    latency,
                    Success: false,
                    string.Empty,
                    "No content in stream response");
            }

            return new TestChannelResult(latency, Success: true, accumulated.ToString(), Error: null);
        }
    }

    /// <summary>
    /// Port of buildChannelTestInput (tester.go:203-232). The systemone path is
    /// fail-closed: a rule load or a protection rejection fails the test before
    /// any request is sent.
    /// </summary>
    private async Task<(IInboundTransformer Inbound, byte[] Body)> BuildChannelTestInputAsync(
        string model,
        bool useStream,
        string systemPrompt,
        string userPrompt,
        bool responsesWebSocket,
        string apiFormat,
        CancellationToken cancellationToken)
    {
        if (apiFormat == ApiFormats.TypeSafeSystemOne)
        {
            if (useStream)
            {
                throw new InvalidOperationException("systemone does not support streaming");
            }

            // Protect role-scoped prompts before System One combines them into State.
            var testRequest = BuildChannelTestRequest(
                model,
                useStream: false,
                systemPrompt,
                userPrompt,
                responsesWebSocket: false,
                ApiFormats.OpenAIChatCompletion);
            var protectedRequest = await ProtectPromptsAsync(testRequest, cancellationToken)
                .ConfigureAwait(false);

            var state = ContentText(protectedRequest.Messages[0])
                + "\n\n"
                + ContentText(protectedRequest.Messages[1]);

            var body = GoJson.Marshal(new SystemOneChannelTestRequest
            {
                State = JsonSerializer.SerializeToElement(state),
                Questions = new Dictionary<string, SystemOneQuestion>(StringComparer.Ordinal)
                {
                    ["connection"] = new()
                    {
                        Type = "noul",
                        Instructions = JsonSerializer.SerializeToElement(
                            "Does the state contain a test prompt?"),
                    },
                },
                Model = model,
            });

            return (new SystemOneInboundTransformer(), body);
        }

        var request = BuildChannelTestRequest(
            model,
            useStream,
            systemPrompt,
            userPrompt,
            responsesWebSocket,
            apiFormat);

        return (ChannelTestInbound(apiFormat), GoJson.Marshal(request));
    }

    /// <summary>
    /// Port of the <c>promptProtectionRuleService.Protect(ctx, prompts)</c> call
    /// in buildChannelTestInput: unlike the pipeline's protect-prompts
    /// middleware, the tester's direct call propagates both load failures and
    /// rejection (fail closed).
    /// </summary>
    private async Task<LlmRequest> ProtectPromptsAsync(
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        if (promptProtectionRules is null)
        {
            return request;
        }

        var rules = await promptProtectionRules
            .ListEnabledAsync(cancellationToken)
            .ConfigureAwait(false);

        var result = new PromptProtectionRuleProtector(rules).ProtectWithResult(request);

        return result.Request ?? request;
    }

    /// <summary>Port of buildChannelTestRequest (tester.go:79-128).</summary>
    internal static LlmRequest BuildChannelTestRequest(
        string model,
        bool useStream,
        string systemPrompt,
        string userPrompt,
        bool responsesWebSocket,
        string apiFormat)
    {
        if (apiFormat == ApiFormats.OpenAIDecisions)
        {
            // xjson.MustMarshal of a map: the Go map marshal sorts keys.
            var body = GoJson.Marshal(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["input"] = userPrompt,
                ["questions"] = new object[]
                {
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["name"] = "answer",
                        ["type"] = "predicate",
                        ["predicate"] = "Is this a channel connectivity test?",
                    },
                },
            });

            return new LlmRequest
            {
                // Go leaves Messages nil; the marshalled request carries
                // "messages":null.
                Messages = null!,
                Model = model,
                RequestType = RequestTypes.Decisions,
                ApiFormat = ApiFormats.OpenAIDecisions,
                Stream = false,
                Decisions = new DecisionsRequest { Body = body },
            };
        }

        var request = new LlmRequest
        {
            Model = model,
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = MessageContent.FromText(systemPrompt),
                },
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromText(userPrompt),
                },
            ],
            MaxCompletionTokens = 256,
            Stream = useStream,
        };

        if (responsesWebSocket)
        {
            request.Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromText(ResponsesWebSocketTestPrompt),
                },
            ];
            request.MaxCompletionTokens = null;
            request.Stream = true;
        }

        return request;
    }

    /// <summary>Port of channelTestAPIFormat (tester.go:130-151).</summary>
    internal static string ChannelTestApiFormat(ChannelDescriptor channel, string model)
    {
        var endpoints = ChannelEndpoints.Resolve(channel.Type, channel.Endpoints, channel.BaseUrl);

        if (!ChannelModelEntries.Build(channel).TryGetValue(model, out var entry)
            && !ChannelModelEntries.BuildDirect(channel).TryGetValue(model, out entry))
        {
            // Go's zero ChannelModelEntry: no request/actual model.
            entry = new ChannelModelEntry(string.Empty, string.Empty, string.Empty);
        }

        var forced = ChannelEndpointSelection.ForcedApiFormats(channel, [entry], model);
        var filtered = ChannelEndpointSelection.FilterEndpointsByApiFormats(endpoints, forced);
        if (filtered is { Count: > 0 })
        {
            // The reference narrows to the first matching endpoint only.
            endpoints = [filtered[0]];
        }

        foreach (var endpoint in endpoints)
        {
            if (endpoint.ApiFormat == ApiFormats.OpenAIDecisions)
            {
                return ApiFormats.OpenAIDecisions;
            }
        }

        // Chat is the default test surface, and it wins over System One unless the
        // channel serves nothing else (tester.go:153-166).
        foreach (var endpoint in endpoints)
        {
            if (endpoint.ApiFormat == ApiFormats.OpenAIChatCompletion)
            {
                return ApiFormats.OpenAIChatCompletion;
            }
        }

        foreach (var endpoint in endpoints)
        {
            if (endpoint.ApiFormat == ApiFormats.TypeSafeSystemOne)
            {
                return ApiFormats.TypeSafeSystemOne;
            }
        }

        return ApiFormats.OpenAIChatCompletion;
    }

    /// <summary>Port of channelTestInbound (tester.go:153-158).</summary>
    private static IInboundTransformer ChannelTestInbound(string apiFormat)
        => apiFormat == ApiFormats.OpenAIDecisions
            ? new DecisionsInboundTransformer()
            : new OpenAiInboundTransformer();

    /// <summary>Port of usesResponsesWebSocket (tester.go:189-221).</summary>
    internal static bool UsesResponsesWebSocket(ChannelDescriptor channel)
    {
        foreach (var endpoint in ChannelEndpoints.Resolve(channel.Type, channel.Endpoints, channel.BaseUrl))
        {
            if (endpoint.ApiFormat == ApiFormats.OpenAIResponseWebSocket)
            {
                return true;
            }

            if (endpoint.ApiFormat is not (ApiFormats.OpenAIResponse or ApiFormats.OpenAIResponseCompact))
            {
                continue;
            }

            var transport = GoStrings.TrimSpace(endpoint.Transport).ToLowerInvariant();
            if (transport == ChannelEndpointTransports.WebSocket)
            {
                return true;
            }

            if (transport.Length != 0)
            {
                continue;
            }

            var baseUrl = endpoint.BaseUrl;
            if (baseUrl.Length == 0)
            {
                baseUrl = channel.BaseUrl ?? string.Empty;
            }

            baseUrl = GoStrings.TrimSpace(baseUrl).ToLowerInvariant();
            if (baseUrl.StartsWith("ws://", StringComparison.Ordinal)
                || baseUrl.StartsWith("wss://", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of parseDecisionsTestResponse (tester.go:160-169). The success value
    /// is the raw response body, not the decoded envelope.
    /// </summary>
    internal static string ParseDecisionsTestResponse(byte[] body)
    {
        DecisionsResponse? response;
        try
        {
            response = LlmJson.Deserialize<DecisionsResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("invalid Decisions response: " + ex.Message, ex);
        }

        if (response?.Answers is not { Count: > 0 })
        {
            throw new InvalidOperationException("no answers in Decisions response");
        }

        return Encoding.UTF8.GetString(body);
    }

    /// <summary>Port of channelTestResponseMessage (tester.go:234-260).</summary>
    internal static string? ChannelTestResponseMessage(byte[] body, string apiFormat)
    {
        if (apiFormat == ApiFormats.TypeSafeSystemOne)
        {
            SystemOneResponse? response;
            try
            {
                response = LlmJson.Deserialize<SystemOneResponse>(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }

            if (response?.Answers is not { Count: > 0 })
            {
                throw new InvalidOperationException("no answers in System One response");
            }

            if (!response.Answers.ContainsKey("connection"))
            {
                throw new InvalidOperationException("no connection answer in System One response");
            }

            return GoJson.MarshalToString(response.Answers);
        }

        LlmResponse? completion;
        try
        {
            completion = LlmJson.Deserialize<LlmResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (completion is null || completion.Choices.Count == 0)
        {
            throw new InvalidOperationException("No message in response");
        }

        // Go dereferences Choice.Message and its Content pointer; a response
        // without a message panics there. The port reports a null message
        // instead of failing the test (difference C156).
        return completion.Choices[0].Message?.Content?.Content;
    }

    /// <summary>
    /// Port of the SpecifiedChannelSelector.Select candidate build
    /// (candidates.go:1042-1078). The channel is re-read per attempt, exactly
    /// like the reference's selector, so a concurrent edit is observed; an
    /// empty API key means the selector's plain GetChannel path.
    /// </summary>
    private async Task<ChannelCandidate?> BuildCandidateAsync(
        long channelId,
        LlmRequest llmRequest,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        ChannelRecord channel;
        try
        {
            channel = await LoadChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get channel for test: " + ex.Message,
                ex);
        }

        var descriptor = ChannelMapping.ToDescriptor(channel);
        var model = llmRequest.Model;

        if (!ChannelModelEntries.Build(descriptor).TryGetValue(model, out var entry)
            && !ChannelModelEntries.BuildDirect(descriptor).TryGetValue(model, out entry))
        {
            throw new InvalidOperationException(
                "model " + model + " not supported in channel " + descriptor.Name);
        }

        var endpoints = ChannelEndpointSelection.ApplyForcedApiFormatsForRequest(
            descriptor,
            [entry],
            model,
            llmRequest.RequestType,
            ChannelEndpoints.Resolve(descriptor.Type, descriptor.Endpoints, descriptor.BaseUrl));

        // A rejected explicit-format request has no endpoints; SelectAPIFormat
        // then yields "" and the candidate is rejected below.
        var apiFormat = ChannelEndpointSelection.SelectApiFormat(
            endpoints ?? [],
            llmRequest);

        if (ChannelEndpointSelection.RequiresExplicitEndpoint(llmRequest.RequestType)
            && apiFormat.Length == 0)
        {
            // The reference returns an empty candidate list, which the caller
            // reports as the invalid-model error.
            return null;
        }

        return new ChannelCandidate
        {
            ChannelId = descriptor.Id,
            ChannelName = descriptor.Name,
            ActualModel = entry.ActualModel,
            OrderingWeight = descriptor.OrderingWeight,
            Score = 0,
            UpdatedAt = descriptor.UpdatedAt,
            Type = descriptor.Type,
            BaseUrl = descriptor.BaseUrl,
            Credentials = descriptor.Credentials,
            DisabledApiKeys = descriptor.DisabledApiKeys,
            RetrySettings = ChannelRetrySettings.Parse(descriptor.Settings),
            OverrideSettings = ChannelOverrideSettings.Parse(descriptor.Settings),
            RateLimitSettings = ChannelRateLimitSettings.Parse(descriptor.Settings),
            PassThroughSettings = ChannelPassThroughSettings.Parse(descriptor.Settings),
            DefaultTestModel = ResolveDefaultTestModel(descriptor),
            ApiFormat = apiFormat,
            ApiKeyOverride = string.IsNullOrEmpty(apiKeyOverride) ? null : apiKeyOverride,
            Settings = descriptor.Settings,
            Endpoints = descriptor.Endpoints,
            Tags = descriptor.Tags,
            Policies = descriptor.Policies,
        };
    }

    /// <summary>
    /// Port of the default-test-model resolution used by ChannelPoolGrain
    /// (channel_llm.go:304-317), shared here because the tester's candidate
    /// carries the same field.
    /// </summary>
    private static string ResolveDefaultTestModel(ChannelDescriptor channel)
    {
        var model = channel.DefaultTestModel.Trim();
        if (model.Length == 0)
        {
            return model;
        }

        return ChannelModelEntries.ChooseModel(channel, model) ?? model;
    }

    /// <summary>
    /// Port of the reference's channel load in ChannelService.GetChannel
    /// (channel.go:397-418): ent.Get, with not-found and store failures carrying
    /// the ent wording.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist.</exception>
    private async Task<ChannelRecord> LoadChannelAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await channels.GetAsync(channelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "channel not found: ent: channel not found");
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException("channel not found: " + ex.Message, ex);
        }
    }

    /// <summary>Port of objects.ChannelPolicies.Stream == "require".</summary>
    private static bool StreamRequired(string? policiesJson)
    {
        if (string.IsNullOrWhiteSpace(policiesJson) || policiesJson == "null")
        {
            return false;
        }

        try
        {
            // Go decodes the enum from its JSON string; malformed JSON fails
            // the channel load there, while the port reads the raw column.
            var policies = LlmJson.Deserialize<ChannelPoliciesDraft>(policiesJson);
            return policies?.Stream == "require";
        }
        catch (JsonException)
        {
            // The port's channel load is lenient (difference C157).
            return false;
        }
    }

    /// <summary>Every stored disable record key, without the expiry filter.</summary>
    private static HashSet<string> DisabledKeySet(string? disabledApiKeysJson)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        List<DisabledApiKey> disabled;
        try
        {
            disabled = ChannelWriteSerialization.ParseDisabledApiKeys(disabledApiKeysJson);
        }
        catch (JsonException)
        {
            // ent fails the whole channel load on malformed JSON; the port
            // reads raw columns and stays lenient (difference C157).
            return set;
        }

        foreach (var key in disabled)
        {
            set.Add(key.Key);
        }

        return set;
    }

    /// <summary>
    /// Port of maskAPIKey (tester.go:750-757): byte-based, so non-ASCII keys
    /// keep the reference's exact prefix/suffix widths.
    /// </summary>
    internal static string MaskApiKey(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length <= 8)
        {
            return "****";
        }

        return Encoding.UTF8.GetString(bytes, 0, 4)
            + "****"
            + Encoding.UTF8.GetString(bytes, bytes.Length - 4, 4);
    }

    /// <summary>
    /// The reference's pipeline-error branch: the error is rewritten by the
    /// tester's inbound transformer and only its <c>error.message</c> survives.
    /// </summary>
    private TestChannelResult FailedTestResult(
        IInboundTransformer inbound,
        Exception error,
        long startTimestamp)
    {
        var rawError = inbound.TransformError(error);
        var message = PromptProtectionJson.String(
            rawError.Body ?? [],
            "error.message");

        return new TestChannelResult(
            ElapsedSeconds(startTimestamp),
            Success: false,
            Message: string.Empty,
            Error: message);
    }

    private double ElapsedSeconds(long startTimestamp)
        => timeProvider.GetElapsedTime(startTimestamp).TotalSeconds;

    private static HttpRequestData TestRequest(byte[] body)
        => new()
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = body,
        };

    /// <summary>
    /// The reference marks admin channel tests with contexts.SourceTest and no
    /// project; ProjectID 0 is what biz.RequestService.CreateRequest reads for
    /// such requests (request.go:182-240).
    /// </summary>
    private static RequestIdentity TestIdentity { get; } =
        new(0, null) { Source = RequestIdentity.SourceTest };

    private static OperationCanceledException ContextCanceledError()
        => new(ContextCanceledMessage);

    private static string ContentText(Message message)
        => message.Content?.Content ?? string.Empty;

    /// <summary>
    /// The anonymous struct embedded around llm.SystemOneRequest in
    /// buildChannelTestInput: fields in Go's embed order (state, questions),
    /// then model.
    /// </summary>
    private sealed class SystemOneChannelTestRequest
    {
        [JsonPropertyName("state")]
        [JsonConverter(typeof(GoAnyJsonConverter))]
        public JsonElement State { get; set; }

        [JsonPropertyName("questions")]
        public Dictionary<string, SystemOneQuestion>? Questions { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;
    }
}
