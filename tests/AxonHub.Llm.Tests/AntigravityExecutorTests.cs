using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Stands in for the transport in the executor tests: records the endpoint of
/// every call and answers through scripted hooks. The reference mock returns
/// raw responses (it never translates statuses into errors), so both failure
/// modes are scripted explicitly - raw non-2xx responses and thrown
/// <see cref="HttpClientError"/> values - exactly like the reference's
/// <c>mockHTTPClient</c> doFunc contract.
/// </summary>
internal sealed class AntigravityTestExecutor : IExecutor
{
    public Func<CancellationToken, HttpRequestData, HttpResponseData>? DoFn { get; set; }

    public Func<CancellationToken, HttpRequestData, IStream<StreamEvent>?>? DoStreamFn { get; set; }

    public int CallCount { get; private set; }

    public List<string> Endpoints { get; } = [];

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        CallCount++;
        Endpoints.Add(ExtractEndpoint(request.Url));
        return DoFn!(cancellationToken, request);
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        CallCount++;
        Endpoints.Add(ExtractEndpoint(request.Url));
        // The Go mock returns a nil stream for "no error"; the port seam is
        // non-nullable, so scripted nulls pass through with forgiveness.
        return DoStreamFn!(cancellationToken, request)!;
    }

    /// <summary>
    /// Port of extractEndpoint (executor_test.go:49-66): everything before the
    /// /v1internal path.
    /// </summary>
    internal static string ExtractEndpoint(string url)
    {
        var pathStart = url.IndexOf("/v1internal", StringComparison.Ordinal);
        return pathStart < 0 ? url : url[..pathStart];
    }
}

/// <summary>
/// Port of llm/transformer/antigravity/executor_test.go and
/// executor_retry_test.go: endpoint fallback (Daily -&gt; Autopush -&gt; Prod),
/// retry classification, URL rebasing, and per-model cooldown skipping.
/// </summary>
public sealed class AntigravityExecutorTests
{
    private static HttpResponseData Ok() => new()
    {
        StatusCode = 200,
        Body = Encoding.UTF8.GetBytes("""{"response": {"candidates": []}}"""),
    };

    private static HttpResponseData Status(int code, string error) => new()
    {
        StatusCode = code,
        Body = Encoding.UTF8.GetBytes(""""{"error": """" + error + """"}""""),
    };

    private static HttpRequestData Request(string model)
        => Request(model, AntigravityConstants.EndpointDaily + "/v1internal:generateContent");

    private static HttpRequestData Request(string model, string url) => new()
    {
        Method = "POST",
        Url = url,
        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["antigravity_model"] = model,
        },
    };

    [Fact]
    public void DefaultTransportHasNoTotalTimeout()
    {
        var executor = new AntigravityExecutor(inner: null);

        var http = Assert.IsType<HttpExecutor>(executor.Inner);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Client.Timeout);
    }

    // ------------------------------------------------------------------
    // Do with endpoint fallback (reference TestExecutor_DoWithEndpointFallback)
    // ------------------------------------------------------------------

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_SucceedsOnFirstEndpointForGeminiCliModel()
    {
        var mock = new AntigravityTestExecutor { DoFn = (_, _) => Ok() };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("gemini-2.5-pro"));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(1, mock.CallCount);
        // All models now start with Daily for better quota distribution.
        Assert.Equal(AntigravityConstants.EndpointDaily, mock.Endpoints[0]);
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_SucceedsOnFirstEndpointForAntigravityModel()
    {
        var mock = new AntigravityTestExecutor { DoFn = (_, _) => Ok() };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(
            CancellationToken.None,
            Request("claude-sonnet-4-5", AntigravityConstants.EndpointProd + "/v1internal:generateContent"));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(1, mock.CallCount);
        // Should try daily first for antigravity models.
        Assert.Equal(AntigravityConstants.EndpointDaily, mock.Endpoints[0]);
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_RetriesOn403()
    {
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, _) =>
            {
                callCount++;
                return callCount == 1 ? Status(403, "forbidden") : Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("claude-sonnet-4-5"));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, mock.CallCount);
        // Should try daily, then autopush.
        Assert.Equal(AntigravityConstants.EndpointDaily, mock.Endpoints[0]);
        Assert.Equal(AntigravityConstants.EndpointAutopush, mock.Endpoints[1]);
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_RetriesOn404()
    {
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, _) =>
            {
                callCount++;
                return callCount < 3 ? Status(404, "not found") : Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("claude-sonnet-4-5"));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(3, mock.CallCount);
        // Should try all three endpoints.
        Assert.Equal(AntigravityConstants.EndpointDaily, mock.Endpoints[0]);
        Assert.Equal(AntigravityConstants.EndpointAutopush, mock.Endpoints[1]);
        Assert.Equal(AntigravityConstants.EndpointProd, mock.Endpoints[2]);
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_RetriesOn5xx()
    {
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, _) =>
            {
                callCount++;
                return callCount == 1 ? Status(500, "internal server error") : Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("claude-sonnet-4-5"));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, mock.CallCount);
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_DoesNotRetryOn400()
    {
        var mock = new AntigravityTestExecutor { DoFn = (_, _) => Status(400, "bad request") };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("claude-sonnet-4-5"));

        Assert.Equal(400, resp.StatusCode);
        Assert.Equal(1, mock.CallCount); // Should not retry.
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_ExhaustsAllEndpoints()
    {
        var mock = new AntigravityTestExecutor { DoFn = (_, _) => Status(403, "forbidden") };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("claude-sonnet-4-5"));

        Assert.Equal(403, resp.StatusCode);    // Returns the last error.
        Assert.Equal(3, mock.CallCount);      // Tried all three endpoints.
    }

    [Fact]
    public void TestExecutor_DoWithEndpointFallback_HandlesNetworkErrors()
    {
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("network error");
                }

                return Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);

        // Network errors return immediately without retrying.
        var ex = Assert.Throws<HttpRequestException>(() => executor.Do(CancellationToken.None, Request("claude-sonnet-4-5")));

        Assert.Equal("network error", ex.Message);
        Assert.Equal(1, mock.CallCount);
    }

    // ------------------------------------------------------------------
    // ReplaceBaseURL (reference TestExecutor_ReplaceBaseURL)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(
        "https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:generateContent",
        "https://cloudcode-pa.googleapis.com",
        "https://cloudcode-pa.googleapis.com/v1internal:generateContent")]
    [InlineData(
        "https://cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse",
        "https://autopush-cloudcode-pa.sandbox.googleapis.com",
        "https://autopush-cloudcode-pa.sandbox.googleapis.com/v1internal:streamGenerateContent?alt=sse")]
    [InlineData(
        "https://example.com/other/path",
        "https://newbase.com",
        "https://example.com/other/path")]
    public void TestExecutor_ReplaceBaseURL(string originalURL, string newBase, string expected)
        => Assert.Equal(expected, AntigravityExecutor.ReplaceBaseURL(originalURL, newBase));

    // ------------------------------------------------------------------
    // GetEndpointsInOrder (reference TestExecutor_GetEndpointsInOrder)
    // ------------------------------------------------------------------

    [Fact]
    public void TestExecutor_GetEndpointsInOrder_GeminiCliModelStartsWithDaily()
    {
        var endpoints = AntigravityExecutor.GetEndpointsInOrder("gemini-2.5-pro");

        Assert.Equal(3, endpoints.Count);
        Assert.Equal(AntigravityConstants.EndpointDaily, endpoints[0]);
        Assert.Contains(AntigravityConstants.EndpointProd, endpoints);
        Assert.Contains(AntigravityConstants.EndpointAutopush, endpoints);
    }

    [Fact]
    public void TestExecutor_GetEndpointsInOrder_AntigravityModelStartsWithDaily()
    {
        var endpoints = AntigravityExecutor.GetEndpointsInOrder("claude-sonnet-4-5");

        Assert.Equal(3, endpoints.Count);
        Assert.Equal(AntigravityConstants.EndpointDaily, endpoints[0]);
        Assert.Contains(AntigravityConstants.EndpointProd, endpoints);
        Assert.Contains(AntigravityConstants.EndpointAutopush, endpoints);
    }

    [Fact]
    public void TestExecutor_GetEndpointsInOrder_ExplicitSuffixWorksWithModel()
    {
        // All models start with Daily regardless of suffix (quota preference).
        var endpoints = AntigravityExecutor.GetEndpointsInOrder("gemini-2.5-pro:antigravity");

        Assert.Equal(3, endpoints.Count);
        Assert.Equal(AntigravityConstants.EndpointDaily, endpoints[0]);
    }

    [Fact]
    public void TestExecutor_GetEndpointsInOrder_NoModelUsesDefaultFallbackOrder()
    {
        Assert.Equal(
            [AntigravityConstants.EndpointDaily, AntigravityConstants.EndpointAutopush, AntigravityConstants.EndpointProd],
            AntigravityExecutor.GetEndpointsInOrder(""));
    }

    // ------------------------------------------------------------------
    // Do with cooldown (reference TestExecutor_DoWithCooldown_*)
    // ------------------------------------------------------------------

    [Fact]
    public void TestExecutor_DoWithCooldown_SkipsFailedEndpoint()
    {
        var callCount = 0;
        var lastEndpoint = string.Empty;

        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, req) =>
            {
                callCount++;
                var endpoint = AntigravityTestExecutor.ExtractEndpoint(req.Url);
                lastEndpoint = endpoint;

                // Daily always fails with 429.
                if (endpoint == AntigravityConstants.EndpointDaily)
                {
                    return Status(429, "rate limit exceeded");
                }

                // Other endpoints succeed.
                return Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);
        var request = Request("claude-sonnet-4-5");

        // First request - tries Daily (fails 429), then Autopush (succeeds).
        var resp1 = executor.Do(CancellationToken.None, request);
        Assert.Equal(200, resp1.StatusCode); // Succeeded on fallback.
        Assert.Equal(2, callCount);          // Tried Daily + Autopush.
        Assert.Equal(AntigravityConstants.EndpointAutopush, lastEndpoint);

        // Second request - should skip Daily (in cooldown) and go straight to Autopush.
        callCount = 0;
        var resp2 = executor.Do(CancellationToken.None, request);
        Assert.Equal(200, resp2.StatusCode);
        Assert.Equal(1, callCount); // Only tried Autopush (skipped Daily).
        Assert.Equal(AntigravityConstants.EndpointAutopush, lastEndpoint);
    }

    [Fact]
    public void TestExecutor_DoWithCooldown_FailsFastWhenAllInCooldown()
    {
        var mock = new AntigravityTestExecutor { DoFn = (_, _) => Status(503, "service unavailable") };
        var executor = new AntigravityExecutor(mock);
        var request = Request("claude-sonnet-4-5");

        // First request - all endpoints fail and go into cooldown.
        var resp1 = executor.Do(CancellationToken.None, request);
        Assert.Equal(503, resp1.StatusCode);

        // Second request - should fail fast without trying any endpoint.
        var ex = Assert.Throws<InvalidOperationException>(() => executor.Do(CancellationToken.None, request));
        Assert.Contains("all antigravity endpoints in cooldown", ex.Message);
    }

    [Fact]
    public void TestExecutor_DoWithCooldown_PerModelIsolation()
    {
        var callsByModel = new Dictionary<string, int>(StringComparer.Ordinal);
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, req) =>
            {
                var model = req.Metadata!["antigravity_model"];
                callsByModel[model] = callsByModel.GetValueOrDefault(model) + 1;
                var endpoint = AntigravityTestExecutor.ExtractEndpoint(req.Url);

                if (model == "claude-sonnet-4-5" && endpoint == AntigravityConstants.EndpointDaily)
                {
                    // Claude fails on Daily.
                    return Status(429, "rate limit");
                }

                // Everything else succeeds.
                return Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);

        var claudeRequest = Request("claude-sonnet-4-5");
        var geminiRequest = Request(
            "gemini-2.5-pro",
            AntigravityConstants.EndpointProd + "/v1internal:generateContent");

        // Claude request fails on Daily (429), succeeds on Autopush (fallback).
        var resp1 = executor.Do(CancellationToken.None, claudeRequest);
        Assert.Equal(200, resp1.StatusCode);
        Assert.Equal(2, callsByModel["claude-sonnet-4-5"]);

        // Gemini request should try Daily (all models start with Daily);
        // Daily is a different model so isolation works.
        var resp2 = executor.Do(CancellationToken.None, geminiRequest);
        Assert.Equal(200, resp2.StatusCode);
        Assert.Equal(1, callsByModel["gemini-2.5-pro"]);
    }

    [Fact]
    public void TestExecutor_DoWithCooldown_RecoveryAfterSuccess()
    {
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, req) =>
            {
                callCount++;
                var endpoint = AntigravityTestExecutor.ExtractEndpoint(req.Url);

                if (callCount == 1 && endpoint == AntigravityConstants.EndpointDaily)
                {
                    // First call to Daily fails.
                    return Status(503, "service unavailable");
                }

                // All other calls succeed.
                return Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);
        var request = Request("claude-sonnet-4-5");

        // First request - Daily fails, falls back to Autopush.
        var resp1 = executor.Do(CancellationToken.None, request);
        Assert.Equal(200, resp1.StatusCode);

        // Second request - Daily is in cooldown, goes straight to Autopush.
        var resp2 = executor.Do(CancellationToken.None, request);
        Assert.Equal(200, resp2.StatusCode);

        // Autopush succeeded, so it should NOT be in cooldown.
        // Third request should still skip Daily (in cooldown) but use Autopush.
        var resp3 = executor.Do(CancellationToken.None, request);
        Assert.Equal(200, resp3.StatusCode);
    }

    // ------------------------------------------------------------------
    // DoStream with cooldown (reference TestExecutor_DoStreamWithCooldown_SkipsFailedEndpoint)
    // ------------------------------------------------------------------

    [Fact]
    public void TestExecutor_DoStreamWithCooldown_SkipsFailedEndpoint()
    {
        var callCount = 0;
        var lastEndpoint = string.Empty;

        var mock = new AntigravityTestExecutor
        {
            DoStreamFn = (_, req) =>
            {
                callCount++;
                var endpoint = AntigravityTestExecutor.ExtractEndpoint(req.Url);
                lastEndpoint = endpoint;

                // Daily always fails.
                if (endpoint == AntigravityConstants.EndpointDaily)
                {
                    throw new IOException("connection error");
                }

                // Other endpoints succeed.
                return null;
            },
        };
        var executor = new AntigravityExecutor(mock);
        var request = Request(
            "claude-sonnet-4-5",
            AntigravityConstants.EndpointDaily + "/v1internal:streamGenerateContent?alt=sse");

        // First request - tries Daily (fails), then Autopush (succeeds).
        _ = executor.DoStream(CancellationToken.None, request);
        Assert.Equal(2, callCount);
        Assert.Equal(AntigravityConstants.EndpointAutopush, lastEndpoint);

        // Second request - should skip Daily and go straight to Autopush.
        callCount = 0;
        _ = executor.DoStream(CancellationToken.None, request);
        Assert.Equal(1, callCount); // Only tried Autopush (skipped Daily).
        Assert.Equal(AntigravityConstants.EndpointAutopush, lastEndpoint);
    }

    // ------------------------------------------------------------------
    // Retry on thrown transport errors (reference executor_retry_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public void TestExecutor_RetryOnNilResponseWithError()
    {
        // Verify that retry logic works when the transport returns no response
        // but a *httpclient.Error (the 429 case).
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, req) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpClientError(
                        req.Method,
                        req.Url,
                        429,
                        "429 Too Many Requests",
                        Encoding.UTF8.GetBytes("""{"error": "rate limit"}"""));
                }

                return Ok();
            },
        };
        var executor = new AntigravityExecutor(mock);

        var resp = executor.Do(CancellationToken.None, Request("claude-sonnet-4-5"));

        Assert.Equal(200, resp.StatusCode);
        Assert.Equal(2, mock.CallCount);
        // Should have tried Daily (failed) then Autopush (succeeded).
        Assert.Equal(AntigravityConstants.EndpointDaily, mock.Endpoints[0]);
        Assert.Equal(AntigravityConstants.EndpointAutopush, mock.Endpoints[1]);
    }

    [Fact]
    public void TestExecutor_NoRetryOnNonRetryableNilResponseWithError()
    {
        // Verify that we DO NOT retry for 400 Bad Request.
        var callCount = 0;
        var mock = new AntigravityTestExecutor
        {
            DoFn = (_, req) =>
            {
                callCount++;
                throw new HttpClientError(
                    req.Method,
                    req.Url,
                    400,
                    "400 Bad Request",
                    Encoding.UTF8.GetBytes("""{"error": "bad request"}"""));
            },
        };
        var executor = new AntigravityExecutor(mock);

        var ex = Assert.Throws<HttpClientError>(() => executor.Do(CancellationToken.None, Request("claude-sonnet-4-5")));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, mock.CallCount); // Should NOT retry.
    }
}
