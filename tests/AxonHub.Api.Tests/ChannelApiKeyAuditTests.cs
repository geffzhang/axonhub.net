using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// End-to-end coverage of the execution audit columns
/// (internal/server/biz/request.go: channel_api_key_suffix and
/// channel_api_key_index): the suffix comes from the key actually sent upstream
/// and the index from its position in the channel's full key list.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ChannelApiKeyAuditTests(GrainClusterFixture fixture)
{
    private static readonly string[] LegacyAndNewKeys =
        ["sk-legacy-1111", "sk-alpha-2222", "sk-beta-3333"];

    private static readonly string[] OAuthKeys = ["sk-oauth-5555", "sk-oauth-6666"];

    private static readonly string[] FirstChannelKeys = ["sk-one-a-1111", "sk-one-b-2222"];

    private static readonly string[] SecondChannelKeys = ["sk-two-a-3333", "sk-two-b-4444"];

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder recorder)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static ChannelDescriptor Channel(
        long id,
        string model,
        string credentials,
        string? disabledApiKeys = null)
        => TestChannels.OpenAi(id, model) with
        {
            Credentials = credentials,
            DisabledApiKeys = disabledApiKeys,
        };

    private static string SentKey(HttpRequestData request)
    {
        const string prefix = "Bearer ";
        var authorization = HttpClientUtils.HeaderGet(request.Headers, "Authorization");
        Assert.StartsWith(prefix, authorization, StringComparison.Ordinal);
        return authorization[prefix.Length..];
    }

    private static int IndexOfKey(string key, params string[] allKeys)
    {
        var index = Array.IndexOf(allKeys, key);
        Assert.True(index >= 0, "upstream key " + key + " is not one of the configured keys");
        return index + 1;
    }

    private static HttpResponseData AnthropicCompletion(string model) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(
            "{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\""
            + model
            + "\",\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],"
            + "\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"),
    };

    private static HttpResponseData CodexCompletion(string model) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(
            "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1700000000,"
            + "\"status\":\"completed\",\"model\":\""
            + model
            + "\",\"output\":[{\"id\":\"msg_test\",\"type\":\"message\",\"status\":\"completed\","
            + "\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}]}"),
    };

    [Fact]
    public async Task MultiKeyAttemptRecordsTheSuffixAndIndexItSent()
    {
        fixture.Channels.Set(Channel(
            7101,
            "key-audit-multi",
            "{\"apiKey\":\"sk-legacy-1111\",\"apiKeys\":[\"sk-alpha-2222\",\"sk-beta-3333\"]}"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("key-audit-multi"),
        };
        var recorder = new RecordingRequestChainRecorder();

        var result = await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-multi"));

        Assert.Equal(200, result.Response!.StatusCode);

        var sentKey = SentKey(Assert.Single(executor.Requests));
        Assert.Contains(sentKey, LegacyAndNewKeys);

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal(sentKey[^4..], execution.ChannelApiKeySuffix);
        Assert.Equal(IndexOfKey(sentKey, LegacyAndNewKeys), execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task SingleKeyAttemptRecordsTheSuffixWithoutAnIndex()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7102, "key-audit-single", apiKey: "sk-single-4444"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("key-audit-single"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-single"));

        Assert.Equal("Bearer sk-single-4444", HttpClientUtils.HeaderGet(
            Assert.Single(executor.Requests).Headers,
            "Authorization"));

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal("4444", execution.ChannelApiKeySuffix);
        Assert.Null(execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task ShortKeysHaveNoSuffix()
    {
        fixture.Channels.Set(TestChannels.OpenAi(7103, "key-audit-short", apiKey: "sk-4"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("key-audit-short"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-short"));

        var execution = Assert.Single(recorder.Executions);
        Assert.Null(execution.ChannelApiKeySuffix);
        Assert.Null(execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task DisabledKeyIsNeitherSentNorIndexedOutOfTheFullList()
    {
        fixture.Channels.Set(Channel(
            7104,
            "key-audit-disabled",
            "{\"apiKeys\":[\"sk-alpha-1111\",\"sk-beta-2222\"]}",
            "[{\"key\":\"sk-alpha-1111\"}]"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("key-audit-disabled"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-disabled"));

        Assert.Equal("Bearer sk-beta-2222", HttpClientUtils.HeaderGet(
            Assert.Single(executor.Requests).Headers,
            "Authorization"));

        // The index counts the disabled key too, matching the reference.
        var execution = Assert.Single(recorder.Executions);
        Assert.Equal("2222", execution.ChannelApiKeySuffix);
        Assert.Equal(2, execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task KeysSharingTheirLastFourCharactersAreDisambiguatedByIndex()
    {
        fixture.Channels.Set(Channel(
            7108,
            "key-audit-same-suffix",
            "{\"apiKeys\":[\"sk-alpha-9999\",\"sk-bravo-9999\"]}",
            "[{\"key\":\"sk-alpha-9999\"}]"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("key-audit-same-suffix"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-same-suffix"));

        // The suffix cannot tell the two keys apart; the index can.
        var execution = Assert.Single(recorder.Executions);
        Assert.Equal("9999", execution.ChannelApiKeySuffix);
        Assert.Equal(2, execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task OAuthLegacyKeyIsNotTreatedAsAnApiKey()
    {
        fixture.Channels.Set(Channel(
            7105,
            "key-audit-oauth",
            "{\"apiKey\":\"{\\\"access_token\\\":\\\"tok\\\"}\","
            + "\"apiKeys\":[\"sk-oauth-5555\",\"sk-oauth-6666\"]}"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("key-audit-oauth"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-oauth"));

        var sentKey = SentKey(Assert.Single(executor.Requests));
        Assert.Contains(sentKey, OAuthKeys);

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal(sentKey[^4..], execution.ChannelApiKeySuffix);
        Assert.Equal(IndexOfKey(sentKey, OAuthKeys), execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task ClaudeCodeKeyChannelAuditsTheKeyItSends()
    {
        // channel_llm.go:973-975: the claudecode non-OAuth branch wraps its key
        // provider in ChannelAPIKeyContextProvider, so requests sent through it
        // are audited exactly like the OpenAI-family channels (C096).
        string[] allKeys = ["sk-claude-aaaa", "sk-claude-bbbb"];
        fixture.Channels.Set(Channel(
            7110,
            "key-audit-claudecode",
            "{\"apiKeys\":[\"sk-claude-aaaa\",\"sk-claude-bbbb\"]}") with
        {
            Type = "claudecode",
        });

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => AnthropicCompletion("key-audit-claudecode"),
        };
        var recorder = new RecordingRequestChainRecorder();

        var result = await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-claudecode"));

        Assert.Equal(200, result.Response!.StatusCode);

        var sentKey = SentKey(Assert.Single(executor.Requests));
        Assert.Contains(sentKey, allKeys);

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal(sentKey[^4..], execution.ChannelApiKeySuffix);
        Assert.Equal(IndexOfKey(sentKey, allKeys), execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task CodexKeyChannelAuditsTheKeyItSends()
    {
        // buildCodexOutbound uses the same wrapped key provider as the codex
        // OAuth-free path, so the audit row carries the key it sent (C096).
        string[] allKeys = ["sk-codex-aaaa", "sk-codex-bbbb"];
        fixture.Channels.Set(Channel(
            7111,
            "key-audit-codex",
            "{\"apiKeys\":[\"sk-codex-aaaa\",\"sk-codex-bbbb\"]}") with
        {
            Type = "codex",
        });

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => CodexCompletion("key-audit-codex"),
        };
        var recorder = new RecordingRequestChainRecorder();

        var result = await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-codex"));

        Assert.Equal(200, result.Response!.StatusCode);

        var sentKey = SentKey(Assert.Single(executor.Requests));
        Assert.Contains(sentKey, allKeys);

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal(sentKey[^4..], execution.ChannelApiKeySuffix);
        Assert.Equal(IndexOfKey(sentKey, allKeys), execution.ChannelApiKeyIndex);
    }

    [Fact]
    public async Task EveryRetryAttemptAuditsTheKeyItActuallySent()
    {
        fixture.Channels.Set(
            Channel(7106, "key-audit-retry", "{\"apiKeys\":[\"sk-one-a-1111\",\"sk-one-b-2222\"]}"),
            Channel(7107, "key-audit-retry", "{\"apiKeys\":[\"sk-two-a-3333\",\"sk-two-b-4444\"]}"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw TestChannels.HttpClient(500)
                : TestChannels.Completion("key-audit-retry"),
        };
        var recorder = new RecordingRequestChainRecorder();

        var result = await NewOrchestrator(executor, recorder)
            .ProcessAsync(TestChannels.ChatRequest("key-audit-retry"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(new long[] { 7106, 7107 }, recorder.Executions.Select(e => e.ChannelId!.Value).ToArray());

        var firstKey = SentKey(executor.Requests[0]);
        Assert.Contains(firstKey, FirstChannelKeys);
        Assert.Equal(firstKey[^4..], recorder.Executions[0].ChannelApiKeySuffix);
        Assert.Equal(IndexOfKey(firstKey, FirstChannelKeys), recorder.Executions[0].ChannelApiKeyIndex);

        var secondKey = SentKey(executor.Requests[1]);
        Assert.Contains(secondKey, SecondChannelKeys);
        Assert.Equal(secondKey[^4..], recorder.Executions[1].ChannelApiKeySuffix);
        Assert.Equal(IndexOfKey(secondKey, SecondChannelKeys), recorder.Executions[1].ChannelApiKeyIndex);
    }
}
