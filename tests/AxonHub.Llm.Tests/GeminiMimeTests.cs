using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the MIME inference helpers
/// (llm/transformer/gemini/filedata_mimetype_test.go, the pure-function part).
/// </summary>
public sealed class GeminiMimeTests
{
    [Theory]
    [InlineData("extensionless", "https://assets.example.com/images/input")]
    [InlineData("unknown extension", "https://assets.example.com/images/input.axonhubtest")]
    [InlineData("document extension", "https://assets.example.com/images/input.pdf")]
    [InlineData("video extension", "https://assets.example.com/images/input.mp4")]
    public void ImageMimeType_LeavesUnknownUrlTypesEmpty(string _, string url)
    {
        var image = new ImageUrl { Url = url };

        Assert.Equal("", GeminiMime.ImageMimeType(image));
    }

    [Theory]
    [InlineData("explicit MIME takes precedence", "video/quicktime",
        "https://assets.example.com/video/input.mp4", "video", "video/quicktime")]
    [InlineData("video from URL", "",
        "https://assets.example.com/video/input.mp4?token=test", "video", "video/mp4")]
    [InlineData("Word document from URL", "",
        "https://assets.example.com/documents/input.docx?token=test", "document",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("Excel document from URL", "",
        "https://assets.example.com/documents/input.xlsx?token=test", "document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("PowerPoint document from URL", "",
        "https://assets.example.com/documents/input.pptx?token=test", "document",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("audio from URL", "",
        "https://assets.example.com/audio/input.mp3?token=test", "audio", "audio/mpeg")]
    [InlineData("modality mismatch", "",
        "https://assets.example.com/documents/input.pdf", "video", "")]
    public void MediaMimeType_MatchesReference(
        string _,
        string explicitMime,
        string url,
        string modality,
        string expected)
    {
        Func<string, bool> accepts = modality switch
        {
            "video" => GeminiMime.IsVideoMimeType,
            "audio" => GeminiMime.IsAudioMimeType,
            _ => GeminiMime.IsDocumentMimeType,
        };

        Assert.Equal(expected, GeminiMime.MediaMimeType(explicitMime, url, accepts));
    }
}
