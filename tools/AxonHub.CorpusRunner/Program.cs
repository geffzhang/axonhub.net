using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.CorpusRunner;

/// <summary>
/// Differential-corpus runner: the .NET mirror of the Go harness in
/// E:\GitHub\axonhub-corpus. It reads one case from stdin and writes the
/// observable result to stdout, so diff_corpus.py can compare the two
/// implementations byte-for-byte.
///
/// Modes:
///   upstream-request  client HTTP request  -> final upstream HTTP request
///   unified-request   client HTTP request  -> unified llm.Request JSON
///   client-response   upstream HTTP response -> client HTTP response
/// </summary>
public static class Program
{
    private const string UpstreamBaseUrl = "http://upstream.test/v1";
    private const string UpstreamApiKey = "sk-corpus-fixed";

    // Go's Encoder always terminates lines with "\n"; the default here is
    // Environment.NewLine, which would emit CRLF on Windows.
    private const string GoNewLine = "\n";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = GoNewLine,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Inherits LlmJson's converters (notably the Go sorted-map emission) so the
    // mirrored view uses exactly the wire rules the transformer uses.
    private static readonly JsonSerializerOptions ValueOptions = new(LlmJson.Options)
    {
        WriteIndented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = GoNewLine,
    };

    public static int Main(string[] args)
    {
        using var stdin = Console.OpenStandardInput();
        using var buffer = new MemoryStream();
        stdin.CopyTo(buffer);
        var raw = buffer.ToArray();

        var mode = ParseMode(args);
        if (mode is null)
        {
            WriteUsageError("unknown mode");
            return 2;
        }

        try
        {
            switch (mode)
            {
                case "upstream-request":
                    Emit(BuildUpstreamRequest(raw));
                    break;
                case "unified-request":
                    EmitValue(BuildUnifiedRequest(raw));
                    break;
                case "client-response":
                    EmitValue(BuildClientResponse(raw));
                    break;
                case "client-stream":
                    EmitValue(BuildClientStream(raw));
                    break;
                default:
                    WriteUsageError("unknown mode \"" + mode + "\"");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            EmitError(ex is ResponseError re ? re.ToString() : ex.Message);
        }

        return 0;
    }

    private static string? ParseMode(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--mode" && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith("--mode=", StringComparison.Ordinal))
            {
                return args[i]["--mode=".Length..];
            }
        }

        return null;
    }

    private static Dictionary<string, List<string>> ToHeader(Dictionary<string, List<string>>? input)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (input is null)
        {
            return headers;
        }

        foreach (var (key, values) in input)
        {
            foreach (var value in values)
            {
                HttpClientUtils.HeaderAdd(headers, key, value);
            }
        }

        return headers;
    }

    private static HttpRequestView BuildUpstreamRequest(byte[] raw)
    {
        var input = LlmJson.Deserialize<ClientRequest>(raw) ?? throw new InvalidOperationException("empty input");

        var method = string.IsNullOrEmpty(input.Method) ? "POST" : input.Method;

        var hreq = new HttpRequestData
        {
            Method = method,
            Url = input.Url ?? string.Empty,
            Headers = ToHeader(input.Headers),
            Body = Encoding.UTF8.GetBytes(input.Body ?? string.Empty),
        };

        var llmReq = new OpenAiInboundTransformer().TransformRequest(hreq);
        llmReq.RawRequest = hreq;

        var outbound = OpenAiOutboundTransformer.Create(UpstreamBaseUrl, UpstreamApiKey);
        var hcReq = outbound.TransformRequest(llmReq);

        hcReq = HttpClientUtils.MergeInboundRequest(hcReq, hreq);
        hcReq = HttpClientUtils.FinalizeAuthHeaders(hcReq);

        var final = HttpClientUtils.BuildHttpRequest(hcReq);

        var view = new HttpRequestView
        {
            Method = final.Method,
            Url = final.Url,
            Headers = new SortedDictionary<string, List<string>>(final.Headers, StringComparer.Ordinal),
        };

        if (IsTextBody(HttpClientUtils.HeaderGet(final.Headers, "Content-Type")))
        {
            view.Body = Encoding.UTF8.GetString(final.Body);
        }
        else
        {
            view.BodyBase64 = Convert.ToBase64String(final.Body);
        }

        return view;
    }

    private static LlmRequest BuildUnifiedRequest(byte[] raw)
    {
        var input = LlmJson.Deserialize<ClientRequest>(raw) ?? throw new InvalidOperationException("empty input");

        // Go hardcodes POST here; the client-supplied method is ignored.
        var hreq = new HttpRequestData
        {
            Method = "POST",
            Url = input.Url ?? string.Empty,
            Headers = ToHeader(input.Headers),
            Body = Encoding.UTF8.GetBytes(input.Body ?? string.Empty),
        };

        return new OpenAiInboundTransformer().TransformRequest(hreq);
    }

    private static HttpResponseView BuildClientResponse(byte[] raw)
    {
        var input = LlmJson.Deserialize<UpstreamResponse>(raw) ?? throw new InvalidOperationException("empty input");

        var llmResp = OpenAiOutboundTransformer.Create(UpstreamBaseUrl, UpstreamApiKey)
            .TransformResponse(new HttpResponseData
            {
                StatusCode = input.StatusCode,
                Headers = ToHeader(input.Headers),
                Body = Encoding.UTF8.GetBytes(input.Body ?? string.Empty),
            });

        var clientResp = new OpenAiInboundTransformer().TransformResponse(llmResp);

        var view = new HttpResponseView
        {
            StatusCode = clientResp.StatusCode,
            Headers = new SortedDictionary<string, List<string>>(clientResp.Headers, StringComparer.Ordinal),
        };

        var body = clientResp.Body ?? [];
        if (IsTextBody(HttpClientUtils.HeaderGet(clientResp.Headers, "Content-Type")))
        {
            view.Body = Encoding.UTF8.GetString(body);
        }
        else
        {
            view.BodyBase64 = Convert.ToBase64String(body);
        }

        return view;
    }

    private static StreamView BuildClientStream(byte[] raw)
    {
        var input = LlmJson.Deserialize<UpstreamResponse>(raw) ?? throw new InvalidOperationException("empty input");

        using var sse = new AxonHub.Llm.Streams.SseEventStream(input.Sse ?? string.Empty);
        using var llmStream = OpenAiOutboundTransformer.Create(UpstreamBaseUrl, UpstreamApiKey)
            .TransformStream(null, sse);
        using var clientStream = new OpenAiInboundTransformer().TransformStream(llmStream);

        var view = new StreamView();

        while (clientStream.Next())
        {
            var ev = clientStream.Current!;
            view.Events.Add(new StreamEventView
            {
                LastEventId = string.IsNullOrEmpty(ev.LastEventId) ? null : ev.LastEventId,
                Type = ev.Type,
                Data = Encoding.UTF8.GetString(ev.Data),
            });
        }

        if (clientStream.Err is { } err)
        {
            throw err;
        }

        return view;
    }

    private static bool IsTextBody(string contentType)
    {
        var ct = contentType.ToLowerInvariant();
        if (ct.Length == 0)
        {
            return true;
        }

        return ct.Contains("json", StringComparison.Ordinal)
            || ct.Contains("text", StringComparison.Ordinal)
            || ct.Contains("event-stream", StringComparison.Ordinal)
            || ct.Contains("xml", StringComparison.Ordinal);
    }

    private static void Emit(HttpRequestView view)
    {
        using var stdout = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stdout, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("method", view.Method);
        writer.WriteString("url", view.Url);

        writer.WriteStartObject("headers");
        foreach (var (key, values) in view.Headers)
        {
            writer.WriteStartArray(key);
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();

        if (view.Body is not null)
        {
            writer.WriteString("body", view.Body);
        }

        if (view.BodyBase64 is not null)
        {
            writer.WriteString("body_base64", view.BodyBase64);
        }

        writer.WriteEndObject();
        writer.Flush();
        stdout.WriteByte((byte)'\n');
    }

    /// <summary>
    /// Emits an arbitrary view. Go's <c>json.Encoder</c> with <c>SetEscapeHTML(false)</c>
    /// is the reference: two-space indent, no HTML escaping, trailing newline.
    /// </summary>
    private static void EmitValue(object view)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(view, ValueOptions);
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(json);
        stdout.WriteByte((byte)'\n');
    }

    private static void EmitError(string message)
    {
        using var stdout = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(stdout, WriterOptions);
        writer.WriteStartObject();
        writer.WriteString("error", message);
        writer.WriteEndObject();
        writer.Flush();
        stdout.WriteByte((byte)'\n');
    }

    private static void WriteUsageError(string message)
    {
        EmitError(message);
    }

    private sealed class ClientRequest
    {
        public string? Method { get; set; }

        public string? Url { get; set; }

        public Dictionary<string, List<string>>? Headers { get; set; }

        public string? Body { get; set; }
    }

    private sealed class UpstreamResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("status_code")]
        public int StatusCode { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("headers")]
        public Dictionary<string, List<string>>? Headers { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("body")]
        public string? Body { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("sse")]
        public string? Sse { get; set; }
    }

    private sealed class StreamView
    {
        [System.Text.Json.Serialization.JsonPropertyName("events")]
        public List<StreamEventView> Events { get; } = [];
    }

    private sealed class StreamEventView
    {
        [System.Text.Json.Serialization.JsonPropertyName("last_event_id")]
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? LastEventId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("data")]
        public string Data { get; set; } = string.Empty;
    }

    private sealed class HttpResponseView
    {
        [System.Text.Json.Serialization.JsonPropertyName("status_code")]
        public int StatusCode { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("headers")]
        public SortedDictionary<string, List<string>> Headers { get; init; } = new(StringComparer.Ordinal);

        [System.Text.Json.Serialization.JsonPropertyName("body")]
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Body { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("body_base64")]
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? BodyBase64 { get; set; }
    }

    private sealed class HttpRequestView
    {
        public string Method { get; init; } = string.Empty;

        public string Url { get; init; } = string.Empty;

        public SortedDictionary<string, List<string>> Headers { get; init; } = new(StringComparer.Ordinal);

        public string? Body { get; set; }

        public string? BodyBase64 { get; set; }
    }
}
