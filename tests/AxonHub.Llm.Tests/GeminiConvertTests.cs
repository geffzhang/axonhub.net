using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the shared Gemini conversion helpers
/// (llm/transformer/gemini/convert_test.go): document/audio part conversion,
/// MIME predicates, thinking level selection and usage round-trips.
/// </summary>
public sealed class GeminiConvertTests
{
    // ------------------------------------------------------------------
    // TestConvertDocumentURLToGeminiPart
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertDocumentUrlToGeminiPart_PdfDataUrl_UsesInlineData()
    {
        var doc = new DocumentUrl
        {
            Url = "data:application/pdf;base64,JVBERi0xLjQK",
            MimeType = "application/pdf",
        };

        var result = GeminiConvert.ConvertDocumentUrlToGeminiPart(doc);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineData);
        Assert.Equal("application/pdf", result.InlineData.MIMEType);
        Assert.Equal("JVBERi0xLjQK", result.InlineData.Data);
        Assert.Null(result.FileData);
    }

    [Fact]
    public void ConvertDocumentUrlToGeminiPart_PdfFileUrl_UsesFileData()
    {
        var doc = new DocumentUrl
        {
            Url = "https://example.com/document.pdf",
            MimeType = "application/pdf",
        };

        var result = GeminiConvert.ConvertDocumentUrlToGeminiPart(doc);

        Assert.NotNull(result);
        Assert.NotNull(result.FileData);
        Assert.Equal("https://example.com/document.pdf", result.FileData.FileURI);
        Assert.Equal("application/pdf", result.FileData.MIMEType);
        Assert.Null(result.InlineData);
    }

    [Fact]
    public void ConvertDocumentUrlToGeminiPart_WordDataUrl_UsesInlineData()
    {
        var doc = new DocumentUrl
        {
            Url = "data:application/msword;base64,0M8R4KGx",
            MimeType = "application/msword",
        };

        var result = GeminiConvert.ConvertDocumentUrlToGeminiPart(doc);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineData);
        Assert.Equal("application/msword", result.InlineData.MIMEType);
        Assert.Equal("0M8R4KGx", result.InlineData.Data);
    }

    [Fact]
    public void ConvertDocumentUrlToGeminiPart_PdfUrlWithoutMimeType_InfersFromExtension()
    {
        var doc = new DocumentUrl { Url = "https://example.com/report.pdf" };

        var result = GeminiConvert.ConvertDocumentUrlToGeminiPart(doc);

        Assert.NotNull(result);
        Assert.NotNull(result.FileData);
        Assert.Equal("https://example.com/report.pdf", result.FileData.FileURI);
        Assert.Equal("application/pdf", result.FileData.MIMEType);
    }

    [Fact]
    public void ConvertDocumentUrlToGeminiPart_NilDocument_ReturnsNull()
    {
        Assert.Null(GeminiConvert.ConvertDocumentUrlToGeminiPart(null));
    }

    [Fact]
    public void ConvertDocumentUrlToGeminiPart_EmptyUrl_ReturnsNull()
    {
        Assert.Null(GeminiConvert.ConvertDocumentUrlToGeminiPart(new DocumentUrl { Url = "" }));
    }

    // ------------------------------------------------------------------
    // TestConvertAudioToGeminiPart
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertAudioToGeminiPart_Mp3Audio_UsesInlineData()
    {
        var audio = new InputAudio { Format = "mp3", Data = "SUQzBAAAAAA=" };

        var result = GeminiConvert.ConvertAudioToGeminiPart(audio);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineData);
        Assert.Equal("audio/mp3", result.InlineData.MIMEType);
        Assert.Equal("SUQzBAAAAAA=", result.InlineData.Data);
    }

    [Fact]
    public void ConvertAudioToGeminiPart_WavAudio_UsesInlineData()
    {
        var audio = new InputAudio { Format = "wav", Data = "UklGRiQAAABXQVZF" };

        var result = GeminiConvert.ConvertAudioToGeminiPart(audio);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineData);
        Assert.Equal("audio/wav", result.InlineData.MIMEType);
        Assert.Equal("UklGRiQAAABXQVZF", result.InlineData.Data);
    }

    [Fact]
    public void ConvertAudioToGeminiPart_RemoteMp3Audio_UsesFileDataWithInferredMime()
    {
        var audio = new InputAudio
        {
            Url = "https://assets.example.com/audio/input.mp3?token=test",
        };

        var result = GeminiConvert.ConvertAudioToGeminiPart(audio);

        Assert.NotNull(result);
        Assert.NotNull(result.FileData);
        Assert.Equal("audio/mpeg", result.FileData.MIMEType);
        Assert.Equal("https://assets.example.com/audio/input.mp3?token=test", result.FileData.FileURI);
    }

    [Fact]
    public void ConvertAudioToGeminiPart_InlineAudioTakesPrecedenceOverUrl()
    {
        var audio = new InputAudio
        {
            Format = "wav",
            Data = "UklGRiQAAABXQVZF",
            Url = "https://assets.example.com/audio/input.mp3",
            MimeType = "audio/wav",
        };

        var result = GeminiConvert.ConvertAudioToGeminiPart(audio);

        Assert.NotNull(result);
        Assert.NotNull(result.InlineData);
        Assert.Equal("audio/wav", result.InlineData.MIMEType);
        Assert.Equal("UklGRiQAAABXQVZF", result.InlineData.Data);
        Assert.Null(result.FileData);
    }

    // ------------------------------------------------------------------
    // TestIsDocumentMIMEType
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("application/pdf", true)]
    [InlineData("application/msword", true)]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("application/vnd.ms-excel", true)]
    [InlineData("text/plain", true)]
    [InlineData("text/html", true)]
    [InlineData("image/png", false)]
    [InlineData("image/jpeg", false)]
    [InlineData("image/gif", false)]
    [InlineData("", false)]
    [InlineData("APPLICATION/PDF", true)]
    [InlineData("IMAGE/PNG", false)]
    public void IsDocumentMimeType_MatchesReference(string mimeType, bool expected)
    {
        Assert.Equal(expected, GeminiMime.IsDocumentMimeType(mimeType));
    }

    // ------------------------------------------------------------------
    // TestShouldUseThinkinLevelForBudget
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("gemini-3-5m", 1024)]
    [InlineData("gemini-3-5m", 5000)]
    [InlineData("gemini-3-5m", 20000)]
    [InlineData("gemini-3-5m", 32768)]
    [InlineData("gemini-3-5-pro", 1024)]
    [InlineData("gemini-3-pro", 5000)]
    [InlineData("gemini-3-5-flash", 20000)]
    public void ShouldUseThinkingLevelForBudget_Gemini3StandardBudgets_True(string model, long budget)
    {
        Assert.True(GeminiConvert.ShouldUseThinkingLevelForBudget(model, budget));
    }

    [Theory]
    [InlineData("gemini-2.5-flash", 1024)]
    [InlineData("gemini-2.0-flash", 5000)]
    [InlineData("gemini-1.5-pro", 20000)]
    [InlineData("gemini-pro", 32768)]
    public void ShouldUseThinkingLevelForBudget_NonGemini3_False(string model, long budget)
    {
        Assert.False(GeminiConvert.ShouldUseThinkingLevelForBudget(model, budget));
    }

    [Fact]
    public void ShouldUseThinkingLevelForBudget_VeryHighBudgetForGemini3_False()
    {
        Assert.False(GeminiConvert.ShouldUseThinkingLevelForBudget("gemini-3-5m", 50000));
    }

    [Fact]
    public void ShouldUseThinkingLevelForBudget_VeryLowBudgetForNonGemini3_False()
    {
        Assert.False(GeminiConvert.ShouldUseThinkingLevelForBudget("gemini-2.5-flash", 500));
    }

    // ------------------------------------------------------------------
    // TestConvertToLLMUsage_CacheHitCalculation
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToLlmUsage_HighCacheHitRateScenario_MapsCachedAndThoughtsTokens()
    {
        var geminiUsage = new UsageMetadata
        {
            PromptTokenCount = 20981,
            CandidatesTokenCount = 22,
            TotalTokenCount = 21097,
            CachedContentTokenCount = 20350,
            ThoughtsTokenCount = 94,
        };

        var result = GeminiConvert.ConvertToLlmUsage(geminiUsage);

        Assert.NotNull(result);
        // prompt_tokens includes cached tokens per OpenAI spec.
        Assert.Equal(20981, result.PromptTokens);
        Assert.Equal(116, result.CompletionTokens); // 22 + 94
        Assert.Equal(21097, result.TotalTokens);
        Assert.NotNull(result.PromptTokensDetails);
        Assert.Equal(20350, result.PromptTokensDetails.CachedTokens);
        Assert.NotNull(result.CompletionTokensDetails);
        Assert.Equal(94, result.CompletionTokensDetails.ReasoningTokens);
    }

    [Fact]
    public void ConvertToLlmUsage_NoCacheScenario_KeepsPlainCounts()
    {
        var geminiUsage = new UsageMetadata
        {
            PromptTokenCount = 1000,
            CandidatesTokenCount = 500,
            TotalTokenCount = 1500,
        };

        var result = GeminiConvert.ConvertToLlmUsage(geminiUsage);

        Assert.NotNull(result);
        Assert.Equal(1000, result.PromptTokens);
        Assert.Equal(500, result.CompletionTokens);
        Assert.Equal(1500, result.TotalTokens);
        Assert.Null(result.PromptTokensDetails);
    }

    [Fact]
    public void ConvertToLlmUsage_PartialCacheScenario_MapsCachedTokens()
    {
        var geminiUsage = new UsageMetadata
        {
            PromptTokenCount = 1000,
            CandidatesTokenCount = 100,
            TotalTokenCount = 1100,
            CachedContentTokenCount = 600,
        };

        var result = GeminiConvert.ConvertToLlmUsage(geminiUsage);

        Assert.NotNull(result);
        Assert.Equal(1000, result.PromptTokens);
        Assert.Equal(100, result.CompletionTokens);
        Assert.Equal(1100, result.TotalTokens);
        Assert.NotNull(result.PromptTokensDetails);
        Assert.Equal(600, result.PromptTokensDetails.CachedTokens);
    }

    // ------------------------------------------------------------------
    // TestConvertToGeminiUsage_BidirectionalConsistency
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertToGeminiUsage_WithCachedTokens_SplitsThoughtsFromCandidates()
    {
        var llmUsage = new Usage
        {
            PromptTokens = 20981,
            CompletionTokens = 116,
            TotalTokens = 21097,
            PromptTokensDetails = new PromptTokensDetails { CachedTokens = 20350 },
            CompletionTokensDetails = new CompletionTokensDetails { ReasoningTokens = 94 },
        };

        var geminiUsage = GeminiConvert.ConvertToGeminiUsage(llmUsage);

        Assert.NotNull(geminiUsage);
        Assert.Equal(20981, geminiUsage.PromptTokenCount);
        Assert.Equal(22, geminiUsage.CandidatesTokenCount); // 116 - 94
        Assert.Equal(21097, geminiUsage.TotalTokenCount);
        Assert.Equal(20350, geminiUsage.CachedContentTokenCount);
        Assert.Equal(94, geminiUsage.ThoughtsTokenCount);

        var roundtrip = GeminiConvert.ConvertToLlmUsage(geminiUsage);
        Assert.NotNull(roundtrip);
        Assert.Equal(llmUsage.PromptTokens, roundtrip.PromptTokens);
        Assert.Equal(llmUsage.CompletionTokens, roundtrip.CompletionTokens);
        Assert.Equal(llmUsage.TotalTokens, roundtrip.TotalTokens);
        Assert.NotNull(roundtrip.PromptTokensDetails);
        Assert.Equal(20350, roundtrip.PromptTokensDetails.CachedTokens);
    }

    [Fact]
    public void ConvertToGeminiUsage_WithoutCachedTokens_KeepsPlainCounts()
    {
        var llmUsage = new Usage
        {
            PromptTokens = 1000,
            CompletionTokens = 500,
            TotalTokens = 1500,
        };

        var geminiUsage = GeminiConvert.ConvertToGeminiUsage(llmUsage);

        Assert.NotNull(geminiUsage);
        Assert.Equal(1000, geminiUsage.PromptTokenCount);
        Assert.Equal(500, geminiUsage.CandidatesTokenCount);
        Assert.Equal(1500, geminiUsage.TotalTokenCount);
        Assert.Equal(0, geminiUsage.CachedContentTokenCount);
    }

    [Fact]
    public void ConvertToGeminiUsage_WithThoughtsTokens_SplitsCandidates()
    {
        var llmUsage = new Usage
        {
            PromptTokens = 1000,
            CompletionTokens = 500,
            TotalTokens = 1500,
            CompletionTokensDetails = new CompletionTokensDetails { ReasoningTokens = 100 },
        };

        var geminiUsage = GeminiConvert.ConvertToGeminiUsage(llmUsage);

        Assert.NotNull(geminiUsage);
        Assert.Equal(1000, geminiUsage.PromptTokenCount);
        Assert.Equal(400, geminiUsage.CandidatesTokenCount); // 500 - 100
        Assert.Equal(1500, geminiUsage.TotalTokenCount);
        Assert.Equal(100, geminiUsage.ThoughtsTokenCount);
    }
}
