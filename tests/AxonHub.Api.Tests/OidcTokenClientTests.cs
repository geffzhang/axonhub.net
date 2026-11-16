using System.Net;
using System.Text;
using AxonHub.Api.Auth.Oidc;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks golang.org/x/oauth2 v0.36.0's Config.Exchange wire shape
/// (internal/server/biz/oidc.go:619-785 calls into it): the sorted
/// form body, the Basic-first authentication-style probe with the
/// params retry, the per-(token URL, client id) style cache and the
/// RetrieveError renderings.
/// </summary>
public sealed class OidcTokenClientTests
{
    // The auth-style cache is process-wide, so every test gets its own token
    // URL to stay independent of test ordering and parallelism.
    private readonly string _tokenUrl =
        "https://token-" + Guid.NewGuid().ToString("N") + ".example.com/token";

    [Fact]
    public async Task TheFirstAttemptUsesBasicAuthAndTheSortedFormBody()
    {
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"at\",\"token_type\":\"bearer\"}"));
        using var client = new HttpClient(handler);

        var result = await new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
            _tokenUrl,
            "client-1",
            "secret-1",
            "https://app.example.com/oauth/oidc/callback",
            "code-1",
            "verifier-1"));

        Assert.Equal("at", result.AccessToken);
        Assert.Equal("bearer", result.TokenType);
        Assert.Null(result.IdToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(
            "code=code-1&code_verifier=verifier-1&grant_type=authorization_code"
            + "&redirect_uri=https%3A%2F%2Fapp.example.com%2Foauth%2Foidc%2Fcallback",
            request.Body);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("client-1:secret-1")),
            request.Authorization);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
    }

    [Fact]
    public async Task RedirectAndVerifierAreOmittedWhenEmpty()
    {
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"at\"}"));
        using var client = new HttpClient(handler);

        await new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
            _tokenUrl,
            "client-1",
            "secret-1",
            string.Empty,
            "code-1",
            null));

        Assert.Equal(
            "code=code-1&grant_type=authorization_code",
            Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task AFailedBasicProbeRetriesWithTheCredentialsInTheParams()
    {
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}"),
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"at\"}"));
        using var client = new HttpClient(handler);

        var result = await new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
            _tokenUrl,
            "client-1",
            "secret-1",
            "https://app.example.com/oauth/oidc/callback",
            "code-1",
            "verifier-1"));

        Assert.Equal("at", result.AccessToken);
        Assert.Equal(2, handler.Requests.Count);
        var retry = handler.Requests[1];
        Assert.Null(retry.Authorization);
        Assert.Equal(
            "client_id=client-1&client_secret=secret-1&code=code-1"
            + "&code_verifier=verifier-1&grant_type=authorization_code"
            + "&redirect_uri=https%3A%2F%2Fapp.example.com%2Foauth%2Foidc%2Fcallback",
            retry.Body);
    }

    [Fact]
    public async Task TheSuccessfulStyleIsCachedPerTokenUrlAndClient()
    {
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}"),
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"at\"}"),
            _ => Json(HttpStatusCode.OK, "{\"access_token\":\"at2\"}"));
        using var client = new HttpClient(handler);
        var tokens = new OidcTokenClient(client);
        var request = new OidcTokenRequest(
            _tokenUrl,
            "client-1",
            "secret-1",
            string.Empty,
            "code-1",
            null);

        await tokens.ExchangeAsync(request);
        var second = await tokens.ExchangeAsync(request);

        Assert.Equal("at2", second.AccessToken);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Null(handler.Requests[2].Authorization);
        Assert.Contains("client_id=client-1", handler.Requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenErrorUsesTheRetrieveErrorMessage()
    {
        const string Body =
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\","
            + "\"error_uri\":\"https://errors.example.com/invalid_grant\"}";
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.BadRequest, Body),
            _ => Json(HttpStatusCode.BadRequest, Body));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
                _tokenUrl,
                "client-1",
                "secret-1",
                string.Empty,
                "code-1",
                null)));

        Assert.Equal(
            "oauth2: \"invalid_grant\" \"bad code\" "
            + "\"https://errors.example.com/invalid_grant\"",
            exception.Message);
    }

    [Fact]
    public async Task AFailureWithoutAnErrorCodeRendersTheStatusAndBody()
    {
        using var handler = new StubHttpHandler(
            _ => Text(HttpStatusCode.InternalServerError, "oops"),
            _ => Text(HttpStatusCode.InternalServerError, "oops"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
                _tokenUrl,
                "client-1",
                "secret-1",
                string.Empty,
                "code-1",
                null)));

        Assert.Equal(
            "oauth2: cannot fetch token: 500 Internal Server Error\nResponse: oops",
            exception.Message);
    }

    [Fact]
    public async Task AFormEncodedTokenResponseIsParsed()
    {
        using var handler = new StubHttpHandler(
            _ => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(
                    "access_token=at&token_type=Bearer&id_token=id-token",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded"),
            });
        using var client = new HttpClient(handler);

        var result = await new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
            _tokenUrl,
            "client-1",
            "secret-1",
            string.Empty,
            "code-1",
            null));

        Assert.Equal("at", result.AccessToken);
        Assert.Equal("Bearer", result.TokenType);
        Assert.Equal("id-token", result.IdToken);
    }

    [Fact]
    public async Task AMissingAccessTokenFails()
    {
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.OK, "{\"token_type\":\"bearer\"}"),
            _ => Json(HttpStatusCode.OK, "{\"token_type\":\"bearer\"}"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
                _tokenUrl,
                "client-1",
                "secret-1",
                string.Empty,
                "code-1",
                null)));

        Assert.Equal("oauth2: server response missing access_token", exception.Message);
    }

    [Fact]
    public async Task AJsonErrorFieldOnASuccessStatusIsStillAnError()
    {
        using var handler = new StubHttpHandler(
            _ => Json(HttpStatusCode.OK, "{\"error\":\"invalid_grant\"}"),
            _ => Json(HttpStatusCode.OK, "{\"error\":\"invalid_grant\"}"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcTokenClient(client).ExchangeAsync(new OidcTokenRequest(
                _tokenUrl,
                "client-1",
                "secret-1",
                string.Empty,
                "code-1",
                null)));

        Assert.Equal("oauth2: \"invalid_grant\"", exception.Message);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new()
        {
            StatusCode = status,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Text(HttpStatusCode status, string body)
        => new()
        {
            StatusCode = status,
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

    internal sealed record RecordedRequest(
        HttpMethod Method,
        Uri? RequestUri,
        string? Authorization,
        string Body,
        string? ContentType);

    private sealed class StubHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.Authorization?.ToString(),
                body,
                request.Content?.Headers.ContentType?.MediaType));

            var respond = responses[Math.Min(_index, responses.Length - 1)];
            _index++;
            return respond(request);
        }
    }
}
