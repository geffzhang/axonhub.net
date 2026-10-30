using System.Text;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks Go's <c>SyntaxError</c> wording, including the character quoting
/// (<c>quoteChar</c> / <c>strconv.Quote</c>) applied to invalid bytes.
/// </summary>
public sealed class GoJsonSyntaxTests
{
    [Fact]
    public void AcceptsIllFormedUtf8InsideStrings()
    {
        // Go's checkValid is byte oriented: UTF-8 validity is not its concern.
        Assert.Null(GoJsonSyntax.Validate(Body("{\"model\":\"gpt-", [0xFF], "\"}")));
    }

    [Fact]
    public void QuotesInvalidHighBytesLikeGo()
    {
        Assert.Equal(
            "invalid character 'ÿ' looking for beginning of object key string",
            GoJsonSyntax.Validate(Body("{\"model\":\"x\",", [0xFF], "\"n\":1}")));

        // Latin-1 letters are printable for strconv; select ones are not.
        Assert.Equal(
            "invalid character '¡' looking for beginning of object key string",
            GoJsonSyntax.Validate(Body("{\"model\":\"x\",", [0xA1], "\"n\":1}")));

        Assert.Equal(
            "invalid character '\\u00ad' looking for beginning of object key string",
            GoJsonSyntax.Validate(Body("{\"model\":\"x\",", [0xAD], "\"n\":1}")));

        Assert.Equal(
            "invalid character '\\u0080' looking for beginning of object key string",
            GoJsonSyntax.Validate(Body("{\"model\":\"x\",", [0x80], "\"n\":1}")));

        Assert.Equal(
            "invalid character '\\x7f' looking for beginning of object key string",
            GoJsonSyntax.Validate(Body("{\"model\":\"x\",", [0x7F], "\"n\":1}")));
    }

    [Fact]
    public void QuotesBomBytesLikeGo()
    {
        Assert.Equal(
            "invalid character 'ï' looking for beginning of value",
            GoJsonSyntax.Validate([0xEF, 0xBB, 0xBF, .. Utf8("{\"model\":\"x\"}")]));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Body(string prefix, byte[] raw, string suffix)
        => [.. Utf8(prefix), .. raw, .. Utf8(suffix)];
}
