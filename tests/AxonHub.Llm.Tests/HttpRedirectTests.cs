using System.Net;
using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the redirect loop ported from net/http.Client.do (client.go:597-755) and
/// the two rejecting policies of llm/httpclient/client.go:110-151.
/// </summary>
public sealed class HttpRedirectTests
{
    [Fact]
    public void FollowsRelativeRedirectAndKeepsGetMethod()
    {
        var handler = new RedirectHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/start" => Redirect(HttpStatusCode.Found, "/next"),
            _ => Ok("""{"ok":true}"""),
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var response = executor.Do(CancellationToken.None, Get("https://u.example/start"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(response.Body!));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[0].Method);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.Equal("https://u.example/next", handler.Requests[1].Url);
    }

    [Fact]
    public void RewritesPostToGetAfter302AndDropsBodyAndBodyHeaders()
    {
        var handler = new RedirectHandler(request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect(HttpStatusCode.Found, "/next")
            : Ok("{}"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var response = executor.Do(CancellationToken.None, new HttpRequestData
        {
            Method = "POST",
            Url = "https://u.example/start",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"model":"m"}"""),
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = "sk-test" },
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("""{"model":"m"}""", handler.Requests[0].Body);

        var hop = handler.Requests[1];
        Assert.Equal("GET", hop.Method);
        Assert.Null(hop.Body);
        Assert.Equal(string.Empty, hop.Header("Content-Type"));
        // Same host keeps the credential headers; the safe ones always travel.
        Assert.Equal("Bearer sk-test", hop.Header("Authorization"));
        Assert.Equal("application/json", hop.Header("Accept"));
    }

    [Fact]
    public void PreservesMethodAndBodyFor307()
    {
        var handler = new RedirectHandler(request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect(HttpStatusCode.TemporaryRedirect, "/retry")
            : Ok("{}"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        executor.Do(CancellationToken.None, new HttpRequestData
        {
            Method = "POST",
            Url = "https://u.example/start",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"a":1}"""),
        });

        var hop = handler.Requests[1];
        Assert.Equal("POST", hop.Method);
        Assert.Equal("""{"a":1}""", hop.Body);
        Assert.Equal("application/json", hop.Header("Content-Type"));
    }

    [Fact]
    public void StripsSensitiveHeadersWhenHostChanges()
    {
        var handler = new RedirectHandler(RedirectOnceTo("https://other.example/next"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var request = Get("https://u.example/start");
        request.Headers!["Cookie"] = ["session=abc"];

        executor.Do(CancellationToken.None, request);

        var hop = handler.Requests[1];
        Assert.Equal("https://other.example/next", hop.Url);
        Assert.Equal(string.Empty, hop.Header("Authorization"));
        Assert.Equal(string.Empty, hop.Header("Cookie"));
        Assert.Equal("application/json", hop.Header("Accept"));
    }

    [Fact]
    public void KeepsSensitiveHeadersForSubdomain()
    {
        var handler = new RedirectHandler(RedirectOnceTo("https://api.u.example/next"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        executor.Do(CancellationToken.None, Get("https://u.example/start"));

        Assert.Equal("Bearer sk-test", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public void AddsRefererFromPreviousUrl()
    {
        var handler = new RedirectHandler(request => request.RequestUri!.AbsolutePath == "/one"
            ? Redirect(HttpStatusCode.Found, "/two")
            : Ok("{}"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        executor.Do(CancellationToken.None, Get("https://u.example/one"));

        Assert.Equal("https://u.example/one", handler.Requests[1].Header("Referer"));
    }

    [Fact]
    public void StopsAfterTenRedirects()
    {
        var next = 0;
        var handler = new RedirectHandler(_ =>
        {
            next++;
            return Redirect(HttpStatusCode.MovedPermanently, "/r" + next.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var error = Assert.Throws<HttpRedirectException>(() =>
            executor.Do(CancellationToken.None, Get("https://u.example/r0")));

        Assert.Equal(
            """HTTP request failed: Get "/r10": stopped after 10 redirects""",
            error.Message);
        Assert.Equal(10, handler.Requests.Count);
    }

    [Fact]
    public void RejectsHttpsToHttpDowngrade()
    {
        var handler = new RedirectHandler(_ => Redirect(HttpStatusCode.Found, "http://u.example/plain"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client).WithRejectHttpsDowngrade();

        var error = Assert.Throws<HttpRedirectException>(() =>
            executor.Do(CancellationToken.None, Get("https://u.example/secure")));

        Assert.Equal(
            """HTTP request failed: Get "http://u.example/plain": refusing HTTPS to HTTP redirect""",
            error.Message);
        Assert.Single(handler.Requests);
        Assert.Equal("https://u.example/secure", handler.Requests[0].Url);
    }

    [Fact]
    public void DowngradePolicyStillFollowsHttpsToHttps()
    {
        var handler = new RedirectHandler(request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect(HttpStatusCode.Found, "https://u.example/next")
            : Ok("{}"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client).WithRejectHttpsDowngrade();

        var response = executor.Do(CancellationToken.None, Get("https://u.example/start"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void DowngradePolicyHasNoHopCap()
    {
        // Installing CheckRedirect replaces Go's defaultCheckRedirect, so the
        // policy client has no 10-hop limit (net/http/client.go:502-508).
        var next = 0;
        var handler = new RedirectHandler(_ =>
        {
            next++;
            return next <= 12
                ? Redirect(HttpStatusCode.Found, "/r" + next.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : Ok("{}");
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client).WithRejectHttpsDowngrade();

        var response = executor.Do(CancellationToken.None, Get("https://u.example/r0"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(13, handler.Requests.Count);
    }

    [Fact]
    public void RejectAllPolicyRefusesEveryRedirect()
    {
        var handler = new RedirectHandler(_ => Redirect(HttpStatusCode.Found, "https://u.example/next"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client).WithRejectRedirects();

        var error = Assert.Throws<HttpRedirectException>(() =>
            executor.Do(CancellationToken.None, Get("https://u.example/start")));

        Assert.Equal(
            """HTTP request failed: Get "https://u.example/next": refusing redirect for credential-bearing request""",
            error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void RedirectWithoutLocationIsReturnedAsResponse()
    {
        var handler = new RedirectHandler(_ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent("moved"),
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var response = executor.Do(CancellationToken.None, Get("https://u.example/start"));

        Assert.Equal(302, response.StatusCode);
        Assert.Equal("moved", Encoding.UTF8.GetString(response.Body!));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void RefusalObservesRedirectResponseHeaders()
    {
        IReadOnlyDictionary<string, List<string>>? observed = null;
        var handler = new RedirectHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "http://u.example/plain");
            response.Headers.TryAddWithoutValidation("X-Redirect-Reason", "downgrade");
            return response;
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client).WithRejectHttpsDowngrade();
        var request = Get("https://u.example/secure");
        request.OnResponseHeaders = headers => observed = headers;

        Assert.Throws<HttpRedirectException>(() => executor.Do(CancellationToken.None, request));

        Assert.NotNull(observed);
        Assert.Equal("downgrade", observed!["X-Redirect-Reason"][0]);
    }

    [Fact]
    public void DoStreamWrapsRefusalWithStreamPrefix()
    {
        var handler = new RedirectHandler(_ => Redirect(HttpStatusCode.Found, "http://u.example/plain"));

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client).WithRejectHttpsDowngrade();

        var error = Assert.Throws<HttpRedirectException>(() =>
            executor.DoStream(CancellationToken.None, new HttpRequestData
            {
                Method = "POST",
                Url = "https://u.example/stream",
                Body = Encoding.UTF8.GetBytes("{}"),
            }));

        Assert.Equal(
            """HTTP stream request failed: Post "http://u.example/plain": refusing HTTPS to HTTP redirect""",
            error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void UnparsableLocationFailsWithoutResponseHeaders()
    {
        IReadOnlyDictionary<string, List<string>>? observed = null;
        var handler = new RedirectHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "http://[::1");
            return response;
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var request = Get("https://u.example/start");
        request.OnResponseHeaders = headers => observed = headers;

        var error = Assert.Throws<HttpRedirectException>(() => executor.Do(CancellationToken.None, request));

        Assert.StartsWith(
            """HTTP request failed: Get "https://u.example/start": failed to parse Location header "http://[::1": parse "http://[::1": """,
            error.Message,
            StringComparison.Ordinal);
        Assert.Null(observed);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void ScriptedExecutorsIgnoreTheRedirectPolicies()
    {
        IExecutor scripted = new PassthroughExecutor();

        Assert.Same(scripted, scripted.WithRejectHttpsDowngrade());
        Assert.Same(scripted, scripted.WithRejectRedirects());
    }

    private static HttpRequestData Get(string url) => new()
    {
        Method = "GET",
        Url = url,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = "sk-test" },
    };

    private static Func<HttpRequestMessage, HttpResponseMessage> RedirectOnceTo(string location)
        => request => request.RequestUri!.AbsolutePath == "/start"
            ? Redirect(HttpStatusCode.Found, location)
            : Ok("{}");

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class RedirectHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest
            {
                Method = request.Method.Method,
                Url = request.RequestUri!.OriginalString,
                Headers = SnapshotHeaders(request),
                Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult(),
            });

            return Task.FromResult(responder(request));
        }

        private static Dictionary<string, string> SnapshotHeaders(HttpRequestMessage request)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, values) in request.Headers)
            {
                headers[key] = string.Join(", ", values);
            }

            if (request.Content is not null)
            {
                foreach (var (key, values) in request.Content.Headers)
                {
                    headers[key] = string.Join(", ", values);
                }
            }

            return headers;
        }
    }

    private sealed class RecordedRequest
    {
        public required string Method { get; init; }

        public required string Url { get; init; }

        public required Dictionary<string, string> Headers { get; init; }

        public string? Body { get; init; }

        public string Header(string name)
            => Headers.TryGetValue(name, out var value) ? value : string.Empty;
    }

    private sealed class PassthroughExecutor : IExecutor
    {
        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => throw new NotSupportedException();

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => throw new NotSupportedException();
    }
}
