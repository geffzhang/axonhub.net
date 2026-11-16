using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the credential-conflict half of llm/oauth/token_provider.go
/// (token_provider.go:196-257, 284-351): <c>OnRefreshed</c> may veto the
/// refreshed credentials with a <see cref="CredentialConflictException"/>; a
/// non-nil winner is adopted (and refreshed again when still expired), a nil
/// winner retires the provider, and any other callback failure is ignored.
/// </summary>
public sealed class OAuthTokenProviderConflictTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static OAuthCredentials ExpiredCredentials() => new()
    {
        ClientId = "client-1",
        AccessToken = "access-old",
        RefreshToken = "refresh-old",
        ExpiresAt = Now.AddMinutes(-10),
    };

    private static TokenProvider NewProvider(
        OAuthTestExecutor executor,
        Func<CancellationToken, OAuthCredentials, Exception?> onRefreshed,
        OAuthCredentials? credentials = null)
        => new(new TokenProviderParams
        {
            HttpClient = executor,
            OAuthUrls = new OAuthUrls { TokenUrl = "https://example.com/token" },
            Credentials = credentials ?? ExpiredCredentials(),
            OnRefreshed = onRefreshed,
        });

    [Fact]
    public void ConflictWinnerIsAdopted()
    {
        var refreshCalls = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = _ =>
            {
                refreshCalls++;
                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-refreshed","refresh_token":"refresh-refreshed","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        var winner = new OAuthCredentials
        {
            ClientId = "client-1",
            AccessToken = "access-winner",
            RefreshToken = "refresh-winner",
            ExpiresAt = Now.AddHours(1),
        };

        var provider = NewProvider(
            executor,
            (_, _) => new CredentialConflictException(winner));

        var actual = provider.Get(CancellationToken.None);

        Assert.Same(winner, actual);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public void ExpiredConflictWinnerIsRefreshedAgain()
    {
        var forms = new List<Dictionary<string, string>>();
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                forms.Add(OAuthTestSupport.ParseForm(request));
                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-refreshed","refresh_token":"refresh-refreshed","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        // token_provider.go:204-208/300-304: a winner that is still expired is
        // refreshed again with the winner's refresh token.
        var expiredWinner = new OAuthCredentials
        {
            ClientId = "client-1",
            AccessToken = "access-winner",
            RefreshToken = "refresh-winner",
            ExpiresAt = Now.AddMinutes(-1),
        };

        var refreshedCalls = 0;
        var provider = NewProvider(
            executor,
            (_, _) =>
            {
                refreshedCalls++;
                return refreshedCalls == 1
                    ? new CredentialConflictException(expiredWinner)
                    : null;
            });

        var actual = provider.Get(CancellationToken.None);

        Assert.Equal("access-refreshed", actual.AccessToken);
        Assert.Equal(2, refreshedCalls);
        Assert.Equal(2, forms.Count);
        Assert.Equal("refresh-old", forms[0]["refresh_token"]);
        Assert.Equal("refresh-winner", forms[1]["refresh_token"]);
    }

    [Fact]
    public void NilConflictWinnerRetiresTheProvider()
    {
        var refreshCalls = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = _ =>
            {
                refreshCalls++;
                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-refreshed","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        var provider = NewProvider(
            executor,
            (_, _) => new CredentialConflictException(null));

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.Get(CancellationToken.None));
        Assert.Equal("credentials is nil", error.Message);

        // The provider cached nil and stopped its loop: later reads fail
        // without touching the network.
        error = Assert.Throws<InvalidOperationException>(
            () => provider.Get(CancellationToken.None));
        Assert.Equal("credentials is nil", error.Message);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public void NonConflictPersistFailureKeepsTheRefreshedCredentials()
    {
        var refreshCalls = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = _ =>
            {
                refreshCalls++;
                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-refreshed","refresh_token":"refresh-refreshed","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        var provider = NewProvider(
            executor,
            (_, _) => new InvalidOperationException("database is down"));

        var actual = provider.Get(CancellationToken.None);

        Assert.Equal("access-refreshed", actual.AccessToken);

        // The refreshed credentials stay cached, so the next read is local.
        var again = provider.Get(CancellationToken.None);
        Assert.Same(actual, again);
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public void ExpiredWinnerWithoutRefreshTokenIsNotRetried()
    {
        var refreshCalls = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = _ =>
            {
                refreshCalls++;
                return OAuthTestSupport.Json(
                    """
                    {"access_token":"access-refreshed","token_type":"Bearer","expires_in":3600}
                    """);
            },
        };

        var winner = new OAuthCredentials
        {
            ClientId = "client-1",
            AccessToken = "access-winner",
            ExpiresAt = Now.AddMinutes(-1),
        };

        var provider = NewProvider(
            executor,
            (_, _) => new CredentialConflictException(winner));

        var actual = provider.Get(CancellationToken.None);

        Assert.Same(winner, actual);
        Assert.Equal(1, refreshCalls);
    }
}
