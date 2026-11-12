using System.Globalization;
using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/model_fetcher_antigravity_test.go: keyless
/// account discovery, endpoint-change protection, refresh persistence and the
/// failure paths that must not return static models.
/// </summary>
public sealed class ModelFetcherAntigravityTests
{
    [Theory]
    [InlineData("valid saved OAuth", "saved-refresh", false)]
    [InlineData("valid access token only", "", false)]
    [InlineData("expired saved OAuth", "saved-refresh", true)]
    public async Task AntigravityOAuthOnlyChannel(
        string name,
        string refreshToken,
        bool expired)
    {
        _ = name;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1);
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "oauth-only-account",
            Type = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            Credentials = OAuthCredentialsJson("saved-access", refreshToken, expiresAt),
            SupportedModels = """["old-model"]""",
            DefaultTestModel = "old-model",
        });

        var refreshCalls = 0;
        var catalogCalls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                refreshCalls++;
                Assert.Equal("saved-refresh", FormValue(request, "refresh_token"));
                return ModelFetchTestResponses.Json(
                    """{"access_token":"fresh-access","expires_in":3600,"token_type":"Bearer"}""");
            }

            catalogCalls++;
            var accessToken = expired ? "fresh-access" : "saved-access";
            Assert.Equal("Bearer " + accessToken, HeaderValue(request, "Authorization"));
            return ModelFetchTestResponses.Json(
                """{"models":{"oauth-account-model":{}}}""");
        };

        var fetcher = new ModelFetcher(executor, channels, TimeProvider.System);
        var input = new FetchModelsInput
        {
            ChannelType = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            ChannelId = 1,
        };

        var result = await fetcher.FetchModelsAsync(input);

        Assert.Null(result.Error);
        Assert.Equal("oauth-account-model", Assert.Single(result.Models).Id);
        Assert.Equal(expired ? 1 : 0, refreshCalls);
        Assert.Equal(1, catalogCalls);

        input.BaseUrl = "https://changed.example";
        var changed = await fetcher.FetchModelsAsync(input);

        Assert.NotNull(changed.Error);
        Assert.Equal(1, catalogCalls);
    }

    [Fact]
    public async Task AntigravityDiscoveryUsesTheChannelProxy()
    {
        // model_fetcher_antigravity.go:20-36: the stored channel's settings.proxy
        // applies to the catalog request (and to any token refresh it needs).
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 2,
            Name = "proxied-antigravity",
            Type = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            Credentials = OAuthCredentialsJson(
                "saved-access",
                string.Empty,
                DateTimeOffset.UtcNow.AddHours(1)),
            Settings = """{"proxy":{"type":"url","url":"http://antigravity.proxy:8080"}}""",
        });
        var shared = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, request, _) =>
            {
                Assert.Equal("Bearer saved-access", HeaderValue(request, "Authorization"));
                return ModelFetchTestResponses.Json("""{"models":{"proxied-model":{}}}""");
            },
        };
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var proxies = new ChannelProxyExecutors(shared, factory);
        var fetcher = new ModelFetcher(shared, channels, TimeProvider.System, proxies);

        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            ChannelId = 2,
        });

        Assert.Null(result.Error);
        Assert.Equal("proxied-model", Assert.Single(result.Models).Id);
        Assert.Single(proxied.Requests);
        Assert.Empty(shared.Requests);
        Assert.Equal("http://antigravity.proxy:8080", Assert.Single(factory.Requests).Url);
    }

    [Fact]
    public async Task AntigravitySavedCredentialsAndRefreshPersistence()
    {
        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "antigravity-catalog",
            Type = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            Credentials = """{"apiKey":"stored-refresh|stored-project"}""",
            SupportedModels = """["old-model"]""",
            DefaultTestModel = "old-model",
        });

        var refreshCalls = 0;
        var catalogCalls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                refreshCalls++;
                Assert.Equal("stored-refresh", FormValue(request, "refresh_token"));
                return ModelFetchTestResponses.Json(
                    """{"access_token":"saved-access","refresh_token":"rotated-refresh","expires_in":3600,"token_type":"Bearer"}""");
            }

            catalogCalls++;
            Assert.Equal("Bearer saved-access", HeaderValue(request, "Authorization"));
            Assert.Equal("stored-project", JsonBody(request)["project"]);
            return ModelFetchTestResponses.Json(
                """{"models":{"new-account-model":{}}}""");
        };

        var fetcher = new ModelFetcher(executor, channels, TimeProvider.System);
        var input = new FetchModelsInput
        {
            ChannelType = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            ChannelId = 1,
        };

        for (var iteration = 0; iteration < 2; iteration++)
        {
            var result = await fetcher.FetchModelsAsync(input);
            Assert.Null(result.Error);
            Assert.Equal("new-account-model", Assert.Single(result.Models).Id);
        }

        Assert.Equal(1, refreshCalls);
        Assert.Equal(2, catalogCalls);

        var updated = ChannelWriteSerialization.ParseCredentials(channels.Row(1)!.Credentials);
        Assert.Equal("rotated-refresh|stored-project", updated.ApiKey);
        Assert.Equal("saved-access", updated.OAuth!.AccessToken);

        input.BaseUrl = "https://changed.example";
        var changed = await fetcher.FetchModelsAsync(input);

        Assert.NotNull(changed.Error);
        Assert.Empty(changed.Models);
        Assert.Equal(2, catalogCalls);
    }

    [Fact]
    public async Task AntigravityAccountCatalog()
    {
        const string key = """
            {"access_token":"test-access","refresh_token":"test-refresh","expires_at":"2099-01-01T00:00:00Z","project_id":"test-project"}
            """;
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            Assert.Equal(
                AntigravityConstants.EndpointProd + "/v1internal:fetchAvailableModels",
                request.Url);
            Assert.Equal("POST", request.Method);
            Assert.Equal("Bearer test-access", HeaderValue(request, "Authorization"));

            var expectedHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            AntigravityVersion.SetClientHeaders(expectedHeaders);
            foreach (var (headerName, values) in expectedHeaders)
            {
                Assert.True(
                    request.Headers!.TryGetValue(headerName, out var actual),
                    "missing header " + headerName);
                Assert.Equal(values, actual!);
            }

            Assert.Equal("test-project", JsonBody(request)["project"]);
            return ModelFetchTestResponses.Json(
                """
                {"models":{"claude-sonnet-5-5-high":{},"claude-opus-5-5-low":{},"claude-opus-5-5-medium":{},"claude-opus-5-5-high":{},"hidden":{"isInternal":true}," ":{}}}
                """);
        };

        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);
        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            ApiKey = key,
        });

        Assert.Null(result.Error);
        Assert.False(result.Fallback);
        Assert.Equal(
            ["claude-opus-5-5-high", "claude-opus-5-5-low", "claude-opus-5-5-medium", "claude-sonnet-5-5-high"],
            result.Models.Select(model => model.Id).ToArray());
        Assert.Equal(1, calls);
        Assert.Null(fetcher.GetDefaultModelsByType("antigravity", CancellationToken.None));
    }

    [Fact]
    public async Task AntigravityLegacyRefresh()
    {
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            calls++;
            if (calls == 1)
            {
                Assert.Equal(AntigravityConstants.TokenURL, request.Url);
                Assert.Equal("test-refresh", FormValue(request, "refresh_token"));
                Assert.Equal(AntigravityConstants.ClientID, FormValue(request, "client_id"));
                Assert.Equal(AntigravityVersion.GetUserAgent(), HeaderValue(request, "User-Agent"));
                return ModelFetchTestResponses.Json(
                    """{"access_token":"fresh-access","expires_in":3600,"token_type":"Bearer"}""");
            }

            Assert.Equal("Bearer fresh-access", HeaderValue(request, "Authorization"));
            return ModelFetchTestResponses.Json("""{"models":{"account-model":{}}}""");
        };

        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);
        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "antigravity",
            ApiKey = "test-refresh|test-project",
        });

        Assert.Null(result.Error);
        Assert.Equal("account-model", Assert.Single(result.Models).Id);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("missing credentials", "", "", 200, 0)]
    [InlineData("invalid credentials", "{broken", "", 200, 0)]
    [InlineData("forbidden", """{"access_token":"access","expires_at":"2099-01-01T00:00:00Z"}""", """{"error":"forbidden"}""", 403, 1)]
    [InlineData("empty catalog", """{"access_token":"access","expires_at":"2099-01-01T00:00:00Z"}""", """{"models":{}}""", 200, 1)]
    [InlineData("invalid response", """{"access_token":"access","expires_at":"2099-01-01T00:00:00Z"}""", "{broken", 200, 1)]
    public async Task AntigravityFailuresDoNotReturnStaticModels(
        string name,
        string key,
        string body,
        int status,
        int expectedCalls)
    {
        _ = name;
        var calls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, _, _) =>
        {
            calls++;
            return ModelFetchTestResponses.Json(body, status);
        };

        var fetcher = new ModelFetcher(executor, new FakeChannelModelCatalogStore(), TimeProvider.System);
        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "antigravity",
            ApiKey = key,
        });

        Assert.NotNull(result.Error);
        Assert.Empty(result.Models);
        Assert.False(result.Fallback);
        Assert.Equal(expectedCalls, calls);
    }

    [Theory]
    [InlineData("valid token for another account", "old-refresh", false)]
    [InlineData("expired token for another account", "old-refresh", true)]
    [InlineData("unverifiable saved token", "", false)]
    public async Task AntigravityMismatchedSavedOAuth(
        string name,
        string savedRefresh,
        bool expired)
    {
        _ = name;
        var expiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1);
        var original = string.Format(
            CultureInfo.InvariantCulture,
            """{{"apiKey":"new-refresh|new-project","oauth":{{"access_token":"old-access","refresh_token":"{0}","expires_at":"{1}"}}}}""",
            savedRefresh,
            Rfc3339(expiresAt));

        var channels = new FakeChannelModelCatalogStore();
        channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "mismatched-account",
            Type = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            Credentials = original,
            SupportedModels = """["old-model"]""",
            DefaultTestModel = "old-model",
        });

        var refreshCalls = 0;
        var catalogCalls = 0;
        var executor = new ScriptedExecutor();
        executor.OnDo = (_, request, _) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                refreshCalls++;
                Assert.Equal("new-refresh", FormValue(request, "refresh_token"));
                return ModelFetchTestResponses.Json(
                    """{"access_token":"new-access","refresh_token":"new-rotated-refresh","expires_in":3600,"token_type":"Bearer"}""");
            }

            catalogCalls++;
            Assert.Equal("Bearer new-access", HeaderValue(request, "Authorization"));
            Assert.Equal("new-project", JsonBody(request)["project"]);
            return ModelFetchTestResponses.Json(
                """{"models":{"new-account-model":{}}}""");
        };

        var fetcher = new ModelFetcher(executor, channels, TimeProvider.System);
        var result = await fetcher.FetchModelsAsync(new FetchModelsInput
        {
            ChannelType = "antigravity",
            BaseUrl = AntigravityConstants.EndpointDaily,
            ChannelId = 1,
        });

        Assert.Null(result.Error);
        Assert.Equal("new-account-model", Assert.Single(result.Models).Id);
        Assert.Equal(1, refreshCalls);
        Assert.Equal(1, catalogCalls);

        // Unverified OAuth pairs must not be overwritten by discovery.
        Assert.Equal(original, channels.Row(1)!.Credentials);
        Assert.Empty(channels.CredentialUpdates);
    }

    // -- Helpers -------------------------------------------------------------

    private static string OAuthCredentialsJson(
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt)
        => string.Format(
            CultureInfo.InvariantCulture,
            """{{"oauth":{{"access_token":"{0}","refresh_token":"{1}","expires_at":"{2}"}}}}""",
            accessToken,
            refreshToken,
            Rfc3339(expiresAt));

    private static string Rfc3339(DateTimeOffset value)
        => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string FormValue(HttpRequestData request, string key)
    {
        var form = HttpClientUtils.ParseQuery(Encoding.UTF8.GetString(request.Body ?? []));
        return form.TryGetValue(key, out var values) && values.Count > 0
            ? values[0]
            : string.Empty;
    }

    private static Dictionary<string, string> JsonBody(HttpRequestData request)
        => LlmJson.Deserialize<Dictionary<string, string>>(request.Body ?? [])
            ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static string HeaderValue(HttpRequestData request, string name)
        => HttpClientUtils.HeaderGet(request.Headers, name);
}
