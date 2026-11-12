using System.Net;
using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/model_fetcher_test.go: endpoint preparation,
/// response parsing, the provider-conf cache, Cline discovery, Command Code
/// filtering, stored-credential reuse and channel header overrides.
/// </summary>
public sealed class ModelFetcherTests
{
    private const string ClineTestUrl = "https://cline.test/recommended-models";

    // -- Provider defaults ---------------------------------------------------

    [Fact]
    public async Task GetDefaultModelsByTypeReturnsXaiSubscriptionModels()
    {
        var executor = new ScriptedExecutor();
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "xai_subscription",
        });

        Assert.Null(result.Error);
        Assert.False(result.Fallback);
        Assert.Equal(SubscriptionConstants.DefaultModels().Count, result.Models.Count);
        Assert.Equal(SubscriptionConstants.DefaultModels()[0], result.Models[0].Id);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public void GetDefaultModelsByTypeReturnsXaiSubscriptionModelsDirectly()
    {
        var fetcher = NewFetcher(new ScriptedExecutor());

        var models = fetcher.GetDefaultModelsByType("xai_subscription", CancellationToken.None);

        Assert.NotNull(models);
        Assert.Equal(SubscriptionConstants.DefaultModels().Count, models.Count);
        Assert.Equal(SubscriptionConstants.DefaultModels()[0], models[0].Id);
    }

    // -- JSON array extraction ----------------------------------------------

    [Fact]
    public void ExtractJsonArrayMatchesTheReference()
    {
        var cases = new (string Name, string Body, bool ExpectError, int ExpectedCount)[]
        {
            ("valid JSON array with data field", """{"data":[{"id":"model1"},{"id":"model2"}]}""", false, 2),
            ("valid JSON array without object wrapper", """[{"id":"model1"},{"id":"model2"},{"id":"model3"}]""", false, 3),
            ("JSON array with nested arrays", """{"data":[],"models":[{"name":"models/gemini-pro"}]}""", false, 0),
            ("no JSON array in body", """{"object":"value"}""", true, 0),
            ("empty JSON array", "[]", false, 0),
            ("JSON array with single element", """[{"id":"single-model"}]""", false, 1),
            ("complex response with multiple arrays", """{"data":[{"id":"model1"}],"extra":[{"key":"value"}],"models":[]}""", false, 1),
            ("invalid JSON array structure", "[invalid json]", true, 0),
        };

        foreach (var testCase in cases)
        {
            if (testCase.ExpectError)
            {
                Assert.ThrowsAny<Exception>(() => ModelFetcher.ExtractJsonArray(testCase.Body));
                continue;
            }

            var models = ModelFetcher.ExtractJsonArray(testCase.Body);
            Assert.Equal(testCase.ExpectedCount, models.Count);
        }
    }

    [Fact]
    public void ExtractJsonArrayReturnsTheModelsInBodyOrder()
    {
        var models = ModelFetcher.ExtractJsonArray(
            """{"data":[{"id":"gpt-4"},{"id":"gpt-3.5-turbo"},{"id":"claude-3"}]}""");

        Assert.Equal(["gpt-4", "gpt-3.5-turbo", "claude-3"], models.Select(model => model.Id).ToArray());
    }

    [Fact]
    public async Task ParseModelsResponseTrimsTheGeminiModelsPrefix()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """{"models":[{"name":"models/gemini-pro","displayName":"Gemini Pro"}]}"""),
        };
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://api.openai.test",
            ApiKey = "test-key",
        });

        Assert.Null(result.Error);
        Assert.Equal("gemini-pro", Assert.Single(result.Models).Id);
    }

    // -- Endpoint preparation ------------------------------------------------

    [Fact]
    public void PrepareModelsEndpointMatchesTheReference()
    {
        var cases = new (string Name, string Type, string BaseUrl, string ExpectedUrl, string? HeaderKey, string? HeaderValue)[]
        {
            ("Anthropic with /v1 suffix", "anthropic", "https://api.anthropic.com/v1", "https://api.anthropic.com/v1/models", "Anthropic-Version", "2023-06-01"),
            ("Anthropic without /v1 suffix", "anthropic", "https://api.anthropic.com", "https://api.anthropic.com/v1/models", "Anthropic-Version", "2023-06-01"),
            ("Anthropic with /anthropic suffix", "anthropic", "https://api.example.com/anthropic", "https://api.example.com/v1/models", "Anthropic-Version", "2023-06-01"),
            ("Anthropic with /claude suffix", "anthropic", "https://api.example.com/claude", "https://api.example.com/v1/models", "Anthropic-Version", "2023-06-01"),
            ("Anthropic with raw URL marker (#)", "anthropic", "https://api.example.com/custom/path#", "https://api.example.com/custom/path/models", "Anthropic-Version", "2023-06-01"),
            ("ZhipuAnthropic", "zhipu_anthropic", "https://open.bigmodel.cn/api/anthropic", "https://open.bigmodel.cn/api/paas/v4/models", null, null),
            ("ZaiAnthropic", "zai_anthropic", "https://api.zai.ai/anthropic", "https://api.zai.ai/paas/v4/models", null, null),
            ("Zai", "zai", "https://api.zai.ai/v4", "https://api.zai.ai/v4/models", null, null),
            ("Zhipu", "zhipu", "https://open.bigmodel.cn/api", "https://open.bigmodel.cn/api/v4/models", null, null),
            ("ZhipuAnthropic with raw URL marker (#)", "zhipu_anthropic", "https://custom-proxy.example.com/v1#", "https://custom-proxy.example.com/v1/models", null, null),
            ("ZaiAnthropic with raw URL marker (#)", "zai_anthropic", "https://custom-proxy.example.com/v1#", "https://custom-proxy.example.com/v1/models", null, null),
            ("ZaiAnthropic with /v1 suffix", "zai_anthropic", "https://custom-proxy.example.com/v1", "https://custom-proxy.example.com/v1/models", null, null),
            ("Zai with raw URL marker (#)", "zai", "https://custom-proxy.example.com/v1#", "https://custom-proxy.example.com/v1/models", null, null),
            ("Zai with /v1 suffix", "zai", "https://custom-proxy.example.com/v1", "https://custom-proxy.example.com/v1/models", null, null),
            ("Zhipu with raw URL marker on official path", "zhipu", "https://open.bigmodel.cn/api/paas/v4#", "https://open.bigmodel.cn/api/paas/v4/models", null, null),
            ("DeepseekAnthropic", "deepseek_anthropic", "https://api.deepseek.com/anthropic", "https://api.deepseek.com/v1/models", null, null),
            ("MoonshotAnthropic", "moonshot_anthropic", "https://api.moonshot.cn/claude", "https://api.moonshot.cn/v1/models", null, null),
            ("OpencodeGoAnthropic with /v1 suffix", "opencode_go_anthropic", "https://opencode.ai/zen/go/v1", "https://opencode.ai/zen/go/v1/models", null, null),
            ("OpencodeGoAnthropic without /v1 suffix", "opencode_go_anthropic", "https://opencode.ai/zen/go", "https://opencode.ai/zen/go/v1/models", null, null),
            ("Gemini with /v1 suffix", "gemini", "https://generativelanguage.googleapis.com/v1", "https://generativelanguage.googleapis.com/v1/models", null, null),
            ("Gemini without /v1 suffix", "gemini", "https://generativelanguage.googleapis.com", "https://generativelanguage.googleapis.com/v1beta/models", null, null),
            ("OpenAI with /v1 suffix", "openai", "https://api.openai.com/v1", "https://api.openai.com/v1/models", null, null),
            ("OpenAI without /v1 suffix", "openai", "https://api.openai.com", "https://api.openai.com/v1/models", null, null),
            ("OpenAI with raw URL marker (#)", "openai", "https://custom.api.com/custom/path#", "https://custom.api.com/custom/path/models", null, null),
            ("OpenAI secure WebSocket endpoint", "openai", "wss://api.openai.com/v1#", "https://api.openai.com/v1/models", null, null),
            ("OpenAI WebSocket endpoint", "openai", "ws://api.example.com/v1", "http://api.example.com/v1/models", null, null),
            ("Deepseek", "deepseek", "https://api.deepseek.com", "https://api.deepseek.com/v1/models", null, null),
            ("Vercel", "vercel", "https://api.vercel.com", "https://api.vercel.com/v1/models", null, null),
            ("Openrouter", "openrouter", "https://openrouter.ai/api", "https://openrouter.ai/api/v1/models", null, null),
            ("Xai", "xai", "https://api.x.ai", "https://api.x.ai/v1/models", null, null),
            ("Siliconflow", "siliconflow", "https://api.siliconflow.cn", "https://api.siliconflow.cn/v1/models", null, null),
            ("URL with trailing slash", "openai", "https://api.openai.com/", "https://api.openai.com/v1/models", null, null),
            ("URL with double trailing slash", "openai", "https://api.openai.com//", "https://api.openai.com//v1/models", null, null),
            ("Gemini Openai", "gemini_openai", "https://generativelanguage.googleapis.com", "https://generativelanguage.googleapis.com/v1/models", null, null),
            ("Gemini Vertex", "gemini_vertex", "https://us-central1-aiplatform.googleapis.com", "https://us-central1-aiplatform.googleapis.com/v1/models", null, null),
            ("Longcat Anthropic", "longcat_anthropic", "https://api.longcat.ai/anthropic", "https://api.longcat.ai/v1/models", null, null),
            ("Minimax Anthropic", "minimax_anthropic", "https://api.minimax.ai/claude", "https://api.minimax.ai/v1/models", null, null),
            ("Doubao Anthropic", "doubao_anthropic", "https://ark.cn-beijing.volces.com/api/compatible", "https://ark.cn-beijing.volces.com/api/v3/models", null, null),
            ("CommandCode provider base without /v1", "commandcode", "https://api.commandcode.ai/provider", "https://api.commandcode.ai/provider/v1/models", null, null),
            ("CommandCode anthropic provider base without /v1", "commandcode_anthropic", "https://api.commandcode.ai/provider", "https://api.commandcode.ai/provider/v1/models", null, null),
            ("CommandCode with /v1 suffix", "commandcode", "https://api.commandcode.ai/provider/v1", "https://api.commandcode.ai/provider/v1/models", null, null),
            ("CommandCode anthropic with /v1 trailing slash", "commandcode_anthropic", "https://api.commandcode.ai/provider/v1/", "https://api.commandcode.ai/provider/v1/models", null, null),
        };

        foreach (var testCase in cases)
        {
            var (url, headers) = ModelFetcher.PrepareModelsEndpoint(testCase.Type, testCase.BaseUrl);
            Assert.True(
                url == testCase.ExpectedUrl,
                $"{testCase.Name}: URL = {url}, want {testCase.ExpectedUrl}");

            if (testCase.HeaderKey is not null)
            {
                Assert.True(
                    HttpClientUtils.HeaderGet(headers, testCase.HeaderKey) == testCase.HeaderValue,
                    $"{testCase.Name}: header {testCase.HeaderKey} = "
                        + $"{HttpClientUtils.HeaderGet(headers, testCase.HeaderKey)}, want {testCase.HeaderValue}");
            }
        }
    }

    [Fact]
    public void PrepareModelsEndpointHeadersMatchTheReference()
    {
        var (_, anthropicHeaders) = ModelFetcher.PrepareModelsEndpoint(
            "anthropic",
            "https://api.anthropic.com");
        Assert.Equal("2023-06-01", HttpClientUtils.HeaderGet(anthropicHeaders, "Anthropic-Version"));

        var (_, openAiHeaders) = ModelFetcher.PrepareModelsEndpoint("openai", "https://api.openai.com");
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(openAiHeaders, "Anthropic-Version"));

        var (_, geminiHeaders) = ModelFetcher.PrepareModelsEndpoint(
            "gemini",
            "https://generativelanguage.googleapis.com");
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(geminiHeaders, "Anthropic-Version"));

        var (_, anthropicLikeHeaders) = ModelFetcher.PrepareModelsEndpoint(
            "deepseek_anthropic",
            "https://api.deepseek.com");
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(anthropicLikeHeaders, "Anthropic-Version"));
    }

    // -- Gemini pagination ---------------------------------------------------

    [Fact]
    public async Task FetchModelsGeminiPaginationFollowsNextPageTokens()
    {
        var executor = new ScriptedExecutor();
        var calls = 0;
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal("GET", request.Method);
            Assert.Equal(
                "https://generativelanguage.googleapis.com/v1beta/models",
                request.Url!.Split('?')[0]);
            Assert.Contains("pageSize=1000", request.Url);

            if (request.Url.Contains("pageToken=t1", StringComparison.Ordinal))
            {
                return ModelFetchTestResponses.Json("""{"models":[{"name":"models/m3"}]}""");
            }

            if (request.Url.Contains("pageToken=", StringComparison.Ordinal))
            {
                return ModelFetchTestResponses.Json(
                    """{"error":"unexpected pageToken"}""",
                    (int)HttpStatusCode.BadRequest,
                    "Bad Request");
            }

            return ModelFetchTestResponses.Json(
                """{"models":[{"name":"models/m1"},{"name":"models/m2"}],"nextPageToken":"t1"}""");
        };

        var fetcher = NewFetcher(executor);
        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "gemini",
            BaseUrl = "https://generativelanguage.googleapis.com",
            ApiKey = "test-key",
        });

        Assert.Null(result.Error);
        Assert.Equal(2, calls);
        Assert.Equal(["m1", "m2", "m3"], result.Models.Select(model => model.Id).ToArray());
    }

    [Fact]
    public async Task FetchModelsFamilyAnthropicWithRawUrlMarkerUsesTheRelayPath()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                Assert.Equal("https://relay.example/v1/models", request.Url);
                return ModelFetchTestResponses.Json(
                    """{"data":[{"id":"glm-5.2"},{"id":"glm-5.1"}]}""");
            },
        };

        var fetcher = NewFetcher(executor);
        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "zai_anthropic",
            BaseUrl = "https://relay.example/v1#",
            ApiKey = "test-key",
        });

        Assert.Null(result.Error);
        Assert.Equal(["glm-5.2", "glm-5.1"], result.Models.Select(model => model.Id).ToArray());
    }

    // -- Stored credentials --------------------------------------------------

    [Fact]
    public async Task FetchModelsWithChannelIdUsesStoredCredentialsOnlyForStoredEndpoint()
    {
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "stored-endpoint",
            Type = "openai",
            BaseUrl = "https://stored.example",
            Credentials = """{"apiKey":"stored-secret"}""",
            SupportedModels = """["stored-model"]""",
            DefaultTestModel = "stored-model",
        });
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                Assert.Equal("Bearer stored-secret", HeaderValue(request, "Authorization"));
                return ModelFetchTestResponses.Json("""{"data":[{"id":"stored-model"}]}""");
            },
        };
        var fetcher = NewFetcher(executor, channels);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://stored.example/",
            ChannelId = 1,
        });

        Assert.Null(result.Error);
        Assert.Equal("stored-model", Assert.Single(result.Models).Id);
    }

    [Fact]
    public async Task FetchModelsForAStoredChannelUsesTheChannelProxy()
    {
        // model_fetcher.go:524-527: the saved channel's settings.proxy creates
        // the client used for every discovery request.
        var shared = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json("""{"data":[{"id":"proxied-model"}]}"""),
        };
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var proxies = new ChannelProxyExecutors(shared, factory);
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 7,
            Name = "proxied-endpoint",
            Type = "openai",
            BaseUrl = "https://stored.example",
            Credentials = """{"apiKey":"stored-secret"}""",
            Settings = """{"proxy":{"type":"url","url":"http://channel.proxy:8080"}}""",
        });
        var fetcher = NewFetcher(shared, channels, channelProxyExecutors: proxies);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://stored.example",
            ChannelId = 7,
        });

        Assert.Null(result.Error);
        Assert.Equal("proxied-model", Assert.Single(result.Models).Id);
        Assert.Single(proxied.Requests);
        Assert.Empty(shared.Requests);
        Assert.Equal("http://channel.proxy:8080", Assert.Single(factory.Requests).Url);
    }

    [Fact]
    public async Task ClineDiscoveryUsesTheStoredChannelProxy()
    {
        // model_fetcher.go:374-387: the Cline catalog request also follows the
        // stored channel's proxy when a channel id is supplied.
        var shared = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """{"clinePass":[{"id":"pass-1"}]}"""),
        };
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var proxies = new ChannelProxyExecutors(shared, factory);
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 8,
            Name = "cline-proxied",
            Type = "cline",
            BaseUrl = ClineTestUrl,
            Credentials = "{}",
            Settings = """{"proxy":{"type":"url","url":"http://cline.proxy:8080"}}""",
        });
        var fetcher = NewFetcher(shared, channels, channelProxyExecutors: proxies);
        fetcher.ClineRecommendedModelsUrl = ClineTestUrl;

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "cline",
            BaseUrl = ClineTestUrl,
            ChannelId = 8,
        });

        Assert.Null(result.Error);
        Assert.Equal("pass-1", Assert.Single(result.Models).Id);
        Assert.Single(proxied.Requests);
        Assert.Empty(shared.Requests);
        Assert.Equal("http://cline.proxy:8080", Assert.Single(factory.Requests).Url);
    }

    [Fact]
    public async Task FetchModelsWithChannelIdRejectsStoredCredentialForChangedEndpoint()
    {
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "stored-endpoint",
            Type = "openai",
            BaseUrl = "https://api.openai.example",
            Credentials = """{"apiKey":"stored-secret"}""",
            SupportedModels = """["stored-model"]""",
            DefaultTestModel = "stored-model",
        });
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json("""{"data":[{"id":"exfiltrated"}]}"""),
        };
        var fetcher = NewFetcher(executor, channels);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://attacker.example",
            ChannelId = 1,
        });

        Assert.NotNull(result.Error);
        Assert.Contains("API key is required", result.Error);
        Assert.Equal(0, executor.Calls);
    }

    // -- Provider-conf cache -------------------------------------------------

    [Fact]
    public async Task ProviderConfFetcherCachesForTheCacheDuration()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """{"id":"google-vertex","models":[{"id":"gemini-1.5-pro"},{"id":"gemini-1.5-flash"},{"id":"gemini-1.0-pro"}]}"""),
        };
        var fetcher = NewFetcher(executor, time: time);

        var models = fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None);
        Assert.NotNull(models);
        Assert.Equal(["gemini-1.5-pro", "gemini-1.5-flash", "gemini-1.0-pro"], models.Select(model => model.Id).ToArray());

        var cached = fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(1, executor.Calls);

        time.UtcNow = time.UtcNow.AddMinutes(61);
        var refreshed = fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.Equal(2, executor.Calls);
    }

    [Fact]
    public async Task ProviderConfFetcherKeepsTheStaleCacheWhenTheRefetchFails()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero));
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) =>
        {
            calls++;
            return calls == 1
                ? ModelFetchTestResponses.Json("""{"id":"google-vertex","models":[{"id":"gemini-1.5-pro"}]}""")
                : ModelFetchTestResponses.Json("""{"error":"down"}""", 500, "Internal Server Error");
        };
        var fetcher = NewFetcher(executor, time: time);

        Assert.NotNull(fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None));

        time.UtcNow = time.UtcNow.AddMinutes(61);
        var stale = fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None);

        Assert.NotNull(stale);
        Assert.Equal("gemini-1.5-pro", Assert.Single(stale).Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ProviderConfFetcherDoesNotCacheAnEmptySuccess()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json("""{"id":"google-vertex","models":[]}"""),
        };
        var fetcher = NewFetcher(executor);

        Assert.Null(fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None));
        Assert.Null(fetcher.GetDefaultModelsByType("gemini_vertex", CancellationToken.None));
        Assert.Equal(2, executor.Calls);
    }

    [Fact]
    public async Task FetchCopilotModelsUsesTheCachedProviderConf()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """
                {"id":"github-copilot","models":[
                  {"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"o1"},{"id":"o3-mini"},
                  {"id":"claude-3-7-sonnet"},{"id":"claude-sonnet-4"},{"id":"gemini-2.5-pro"}
                ]}
                """),
        };
        var fetcher = NewFetcher(executor);

        var first = await fetcher.FetchModelsAsync(new FetchModelsInput { ChannelType = "github_copilot" });
        Assert.Null(first.Error);
        Assert.Equal(
            ["gpt-4o", "gpt-4o-mini", "o1", "o3-mini", "claude-3-7-sonnet", "claude-sonnet-4", "gemini-2.5-pro"],
            first.Models.Select(model => model.Id).ToArray());

        var second = await fetcher.FetchModelsAsync(new FetchModelsInput { ChannelType = "github_copilot" });
        Assert.Null(second.Error);
        Assert.Equal(1, executor.Calls);
    }

    // -- Cline discovery -----------------------------------------------------

    [Fact]
    public async Task FetchModelsClineRecommendedModelsMergesAndDeduplicates()
    {
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal("GET", request.Method);
            Assert.Equal(ClineTestUrl, request.Url);
            Assert.Equal("application/json", HeaderValue(request, "Accept"));
            Assert.Equal(string.Empty, HeaderValue(request, "Authorization"));
            Assert.Equal(string.Empty, HeaderValue(request, "X-Api-Key"));
            return ModelFetchTestResponses.Json(
                """
                {
                  "recommended": [{"id":"payg-1"},{"id":" shared "},{"id":" "}],
                  "free": [{"id":"shared"},{"id":"free-1"}],
                  "clinePass": [{"id":"pass-1"},{"id":"free-1"}],
                  "futureField": true
                }
                """);
        };
        var fetcher = NewFetcher(executor);
        fetcher.ClineRecommendedModelsUrl = ClineTestUrl;

        for (var iteration = 0; iteration < 2; iteration++)
        {
            var result = await fetcher.FetchModelsAsync(new FetchModelsInput
            {
                ChannelType = "cline",
                BaseUrl = "https://changed.example.invalid",
                ApiKey = "must-not-be-sent",
            });

            Assert.Null(result.Error);
            Assert.False(result.Fallback);
            Assert.Equal(["payg-1", "shared", "free-1", "pass-1"], result.Models.Select(model => model.Id).ToArray());
        }

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FetchModelsClineDuplicatePassDoesNotAppendStaticFallback()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """{"recommended":[{"id":"shared"}],"clinePass":[{"id":" shared "}]}"""),
        };
        var fetcher = NewFetcher(executor);
        fetcher.ClineRecommendedModelsUrl = ClineTestUrl;

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput { ChannelType = "cline" });

        Assert.Null(result.Error);
        Assert.False(result.Fallback);
        Assert.Equal("shared", Assert.Single(result.Models).Id);
    }

    [Fact]
    public async Task FetchModelsClineMissingPassAppendsStaticFallback()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """
                {
                  "recommended": [{"id":"payg-1"},{"id":"cline-pass/deepseek-v4-flash"}],
                  "free": [{"id":"free-1"}],
                  "clinePass": [{"id":" "}]
                }
                """),
        };
        var fetcher = NewFetcher(executor);
        fetcher.ClineRecommendedModelsUrl = ClineTestUrl;

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput { ChannelType = "cline" });

        Assert.Null(result.Error);
        Assert.True(result.Fallback);
        Assert.Equal(
            [
                "payg-1",
                "cline-pass/deepseek-v4-flash",
                "free-1",
                "cline-pass/deepseek-v4-pro",
                "cline-pass/qwen3.7-plus",
                "cline-pass/qwen3.7-max",
                "cline-pass/kimi-k3",
                "cline-pass/kimi-k2.7-code",
                "cline-pass/kimi-k2.6",
                "cline-pass/glm-5.2",
                "cline-pass/mimo-v2.5",
                "cline-pass/mimo-v2.5-pro",
                "cline-pass/minimax-m3",
            ],
            result.Models.Select(model => model.Id).ToArray());
    }

    [Fact]
    public async Task FetchModelsClineFailuresReturnStaticFallback()
    {
        var fallback = ClineConstants.DefaultModels();

        foreach (var (name, statusCode, body) in new[]
                 {
                     ("non-OK status", (int)HttpStatusCode.BadGateway, """{"error":"upstream unavailable"}"""),
                     ("invalid JSON", 200, "{"),
                     ("empty groups", 200, """{"recommended":[],"free":[],"clinePass":[]}"""),
                 })
        {
            var calls = 0;
            var executor = new ScriptedExecutor();
            executor.OnDo = (_, _, _) =>
            {
                calls++;
                return ModelFetchTestResponses.Json(body, statusCode);
            };
            var fetcher = NewFetcher(executor);
            fetcher.ClineRecommendedModelsUrl = ClineTestUrl;

            var result = await fetcher.FetchModelsAsync(new FetchModelsInput { ChannelType = "cline" });

            Assert.True(result.Fallback, name);
            Assert.Null(result.Error);
            Assert.Equal(fallback, result.Models.Select(model => model.Id).ToArray());
            Assert.Equal(1, calls);
        }

        var failedExecutor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpRequestException("connection refused"),
        };
        var failedFetcher = NewFetcher(failedExecutor);
        failedFetcher.ClineRecommendedModelsUrl = ClineTestUrl;

        var failed = await failedFetcher.FetchModelsAsync(new FetchModelsInput { ChannelType = "cline" });
        Assert.True(failed.Fallback);
        Assert.Null(failed.Error);
        Assert.Equal(fallback, failed.Models.Select(model => model.Id).ToArray());
    }

    // -- Command Code --------------------------------------------------------

    [Fact]
    public async Task FetchModelsCommandCodeSendsASingleBearerRequest()
    {
        foreach (var channelType in new[] { "commandcode", "commandcode_anthropic" })
        {
            var calls = 0;
            var executor = new ScriptedExecutor();
            executor.OnDo = (_, request, _) =>
            {
                calls++;
                Assert.Equal("Bearer test-key", HeaderValue(request, "Authorization"));
                Assert.Equal(string.Empty, HeaderValue(request, "X-Api-Key"));
                Assert.Equal("https://api.commandcode.ai/provider/v1/models", request.Url);
                return ModelFetchTestResponses.Json(
                    """{"data":[{"id":"claude-sonnet-4-5"},{"id":"gpt-5-codex"},{"id":"deepseek-v3"}]}""");
            };
            var fetcher = NewFetcher(executor);

            var result = await fetcher.FetchModelsAsync(new FetchModelsInput
            {
                ChannelType = channelType,
                BaseUrl = "https://api.commandcode.ai/provider/v1",
                ApiKey = "test-key",
            });

            Assert.Null(result.Error);
            Assert.Equal(1, calls);
            var expected = channelType == "commandcode_anthropic"
                ? new[] { "claude-sonnet-4-5" }
                : ["gpt-5-codex", "deepseek-v3"];
            Assert.Equal(expected, result.Models.Select(model => model.Id).ToArray());
        }
    }

    [Fact]
    public async Task FetchModelsCommandCodeBaseWithoutV1AppendsV1()
    {
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal("https://api.commandcode.ai/provider/v1/models", request.Url);
            return ModelFetchTestResponses.Json("""{"data":[]}""");
        };
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "commandcode_anthropic",
            BaseUrl = "https://api.commandcode.ai/provider",
            ApiKey = "test-key",
        });

        Assert.Null(result.Error);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FetchModelsCommandCodeRejectsHttp()
    {
        var executor = new ScriptedExecutor();
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "commandcode",
            BaseUrl = "http://api.commandcode.ai/provider",
            ApiKey = "test-key",
        });

        Assert.NotNull(result.Error);
        Assert.Contains("HTTPS", result.Error);
        Assert.Equal(0, executor.Calls);
    }

    /// <summary>
    /// model_fetcher.go:542-548: a Command Code catalog request is sent by a
    /// client that refuses HTTPS to HTTP redirects, so the API key is never
    /// replayed over cleartext.
    /// </summary>
    [Fact]
    public async Task FetchModelsCommandCodeRefusesHttpsDowngradeRedirect()
    {
        var handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler
        {
            OnSend = _ => RedirectTo("http://api.commandcode.ai/provider/v1/models"),
        };
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "commandcode",
            BaseUrl = "https://api.commandcode.ai/provider/v1",
            ApiKey = "test-key",
        });

        Assert.Equal(
            """failed to fetch models: HTTP request failed: Get "http://api.commandcode.ai/provider/v1/models": refusing HTTPS to HTTP redirect""",
            result.Error);
        Assert.Empty(result.Models);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.commandcode.ai/provider/v1/models", request.Url);
    }

    /// <summary>
    /// The policy is scoped: an ordinary OpenAI channel still follows the same
    /// redirect, like the reference's shared client.
    /// </summary>
    [Fact]
    public async Task FetchModelsOpenAiChannelFollowsHttpsDowngradeRedirect()
    {
        var handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler
        {
            OnSend = recorded => recorded.Url == "https://api.openai.test/v1/models"
                ? RedirectTo("http://api.openai.test/v1/models")
                : JsonResponse("""{"data":[{"id":"gpt-4"}]}"""),
        };
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://api.openai.test",
            ApiKey = "test-key",
        });

        Assert.Null(result.Error);
        Assert.Equal("gpt-4", Assert.Single(result.Models).Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("http://api.openai.test/v1/models", handler.Requests[1].Url);
    }

    // -- Header overrides ----------------------------------------------------

    [Fact]
    public async Task FetchModelsAppliesHeaderOverrideOperationsFromSettings()
    {
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "header-override",
            Type = "openai",
            BaseUrl = "https://stored.example",
            Credentials = """{"apiKey":"stored-secret"}""",
            SupportedModels = """["override-model"]""",
            DefaultTestModel = "override-model",
            Settings = """
                {"headerOverrideOperations":[
                  {"op":"set","path":"x-api-key","value":"override-secret"},
                  {"op":"delete","path":"Authorization"}
                ]}
                """,
        });
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                Assert.Equal("override-secret", HeaderValue(request, "X-Api-Key"));
                Assert.Equal(string.Empty, HeaderValue(request, "Authorization"));
                return ModelFetchTestResponses.Json("""{"data":[{"id":"override-model"}]}""");
            },
        };
        var fetcher = NewFetcher(executor, channels);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://stored.example/",
            ChannelId = 1,
        });

        Assert.Null(result.Error);
        Assert.Equal("override-model", Assert.Single(result.Models).Id);
    }

    [Fact]
    public async Task FetchModelsAppliesLegacyOverrideHeaders()
    {
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "legacy-header-override",
            Type = "openai",
            BaseUrl = "https://stored.example",
            Credentials = """{"apiKey":"stored-secret"}""",
            SupportedModels = """["legacy-model"]""",
            DefaultTestModel = "legacy-model",
            Settings = """{"overrideHeaders":[{"key":"x-api-key","value":"legacy-secret"}]}""",
        });
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                Assert.Equal("legacy-secret", HeaderValue(request, "X-Api-Key"));
                return ModelFetchTestResponses.Json("""{"data":[{"id":"legacy-model"}]}""");
            },
        };
        var fetcher = NewFetcher(executor, channels);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://stored.example/",
            ChannelId = 1,
        });

        Assert.Null(result.Error);
        Assert.Equal("legacy-model", Assert.Single(result.Models).Id);
    }

    [Fact]
    public async Task FetchModelsWrapsTransportFailuresWithTheReferencePrefix()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpRequestException(
                "Connection refused (provider.test:443)"),
        };
        var fetcher = NewFetcher(executor);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "openai",
            BaseUrl = "https://provider.test/v1",
            ApiKey = "test-key",
        });

        // The text after the prefix comes from the .NET HTTP stack (C159).
        Assert.NotNull(result.Error);
        Assert.StartsWith("failed to fetch models: ", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Models);
        Assert.False(result.Fallback);
    }

    // -- Helpers -------------------------------------------------------------

    private static ModelFetcher NewFetcher(
        IExecutor executor,
        FakeChannelModelCatalogStore? channels = null,
        TimeProvider? time = null,
        ChannelProxyExecutors? channelProxyExecutors = null)
        => new(
            executor,
            channels ?? new FakeChannelModelCatalogStore(),
            time ?? TimeProvider.System,
            channelProxyExecutors);

    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string HeaderValue(HttpRequestData request, string name)
        => HttpClientUtils.HeaderGet(request.Headers, name);
}
