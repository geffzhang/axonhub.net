using System.Net;
using System.Text;
using AxonHub.Api.Auth.Oidc;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks <c>OIDCService.fetchUserInfo</c> (internal/server/biz/oidc.go:804-866):
/// the discovery path models go-oidc's <c>Provider.UserInfo</c>, the manual
/// path the plain authenticated GET, and both keep the reference's error
/// texts and the GitHub <c>id</c>/<c>login</c> subject fallback.
/// </summary>
public sealed class OidcUserInfoClientTests
{
    [Fact]
    public async Task TheDiscoveryPathSendsABearerGetToTheConfiguredEndpoint()
    {
        using var handler = new StubHttpHandler(_ => Json(
            "{\"sub\":\"s1\",\"email\":\"sso@example.com\",\"email_verified\":true,"
            + "\"name\":\"SSO User\",\"groups\":[\"A\"]}"));
        using var client = new HttpClient(handler);

        var result = await new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
            UseDiscovery: true,
            "https://idp.example.com/userinfo",
            "access-1",
            "bearer"));

        Assert.Equal(HttpMethod.Get, Assert.Single(handler.Requests).Method);
        Assert.Equal("https://idp.example.com/userinfo", handler.Requests[0].RequestUri);
        Assert.Equal("Bearer access-1", handler.Requests[0].Authorization);
        Assert.Equal("s1", result.Claims.Sub);
        Assert.Equal("sso@example.com", result.Claims.Email);
        Assert.True(result.Claims.EmailVerified);
        Assert.Equal("SSO User", result.Claims.Name);
    }

    [Fact]
    public async Task AnEmptyDiscoveryEndpointFailsWithoutARequest()
    {
        using var handler = new StubHttpHandler(_ => Json("{}"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
                UseDiscovery: true,
                string.Empty,
                "access-1",
                "bearer")));

        Assert.Equal(
            "oidc: user info endpoint is not supported by this provider",
            exception.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ADiscoveryFailureRendersTheStatusAndBody()
    {
        using var handler = new StubHttpHandler(_ => Text(HttpStatusCode.InternalServerError, "boom"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
                UseDiscovery: true,
                "https://idp.example.com/userinfo",
                "access-1",
                "bearer")));

        Assert.Equal("oidc: failed to query userinfo: 500: boom", exception.Message);
    }

    [Fact]
    public async Task ADiscoveryNonObjectBodyIsRejected()
    {
        using var handler = new StubHttpHandler(_ => Json("[]"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
                UseDiscovery: true,
                "https://idp.example.com/userinfo",
                "access-1",
                "bearer")));

        Assert.Equal(
            "oidc: failed to decode userinfo: json: cannot unmarshal array "
            + "into Go value of type oidc.UserInfo",
            exception.Message);
    }

    [Fact]
    public async Task TheManualPathUsesTheOAuth2TokenTypeName()
    {
        using var handler = new StubHttpHandler(_ => Json("{\"sub\":\"s1\"}"));
        using var client = new HttpClient(handler);

        await new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
            UseDiscovery: false,
            "https://api.github.com/user",
            "access-1",
            "mac"));

        Assert.Equal("MAC access-1", Assert.Single(handler.Requests).Authorization);
    }

    [Fact]
    public async Task AManualFailureRendersOnlyTheStatus()
    {
        using var handler = new StubHttpHandler(_ => Text(HttpStatusCode.Forbidden, "nope"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
                UseDiscovery: false,
                "https://api.github.com/user",
                "access-1",
                "bearer")));

        Assert.Equal("userinfo request failed with status: 403", exception.Message);
    }

    [Fact]
    public async Task ANumericGitHubIdBecomesTheSubject()
    {
        using var handler = new StubHttpHandler(_ => Json("{\"id\":123456789}"));
        using var client = new HttpClient(handler);

        var result = await new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
            UseDiscovery: false,
            "https://api.github.com/user",
            "access-1",
            "bearer"));

        Assert.Equal("123456789", result.Claims.Sub);
    }

    [Fact]
    public async Task TheGitHubLoginBecomesTheSubjectWhenThereIsNoId()
    {
        using var handler = new StubHttpHandler(_ => Json("{\"login\":\"octocat\"}"));
        using var client = new HttpClient(handler);

        var result = await new OidcUserInfoClient(client).GetAsync(new OidcUserInfoRequest(
            UseDiscovery: false,
            "https://api.github.com/user",
            "access-1",
            "bearer"));

        Assert.Equal("octocat", result.Claims.Sub);
    }

    [Fact]
    public void TheTokenTypeNameFollowsOAuth2TokenType()
    {
        Assert.Equal("Bearer", OidcUserInfoClient.TokenTypeName("bearer"));
        Assert.Equal("MAC", OidcUserInfoClient.TokenTypeName("MAC"));
        Assert.Equal("Basic", OidcUserInfoClient.TokenTypeName("basic"));
        Assert.Equal("custom", OidcUserInfoClient.TokenTypeName("custom"));
        Assert.Equal("Bearer", OidcUserInfoClient.TokenTypeName(string.Empty));
    }

    private static HttpResponseMessage Json(string body)
        => new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Text(HttpStatusCode status, string body)
        => new()
        {
            StatusCode = status,
            Content = new StringContent(body, Encoding.UTF8, "text/plain"),
        };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<RecordedUserInfoRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Requests.Add(new RecordedUserInfoRequest(
                request.Method,
                request.RequestUri?.ToString(),
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    internal sealed record RecordedUserInfoRequest(
        HttpMethod Method,
        string? RequestUri,
        string? Authorization);
}
