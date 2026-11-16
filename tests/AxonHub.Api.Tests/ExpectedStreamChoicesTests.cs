using System.Text;
using AxonHub.Api.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of orchestrator.ExpectedStreamChoices: gjson reads the raw request body
/// without validating UTF-8, so ill-formed bytes elsewhere in the document must
/// not hide <c>n</c> or <c>stream</c> (C008).
/// </summary>
public sealed class ExpectedStreamChoicesTests
{
    [Fact]
    public void ReadsNWithIllFormedUtf8Elsewhere()
    {
        Assert.Equal(2, ExpectedStreamChoices.FromRequestBody(
            Body("{\"n\":2,\"model\":\"gpt-", [0xFF], "\"}")));
    }

    [Fact]
    public void ReadsStreamFlagWithIllFormedUtf8Elsewhere()
    {
        Assert.Equal(1, ExpectedStreamChoices.FromRequestBody(
            Body("{\"model\":\"gpt-", [0xFF], "\",\"stream\":true}")));
    }

    private static byte[] Body(string prefix, byte[] raw, string suffix)
        => [.. Encoding.UTF8.GetBytes(prefix), .. raw, .. Encoding.UTF8.GetBytes(suffix)];
}
