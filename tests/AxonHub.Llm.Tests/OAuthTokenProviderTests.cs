using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/oauth/token_provider_test.go. The reference's
/// <c>TestTokenProviderGetRefreshFlowSingleflight</c> is skipped upstream
/// ("skipping unstable test"); its singleflight path is covered by the ported
/// device-flow test instead (see differences ledger C093).
/// </summary>
public sealed class OAuthTokenProviderTests
{
    private static TokenProvider NewProvider(
        IExecutor? httpClient,
        OAuthCredentials? credentials = null,
        string tokenUrl = "",
        string userAgent = "")
        => new(new TokenProviderParams
        {
            HttpClient = httpClient,
            Credentials = credentials,
            OAuthUrls = new OAuthUrls { TokenUrl = tokenUrl },
            UserAgent = userAgent,
        });

    [Fact]
    public void ExchangeValidation()
    {
        var provider = NewProvider(httpClient: null);
        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(CancellationToken.None, new ExchangeParams()));
        Assert.Equal("http client is nil", error.Message);

        provider = NewProvider(new OAuthTestExecutor());
        error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(CancellationToken.None, new ExchangeParams()));
        Assert.Equal("token URL is empty", error.Message);

        provider = NewProvider(new OAuthTestExecutor(), tokenUrl: "http://example.com/token");

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(CancellationToken.None, new ExchangeParams()));
        Assert.Equal("code is empty", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(
                CancellationToken.None,
                new ExchangeParams { Code = "code" }));
        Assert.Equal("code_verifier is empty", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(
                CancellationToken.None,
                new ExchangeParams { Code = "code", CodeVerifier = "verifier" }));
        Assert.Equal("client_id is empty", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(
                CancellationToken.None,
                new ExchangeParams
                {
                    Code = "code",
                    CodeVerifier = "verifier",
                    ClientId = "client",
                }));
        Assert.Equal("redirect_uri is empty", error.Message);
    }

    [Fact]
    public void ExchangeSuccess()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                Assert.Equal("POST", request.Method);
                Assert.Equal("https://example.com/token", request.Url);
                Assert.Equal(
                    "application/x-www-form-urlencoded",
                    OAuthTestSupport.Header(request, "Content-Type"));
                Assert.Equal("application/json", OAuthTestSupport.Header(request, "Accept"));
                Assert.Equal("axonhub-test", OAuthTestSupport.Header(request, "User-Agent"));

                var form = OAuthTestSupport.ParseForm(request);
                Assert.Equal("authorization_code", form["grant_type"]);
                Assert.Equal("client-1", form["client_id"]);
                Assert.Equal("code-1", form["code"]);
                Assert.Equal("https://example.com/callback", form["redirect_uri"]);
                Assert.Equal("verifier-1", form["code_verifier"]);

                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-1","refresh_token":"refresh-1","token_type":"Bearer","scope":"openid profile","expires_in":3600}
                    """);
            },
        };

        var provider = NewProvider(
            executor,
            tokenUrl: "https://example.com/token",
            userAgent: "axonhub-test");

        var start = DateTimeOffset.UtcNow;
        var creds = provider.Exchange(
            CancellationToken.None,
            new ExchangeParams
            {
                Code = "code-1",
                CodeVerifier = "verifier-1",
                ClientId = "client-1",
                RedirectUri = "https://example.com/callback",
            });

        Assert.Equal("client-1", creds.ClientId);
        Assert.Equal("access-1", creds.AccessToken);
        Assert.Equal("refresh-1", creds.RefreshToken);
        Assert.Equal("Bearer", creds.TokenType);
        Assert.Equal(["openid", "profile"], creds.Scopes);
        Assert.True(creds.ExpiresAt > start);
        Assert.True(creds.ExpiresAt < start.AddHours(2));

        // Ensure the provider cached the credentials.
        var got = provider.Get(CancellationToken.None);
        Assert.Same(creds, got);
    }

    [Fact]
    public void ExchangeErrorResponse()
    {
        var executor = new OAuthTestExecutor
        {
            Handler = _ => OAuthTestSupport.Json(
                """{"error":"invalid_grant","error_description":"bad code"}"""),
        };

        var provider = NewProvider(executor, tokenUrl: "https://example.com/token");

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Exchange(
                CancellationToken.None,
                new ExchangeParams
                {
                    Code = "code-1",
                    CodeVerifier = "verifier-1",
                    ClientId = "client-1",
                    RedirectUri = "https://example.com/callback",
                }));
        Assert.Equal("token exchange failed: invalid_grant - bad code", error.Message);
    }

    [Fact]
    public void RefreshValidation()
    {
        var provider = new TokenProvider(new TokenProviderParams());

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(CancellationToken.None, null));
        Assert.Equal("nil credentials", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(CancellationToken.None, new OAuthCredentials()));
        Assert.Equal("refresh_token is empty", error.Message);

        error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(
                CancellationToken.None,
                new OAuthCredentials { RefreshToken = "refresh" }));
        Assert.Equal("token URL is empty", error.Message);

        provider = new TokenProvider(new TokenProviderParams
        {
            OAuthUrls = new OAuthUrls { TokenUrl = "http://example.com/token" },
        });
        error = Assert.Throws<InvalidOperationException>(
            () => provider.Refresh(
                CancellationToken.None,
                new OAuthCredentials { RefreshToken = "refresh" }));
        Assert.Equal("http client is nil", error.Message);
    }
}
