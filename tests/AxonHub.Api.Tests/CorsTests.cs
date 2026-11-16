using System.Net;
using AxonHub.Api.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// routes.go:67-79 maps the <c>server.cors.*</c> block onto rs/cors. The port
/// maps the same fields onto an ASP.NET Core default policy mounted only when
/// enabled.
/// </summary>
public sealed class CorsTests
{
    private static async Task<WebApplication> StartAsync(CorsSettings settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCors(options =>
            options.AddDefaultPolicy(settings.ConfigurePolicy));

        var app = builder.Build();
        app.UseCors();
        app.MapGet("/ping", static () => "ok");
        await app.StartAsync();
        return app;
    }

    private static CorsSettings Enabled(
        IReadOnlyList<string>? origins = null,
        bool allowCredentials = false,
        IReadOnlyList<string>? exposedHeaders = null)
        => CorsSettings.Default with
        {
            Enabled = true,
            AllowedOrigins = origins ?? ["http://localhost:8090"],
            AllowCredentials = allowCredentials,
            ExposedHeaders = exposedHeaders ?? [],
        };

    [Fact]
    public async Task PreflightFromAllowedOriginReturnsRsCorsHeaders()
    {
        await using var app = await StartAsync(Enabled());
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/ping");
        request.Headers.Add("Origin", "http://localhost:8090");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "X-API-Key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://localhost:8090",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains(
            "POST",
            response.Headers.GetValues("Access-Control-Allow-Methods").Single(),
            StringComparison.Ordinal);
        Assert.Equal(
            "1800",
            response.Headers.GetValues("Access-Control-Max-Age").Single());
    }

    [Fact]
    public async Task SimpleRequestFromAllowedOriginGetsAllowOrigin()
    {
        await using var app = await StartAsync(Enabled());
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("Origin", "http://localhost:8090");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "http://localhost:8090",
            response.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task DisallowedOriginGetsNoCorsHeaders()
    {
        await using var app = await StartAsync(Enabled());
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("Origin", "https://evil.example");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task CredentialsAndExposedHeadersAreEmitted()
    {
        await using var app = await StartAsync(Enabled(
            allowCredentials: true,
            exposedHeaders: ["X-Trace-Id"]));
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/ping");
        request.Headers.Add("Origin", "http://localhost:8090");

        using var response = await client.SendAsync(request);

        Assert.Equal(
            "true",
            response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Equal(
            "X-Trace-Id",
            response.Headers.GetValues("Access-Control-Expose-Headers").Single());
    }

    [Fact]
    public void DefaultsMatchTheReferenceConfig()
    {
        var settings = CorsSettings.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(settings.Enabled);
        Assert.Equal(new[] { "http://localhost:8090" }, settings.AllowedOrigins);
        Assert.Equal(
            new[] { "GET", "POST", "DELETE", "PATCH", "PUT", "OPTIONS", "HEAD" },
            settings.AllowedMethods);
        Assert.Contains("X-API-Key", settings.AllowedHeaders);
        Assert.Empty(settings.ExposedHeaders);
        Assert.False(settings.AllowCredentials);
        Assert.Equal(TimeSpan.FromMinutes(30), settings.MaxAge);
    }

    [Fact]
    public void EnvironmentSpellingIsAccepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AXONHUB_SERVER_CORS_ENABLED"] = "true",
                ["AXONHUB_SERVER_CORS_ALLOWED_ORIGINS"] = "https://a.example,https://b.example",
                ["AXONHUB_SERVER_CORS_ALLOW_CREDENTIALS"] = "true",
                ["AXONHUB_SERVER_CORS_MAX_AGE"] = "1h",
            })
            .Build();

        var settings = CorsSettings.FromConfiguration(configuration);

        Assert.True(settings.Enabled);
        Assert.Equal(new[] { "https://a.example", "https://b.example" }, settings.AllowedOrigins);
        Assert.True(settings.AllowCredentials);
        Assert.Equal(TimeSpan.FromHours(1), settings.MaxAge);
    }
}
