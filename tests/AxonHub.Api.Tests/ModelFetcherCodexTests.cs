using System.Net;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/model_fetcher_codex_test.go: the ChatGPT catalog
/// fetch, stored-credential replacement and relay model listing. The
/// per-channel-proxy case is registered as C158 (proxies are not ported).
/// </summary>
public sealed class ModelFetcherCodexTests
{
    private const string CatalogBaseUrl = "https://chatgpt.com/backend-api/codex#";

    [Theory]
    [InlineData("visible catalog", 200, """{"models":[{"slug":"new-sol","visibility":"list","priority":2,"supported_in_api":false},{"slug":"hidden","visibility":"hide","priority":0},{"slug":"first-astra","visibility":"list","priority":1},{"slug":"new-sol","visibility":"list","priority":3}]}""", false)]
    [InlineData("unauthorized", 401, "{}", true)]
    [InlineData("malformed", 200, "{", true)]
    [InlineData("empty", 200, """{"models":[]}""", true)]
    [InlineData("hidden only", 200, """{"models":[{"slug":"hidden","visibility":"hide"}]}""", true)]
    public async Task CodexCatalog(
        string name,
        int status,
        string body,
        bool fallback)
    {
        _ = name;
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            var uri = new Uri(request.Url);
            Assert.Equal(
                "https://chatgpt.com/backend-api/codex/models",
                uri.Scheme + "://" + uri.Host + uri.AbsolutePath);

            var query = HttpClientUtils.ParseQuery(uri.Query.TrimStart('?'));
            Assert.True(query.TryGetValue("client_version", out var clientVersions));
            Assert.NotEmpty(clientVersions[0]);
            Assert.Equal("Bearer synthetic-token", HeaderValue(request, "Authorization"));
            Assert.Equal(clientVersions[0], HeaderValue(request, "Version"));
            Assert.NotEmpty(HeaderValue(request, CodexHeaders.BetaFeaturesHeader));

            return ModelFetchTestResponses.Json(body, status);
        };
        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "codex",
            BaseUrl = CatalogBaseUrl,
            ApiKey = """{"access_token":"synthetic-token","token_type":"bearer","expires_at":"2099-01-01T00:00:00Z"}""",
        });

        Assert.Null(result.Error);
        Assert.Equal(1, calls);
        Assert.Equal(fallback, result.Fallback);
        if (fallback)
        {
            Assert.Equal(
                CodexConstants.DefaultModels(),
                result.Models.Select(model => model.Id).ToArray());
        }
        else
        {
            Assert.Equal(
                ["first-astra", "new-sol", "gpt-image-2"],
                result.Models.Select(model => model.Id).ToArray());
        }
    }

    [Fact]
    public async Task ThirdPartyCodexCatalogUsesPlainKeyAndHttps()
    {
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal(
                "https://relay.example/v1/models",
                new Uri(request.Url).GetLeftPart(UriPartial.Path));
            Assert.Equal("Bearer sk-third-party", HeaderValue(request, "Authorization"));
            Assert.NotEmpty(HeaderValue(request, "Version"));
            Assert.NotEmpty(HeaderValue(request, CodexHeaders.BetaFeaturesHeader));
            return ModelFetchTestResponses.Json(
                """{"models":[{"slug":"hidden","visibility":"hide","priority":0},{"slug":"shown","visibility":"list","priority":1},{"slug":"gpt-image-2","visibility":"hide","priority":2},{"slug":"gpt-image-2","visibility":"hide","priority":3}]}""");
        };
        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "codex",
            BaseUrl = "https://relay.example/v1",
            ApiKey = "sk-third-party",
        });

        Assert.Null(result.Error);
        Assert.Equal(1, calls);
        // Third-party catalogs are explicit: no visibility filter, image model
        // only when listed, and duplicates collapse to the first priority.
        Assert.Equal(
            ["hidden", "shown", "gpt-image-2"],
            result.Models.Select(model => model.Id).ToArray());
    }

    [Fact]
    public async Task ThirdPartyCodexCatalogRequiresHttps()
    {
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) =>
        {
            calls++;
            return ModelFetchTestResponses.Json("""{"models":[]}""");
        };
        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "codex",
            BaseUrl = "http://relay.example/v1",
            ApiKey = "sk-third-party",
        });

        Assert.Equal(
            "failed to prepare Codex models request: Codex catalog URL must use HTTPS",
            result.Error);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("replace-oauth")]
    [InlineData("replace-api-key")]
    [InlineData("mismatch")]
    [InlineData("stored-api-key")]
    public async Task CodexStoredCredentialReplacement(string mode)
    {
        var credentials = mode == "stored-api-key"
            ? """{"apiKey":"stored-key"}"""
            : """{"oauth":{"access_token":"stored-token","expires_at":"2099-01-01T00:00:00Z"}}""";
        const string settings = """
            {"headerOverrideOperations":[{"op":"set","path":"X-Catalog-Test","value":"preserved"}]}
            """;

        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "stored-codex",
            Type = "codex",
            BaseUrl = CatalogBaseUrl,
            Credentials = credentials,
            Settings = settings,
            SupportedModels = """["existing"]""",
            DefaultTestModel = "existing",
        });

        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal("preserved", HeaderValue(request, "X-Catalog-Test"));

            if (mode == "replace-api-key")
            {
                Assert.Equal(
                    "https://relay.example/v1/models",
                    new Uri(request.Url).GetLeftPart(UriPartial.Path));
                Assert.Equal("Bearer replacement-key", HeaderValue(request, "Authorization"));
                return ModelFetchTestResponses.Json("""{"models":[{"slug":"new-model"}]}""");
            }

            Assert.Equal("Bearer replacement-token", HeaderValue(request, "Authorization"));
            return ModelFetchTestResponses.Json("""{"models":[{"slug":"new-model","visibility":"list"}]}""");
        };

        var fetcher = new ModelFetcher(executor, channels, TimeProvider.System);
        var input = new FetchModelsInput
        {
            ChannelId = 1,
            ChannelType = "codex",
            BaseUrl = CatalogBaseUrl,
            ApiKey = """{"access_token":"replacement-token","expires_at":"2099-01-01T00:00:00Z"}""",
        };
        if (mode is "replace-api-key" or "mismatch")
        {
            input.BaseUrl = "https://relay.example/v1";
            input.ApiKey = "replacement-key";
        }

        if (mode == "mismatch")
        {
            input.ApiKey = null;
        }

        var result = await fetcher.FetchModelsAsync(input);

        switch (mode)
        {
            case "mismatch":
                Assert.NotNull(result.Error);
                Assert.Equal(0, calls);
                break;
            default:
                Assert.Null(result.Error);
                Assert.Equal(1, calls);
                Assert.False(result.Fallback);
                if (mode == "replace-api-key")
                {
                    // A third-party catalog is explicit: no image supplement.
                    Assert.Equal("new-model", Assert.Single(result.Models).Id);
                }
                else
                {
                    Assert.Equal(
                        ["new-model", "gpt-image-2"],
                        result.Models.Select(model => model.Id).ToArray());
                }

                break;
        }

        Assert.Equal(credentials, channels.Row(1)!.Credentials);
        Assert.Empty(channels.CredentialUpdates);
    }

    [Fact]
    public async Task CodexRelayModels()
    {
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal(
                "https://relay.example/v1/models",
                new Uri(request.Url).GetLeftPart(UriPartial.Path));
            Assert.Equal("Bearer synthetic-api-key", HeaderValue(request, "Authorization"));
            return ModelFetchTestResponses.Json("""{"models":[{"slug":"relay-model"}]}""");
        };
        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "codex",
            BaseUrl = "https://relay.example/v1",
            ApiKey = "synthetic-api-key",
        });

        Assert.Null(result.Error);
        Assert.Equal(1, calls);
        Assert.False(result.Fallback);
        Assert.Equal("relay-model", Assert.Single(result.Models).Id);
    }

    private static string HeaderValue(HttpRequestData request, string name)
        => HttpClientUtils.HeaderGet(request.Headers, name);
}
