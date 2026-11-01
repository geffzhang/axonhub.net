using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/codex/alpha_search_test.go. The reference
/// reads the trace session from the context; the port supplies it through the
/// transformer's SessionIdProvider seam (C062).
/// </summary>
public sealed class CodexAlphaSearchTests
{
    [Theory]
    [InlineData(
        "header before body and context",
        "header-session",
        """{"id":"body-session","model":"client-model","commands":{"search_query":[]}}""",
        "header-session")]
    [InlineData(
        "body before context",
        "",
        """{"id":"body-session","model":"client-model","commands":{"search_query":[]}}""",
        "body-session")]
    [InlineData(
        "context fallback",
        "",
        """{"model":"client-model","commands":{"search_query":[]}}""",
        "context-session")]
    public void TestAlphaSearchRequestSessionIDPrecedence(
        string name,
        string headerSessionValue,
        string body,
        string expectedSession)
    {
        _ = name;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (headerSessionValue.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, headerSessionValue);
        }

        var outbound = NewAlphaSearchTestTransformer(() => "context-session");

        var request = outbound.TransformRequest(new LlmRequest
        {
            Model = "mapped-model",
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
            RawRequest = new HttpRequestData { Headers = headers },
            AlphaSearch = new AlphaSearchRequest { Body = Encoding.UTF8.GetBytes(body) },
        });

        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/alpha/search",
            request.Url);
        Assert.Equal(
            expectedSession,
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.SessionHeaderHyphen));
        Assert.Equal(
            expectedSession,
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.ThreadIdHeader));
        Assert.Equal(
            expectedSession + ":0",
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.WindowIdHeader));
        Assert.NotEmpty(
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.ClientRequestIdHeader));
        Assert.Equal(
            CodexConstants.FabricatedBetaFeatures,
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.BetaFeaturesHeader));
        Assert.Equal(
            CodexConstants.AxonHubOriginator,
            HttpClientUtils.HeaderGet(request.Headers, "Originator"));
        Assert.Equal(
            CodexConstants.DefaultVersion,
            HttpClientUtils.HeaderGet(request.Headers, "Version"));
        Assert.Equal(
            CodexTestSupport.TestChatAccountId,
            HttpClientUtils.HeaderGet(request.Headers, "Chatgpt-Account-Id"));
        Assert.Equal(
            "application/json",
            HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(
            "application/json",
            HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(AuthTypes.Bearer, request.Auth?.Type);

        var metadata = LlmJson.Deserialize<TurnMetadata>(
            HttpClientUtils.HeaderGet(request.Headers, CodexHeaders.TurnMetadataHeader));
        Assert.NotNull(metadata);
        Assert.Equal(expectedSession, metadata!.SessionId);
        Assert.Equal(expectedSession, metadata.ThreadId);

        var requestBody = CodexJson.Root(request.Body!);
        Assert.Equal("mapped-model", CodexJson.PathString(requestBody, "model"));
    }

    private static CodexOutboundTransformer NewAlphaSearchTestTransformer(
        Func<string?>? sessionId = null)
        => new(new CodexOutboundParams
        {
            BaseUrl = CodexOutboundTransformer.CodexBaseUrl,
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = CodexTestSupport.Credentials(CodexTestSupport.TestAccessTokenWithAccountId()),
            },
            SessionIdProvider = sessionId,
        });
}
