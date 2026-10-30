using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Provider error bodies go through Go's <c>json.Unmarshal</c>, which repairs
/// ill-formed UTF-8 instead of failing the decode (C008).
/// </summary>
public sealed class OpenAiOutboundErrorTests
{
    [Fact]
    public void TransformErrorRepairsIllFormedUtf8InMessage()
    {
        var body = new List<byte>();
        body.AddRange(Encoding.UTF8.GetBytes("{\"error\":{\"message\":\"bad "));
        body.Add(0xFF);
        body.AddRange(Encoding.UTF8.GetBytes("\",\"type\":\"invalid_request_error\"}}"));

        var transformer = OpenAiOutboundTransformer.Create("https://upstream.test", "sk-test");
        var response = transformer.TransformError(
            new HttpClientError("POST", "https://upstream.test/v1/chat/completions", 400, "400 Bad Request", [.. body]));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("bad �", response.Detail.Message);
        Assert.Equal("invalid_request_error", response.Detail.Type);
    }
}
