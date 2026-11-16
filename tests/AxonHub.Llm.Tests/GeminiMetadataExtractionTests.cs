using System.Text.Json;
using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the TransformerMetadata extraction helpers
/// (llm/transformer/gemini/outbound_convert_test.go:3451
/// TestExtractSafetySettingsFromMetadata and inbound_convert_test.go:2726
/// TestExtractImageConfigFromMetadata).
/// </summary>
public sealed class GeminiMetadataExtractionTests
{
    [Fact]
    public void ExtractSafetySettingsFromMetadata_MultipleSettings_ReturnsThemInOrder()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GeminiConvert.TransformerMetadataKeySafetySettings] = JsonSerializer.SerializeToElement(
                new[]
                {
                    new SafetySetting
                    {
                        Category = "HARM_CATEGORY_HARASSMENT",
                        Threshold = "BLOCK_LOW_AND_ABOVE",
                    },
                    new SafetySetting
                    {
                        Category = "HARM_CATEGORY_HATE_SPEECH",
                        Threshold = "BLOCK_MEDIUM_AND_ABOVE",
                    },
                }),
        };

        var result = GeminiConvert.ExtractSafetySettingsFromMetadata(metadata);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal("HARM_CATEGORY_HARASSMENT", result[0].Category);
        Assert.Equal("BLOCK_LOW_AND_ABOVE", result[0].Threshold);
        Assert.Equal("HARM_CATEGORY_HATE_SPEECH", result[1].Category);
        Assert.Equal("BLOCK_MEDIUM_AND_ABOVE", result[1].Threshold);
    }

    [Fact]
    public void ExtractSafetySettingsFromMetadata_NilMetadata_ReturnsNull()
    {
        Assert.Null(GeminiConvert.ExtractSafetySettingsFromMetadata(null));
    }

    [Fact]
    public void ExtractSafetySettingsFromMetadata_EmptySettings_ReturnsNull()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GeminiConvert.TransformerMetadataKeySafetySettings] = JsonSerializer.SerializeToElement(
                Array.Empty<SafetySetting>()),
        };

        Assert.Null(GeminiConvert.ExtractSafetySettingsFromMetadata(metadata));
    }

    [Fact]
    public void ExtractSafetySettingsFromMetadata_SingleSetting_ReturnsIt()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GeminiConvert.TransformerMetadataKeySafetySettings] = JsonSerializer.SerializeToElement(
                new[]
                {
                    new SafetySetting
                    {
                        Category = "HARM_CATEGORY_DANGEROUS_CONTENT",
                        Threshold = "BLOCK_NONE",
                    },
                }),
        };

        var result = GeminiConvert.ExtractSafetySettingsFromMetadata(metadata);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("HARM_CATEGORY_DANGEROUS_CONTENT", result[0].Category);
        Assert.Equal("BLOCK_NONE", result[0].Threshold);
    }

    [Fact]
    public void ExtractImageConfigFromMetadata_PopulatedConfig_ReturnsIt()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GeminiConvert.TransformerMetadataKeyImageConfig] = JsonSerializer.SerializeToElement(
                new ImageConfig { AspectRatio = "16:9", ImageSize = "4K" }),
        };

        var result = GeminiConvert.ExtractImageConfigFromMetadata(metadata);

        Assert.NotNull(result);
        Assert.Equal("16:9", result.AspectRatio);
        Assert.Equal("4K", result.ImageSize);
    }

    [Fact]
    public void ExtractImageConfigFromMetadata_NilMetadata_ReturnsNull()
    {
        Assert.Null(GeminiConvert.ExtractImageConfigFromMetadata(null));
    }

    [Fact]
    public void ExtractImageConfigFromMetadata_EmptyMetadata_ReturnsNull()
    {
        Assert.Null(GeminiConvert.ExtractImageConfigFromMetadata(new Dictionary<string, JsonElement>()));
    }

    [Fact]
    public void ExtractImageConfigFromMetadata_WrongType_ReturnsNull()
    {
        var metadata = new Dictionary<string, JsonElement>
        {
            [GeminiConvert.TransformerMetadataKeyImageConfig] = JsonSerializer.SerializeToElement("invalid"),
        };

        Assert.Null(GeminiConvert.ExtractImageConfigFromMetadata(metadata));
    }
}
