using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.SpeechRequestBody (audio_outbound.go:20-30): the wire struct
/// decoded by the speech inbound transformer and re-marshaled by the outbound
/// one. omitempty fields are nullable so empty values drop off the wire.
/// </summary>
public sealed class SpeechRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("input")]
    public string Input { get; set; } = string.Empty;

    [JsonPropertyName("voice")]
    public string Voice { get; set; } = string.Empty;

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("speed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Speed { get; set; }

    [JsonPropertyName("instructions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Instructions { get; set; }

    [JsonPropertyName("stream_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StreamFormat { get; set; }
}

/// <summary>
/// Port of audioFormData (audio_inbound.go:520-525): the parsed multipart form
/// of an STT request. File is null when no file part was present, mirroring
/// Go's <c>form.File != nil</c> test (an empty file part still counts).
/// </summary>
public sealed class AudioFormData
{
    /// <summary>
    /// audio_inbound.go:537-545 — the multipart fields modeled explicitly on
    /// the unified request; everything else is preserved in Extra and
    /// forwarded to the provider as-is.
    /// </summary>
    private static readonly HashSet<string> KnownFields =
        new(StringComparer.Ordinal) { "model", "language", "prompt", "response_format", "temperature" };

    public byte[]? File { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>Keeps multiplicity for repeated multipart fields (e.g. timestamp_granularities[]).</summary>
    public Dictionary<string, List<string>> Fields { get; set; } = [];

    /// <summary>Returns the first value of a multipart field, or "" when absent.</summary>
    public string First(string name)
        => Fields.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : string.Empty;

    /// <summary>
    /// Extracts unmodeled multipart fields (preserving multiplicity) for
    /// passthrough. Null when there are none, like Go's nil map.
    /// </summary>
    public Dictionary<string, List<string>>? ExtraFields()
    {
        Dictionary<string, List<string>>? extra = null;

        foreach (var (name, values) in Fields)
        {
            if (KnownFields.Contains(name))
            {
                continue;
            }

            extra ??= [];
            extra[name] = values;
        }

        return extra;
    }
}

/// <summary>
/// Port of the shared audio helpers in audio_inbound.go/audio_outbound.go:
/// multipart parsing, field parsing and the file-name sanitizer.
/// </summary>
public static class OpenAiAudioModels
{
    /// <summary>audio_inbound.go:24 — bounds the raw multipart body for STT uploads.</summary>
    public const int MaxAudioBodySize = 50 * 1024 * 1024;

    /// <summary>audio_inbound.go:26 — bounds a single audio file (OpenAI Whisper limit is 25MB).</summary>
    public const int MaxAudioFileSize = 26 * 1024 * 1024;

    /// <summary>audio_inbound.go:539-545 — multipart fields modeled on the unified request.</summary>
    private static readonly HashSet<string> AudioKnownFields = new(StringComparer.Ordinal)
    {
        "model",
        "language",
        "prompt",
        "response_format",
        "temperature",
    };

    /// <summary>
    /// Port of parseAudioMultipartRequest (audio_inbound.go:566-639). All error
    /// texts are byte-identical to the reference.
    /// </summary>
    public static AudioFormData ParseAudioMultipartRequest(HttpRequestData httpReq)
    {
        if (httpReq.Body!.Length > MaxAudioBodySize)
        {
            throw TransformerErrors.InvalidRequest("request body too large");
        }

        if (!GoMimeTypes.TryParseMediaType(
                HttpClientUtils.HeaderGet(httpReq.Headers, "Content-Type"),
                out var mediaType,
                out var parameters))
        {
            throw TransformerErrors.InvalidRequest("invalid content-type");
        }

        if (!mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            throw TransformerErrors.InvalidRequest("expected multipart/form-data");
        }

        var boundary = parameters.TryGetValue("boundary", out var value) ? value : string.Empty;
        if (boundary.Length == 0)
        {
            throw TransformerErrors.InvalidRequest("missing boundary in content-type");
        }

        var reader = new GoMultipartReader(httpReq.Body!, boundary);
        var form = new AudioFormData();

        while (true)
        {
            GoMultipartPart? part;
            try
            {
                part = reader.NextPart();
            }
            catch (GoMultipartFormatException)
            {
                // Go's NextPart error and the malformed part reads all fold into
                // this one wording. (A body truncated inside the file data is the
                // one mismatch: Go reports it as a part-read failure instead;
                // difference C079.)
                throw TransformerErrors.InvalidRequest("failed to read multipart");
            }

            if (part is null)
            {
                break;
            }

            var fieldName = part.Name;
            var fileName = part.FileName;

            if (fileName.Length == 0)
            {
                // GoMultipartReader reads the part body eagerly, so the LimitReader
                // cap is a plain length check (C079).
                var fieldValue = part.Data;
                if (fieldValue.Length > MaxAudioFileSize)
                {
                    throw TransformerErrors.InvalidRequest("multipart field too large");
                }

                if (!form.Fields.TryGetValue(fieldName, out var values))
                {
                    values = [];
                    form.Fields[fieldName] = values;
                }

                values.Add(System.Text.Encoding.UTF8.GetString(fieldValue));
                continue;
            }

            if (fieldName != "file")
            {
                // Non-file parts with a filename are skipped unread, exactly
                // like the reference.
                continue;
            }

            // Fail fast on duplicate file parts instead of silently using the last one.
            if (form.File is not null)
            {
                throw TransformerErrors.InvalidRequest("multiple file parts are not allowed");
            }

            var data = part.Data;
            if (data.Length > MaxAudioFileSize)
            {
                throw TransformerErrors.InvalidRequest("file too large");
            }

            form.File = data;
            form.FileName = fileName;
        }

        return form;
    }

    /// <summary>
    /// Port of buildAudioJSONBody (audio_inbound.go:643-666): the JSON logging
    /// representation of the multipart request. Empty single-valued fields are
    /// dropped; multi-valued fields keep their array.
    /// </summary>
    public static byte[] BuildAudioJSONBody(AudioFormData form)
    {
        var body = new Dictionary<string, object>(form.Fields.Count + 1);

        foreach (var (name, values) in form.Fields)
        {
            switch (values.Count)
            {
                case 0:
                    break;
                case 1:
                    if (values[0].Length > 0)
                    {
                        body[name] = values[0];
                    }

                    break;
                default:
                    body[name] = values;
                    break;
            }
        }

        body["file"] = $"<audio bytes: {form.File?.Length ?? 0}, filename: {form.FileName}>";

        return GoJson.Marshal(body);
    }

    /// <summary>
    /// Port of parseOptionalFloat64 (audio_inbound.go:670-682): the reference
    /// rejects an invalid non-empty value instead of silently dropping it. The
    /// %q in the message is Go's strconv.Quote (see GoCast.GoQuote).
    /// </summary>
    public static double? ParseOptionalFloat64(string name, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (!GoCast.TryParseFloat(trimmed, out var parsed))
        {
            throw TransformerErrors.InvalidRequest("invalid " + name + ": " + GoCast.GoQuote(trimmed));
        }

        return parsed;
    }

    /// <summary>
    /// Port of parseStreamField (audio_inbound.go:503-517): tolerant of the
    /// common boolean spellings.
    /// </summary>
    public static bool ParseStreamField(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "" => false,
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => throw TransformerErrors.InvalidRequest("invalid stream value: " + GoCast.GoQuote(normalized)),
        };
    }

    /// <summary>
    /// Port of sanitizeAudioFileName (audio_outbound.go:248-255): strips control
    /// characters (CR/LF etc.) from a client-supplied filename so it cannot break
    /// the multipart Content-Disposition header.
    /// </summary>
    public static string SanitizeAudioFileName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7f)
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Port of looksLikeJSON (audio_outbound.go:448-451).</summary>
    public static bool LooksLikeJson(byte[] body)
    {
        var trimmed = System.Text.Encoding.UTF8.GetString(body).Trim();
        return trimmed.Length > 0 && trimmed[0] is '{' or '[';
    }

    /// <summary>Port of requestTypeForAudioFormat (audio_outbound.go:440-446).</summary>
    public static string RequestTypeForAudioFormat(string apiFormat)
        => apiFormat == ApiFormats.OpenAITranslation ? RequestTypes.Translation : RequestTypes.Transcription;
}
