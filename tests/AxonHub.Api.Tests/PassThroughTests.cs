using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the request half of internal/server/orchestrator/pass_through_test.go:
/// body replay with the mapped model patched in, the format and stream gates,
/// the outbound policy veto, the Codex header allowlist and the User-Agent
/// policy.
/// </summary>
/// <remarks>
/// The response and stream half lives in
/// <see cref="PassThroughStreamTests"/>; prompt-protection patching of the
/// replayed body lives in <see cref="PassThroughPromptProtectionTests"/>.
/// </remarks>
public sealed class PassThroughTests
{
    private const string SettingsEnabled = "{\"passThroughBody\":true}";

    // ------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------

    /// <summary>Minimal outbound transformer without a pass-through policy.</summary>
    private sealed class StubOutboundTransformer(string apiFormat) : IOutboundTransformer
    {
        public string ApiFormat { get; } = apiFormat;

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            _ = request;
            return new HttpRequestData { ApiFormat = ApiFormat };
        }

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }

    /// <summary>Port of the reference's passthroughPolicyOutbound.</summary>
    private sealed class PolicyOutboundTransformer(string apiFormat, bool allow)
        : IOutboundTransformer, IPassThroughBodyPolicy
    {
        public string ApiFormat { get; } = apiFormat;

        public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
        {
            _ = llmRequest;
            _ = providerRequest;
            return allow;
        }

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            _ = request;
            return new HttpRequestData { ApiFormat = ApiFormat };
        }

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static Dictionary<string, List<string>> Headers(params (string Key, string Value)[] entries)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            headers[key] = [value];
        }

        return headers;
    }

    /// <summary>Builds the unified request the inbound transformer would produce.</summary>
    private static LlmRequest UnifiedRequest(
        string apiFormat,
        string model,
        string rawBody,
        bool? stream = null,
        string? rawContentType = null,
        string? rawApiFormat = null,
        Dictionary<string, List<string>>? rawHeaders = null)
    {
        if (rawContentType is not null)
        {
            rawHeaders ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
            rawHeaders["Content-Type"] = [rawContentType];
        }

        return new LlmRequest
        {
            Model = model,
            ApiFormat = apiFormat,
            Stream = stream,
            RawRequest = new HttpRequestData
            {
                Method = "POST",
                Url = "http://gateway.test/v1",
                Headers = rawHeaders,
                ApiFormat = rawApiFormat ?? apiFormat,
                Body = Bytes(rawBody),
            },
        };
    }

    /// <summary>
    /// Runs one outbound transform so the middleware sees the same state the
    /// reference constructs directly, then returns the transformer.
    /// </summary>
    private static PersistentOutboundTransformer Outbound(
        LlmRequest llmRequest,
        string? channelSettings = null,
        string? rawProviderApiFormat = null,
        IOutboundTransformer? transformer = null)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = 1,
            ChannelName = "test-channel",
            ActualModel = llmRequest.Model,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            PassThroughSettings = ChannelPassThroughSettings.Parse(channelSettings),
        };

        var format = rawProviderApiFormat ?? llmRequest.ApiFormat ?? ApiFormats.OpenAIChatCompletion;

        var outbound = new PersistentOutboundTransformer(
            [candidate],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            (_, _, _) => transformer ?? new StubOutboundTransformer(format));

        outbound.TransformRequest(llmRequest);
        return outbound;
    }

    private static HttpRequestData ProviderRequest(
        string apiFormat,
        string body,
        string? contentType = null)
    {
        Dictionary<string, List<string>>? headers = null;
        if (contentType is not null)
        {
            headers = Headers(("Content-Type", contentType));
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = "http://upstream.test/v1",
            Headers = headers,
            ContentType = contentType ?? string.Empty,
            ApiFormat = apiFormat,
            Body = Bytes(body),
        };
    }

    private static (HttpRequestData Processed, PersistentOutboundTransformer Outbound) RunBody(
        PersistentOutboundTransformer outbound,
        HttpRequestData request,
        PassThroughSettings? settings = null)
        => (
            new PassThroughRequestBodyMiddleware(
                    outbound,
                    settings ?? PassThroughSettings.Disabled)
                .TransformOutboundRawRequest(request),
            outbound);

    private static string? BodyModel(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body ?? []);
        return document.RootElement.TryGetProperty("model", out var model)
            ? model.GetString()
            : null;
    }

    private static JsonElement BodyElement(HttpRequestData request)
    {
        using var document = JsonDocument.Parse(request.Body ?? []);
        return document.RootElement.Clone();
    }

    // ------------------------------------------------------------------
    // isPassThroughEnabled
    // ------------------------------------------------------------------

    [Fact]
    public void ChannelFlagEnablesWithMatchingFormats()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        _ = RunBody(outbound, ProviderRequest(ApiFormats.OpenAIChatCompletion, "{}"));

        Assert.True(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Fact]
    public void ChannelFalseOverridesGlobalTrue()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest, "{\"passThroughBody\":false}");
        _ = RunBody(outbound, ProviderRequest(ApiFormats.OpenAIChatCompletion, "{}"));

        Assert.False(PassThrough.IsEnabled(outbound, new PassThroughSettings(true, false)));
    }

    [Fact]
    public void ChannelNullInheritsGlobalTrue()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest);
        _ = RunBody(outbound, ProviderRequest(ApiFormats.OpenAIChatCompletion, "{}"));

        Assert.True(PassThrough.IsEnabled(outbound, new PassThroughSettings(true, false)));
    }

    [Fact]
    public void MissingRawProviderRequestDisables()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);

        Assert.False(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Fact]
    public void EmptyRawProviderApiFormatDisables()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        _ = RunBody(outbound, ProviderRequest(string.Empty, "{}"));

        Assert.False(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Fact]
    public void MismatchedApiFormatDisables()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        _ = RunBody(outbound, ProviderRequest(ApiFormats.AnthropicMessage, "{}"));

        Assert.False(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Fact]
    public void NilAndFalseStreamAlign()
    {
        // An absent client stream intent (nil) aligns with an explicit false:
        // pass-through only requires the two truthiness values to agree.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""",
            stream: false);

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.OriginalRequestStream = null;
        _ = RunBody(outbound, ProviderRequest(ApiFormats.OpenAIChatCompletion, "{}"));

        Assert.Null(outbound.OriginalRequestStream);
        Assert.True(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Fact]
    public void FalseAndNilStreamAlign()
    {
        // The mirror case: an explicit false intent and an absent effective
        // stream are still aligned.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.OriginalRequestStream = false;
        _ = RunBody(outbound, ProviderRequest(ApiFormats.OpenAIChatCompletion, "{}"));

        Assert.True(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Fact]
    public void ForcedStreamingDisables()
    {
        // The client did not ask to stream, but the execution was forced to.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias"}""",
            stream: true);

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.OriginalRequestStream = null;
        _ = RunBody(outbound, ProviderRequest(ApiFormats.OpenAIChatCompletion, "{}"));

        Assert.False(PassThrough.IsEnabled(outbound, PassThroughSettings.Disabled));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, false, true)]
    [InlineData(false, null, true)]
    [InlineData(true, true, true)]
    [InlineData(null, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, null, false)]
    [InlineData(true, false, false)]
    public void StreamAlignedMatchesGoTruthiness(bool? original, bool? effective, bool aligned)
        => Assert.Equal(aligned, PassThrough.StreamAligned(original, effective));

    // ------------------------------------------------------------------
    // Body pass-through
    // ------------------------------------------------------------------

    [Fact]
    public void PreservesMappedModelAndRawBody()
    {
        // Port of TestApplyPassThroughBodyPreservesMappedModel.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias","messages":[{"role":"user","content":"hi"}],"temperature":0.4}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var request = ProviderRequest(
            ApiFormats.OpenAIChatCompletion,
            """{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}]}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.True(outbound.PassThroughApplied);
        Assert.Equal("gpt-4o", BodyModel(processed));
        Assert.Equal(0.4, BodyElement(processed).GetProperty("temperature").GetDouble());
        Assert.Equal(
            """{"model":"my-alias","messages":[{"role":"user","content":"hi"}],"temperature":0.4}""",
            Encoding.UTF8.GetString(outbound.CurrentLlmRequest!.RawRequest!.Body!));

        // The replayed body is a copy: mutating it never touches the raw body.
        processed.Body![0] = (byte)'[';
        Assert.Equal(
            """{"model":"my-alias","messages":[{"role":"user","content":"hi"}],"temperature":0.4}""",
            Encoding.UTF8.GetString(outbound.CurrentLlmRequest.RawRequest.Body!));
    }

    public static TheoryData<string> OpenAIJsonFormats => new()
    {
        ApiFormats.OpenAIChatCompletion,
        ApiFormats.OpenAICompletion,
        ApiFormats.OpenAIResponse,
        ApiFormats.OpenAIResponseCompact,
        ApiFormats.OpenAIEmbedding,
        ApiFormats.OpenAIModeration,
        ApiFormats.OpenAIAlphaSearch,
        ApiFormats.OpenAIImageGeneration,
        ApiFormats.OpenAIVideo,
        ApiFormats.OpenAISpeech,
    };

    [Theory]
    [MemberData(nameof(OpenAIJsonFormats))]
    public void JsonRoutingContractsReplaceTheBody(string format)
    {
        var llmRequest = UnifiedRequest(
            format,
            "provider-model",
            """{"model":"client-alias","official_future_field":{"enabled":true}}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var request = ProviderRequest(
            format,
            """{"model":"provider-model","converted_only":true}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.True(outbound.PassThroughApplied);
        Assert.Equal("provider-model", BodyModel(processed));
        Assert.True(BodyElement(processed).GetProperty("official_future_field").GetProperty("enabled").GetBoolean());
        Assert.False(BodyElement(processed).TryGetProperty("converted_only", out _));
    }

    [Theory]
    [MemberData(nameof(OpenAIJsonFormats))]
    public void ConversionMismatchKeepsTheOutboundBody(string format)
    {
        var llmRequest = UnifiedRequest(
            format,
            "provider-model",
            """{"model":"client-alias","original_only":true}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var request = ProviderRequest(
            ApiFormats.AnthropicMessage,
            """{"model":"provider-model","converted_only":true}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.False(outbound.PassThroughApplied);
        Assert.Same(request, processed);
        Assert.True(BodyElement(processed).GetProperty("converted_only").GetBoolean());
        Assert.False(BodyElement(processed).TryGetProperty("original_only", out _));
    }

    [Fact]
    public void SkipsWhenOutboundPolicyRejects()
    {
        // Port of TestApplyPassThroughBodySkipsWhenOutboundPolicyRejects.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIResponse,
            "gpt-5.4-mini",
            """{"model":"gpt-5.4-mini","input":"hi","stream":true,"temperature":0.4}""",
            stream: true);

        var outbound = Outbound(
            llmRequest,
            SettingsEnabled,
            transformer: new PolicyOutboundTransformer(ApiFormats.OpenAIResponse, allow: false));
        var request = ProviderRequest(
            ApiFormats.OpenAIResponse,
            """{"model":"gpt-5.4-mini","input":[{"role":"user","content":"hi"}],"stream":true}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.False(outbound.PassThroughApplied);
        Assert.Same(request, processed);
        Assert.False(BodyElement(processed).TryGetProperty("temperature", out _));
    }

    [Fact]
    public void PolicyAllowingPassThroughApplies()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIResponse,
            "gpt-5.4-mini",
            """{"model":"gpt-5.4-mini","input":"hi","stream":true,"temperature":0.4}""",
            stream: true);

        var outbound = Outbound(
            llmRequest,
            SettingsEnabled,
            transformer: new PolicyOutboundTransformer(ApiFormats.OpenAIResponse, allow: true));
        var request = ProviderRequest(
            ApiFormats.OpenAIResponse,
            """{"model":"gpt-5.4-mini","input":[{"role":"user","content":"hi"}],"stream":true}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.True(outbound.PassThroughApplied);
        Assert.Equal(0.4, BodyElement(processed).GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void SkipsWhenStreamParameterChanges()
    {
        // Port of TestApplyPassThroughBodySkipsPassThroughWhenSupportedStreamParameterChanges.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias","stream":false,"messages":[{"role":"user","content":"hi"}],"temperature":0.4}""",
            stream: true);

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.OriginalRequestStream = null;
        var request = ProviderRequest(
            ApiFormats.OpenAIChatCompletion,
            """{"model":"gpt-4o","stream":true,"messages":[{"role":"user","content":"hi"}]}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.False(outbound.PassThroughApplied);
        Assert.Equal("gpt-4o", BodyModel(processed));
        Assert.True(BodyElement(processed).GetProperty("stream").GetBoolean());
        Assert.False(BodyElement(processed).TryGetProperty("temperature", out _));
    }

    [Fact]
    public void PreservesAlignedStreamWithoutPatchingIt()
    {
        // Port of TestApplyPassThroughBodyPreservesAlignedStreamWithoutPatchingIt.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4o",
            """{"model":"my-alias","stream":true,"messages":[{"role":"user","content":"hi"}],"temperature":0.4}""",
            stream: true);

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var request = ProviderRequest(
            ApiFormats.OpenAIChatCompletion,
            """{"model":"gpt-4o","stream":true,"messages":[{"role":"user","content":"hi"}]}""");

        var (processed, _) = RunBody(outbound, request);

        Assert.Equal("gpt-4o", BodyModel(processed));
        Assert.True(BodyElement(processed).GetProperty("stream").GetBoolean());
        Assert.Equal(0.4, BodyElement(processed).GetProperty("temperature").GetDouble());
    }

    public static TheoryData<string> MultipartFormats => new()
    {
        ApiFormats.OpenAITranscription,
        ApiFormats.OpenAITranslation,
        ApiFormats.OpenAIImageVariation,
        // The image edit inbound only produces JSON; without a JSON
        // Content-Type it is not replayable.
        ApiFormats.OpenAIImageEdit,
    };

    [Theory]
    [MemberData(nameof(MultipartFormats))]
    public void SkipsMultipartFormats(string format)
    {
        // Port of TestApplyPassThroughBodySkipsMultipartFormats.
        const string inboundBody =
            "--client-boundary\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\noriginal-model\r\n--client-boundary--\r\n";
        const string outboundBody =
            "--new-boundary\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\nmapped-model\r\n--new-boundary--\r\n";

        var llmRequest = UnifiedRequest(format, "mapped-model", inboundBody);

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var (processed, _) = RunBody(
            outbound,
            ProviderRequest(format, outboundBody, "multipart/form-data; boundary=new-boundary"));

        Assert.Equal(outboundBody, Encoding.UTF8.GetString(processed.Body!));
        Assert.False(outbound.PassThroughApplied);
    }

    [Fact]
    public void SkipsMultipartVideo()
    {
        // Port of TestApplyPassThroughBodySkipsMultipartVideo.
        const string inboundBody =
            "--client-boundary\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\nclient-alias\r\n--client-boundary--\r\n";
        const string outboundBody =
            "--provider-boundary\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\nprovider-model\r\n--provider-boundary--\r\n";

        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIVideo,
            "provider-model",
            inboundBody,
            rawContentType: "multipart/form-data; boundary=client-boundary");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var (processed, _) = RunBody(
            outbound,
            ProviderRequest(
                ApiFormats.OpenAIVideo,
                outboundBody,
                "multipart/form-data; boundary=provider-boundary"));

        Assert.False(outbound.PassThroughApplied);
        Assert.Equal(outboundBody, Encoding.UTF8.GetString(processed.Body!));
    }

    [Fact]
    public void AppliesJsonImageEdit()
    {
        // Port of TestApplyPassThroughBodyAppliesJSONImageEdit.
        const string inboundBody =
            """{"model":"my-edit-alias","prompt":"make it blue","image":"data:image/png;base64,aGk="}""";
        const string outboundBody =
            "--new-boundary\r\nContent-Disposition: form-data; name=\"prompt\"\r\n\r\nmake it blue\r\n--new-boundary--\r\n";

        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIImageEdit,
            "sensenova-u1.5-lite",
            inboundBody,
            rawContentType: "application/json");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var (processed, _) = RunBody(
            outbound,
            ProviderRequest(
                ApiFormats.OpenAIImageEdit,
                outboundBody,
                "multipart/form-data; boundary=new-boundary"));

        Assert.True(outbound.PassThroughApplied);
        Assert.Equal("sensenova-u1.5-lite", BodyModel(processed));
        Assert.Equal("make it blue", BodyElement(processed).GetProperty("prompt").GetString());

        // Content-Type follows the replayed JSON body, not the rebuilt multipart one.
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(processed.Headers, "Content-Type"));
        Assert.Equal("application/json", processed.ContentType);
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/json-patch+json", false)]
    [InlineData("application/json; charset", false)]
    public void BodySupportedImageEditContentType(string contentType, bool supported)
    {
        // Port of TestPassThroughBodySupported_ImageEditContentType.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIImageEdit,
            "model",
            "{}",
            rawContentType: contentType);

        Assert.Equal(supported, PassThrough.BodySupported(llmRequest));
    }

    [Fact]
    public void AppliesSpeechModelPatch()
    {
        // Port of TestApplyPassThroughBodyAppliesSpeechModelPatch.
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAISpeech,
            "tts-1-hd",
            """{"model":"my-tts-alias","input":"hello","voice":"alloy"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var (processed, _) = RunBody(
            outbound,
            ProviderRequest(
                ApiFormats.OpenAISpeech,
                """{"model":"tts-1-hd","input":"hello","voice":"alloy"}"""));

        Assert.Equal("tts-1-hd", BodyModel(processed));
        Assert.Equal("alloy", BodyElement(processed).GetProperty("voice").GetString());
    }

    // ------------------------------------------------------------------
    // mergePassThroughRequestBody
    // ------------------------------------------------------------------

    private static string Merge(string rawBody, string apiFormat, string model)
    {
        Assert.True(PassThrough.TryMergeRequestBody(
            Bytes(rawBody),
            apiFormat,
            model,
            out var merged,
            out var error));
        Assert.Null(error);
        return Encoding.UTF8.GetString(merged);
    }

    [Fact]
    public void MergeSkipsFormatsWithoutTopLevelModel()
    {
        // Port of TestMergePassThroughBodySkipsFormatsWithoutTopLevelModel.
        const string rawBody = """{"contents":[{"role":"user","parts":[{"text":"hi"}]}]}""";

        Assert.Equal(
            rawBody,
            Merge(rawBody, ApiFormats.GeminiContents, "gemini-2.5-pro"));
    }

    [Fact]
    public void MergePatchesModerationModel()
    {
        // Port of TestMergePassThroughBodyPatchesModerationModel.
        var merged = Merge(
            """{"model":"omni-moderation-latest","input":"hello"}""",
            ApiFormats.OpenAIModeration,
            "provider-moderation-v1");

        using var document = JsonDocument.Parse(merged);
        Assert.Equal("provider-moderation-v1", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", document.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public void MergePatchesDecisionsModel()
    {
        // Port of TestMergePassThroughBodyPatchesDecisionsModel.
        var merged = Merge(
            """{"model":"client-model","input":"context","x_beta":{"choice":true}}""",
            ApiFormats.OpenAIDecisions,
            "provider-model");

        using var document = JsonDocument.Parse(merged);
        Assert.Equal("provider-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("context", document.RootElement.GetProperty("input").GetString());
        Assert.True(document.RootElement.GetProperty("x_beta").GetProperty("choice").GetBoolean());
    }

    [Fact]
    public void MergeTreatsBlankBodyLikeSjson()
    {
        // sjson starts an empty document as {}; the model patch then creates it.
        Assert.Equal("{\"model\":\"provider-model\"}", Merge(string.Empty, ApiFormats.OpenAIChatCompletion, "provider-model"));
        Assert.Equal("{\"model\":\"provider-model\"}", Merge("  \r\n", ApiFormats.OpenAIChatCompletion, "provider-model"));
    }

    [Fact]
    public void MergeWithoutModelKeepsRawBody()
    {
        // mergePassThroughRequestBody returns the copy untouched when the
        // unified model is empty.
        Assert.Equal(
            """{"model":"alias"}""",
            Merge("""{"model":"alias"}""", ApiFormats.OpenAIChatCompletion, string.Empty));
    }

    [Theory]
    [InlineData(ApiFormats.JinaRerank)]
    [InlineData(ApiFormats.JinaEmbedding)]
    public void JinaFormatsPatchTheModel(string format)
    {
        // Port of TestApplyPassThroughBodyPreservesMappedModelForJinaRerank/Embedding.
        var llmRequest = UnifiedRequest(
            format,
            "jina-embeddings-v3",
            """{"model":"my-embedding-alias","input":"hello","task":"retrieval.query"}""");

        var outbound = Outbound(llmRequest, SettingsEnabled);
        var (processed, _) = RunBody(
            outbound,
            ProviderRequest(format, """{"model":"jina-embeddings-v3","input":"hello"}"""));

        Assert.Equal("jina-embeddings-v3", BodyModel(processed));
        Assert.Equal("retrieval.query", BodyElement(processed).GetProperty("task").GetString());
        Assert.Equal(
            """{"model":"my-embedding-alias","input":"hello","task":"retrieval.query"}""",
            Encoding.UTF8.GetString(outbound.CurrentLlmRequest!.RawRequest!.Body!));
    }

    // ------------------------------------------------------------------
    // applyPassThroughRequestHeaders
    // ------------------------------------------------------------------

    [Fact]
    public void ForwardsCodexHeadersOnly()
    {
        // Port of TestApplyPassThroughRequestHeaders.
        var inboundHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["X-Codex-Turn-Metadata"] = ["""{"session_id":"session-123","turn_id":"turn-456"}"""],
            ["X-Codex-Turn-State"] = ["ts-1"],
            ["X-Codex-Window-Id"] = ["window-123"],
            ["X-Client-Request-Id"] = ["request-123"],
            ["X-Codex-Beta-Features"] = ["js_repl"],
            ["Session-Id"] = ["session-123"],
            ["Originator"] = ["codex_desktop_rs"],
            ["X-Openai-Internal-Codex-Responses-Lite"] = ["true"],
            ["Thread-Id"] = ["thread-123"],
            ["Authorization"] = ["Bearer inbound-secret"],
            ["Cookie"] = ["session=inbound-secret"],
            ["Host"] = ["client.example"],
            ["Content-Length"] = ["12345"],
            ["X-Untrusted-Custom-Header"] = ["do-not-forward"],
        };

        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIResponse,
            "gpt-5.4-mini",
            "{}",
            rawHeaders: inboundHeaders);

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.PassThroughApplied = true;

        var request = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Authorization"] = ["Bearer provider-secret"],
                ["Originator"] = ["axonhub"],
            },
        };

        var processed = new PassThroughRequestHeadersMiddleware(outbound)
            .TransformOutboundRawRequest(request);

        foreach (var header in PassThrough.CodexResponsesPassThroughHeaders)
        {
            Assert.Equal(
                inboundHeaders[header],
                processed.Headers![HttpClientUtils.CanonicalHeaderKey(header)]);
        }

        Assert.Equal("Bearer provider-secret", HttpClientUtils.HeaderGet(processed.Headers, "Authorization"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "X-Openai-Internal-Codex-Responses-Lite"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "Cookie"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "Host"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "Content-Length"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "X-Untrusted-Custom-Header"));
    }

    [Theory]
    [InlineData(false, ApiFormats.OpenAIResponse)]
    [InlineData(true, ApiFormats.OpenAIChatCompletion)]
    public void CodexHeadersRequireResponsesBodyPassThrough(
        bool passThroughApplied,
        string apiFormat)
    {
        // Port of TestApplyPassThroughRequestHeadersRequiresResponsesBodyPassThrough.
        var llmRequest = UnifiedRequest(
            apiFormat,
            "gpt-5.4-mini",
            "{}",
            rawHeaders: Headers(("X-Codex-Turn-Metadata", "must-not-forward")));

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.PassThroughApplied = passThroughApplied;

        var processed = new PassThroughRequestHeadersMiddleware(outbound)
            .TransformOutboundRawRequest(new HttpRequestData
            {
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            });

        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "X-Codex-Turn-Metadata"));
    }

    [Fact]
    public void CodexHeadersAreReplacedNotAppended()
    {
        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIResponse,
            "gpt-5.4-mini",
            "{}",
            rawHeaders: Headers(("Originator", "codex_desktop_rs")));

        var outbound = Outbound(llmRequest, SettingsEnabled);
        outbound.PassThroughApplied = true;

        var processed = new PassThroughRequestHeadersMiddleware(outbound)
            .TransformOutboundRawRequest(new HttpRequestData
            {
                Headers = Headers(("Originator", "axonhub")),
            });

        Assert.Equal(["codex_desktop_rs"], processed.Headers!["Originator"]);
    }

    // ------------------------------------------------------------------
    // applyUserAgentPassThrough
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(false, true, "Client/1.0", null, "axonhub/1.0")]
    [InlineData(true, false, "Client/1.0", null, "Client/1.0")]
    [InlineData(null, false, "Client/1.0", null, "axonhub/1.0")]
    [InlineData(null, true, "Client/1.0", null, "Client/1.0")]
    [InlineData(true, true, "", null, "")]
    [InlineData(null, false, "", "GitHubCopilotChat/0.26.7", "GitHubCopilotChat/0.26.7")]
    [InlineData(true, false, "Client/1.0", "GitHubCopilotChat/0.26.7", "Client/1.0")]
    public void UserAgentPolicy(
        bool? channelSetting,
        bool globalEnabled,
        string clientUserAgent,
        string? outboundUserAgent,
        string expected)
    {
        // Port of TestApplyUserAgentPassThrough.
        var settingsJson = channelSetting is { } value
            ? "{\"passThroughUserAgent\":" + (value ? "true" : "false") + "}"
            : null;

        var rawHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (clientUserAgent.Length > 0)
        {
            rawHeaders["User-Agent"] = [clientUserAgent];
        }

        var llmRequest = UnifiedRequest(
            ApiFormats.OpenAIChatCompletion,
            "gpt-4",
            "{}",
            rawHeaders: rawHeaders);

        var outbound = Outbound(llmRequest, settingsJson);

        var requestHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (outboundUserAgent is not null)
        {
            requestHeaders["User-Agent"] = [outboundUserAgent];
        }

        var request = new HttpRequestData { Headers = requestHeaders };

        var processed = new UserAgentPassThroughMiddleware(
                outbound,
                new PassThroughSettings(false, globalEnabled))
            .TransformOutboundRawRequest(request);

        if (expected.Length > 0)
        {
            Assert.Equal(expected, HttpClientUtils.HeaderGet(processed.Headers, "User-Agent"));
        }
        else
        {
            Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "User-Agent"));
        }
    }

    [Fact]
    public void UserAgentWithoutChannelIsUntouched()
    {
        // Port of TestApplyUserAgentPassThrough_NoChannel.
        var outbound = new PersistentOutboundTransformer(
            [],
            TimeProvider.System,
            new RecordingMetricsRecorder());

        var request = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        };

        var processed = new UserAgentPassThroughMiddleware(outbound, new PassThroughSettings(true, true))
            .TransformOutboundRawRequest(request);

        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(processed.Headers, "User-Agent"));
    }
}

/// <summary>
/// Channel-level pass-through overrides parsed from the settings jsonb, with
/// Go's case-insensitive keys and last-duplicate-wins.
/// </summary>
public sealed class ChannelPassThroughSettingsTests
{
    [Fact]
    public void ExplicitValuesWin()
    {
        var settings = ChannelPassThroughSettings.Parse(
            """{"passThroughBody":true,"passThroughUserAgent":false}""");

        Assert.True(settings.PassThroughBody is true);
        Assert.True(settings.PassThroughUserAgent is false);
    }

    [Fact]
    public void ExplicitFalseIsNotInherit()
    {
        var settings = ChannelPassThroughSettings.Parse("""{"passThroughBody":false}""");

        Assert.True(settings.PassThroughBody is false);
        Assert.Null(settings.PassThroughUserAgent);
    }

    [Fact]
    public void KeysAreCaseInsensitiveAndLastDuplicateWins()
    {
        var settings = ChannelPassThroughSettings.Parse(
            """{"PASSTHROUGHBODY":true,"passthroughbody":false}""");

        Assert.True(settings.PassThroughBody is false);
    }

    [Fact]
    public void NullValueInherits()
    {
        var settings = ChannelPassThroughSettings.Parse(
            """{"passThroughBody":null,"passThroughUserAgent":null}""");

        Assert.Null(settings.PassThroughBody);
        Assert.Null(settings.PassThroughUserAgent);
    }

    [Fact]
    public void WrongTypeInherits()
    {
        var settings = ChannelPassThroughSettings.Parse(
            """{"passThroughBody":"true","passThroughUserAgent":1}""");

        Assert.Null(settings.PassThroughBody);
        Assert.Null(settings.PassThroughUserAgent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void UnreadableSettingsDegradeToInherit(string? settingsJson)
    {
        var settings = ChannelPassThroughSettings.Parse(settingsJson);

        Assert.Null(settings.PassThroughBody);
        Assert.Null(settings.PassThroughUserAgent);
    }
}

/// <summary>
/// The two global pass-through switches (internal/server/biz/system.go:1743-1785):
/// a missing row is false, a value other than "true" is false, and a store
/// failure disables the flag instead of failing the request.
/// </summary>
public sealed class SystemPassThroughSettingsServiceTests
{
    private static SystemPassThroughSettingsService Service(FakeSystemStore store)
        => new(store);

    [Fact]
    public async Task MissingRowsDisableBothFlags()
    {
        var settings = await Service(new FakeSystemStore()).GetAsync();

        Assert.False(settings.PassThrough);
        Assert.False(settings.UserAgentPassThrough);
    }

    [Fact]
    public async Task TrueEnables()
    {
        var store = new FakeSystemStore();
        store.Add(SystemPassThroughSettingsService.PassThroughKey, "true");
        store.Add(SystemPassThroughSettingsService.UserAgentPassThroughKey, "true");

        var settings = await Service(store).GetAsync();

        Assert.True(settings.PassThrough);
        Assert.True(settings.UserAgentPassThrough);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("TRUE")]
    [InlineData("1")]
    [InlineData("")]
    public async Task OtherValuesDisable(string value)
    {
        var store = new FakeSystemStore();
        store.Add(SystemPassThroughSettingsService.PassThroughKey, value);

        Assert.False(await Service(store).GetPassThroughAsync());
    }

    [Fact]
    public async Task StoreFailureDisables()
    {
        var store = new FakeSystemStore { GetFailure = new InvalidOperationException("boom") };

        var settings = await Service(store).GetAsync();

        Assert.False(settings.PassThrough);
        Assert.False(settings.UserAgentPassThrough);
    }

    [Fact]
    public async Task SoftDeletedRowIsMissing()
    {
        // The store's live-row predicate hides deleted rows, like ent.
        var store = new FakeSystemStore();
        var row = store.Add(SystemPassThroughSettingsService.PassThroughKey, "true");
        row.DeletedAt = 1;

        Assert.False(await Service(store).GetPassThroughAsync());
    }
}

/// <summary>
/// The pass-through request path through the real orchestrator: the executor
/// receives the replayed body with the mapped model patched in, and the
/// request execution row records <c>pass_through_applied</c>.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class PassThroughOrchestrationTests(GrainClusterFixture fixture)
{
    private static RetryPolicy Policy()
        => new()
        {
            Enabled = true,
            MaxChannelRetries = 3,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
        };

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder recorder,
        PassThroughSettings settings)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(Policy()),
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            passThroughSettings: new StaticPassThroughSettingsProvider(settings));

    private static ChannelDescriptor MappedChannel(long id, string settingsJson)
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-mapped\"}",
            SupportedModels = "[\"upstream-pass\"]",
            Settings = "{\"modelMappings\":[{\"from\":\"client-pass\",\"to\":\"upstream-pass\"}]"
                + settingsJson + "}",
        };

    private static HttpRequestData ClientRequest()
    {
        var request = TestChannels.ChatRequest("client-pass");
        request.Headers!["User-Agent"] = ["Client/1.0"];
        request.Body = Encoding.UTF8.GetBytes(
            """{"model":"client-pass","messages":[{"role":"user","content":"hi"}],"future_provider_field":{"enabled":true}}""");
        return request;
    }

    [Fact]
    public async Task EnabledChannelReplaysTheClientBody()
    {
        fixture.Channels.Set(MappedChannel(5301, ",\"passThroughBody\":true"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder, PassThroughSettings.Disabled)
            .ProcessAsync(ClientRequest());

        var sent = Assert.Single(executor.Requests);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("upstream-pass", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("future_provider_field").GetProperty("enabled").GetBoolean());

        var execution = Assert.Single(recorder.Executions);
        Assert.True(execution.PassThroughApplied);

        // The replayed body keeps the client's media type.
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(sent.Headers, "Content-Type"));
    }

    [Fact]
    public async Task DisabledChannelSendsTheTransformedBody()
    {
        fixture.Channels.Set(MappedChannel(5302, string.Empty));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder, PassThroughSettings.Disabled)
            .ProcessAsync(ClientRequest());

        var sent = Assert.Single(executor.Requests);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("upstream-pass", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.TryGetProperty("future_provider_field", out _));

        var execution = Assert.Single(recorder.Executions);
        Assert.False(execution.PassThroughApplied);
    }

    [Fact]
    public async Task GlobalFlagEnablesChannelsWithoutAnOverride()
    {
        fixture.Channels.Set(MappedChannel(5303, string.Empty));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };
        var recorder = new RecordingRequestChainRecorder();

        await NewOrchestrator(executor, recorder, new PassThroughSettings(true, false))
            .ProcessAsync(ClientRequest());

        var sent = Assert.Single(executor.Requests);
        using var body = JsonDocument.Parse(sent.Body!);
        Assert.True(body.RootElement.GetProperty("future_provider_field").GetProperty("enabled").GetBoolean());

        var execution = Assert.Single(recorder.Executions);
        Assert.True(execution.PassThroughApplied);
    }

    [Fact]
    public async Task UserAgentDefaultIsAppliedWhenPassThroughIsOff()
    {
        fixture.Channels.Set(MappedChannel(5304, string.Empty));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-pass"),
        };

        await NewOrchestrator(executor, new RecordingRequestChainRecorder(), PassThroughSettings.Disabled)
            .ProcessAsync(ClientRequest());

        var sent = Assert.Single(executor.Requests);
        Assert.Equal("axonhub/1.0", HttpClientUtils.HeaderGet(sent.Headers, "User-Agent"));
    }
}
