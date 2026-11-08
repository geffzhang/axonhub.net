using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the per-channel token provider actor (§146): the cold seed, the
/// three-minute expiry skew behind <c>Get</c>, the five-minute
/// <c>EnsureFresh</c> window, snapshot adoption by row timestamp, the
/// conflict-driven retirement, and the keep-last-good behavior on refresh
/// failure. The one-minute timer reuses the same window logic that
/// <see cref="IChannelTokenGrain.EnsureFreshAsync"/> exercises here.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ChannelTokenGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTime Start = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private IChannelTokenGrain Grain(long channelId)
        => fixture.Cluster.GrainFactory.GetGrain<IChannelTokenGrain>(channelId);

    private static ChannelOAuthToken Token(
        string accessToken,
        DateTimeOffset? expiresAt = null,
        string refreshToken = "refresh-1")
        => new()
        {
            ClientId = "client-1",
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            TokenType = "Bearer",
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddHours(1),
        };

    private static ChannelTokenSeed Seed(
        long channelId,
        DateTime updatedAt,
        string accessToken = "access-1",
        DateTimeOffset? expiresAt = null,
        string refreshToken = "refresh-1",
        string credentialsJson = """{"apiKey":"legacy","managementApiKey":"mgmt"}""")
        => new()
        {
            ChannelId = channelId,
            ChannelType = "codex",
            Credentials = Token(accessToken, expiresAt, refreshToken),
            CredentialsJson = credentialsJson,
            UpdatedAt = updatedAt,
        };

    [Fact]
    public async Task ColdSeedServesTheCachedTokenWithoutRefresh()
    {
        fixture.TokenSource.Reset();

        var token = await Grain(9001).GetAccessTokenAsync(Seed(9001, Start));

        Assert.Equal("access-1", token.AccessToken);
        Assert.Empty(fixture.TokenSource.Requests);
    }

    [Fact]
    public async Task ExpiredTokenRefreshesThroughTheSourceAndAdoptsTheResult()
    {
        fixture.TokenSource.Reset();
        fixture.TokenSource.OnRefresh = request =>
        {
            Assert.False(request.EnsureFresh);
            Assert.Equal(9002, request.ChannelId);
            Assert.Equal("codex", request.ChannelType);
            Assert.Equal(Start, request.UpdatedAt);
            Assert.Equal("access-1", request.Credentials.AccessToken);

            return new ChannelTokenRefreshResult
            {
                Credentials = Token("access-2"),
                CredentialsJson = """{"apiKey":"rotated","managementApiKey":"mgmt"}""",
                UpdatedAt = Start.AddMinutes(1),
            };
        };

        var grain = Grain(9002);
        var expired = Seed(9002, Start, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var token = await grain.GetAccessTokenAsync(expired);
        Assert.Equal("access-2", token.AccessToken);
        Assert.Single(fixture.TokenSource.Requests);

        // The adopted row timestamp makes the old candidate snapshot stale: it
        // must not roll the rotated token back (the enabled-channel cache can
        // lag a refresh).
        var again = await grain.GetAccessTokenAsync(expired);
        Assert.Equal("access-2", again.AccessToken);
        Assert.Single(fixture.TokenSource.Requests);
    }

    [Fact]
    public async Task NewerSnapshotWinsAndOlderSnapshotsAreIgnored()
    {
        fixture.TokenSource.Reset();

        var grain = Grain(9003);
        _ = await grain.GetAccessTokenAsync(Seed(9003, Start, accessToken: "access-1"));

        var newer = await grain.GetAccessTokenAsync(
            Seed(9003, Start.AddMinutes(5), accessToken: "access-new"));
        Assert.Equal("access-new", newer.AccessToken);

        // An older (or kind-normalized-equal) snapshot never replaces the
        // adopted one.
        var stale = await grain.GetAccessTokenAsync(
            Seed(
                9003,
                DateTime.SpecifyKind(Start, DateTimeKind.Unspecified),
                accessToken: "access-stale"));
        Assert.Equal("access-new", stale.AccessToken);

        Assert.Empty(fixture.TokenSource.Requests);
    }

    [Fact]
    public async Task RetiredProviderThrowsUntilANewerSnapshotArrives()
    {
        fixture.TokenSource.Reset();
        fixture.TokenSource.OnRefresh = _ => new ChannelTokenRefreshResult
        {
            Credentials = null,
            CredentialsJson = """{"apiKey":"plain"}""",
            UpdatedAt = Start.AddMinutes(1),
            Stopped = true,
        };

        var grain = Grain(9004);
        var expired = Seed(9004, Start, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.GetAccessTokenAsync(expired));
        Assert.Equal("credentials is nil", error.Message);

        // StopAutoRefresh: later reads fail without another refresh.
        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.GetAccessTokenAsync(expired));
        Assert.Equal("credentials is nil", error.Message);
        Assert.Single(fixture.TokenSource.Requests);

        // The reference swaps in a new provider when the channel cache
        // reloads; a newer snapshot revives the grain the same way.
        var revived = await grain.GetAccessTokenAsync(
            Seed(9004, Start.AddMinutes(2), accessToken: "access-new"));
        Assert.Equal("access-new", revived.AccessToken);
    }

    [Fact]
    public async Task EnsureFreshDoesNothingWithoutARefreshToken()
    {
        fixture.TokenSource.Reset();

        var grain = Grain(9005);

        // Expiry sits between the 3-minute Get skew and the 5-minute
        // EnsureFresh window, so Get caches it and EnsureFresh must stop at the
        // missing refresh token.
        _ = await grain.GetAccessTokenAsync(
            Seed(
                9005,
                Start,
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(4),
                refreshToken: string.Empty));

        await grain.EnsureFreshAsync();

        Assert.Empty(fixture.TokenSource.Requests);
    }

    [Fact]
    public async Task EnsureFreshDoesNothingOutsideTheWindow()
    {
        fixture.TokenSource.Reset();

        var grain = Grain(9006);
        _ = await grain.GetAccessTokenAsync(
            Seed(9006, Start, expiresAt: DateTimeOffset.UtcNow.AddHours(1)));

        await grain.EnsureFreshAsync();

        Assert.Empty(fixture.TokenSource.Requests);
    }

    [Fact]
    public async Task EnsureFreshRefreshesInsideTheWindow()
    {
        fixture.TokenSource.Reset();
        fixture.TokenSource.OnRefresh = request =>
        {
            Assert.True(request.EnsureFresh);
            Assert.Equal(Start, request.UpdatedAt);

            return new ChannelTokenRefreshResult
            {
                Credentials = Token("access-fresh"),
                CredentialsJson = """{"apiKey":"fresh"}""",
                UpdatedAt = Start.AddMinutes(1),
            };
        };

        var grain = Grain(9007);
        _ = await grain.GetAccessTokenAsync(
            Seed(9007, Start, expiresAt: DateTimeOffset.UtcNow.AddMinutes(4)));

        // Get caches the still-valid token; the background window refreshes it.
        Assert.Empty(fixture.TokenSource.Requests);

        await grain.EnsureFreshAsync();

        Assert.Single(fixture.TokenSource.Requests);

        var token = await grain.GetAccessTokenAsync(Seed(9007, Start));
        Assert.Equal("access-fresh", token.AccessToken);
    }

    [Fact]
    public async Task EnsureFreshFailureKeepsTheSnapshot()
    {
        fixture.TokenSource.Reset();
        fixture.TokenSource.Failure = new InvalidOperationException("token endpoint is down");

        var grain = Grain(9008);
        _ = await grain.GetAccessTokenAsync(
            Seed(9008, Start, expiresAt: DateTimeOffset.UtcNow.AddMinutes(4)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.EnsureFreshAsync());
        Assert.Equal("token endpoint is down", error.Message);

        fixture.TokenSource.Failure = null;
        fixture.TokenSource.OnRefresh = request =>
        {
            // The failed refresh left the snapshot untouched.
            Assert.Equal("access-1", request.Credentials.AccessToken);
            Assert.Equal(Start, request.UpdatedAt);

            return new ChannelTokenRefreshResult
            {
                Credentials = Token("access-fresh"),
                CredentialsJson = """{"apiKey":"fresh"}""",
                UpdatedAt = Start.AddMinutes(1),
            };
        };

        await grain.EnsureFreshAsync();

        var token = await grain.GetAccessTokenAsync(Seed(9008, Start));
        Assert.Equal("access-fresh", token.AccessToken);
        Assert.Equal(2, fixture.TokenSource.Requests.Count);
    }
}
