using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the modelname port's format dispatch (llm/modelname/model.go): the
/// top-level <c>model</c> is read for OpenAI chat, legacy completions and Ollama
/// chat, and every other format keeps the upstream model id empty.
/// </summary>
public sealed class RequestChainModelNameTests
{
    private static StreamEvent Event(string json)
        => new() { Data = Encoding.UTF8.GetBytes(json) };

    [Fact]
    public void OllamaChatChunkReportsItsTopLevelModel()
    {
        // reportedModel groups APIFormatOllamaChat with the OpenAI chat branch;
        // every chunk carries the model name at the top level.
        var model = RequestChainPayloads.EventModel(
            Event("""{"model":"qwen3","message":{"role":"assistant","content":"Hel"},"done":false}"""),
            ApiFormats.OllamaChat);

        Assert.Equal("qwen3", model);
    }

    [Fact]
    public void LegacyCompletionChunkReportsItsTopLevelModel()
    {
        var model = RequestChainPayloads.EventModel(
            Event("""{"model":"gpt-3.5-turbo-instruct","choices":[{"text":"Hi"}]}"""),
            ApiFormats.OpenAICompletion);

        Assert.Equal("gpt-3.5-turbo-instruct", model);
    }

    [Fact]
    public void FormatsWithoutATopLevelModelKeepTheUpstreamModelEmpty()
    {
        // The Anthropic stream carries its model inside message_start.message,
        // which this metadata path does not read.
        var model = RequestChainPayloads.EventModel(
            Event("""{"type":"message_start","message":{"model":"claude-sonnet-5-5"}}"""),
            ApiFormats.AnthropicMessage);

        Assert.Empty(model);
    }

    [Fact]
    public void TheClineEnvelopeIsOnlyUnwrappedBehindTheSuccessFlag()
    {
        // jsonModel reads data.model only when success is true.
        var model = RequestChainPayloads.EventModel(
            Event("""{"data":{"model":"inside-envelope"}}"""),
            ApiFormats.OllamaChat);

        Assert.Empty(model);
    }
}
