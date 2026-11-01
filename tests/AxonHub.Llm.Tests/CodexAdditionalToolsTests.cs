using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/openai/codex/outbound_additional_tools_test.go: the
/// Codex side of the Responses Lite tool definitions. A Lite request carries
/// its tools in an <c>additional_tools</c> input item rather than the top-level
/// <c>tools</c> array; the official backend is the only upstream that
/// understands that item, so it has to survive for it and stay dropped for
/// relays.
/// </summary>
public sealed class CodexAdditionalToolsTests
{
    private const string Body =
        """
        {
        	"model": "gpt-6-luna",
        	"input": [
        		{"type": "additional_tools", "role": "developer", "tools": [{"type": "custom", "name": "exec"}]},
        		{"type": "message", "role": "user", "content": "Hello"}
        	]
        }
        """;

    [Theory]
    [InlineData("official backend", "https://chatgpt.com/backend-api/codex#", true)]
    [InlineData("official backend without scheme", "chatgpt.com/backend-api/codex", true)]
    [InlineData("compatible relay", "https://relay.example.com/v1", false)]
    [InlineData(
        "relay with the official host in its path",
        "https://relay.example.com/chatgpt.com/v1",
        false)]
    [InlineData(
        "relay whose hostname ends with the official host",
        "https://chatgpt.com.relay.example/v1",
        false)]
    [InlineData(
        "relay whose hostname starts with the official host",
        "https://chatgpt.com-evil.example/v1",
        false)]
    public void TestOutboundTransformer_AdditionalToolsScope(
        string name,
        string baseUrl,
        bool preserve)
    {
        _ = name;

        var outbound = new CodexOutboundTransformer(new CodexOutboundParams
        {
            BaseUrl = baseUrl,
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = CodexTestSupport.Credentials(CodexTestSupport.TestAccessTokenWithAccountId()),
            },
        });

        var request = new ResponsesInboundTransformer().TransformRequest(
            new HttpRequestData { Body = Encoding.UTF8.GetBytes(Body) });

        var wire = outbound.TransformRequest(request);

        var root = CodexJson.Root(wire.Body!);
        var input = CodexJson.Path(root, "input");
        Assert.NotNull(input);
        Assert.Equal(JsonValueKind.Array, input!.Value.ValueKind);

        if (preserve)
        {
            Assert.Equal(2, input.Value.GetArrayLength());
            Assert.Contains("\"additional_tools\"", input.Value[0].GetRawText(), StringComparison.Ordinal);
            Assert.Contains("\"exec\"", input.Value[0].GetRawText(), StringComparison.Ordinal);
            Assert.Contains("Hello", input.Value[1].GetRawText(), StringComparison.Ordinal);

            return;
        }

        Assert.Equal(1, input.Value.GetArrayLength());
        var wireBody = Encoding.UTF8.GetString(wire.Body!);
        Assert.DoesNotContain("additional_tools", wireBody, StringComparison.Ordinal);
        Assert.Contains("Hello", wireBody, StringComparison.Ordinal);
    }
}
