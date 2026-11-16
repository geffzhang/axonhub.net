using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the §194 wiring: fenno and antigravity outbounds take their token from
/// the per-channel token grain when a grain factory is present, and the grain
/// seed matches the reference's provider recipe
/// (channel_llm.go:1208 codex/fenno, :1375-1391 antigravity,
/// transformer.go:94-122 initTokenProvider).
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class TokenGrainOutboundWiringTests(GrainClusterFixture fixture)
{
    private static readonly DateTime UpdatedAt = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private static LlmRequest ResponseRequest()
        => new()
        {
            Model = "gpt-5.2-codex",
            ApiFormat = ApiFormats.OpenAIResponse,
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    private static LlmRequest GeminiRequest()
        => new()
        {
            Model = "gemini-2.5-flash",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    [Fact]
    public void FennoOAuthChannelsTakeTheirTokenFromTheGrain()
    {
        fixture.TokenSource.Reset();

        // The reference's fail-fast switch does not list TypeFenno, so a fenno
        // channel needs a stored key even though the oauth object wins
        // (channel_llm.go:662-693). IsOAuth hides the legacy apiKey from
        // GetAllAPIKeys, so the key has to live in the apiKeys list.
        const string Credentials = """
            {"apiKeys":["sk-fenno"],"oauth":{"client_id":"codex-client","access_token":"grain-access","refresh_token":"grain-refresh","expires_at":"2020-01-01T00:00:00Z","token_type":"Bearer"}}
            """;

        var candidate = new ChannelCandidate
        {
            ChannelId = 9941,
            ChannelName = "fenno-grain",
            Type = ChannelOutboundFactory.FennoChannelType,
            BaseUrl = "https://api.fenno.ai",
            Credentials = Credentials,
            UpdatedAt = UpdatedAt,
        };

        var transformer = Assert.IsType<CodexOutboundTransformer>(
            ChannelOutboundFactory.Create(
                candidate,
                ResponseRequest(),
                DateTimeOffset.UnixEpoch,
                grainFactory: fixture.Cluster.Client));

        Assert.IsType<GrainTokenGetter>(transformer.TokenProvider);

        var httpRequest = transformer.TransformRequest(ResponseRequest());
        Assert.Equal("grain-access", httpRequest.Auth?.ApiKey);

        // The expired seed sent the grain through its refresh; the fake source
        // echoed it, so no codex HTTP refresh ran on this path.
        var refresh = Assert.Single(fixture.TokenSource.Requests);
        Assert.Equal(ChannelOutboundFactory.FennoChannelType, refresh.ChannelType);
        Assert.Equal("grain-refresh", refresh.Credentials.RefreshToken);
        Assert.Equal(Credentials, refresh.CredentialsJson);
    }

    [Fact]
    public void AntigravityChannelsTakeTheirTokenFromTheGrain()
    {
        fixture.TokenSource.Reset();

        const string Credentials = """{"apiKey":"refresh-token|project-123"}""";

        var candidate = new ChannelCandidate
        {
            ChannelId = 9942,
            ChannelName = "antigravity-grain",
            Type = ChannelOutboundFactory.AntigravityChannelType,
            BaseUrl = "https://antigravity.googleapis.com",
            Credentials = Credentials,
            UpdatedAt = UpdatedAt,
        };

        var transformer = Assert.IsType<AntigravityTransformer>(
            ChannelOutboundFactory.Create(
                candidate,
                GeminiRequest(),
                DateTimeOffset.UnixEpoch,
                grainFactory: fixture.Cluster.Client));

        var httpRequest = transformer.TransformRequest(GeminiRequest());

        // The seed has no access token, so the empty bearer proves the grain
        // answered instead of the transformer's local Google token exchange.
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth!.Type);
        Assert.Equal(string.Empty, httpRequest.Auth.ApiKey);

        var refresh = Assert.Single(fixture.TokenSource.Requests);
        Assert.Equal(ChannelOutboundFactory.AntigravityChannelType, refresh.ChannelType);
        Assert.Equal("refresh-token", refresh.Credentials.RefreshToken);
        Assert.Equal(AntigravityConstants.ClientID, refresh.Credentials.ClientId);
        Assert.Equal(AntigravityConstants.Scopes, refresh.Credentials.Scopes);
        Assert.Equal(Credentials, refresh.CredentialsJson);

        // The envelope still extracts the project from the legacy key.
        using var envelope = JsonDocument.Parse(httpRequest.Body!);
        Assert.Equal("project-123", envelope.RootElement.GetProperty("project").GetString());
    }

    [Fact]
    public void AnAntigravityKeyWithoutARefreshTokenKeepsTheLocalProvider()
    {
        // initTokenProvider returns early when parts[0] is empty, so the
        // reference's GetTokenProvider() stays nil and setupAutoRefresh never
        // runs; the grain must not adopt the outbound in that case.
        fixture.TokenSource.Reset();

        var candidate = new ChannelCandidate
        {
            ChannelId = 9943,
            ChannelName = "antigravity-norefresh",
            Type = ChannelOutboundFactory.AntigravityChannelType,
            BaseUrl = "https://antigravity.googleapis.com",
            Credentials = """{"apiKey":"|project-123"}""",
            UpdatedAt = UpdatedAt,
        };

        var transformer = Assert.IsType<AntigravityTransformer>(
            ChannelOutboundFactory.Create(
                candidate,
                GeminiRequest(),
                DateTimeOffset.UnixEpoch,
                grainFactory: fixture.Cluster.Client));

        var error = Assert.Throws<InvalidOperationException>(
            () => transformer.TransformRequest(GeminiRequest()));

        Assert.Equal("no OAuth token provider configured", error.Message);
        Assert.Empty(fixture.TokenSource.Requests);
    }
}
