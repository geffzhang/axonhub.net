namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of <c>resolveIconURL</c> (internal/server/biz/oidc.go:306-336): an
/// http(s) URL or data: URI passes through; anything else is read from disk
/// and inlined as a base64 data URL.
/// </summary>
/// <remarks>
/// The MIME lookup reproduces the mime package's builtin table and a subset of
/// <c>http.DetectContentType</c> (the image/icon signatures). Go also consults
/// the platform MIME database before sniffing; that platform lookup is not
/// reproduced (C169).
/// </remarks>
public static class OidcIconResolver
{
    // mime package's builtin table (mime/type.go, Go 1.26).
    private static readonly Dictionary<string, string> BuiltinTypes = new(StringComparer.Ordinal)
    {
        [".avif"] = "image/avif",
        [".css"] = "text/css; charset=utf-8",
        [".gif"] = "image/gif",
        [".htm"] = "text/html; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg",
        [".js"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json",
        [".mjs"] = "text/javascript; charset=utf-8",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".svg"] = "image/svg+xml",
        [".wasm"] = "application/wasm",
        [".webp"] = "image/webp",
        [".xml"] = "text/xml; charset=utf-8",
    };

    /// <summary>Port of <c>resolveIconURL</c>; throws when a local file cannot be read.</summary>
    public static string Resolve(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        if (raw.Length == 0)
        {
            return string.Empty;
        }

        if (raw.StartsWith("http://", StringComparison.Ordinal)
            || raw.StartsWith("https://", StringComparison.Ordinal)
            || raw.StartsWith("data:", StringComparison.Ordinal))
        {
            return raw;
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"reading icon file \"{raw}\": {ex.Message}",
                ex);
        }

        var extension = System.IO.Path.GetExtension(raw).ToLowerInvariant();
        var mimeType = BuiltinTypes.TryGetValue(extension, out var mapped)
            ? mapped
            : DetectContentType(data);

        return "data:" + mimeType + ";base64," + Convert.ToBase64String(data);
    }

    /// <summary>
    /// Subset of <c>http.DetectContentType</c>: the WHATWG image signatures an
    /// icon file realistically uses, then the binary fallback. Text sniffing
    /// (HTML/XML/UTF BOM) is not reproduced (C169).
    /// </summary>
    private static string DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        if (data.Length >= 12
            && data.StartsWith("RIFF"u8)
            && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
        {
            return "image/x-icon";
        }

        if (data.Length >= 4 && data.StartsWith("<svg"u8))
        {
            return "image/svg+xml";
        }

        return "application/octet-stream";
    }
}
