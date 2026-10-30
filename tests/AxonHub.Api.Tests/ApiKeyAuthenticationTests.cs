using System.Net;
using System.Text.Json.Nodes;
using AxonHub.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the API key middleware port
/// (internal/server/middleware/{auth,header,ip_blocklist}.go): header
/// extraction, the AuthenticateAPIKey → AuthenticateNoAuth fallback, allowed_ips
/// matching and the 401/403/500 bodies.
/// </summary>
public sealed class ApiKeyAuthenticationTests
{
    [Fact]
    public void ExtractorPrefersAuthorizationAndStripsAllowedPrefixes()
    {
        foreach (var prefix in new[] { "Bearer ", "Token ", "Api-Key ", "API-Key " })
        {
            var request = NewRequest(("Authorization", prefix + "sk-value"));
            Assert.Equal("sk-value", ApiKeyHeaderExtractor.Extract(request.Request.Headers));
        }

        // RequireBearer is false: a bare key in Authorization is accepted.
        Assert.Equal("sk-bare", ApiKeyHeaderExtractor.Extract(
            NewRequest(("Authorization", "sk-bare")).Request.Headers));
    }

    [Theory]
    [InlineData("X-API-Key")]
    [InlineData("X-Api-Key")]
    [InlineData("API-Key")]
    [InlineData("Api-Key")]
    [InlineData("X-Goog-Api-Key")]
    [InlineData("X-Google-Api-Key")]
    public void ExtractorFallsBackToEveryDefaultHeader(string headerName)
    {
        var request = NewRequest((headerName, "sk-header"));
        Assert.Equal("sk-header", ApiKeyHeaderExtractor.Extract(request.Request.Headers));
    }

    [Fact]
    public void ExtractorHonoursHeaderPriorityOrder()
    {
        var request = NewRequest(
            ("X-API-Key", "sk-second"),
            ("Authorization", "Bearer sk-first"));

        Assert.Equal("sk-first", ApiKeyHeaderExtractor.Extract(request.Request.Headers));
    }

    [Fact]
    public void ExtractorUsesOnlyTheFirstHeaderValue()
    {
        // Go's Header.Get returns the first value, not a comma-joined list.
        var request = new DefaultHttpContext();
        request.Request.Headers.Append("Authorization", "Bearer sk-first");
        request.Request.Headers.Append("Authorization", "Bearer sk-second");

        Assert.Equal("sk-first", ApiKeyHeaderExtractor.Extract(request.Request.Headers));
    }

    [Fact]
    public void ExtractorTrimsAndSkipsEmptyValues()
    {
        var request = NewRequest(
            ("Authorization", "Bearer "),
            ("X-API-Key", "  sk-spaced  "));

        Assert.Equal("sk-spaced", ApiKeyHeaderExtractor.Extract(request.Request.Headers));
        Assert.Null(ApiKeyHeaderExtractor.Extract(NewRequest().Request.Headers));
    }

    [Fact]
    public void ExtractorDoesNotStripPrefixesCaseInsensitively()
    {
        // strings.CutPrefix is case-sensitive in the reference.
        var request = NewRequest(("Authorization", "bearer sk-lower"));
        Assert.Equal("bearer sk-lower", ApiKeyHeaderExtractor.Extract(request.Request.Headers));
    }

    [Fact]
    public async Task ValidKeyReturnsKeyAndProjectIdentity()
    {
        var (auth, keys, projects, users) = NewAuthenticator();
        var project = projects.Add();
        var key = keys.Add(row => row.ProjectId = project.Id);
        users.Add();

        var identity = await auth.AuthenticateAsync(
            NewRequest(("Authorization", "Bearer sk-user")).Request);

        Assert.Equal(new RequestIdentity(project.Id, key.Id), identity);
    }

    [Fact]
    public async Task KeyWithoutMatchingRowIsRejectedWhenNoAuthIsDisabled()
    {
        var (auth, _, _, _) = NewAuthenticator();
        await Assert.ThrowsAsync<InvalidApiKeyException>(() => auth.AuthenticateAsync(
            NewRequest(("Authorization", "Bearer sk-missing")).Request));
    }

    [Fact]
    public async Task DisabledKeyIsRejectedWhenNoAuthIsDisabled()
    {
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.Status = "disabled";
        });

        await Assert.ThrowsAsync<InvalidApiKeyException>(() => auth.AuthenticateAsync(
            NewRequest(("Authorization", "Bearer sk-user")).Request));
    }

    [Fact]
    public async Task ArchivedProjectIsRejected()
    {
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add(row => row.Status = "archived");
        keys.Add(row => row.ProjectId = project.Id);

        await Assert.ThrowsAsync<InvalidApiKeyException>(() => auth.AuthenticateAsync(
            NewRequest(("Authorization", "Bearer sk-user")).Request));
    }

    [Fact]
    public async Task ClientSuppliedNoAuthTypeKeyIsRejected()
    {
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.Type = "noauth";
        });

        await Assert.ThrowsAsync<InvalidApiKeyException>(() => auth.AuthenticateAsync(
            NewRequest(("X-API-Key", "sk-user")).Request));
    }

    [Fact]
    public async Task NoAuthMagicValueIsAlwaysRejected()
    {
        var (auth, keys, projects, users) = NewAuthenticator(allowNoAuth: true);
        projects.Add();
        users.Add();

        await Assert.ThrowsAsync<InvalidApiKeyException>(() => auth.AuthenticateAsync(
            NewRequest(("Authorization", "Bearer " + ApiKeyAuthenticator.NoAuthApiKeyValue)).Request));

        Assert.Empty(keys.Rows);
    }

    [Fact]
    public async Task InvalidKeyFallsBackToLazilyCreatedNoAuthKey()
    {
        var (auth, keys, projects, users) = NewAuthenticator(allowNoAuth: true);
        var project = projects.Add();
        var owner = users.Add();

        var identity = await auth.AuthenticateAsync(
            NewRequest(("Authorization", "Bearer sk-unknown")).Request);

        var created = Assert.Single(keys.Rows);
        Assert.Equal(ApiKeyAuthenticator.NoAuthApiKeyValue, created.Key);
        Assert.Equal(ApiKeyAuthenticator.NoAuthApiKeyName, created.Name);
        Assert.Equal("noauth", created.Type);
        Assert.Equal("enabled", created.Status);
        Assert.Equal(project.Id, created.ProjectId);
        Assert.Equal(owner.Id, created.UserId);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""["write_requests","read_channels"]"""),
                JsonNode.Parse(created.Scopes!)),
            "scopes differ: " + created.Scopes);
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse("""{"activeProfile":"","profiles":null}"""),
                JsonNode.Parse(created.Profiles!)),
            "profiles differ: " + created.Profiles);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[]"), JsonNode.Parse(created.AllowedIps!)));

        Assert.Equal(new RequestIdentity(project.Id, created.Id), identity);
    }

    [Fact]
    public async Task ExistingNoAuthKeyIsReused()
    {
        var (auth, keys, projects, users) = NewAuthenticator(allowNoAuth: true);
        var project = projects.Add();
        var owner = users.Add();
        var existing = keys.Add(row =>
        {
            row.Key = ApiKeyAuthenticator.NoAuthApiKeyValue;
            row.Name = ApiKeyAuthenticator.NoAuthApiKeyName;
            row.Type = "noauth";
            row.ProjectId = project.Id;
            row.UserId = owner.Id;
        });

        var identity = await auth.AuthenticateAsync(NewRequest().Request);

        Assert.Single(keys.Rows);
        Assert.Equal(new RequestIdentity(project.Id, existing.Id), identity);
    }

    [Fact]
    public async Task ExistingNoAuthKeyWithMissingProjectIsRejectedWithoutRecreatingIt()
    {
        // The reference accidentally re-runs EnsureNoAuthAPIKey and hits the
        // unique key index (500); the port reports the same 401 as any other
        // invalid key and leaves the existing row alone (C024).
        var (auth, keys, _, users) = NewAuthenticator(allowNoAuth: true);
        users.Add();
        keys.Add(row =>
        {
            row.Key = ApiKeyAuthenticator.NoAuthApiKeyValue;
            row.Name = ApiKeyAuthenticator.NoAuthApiKeyName;
            row.Type = "noauth";
            row.ProjectId = 999;
        });

        await Assert.ThrowsAsync<InvalidApiKeyException>(
            () => auth.AuthenticateAsync(NewRequest().Request));

        Assert.Single(keys.Rows);
    }

    [Fact]
    public async Task NoAuthWithoutProjectIsRejectedAndCreatesNothing()
    {
        // Project.Query().First returns ent.NotFoundError, which the middleware
        // maps to 401 (not 500).
        var (auth, keys, _, users) = NewAuthenticator(allowNoAuth: true);
        users.Add();

        await Assert.ThrowsAsync<InvalidApiKeyException>(
            () => auth.AuthenticateAsync(NewRequest().Request));

        Assert.Empty(keys.Rows);
    }

    [Fact]
    public async Task NoAuthWithoutOwnerIsRejectedAndCreatesNothing()
    {
        var (auth, keys, projects, _) = NewAuthenticator(allowNoAuth: true);
        projects.Add();

        await Assert.ThrowsAsync<InvalidApiKeyException>(
            () => auth.AuthenticateAsync(NewRequest().Request));

        Assert.Empty(keys.Rows);
    }

    [Fact]
    public async Task AllowedIpsAcceptsExactAddressesAndCidrRanges()
    {
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.AllowedIps = """["10.0.0.0/8","192.168.1.20"]""";
        });

        var rangeIdentity = await auth.AuthenticateAsync(NewRequest(
            "10.1.2.3",
            ("Authorization", "Bearer sk-user")).Request);
        Assert.Equal(project.Id, rangeIdentity.ProjectId);

        var exactIdentity = await auth.AuthenticateAsync(NewRequest(
            "192.168.1.20",
            ("Authorization", "Bearer sk-user")).Request);
        Assert.Equal(project.Id, exactIdentity.ProjectId);
    }

    [Fact]
    public async Task AllowedIpsRejectsClientsOutsideTheList()
    {
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.AllowedIps = """["10.0.0.0/8"]""";
        });

        await Assert.ThrowsAsync<ClientIpForbiddenException>(() => auth.AuthenticateAsync(
            NewRequest("192.168.1.20", ("Authorization", "Bearer sk-user")).Request));
    }

    [Fact]
    public async Task AllowedIpsUsesGinClientIpForConfiguredTrustedProxies()
    {
        // server.trusted_proxies = 10.0.0.0/8: the connection peer is a proxy,
        // so the left-most X-Forwarded-For entry is the client.
        var (auth, keys, projects, _) = NewAuthenticator(trustedProxies: "10.0.0.0/8");
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.AllowedIps = """["10.0.0.0/8"]""";
        });

        await Assert.ThrowsAsync<ClientIpForbiddenException>(() => auth.AuthenticateAsync(
            NewRequest(
                "10.1.2.3",
                ("Authorization", "Bearer sk-user"),
                ("X-Forwarded-For", "203.0.113.9, 10.1.2.3")).Request));

        var identity = await auth.AuthenticateAsync(NewRequest(
            "10.1.2.3",
            ("Authorization", "Bearer sk-user"),
            ("X-Forwarded-For", "10.9.9.9, 10.1.2.3")).Request);
        Assert.Equal(project.Id, identity.ProjectId);
    }

    [Fact]
    public async Task SpoofedForwardedForCannotBypassAllowedIpsWhenThePeerIsUntrusted()
    {
        // With the default trusted_proxies = [], forwarded headers are ignored
        // and the TCP peer address is checked (gin server.go hardening).
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.AllowedIps = """["10.0.0.0/8"]""";
        });

        await Assert.ThrowsAsync<ClientIpForbiddenException>(() => auth.AuthenticateAsync(
            NewRequest(
                "203.0.113.9",
                ("Authorization", "Bearer sk-user"),
                ("X-Forwarded-For", "10.1.2.3")).Request));
    }

    [Fact]
    public async Task EmptyAllowedIpsMeansNoRestriction()
    {
        var (auth, keys, projects, _) = NewAuthenticator();
        var project = projects.Add();
        keys.Add(row =>
        {
            row.ProjectId = project.Id;
            row.AllowedIps = "[]";
        });

        var identity = await auth.AuthenticateAsync(
            NewRequest("203.0.113.9", ("Authorization", "Bearer sk-user")).Request);

        Assert.Equal(project.Id, identity.ProjectId);
    }

    private static (ApiKeyAuthenticator Auth, FakeApiKeyStore Keys, FakeProjectStore Projects, FakeUserStore Users)
        NewAuthenticator(bool allowNoAuth = false, params string[] trustedProxies)
    {
        var keys = new FakeApiKeyStore();
        var projects = new FakeProjectStore();
        var users = new FakeUserStore();
        var auth = new ApiKeyAuthenticator(
            keys,
            projects,
            users,
            new ApiKeyAuthOptions(allowNoAuth, trustedProxies),
            TimeProvider.System);
        return (auth, keys, projects, users);
    }

    private static DefaultHttpContext NewRequest(
        params (string Name, string Value)[] headers)
        => NewRequest(null, headers);

    private static DefaultHttpContext NewRequest(
        string? remoteIp,
        params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        return context;
    }
}
