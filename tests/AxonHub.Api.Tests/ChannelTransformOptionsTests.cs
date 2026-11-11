using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behaviour locks for the channel transform options and the per-candidate
/// request shaping in outbound.go (transform_options.go:16-131,
/// outbound.go:434-450 and 550-598): the four settings-driven rewrites, the
/// Responses custom-tool filter, and the stream-intent normalisation that a
/// require-streaming channel forces on an aggregatable non-streaming request.
/// </summary>
public sealed class ChannelTransformOptionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------
    // applyTransformOptions
    // ------------------------------------------------------------------

    [Fact]
    public void ReplaceDeveloperRoleWithSystemRewritesACopy()
    {
        var request = ChatRequest();
        request.Messages =
        [
            new Message { Role = "developer", Content = MessageContent.FromText("dev") },
            new Message { Role = "user", Content = MessageContent.FromText("hi") },
        ];

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ReplaceDeveloperRoleWithSystem = true }));

        Assert.NotSame(request, result);
        Assert.Equal("system", result.Messages[0].Role);
        Assert.Equal("user", result.Messages[1].Role);

        // Go copies each Message struct before rewriting its role; the caller's
        // messages keep the developer role.
        Assert.Equal("developer", request.Messages[0].Role);
    }

    [Theory]
    [InlineData("Developer")]
    [InlineData("DEVELOPER")]
    public void DeveloperReplacementIsCaseInsensitive(string role)
    {
        var request = ChatRequest();
        request.Messages = [new Message { Role = role }];

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ReplaceDeveloperRoleWithSystem = true }));

        Assert.Equal("system", result.Messages[0].Role);
    }

    [Fact]
    public void DisabledDeveloperReplacementKeepsTheSameRequest()
    {
        var request = ChatRequest();
        request.Messages = [new Message { Role = "developer" }];

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ReplaceDeveloperRoleWithSystem = false }));

        Assert.Same(request, result);
        Assert.Equal("developer", result.Messages[0].Role);
    }

    [Fact]
    public void NullSettingsKeepTheSameRequest()
    {
        var request = ChatRequest();

        Assert.Same(request, ChannelTransformOptions.Apply(request, null));
    }

    [Fact]
    public void NullTransformOptionsKeepTheSameRequest()
    {
        var request = ChatRequest();

        Assert.Same(request, ChannelTransformOptions.Apply(
            request,
            new ChannelSettingsDraft { TransformOptions = null! }));
    }

    [Fact]
    public void DeveloperReplacementWithoutAMatchStillReturnsTheClone()
    {
        var request = ChatRequest();

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ReplaceDeveloperRoleWithSystem = true }));

        Assert.NotSame(request, result);
        Assert.Equal("user", result.Messages[0].Role);
    }

    [Fact]
    public void EmptyMessagesAreSafe()
    {
        var request = ChatRequest();
        request.Messages = [];

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ReplaceDeveloperRoleWithSystem = true }));

        Assert.Empty(result.Messages);
    }

    [Fact]
    public void ForceArrayInstructionsSetsTheRequestOption()
    {
        var request = ChatRequest();

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ForceArrayInstructions = true }));

        Assert.NotSame(request, result);
        Assert.True(result.TransformOptions!.ArrayInstructions);
        Assert.Null(request.TransformOptions);
    }

    [Fact]
    public void ForceArrayInputsSetsTheRequestOption()
    {
        var request = ChatRequest();

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ForceArrayInputs = true }));

        Assert.NotSame(request, result);
        Assert.True(result.TransformOptions!.ArrayInputs);
        Assert.Null(request.TransformOptions);
    }

    [Fact]
    public void ForceOptionsDoNotMutateTheSharedTransformOptionsInstance()
    {
        var request = ChatRequest();
        var shared = new TransformOptions { DefaultMaxTokens = 77 };
        request.TransformOptions = shared;

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ForceArrayInputs = true }));

        Assert.NotSame(shared, result.TransformOptions);
        Assert.True(result.TransformOptions!.ArrayInputs);
        Assert.Equal(77L, result.TransformOptions.DefaultMaxTokens);

        // Go's inline struct copy leaves the original options untouched.
        Assert.Null(shared.ArrayInputs);
        Assert.Same(shared, request.TransformOptions);
    }

    [Fact]
    public void ForceOptionsPreserveExistingOptionValues()
    {
        var request = ChatRequest();
        request.TransformOptions = new TransformOptions
        {
            ArrayInstructions = false,
            ArrayInputs = true,
            DefaultMaxTokens = 12,
        };

        var result = ChannelTransformOptions.Apply(
            request,
            Settings(new TransformOptionsDraft { ForceArrayInstructions = true }));

        // The forced flag wins over the existing value; unrelated options keep
        // their values.
        Assert.True(result.TransformOptions!.ArrayInstructions);
        Assert.True(result.TransformOptions.ArrayInputs);
        Assert.Equal(12L, result.TransformOptions.DefaultMaxTokens);
    }

    // ------------------------------------------------------------------
    // applyReasoningEffortMapping
    // ------------------------------------------------------------------

    [Fact]
    public void ReasoningEffortIsMappedOnAClone()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "xhigh";

        var result = ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings());

        Assert.NotSame(request, result);
        Assert.Equal("max", result.ReasoningEffort);
        Assert.Equal("xhigh", request.ReasoningEffort);
    }

    [Fact]
    public void TheFirstMatchingReasoningEntryWins()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "max";

        var result = ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings());

        Assert.Equal("high", result.ReasoningEffort);
    }

    [Fact]
    public void AnUnmappedReasoningEffortKeepsTheSameRequest()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "low";

        Assert.Same(request, ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings()));
    }

    [Fact]
    public void AnEmptyReasoningEffortKeepsTheSameRequest()
    {
        var request = ChatRequest();

        Assert.Same(request, ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings()));
    }

    [Fact]
    public void AnEmptyReasoningMappingKeepsTheSameRequest()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "xhigh";

        Assert.Same(
            request,
            ChannelTransformOptions.ApplyReasoningEffortMapping(request, new ChannelSettingsDraft()));
    }

    [Fact]
    public void NullSettingsKeepTheSameRequestForReasoningEffort()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "xhigh";

        Assert.Same(request, ChannelTransformOptions.ApplyReasoningEffortMapping(request, null));
    }

    [Fact]
    public void TheAnthropicOutputConfigEffortMarkerFollowsTheMapping()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "max";
        request.TransformerMetadata = new Dictionary<string, JsonElement>
        {
            [AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort] = JsonSerializer.SerializeToElement("max"),
            ["thinking_type"] = JsonSerializer.SerializeToElement("adaptive"),
        };

        var result = ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings());

        Assert.Equal("high", result.ReasoningEffort);
        Assert.Equal("high", result.TransformerMetadata![AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort].GetString());
        Assert.Equal("adaptive", result.TransformerMetadata["thinking_type"].GetString());
    }

    [Fact]
    public void ReasoningMappingDoesNotMutateTheOriginalMetadata()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort] = JsonSerializer.SerializeToElement("max"),
        };
        var request = ChatRequest();
        request.ReasoningEffort = "max";
        request.TransformerMetadata = metadata;

        var result = ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings());

        Assert.Equal("high", result.TransformerMetadata![AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort].GetString());
        Assert.Equal("max", metadata[AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort].GetString());
        Assert.Same(metadata, request.TransformerMetadata);
    }

    [Fact]
    public void NoAnthropicMarkerKeepsMetadataUntouched()
    {
        var request = ChatRequest();
        request.ReasoningEffort = "xhigh";

        var result = ChannelTransformOptions.ApplyReasoningEffortMapping(request, MappingSettings());

        Assert.Equal("max", result.ReasoningEffort);
        Assert.Null(result.TransformerMetadata);
    }

    // ------------------------------------------------------------------
    // filterResponseCustomToolMessagesForNonResponsesOutbound
    // ------------------------------------------------------------------

    [Fact]
    public void ResponsesCustomToolMessagesAreFilteredForAGenericOutbound()
    {
        var request = ResponsesRequest();

        var result = ChannelTransformOptions.FilterResponseCustomToolMessagesForNonResponsesOutbound(
            request,
            ApiFormats.OpenAIChatCompletion);

        Assert.NotSame(request, result);
        Assert.Equal(2, result.Messages.Count);
        Assert.Single(result.Messages[0].ToolCalls!);
        Assert.Equal("call_function_1", result.Messages[1].ToolCallId);
        Assert.Equal(3, request.Messages.Count);
    }

    [Fact]
    public void CompactInboundCountsAsAResponsesFormat()
    {
        var request = ResponsesRequest();
        request.ApiFormat = ApiFormats.OpenAIResponseCompact;

        var result = ChannelTransformOptions.FilterResponseCustomToolMessagesForNonResponsesOutbound(
            request,
            ApiFormats.OpenAIChatCompletion);

        Assert.NotSame(request, result);
        Assert.Equal(2, result.Messages.Count);
    }

    [Theory]
    [InlineData(ApiFormats.OpenAIResponse)]
    [InlineData(ApiFormats.OpenAIResponseCompact)]
    public void ResponsesOutboundsKeepTheCustomToolMessages(string outboundFormat)
    {
        var request = ResponsesRequest();

        Assert.Same(
            request,
            ChannelTransformOptions.FilterResponseCustomToolMessagesForNonResponsesOutbound(
                request,
                outboundFormat));
    }

    [Fact]
    public void NonResponsesInboundsKeepTheCustomToolMessages()
    {
        var request = ResponsesRequest();
        request.ApiFormat = ApiFormats.OpenAIChatCompletion;

        Assert.Same(
            request,
            ChannelTransformOptions.FilterResponseCustomToolMessagesForNonResponsesOutbound(
                request,
                ApiFormats.OpenAIChatCompletion));
    }

    [Fact]
    public void RequestsWithoutResponseCustomToolMessagesKeepTheSameInstance()
    {
        var request = ChatRequest();
        request.ApiFormat = ApiFormats.OpenAIResponse;

        Assert.Same(
            request,
            ChannelTransformOptions.FilterResponseCustomToolMessagesForNonResponsesOutbound(
                request,
                ApiFormats.OpenAIChatCompletion));
    }

    // ------------------------------------------------------------------
    // shouldForceStreamingForCandidate
    // ------------------------------------------------------------------

    [Fact]
    public void ARequireStreamingChannelForcesAnAggregatableRequest()
    {
        Assert.True(ChannelTransformOptions.ShouldForceStreamingForCandidate(
            Candidate(policies: """{"stream":"require"}"""),
            ChatRequest()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"stream":"unlimited"}""")]
    [InlineData("""{"stream":"forbid"}""")]
    public void NonRequiringChannelsDoNotForceStreaming(string? policies)
    {
        Assert.False(ChannelTransformOptions.ShouldForceStreamingForCandidate(
            Candidate(policies: policies),
            ChatRequest()));
    }

    [Fact]
    public void EmbeddingRequestsDoNotForceStreaming()
    {
        Assert.False(ChannelTransformOptions.ShouldForceStreamingForCandidate(
            Candidate(policies: """{"stream":"require"}"""),
            new LlmRequest
            {
                Model = "client-model",
                RequestType = RequestTypes.Embedding,
                ApiFormat = ApiFormats.OpenAIEmbedding,
            }));
    }

    [Fact]
    public void CompactRequestsDoNotForceStreaming()
    {
        Assert.False(ChannelTransformOptions.ShouldForceStreamingForCandidate(
            Candidate(policies: """{"stream":"require"}"""),
            new LlmRequest
            {
                Model = "client-model",
                RequestType = RequestTypes.Compact,
                ApiFormat = ApiFormats.OpenAIResponseCompact,
            }));
    }

    [Fact]
    public void ClientStreamingRequestsKeepTheirExistingPath()
    {
        var request = ChatRequest();
        request.Stream = true;

        Assert.False(ChannelTransformOptions.ShouldForceStreamingForCandidate(
            Candidate(policies: """{"stream":"require"}"""),
            request));
    }

    [Fact]
    public void MalformedPoliciesDoNotForceStreaming()
    {
        Assert.False(ChannelTransformOptions.ShouldForceStreamingForCandidate(
            Candidate(policies: "{not json"),
            ChatRequest()));
    }

    // ------------------------------------------------------------------
    // PersistentOutboundTransformer wiring
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultConfigurationPassesTheSameRequestInstance()
    {
        var (transformer, outbound) = RecordingHarness(Candidate());
        var request = ChatRequest();

        transformer.TransformRequest(request);

        Assert.Same(request, outbound.Requests[^1]);
    }

    [Fact]
    public void MalformedSettingsAreTreatedAsAbsent()
    {
        var (transformer, outbound) = RecordingHarness(Candidate(settings: "{not json"));
        var request = ChatRequest();

        transformer.TransformRequest(request);

        Assert.Same(request, outbound.Requests[^1]);
    }

    [Fact]
    public void TransformOptionsAndReasoningEffortReachTheOutboundRequest()
    {
        var (transformer, outbound) = RecordingHarness(Candidate(settings:
            """
            {"transformOptions":{"forceArrayInstructions":true,"forceArrayInputs":true,"replaceDeveloperRoleWithSystem":true,"reasoningEffortMapping":[{"from":"high","to":"max"}]}}
            """));
        var request = ChatRequest();
        request.Messages =
        [
            new Message { Role = "developer", Content = MessageContent.FromText("dev") },
            new Message { Role = "user", Content = MessageContent.FromText("hi") },
        ];
        request.ReasoningEffort = "high";
        request.TransformerMetadata = new Dictionary<string, JsonElement>
        {
            [AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort] = JsonSerializer.SerializeToElement("high"),
        };

        transformer.TransformRequest(request);

        var sent = outbound.Requests[^1];
        Assert.NotSame(request, sent);
        Assert.True(sent.TransformOptions!.ArrayInstructions);
        Assert.True(sent.TransformOptions.ArrayInputs);
        Assert.Equal("system", sent.Messages[0].Role);
        Assert.Equal("max", sent.ReasoningEffort);
        Assert.Equal("max", sent.TransformerMetadata![AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort].GetString());

        // The state request keeps the client's values: every rewrite is a copy.
        Assert.Equal("developer", request.Messages[0].Role);
        Assert.Equal("high", request.ReasoningEffort);
        Assert.Null(request.TransformOptions);
        Assert.Equal("high", request.TransformerMetadata[AnthropicOutboundRequest.TransformerMetadataKeyOutputConfigEffort].GetString());
    }

    [Fact]
    public void ForcedStreamingUpdatesTheOutboundAndStateRequests()
    {
        var (transformer, outbound) = RecordingHarness(
            Candidate(policies: """{"stream":"require"}"""));
        var request = ChatRequest();

        transformer.TransformRequest(request);

        var sent = outbound.Requests[^1];
        Assert.True(sent.Stream);
        Assert.True(sent.StreamOptions!.IncludeUsage);

        // outbound.go:576-585 syncs state.LlmRequest, which the pipeline reads
        // to decide between streaming and aggregation.
        Assert.True(request.Stream);
        Assert.True(request.StreamOptions!.IncludeUsage);
        Assert.True(transformer.CurrentLlmRequest!.Stream);

        // Pass-through compares against the client's original intent, captured
        // before the candidate forced streaming.
        Assert.Null(transformer.OriginalRequestStream);
    }

    [Fact]
    public void ClientStreamingRequestsDoNotGainForcedUsageOptions()
    {
        var (transformer, outbound) = RecordingHarness(
            Candidate(policies: """{"stream":"require"}"""));
        var request = ChatRequest();
        request.Stream = true;

        transformer.TransformRequest(request);

        var sent = outbound.Requests[^1];
        Assert.True(sent.Stream);
        Assert.Null(sent.StreamOptions);
        Assert.Null(request.StreamOptions);
    }

    [Fact]
    public void NonAnthropicRequestsKeepAnUnsetStreamIntent()
    {
        var (transformer, outbound) = RecordingHarness(Candidate());
        var request = ChatRequest();

        transformer.TransformRequest(request);

        Assert.Null(outbound.Requests[^1].Stream);
        Assert.Null(request.Stream);
    }

    [Fact]
    public void AnthropicNonStreamingRequestsCarryAnExplicitFalseStream()
    {
        var candidate = Candidate(apiFormat: ApiFormats.AnthropicMessage);
        var transformer = AnthropicHarness(candidate);
        var request = ChatRequest();
        request.ApiFormat = ApiFormats.AnthropicMessage;
        request.MaxTokens = 1024;

        var httpRequest = transformer.TransformRequest(request);

        Assert.NotNull(httpRequest.Body);
        using var body = JsonDocument.Parse(Encoding.UTF8.GetString(httpRequest.Body!));
        Assert.True(body.RootElement.TryGetProperty("stream", out var stream));
        Assert.False(stream.GetBoolean());

        // The normalisation happens on the state request too (outbound.go:565).
        Assert.Equal(false, request.Stream);
    }

    [Fact]
    public void ResponseCustomToolMessagesAreFilteredForANonResponsesOutbound()
    {
        var (transformer, outbound) = RecordingHarness(Candidate());
        var request = ResponsesRequest();

        transformer.TransformRequest(request);

        var sent = outbound.Requests[^1];
        Assert.NotSame(request, sent);
        Assert.Equal(2, sent.Messages.Count);
        Assert.Single(sent.Messages[0].ToolCalls!);
        Assert.Equal(3, request.Messages.Count);
    }

    // -- Helpers -------------------------------------------------------------

    private static LlmRequest ChatRequest() => new()
    {
        Model = "client-model",
        RequestType = RequestTypes.Chat,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("hi") }],
    };

    private static LlmRequest ResponsesRequest() => new()
    {
        Model = "client-model",
        RequestType = RequestTypes.Chat,
        ApiFormat = ApiFormats.OpenAIResponse,
        Messages =
        [
            new Message
            {
                Role = "assistant",
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_custom_1",
                        Type = ToolTypes.ResponsesCustomTool,
                        ResponseCustomToolCall = new ResponseCustomToolCall
                        {
                            CallId = "call_custom_1",
                            Name = "apply_patch",
                            Input = "*** Begin Patch\n*** End Patch\n",
                        },
                    },
                    new ToolCall
                    {
                        Id = "call_function_1",
                        Type = ToolTypes.Function,
                        Function = new FunctionCall { Name = "get_weather", Arguments = "{}" },
                    },
                ],
            },
            new() { Role = "tool", ToolCallId = "call_custom_1", Content = MessageContent.FromText("custom") },
            new() { Role = "tool", ToolCallId = "call_function_1", Content = MessageContent.FromText("function") },
        ],
    };

    private static ChannelCandidate Candidate(
        string? settings = null,
        string? policies = null,
        string apiFormat = ApiFormats.OpenAIChatCompletion) => new()
        {
            ChannelId = 101,
            ChannelName = "channel-101",
            ActualModel = "upstream-model",
            Type = "openai",
            ApiFormat = apiFormat,
            Settings = settings,
            Policies = policies,
            Credentials = """{"api_key":"test-key"}""",
        };

    private static ChannelSettingsDraft Settings(TransformOptionsDraft options)
        => new() { TransformOptions = options };

    private static ChannelSettingsDraft MappingSettings() => Settings(new TransformOptionsDraft
    {
        ReasoningEffortMapping =
        [
            new ReasoningEffortMappingDraft { From = "xhigh", To = "max" },
            new ReasoningEffortMappingDraft { From = "max", To = "high" },
        ],
    });

    private static (PersistentOutboundTransformer Transformer, RecordingOutbound Outbound)
        RecordingHarness(ChannelCandidate candidate)
    {
        var outbound = new RecordingOutbound(
            candidate.ApiFormat ?? ApiFormats.OpenAIChatCompletion);
        var transformer = new PersistentOutboundTransformer(
            [candidate],
            new TestTimeProvider(Now),
            new RecordingMetricsRecorder(),
            (_, _, _) => outbound);
        return (transformer, outbound);
    }

    private static PersistentOutboundTransformer AnthropicHarness(ChannelCandidate candidate)
    {
        IOutboundTransformer anthropic = AnthropicOutboundTransformer.Create(
            "https://api.example.com",
            "test-key");
        return new PersistentOutboundTransformer(
            [candidate],
            new TestTimeProvider(Now),
            new RecordingMetricsRecorder(),
            (_, _, _) => anthropic);
    }

    private sealed class RecordingOutbound(string apiFormat) : IOutboundTransformer
    {
        public string ApiFormat { get; } = apiFormat;

        public List<LlmRequest> Requests { get; } = [];

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            Requests.Add(request!);
            return new HttpRequestData();
        }

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
