using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Stands in for <c>httpclient.HttpClient</c> in the OAuth tests: records the
/// request the provider built and answers it through a scripted handler.
/// </summary>
/// <remarks>
/// The reference tests use an <c>httptest.Server</c>; this fake keeps the same
/// observable contract — including the transport rule that a status >= 400
/// surfaces as <see cref="HttpClientError"/> — without a network listener, so
/// the assertions lock the exact bytes the real executor would send.
/// </remarks>
internal sealed class OAuthTestExecutor : IExecutor
{
    private readonly object _sync = new();
    private readonly List<HttpRequestData> _requests = [];

    public Func<HttpRequestData, HttpResponseData> Handler { get; set; }
        = _ => throw new InvalidOperationException("oauth test handler is not configured");

    public List<HttpRequestData> Requests
    {
        get
        {
            lock (_sync)
            {
                return [.. _requests];
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_sync)
            {
                return _requests.Count;
            }
        }
    }

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        lock (_sync)
        {
            _requests.Add(request);
        }

        var response = Handler(request);
        if (response.StatusCode >= 400)
        {
            throw new HttpClientError(
                request.Method,
                request.Url,
                response.StatusCode,
                response.StatusCode + " Bad Request",
                response.Body)
            {
                Headers = response.Headers,
            };
        }

        return response;
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
        => throw new NotSupportedException("OAuth providers never open a stream");

    public HttpRequestData SingleRequest()
    {
        var requests = Requests;
        Assert.Single(requests);
        return requests[0];
    }
}

/// <summary>Port of the mockTokenExchanger used by device_flow_provider_test.go.</summary>
internal sealed class MockTokenExchanger : ITokenExchanger
{
    public string Token { get; set; } = string.Empty;

    public long ExpiresAt { get; set; }

    public Exception? Error { get; set; }

    public int ExchangeWithClientCalls { get; private set; }

    public int ExchangeCalls { get; private set; }

    public TokenExchangeResult Exchange(CancellationToken cancellationToken, string accessToken)
    {
        _ = accessToken;
        ExchangeCalls++;
        return Error is null
            ? TokenExchangeResult.Success(Token, ExpiresAt)
            : TokenExchangeResult.Failure(Error);
    }

    public TokenExchangeResult ExchangeWithClient(
        CancellationToken cancellationToken,
        IExecutor? httpClient,
        string accessToken)
    {
        _ = httpClient;
        _ = accessToken;
        ExchangeWithClientCalls++;
        return Error is null
            ? TokenExchangeResult.Success(Token, ExpiresAt)
            : TokenExchangeResult.Failure(Error);
    }
}

internal static class OAuthTestSupport
{
    public static HttpResponseData Json(string json, int statusCode = 200) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(json),
    };

    public static Dictionary<string, string> ParseForm(HttpRequestData request)
    {
        var text = Encoding.UTF8.GetString(request.Body ?? []);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Unescape(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Unescape(pair[(separator + 1)..]);
            result[key] = value;
        }

        return result;
    }

    public static string BodyString(HttpRequestData request)
        => Encoding.UTF8.GetString(request.Body ?? []);

    public static string Header(HttpRequestData request, string name)
        => HttpClientUtils.HeaderGet(request.Headers, name);

    public static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }

    private static string Unescape(string value)
        => Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
}
