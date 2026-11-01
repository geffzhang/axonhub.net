using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>
/// Port of the MIME helpers (llm/transformer/gemini/image_mime.go and the
/// predicate helpers in convert.go).
/// </summary>
public static class GeminiMime
{
    /// <summary>
    /// Port of mediaMIMEType (image_mime.go:7-26): the explicit MIME type wins
    /// when the predicate accepts it; otherwise the URL path extension is
    /// looked up in Go's built-in <c>mime</c> table (charset parameter
    /// stripped) and accepted by the same predicate.
    /// </summary>
    public static string MediaMimeType(string? explicitMimeType, string rawUrl, Func<string, bool> accepts)
    {
        if (explicitMimeType is { Length: > 0 } && accepts(explicitMimeType))
        {
            return explicitMimeType;
        }

        var path = GoUrlPath(rawUrl);
        if (path is null)
        {
            // Go: url.Parse error.
            return "";
        }

        var extension = GoPathExt(path).ToLowerInvariant();
        var mediaType = GoMimeTypes.TypeByExtension(extension);

        // Go: strings.Cut(mediaType, ";").
        var semi = mediaType.IndexOf(';');
        if (semi >= 0)
        {
            mediaType = mediaType[..semi];
        }

        return accepts(mediaType) ? mediaType : "";
    }

    /// <summary>
    /// Port of imageMIMEType (image_mime.go:28-32). Note the reference's image
    /// predicate does NOT lowercase, unlike the shared predicates below.
    /// </summary>
    public static string ImageMimeType(ImageUrl image)
        => MediaMimeType(image.MimeType, image.Url, static mediaType => mediaType.StartsWith("image/", StringComparison.Ordinal));

    /// <summary>Port of isDocumentMIMEType (convert.go:245-258).</summary>
    public static bool IsDocumentMimeType(string mimeType)
    {
        if (mimeType.Length == 0)
        {
            return false;
        }

        mimeType = mimeType.ToLowerInvariant();
        return mimeType.StartsWith("application/pdf", StringComparison.Ordinal)
            || mimeType.StartsWith("application/msword", StringComparison.Ordinal)
            || mimeType.StartsWith("application/vnd.openxmlformats-officedocument", StringComparison.Ordinal)
            || mimeType.StartsWith("application/vnd.ms-", StringComparison.Ordinal)
            || mimeType.StartsWith("text/", StringComparison.Ordinal);
    }

    /// <summary>Port of isVideoMIMEType (convert.go:260-266).</summary>
    public static bool IsVideoMimeType(string mimeType)
    {
        if (mimeType.Length == 0)
        {
            return false;
        }

        return mimeType.ToLowerInvariant().StartsWith("video/", StringComparison.Ordinal);
    }

    /// <summary>Port of isAudioMIMEType (convert.go:268-274).</summary>
    public static bool IsAudioMimeType(string mimeType)
    {
        if (mimeType.Length == 0)
        {
            return false;
        }

        return mimeType.ToLowerInvariant().StartsWith("audio/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>Path</c> component the way Go's <c>url.Parse</c> yields it for the
    /// URLs the Gemini transformer observes: everything from the first "/"
    /// after an optional <c>scheme://</c> authority up to "?" or "#", or the
    /// whole reference when it carries no scheme; null where Go errors.
    /// </summary>
    private static string? GoUrlPath(string rawUrl)
    {
        // Go: a reference with a scheme keeps only the component after the
        // authority; one without a scheme is its own path.
        var pathStart = 0;

        var schemeEnd = rawUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            pathStart = rawUrl.IndexOf('/', schemeEnd + 3);
            if (pathStart < 0)
            {
                return "";
            }
        }

        var pathEnd = rawUrl.Length;
        for (var i = pathStart; i < rawUrl.Length; i++)
        {
            if (rawUrl[i] is '?' or '#')
            {
                pathEnd = i;
                break;
            }
        }

        return rawUrl[pathStart..pathEnd];
    }

    /// <summary>
    /// Port of <c>path.Ext</c>: the suffix beginning at the final dot in the
    /// final slash-separated element; empty when there is no dot.
    /// </summary>
    private static string GoPathExt(string path)
    {
        for (var i = path.Length - 1; i >= 0 && path[i] != '/'; i--)
        {
            if (path[i] == '.')
            {
                return path[i..];
            }
        }

        return "";
    }
}
