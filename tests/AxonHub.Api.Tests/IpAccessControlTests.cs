using System.Net;
using AxonHub.Api.Auth;
using AxonHub.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// internal/server/middleware/ip_access_control.go: the global allowlist is
/// evaluated before every route, exact IPs and CIDR prefixes both match, and a
/// denied request is a 302 redirect or an empty 404.
/// </summary>
public sealed class IpAccessControlTests
{
    private static (IpAccessControlMiddleware Middleware, Func<bool> Called) NewMiddleware(
        IpAccessControlConfig config)
    {
        var called = false;
        var middleware = new IpAccessControlMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            config,
            ApiKeyAuthOptions.Default,
            NullLogger<IpAccessControlMiddleware>.Instance);

        return (middleware, () => called);
    }

    private static DefaultHttpContext NewContext(string? remoteIp)
    {
        var context = new DefaultHttpContext();
        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        return context;
    }

    [Fact]
    public async Task DisabledConfigPassesEveryRequest()
    {
        var config = new IpAccessControlConfig(IpAccessControlSettings.Default);
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext("203.0.113.9");

        await middleware.InvokeAsync(context);

        Assert.True(called());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ExactAddressAllowed()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["192.168.1.10"],
            string.Empty));
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext("192.168.1.10");

        await middleware.InvokeAsync(context);

        Assert.True(called());
    }

    [Fact]
    public async Task CidrPrefixAllowed()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["10.0.0.0/8"],
            string.Empty));
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext("10.42.7.3");

        await middleware.InvokeAsync(context);

        Assert.True(called());
    }

    [Fact]
    public async Task DeniedRequestIsEmptyNotFound()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["192.168.1.10"],
            string.Empty));
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext("192.168.1.11");

        await middleware.InvokeAsync(context);

        Assert.False(called());
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(string.Empty, context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task DeniedRequestRedirectsWhenConfigured()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["192.168.1.10"],
            "https://example.com/blocked"));
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext("192.168.1.11");

        await middleware.InvokeAsync(context);

        Assert.False(called());
        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        Assert.Equal("https://example.com/blocked", context.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task MissingClientAddressIsDenied()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["192.168.1.10"],
            string.Empty));
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext(remoteIp: null);

        await middleware.InvokeAsync(context);

        Assert.False(called());
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public void InvalidAddressFailsAtConstruction()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new IpAccessControlConfig(new IpAccessControlSettings(
                true,
                ["not-an-ip"],
                string.Empty)));

        Assert.Contains("invalid allowed IP \"not-an-ip\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPrefixFailsAtConstruction()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new IpAccessControlConfig(new IpAccessControlSettings(
                true,
                ["10.0.0.0/99"],
                string.Empty)));

        Assert.Contains("invalid allowed IP prefix \"10.0.0.0/99\"", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyEntriesAreIgnored()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["  ", "192.168.1.10"],
            string.Empty));
        var (middleware, called) = NewMiddleware(config);
        var context = NewContext("192.168.1.10");

        await middleware.InvokeAsync(context);

        Assert.True(called());
    }

    [Fact]
    public async Task ApplyReplacesSnapshotAndRejectedApplyKeepsTheOldOne()
    {
        var config = new IpAccessControlConfig(new IpAccessControlSettings(
            true,
            ["192.168.1.10"],
            string.Empty));
        var called = false;
        var middleware = new IpAccessControlMiddleware(
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            },
            config,
            ApiKeyAuthOptions.Default,
            NullLogger<IpAccessControlMiddleware>.Instance);

        config.Apply(new IpAccessControlSettings(true, ["192.168.1.11"], string.Empty));
        var allowedAfterApply = NewContext("192.168.1.11");
        await middleware.InvokeAsync(allowedAfterApply);
        Assert.True(called);

        Assert.Throws<InvalidOperationException>(() =>
            config.Apply(new IpAccessControlSettings(true, ["bogus"], string.Empty)));

        called = false;
        var stillAllowed = NewContext("192.168.1.11");
        await middleware.InvokeAsync(stillAllowed);
        Assert.True(called);

        var denied = NewContext("192.168.1.10");
        await middleware.InvokeAsync(denied);
        Assert.Equal(StatusCodes.Status404NotFound, denied.Response.StatusCode);
    }
}
