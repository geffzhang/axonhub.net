using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the antigravity branches of the channel transformer switch
/// (channel_llm.go:670-674, 1375-1391): the single legacy apiKey carries
/// refreshToken|projectID, enabled key lists are ignored, and the outbound is
/// the envelope-wrapping OAuth-only transformer.
/// </summary>
public sealed class AntigravityChannelWiringTests
{
    private const string BaseUrl = "https://antigravity.googleapis.com";

    private static ChannelCandidate Candidate(string credentials) => new()
    {
        ChannelId = 59,
        ChannelName = "antigravity-wiring",
        ActualModel = "gemini-2.5-flash",
        Type = ChannelOutboundFactory.AntigravityChannelType,
        BaseUrl = BaseUrl,
        Credentials = credentials,
    };

    private static LlmRequest Request()
        => new()
        {
            Model = "gemini-2.5-flash",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
        };

    /// <summary>Scripts the OAuth token endpoint; everything else 404s.</summary>
    private static ScriptedExecutor TokenExecutor(string accessToken = "wiring-access")
    {
        var tokenJson = $$"""{"access_token":"{{accessToken}}","token_type":"Bearer","expires_in":3600}""";

        return new ScriptedExecutor
        {
            OnDo = (_, request, _) => request.Url == AntigravityConstants.TokenURL
                ? new HttpResponseData
                {
                    StatusCode = 200,
                    Body = Encoding.UTF8.GetBytes(tokenJson),
                }
                : new HttpResponseData { StatusCode = 404 },
        };
    }

    private static AntigravityTransformer Create(
        string credentials,
        string? baseUrl = BaseUrl)
    {
        var candidate = Candidate(credentials);
        candidate.BaseUrl = baseUrl;

        return Assert.IsType<AntigravityTransformer>(
            ChannelOutboundFactory.Create(
                candidate,
                Request(),
                DateTimeOffset.UnixEpoch,
                TokenExecutor()));
    }

    [Fact]
    public void AntigravityChannelBuildsTheEnvelopeTransformerWithOAuthAuth()
    {
        var transformer = Create("""{"apiKey":"refresh-token|project-123"}""");

        Assert.Equal(ApiFormats.GeminiContents, transformer.ApiFormat);

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(BaseUrl + "/v1internal:generateContent", httpRequest.Url);

        // OAuth-only: the bearer token comes from the token provider and
        // X-Goog-Api-Key must be absent entirely.
        Assert.NotNull(httpRequest.Auth);
        Assert.Equal(AuthTypes.Bearer, httpRequest.Auth.Type);
        Assert.Equal("wiring-access", httpRequest.Auth.ApiKey);
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(httpRequest.Headers, "X-Goog-Api-Key"));

        // The envelope carries the project extracted from the legacy key and
        // the Gemini-shaped inner request.
        using var envelope = JsonDocument.Parse(httpRequest.Body!);
        var root = envelope.RootElement;
        Assert.Equal("project-123", root.GetProperty("project").GetString());
        Assert.Equal("gemini-2.5-flash", root.GetProperty("model").GetString());
        Assert.Equal("agent", root.GetProperty("requestType").GetString());
        Assert.Equal("antigravity", root.GetProperty("userAgent").GetString());
        Assert.StartsWith("agent-", root.GetProperty("requestId").GetString());

        var contents = root.GetProperty("request").GetProperty("contents");
        var first = Assert.Single(contents.EnumerateArray());
        Assert.Equal("Hello", first.GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void AntigravityChannelKeepsACustomBaseUrl()
    {
        var transformer = Create(
            """{"apiKey":"refresh-token|project-123"}""",
            "https://custom.antigravity.example.com");

        var httpRequest = transformer.TransformRequest(Request());
        Assert.Equal(
            "https://custom.antigravity.example.com/v1internal:generateContent",
            httpRequest.Url);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"   "}""")]
    public void AChannelWithoutAnApiKeyIsRejectedBeforeTheTransformerIsBuilt(string credentials)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate(credentials),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel antigravity-wiring", error.Message);
    }

    [Fact]
    public void AnEnabledKeyListDoesNotSatisfyAntigravity()
    {
        // channel_llm.go:670-674 reads c.Credentials.APIKey only; the apiKeys
        // list never satisfies an antigravity channel.
        var error = Assert.Throws<InvalidOperationException>(
            () => ChannelOutboundFactory.Create(
                Candidate("""{"apiKeys":["some-key"]}"""),
                Request(),
                DateTimeOffset.UnixEpoch));

        Assert.Equal("missing api key for channel antigravity-wiring", error.Message);
    }
}
