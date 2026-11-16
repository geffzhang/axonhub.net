using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Assets;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks GET /favicon (system.go:187-251): the embedded default icon, the
/// brand-logo data URL branches and their two 400s. The route was registered
/// as gap C242 before this port.
/// </summary>
public sealed class FaviconEndpointTests : IAsyncLifetime
{
    private static readonly byte[] DefaultIcon = [0x00, 0x01, 0xfe, 0xff];

    private readonly FakeSystemStore _systems = new();
    private readonly FakeFaviconProvider _favicons = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IDataStorageStore>(new FakeDataStorageStore());
        builder.Services.AddSingleton<ISystemSettingsAdminService, SystemSettingsAdminService>();
        builder.Services.AddSingleton<IFaviconProvider>(_favicons);

        _app = builder.Build();

        // RequestDelegate mapping mirrors minimal API binding and keeps the
        // route-handler analyzer away from a foreign method group (AD0001).
        RequestDelegate favicon = context => SystemHandlers.GetFaviconAsync(
            context,
            context.RequestServices.GetRequiredService<ISystemSettingsAdminService>(),
            context.RequestServices.GetRequiredService<IFaviconProvider>(),
            context.RequestServices.GetRequiredService<ILoggerFactory>(),
            context.RequestAborted);
        _app.MapGet("/favicon", favicon);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task FaviconServesTheDefaultIconWhenNoBrandLogoIsSet()
    {
        _favicons.Bytes = DefaultIcon;

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/x-icon", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "public, max-age=3600",
            Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal(DefaultIcon, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FaviconFallsBackToTheDefaultIconWhenTheBrandLogoReadFails()
    {
        _favicons.Bytes = DefaultIcon;
        _systems.GetFailuresByKey[SystemConfigBackupKeys.BrandLogo] =
            new InvalidOperationException("db down");

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/x-icon", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(DefaultIcon, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FaviconReportsAMissingDefaultAssetWithTheGinEnvelope()
    {
        _favicons.Bytes = null;

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            "Failed to read default favicon",
            await ReadErrorMessageAsync(response));
    }

    [Fact]
    public async Task FaviconServesTheBrandLogoDataUrl()
    {
        var image = new byte[] { 0x89, 0x50, 0x4e, 0x47 };
        _systems.Add(
            SystemConfigBackupKeys.BrandLogo,
            "data:image/png;base64," + Convert.ToBase64String(image));

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "public, max-age=3600",
            Assert.Single(response.Headers.GetValues("Cache-Control")));
        Assert.Equal(image.Length, response.Content.Headers.ContentLength);
        Assert.Equal(image, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FaviconAcceptsAnEmptyBase64Payload()
    {
        _systems.Add(SystemConfigBackupKeys.BrandLogo, "data:image/png;base64,");

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task FaviconIgnoresCrLfInsideBase64LikeGo()
    {
        // base64.StdEncoding ignores \r and \n: "QU\r\nJD" is "ABC".
        _systems.Add(
            SystemConfigBackupKeys.BrandLogo,
            "data:image/png;base64,QU\r\nJD");

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ABC", Encoding.ASCII.GetString(
            await response.Content.ReadAsByteArrayAsync()));
    }

    [Theory]
    [InlineData("image/png;base64,QUJD")]
    [InlineData("data:image/png;base64")]
    [InlineData("data:image/png;base64,QUJD,extra")]
    [InlineData("data:image/png,QUJD")]
    public async Task FaviconRejectsMalformedBrandLogos(string brandLogo)
    {
        _systems.Add(SystemConfigBackupKeys.BrandLogo, brandLogo);

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Invalid brand logo format",
            await ReadErrorMessageAsync(response));
    }

    [Theory]
    [InlineData("!!!")]
    [InlineData("QU JD")]
    [InlineData("QUI")]
    public async Task FaviconRejectsUndecodableBase64(string payload)
    {
        // Go's StdEncoding rejects non-alphabet characters, whitespace other
        // than CR/LF, and a final quantum without complete padding ("QUI"
        // would need "QUI="); C242 locks the same accept/reject set.
        _systems.Add(
            SystemConfigBackupKeys.BrandLogo,
            "data:image/png;base64," + payload);

        using var response = await _client.GetAsync("/favicon");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Failed to decode brand logo",
            await ReadErrorMessageAsync(response));
    }

    [Fact]
    public void EmbeddedFaviconIsByteIdenticalToTheReferenceAsset()
    {
        var bytes = new EmbeddedFaviconProvider().ReadDefaultFavicon();

        Assert.NotNull(bytes);
        Assert.Equal(28882, bytes.Length);
        Assert.Equal(
            "faadeaf0626d71ef774fdb899e54c82bd2f7717581c337e4d095ea681b2eefed",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        return document.RootElement
            .GetProperty("error")
            .GetProperty("message")
            .GetString()!;
    }

    private sealed class FakeFaviconProvider : IFaviconProvider
    {
        public byte[]? Bytes { get; set; }

        public byte[]? ReadDefaultFavicon() => Bytes;
    }
}
