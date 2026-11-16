using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/orchestrator/retry_channel_refresh_test.go (#2545):
/// before spending a same-channel retry, re-read the stored channel so a
/// credential disabled mid-request is replaced, and so a channel that left
/// service ends the retry instead of consuming the remaining budget.
/// </summary>
public sealed class RetryChannelRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);

    private const string TwoKeys = """{"apiKeys":["sk-a","sk-b"]}""";

    private const string DisabledA =
        """[{"key":"sk-a","disabledAt":"2026-05-02T11:00:00Z","errorCode":401}]""";

    [Fact]
    public void RefreshesThePinnedCandidateWhenTheAttemptCredentialWasDisabled()
    {
        var candidate = Candidate(TwoKeys);
        candidate.ApiKeyOverride = "forced-key";
        candidate.Priority = 7;
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(
                """{"apiKeys":["sk-b"]}""",
                DisabledA)),
        };
        var harness = new Harness(candidate, source);

        harness.Transformer.TransformRequest(Request("m"));
        Assert.Equal("sk-a", harness.Created[0].SelectedApiKey);

        var error = harness.Transformer.TryPrepareForRetry(CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(2, harness.Created.Count);
        Assert.Equal("sk-b", harness.Created[1].SelectedApiKey);
        Assert.Equal("sk-b", harness.Transformer.CurrentApiKey);

        // The refreshed copy carries the stored fields but keeps the
        // candidate-specific model list, placement and key override.
        var refreshed = harness.Transformer.CurrentCandidate!;
        Assert.NotSame(candidate, refreshed);
        Assert.Equal("""{"apiKeys":["sk-b"]}""", refreshed.Credentials);
        Assert.Equal(DisabledA, refreshed.DisabledApiKeys);
        Assert.Equal("forced-key", refreshed.ApiKeyOverride);
        Assert.Equal(7, refreshed.Priority);
        Assert.Single(refreshed.Models);

        // The abandoned attempt's transformer is stopped before replacement.
        Assert.Equal(1, harness.Created[0].StopCount);
    }

    [Fact]
    public void KeepsThePinnedCandidateWhenTheDisableIsNotCommittedYet()
    {
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            // The performance worker has not committed the disable record yet.
            Result = EnabledChannelRefreshLookup.Found(Descriptor(TwoKeys)),
        };
        var harness = new Harness(candidate, source);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
        Assert.Same(candidate, harness.Transformer.CurrentCandidate);
        Assert.Equal("sk-a", harness.Transformer.CurrentApiKey);
    }

    [Fact]
    public void OAuthChannelsAreNotMistakenForCredentiallessChannels()
    {
        const string oauth =
            """{"oauth":{"access_token":"token","refresh_token":"refresh","expires_at":"2099-01-01T00:00:00Z"}}""";
        var candidate = Candidate(oauth, type: "codex");
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(oauth)),
        };
        var harness = new Harness(candidate, source, keySelector: _ => null);

        harness.Transformer.TransformRequest(Request("m"));

        // CurrentApiKey is null for OAuth transformers, but the fixed OAuth
        // credential ref is still present and enabled, so nothing is rebuilt.
        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
    }

    [Fact]
    public void OAuthChannelWithADisabledCredentialStopsTheRetry()
    {
        const string oauth =
            """{"oauth":{"access_token":"token","refresh_token":"refresh","expires_at":"2099-01-01T00:00:00Z"}}""";
        var candidate = Candidate(oauth, type: "codex");
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(
                oauth,
                """[{"key":"__oauth__","disabledAt":"2026-05-02T11:00:00Z","errorCode":401}]""")),
        };
        var harness = new Harness(candidate, source, keySelector: _ => null);

        harness.Transformer.TransformRequest(Request("m"));

        var error = harness.Transformer.TryPrepareForRetry(CancellationToken.None);

        Assert.Same(
            PersistentOutboundTransformer.ChannelUnavailableForRetry,
            error);
        Assert.Single(harness.Created);
    }

    [Fact]
    public void CredentiallessChannelsKeepTheirRetries()
    {
        var candidate = Candidate("{}", type: "ollama");
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor("{}")),
        };
        var harness = new Harness(candidate, source, keySelector: _ => null);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
        Assert.Same(candidate, harness.Transformer.CurrentCandidate);
    }

    [Fact]
    public void EmptyAttemptCredentialLeavesTheCandidateUntouched()
    {
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(
                """{"apiKeys":["sk-b"]}""",
                DisabledA)),
        };
        var harness = new Harness(candidate, source, keySelector: _ => null);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
        Assert.Same(candidate, harness.Transformer.CurrentCandidate);
    }

    [Fact]
    public void TransientReadFailuresLeaveTheCandidateUntouched()
    {
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Failure,
        };
        var harness = new Harness(candidate, source);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
        Assert.Same(candidate, harness.Transformer.CurrentCandidate);
        Assert.Equal("sk-a", harness.Transformer.CurrentApiKey);
    }

    [Fact]
    public void MalformedStoredDisableDocumentsLeaveTheCandidateUntouched()
    {
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(
                TwoKeys,
                "{not json")),
        };
        var harness = new Harness(candidate, source);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
        Assert.Same(candidate, harness.Transformer.CurrentCandidate);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("archived")]
    [InlineData("soft-deleted")]
    [InlineData("unknown")]
    public void ChannelsThatLeftServiceStopTheRetry(string state)
    {
        _ = state;
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Unavailable,
        };
        var harness = new Harness(candidate, source);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Same(
            PersistentOutboundTransformer.ChannelUnavailableForRetry,
            harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
    }

    [Fact]
    public void ChannelsWithNoUsableCredentialLeftStopTheRetry()
    {
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(
                "{}",
                DisabledA)),
        };
        var harness = new Harness(candidate, source);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Same(
            PersistentOutboundTransformer.ChannelUnavailableForRetry,
            harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
    }

    [Fact]
    public void RebuildFailuresStopTheRetryAndKeepTheOriginalCandidate()
    {
        var candidate = Candidate(TwoKeys);
        var source = new FakeRefreshSource
        {
            Result = EnabledChannelRefreshLookup.Found(Descriptor(
                """{"apiKeys":["sk-b"]}""",
                DisabledA)),
        };
        var harness = new Harness(candidate, source, failOnRebuild: true);

        harness.Transformer.TransformRequest(Request("m"));

        Assert.Same(
            PersistentOutboundTransformer.ChannelUnavailableForRetry,
            harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.Single(harness.Created);
        Assert.Same(candidate, harness.Transformer.CurrentCandidate);
        Assert.Equal("sk-a", harness.Transformer.CurrentApiKey);
        Assert.Equal(0, harness.Created[0].StopCount);
    }

    // -- Helpers -------------------------------------------------------------

    private static LlmRequest Request(string model) => new()
    {
        Model = model,
        RequestType = "chat",
    };

    private static ChannelCandidate Candidate(
        string credentials,
        string? disabledApiKeys = null,
        string type = "openai") => new()
    {
        ChannelId = 950,
        ChannelName = "refresh-channel",
        OrderingWeight = 1,
        Type = type,
        BaseUrl = "https://upstream.test/v1",
        ApiFormat = ApiFormats.OpenAIChatCompletion,
        Credentials = credentials,
        DisabledApiKeys = disabledApiKeys,
        Models =
        [
            new ChannelCandidateModel { RequestModel = "m", ActualModel = "m" },
        ],
    };

    private static ChannelDescriptor Descriptor(
        string credentials,
        string? disabledApiKeys = null) => new()
    {
        Id = 950,
        Name = "refresh-channel",
        OrderingWeight = 1,
        Type = "openai",
        BaseUrl = "https://upstream.test/v1",
        Credentials = credentials,
        DisabledApiKeys = disabledApiKeys,
        Settings = "{}",
        Endpoints = """[{"api_format":"openai/chat_completions"}]""",
    };

    private sealed class FakeRefreshSource : IChannelRefreshSource
    {
        public EnabledChannelRefreshLookup Result { get; set; }

        public int Calls { get; private set; }

        public EnabledChannelRefreshLookup GetEnabledChannel(long channelId)
        {
            Calls++;
            return Result;
        }
    }

    private sealed class Harness
    {
        public Harness(
            ChannelCandidate candidate,
            FakeRefreshSource source,
            Func<ChannelCandidate, string?>? keySelector = null,
            bool failOnRebuild = false)
        {
            Transformer = new PersistentOutboundTransformer(
                [candidate],
                new TestTimeProvider(Now),
                new RecordingMetricsRecorder(),
                (selected, _, _) =>
                {
                    if (failOnRebuild && Created.Count > 0)
                    {
                        throw new InvalidOperationException("no transformer for the refreshed channel");
                    }

                    var keys = ChannelCredentials.GetEnabledApiKeys(
                        selected.Credentials,
                        selected.DisabledApiKeys,
                        Now);
                    var key = keySelector is null
                        ? keys.Count > 0 ? keys[0] : null
                        : keySelector(selected);
                    var outbound = new RecordingOutbound(
                        selected.ApiFormat ?? ApiFormats.OpenAIChatCompletion,
                        key);
                    Created.Add(outbound);
                    return outbound;
                },
                channelRefresh: source);
        }

        public List<RecordingOutbound> Created { get; } = [];

        public PersistentOutboundTransformer Transformer { get; }
    }

    private sealed class RecordingOutbound(string apiFormat, string? selectedApiKey)
        : IOutboundTransformer, IApiKeyProvenance, IStoppableOutbound
    {
        public string ApiFormat { get; } = apiFormat;

        public string? SelectedApiKey { get; } = selectedApiKey;

        public int? SelectedApiKeyIndex => null;

        public int StopCount { get; private set; }

        public void Stop() => StopCount++;

        public HttpRequestData TransformRequest(LlmRequest? request)
            => new();

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException("response path not used by these tests");

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException("stream path not used by these tests");

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException("error path not used by these tests");

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
        {
            meta = new ResponseMeta();
            throw new NotSupportedException("aggregate path not used by these tests");
        }
    }
}
