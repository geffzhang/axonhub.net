using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer.OpenAI.Copilot;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for llm/transformer/openai/copilot/token_provider.go: the
/// device-flow provider pinned to GitHub's endpoints exchanges the channel's
/// OAuth access token for a Copilot token.
/// </summary>
public sealed class CopilotTokenProviderTests
{
    private static HttpResponseData Json(string json)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(json),
        };

    [Fact]
    public void GetToken_ExchangesTheOAuthAccessToken()
    {
        HttpRequestData? captured = null;
        var executor = new MockExecutor
        {
            DoFn = (_, request) =>
            {
                captured = request;
                var expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
                return Json($$"""{"token":"copilot_token_ghu_access","expires_at":{{expiresAt}}}""");
            },
        };

        var provider = new CopilotTokenProvider(new CopilotTokenProviderParams
        {
            Credentials = new OAuthCredentials
            {
                AccessToken = "ghu_access",
                RefreshToken = "ghr_refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            },
            HttpClient = executor,
        });

        var token = provider.GetToken(CancellationToken.None);

        Assert.Equal("copilot_token_ghu_access", token);
        Assert.NotNull(captured);
        Assert.Equal("GET", captured!.Method);
        Assert.Equal(
            CopilotConstants.DefaultCopilotTokenEndpoint,
            captured.Url);
        Assert.Equal(
            "token ghu_access",
            HttpClientUtils.HeaderGet(captured.Headers, "Authorization"));
    }

    [Fact]
    public void GetToken_RequiresCredentials()
    {
        var provider = new CopilotTokenProvider(new CopilotTokenProviderParams
        {
            HttpClient = new MockExecutor(),
        });

        var error = Assert.Throws<InvalidOperationException>(
            () => provider.GetToken(CancellationToken.None));

        Assert.Contains("credentials is nil", error.Message);
    }

    [Fact]
    public void UpdateCredentials_ReplacesTheStoredCopy()
    {
        var provider = new CopilotTokenProvider(new CopilotTokenProviderParams
        {
            Credentials = new OAuthCredentials { AccessToken = "old" },
        });

        provider.UpdateCredentials(new OAuthCredentials { AccessToken = "new" });

        Assert.Equal("new", provider.GetCredentials()!.AccessToken);
    }
}
