using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Zenmux;

/// <summary>
/// Port of llm/transformer/zenmux/video_response.go: the native create/poll
/// response decode and the unified video response mapping.
/// </summary>
public sealed partial class ZenmuxOutboundTransformer
{
    /// <summary>Port of parseCreateResponse (video.go:148-154).</summary>
    private LlmResponse ParseCreateResponse(HttpResponseData response)
    {
        var parsed = ParseNativeVideoResponse(response, "create");
        return ToLlmVideoResponse(parsed, response);
    }

    /// <summary>Port of ParseGetVideoTaskResponse (video.go:170-176).</summary>
    public LlmResponse ParseGetVideoTaskResponse(HttpResponseData? httpResp)
    {
        var parsed = ParseNativeVideoResponse(httpResp, "poll");
        return ToLlmVideoResponse(parsed, httpResp!);
    }

    /// <summary>Port of parseNativeVideoResponse (video_response.go:25-51).</summary>
    private static ZenmuxNativeVideoResponse ParseNativeVideoResponse(
        HttpResponseData? response,
        string operation)
    {
        if (response is null)
        {
            throw TransformerErrors.InvalidResponse("http response is nil");
        }

        var parsed = DecodeNativeVideoResponse(response.Body ?? [], operation);

        if (GoStrings.TrimSpace(parsed.ID ?? string.Empty).Length == 0)
        {
            throw TransformerErrors.InvalidResponse("missing id in ZenMux video response");
        }

        if (parsed.Content is { } content
            && content.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            parsed.ParsedContent = DecodeNativeVideoContent(content);
        }

        var status = GoStrings.TrimSpace(parsed.Status ?? string.Empty).ToLowerInvariant();
        switch (status)
        {
            case "queued":
            case "running":
            case "succeeded":
            case "failed":
                parsed.Status = status;
                break;
            default:
                // The reference quotes the original, untrimmed status.
                throw TransformerErrors.InvalidResponse(
                    "unsupported ZenMux video status "
                    + GoCast.GoQuote(parsed.Status ?? string.Empty));
        }

        return parsed;
    }

    /// <summary>Port of the create/poll body decode (video_response.go:29-32).</summary>
    private static ZenmuxNativeVideoResponse DecodeNativeVideoResponse(
        byte[] body,
        string operation)
    {
        var prefix = "failed to unmarshal ZenMux video " + operation + " response: ";

        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw TransformerErrors.InvalidResponse(prefix + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            return new ZenmuxNativeVideoResponse();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidResponse(
                prefix + GoStructJson.TypeMismatch(rootKind, "zenmux.nativeVideoResponse"));
        }

        try
        {
            return LlmJson.Deserialize<ZenmuxNativeVideoResponse>(body)
                ?? new ZenmuxNativeVideoResponse();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidResponse(
                prefix + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>Port of the native content decode (video_response.go:36-42).</summary>
    private static ZenmuxNativeVideoContent DecodeNativeVideoContent(JsonElement content)
    {
        var raw = Encoding.UTF8.GetBytes(content.GetRawText());

        var rootKind = GoStructJson.RootKind(raw);
        if (rootKind == "null")
        {
            return new ZenmuxNativeVideoContent();
        }

        if (rootKind is not null)
        {
            throw TransformerErrors.InvalidResponse(
                "invalid ZenMux video content: "
                + GoStructJson.TypeMismatch(rootKind, "zenmux.nativeVideoContent"));
        }

        try
        {
            return LlmJson.Deserialize<ZenmuxNativeVideoContent>(raw)
                ?? new ZenmuxNativeVideoContent();
        }
        catch (JsonException ex)
        {
            throw TransformerErrors.InvalidResponse(
                "invalid ZenMux video content: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>Port of toLLMVideoResponse (video_response.go:53-78).</summary>
    private static LlmResponse ToLlmVideoResponse(
        ZenmuxNativeVideoResponse response,
        HttpResponseData httpResponse)
    {
        var model = response.Model ?? string.Empty;
        if (model.Length == 0
            && httpResponse.Request?.TransformerMetadata is { } metadata
            && metadata.TryGetValue("model", out var requestModel)
            && requestModel.ValueKind == JsonValueKind.String)
        {
            model = requestModel.GetString() ?? string.Empty;
        }

        var video = new VideoResponse
        {
            ID = response.ID ?? string.Empty,
            Status = response.Status ?? string.Empty,
            Model = OmitIfEmpty(model),
        };

        if (response.ParsedContent is { } content)
        {
            video.VideoURL = OmitIfEmpty(content.VideoURL);
            video.LastFrameURL = OmitIfEmpty(content.LastFrameURL);

            // Go keeps the raw provider content verbatim; the marshal pass
            // compacts it.
            if (response.Content is { } rawContent)
            {
                video.Content = GoJson.CompactElement(rawContent);
            }
        }

        if (response.Error is { } error)
        {
            video.Error = new VideoError
            {
                Code = error.Code ?? string.Empty,
                Message = error.Message ?? string.Empty,
            };
        }

        return new LlmResponse
        {
            Id = response.ID ?? string.Empty,
            Object = "video",
            Model = model,
            RequestType = RequestTypes.Video,
            ApiFormat = ApiFormats.ZenmuxVideo,
            Video = video,
            Choices = [],
        };
    }
}

/// <summary>Port of zenmux.nativeVideoResponse (video_types.go:36-45).</summary>
internal sealed class ZenmuxNativeVideoResponse
{
    [JsonPropertyName("id")]
    public string? ID { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; set; }

    [JsonPropertyName("error")]
    public ZenmuxNativeVideoError? Error { get; set; }

    /// <summary>Go's <c>json:"-"</c>: filled after the content decode.</summary>
    [JsonIgnore]
    public ZenmuxNativeVideoContent? ParsedContent { get; set; }
}

/// <summary>Anonymous error struct of zenmux.nativeVideoResponse (video_types.go:41-44).</summary>
internal sealed class ZenmuxNativeVideoError
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>Port of zenmux.nativeVideoContent (video_types.go:47-50).</summary>
internal sealed class ZenmuxNativeVideoContent
{
    [JsonPropertyName("video_url")]
    public string? VideoURL { get; set; }

    [JsonPropertyName("last_frame_url")]
    public string? LastFrameURL { get; set; }
}
