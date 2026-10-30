namespace AxonHub.Llm.Transformer.Shared;

/// <summary>Port of xurl.DataURL (llm/internal/pkg/xurl/dataurl.go).</summary>
public sealed class DataUrl
{
    /// <summary>The MIME type, e.g. <c>image/png</c>; defaults to <c>text/plain</c>.</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>The base64-encoded or raw data portion.</summary>
    public string Data { get; set; } = string.Empty;

    /// <summary>Whether the header carries the <c>base64</c> parameter.</summary>
    public bool IsBase64 { get; set; }
}

/// <summary>
/// Port of llm/internal/pkg/xurl/dataurl.go: data URL parsing and construction.
/// </summary>
public static class Xurl
{
    /// <summary>
    /// Port of xurl.ParseDataURL. Returns null when the input does not start
    /// with <c>data:</c> or has no comma.
    /// </summary>
    public static DataUrl? ParseDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var comma = url.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var header = url[..comma];
        var data = url[(comma + 1)..];

        // Go: strings.Split(header, ";") always yields at least one part.
        var headerParts = header.Split(';');

        var mediaType = headerParts[0]["data:".Length..];
        if (mediaType.Length == 0)
        {
            mediaType = "text/plain"; // Default media type per RFC 2397.
        }

        var isBase64 = false;
        for (var i = 1; i < headerParts.Length; i++)
        {
            if (headerParts[i].Trim() == "base64")
            {
                isBase64 = true;
                break;
            }
        }

        return new DataUrl
        {
            MediaType = mediaType,
            Data = data,
            IsBase64 = isBase64,
        };
    }

    /// <summary>Port of xurl.IsDataURL.</summary>
    public static bool IsDataUrl(string url) => url.StartsWith("data:", StringComparison.Ordinal);

    /// <summary>
    /// Port of xurl.ExtractBase64FromDataURL: everything after the first comma
    /// for data URLs, otherwise the input unchanged.
    /// </summary>
    public static string ExtractBase64FromDataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal))
        {
            return url;
        }

        var comma = url.IndexOf(',');
        return comma >= 0 ? url[(comma + 1)..] : url;
    }

    /// <summary>Port of xurl.ExtractMediaTypeFromDataURL; empty when not a data URL.</summary>
    public static string ExtractMediaTypeFromDataUrl(string url) => ParseDataUrl(url)?.MediaType ?? string.Empty;

    /// <summary>Port of xurl.BuildDataURL.</summary>
    public static string BuildDataUrl(string mediaType, string data, bool isBase64)
    {
        if (mediaType.Length == 0)
        {
            mediaType = "text/plain";
        }

        return isBase64
            ? "data:" + mediaType + ";base64," + data
            : "data:" + mediaType + "," + data;
    }
}
