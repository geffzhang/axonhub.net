using System.Text;
using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the credentials/token-response wire shape of llm/oauth/credentials.go
/// and exchange_strategy.go: Go's JSON tags, the <c>omitempty</c> rules and the
/// RFC3339 <c>expires_at</c> encoding.
/// </summary>
public sealed class OAuthCredentialsJsonTests
{
    [Fact]
    public void ParseCredentialsReadsGoTimeAndScopes()
    {
        var creds = OAuthCredentialsJson.ParseCredentialsJson(
            """
            {"client_id":"client-1","access_token":"access-1","refresh_token":"refresh-1","id_token":"id-1","expires_at":"2026-01-02T03:04:05.123Z","token_type":"Bearer","scopes":["openid","profile"],"unknown":"ignored"}
            """);

        Assert.Equal("client-1", creds.ClientId);
        Assert.Equal("access-1", creds.AccessToken);
        Assert.Equal("refresh-1", creds.RefreshToken);
        Assert.Equal("id-1", creds.IdToken);
        Assert.Equal("Bearer", creds.TokenType);
        Assert.Equal(["openid", "profile"], creds.Scopes);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, TimeSpan.Zero),
            creds.ExpiresAt);
    }

    [Fact]
    public void ParseCredentialsAssumesOneHourWhenRefreshTokenHasNoExpiry()
    {
        var before = DateTimeOffset.UtcNow;
        var creds = OAuthCredentialsJson.ParseCredentialsJson(
            """{"access_token":"access-1","refresh_token":"refresh-1"}""");
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(creds.ExpiresAt, before.AddHours(1), after.AddHours(1).AddSeconds(1));
    }

    [Fact]
    public void ParseCredentialsRejectsEmptyAndMissingAccessToken()
    {
        var empty = Assert.Throws<InvalidOperationException>(
            () => OAuthCredentialsJson.ParseCredentialsJson("   "));
        Assert.Equal("empty credentials", empty.Message);

        var missing = Assert.Throws<InvalidOperationException>(
            () => OAuthCredentialsJson.ParseCredentialsJson("""{"refresh_token":"refresh"}"""));
        Assert.Equal("access_token is empty", missing.Message);
    }

    [Fact]
    public void ParseCredentialsMatchesFieldNamesCaseInsensitively()
    {
        var creds = OAuthCredentialsJson.ParseCredentialsJson(
            """{"Access_Token":"access-1","REFRESH_TOKEN":"refresh-1","Expires_At":"2026-01-02T03:04:05Z"}""");

        Assert.Equal("access-1", creds.AccessToken);
        Assert.Equal("refresh-1", creds.RefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), creds.ExpiresAt);
    }

    [Fact]
    public void ToJsonOmitsEmptyOptionalFieldsInGoFieldOrder()
    {
        var creds = new OAuthCredentials
        {
            AccessToken = "access-1",
            ExpiresAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        };

        Assert.Equal(
            """{"access_token":"access-1","refresh_token":"","expires_at":"2026-01-02T03:04:05Z"}""",
            creds.ToJson());

        creds.ClientId = "client-1";
        creds.RefreshToken = "refresh-1";
        creds.IdToken = "id-1";
        creds.TokenType = "Bearer";
        creds.Scopes = ["openid"];

        Assert.Equal(
            """{"client_id":"client-1","access_token":"access-1","refresh_token":"refresh-1","id_token":"id-1","expires_at":"2026-01-02T03:04:05Z","token_type":"Bearer","scopes":["openid"]}""",
            creds.ToJson());
    }

    [Fact]
    public void ToJsonRoundTripsThroughParseCredentials()
    {
        var original = new OAuthCredentials
        {
            ClientId = "client-1",
            AccessToken = "access-1",
            RefreshToken = "refresh-1",
            IdToken = "id-1",
            ExpiresAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            TokenType = "Bearer",
            Scopes = ["openid", "profile"],
        };

        var parsed = OAuthCredentialsJson.ParseCredentialsJson(original.ToJson());

        Assert.Equal(original.ClientId, parsed.ClientId);
        Assert.Equal(original.AccessToken, parsed.AccessToken);
        Assert.Equal(original.RefreshToken, parsed.RefreshToken);
        Assert.Equal(original.IdToken, parsed.IdToken);
        Assert.Equal(original.ExpiresAt, parsed.ExpiresAt);
        Assert.Equal(original.TokenType, parsed.TokenType);
        Assert.Equal(original.Scopes, parsed.Scopes);
    }

    [Fact]
    public void ParseTokenResponseReportsProviderErrors()
    {
        var missing = Assert.Throws<InvalidOperationException>(
            () => OAuthCredentialsJson.ParseTokenResponse(
                Encoding.UTF8.GetBytes("""{"token_type":"Bearer"}"""),
                "client-1"));
        Assert.Equal("token response missing access_token", missing.Message);

        var providerError = Assert.Throws<InvalidOperationException>(
            () => OAuthCredentialsJson.ParseTokenResponse(
                Encoding.UTF8.GetBytes("""{"error":"invalid_grant","error_description":"bad code"}"""),
                "client-1"));
        Assert.Equal("token request failed: invalid_grant - bad code", providerError.Message);

        var malformed = Assert.Throws<InvalidOperationException>(
            () => OAuthCredentialsJson.ParseTokenResponse(
                Encoding.UTF8.GetBytes("{invalid"),
                "client-1"));
        Assert.StartsWith("decode response: ", malformed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseTokenResponseMapsFieldsAndExpiry()
    {
        var before = DateTimeOffset.UtcNow;
        var creds = OAuthCredentialsJson.ParseTokenResponse(
            Encoding.UTF8.GetBytes(
                """{"access_token":"access-1","refresh_token":"refresh-1","id_token":"id-1","token_type":"Bearer","scope":"openid  profile","expires_in":120}"""),
            "client-1");

        Assert.Equal("client-1", creds.ClientId);
        Assert.Equal("access-1", creds.AccessToken);
        Assert.Equal("refresh-1", creds.RefreshToken);
        Assert.Equal("id-1", creds.IdToken);
        Assert.Equal("Bearer", creds.TokenType);
        Assert.Equal(["openid", "profile"], creds.Scopes);
        Assert.InRange(creds.ExpiresAt, before.AddSeconds(120), DateTimeOffset.UtcNow.AddSeconds(120));

        var noExpiry = OAuthCredentialsJson.ParseTokenResponse(
            Encoding.UTF8.GetBytes("""{"access_token":"access-1"}"""),
            "client-1");
        Assert.Equal(DateTimeOffset.MinValue, noExpiry.ExpiresAt);
    }
}
