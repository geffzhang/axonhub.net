using AxonHub.Api.Auth.Oidc;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks <c>OIDCHandlers.getBaseURL</c> (internal/server/api/oidc.go:218-241)
/// together with the Go <c>url.Parse</c> rules it relies on: only the
/// configured public URL is accepted (never the Host header), the scheme must
/// be http(s), the host must be present, and exactly one trailing slash is
/// dropped. The parser quirks (no port range check, empty query/fragment
/// accepted and preserved, escapes validated) come from Go's parser.
/// </summary>
public sealed class OidcLoginOptionsTests
{
    [Theory]
    [InlineData("", OidcLoginOptions.PublicUrlRequired)]
    [InlineData("   ", OidcLoginOptions.PublicUrlRequired)]
    [InlineData("app.example.com", "invalid server.public_url")]
    [InlineData("//app.example.com", "invalid server.public_url")]
    [InlineData("https:///path", "invalid server.public_url")]
    [InlineData("https://user@app.example.com", "invalid server.public_url")]
    [InlineData("https://app.example.com?x=1", "invalid server.public_url")]
    [InlineData("https://app.example.com#frag", "invalid server.public_url")]
    [InlineData("https://app.example.com/%zz", "invalid server.public_url")]
    [InlineData("ftp://app.example.com", "server.public_url must use http or https")]
    [InlineData("mailto:admin@example.com", "invalid server.public_url")]
    public void InvalidPublicUrlsAreRejected(string publicUrl, string error)
    {
        var options = new OidcLoginOptions(publicUrl);

        Assert.False(options.TryGetBaseUrl(out var baseUrl, out var actual));
        Assert.Equal(string.Empty, baseUrl);
        Assert.Equal(error, actual);
    }

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("http://app.example.com", "http://app.example.com")]
    [InlineData("  https://app.example.com  ", "https://app.example.com")]
    [InlineData("HTTPS://app.example.com", "HTTPS://app.example.com")]
    [InlineData("https://app.example.com/", "https://app.example.com")]
    [InlineData("https://app.example.com//", "https://app.example.com/")]
    [InlineData("https://app.example.com/path", "https://app.example.com/path")]
    [InlineData("https://app.example.com:8080", "https://app.example.com:8080")]
    [InlineData("https://app.example.com:99999", "https://app.example.com:99999")]
    [InlineData("https://[::1]:8080", "https://[::1]:8080")]
    [InlineData("https://app.example.com?", "https://app.example.com?")]
    [InlineData("https://app.example.com#", "https://app.example.com#")]
    public void ValidPublicUrlsBecomeTheBaseUrl(string publicUrl, string expected)
    {
        var options = new OidcLoginOptions(publicUrl);

        Assert.True(options.TryGetBaseUrl(out var baseUrl, out var error));
        Assert.Equal(expected, baseUrl);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TheServerSectionWinsOverTheEnvironmentVariable()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:PublicUrl"] = "https://app.example.com",
                ["AXONHUB_SERVER_PUBLIC_URL"] = "https://env.example.com",
            })
            .Build();

        var options = OidcLoginOptions.FromConfiguration(configuration);

        Assert.Equal("https://app.example.com", options.PublicUrl);
    }

    [Fact]
    public void TheEnvironmentVariableIsTheFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AXONHUB_SERVER_PUBLIC_URL"] = "https://env.example.com",
            })
            .Build();

        var options = OidcLoginOptions.FromConfiguration(configuration);

        Assert.Equal("https://env.example.com", options.PublicUrl);
        Assert.True(options.TryGetBaseUrl(out var baseUrl, out _));
        Assert.Equal("https://env.example.com", baseUrl);
    }

    [Fact]
    public void AConfigurationWithoutAPublicUrlIsEmpty()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = OidcLoginOptions.FromConfiguration(configuration);

        Assert.Equal(string.Empty, options.PublicUrl);
        Assert.False(options.TryGetBaseUrl(out _, out var error));
        Assert.Equal(OidcLoginOptions.PublicUrlRequired, error);
    }
}
