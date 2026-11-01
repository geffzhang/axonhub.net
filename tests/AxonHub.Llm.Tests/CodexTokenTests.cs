using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Llm.Tests;

/// <summary>Port of llm/transformer/openai/codex/token_test.go.</summary>
public sealed class CodexTokenTests
{
    [Fact]
    public void TestDecodeAuthJSON()
    {
        var creds = CodexToken.DecodeAuthJson(
            """
            {
                "auth_mode":"chatgpt",
                "last_refresh":"2026-04-17T08:58:36.389Z",
                "tokens":{
                    "access_token":"access",
                    "refresh_token":"refresh",
                    "id_token":"id"
                }
            }
            """);

        Assert.Equal(CodexConstants.ClientId, creds.ClientId);
        Assert.Equal("access", creds.AccessToken);
        Assert.Equal("refresh", creds.RefreshToken);
        Assert.Equal("id", creds.IdToken);
        Assert.Equal("bearer", creds.TokenType);
        Assert.Equal(["openid", "profile", "email", "offline_access"], creds.Scopes);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 17, 9, 58, 36, 389, TimeSpan.Zero),
            creds.ExpiresAt.ToUniversalTime());
    }
}
