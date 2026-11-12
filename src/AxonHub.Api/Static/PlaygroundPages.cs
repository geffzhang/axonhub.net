using System.Text;

namespace AxonHub.Api.Static;

/// <summary>
/// Port of the gqlgen playground handler mounted by the reference at
/// GET /admin/playground and GET /openapi/v1/playground
/// (internal/server/routes.go:157,164; gqlgen playground/playground.go:19-34).
/// </summary>
/// <remarks>
/// The pages are the gqlgen v0.17.86 template rendered with the AxonHub
/// configuration; the embedded copies were produced byte-for-byte by the same
/// template (tools/playground-render). The handler writes them with
/// <c>Content-Type: text/html; charset=UTF-8</c>, exactly like gqlgen.
/// </remarks>
public static class PlaygroundPages
{
    internal const string AdminResource = "AxonHub.Api.Static.Playground.admin.html";

    internal const string OpenApiResource = "AxonHub.Api.Static.Playground.openapi.html";

    /// <summary>gqlgen's playground content type (capital UTF-8).</summary>
    public const string ContentType = "text/html; charset=UTF-8";

    public static Task ServeAdminAsync(HttpContext context)
        => ServeAsync(context, AdminResource);

    public static Task ServeOpenApiAsync(HttpContext context)
        => ServeAsync(context, OpenApiResource);

    private static async Task ServeAsync(HttpContext context, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(context);

        var html = LoadHtml(resourceName);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = ContentType;
        await context.Response.Body
            .WriteAsync(Encoding.UTF8.GetBytes(html), context.RequestAborted)
            .ConfigureAwait(false);
    }

    internal static string LoadHtml(string resourceName)
    {
        var assembly = typeof(PlaygroundPages).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                "missing embedded playground resource " + resourceName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // git's autocrlf can rewrite the working tree copy; gqlgen renders LF.
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
