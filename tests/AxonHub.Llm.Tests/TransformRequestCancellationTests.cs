using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.Xai.Subscription;

// The Pipeline type lives in the AxonHub.Llm.Pipeline namespace, whose simple
// name shadows it from inside AxonHub.Llm.Tests. The alias keeps the reference
// type name readable in the ported tests.
using PipelineType = AxonHub.Llm.Pipeline.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Difference C094 (6): Go passes <c>ctx</c> into <c>TransformRequest</c>, so a
/// client disconnect cancels an OAuth token refresh. The port's transformer
/// interface is synchronous, so the caller's token rides on
/// <see cref="LlmRequest.RequestCancellationToken"/>; these tests lock the
/// pipeline and every OAuth transformer to that seam.
/// </summary>
public sealed class TransformRequestCancellationTests
{
    private sealed class RecordingTokenGetter : ITokenGetter
    {
        public CancellationToken LastToken { get; private set; }

        public int Calls { get; private set; }

        public OAuthCredentials Get(CancellationToken cancellationToken)
        {
            Calls++;
            LastToken = cancellationToken;
            return new OAuthCredentials
            {
                AccessToken = "recorded-access-token",
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            };
        }
    }

    private static LlmRequest ChatRequest() => new()
    {
        Model = "test-model",
        MaxTokens = 64,
        Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
    };

    [Fact]
    public void PipelinePassesItsCancellationTokenToTheOutboundTransform()
    {
        using var cts = new CancellationTokenSource();

        CancellationToken observed = default;
        var outbound = new MockOutbound
        {
            TransformRequestFn = request =>
            {
                observed = request!.RequestCancellationToken;
                return new HttpRequestData();
            },
        };
        var executor = new MockExecutor
        {
            DoFn = (_, _) => new HttpResponseData(),
        };

        var pipeline = new PipelineType(executor, new MockInbound(), outbound);
        pipeline.Process(new HttpRequestData(), cts.Token);

        Assert.True(observed.CanBeCanceled);
        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public void CodexRefreshesWithTheRequestToken()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new RecordingTokenGetter();
        var transformer = new CodexOutboundTransformer(new CodexOutboundParams
        {
            BaseUrl = CodexOutboundTransformer.CodexBaseUrl,
            TokenProvider = tokens,
        });

        var request = ChatRequest();
        request.RequestCancellationToken = cts.Token;
        transformer.TransformRequest(request);

        Assert.Equal(1, tokens.Calls);
        Assert.Equal(cts.Token, tokens.LastToken);
    }

    [Fact]
    public void CodexAlphaSearchRefreshesWithTheRequestToken()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new RecordingTokenGetter();
        var transformer = new CodexOutboundTransformer(new CodexOutboundParams
        {
            BaseUrl = CodexOutboundTransformer.CodexBaseUrl,
            TokenProvider = tokens,
        });

        var request = new LlmRequest
        {
            Model = "mapped-model",
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
            AlphaSearch = new AlphaSearchRequest
            {
                Body = Encoding.UTF8.GetBytes("""{"model":"client-model"}"""),
            },
            RequestCancellationToken = cts.Token,
        };
        transformer.TransformRequest(request);

        Assert.Equal(1, tokens.Calls);
        Assert.Equal(cts.Token, tokens.LastToken);
    }

    [Fact]
    public void ClaudeCodeRefreshesWithTheRequestToken()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new RecordingTokenGetter();
        var transformer = new ClaudeCodeOutboundTransformer(new ClaudeCodeParams
        {
            TokenProvider = tokens,
            AccountIdentity = "42",
        });

        var request = ChatRequest();
        request.Model = "claude-sonnet-4-5";
        request.RequestCancellationToken = cts.Token;
        transformer.TransformRequest(request);

        Assert.Equal(1, tokens.Calls);
        Assert.Equal(cts.Token, tokens.LastToken);
    }

    [Fact]
    public void SubscriptionRefreshesWithTheRequestToken()
    {
        using var cts = new CancellationTokenSource();
        var tokens = new RecordingTokenGetter();
        var transformer = SubscriptionOutboundTransformer.NewOutboundTransformer(tokens);

        var request = ChatRequest();
        request.RequestCancellationToken = cts.Token;
        transformer.TransformRequest(request);

        Assert.Equal(1, tokens.Calls);
        Assert.Equal(cts.Token, tokens.LastToken);
    }

    [Fact]
    public void AntigravityRefreshesWithTheRequestToken()
    {
        AntigravityVersion.ResetForTests();
        using var cts = new CancellationTokenSource();
        var executor = new RecordingRefreshingExecutor();
        var transformer = AntigravityTransformer.Create(
            new AntigravityConfig
            {
                BaseURL = "https://api.antigravity.dev",
                APIKey = "test-refresh-token|test-project-id",
                Project = "my-project",
            },
            new AntigravityTransformerOptions { HttpClient = executor });

        var request = ChatRequest();
        request.Model = "gemini-2.5-flash";
        request.RequestCancellationToken = cts.Token;
        transformer.TransformRequest(request);

        Assert.NotNull(executor.LastToken);
        Assert.Equal(cts.Token, executor.LastToken!.Value);
    }

    [Fact]
    public void TheTransformStageTokenIsNotSerialized()
    {
        using var cts = new CancellationTokenSource();
        var request = ChatRequest();
        request.RequestCancellationToken = cts.Token;

        var json = JsonSerializer.Serialize(request);

        Assert.DoesNotContain("RequestCancellationToken", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cancellationToken", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records the token the OAuth refresh reached the wire with.</summary>
    private sealed class RecordingRefreshingExecutor : IExecutor
    {
        public CancellationToken? LastToken { get; private set; }

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        {
            LastToken = cancellationToken;
            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = ["application/json"],
            };
            return new HttpResponseData
            {
                StatusCode = 200,
                Headers = headers,
                Body = Encoding.UTF8.GetBytes(
                    """{"access_token":"antigravity-access","token_type":"Bearer","expires_in":3600}"""),
            };
        }

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => throw new NotSupportedException("OAuth providers never open a stream");
    }
}
