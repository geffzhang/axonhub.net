using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks <c>ChannelTokenSource</c>, the API half of the per-channel token grain
/// (§146): the reference's optimistic credentials write
/// (<c>refreshOAuthTokenWithClient</c>, channel_llm.go:1642-1702), including the
/// metadata-only retry, the conflict winner, the retirement of a non-OAuth row,
/// and the keep-in-memory behavior when persistence fails. Also locks the
/// refresh transport: the channel's proxy-aware client when the seed's
/// settings configure one, the shared client otherwise (§178).
/// </summary>
public sealed class ChannelTokenSourceTests
{
    private static readonly DateTime Start = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ChannelTokenRefreshRequest Request(bool ensureFresh = false) => new()
    {
        ChannelId = 501,
        ChannelType = "codex",
        Credentials = new ChannelOAuthToken
        {
            ClientId = "client-1",
            AccessToken = "access-old",
            RefreshToken = "refresh-old",
            TokenType = "Bearer",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Scopes = ["openid"],
        },
        CredentialsJson = SeedJson,
        UpdatedAt = Start,
        EnsureFresh = ensureFresh,
    };

    private const string SeedJson =
        """{"apiKey":"legacy","managementApiKey":"mgmt","vertexApiKeys":[{"projectID":"proj","apiKey":"vk","region":"us"}]}""";

    private static ScriptedExecutor TokenExecutor() => new()
    {
        OnDo = (_, _, _) => new HttpResponseData
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                """
                {"access_token":"access-refreshed","refresh_token":"refresh-refreshed","token_type":"Bearer","expires_in":3600}
                """),
        },
    };

    private static ChannelTokenSource NewSource(
        FakeChannelTokenStore store,
        ScriptedExecutor executor,
        ScriptedProxyExecutorFactory? proxyFactory = null)
        => new(
            store,
            new ChannelProxyExecutors(executor, proxyFactory ?? new ScriptedProxyExecutorFactory()),
            TimeProvider.System,
            NullLogger<ChannelTokenSource>.Instance);

    [Fact]
    public async Task RefreshPersistsTheRotatedCredentialsAndReturnsTheNewSnapshot()
    {
        var store = new FakeChannelTokenStore();
        var executor = TokenExecutor();
        var source = NewSource(store, executor);

        var result = await source.RefreshAsync(Request());

        Assert.False(result.Stopped);
        Assert.NotNull(result.Credentials);
        Assert.Equal("access-refreshed", result.Credentials!.AccessToken);
        Assert.Single(executor.Requests);

        var call = Assert.Single(store.Updates);
        Assert.Equal(501, call.Id);
        Assert.Equal(Start, call.ExpectedUpdatedAt);
        Assert.Equal(call.Credentials, result.CredentialsJson);
        Assert.Equal(call.UpdatedAt, result.UpdatedAt);

        // The write base is the snapshot document: unrelated fields survive,
        // apiKey carries the refreshed OAuth document, and oauth is the typed
        // twin (channel_llm.go:1658-1667).
        using var document = JsonDocument.Parse(call.Credentials);
        var root = document.RootElement;
        Assert.Equal("mgmt", root.GetProperty("managementApiKey").GetString());
        Assert.Equal("proj", root.GetProperty("vertexApiKeys")[0].GetProperty("projectID").GetString());

        using var apiKeyDocument = JsonDocument.Parse(root.GetProperty("apiKey").GetString()!);
        Assert.Equal(
            "access-refreshed",
            apiKeyDocument.RootElement.GetProperty("access_token").GetString());

        var oauth = root.GetProperty("oauth");
        Assert.Equal("access-refreshed", oauth.GetProperty("access_token").GetString());
        Assert.Equal("refresh-refreshed", oauth.GetProperty("refresh_token").GetString());
        Assert.Equal("client-1", oauth.GetProperty("client_id").GetString());
    }

    [Fact]
    public async Task MetadataOnlyRowChangeRetriesWithTheCurrentTimestamp()
    {
        var store = new FakeChannelTokenStore();

        // A concurrent writer touched only metadata (the credentials are the
        // snapshot's), so the reference retries instead of reporting a
        // conflict: a rotated refresh token must not be discarded.
        store.OnUpdate = call => store.Updates.Count == 1
            ? new ChannelTokenUpdate(
                false,
                new ChannelTokenRow(501, "codex", SeedJson, Start.AddMinutes(3)))
            : new ChannelTokenUpdate(true, null);

        var source = NewSource(store, TokenExecutor());

        var result = await source.RefreshAsync(Request());

        Assert.Equal(2, store.Updates.Count);
        Assert.Equal(Start, store.Updates[0].ExpectedUpdatedAt);
        Assert.Equal(Start.AddMinutes(3), store.Updates[1].ExpectedUpdatedAt);
        Assert.Equal(store.Updates[0].Credentials, store.Updates[1].Credentials);
        Assert.Equal(store.Updates[1].UpdatedAt, result.UpdatedAt);
        Assert.Equal("access-refreshed", result.Credentials!.AccessToken);
    }

    [Fact]
    public async Task ConcurrentCredentialChangeReturnsTheWinner()
    {
        const string WinnerJson =
            """
            {"apiKey":"winner","oauth":{"client_id":"client-1","access_token":"access-winner","refresh_token":"refresh-winner","expires_at":"2030-01-01T00:00:00Z","token_type":"Bearer"}}
            """;

        var store = new FakeChannelTokenStore
        {
            OnUpdate = _ => new ChannelTokenUpdate(
                false,
                new ChannelTokenRow(501, "codex", WinnerJson, Start.AddMinutes(4))),
        };

        var source = NewSource(store, TokenExecutor());

        var result = await source.RefreshAsync(Request());

        Assert.False(result.Stopped);
        Assert.Equal("access-winner", result.Credentials!.AccessToken);
        Assert.Equal("refresh-winner", result.Credentials.RefreshToken);
        Assert.Equal(WinnerJson, result.CredentialsJson);
        Assert.Equal(Start.AddMinutes(4), result.UpdatedAt);
        Assert.Single(store.Updates);
    }

    [Fact]
    public async Task NonOAuthWinningRowRetiresTheProvider()
    {
        var store = new FakeChannelTokenStore
        {
            OnUpdate = _ => new ChannelTokenUpdate(
                false,
                new ChannelTokenRow(501, "codex", """{"apiKey":"plain"}""", Start.AddMinutes(4))),
        };

        var source = NewSource(store, TokenExecutor());

        var result = await source.RefreshAsync(Request());

        Assert.True(result.Stopped);
        Assert.Null(result.Credentials);
        Assert.Equal("""{"apiKey":"plain"}""", result.CredentialsJson);
        Assert.Equal(Start.AddMinutes(4), result.UpdatedAt);
    }

    [Fact]
    public async Task PersistenceFailureKeepsTheRefreshedCredentialsInMemory()
    {
        var store = new FakeChannelTokenStore
        {
            Failure = new StoreFailureException(
                "database is down",
                new InvalidOperationException("database is down")),
        };

        var source = NewSource(store, TokenExecutor());

        var result = await source.RefreshAsync(Request());

        Assert.False(result.Stopped);
        Assert.Equal("access-refreshed", result.Credentials!.AccessToken);

        // The snapshot was not advanced: the next refresh retries the same
        // write base.
        Assert.Equal(SeedJson, result.CredentialsJson);
        Assert.Equal(Start, result.UpdatedAt);
    }

    [Fact]
    public async Task EnsureFreshWithoutARefreshTokenDoesNotRefresh()
    {
        var store = new FakeChannelTokenStore();
        var executor = TokenExecutor();
        var source = NewSource(store, executor);

        var request = Request(ensureFresh: true);
        request.Credentials.RefreshToken = string.Empty;

        var result = await source.RefreshAsync(request);

        Assert.Equal("access-old", result.Credentials!.AccessToken);
        Assert.Empty(store.Updates);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task RefreshUsesTheChannelSettingsProxy()
    {
        // channel_llm.go:707: the token provider receives the channel's own
        // proxy-aware client, so a proxied channel refreshes through it.
        var store = new FakeChannelTokenStore();
        var shared = new ScriptedExecutor();
        var proxied = TokenExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var source = NewSource(store, shared, factory);

        var request = Request();
        request.Settings = """{"proxy":{"type":"url","url":"http://channel.proxy:8080"}}""";

        var result = await source.RefreshAsync(request);

        Assert.Equal("access-refreshed", result.Credentials!.AccessToken);
        Assert.Single(proxied.Requests);
        Assert.Empty(shared.Requests);
        Assert.Equal("http://channel.proxy:8080", Assert.Single(factory.Requests).Url);
    }

    [Fact]
    public async Task AChannelWithoutProxyRefreshesThroughTheSharedExecutor()
    {
        var store = new FakeChannelTokenStore();
        var shared = TokenExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var source = NewSource(store, shared, factory);

        var result = await source.RefreshAsync(Request());

        Assert.Equal("access-refreshed", result.Credentials!.AccessToken);
        Assert.Single(shared.Requests);
        Assert.Empty(factory.Requests);
    }
}

/// <summary>In-memory <see cref="IChannelTokenStore"/> for the source tests.</summary>
public sealed class FakeChannelTokenStore : IChannelTokenStore
{
    public ChannelTokenRow? Current { get; set; }

    public List<ChannelTokenUpdateCall> Updates { get; } = [];

    public Func<ChannelTokenUpdateCall, ChannelTokenUpdate>? OnUpdate { get; set; }

    public Exception? Failure { get; set; }

    public Task<ChannelTokenRow?> GetAsync(long id, CancellationToken cancellationToken = default)
        => Task.FromResult(Current);

    public Task<ChannelTokenUpdate> UpdateCredentialsIfUnchangedAsync(
        long id,
        string credentials,
        DateTime expectedUpdatedAt,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var call = new ChannelTokenUpdateCall(id, credentials, expectedUpdatedAt, updatedAt);
        Updates.Add(call);

        if (Failure is not null)
        {
            return Task.FromException<ChannelTokenUpdate>(Failure);
        }

        return Task.FromResult(
            OnUpdate is not null ? OnUpdate(call) : new ChannelTokenUpdate(true, null));
    }
}

public sealed record ChannelTokenUpdateCall(
    long Id,
    string Credentials,
    DateTime ExpectedUpdatedAt,
    DateTime UpdatedAt);
