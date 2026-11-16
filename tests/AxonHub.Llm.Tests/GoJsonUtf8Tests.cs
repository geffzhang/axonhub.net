using System.Text;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the UTF-8 repair Go's <c>json.Unmarshal</c> applies inside string
/// literals. Expected values come from running the reference decoder on the
/// same byte sequences (C008).
/// </summary>
public sealed class GoJsonUtf8Tests
{
    [Fact]
    public void RepairsEachIllFormedByteWithOneReplacement()
    {
        // "gpt-\xff"
        Assert.Equal("gpt-�", DecodeModel(Body("{\"model\":\"gpt-", [0xFF], "\"}")));

        // "gpt-\xed\xa0\x80": a UTF-8-encoded surrogate, three bad bytes.
        Assert.Equal("gpt-���", DecodeModel(Body("{\"model\":\"gpt-", [0xED, 0xA0, 0x80], "\"}")));

        // "gpt-\xc0\xaf": an overlong encoding, two bad bytes.
        Assert.Equal("gpt-��", DecodeModel(Body("{\"model\":\"gpt-", [0xC0, 0xAF], "\"}")));

        // "gpt-\xe2\x82": a truncated sequence closed by the quote.
        Assert.Equal("gpt-��", DecodeModel(Body("{\"model\":\"gpt-", [0xE2, 0x82], "\"}")));

        // An encoded U+FFFD is already valid UTF-8 and is copied unchanged.
        Assert.Equal("gpt-�", DecodeModel(Body("{\"model\":\"gpt-", [0xEF, 0xBF, 0xBD], "\"}")));
    }

    [Fact]
    public void RepairsLoneSurrogateEscapesLikeGo()
    {
        Assert.Equal("gpt-�", DecodeModel(Utf8("{\"model\":\"gpt-\\ud800\"}")));
        Assert.Equal("gpt-�", DecodeModel(Utf8("{\"model\":\"gpt-\\udc00\"}")));
        Assert.Equal("gpt-��", DecodeModel(Utf8("{\"model\":\"gpt-\\udc00\\ud800\"}")));

        // A high surrogate followed by an ordinary escape: Go emits one
        // replacement and then decodes the escape on its own.
        Assert.Equal("gpt-�\n", DecodeModel(Utf8("{\"model\":\"gpt-\\ud800\\n\"}")));
        Assert.Equal("gpt-�A", DecodeModel(Utf8("{\"model\":\"gpt-\\ud800\\u0041\"}")));
    }

    [Fact]
    public void KeepsValidSurrogatePairs()
    {
        Assert.Equal("gpt-\U0001F600", DecodeModel(Utf8("{\"model\":\"gpt-\\ud83d\\ude00\"}")));
        Assert.Null(GoJsonUtf8.SanitizeStringLiterals(
            Utf8("{\"model\":\"gpt-4o\",\"text\":\"中文 \\ud83d\\ude00\"}")));
    }

    [Fact]
    public void IgnoresIllFormedBytesInPropertyNamesLikeGo()
    {
        // Go repairs the key to "mes�escages", which matches nothing.
        Assert.Equal("gpt-4o", DecodeModel(Body("{\"model\":\"gpt-4o\",\"mes", [0xFF], "sages\":[]}")));
    }

    [Fact]
    public void TracksEscapedQuotesWhileRepairing()
    {
        Assert.Equal(
            "a\"b�c",
            DecodeModel(Body("{\"model\":\"a\\\"b", [0xFF], "c\"}")));
    }

    [Fact]
    public void LeavesBytesOutsideStringsAlone()
    {
        // The repair only applies inside literals; structural errors keep the
        // original byte for the syntax scanner to quote (see GoJsonSyntaxTests).
        Assert.Null(GoJsonUtf8.SanitizeStringLiterals(Body("{\"model\":\"x\",", [0xFF], "\"n\":1}")));
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static byte[] Body(string prefix, byte[] raw, string suffix)
        => [.. Utf8(prefix), .. raw, .. Utf8(suffix)];

    private static string DecodeModel(byte[] body)
        => LlmJson.Deserialize<OpenAiRequest>(body)!.Model;
}
