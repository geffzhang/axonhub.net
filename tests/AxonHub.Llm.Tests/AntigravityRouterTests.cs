using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/antigravity/router_test.go: quota preference
/// classification, endpoint selection, model name normalization, and retry
/// classification.
/// </summary>
public sealed class AntigravityRouterTests
{
    // ------------------------------------------------------------------
    // DetermineQuotaPreference (reference TestDetermineQuotaPreference)
    // ------------------------------------------------------------------

    [Theory]
    // Explicit suffix tests.
    [InlineData("gemini-2.5-pro:antigravity", AntigravityRouter.QuotaAntigravity)]
    [InlineData("claude-sonnet-4-5:gemini-cli", AntigravityRouter.QuotaGeminiCLI)]
    [InlineData("gemini-2.5-flash:antigravity", AntigravityRouter.QuotaAntigravity)]
    // Explicit prefix tests.
    [InlineData("antigravity-gemini-2.5-flash", AntigravityRouter.QuotaAntigravity)]
    [InlineData("antigravity-claude-sonnet-4-5", AntigravityRouter.QuotaAntigravity)]
    // Claude models (always antigravity).
    [InlineData("claude-sonnet-4-5", AntigravityRouter.QuotaAntigravity)]
    [InlineData("claude-sonnet-4-5-thinking", AntigravityRouter.QuotaAntigravity)]
    [InlineData("claude-opus-4-5-thinking", AntigravityRouter.QuotaAntigravity)]
    [InlineData("Claude-Sonnet-4-5", AntigravityRouter.QuotaAntigravity)]
    // GPT models (antigravity).
    [InlineData("gpt-oss-120b-medium", AntigravityRouter.QuotaAntigravity)]
    [InlineData("gpt-4", AntigravityRouter.QuotaAntigravity)]
    // Image models (antigravity).
    [InlineData("gemini-3-pro-image", AntigravityRouter.QuotaAntigravity)]
    [InlineData("imagen-3", AntigravityRouter.QuotaAntigravity)]
    // Legacy Gemini 3 models (antigravity).
    [InlineData("gemini-3-pro-low", AntigravityRouter.QuotaAntigravity)]
    [InlineData("gemini-3-pro-high", AntigravityRouter.QuotaAntigravity)]
    [InlineData("gemini-3-pro-medium", AntigravityRouter.QuotaAntigravity)]
    [InlineData("gemini-3-flash", AntigravityRouter.QuotaAntigravity)]
    [InlineData("gemini-3-flash-low", AntigravityRouter.QuotaAntigravity)]
    // Standard Gemini models (gemini-cli).
    [InlineData("gemini-2.5-pro", AntigravityRouter.QuotaGeminiCLI)]
    [InlineData("gemini-2.5-flash", AntigravityRouter.QuotaGeminiCLI)]
    [InlineData("gemini-2.5-flash-lite", AntigravityRouter.QuotaGeminiCLI)]
    [InlineData("gemini-1.5-pro", AntigravityRouter.QuotaGeminiCLI)]
    [InlineData("gemini-3-pro-preview", AntigravityRouter.QuotaGeminiCLI)]
    [InlineData("gemini-3-flash-preview", AntigravityRouter.QuotaGeminiCLI)]
    public void TestDetermineQuotaPreference(string model, string expected)
        => Assert.Equal(expected, AntigravityRouter.DetermineQuotaPreference(model));

    // ------------------------------------------------------------------
    // GetInitialEndpoint (reference TestGetInitialEndpoint)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(AntigravityRouter.QuotaAntigravity, AntigravityConstants.EndpointDaily)]
    [InlineData(AntigravityRouter.QuotaGeminiCLI, AntigravityConstants.EndpointDaily)]
    public void TestGetInitialEndpoint(string preference, string expected)
        => Assert.Equal(expected, AntigravityRouter.GetInitialEndpoint(preference));

    // ------------------------------------------------------------------
    // GetFallbackEndpoints (reference TestGetFallbackEndpoints)
    // ------------------------------------------------------------------

    [Fact]
    public void TestGetFallbackEndpoints()
        => Assert.Equal(
            [AntigravityConstants.EndpointDaily, AntigravityConstants.EndpointAutopush, AntigravityConstants.EndpointProd],
            AntigravityRouter.GetFallbackEndpoints());

    // ------------------------------------------------------------------
    // StripModelSuffix (reference TestStripModelSuffix)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("gemini-2.5-pro:antigravity", "gemini-2.5-pro")]
    [InlineData("claude-sonnet-4-5:gemini-cli", "claude-sonnet-4-5")]
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro")]
    public void TestStripModelSuffix(string model, string expected)
        => Assert.Equal(expected, AntigravityRouter.StripModelSuffix(model));

    // ------------------------------------------------------------------
    // StripAntigravityPrefix (reference TestStripAntigravityPrefix)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("antigravity-gemini-2.5-pro", "gemini-2.5-pro")]
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro")]
    [InlineData("Antigravity-gemini-2.5-pro", "Antigravity-gemini-2.5-pro")]
    public void TestStripAntigravityPrefix(string model, string expected)
        => Assert.Equal(expected, AntigravityRouter.StripAntigravityPrefix(model));

    // ------------------------------------------------------------------
    // NormalizeModelName (reference TestNormalizeModelName)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("antigravity-gemini-2.5-pro:gemini-cli", "gemini-2.5-pro")]
    [InlineData("gemini-2.5-pro:antigravity", "gemini-2.5-pro")]
    [InlineData("antigravity-claude-sonnet-4-5", "claude-sonnet-4-5")]
    [InlineData("gemini-2.5-pro", "gemini-2.5-pro")]
    public void TestNormalizeModelName(string model, string expected)
        => Assert.Equal(expected, AntigravityRouter.NormalizeModelName(model));

    // ------------------------------------------------------------------
    // ShouldRetryWithDifferentEndpoint (reference TestShouldRetryWithDifferentEndpoint)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(429, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(200, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    public void TestShouldRetryWithDifferentEndpoint(int statusCode, bool expected)
        => Assert.Equal(expected, AntigravityRouter.ShouldRetryWithDifferentEndpoint(statusCode));
}
