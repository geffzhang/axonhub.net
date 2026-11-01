using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Tests;

/// <summary>Port of llm/oauth/token_provider_autorefresh_test.go.</summary>
public sealed class OAuthTokenProviderAutoRefreshTests
{
    [Fact]
    public void StartStopIsIdempotent()
    {
        var provider = new TokenProvider(new TokenProviderParams
        {
            Credentials = new OAuthCredentials
            {
                AccessToken = "access",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
        });

        var options = new AutoRefreshOptions
        {
            Interval = TimeSpan.FromMilliseconds(10),
            RefreshBefore = TimeSpan.FromMinutes(1),
        };

        provider.StartAutoRefresh(CancellationToken.None, options);
        provider.StartAutoRefresh(CancellationToken.None, options);

        Thread.Sleep(30);

        provider.StopAutoRefresh();
        provider.StopAutoRefresh();

        provider.StartAutoRefresh(CancellationToken.None, options);
        provider.StopAutoRefresh();
    }

    [Fact]
    public void StopCancelsSchedule()
    {
        var refreshCalls = 0;
        var executor = new OAuthTestExecutor
        {
            Handler = request =>
            {
                Interlocked.Increment(ref refreshCalls);

                var form = OAuthTestSupport.ParseForm(request);
                Assert.Equal("refresh_token", form["grant_type"]);
                Assert.Equal("client-1", form["client_id"]);
                Assert.Equal("refresh-1", form["refresh_token"]);

                return OAuthTestSupport.Json(
                    $$"""
                    {"access_token":"access-{{DateTimeOffset.UtcNow:O}}","token_type":"Bearer","expires_in":1}
                    """);
            },
        };

        var onRefreshedCalls = 0;
        var provider = new TokenProvider(new TokenProviderParams
        {
            HttpClient = executor,
            OAuthUrls = new OAuthUrls { TokenUrl = "https://example.com/token" },
            Credentials = new OAuthCredentials
            {
                ClientId = "client-1",
                AccessToken = "access-0",
                RefreshToken = "refresh-1",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
            OnRefreshed = (_, _) =>
            {
                Interlocked.Increment(ref onRefreshedCalls);
                return null;
            },
        });

        provider.StartAutoRefresh(
            CancellationToken.None,
            new AutoRefreshOptions
            {
                Interval = TimeSpan.FromMilliseconds(10),
                RefreshBefore = TimeSpan.FromMilliseconds(950),
            });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);

        while (Volatile.Read(ref onRefreshedCalls) < 2)
        {
            Assert.True(
                DateTimeOffset.UtcNow < deadline,
                "auto refresh did not trigger enough times: onRefreshed="
                    + Volatile.Read(ref onRefreshedCalls)
                    + ", refreshCalls=" + Volatile.Read(ref refreshCalls));
            Thread.Sleep(10);
        }

        provider.StopAutoRefresh();

        var stoppedRefreshed = Volatile.Read(ref onRefreshedCalls);
        var stoppedRefreshCalls = Volatile.Read(ref refreshCalls);

        Thread.Sleep(200);

        Assert.Equal(stoppedRefreshed, Volatile.Read(ref onRefreshedCalls));
        Assert.Equal(stoppedRefreshCalls, Volatile.Read(ref refreshCalls));
    }
}
