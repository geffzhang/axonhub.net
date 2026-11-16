namespace AxonHub.Llm.Transformer.Shared;

/// <summary>
/// Port of the image subset of Go's <c>http.DetectContentType</c>
/// (net/http/sniff.go).
/// </summary>
/// <remarks>
/// Every non-image result is collapsed to <c>application/octet-stream</c>.
/// Both callers (Responses image conversion and the OpenAI images transformer)
/// only distinguish the four allowed image types from everything else, so the
/// collapsed identities are unobservable — Go's <c>text/plain; charset=utf-8</c>
/// and <c>application/octet-stream</c> land in the same branch.
/// </remarks>
public static class GoHttpSniff
{
    /// <summary>PNG magic; a byte array because u8 literals encode \x89 as UTF-8.</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>JPEG magic; a byte array because u8 literals encode \xFF as UTF-8.</summary>
    private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];

    /// <summary>Port of Go's <c>http.DetectContentType</c> image signatures.</summary>
    public static string DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith("\x00\x00\x01\x00"u8) || data.StartsWith("\x00\x00\x02\x00"u8))
        {
            return "image/x-icon";
        }

        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        // Go's webp signature masks the RIFF size and requires "WEBPVP".
        if (data.Length >= 14
            && data[..4].SequenceEqual("RIFF"u8)
            && data.Slice(8, 6).SequenceEqual("WEBPVP"u8))
        {
            return "image/webp";
        }

        if (data.StartsWith(PngSignature))
        {
            return "image/png";
        }

        if (data.StartsWith(JpegSignature))
        {
            return "image/jpeg";
        }

        return "application/octet-stream";
    }
}
