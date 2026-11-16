using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for <c>NormalizeSSOToken</c>
/// (llm/transformer/xai/subscription/sso_test.go): a cookie string is mined for
/// the <c>sso</c>/<c>sso-rw</c> value, header controls are stripped, empty
/// supported cookies are skipped, and oversized tokens are rejected.
/// </summary>
public sealed class XaiSubscriptionSsoTests
{
    [Fact]
    public void NormalizeSSOToken_extracts_sso_cookie()
        => Assert.Equal(
            "synthetic-sso-token",
            SubscriptionSso.NormalizeSsoToken(
                "cookie: other=value; sso=synthetic-sso-token; theme=dark"));

    [Fact]
    public void NormalizeSSOToken_sanitizes_header_controls()
        => Assert.Equal(
            "syntheticInjected: value",
            SubscriptionSso.NormalizeSsoToken("sso=synthetic\r\nInjected: value"));

    [Fact]
    public void NormalizeSSOToken_rejects_oversized_token()
    {
        var input = new string('a', SubscriptionSso.MaxSsoTokenLength + 1);

        Assert.Empty(SubscriptionSso.NormalizeSsoToken(input));
    }

    [Fact]
    public void NormalizeSSOToken_skips_empty_cookie_and_uses_next()
        => Assert.Equal(
            "valid-token",
            SubscriptionSso.NormalizeSsoToken("sso=; sso-rw=valid-token"));

    [Fact]
    public void NormalizeSSOToken_empty_supported_cookies_yield_empty()
        => Assert.Empty(SubscriptionSso.NormalizeSsoToken("sso=; sso-rw="));

    // Port-added: Go's len() counts UTF-8 bytes, so a token under the character
    // limit can still exceed the byte limit.
    [Fact]
    public void NormalizeSSOToken_caps_utf8_byte_length_like_go()
    {
        var token = new string('a', SubscriptionSso.MaxSsoTokenLength / 2)
            + new string('汉', SubscriptionSso.MaxSsoTokenLength / 2);

        Assert.True(token.Length <= SubscriptionSso.MaxSsoTokenLength);
        Assert.Empty(SubscriptionSso.NormalizeSsoToken(token));
    }
}
