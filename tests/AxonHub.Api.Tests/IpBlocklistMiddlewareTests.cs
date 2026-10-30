using System.Net;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Security;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks middleware.WithIPBlocklist (internal/server/middleware/ip_blocklist.go):
/// gin ClientIP candidates, isBlockedIP matching and the 403 AbortWithError body.
/// </summary>
public sealed class IpBlocklistMiddlewareTests
{
    [Fact]
    public async Task EmptyBlocklistCallsNext()
    {
        var (context, nextCalled, settings) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: SecuritySettings.Default);

        Assert.True(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, settings.Calls);
    }

    [Fact]
    public async Task NoClientIpSkipsTheSettingsLookup()
    {
        var (_, nextCalled, settings) = await InvokeAsync(
            remoteIp: null,
            settings: Blocked("203.0.113.0/24"));

        Assert.True(nextCalled.Invoked);
        Assert.Equal(0, settings.Calls);
    }

    [Fact]
    public async Task ExactAddressIsBlocked()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: Blocked("203.0.113.10"));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            """{"error":{"type":"Forbidden","message":"IP address is blocked"}}""",
            Body(context));
    }

    [Fact]
    public async Task CidrEntryIsBlocked()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: Blocked("203.0.113.0/24"));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task EntriesAreTrimmedBeforeMatching()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: Blocked(" 203.0.113.10 "));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Ipv6EntryIsBlocked()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "2001:db8::10",
            settings: Blocked("2001:db8::/64"));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvalidEntriesAreSkipped()
    {
        var (_, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: Blocked("bad-ip", "bad-prefix/33", "10.0.0.0/"));

        Assert.True(nextCalled.Invoked);
    }

    [Fact]
    public async Task PrefixBitsFollowNetipStrictness()
    {
        var (_, nextCalled, _) = await InvokeAsync(
            remoteIp: "10.1.2.3",
            settings: Blocked("10.0.0.0/+8", "10.0.0.0/08", "10.0.0.0/ 8"));

        Assert.True(nextCalled.Invoked);
    }

    [Fact]
    public async Task ZonedPrefixIsIgnored()
    {
        var (_, nextCalled, _) = await InvokeAsync(
            remoteIp: "fe80::2",
            settings: Blocked("fe80::1%1/64"));

        Assert.True(nextCalled.Invoked);
    }

    [Fact]
    public async Task NonMatchingClientCallsNext()
    {
        var (_, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: Blocked("198.51.100.0/24", "192.0.2.1"));

        Assert.True(nextCalled.Invoked);
    }

    [Fact]
    public async Task Ipv4MappedPrefixMatchesIpv4ClientLikeNetip()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.10",
            settings: Blocked("::ffff:203.0.113.0/120"));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task TrustedProxyForwardedForIsChecked()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "10.0.0.1",
            forwardedFor: "203.0.113.9, 10.1.2.3",
            settings: Blocked("203.0.113.0/24"),
            trustedProxies: ["10.0.0.0/8"]);

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task SpoofedForwardedForIsIgnoredForUntrustedPeers()
    {
        var (_, nextCalled, _) = await InvokeAsync(
            remoteIp: "198.51.100.7",
            forwardedFor: "203.0.113.9",
            settings: Blocked("203.0.113.0/24"));

        Assert.True(nextCalled.Invoked);
    }

    [Fact]
    public async Task BlockedPeerWinsOverItsOwnForwardedFor()
    {
        var (context, nextCalled, _) = await InvokeAsync(
            remoteIp: "203.0.113.9",
            forwardedFor: "198.51.100.7",
            settings: Blocked("203.0.113.0/24"));

        Assert.False(nextCalled.Invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    private static SecuritySettings Blocked(params string[] blockedIps)
        => new(blockedIps, ShowRequestLogIpBanIcon: true);

    private static async Task<(HttpContext Context, NextCall NextCalled, FakeSecuritySettingsProvider Settings)>
        InvokeAsync(
            string? remoteIp,
            SecuritySettings settings,
            string? forwardedFor = null,
            string[]? trustedProxies = null)
    {
        var context = new DefaultHttpContext
        {
            Response = { Body = new MemoryStream() },
        };

        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        var next = new NextCall();
        var provider = new FakeSecuritySettingsProvider { Settings = settings };
        var middleware = new IpBlocklistMiddleware(
            _ =>
            {
                next.Invoked = true;
                return Task.CompletedTask;
            },
            new ApiKeyAuthOptions(AllowNoAuth: false, trustedProxies ?? []));

        await middleware.InvokeAsync(context, provider);
        return (context, next, provider);
    }

    private static string Body(HttpContext context)
    {
        var stream = Assert.IsType<MemoryStream>(context.Response.Body);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class NextCall
    {
        public bool Invoked { get; set; }
    }
}
