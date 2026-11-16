using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenCode;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of the header cases in internal/server/middleware/trace_test.go
/// (TestTryExtractTraceIDFromOpenCodeRequest) pinned to
/// llm/transformer/opencode/headers.go.
/// </summary>
public sealed class OpenCodeHeadersTests
{
    [Fact]
    public void TestGetSessionIdFromCurrentHeader()
    {
        Assert.Equal(
            "ses_current",
            GetSessionId((OpenCodeHeaders.SessionHeader, "ses_current")));
    }

    [Fact]
    public void TestGetSessionIdFromSessionIdHeader()
    {
        Assert.Equal(
            "ses_id",
            GetSessionId((OpenCodeHeaders.SessionIdHeader, "ses_id")));
    }

    [Fact]
    public void TestGetSessionIdFromAffinityHeader()
    {
        Assert.Equal(
            "ses_affinity",
            GetSessionId((OpenCodeHeaders.SessionAffinityHeader, "ses_affinity")));
    }

    [Fact]
    public void TestCurrentHeaderTakesPriority()
    {
        Assert.Equal(
            "ses_current",
            GetSessionId(
                (OpenCodeHeaders.SessionAffinityHeader, "ses_affinity"),
                (OpenCodeHeaders.SessionIdHeader, "ses_id"),
                (OpenCodeHeaders.SessionHeader, "ses_current")));
    }

    [Fact]
    public void TestMissingHeadersReturnEmpty()
    {
        Assert.Equal(string.Empty, OpenCodeHeaders.GetSessionIdFromHeaders(null));
        Assert.Equal(
            string.Empty,
            OpenCodeHeaders.GetSessionIdFromHeaders(new Dictionary<string, List<string>>()));
    }

    [Fact]
    public void TestBlankValueFallsThroughToNextHeader()
    {
        Assert.Equal(
            "ses_id",
            GetSessionId(
                (OpenCodeHeaders.SessionHeader, "   "),
                (OpenCodeHeaders.SessionIdHeader, "ses_id")));
    }

    [Fact]
    public void TestValuesAreTrimmed()
    {
        Assert.Equal(
            "ses_trimmed",
            GetSessionId((OpenCodeHeaders.SessionHeader, "  ses_trimmed  ")));
    }

    private static string GetSessionId(params (string Name, string Value)[] headers)
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, value) in headers)
        {
            HttpClientUtils.HeaderAdd(dict, name, value);
        }

        return OpenCodeHeaders.GetSessionIdFromHeaders(dict);
    }
}
