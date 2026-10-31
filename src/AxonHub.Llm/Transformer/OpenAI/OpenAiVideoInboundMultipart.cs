using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.video_inbound_multipart.go: the multipart/form-data variant
/// of POST /v1/videos. Form fields map 1:1 onto the JSON request; the
/// input_reference may be a file part (sniffed and converted to a data URL)
/// or a plain URL field.
/// </summary>
internal static class OpenAiVideoInboundMultipart
{
    /// <summary>Go: maxVideoBodySize.</summary>
    private const int MaxVideoBodySize = 20 * 1024 * 1024;

    /// <summary>Go: maxVideoReferenceSize.</summary>
    private const int MaxVideoReferenceSize = 4 * 1024 * 1024;

    /// <summary>Go: maxVideoFieldValueSize.</summary>
    private const int MaxVideoFieldValueSize = 4 * 1024 * 1024;

    /// <summary>Go: videoReferenceFieldName.</summary>
    private const string VideoReferenceFieldName = "input_reference";

    /// <summary>Port of parseVideoMultipartRequest.</summary>
    public static VideoCreateRequest ParseVideoMultipartRequest(HttpRequestData httpReq)
    {
        if (httpReq.Body!.Length > MaxVideoBodySize)
        {
            throw TransformerErrors.InvalidRequest("request body too large");
        }

        var contentType = HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type") ?? string.Empty;

        if (!GoMimeTypes.TryParseMediaType(contentType, out var mediaType, out var parameters))
        {
            throw TransformerErrors.InvalidRequest("invalid content-type");
        }

        if (!mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("expected multipart/form-data");
        }

        if (!parameters.TryGetValue("boundary", out var boundary) || boundary.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("missing boundary in content-type");
        }

        var reader = new GoMultipartReader(httpReq.Body, boundary);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        ParsedVideoReference? referenceFile = null;

        while (true)
        {
            GoMultipartPart? part;
            try
            {
                part = reader.NextPart();
            }
            catch (GoMultipartFormatException)
            {
                throw TransformerErrors.InvalidRequest("failed to read multipart");
            }

            if (part is null)
            {
                break;
            }

            var fieldName = part.Name;
            var filename = part.FileName;

            if (filename.Length == 0)
            {
                if (part.Data.Length > MaxVideoFieldValueSize)
                {
                    throw TransformerErrors.InvalidRequest("multipart field too large");
                }

                fields[fieldName] = Encoding.UTF8.GetString(part.Data);
                continue;
            }

            if (fieldName != VideoReferenceFieldName)
            {
                continue;
            }

            if (referenceFile is not null)
            {
                throw TransformerErrors.InvalidRequest("multiple input_reference files are not supported");
            }

            var partContentType = (part.ContentType ?? string.Empty).Trim();

            if (part.Data.Length > MaxVideoReferenceSize)
            {
                throw TransformerErrors.InvalidRequest("file too large");
            }

            if (partContentType.Length == 0)
            {
                partContentType = GoHttpSniff.DetectContentType(part.Data);
            }

            if (!ImageInboundTransformer.IsAllowedImageType(partContentType))
            {
                throw TransformerErrors.InvalidRequest("unsupported image type");
            }

            referenceFile = new ParsedVideoReference
            {
                ContentType = partContentType,
                Data = part.Data,
            };
        }

        var model = TrimField(fields, "model");
        var prompt = TrimField(fields, "prompt");
        var size = TrimField(fields, "size");

        var seconds = TrimField(fields, "seconds");
        string? secondsPtr = seconds.Length > 0 ? seconds : null;

        var inputReference = TrimField(fields, VideoReferenceFieldName);
        if (referenceFile is not null)
        {
            inputReference = Xurl.BuildDataUrl(
                referenceFile.ContentType,
                Convert.ToBase64String(referenceFile.Data),
                isBase64: true);
        }

        var request = new VideoCreateRequest
        {
            Model = model,
            Prompt = prompt,
            InputReference = inputReference,
            Seconds = secondsPtr,
            Size = size,
        };

        OpenAiVideoExtensions.ApplyVideoMultipartExtensions(request, fields);
        return request;
    }

    private static string TrimField(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value.Trim() : string.Empty;

    private sealed class ParsedVideoReference
    {
        public string ContentType { get; init; } = string.Empty;

        public byte[] Data { get; init; } = [];
    }
}
