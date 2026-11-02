using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using Microsoft.AspNetCore.StaticFiles;

namespace AxonHub.Api.Static;

/// <summary>
/// Port of <c>internal/server/static/embed.go</c>: the gin <c>NoRoute</c>
/// handler installed by routes.go:59.
/// </summary>
/// <remarks>
/// <para>
/// The reference embeds the built frontend (<c>//go:embed all:dist/*</c>) and
/// serves it from memory. The port serves the same directory tree from
/// <c>Server:Static:Root</c> because the copied frontend is not built into this
/// repository (difference C129); the routing rules below are unchanged.
/// </para>
/// <para>
/// Three outcomes, in order:
/// </para>
/// <list type="number">
/// <item>An API prefix (<c>/admin</c>, <c>/anthropic</c>, <c>/doubao</c>,
/// <c>/gemini</c>, <c>/jina</c>, <c>/openapi</c>, <c>/v1</c>, <c>/v1beta</c>)
/// gets the reference's JSON 404 envelope.</item>
/// <item>A static-asset path (<c>/assets/*</c>, <c>/images/*</c>,
/// <c>/favicon.ico</c> or a known suffix) is served from disk; a missing file
/// stays a 404 and never falls back to the SPA.</item>
/// <item>Everything else is an SPA route and receives <c>index.html</c> with
/// the reference's no-cache headers.</item>
/// </list>
/// </remarks>
public sealed class StaticHandler
{
    private static readonly string[] ApiPrefixes =
    [
        "/admin",
        "/anthropic",
        "/doubao",
        "/gemini",
        "/jina",
        "/openapi",
        "/v1",
        "/v1beta",
    ];

    private static readonly string[] AssetPrefixes =
    [
        "/assets/",
        "/images/",
    ];

    private static readonly string[] AssetSuffixes =
    [
        ".js",
        ".css",
        ".png",
        ".jpg",
        ".jpeg",
        ".gif",
        ".svg",
        ".webp",
    ];

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly string _rootPath;

    public StaticHandler(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        _rootPath = System.IO.Path.GetFullPath(rootPath);
    }

    /// <summary>Port of <c>static.Handler()</c>: the gin fallback for unmatched routes.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var path = context.Request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        if (IsApiPath(path))
        {
            await WriteApiNotFoundAsync(context, path).ConfigureAwait(false);
            return;
        }

        if (IsStaticAssetPath(path))
        {
            await ServeAssetAsync(context, path).ConfigureAwait(false);
            return;
        }

        await ServeSpaIndexAsync(context).ConfigureAwait(false);
    }

    /// <summary>Port of <c>isAPIPath</c> / <c>hasPathPrefix</c>.</summary>
    private static bool IsApiPath(string path)
    {
        foreach (var prefix in ApiPrefixes)
        {
            if (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of <c>isStaticAssetPath</c> (case-sensitive, as in Go).</summary>
    private static bool IsStaticAssetPath(string path)
    {
        foreach (var prefix in AssetPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (path == "/favicon.ico")
        {
            return true;
        }

        foreach (var suffix in AssetSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of <c>serveAPINotFound</c> (gin AbortWithStatusJSON).</summary>
    private static async Task WriteApiNotFoundAsync(HttpContext context, string path)
    {
        var body = GoJson.Marshal(new ErrorResponse(
            new ErrorBody("Not Found", "path not found: " + path)));

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>static.Serve("/", staticFS)</c>: <c>http.FileServer</c>
    /// serves an existing file and answers a missing one with
    /// <c>http.NotFound</c>.
    /// </summary>
    private async Task ServeAssetAsync(HttpContext context, string path)
    {
        var fullPath = ResolveWithinRoot(path);
        if (fullPath is null || !File.Exists(fullPath))
        {
            await WritePlainNotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        var contentType = ContentTypes.TryGetContentType(path, out var mapped)
            ? mapped
            : "application/octet-stream";

        // Go's mime.TypeByExtension lists text types with "charset=utf-8"
        // (e.g. "text/javascript; charset=utf-8"); the .NET provider omits it.
        if (contentType.StartsWith("text/", StringComparison.Ordinal)
            && !contentType.Contains("charset=", StringComparison.OrdinalIgnoreCase))
        {
            contentType += "; charset=utf-8";
        }

        await WriteFileAsync(context, fullPath, contentType).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>serveSPAIndex</c>: <c>c.FileFromFS("/", staticFS)</c> opens
    /// the root, finds <c>index.html</c> and serves it with the reference's
    /// no-cache headers.
    /// </summary>
    private async Task ServeSpaIndexAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";

        var indexPath = ResolveWithinRoot("/index.html");
        if (indexPath is null || !File.Exists(indexPath))
        {
            // http.FileServer would list the directory instead; the port
            // treats a missing index as an empty deployment (C129).
            await WritePlainNotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        await WriteFileAsync(context, indexPath, "text/html; charset=utf-8")
            .ConfigureAwait(false);
    }

    private async Task WriteFileAsync(
        HttpContext context,
        string fullPath,
        string contentType)
    {
        var info = new FileInfo(fullPath);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = info.Length;

        // http.ServeContent answers HEAD with the headers only.
        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>http.NotFound writes plain text and forbids MIME sniffing.</summary>
    private static async Task WritePlainNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await context.Response.WriteAsync("404 page not found\n", context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Maps the request path to a file under the configured root, rejecting
    /// paths that would escape it. The reference serves an embedded FS, where
    /// traversal is impossible.
    /// </summary>
    private string? ResolveWithinRoot(string path)
    {
        var relative = path.TrimStart('/');
        var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_rootPath, relative));

        if (fullPath == _rootPath)
        {
            return null;
        }

        var rootPrefix = _rootPath.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + System.IO.Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootPrefix, StringComparison.Ordinal)
            ? fullPath
            : null;
    }

    /// <summary>Port of <c>objects.ErrorResponse</c> (internal/objects/response.go:3-10).</summary>
    private sealed record ErrorResponse(
        [property: JsonPropertyName("error")] ErrorBody Error);

    private sealed record ErrorBody(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("message")] string Message);
}

/// <summary>
/// Marks the catch-all fallback endpoint. The reference attaches its API key
/// and IP blocklist middlewares to gin route groups, so they never run when a
/// request misses every route and reaches <c>NoRoute</c>; the port mounts the
/// same middlewares by path prefix, and the marker lets those branches skip
/// the fallback (C116 residual).
/// </summary>
internal sealed class StaticFallbackMetadata
{
    private StaticFallbackMetadata()
    {
    }

    public static StaticFallbackMetadata Instance { get; } = new();
}
