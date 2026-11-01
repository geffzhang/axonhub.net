using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI.Copilot;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/openai/copilot/token_exchanger.go: the
/// GitHub access-token to Copilot-token exchange, its cache and the expiry
/// buffer. Ported from copilot/token_exchanger_test.go; the reference uses an
/// httptest server, the port scripts the channel executor and asserts the same
/// method/path/auth and result shape.
/// </summary>
public sealed class CopilotTokenExchangerTests
{
    private const string Endpoint = "https://api.github.test/copilot_internal/v2/token";

    private static HttpResponseData Json(string json, int statusCode = 200)
        => new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(json),
        };

    private static long ExpiresIn(long seconds)
        => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + seconds;

    [Fact]
    public void Exchange_SendsTheExpectedRequestAndReturnsTheToken()
    {
        HttpRequestData? captured = null;
        var executor = new MockExecutor
        {
            DoFn = (_, request) =>
            {
                captured = request;
                var accessToken = HttpClientUtils.HeaderGet(request.Headers, "Authorization")
                    .Replace("token ", string.Empty, StringComparison.Ordinal);
                return Json($$"""{"token":"copilot_token_{{accessToken}}","expires_at":{{ExpiresIn(3600)}}}""");
            },
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var result = exchanger.Exchange(CancellationToken.None, "test_access_token_123");

        Assert.Null(result.Error);
        Assert.Equal("copilot_token_test_access_token_123", result.Token);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.NotNull(captured);
        Assert.Equal("GET", captured!.Method);
        Assert.Equal(Endpoint, captured.Url);
        Assert.Equal(
            "token test_access_token_123",
            HttpClientUtils.HeaderGet(captured.Headers, "Authorization"));
        Assert.Equal(
            "application/json",
            HttpClientUtils.HeaderGet(captured.Headers, "Accept"));
    }

    [Fact]
    public void Exchange_CachesPerAccessToken()
    {
        var requestCount = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                requestCount++;
                return Json($$"""{"token":"copilot_token_cached","expires_at":{{ExpiresIn(3600)}}}""");
            },
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var first = exchanger.Exchange(CancellationToken.None, "test_access_token");
        Assert.Null(first.Error);
        Assert.Equal(1, requestCount);

        var second = exchanger.Exchange(CancellationToken.None, "test_access_token");
        Assert.Null(second.Error);
        Assert.Equal(1, requestCount);
        Assert.Equal(first.Token, second.Token);
    }

    [Fact]
    public void Exchange_RefreshesInsideTheExpiryBuffer()
    {
        var requestCount = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                requestCount++;
                return Json(
                    $$"""{"token":"copilot_token_v{{requestCount}}","expires_at":{{ExpiresIn(180)}}}""");
            },
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var first = exchanger.Exchange(CancellationToken.None, "test_access_token");
        Assert.Null(first.Error);
        Assert.Equal(1, requestCount);

        // The cached entry expires five minutes early, so a token with three
        // minutes left is re-exchanged.
        var second = exchanger.Exchange(CancellationToken.None, "test_access_token");
        Assert.Null(second.Error);
        Assert.Equal(2, requestCount);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public void Exchange_RejectsAnEmptyAccessToken()
    {
        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams());

        var result = exchanger.Exchange(CancellationToken.None, string.Empty);

        Assert.NotNull(result.Error);
        Assert.Contains("access token is empty", result.Error!.Message);
    }

    [Fact]
    public void Exchange_RejectsANon2xxStatus()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => Json("""{"error":"unauthorized"}""", 401),
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var result = exchanger.Exchange(CancellationToken.None, "test_access_token");

        Assert.NotNull(result.Error);
        Assert.Contains("token exchange returned non-2xx status: 401", result.Error!.Message);
    }

    [Fact]
    public void Exchange_RejectsInvalidJson()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => Json("{invalid-json"),
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var result = exchanger.Exchange(CancellationToken.None, "test_access_token");

        Assert.NotNull(result.Error);
        Assert.Contains("failed to parse token response:", result.Error!.Message);
    }

    [Fact]
    public void Exchange_RejectsAnEmptyToken()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => Json($$"""{"token":"","expires_at":{{ExpiresIn(3600)}}}"""),
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var result = exchanger.Exchange(CancellationToken.None, "test_access_token");

        Assert.NotNull(result.Error);
        Assert.Contains("copilot token is empty in response", result.Error!.Message);
    }

    [Fact]
    public void Exchange_RejectsAMissingExpiresAt()
    {
        var executor = new MockExecutor
        {
            DoFn = (_, _) => Json("""{"token":"copilot_token"}"""),
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var result = exchanger.Exchange(CancellationToken.None, "test_access_token");

        Assert.NotNull(result.Error);
        Assert.Contains("expires_at is missing in response", result.Error!.Message);
    }

    [Fact]
    public void Exchange_DoesNotCacheAFailure()
    {
        var requestCount = 0;
        var executor = new MockExecutor
        {
            DoFn = (_, _) =>
            {
                requestCount++;
                return requestCount == 1
                    ? Json("""{"error":"unauthorized"}""", 401)
                    : Json($$"""{"token":"copilot_token","expires_at":{{ExpiresIn(3600)}}}""");
            },
        };

        var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
        {
            HttpClient = executor,
            Endpoint = Endpoint,
        });

        var failed = exchanger.Exchange(CancellationToken.None, "test_access_token");
        Assert.NotNull(failed.Error);

        var succeeded = exchanger.Exchange(CancellationToken.None, "test_access_token");
        Assert.Null(succeeded.Error);
        Assert.Equal("copilot_token", succeeded.Token);
        Assert.Equal(2, requestCount);
    }
}
