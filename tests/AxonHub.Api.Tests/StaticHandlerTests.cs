using System.Net;
using System.Net.Http;
using System.Text;
using AxonHub.Api.Static;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the gin NoRoute handler (internal/server/static/embed.go and its
/// embed_test.go): API prefixes answer with the JSON 404 envelope, static
/// assets are served without ever falling back to the SPA, and every other
/// path gets index.html with the reference's no-cache headers.
/// </summary>
public sealed class StaticHandlerTests : IAsyncLifetime
{
    private const string IndexHtml = "<!doctype html><html><body>axonhub</body></html>";
    private const string AppJs = "console.log('axonhub');";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "axonhub-static-tests",
        Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        Directory.CreateDirectory(Path.Combine(_root, "images"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), IndexHtml);
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "app.js"), AppJs);
        await File.WriteAllTextAsync(Path.Combine(_root, "main.css"), "body{margin:0}");
        await File.WriteAllBytesAsync(Path.Combine(_root, "favicon.ico"), [0, 0, 1, 0]);
        await File.WriteAllBytesAsync(Path.Combine(_root, "images", "logo.png"), [0x89, 0x50, 0x4e, 0x47]);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        _app = builder.Build();

        // A mapped route always wins over the fallback, like gin's tree match
        // before NoRoute.
        _app.MapGet("/health", () => Results.Text("ok"));

        // The default MapFallback() pattern is "{*path:nonfile}", which would
        // hide every asset path; the reference NoRoute sees all paths.
        _app.MapFallback("{**path}", (RequestDelegate)new StaticHandler(_root).HandleAsync);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnknownApiPathReturnsTheReferenceJsonEnvelope()
    {
        using var response = await _client.GetAsync("/v1/not-found");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/json; charset=utf-8",
            response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            """{"error":{"type":"Not Found","message":"path not found: /v1/not-found"}}""",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/admin/not-found")]
    [InlineData("/anthropic/not-found")]
    [InlineData("/doubao/not-found")]
    [InlineData("/gemini/not-found")]
    [InlineData("/jina/not-found")]
    [InlineData("/openapi/not-found")]
    [InlineData("/v1beta/not-found")]
    [InlineData("/v1")]
    public async Task EveryApiPrefixGetsTheEnvelope(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "application/json; charset=utf-8",
            response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            "{\"error\":{\"type\":\"Not Found\",\"message\":\"path not found: "
                + path
                + "\"}}",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/v10/models")]
    [InlineData("/administering")]
    [InlineData("/v1betaX")]
    [InlineData("/anthropicx/v1/messages")]
    public async Task PrefixLookalikesFallBackToTheSpa(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AClientRouteServesTheIndexWithNoCacheHeaders()
    {
        using var response = await _client.GetAsync("/settings/profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.TryGetValues("Cache-Control", out _));
    }

    [Fact]
    public async Task TheSpaResponseCarriesTheExactReferenceHeaders()
    {
        // HttpClient re-serializes Cache-Control, so the exact bytes are
        // asserted on the response the handler writes.
        var context = new DefaultHttpContext();

        await new StaticHandler(_root).HandleAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(
            "no-cache, no-store, must-revalidate",
            context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());
        Assert.Equal("0", context.Response.Headers.Expires.ToString());
        Assert.Equal("text/html; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public async Task TheRootServesTheIndex()
    {
        using var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AFileUnderAssetsIsServedFromDisk()
    {
        using var response = await _client.GetAsync("/assets/app.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AppJs, await response.Content.ReadAsStringAsync());
        Assert.Contains(
            "javascript",
            response.Content.Headers.ContentType?.MediaType,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssetSuffixesAreMatchedOutsideTheAssetDirectories()
    {
        using var response = await _client.GetAsync("/main.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("body{margin:0}", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/css", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ImagesAreServedAsBinary()
    {
        using var response = await _client.GetAsync("/images/logo.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([0x89, 0x50, 0x4e, 0x47], await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AMissingAssetStaysAPlainText404()
    {
        using var response = await _client.GetAsync("/assets/definitely-missing.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("404 page not found\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task FaviconIsAnExactAssetPathWhileItsLookalikeIsTheSpa()
    {
        using var favicon = await _client.GetAsync("/favicon.ico");
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal([0, 0, 1, 0], await favicon.Content.ReadAsByteArrayAsync());

        using var lookalike = await _client.GetAsync("/favicon");
        Assert.Equal(HttpStatusCode.OK, lookalike.StatusCode);
        Assert.Equal(IndexHtml, await lookalike.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AssetSuffixMatchingIsCaseSensitiveLikeTheReference()
    {
        using var response = await _client.GetAsync("/MAIN.CSS");

        // Not recognized as an asset, so it is an SPA route; the file name
        // case matters in the reference's Go suffix check.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HeadRequestsGetTheHeadersWithoutABody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/assets/app.js");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AppJs.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task MappedRoutesWinOverTheFallback()
    {
        using var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EveryMethodFallsBackIncludingPost()
    {
        using var response = await _client.PostAsync(
            "/settings/profile",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AMissingIndexIsAPlainText404InsteadOfADirectoryListing()
    {
        // Reference divergence C129: http.FileServer would list the embedded
        // root; the port treats an empty deployment as missing content.
        var emptyRoot = Path.Combine(_root, "empty");
        Directory.CreateDirectory(emptyRoot);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapFallback("{**path}", (RequestDelegate)new StaticHandler(emptyRoot).HandleAsync);
        await app.StartAsync();

        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/settings/profile");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("404 page not found\n", await response.Content.ReadAsStringAsync());
    }
}
