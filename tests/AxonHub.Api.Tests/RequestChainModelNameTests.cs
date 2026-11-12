using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the modelname port (llm/modelname/model.go): FromResponse and
/// FromEvent dispatch on the request's API format and read only the provider
/// protocol metadata paths, never synthesized response content. The remaining
/// format branches recorded by C097 (6) are covered here.
/// </summary>
public sealed class RequestChainModelNameTests
{
    private static StreamEvent Event(string json, string type = "")
        => new() { Data = Encoding.UTF8.GetBytes(json), Type = type };

    private static HttpResponseData Response(
        string json,
        int status = 200,
        string? contentType = "application/json")
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (contentType is not null)
        {
            headers["Content-Type"] = [contentType];
        }

        return new HttpResponseData
        {
            StatusCode = status,
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(json),
        };
    }

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
    public void AnthropicMessageStartReportsTheNestedModel()
    {
        // modelname.FromEvent reads message.model for a message_start event;
        // the model is protocol metadata, not synthesized content.
        var model = RequestChainPayloads.EventModel(
            Event("""{"type":"message_start","message":{"model":"claude-sonnet-5-5"}}"""),
            ApiFormats.AnthropicMessage);

        Assert.Equal("claude-sonnet-5-5", model);
    }

    [Fact]
    public void AnthropicEventsOtherThanMessageStartReportNoModel()
    {
        var model = RequestChainPayloads.EventModel(
            Event("""{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hi"}}"""),
            ApiFormats.AnthropicMessage);

        Assert.Empty(model);
    }

    [Fact]
    public void TheEventTypeWinsOverTheBodyType()
    {
        // reportedModel uses event.Type when the transport supplied one.
        var model = RequestChainPayloads.EventModel(
            Event("""{"type":"content_block_delta","message":{"model":"nested-model"}}""", "message_start"),
            ApiFormats.AnthropicMessage);

        Assert.Equal("nested-model", model);
    }

    [Theory]
    [InlineData("response.created")]
    [InlineData("response.in_progress")]
    [InlineData("response.completed")]
    [InlineData("response.incomplete")]
    [InlineData("response.failed")]
    [InlineData("response.cancelled")]
    [InlineData("response.canceled")]
    public void ResponsesLifecycleEventsReportTheResponseModel(string eventType)
    {
        var json = string.Concat(
            "{\"type\":\"", eventType, "\",\"response\":{\"model\":\"gpt-5.4\"}}");
        var model = RequestChainPayloads.EventModel(Event(json), ApiFormats.OpenAIResponse);

        Assert.Equal("gpt-5.4", model);
    }

    [Fact]
    public void ResponsesContentEventsReportNoModel()
    {
        var model = RequestChainPayloads.EventModel(
            Event("""{"type":"response.output_text.delta","delta":"Hi"}"""),
            ApiFormats.OpenAIResponse);

        Assert.Empty(model);
    }

    [Fact]
    public void CodexImageGenerationStreamsReportTheResponseModel()
    {
        // Codex keeps the image API format on the request while returning
        // Responses SSE, so image formats share the lifecycle rule.
        var model = RequestChainPayloads.EventModel(
            Event("""{"type":"response.completed","response":{"model":"gpt-image-1"}}"""),
            ApiFormats.OpenAIImageGeneration);

        Assert.Equal("gpt-image-1", model);
    }

    [Fact]
    public void GeminiStreamsReportModelVersion()
    {
        var model = RequestChainPayloads.EventModel(
            Event("""{"candidates":[],"modelVersion":"gemini-2.5-pro"}"""),
            ApiFormats.GeminiContents);

        Assert.Equal("gemini-2.5-pro", model);
    }

    [Fact]
    public void GeminiFallsBackToTheNestedModelVersion()
    {
        // jsonModel tries modelVersion first, then response.modelVersion.
        var model = RequestChainPayloads.EventModel(
            Event("""{"response":{"modelVersion":"gemini-2.5-flash"}}"""),
            ApiFormats.GeminiEmbedding);

        Assert.Equal("gemini-2.5-flash", model);
    }

    [Fact]
    public void ResponsesReportTheTopLevelModel()
    {
        var model = RequestChainPayloads.ResponseModel(
            Response("""{"model":"gpt-5.4","output":[]}"""),
            ApiFormats.OpenAIResponse);

        Assert.Equal("gpt-5.4", model);
    }

    [Fact]
    public void AnthropicResponsesReportTheTopLevelModel()
    {
        var model = RequestChainPayloads.ResponseModel(
            Response("""{"type":"message","model":"claude-sonnet-5-5"}"""),
            ApiFormats.AnthropicMessage);

        Assert.Equal("claude-sonnet-5-5", model);
    }

    [Fact]
    public void NonStreamingFormatsOutsideTheReferenceListReportNoModel()
    {
        // reportedModel has no default branch: speech, native image and the
        // AiSDK formats never report an upstream model.
        Assert.Empty(RequestChainPayloads.ResponseModel(
            Response("""{"model":"aisdk-model"}"""),
            ApiFormats.AiSDKText));

        Assert.Empty(RequestChainPayloads.ResponseModel(
            Response("""{"model":"speech-model"}"""),
            ApiFormats.OpenAISpeech));
    }

    [Fact]
    public void StreamingFormatsOutsideTheReferenceListReportNoModel()
    {
        Assert.Empty(RequestChainPayloads.EventModel(
            Event("""{"model":"aisdk-model"}"""),
            ApiFormats.AiSDKText));
    }

    [Fact]
    public void TranscriptionRequiresAJsonMediaType()
    {
        // The transcription/translation body is text-bearing content that may
        // coincide with JSON, so FromResponse gates it on the media type.
        var text = Response(
            """{"model":"whisper-1"}""",
            contentType: "text/plain");
        Assert.Empty(RequestChainPayloads.ResponseModel(text, ApiFormats.OpenAITranscription));

        var json = Response(
            """{"model":"whisper-1"}""",
            contentType: "application/json; charset=utf-8");
        Assert.Equal("whisper-1", RequestChainPayloads.ResponseModel(json, ApiFormats.OpenAITranscription));

        var vendorJson = Response(
            """{"model":"whisper-1"}""",
            contentType: "application/vnd.openai+json");
        Assert.Equal(
            "whisper-1",
            RequestChainPayloads.ResponseModel(vendorJson, ApiFormats.OpenAITranslation));
    }

    [Fact]
    public void ErrorResponsesReportNoModel()
    {
        var response = Response(
            """{"model":"error-model"}""",
            status: 400);

        Assert.Empty(RequestChainPayloads.ResponseModel(response, ApiFormats.OpenAIResponse));
    }

    [Fact]
    public void TheRequestApiFormatWinsOverTheExecutionFormat()
    {
        var response = Response("""{"modelVersion":"gemini-3"}""");
        response.Request = new HttpRequestData { ApiFormat = ApiFormats.GeminiContents };

        // Dispatching on the execution format reads the OpenAI chat path and
        // finds no model; the response's request format selects Gemini.
        Assert.Empty(RequestChainPayloads.ResponseModel(
            Response("""{"modelVersion":"gemini-3"}"""),
            ApiFormats.OpenAIChatCompletion));
        Assert.Equal(
            "gemini-3",
            RequestChainPayloads.ResponseModel(response, ApiFormats.OpenAIChatCompletion));
    }

    [Fact]
    public void TheClineEnvelopeIsOnlyUnwrappedBehindTheSuccessFlag()
    {
        // jsonModel reads data.model only when success is true.
        Assert.Empty(RequestChainPayloads.EventModel(
            Event("""{"data":{"model":"inside-envelope"}}"""),
            ApiFormats.OllamaChat));

        Assert.Equal(
            "inside-envelope",
            RequestChainPayloads.EventModel(
                Event("""{"success":true,"data":{"model":"inside-envelope"}}"""),
                ApiFormats.OllamaChat));
    }

    [Fact]
    public void BinaryAudioChunksCarryNoMetadata()
    {
        Assert.Empty(RequestChainPayloads.EventModel(
            Event("""{"model":"should-not-be-read"}""", "audio/pcm"),
            ApiFormats.OpenAIChatCompletion));
    }

    [Theory]
    [InlineData("""{"model":123}""")]
    [InlineData("""{"model":"   "}""")]
    [InlineData("""{"model":"bad\u0001model"}""")]
    public void InvalidModelValuesAreDropped(string body)
    {
        Assert.Empty(RequestChainPayloads.ResponseModel(
            Response(body),
            ApiFormats.OpenAIChatCompletion));
    }

    [Fact]
    public void ModelsLongerThan512BytesAreDropped()
    {
        var body = """{"model":""" + "\"" + new string('a', 513) + "\"}";

        Assert.Empty(RequestChainPayloads.ResponseModel(
            new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes(body),
            },
            ApiFormats.OpenAIChatCompletion));
    }
}
