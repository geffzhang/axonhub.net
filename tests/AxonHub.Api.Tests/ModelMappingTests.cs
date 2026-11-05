using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
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
/// Port of internal/server/orchestrator/model_mapper_test.go and
/// model_mapping_parity_test.go: the API key profile model mapping, its shared
/// request state, the empty-model rejection and the response/stream model
/// restoration, plus the pipeline wiring where routing, override templates and
/// the client response all observe the correct model.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ModelMappingTests(GrainClusterFixture fixture)
{
    private static ApiKeyRecord Key(string? profilesJson)
        => new()
        {
            Id = 2,
            ProjectId = 1,
            Name = "test-key",
            Profiles = profilesJson,
        };

    private static string Profiles(
        string activeProfile,
        params (string From, string To)[] mappings)
        => ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
        {
            ActiveProfile = activeProfile,
            Profiles =
            [
                new ApiKeyProfileModel
                {
                    Name = "active",
                    ModelMappings =
                    [
                        .. mappings.Select(static mapping => new ApiKeyModelMapping
                        {
                            From = mapping.From,
                            To = mapping.To,
                        }),
                    ],
                },
            ],
        });

    private static string Profiles(params (string From, string To)[] mappings)
        => Profiles("active", mappings);

    // ------------------------------------------------------------------
    // ModelMapper.MapModel (model_mapper_test.go:13-160)
    // ------------------------------------------------------------------

    [Fact]
    public void MapModelUsesFirstMatchingActiveProfileMapping()
    {
        // (name, api key, request model, expected model)
        (string Name, ApiKeyRecord? Key, string Model, string Expected)[] cases =
        [
            ("nil api key", null, "gpt-4", "gpt-4"),
            ("no profiles", Key(null), "gpt-4", "gpt-4"),
            (
                "no active profile",
                Key(Profiles(string.Empty, ("gpt-4", "claude-3"))),
                "gpt-4",
                "gpt-4"),
            (
                "active profile with exact match",
                Key(Profiles("active", ("gpt-4", "claude-3-opus"))),
                "gpt-4",
                "claude-3-opus"),
            (
                "active profile with regexp match",
                Key(Profiles("active", ("gpt-.*", "claude-3-opus"))),
                "gpt-4-turbo",
                "claude-3-opus"),
            (
                "active profile with regexp match 2",
                Key(Profiles("active", ("claude.*-haiku.*", "deepseek-chat"))),
                "claude-haiku-4-5-20251001",
                "deepseek-chat"),
            (
                "active profile with no matching mapping",
                Key(Profiles("active", ("gpt-4", "claude-3-opus"))),
                "gpt-3.5-turbo",
                "gpt-3.5-turbo"),
            (
                "active profile not found in profiles list",
                Key(Profiles("nonexistent", ("gpt-4", "claude-3-opus"))),
                "gpt-4",
                "gpt-4"),
        ];

        foreach (var one in cases)
        {
            var actual = ModelMapper.MapModel(one.Key, one.Model);
            Assert.True(
                string.Equals(one.Expected, actual, StringComparison.Ordinal),
                one.Name + ": expected " + one.Expected + ", got " + actual);
        }
    }

    /// <summary>model_mapper_test.go:162-221, reached through the public mapping.</summary>
    [Fact]
    public void MatchesMappingUsesXRegexpSemantics()
    {
        (string Name, string Pattern, string Model, bool Matches)[] cases =
        [
            ("exact match", "gpt-4", "gpt-4", true),
            ("no match", "gpt-*", "claude-3", false),
            ("wildcard only", "*", "any-model", true),
            ("regex special chars match themselves", "model.v1", "model.v1", true),
            ("dot is a regex wildcard", "model.v1", "modelxv1", true),
            ("invalid regex returns false", "[invalid", "[invalid", false),
            ("invalid regex returns false for any string", "[invalid", "other", false),
        ];

        foreach (var one in cases)
        {
            var actual = ModelMapper.MapModel(
                Key(Profiles("active", (one.Pattern, "mapped"))),
                one.Model)
                == "mapped";
            Assert.True(actual == one.Matches, one.Name);
        }
    }

    // ------------------------------------------------------------------
    // Shared middleware state (model_mapping_parity_test.go:21-77)
    // ------------------------------------------------------------------

    [Fact]
    public void InboundMappingCapturesClientModelAndSavesRoutingModel()
    {
        var profile = Profiles(("client-alias", "routing-alias"));

        (string Name, ApiKeyRecord? Key, string Model, string OriginalModel, string Expected)[] cases =
        [
            ("nil key captures shared model", null, "client-alias", "", "client-alias"),
            ("plain key", Key(null), "client-alias", "", "client-alias"),
            (
                "inactive profile",
                Key(Profiles(string.Empty, ("client-alias", "routing-alias"))),
                "client-alias",
                "",
                "client-alias"),
            (
                "missing profile",
                Key(Profiles("missing", ("client-alias", "routing-alias"))),
                "client-alias",
                "",
                "client-alias"),
            (
                "active mapping",
                Key(profile),
                "client-alias",
                "",
                "routing-alias"),
            (
                "active nonmatch",
                Key(profile),
                "other-alias",
                "",
                "other-alias"),
            (
                "nil key restores routing model",
                null,
                "provider-model",
                "routing-alias",
                "routing-alias"),
            (
                "profile preserves routing model",
                Key(profile),
                "client-alias",
                "saved-alias",
                "saved-alias"),
        ];

        foreach (var one in cases)
        {
            var middleware = new ApiKeyModelMappingMiddleware(one.Key, one.OriginalModel);
            var request = new LlmRequest { Model = one.Model };

            var got = middleware.TransformInboundLlmRequest(request);

            Assert.True(ReferenceEquals(request, got), one.Name + ": same request");
            Assert.True(
                string.Equals(one.Expected, got.Model, StringComparison.Ordinal),
                one.Name + ": expected model " + one.Expected + ", got " + got.Model);
            Assert.True(
                string.Equals(one.Expected, middleware.OriginalModel, StringComparison.Ordinal),
                one.Name + ": expected shared model " + one.Expected + ", got "
                    + middleware.OriginalModel);
            Assert.True(
                string.Equals(one.Model, middleware.RequestModel, StringComparison.Ordinal),
                one.Name + ": expected request model " + one.Model + ", got "
                    + middleware.RequestModel);
        }
    }

    [Fact]
    public void InboundMappingRejectsEmptyModel()
    {
        var middleware = new ApiKeyModelMappingMiddleware(null);

        var ex = Assert.Throws<TransformerException>(
            () => middleware.TransformInboundLlmRequest(new LlmRequest()));

        Assert.Equal(TransformerErrorKind.InvalidModel, ex.Kind);
        Assert.Equal("model not found: request model is empty", ex.Message);
        Assert.Empty(middleware.OriginalModel);
        Assert.Empty(middleware.RequestModel);
    }

    // ------------------------------------------------------------------
    // Response and stream restoration (model_mapper.go:76-95)
    // ------------------------------------------------------------------

    [Fact]
    public void ResponseModelIsReplacedOnlyWhenNonEmptyAndDifferent()
    {
        var middleware = new ApiKeyModelMappingMiddleware(null);
        middleware.TransformInboundLlmRequest(new LlmRequest { Model = "client-model" });

        var replaced = middleware.TransformOutboundLlmResponse(
            new LlmResponse { Model = "provider-model" });
        Assert.Equal("client-model", replaced.Model);

        var same = middleware.TransformOutboundLlmResponse(
            new LlmResponse { Model = "client-model" });
        Assert.Equal("client-model", same.Model);

        var empty = middleware.TransformOutboundLlmResponse(new LlmResponse());
        Assert.Empty(empty.Model);
    }

    [Fact]
    public void StreamResponsesAreMappedAndAnEmptyRequestModelLeavesTheStreamAlone()
    {
        var middleware = new ApiKeyModelMappingMiddleware(null);

        var untouched = new SliceStream<LlmResponse>([new LlmResponse { Model = "provider" }]);
        Assert.Same(untouched, middleware.TransformOutboundLlmStream(untouched));

        middleware.TransformInboundLlmRequest(new LlmRequest { Model = "client-model" });

        var mapped = middleware.TransformOutboundLlmStream(
            new SliceStream<LlmResponse>(
            [
                new LlmResponse { Model = "chunk-a" },
                new LlmResponse { Model = "" },
                new LlmResponse { Model = "client-model" },
            ]));

        var models = new List<string>();
        while (mapped.Next())
        {
            models.Add(mapped.Current!.Model);
        }

        string[] expected = ["client-model", "", "client-model"];
        Assert.Equal(expected, models);
    }

    // ------------------------------------------------------------------
    // Pipeline wiring (model_mapping_parity_test.go:79-165)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProfileMappingDrivesRoutingAndRestoresClientModel()
    {
        const string clientModel = "parity-client-alias";
        const string routingModel = "parity-routing-alias";
        const string providerModel = "parity-provider-model";

        fixture.Channels.Set(new ChannelDescriptor
        {
            Id = 5101,
            Name = "mapping-parity-channel",
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"" + providerModel + "\"]",
            Settings = """
                {
                  "modelMappings":[{"from":"parity-routing-alias","to":"parity-provider-model"}],
                  "bodyOverrideOperations":[
                    {"op":"set","path":"service_tier","value":"auto"},
                    {"op":"set","path":"service_tier","value":"priority","condition":"{{ eq .RequestModel \"parity-routing-alias\" }}"}
                  ],
                  "headerOverrideOperations":[
                    {"op":"set","path":"X-Conditional-Alias","value":"{{ .RequestModel }}","condition":"{{ eq .RequestModel \"parity-routing-alias\" }}"},
                    {"op":"set","path":"X-Routing-Model","value":"{{ .RequestModel }}"},
                    {"op":"set","path":"X-Provider-Model","value":"{{ .Model }}"}
                  ]
                }
                """,
        });

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(providerModel, "hello"),
        };
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 2,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

        var identity = new RequestIdentity(1, 2)
        {
            ApiKey = Key(Profiles((clientModel, routingModel))),
        };

        var result = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(clientModel),
            identity,
            onRequestCreated: null);

        // The provider body carries the channel's actual model and its override
        // conditions render the post-profile routing alias, not the provider
        // model.
        var request = Assert.Single(executor.Requests);
        using var providerBody = JsonDocument.Parse(request.Body!);
        Assert.Equal(
            providerModel,
            providerBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "priority",
            providerBody.RootElement.GetProperty("service_tier").GetString());
        Assert.Equal(routingModel, HttpClientUtils.HeaderGet(request.Headers, "X-Conditional-Alias"));
        Assert.Equal(routingModel, HttpClientUtils.HeaderGet(request.Headers, "X-Routing-Model"));
        Assert.Equal(providerModel, HttpClientUtils.HeaderGet(request.Headers, "X-Provider-Model"));

        // The client response keeps the client model even though the provider
        // reported the upstream model.
        using var responseBody = JsonDocument.Parse(result.Response!.Body!);
        Assert.Equal(clientModel, responseBody.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task TesterSelectorSeesThePostProfileModel()
    {
        const string clientModel = "tester-client-alias";
        const string routingModel = "tester-routing-alias";

        var executor = new ScriptedExecutor();
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy()),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

        var identity = new RequestIdentity(1, 2)
        {
            ApiKey = Key(Profiles((clientModel, routingModel))),
        };

        string? selectedModel = null;
        var ex = await Assert.ThrowsAsync<TransformerException>(
            () => orchestrator.ProcessSpecifiedAsync(
                new OpenAiInboundTransformer(),
                TestChannels.ChatRequest(clientModel),
                (llmRequest, _) =>
                {
                    selectedModel = llmRequest.Model;
                    return Task.FromResult<ChannelCandidate?>(null);
                },
                identity));

        Assert.Equal(routingModel, selectedModel);
        Assert.Equal(TransformerErrorKind.InvalidModel, ex.Kind);
        Assert.Equal("model not found: " + routingModel, ex.Message);
        Assert.Empty(executor.Requests);
    }
}
