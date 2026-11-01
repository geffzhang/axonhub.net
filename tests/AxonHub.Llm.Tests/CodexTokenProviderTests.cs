using System.Net;
using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/codex/token_provider_test.go. The reference
/// stands up an <c>httptest.Server</c> and uses the real
/// <c>httpclient.NewHttpClient()</c>; the port drives the same path through
/// <see cref="HttpExecutor"/>, so the default <c>User-Agent</c> from
/// <c>BuildHttpRequest</c> stays under test.
/// </summary>
public sealed class CodexTokenProviderTests
{
    [Fact]
    public void TestTokenProvider_RefreshInvokesCallback()
    {
        var handler = new CodexTestHttpHandler((request, _) =>
        {
            // The reference asserts the default client User-Agent reaches the
            // token endpoint, not a provider-supplied one.
            Assert.Equal("axonhub/1.0", request.Headers.GetValues("User-Agent").First());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600,"token_type":"bearer","scope":"openid offline_access"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });

        var creds = new OAuthCredentials
        {
            AccessToken = "old-access",
            RefreshToken = "old-refresh",
            ClientId = CodexConstants.ClientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
        };

        var called = 0;

        using var httpClient = new HttpClient(handler);
        var provider = new TokenProvider(new TokenProviderParams
        {
            Credentials = creds,
            HttpClient = new HttpExecutor(httpClient),
            OAuthUrls = new OAuthUrls
            {
                AuthorizeUrl = CodexConstants.AuthorizeUrl,
                TokenUrl = "https://token.example/oauth/token",
            },
            OnRefreshed = (_, refreshed) =>
            {
                called++;

                Assert.Equal("new-access", refreshed.AccessToken);
                Assert.Equal("new-refresh", refreshed.RefreshToken);
                Assert.NotEqual(DateTimeOffset.MinValue, refreshed.ExpiresAt);
                Assert.Equal("bearer", refreshed.TokenType);
                Assert.NotNull(refreshed.Scopes);
                Assert.Contains("openid", refreshed.Scopes!);

                return null;
            },
        });

        var got = provider.Get(CancellationToken.None);
        Assert.Equal("new-access", got.AccessToken);
        Assert.Equal(1, called);
    }

    [Fact]
    public void TestTokenProvider_UnexpiredDoesNotInvokeCallback()
    {
        var creds = new OAuthCredentials
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ClientId = CodexConstants.ClientId,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };

        var called = 0;

        using var httpClient = new HttpClient(
            new CodexTestHttpHandler((_, _) =>
                throw new InvalidOperationException("unexpired credentials must not refresh")));
        var provider = new TokenProvider(new TokenProviderParams
        {
            Credentials = creds,
            HttpClient = new HttpExecutor(httpClient),
            OAuthUrls = CodexToken.DefaultTokenUrls(),
            OnRefreshed = (_, _) =>
            {
                called++;
                return null;
            },
        });

        var got = provider.Get(CancellationToken.None);
        Assert.Equal("access", got.AccessToken);
        Assert.Equal(0, called);
    }
}
