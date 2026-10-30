using System.Net;
using AxonHub.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the gin ClientIP port and the allowed_ips matcher. Reference sources:
/// gin v1.11.0 context.go ClientIP/validateHeader (with
/// SetTrustedProxies(config.TrustedProxies) in server.go) and
/// internal/server/middleware/ip_blocklist.go.
/// </summary>
public sealed class ClientAddressTests
{
    [Fact]
    public void ForwardedHeadersAreIgnoredWhenThePeerIsNotATrustedProxy()
    {
        // server.trusted_proxies defaults to []; a direct client must not be
        // able to spoof X-Forwarded-For / X-Real-IP.
        var request = Request(
            forwardedFor: "203.0.113.9",
            realIp: "198.51.100.7",
            remoteIp: "10.0.0.1");

        Assert.Equal("10.0.0.1", ClientAddress.Resolve(request, []));
    }

    [Fact]
    public void ForwardedForChainReturnsTheLeftMostTrustedHop()
    {
        var request = Request(
            forwardedFor: "203.0.113.9, 10.1.2.3 , 10.0.0.1",
            remoteIp: "10.0.0.1");

        Assert.Equal(
            "203.0.113.9",
            ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void FirstUntrustedHopFromTheRightIsTheClient()
    {
        // The chain is proxy-appended. Walking right-to-left, stop at the first
        // address that is not itself a trusted proxy.
        var request = Request(
            forwardedFor: "203.0.113.9, 198.51.100.5, 10.1.2.3",
            remoteIp: "10.0.0.1");

        Assert.Equal(
            "198.51.100.5",
            ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void ForwardedForMultipleHeaderValuesAreJoined()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Append("X-Forwarded-For", "203.0.113.9");
        context.Request.Headers.Append("X-Forwarded-For", "10.1.2.3");
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");

        Assert.Equal(
            "203.0.113.9",
            ClientAddress.Resolve(context.Request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void InvalidForwardedEntryInvalidatesTheWholeHeader()
    {
        var request = Request(
            forwardedFor: "203.0.113.9, not-an-ip",
            realIp: "198.51.100.7",
            remoteIp: "10.0.0.1");

        Assert.Equal(
            "198.51.100.7",
            ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void InvalidForwardedAndRealIpFallBackToTheRemoteAddress()
    {
        var request = Request(forwardedFor: "nope", realIp: "also-nope", remoteIp: "10.0.0.1");

        Assert.Equal("10.0.0.1", ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void RealIpIsUsedWhenForwardedForIsAbsent()
    {
        var request = Request(realIp: "198.51.100.7", remoteIp: "10.0.0.1");

        Assert.Equal(
            "198.51.100.7",
            ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void RemoteAddressIsUsedWithoutHeaders()
    {
        Assert.Equal(
            "10.0.0.1",
            ClientAddress.Resolve(Request(remoteIp: "10.0.0.1"), ["10.0.0.0/8"]));
        Assert.Equal(
            "10.0.0.1",
            ClientAddress.Resolve(Request(remoteIp: "10.0.0.1"), []));
        Assert.Equal("::1", ClientAddress.Resolve(Request(remoteIp: "::1"), []));
        Assert.Equal(string.Empty, ClientAddress.Resolve(Request(), []));
    }

    [Theory]
    [InlineData("127.1")]
    [InlineData("010.0.0.1")]
    [InlineData("1.2.3")]
    [InlineData("1.2.3.4.5")]
    [InlineData("  ")]
    public void NetipParseAddrStrictnessIsEnforced(string forwardedFor)
    {
        var request = Request(forwardedFor: forwardedFor, remoteIp: "10.0.0.1");

        Assert.Equal("10.0.0.1", ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void Ipv6ForwardedForIsAccepted()
    {
        var request = Request(forwardedFor: "2001:db8::1, 10.0.0.1", remoteIp: "10.0.0.1");

        Assert.Equal(
            "2001:db8::1",
            ClientAddress.Resolve(request, ["10.0.0.0/8"]));
    }

    [Fact]
    public void TrustedProxyEntriesSupportSingleAddressesAndRanges()
    {
        Assert.Equal(
            "203.0.113.9",
            ClientAddress.Resolve(
                Request(forwardedFor: "203.0.113.9", remoteIp: "10.0.0.1"),
                ["10.0.0.1"]));
        Assert.Equal(
            "203.0.113.9",
            ClientAddress.Resolve(
                Request(forwardedFor: "203.0.113.9", remoteIp: "10.0.0.1"),
                ["10.0.0.0/8"]));
    }

    [Fact]
    public void InvalidTrustedProxyConfigurationIsRejected()
    {
        ClientAddress.ValidateTrustedProxies([]);
        ClientAddress.ValidateTrustedProxies(["10.0.0.0/8", "192.168.1.1", "2001:db8::/32"]);

        Assert.Throws<InvalidOperationException>(
            () => ClientAddress.ValidateTrustedProxies(["not-an-ip"]));
        Assert.Throws<InvalidOperationException>(
            () => ClientAddress.ValidateTrustedProxies(["10.0.0.0/33"]));
    }

    [Fact]
    public void AllowedIpsMatchesExactAddressesAndRanges()
    {
        Assert.True(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], ["10.1.2.3"]));
        Assert.True(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], ["10.0.0.0/8"]));

        // netip.ParsePrefix keeps host bits; Contains applies the mask.
        Assert.True(ClientAddress.IsAnyAllowedIp(["192.168.1.5"], ["192.168.1.77/24"]));

        Assert.False(ClientAddress.IsAnyAllowedIp(["192.168.1.5"], ["10.0.0.0/8"]));
        Assert.False(ClientAddress.IsAnyAllowedIp(["not-an-ip"], ["10.0.0.0/8"]));
        Assert.False(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], []));
    }

    [Fact]
    public void AllowedIpsMatchesIpv6Ranges()
    {
        Assert.True(ClientAddress.IsAnyAllowedIp(["2001:db8::10"], ["2001:db8::10"]));
        Assert.True(ClientAddress.IsAnyAllowedIp(["2001:db8::10"], ["2001:db8::/64"]));
        Assert.False(ClientAddress.IsAnyAllowedIp(["2001:db9::1"], ["2001:db8::/32"]));
        Assert.False(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], ["2001:db8::/32"]));
    }

    [Theory]
    [InlineData("::ffff:203.0.113.10", "203.0.113.10", true)]
    [InlineData("::ffff:203.0.113.10", "203.0.113.0/24", true)]
    [InlineData("203.0.113.10", "::ffff:203.0.113.10", true)]
    [InlineData("203.0.113.10", "::ffff:203.0.113.0/120", true)]
    [InlineData("::ffff:203.0.113.10", "::ffff:203.0.113.0/120", true)]
    [InlineData("203.0.113.10", "::ffff:203.0.113.99/120", true)]
    [InlineData("198.51.100.10", "::ffff:0.0.0.0/96", true)]
    [InlineData("203.0.113.10", "::ffff:203.0.113.10/128", true)]
    [InlineData("203.0.113.11", "::ffff:203.0.113.10/128", false)]
    [InlineData("::ffff:198.51.100.10", "203.0.113.0/24", false)]
    [InlineData("198.51.100.10", "::ffff:203.0.113.0/120", false)]
    [InlineData("203.0.113.10", "2001:db8::/64", false)]
    [InlineData("2001:db8::10", "::ffff:0.0.0.0/96", false)]
    [InlineData("203.0.113.10", "::ffff:203.0.113.0/95", false)]
    [InlineData("::ffff:203.0.113.10", "::ffff:203.0.113.0/95", true)]
    [InlineData("::ffff:203.0.113.10", "::/0", true)]
    [InlineData("203.0.113.10", "::/0", false)]
    public void AllowedIpsHandlesIpv4MappedAddressesLikeNetip(
        string client,
        string entry,
        bool expected)
    {
        Assert.Equal(expected, ClientAddress.IsAnyAllowedIp([client], [entry]));
    }

    [Fact]
    public void InvalidAllowedEntriesAreSkipped()
    {
        Assert.True(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], ["bad", "10.0.0.0/33", "10.0.0.0/8"]));
        Assert.False(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], ["bad", "10.0.0.0/33"]));
        Assert.False(ClientAddress.IsAnyAllowedIp(["10.1.2.3"], ["10.0.0.0/"]));
    }

    private static HttpRequest Request(
        string? forwardedFor = null,
        string? realIp = null,
        string? remoteIp = null)
    {
        var context = new DefaultHttpContext();
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        if (realIp is not null)
        {
            context.Request.Headers["X-Real-IP"] = realIp;
        }

        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        return context.Request;
    }
}
