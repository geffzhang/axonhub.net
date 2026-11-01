using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Llm.Tests;

/// <summary>Port of the codex package's <c>staticTokenGetter</c>.</summary>
internal sealed class StaticCodexTokenGetter : ITokenGetter
{
    public OAuthCredentials Creds { get; set; } = new();

    public OAuthCredentials Get(CancellationToken cancellationToken) => Creds;
}

/// <summary>Shared fixtures for the ported llm/transformer/openai/codex tests.</summary>
internal static class CodexTestSupport
{
    /// <summary>Go: <c>testChatAccountID</c>.</summary>
    internal const string TestChatAccountId = "acct_test";

    internal static OAuthCredentials Credentials(string accessToken) => new()
    {
        AccessToken = accessToken,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    };

    /// <summary>
    /// Port of <c>testAccessTokenWithAccountID</c>: an HS256 JWT whose
    /// <c>https://api.openai.com/auth</c> claim carries the ChatGPT account id.
    /// The signature is never verified by the transformer, but it is produced
    /// faithfully so the token stays a valid three-segment JWT.
    /// </summary>
    internal static string TestAccessTokenWithAccountId()
    {
        var header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        var payload = Base64Url(
            """{"https://api.openai.com/auth":{"chatgpt_account_id":"acct_test"}}""");
        var signingInput = header + "." + payload;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("test-secret"));
        var signature = Base64UrlBytes(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
        return signingInput + "." + signature;
    }

    internal static string LegacyCodexOriginator() => "codex" + "_cli_rs";

    internal static string LegacyCodexUserAgent()
        => LegacyCodexOriginator() + "/0.50.0 (macOS 14.0.0; arm64) Terminal";

    /// <summary>Port of <c>newTestCodexOutbound</c>.</summary>
    internal static CodexOutboundTransformer NewTestCodexOutbound(Func<string?>? sessionId = null)
        => NewTestCodexOutboundForBaseUrl(CodexOutboundTransformer.CodexBaseUrl, sessionId);

    /// <summary>Port of <c>newTestCodexOutboundForBaseURL</c>.</summary>
    internal static CodexOutboundTransformer NewTestCodexOutboundForBaseUrl(
        string baseUrl,
        Func<string?>? sessionId = null)
        => new(new CodexOutboundParams
        {
            BaseUrl = baseUrl,
            TokenProvider = new StaticCodexTokenGetter
            {
                Creds = Credentials(TestAccessTokenWithAccountId()),
            },
            SessionIdProvider = sessionId,
        });

    /// <summary>
    /// Port of <c>httpclient.ReadHTTPRequest</c> for the JSON requests these
    /// tests build: method, URL, canonical headers and body (AxonHub.Api owns
    /// the ASP.NET-aware reader and the Llm test project does not reference it).
    /// </summary>
    internal static HttpRequestData NewInboundRequest(
        string body,
        params (string Name, string Value)[] headers)
    {
        var canonical = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(canonical, "Content-Type", "application/json");
        foreach (var (name, value) in headers)
        {
            HttpClientUtils.HeaderSet(canonical, name, value);
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = "http://localhost:8090/v1/chat/completions",
            Path = "/v1/chat/completions",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = canonical,
            ContentType = HttpClientUtils.HeaderGet(canonical, "Content-Type"),
            Body = Encoding.UTF8.GetBytes(body),
            Auth = new AuthConfig(),
            UserAgent = HttpClientUtils.HeaderGet(canonical, "User-Agent"),
        };
    }

    /// <summary>Port of <c>newCodexChatCompletionRequest</c>.</summary>
    internal static HttpRequestData NewChatCompletionRequest(
        params (string Name, string Value)[] headers)
        => NewInboundRequest(
            """{"model":"gpt-5-codex","messages":[{"role":"user","content":"Hello"}]}""",
            headers);

    /// <summary>Port of <c>buildCodexStreamRequest</c>.</summary>
    internal static HttpRequestData BuildCodexStreamRequest(
        CodexOutboundTransformer outbound,
        bool withInboundIdentity)
    {
        var headers = new List<(string Name, string Value)>
        {
            ("Accept", "application/json"),
            ("Connection", "keep-alive"),
            ("Content-Type", "application/json"),
            ("Conversation_id", "legacy-conversation"),
            ("Openai-Beta", "responses=experimental"),
            ("Session_id", "provided-session"),
            ("Version", "9.9.9"),
        };

        if (withInboundIdentity)
        {
            headers.Add(("Originator", LegacyCodexOriginator()));
            headers.Add(("User-Agent", LegacyCodexUserAgent()));
        }

        var inboundRequest = NewInboundRequest(
            """{"model":"gpt-5-codex","stream":true,"messages":[{"role":"user","content":"Hello"}]}""",
            [.. headers]);

        var llmRequest = new OpenAiInboundTransformer().TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var outboundRequest = outbound.TransformRequest(llmRequest);
        outboundRequest = HttpClientUtils.MergeInboundRequest(outboundRequest, inboundRequest);
        return HttpClientUtils.FinalizeAuthHeaders(outboundRequest);
    }

    /// <summary>
    /// Port of <c>simulator.Simulate</c>: inbound transform, outbound transform,
    /// inbound header merge, auth finalisation and the final request build. The
    /// reference reads the trace session from the context; the port passes it
    /// through the transformer's SessionIdProvider seam (C062).
    /// </summary>
    internal static BuiltHttpRequest Simulate(
        IInboundTransformer inbound,
        CodexOutboundTransformer outbound,
        HttpRequestData inboundRequest)
    {
        var llmRequest = inbound.TransformRequest(inboundRequest);
        llmRequest.RawRequest = inboundRequest;

        var outboundRequest = outbound.TransformRequest(llmRequest);
        outboundRequest = HttpClientUtils.MergeInboundRequest(outboundRequest, inboundRequest);
        outboundRequest = HttpClientUtils.FinalizeAuthHeaders(outboundRequest);

        return HttpClientUtils.BuildHttpRequest(outboundRequest);
    }

    /// <summary>Port of <c>newCodexSSEResponse</c>.</summary>
    internal static HttpResponseData NewCodexSseResponse(IEnumerable<StreamEvent?> events)
    {
        var body = new StringBuilder();
        foreach (var ev in events)
        {
            if (ev is null)
            {
                continue;
            }

            if (ev.Type.Length > 0)
            {
                body.Append("event: ").Append(ev.Type).Append('\n');
            }

            if (!string.IsNullOrEmpty(ev.LastEventId))
            {
                body.Append("id: ").Append(ev.LastEventId).Append('\n');
            }

            // SSE requires a separate data field for each logical payload line.
            foreach (var line in Encoding.UTF8.GetString(ev.Data).Split('\n'))
            {
                var trimmed = line.EndsWith('\r') ? line[..^1] : line;
                body.Append("data: ").Append(trimmed).Append('\n');
            }

            body.Append('\n');
        }

        return new HttpResponseData
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["text/event-stream"],
            },
            Body = Encoding.UTF8.GetBytes(body.ToString()),
        };
    }

    internal static StreamEvent Event(string type, string data)
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(data) };

    /// <summary>The completed Responses SSE sequence used by the aggregation tests.</summary>
    internal static List<StreamEvent?> RelayStreamEvents(string responseId, string messageId)
        =>
        [
            Event(
                "response.created",
                $$$"""{"type":"response.created","sequence_number":0,"response":{"id":"{{{responseId}}}","object":"response","created_at":1700000000,"model":"gpt-5-codex","status":"in_progress","output":[]}}"""),
            Event(
                "response.output_item.added",
                $$$"""{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"{{{messageId}}}","type":"message","status":"in_progress","role":"assistant"}}"""),
            Event(
                "response.content_part.added",
                $$$"""{"type":"response.content_part.added","sequence_number":2,"item_id":"{{{messageId}}}","output_index":0,"content_index":0,"part":{"type":"output_text","text":""}}"""),
            Event(
                "response.output_text.delta",
                $$$"""{"type":"response.output_text.delta","sequence_number":3,"item_id":"{{{messageId}}}","output_index":0,"content_index":0,"delta":"Hello"}"""),
            Event(
                "response.output_text.done",
                $$$"""{"type":"response.output_text.done","sequence_number":4,"item_id":"{{{messageId}}}","output_index":0,"content_index":0,"text":"Hello"}"""),
            Event(
                "response.output_item.done",
                $$$"""{"type":"response.output_item.done","sequence_number":5,"output_index":0,"item":{"id":"{{{messageId}}}","type":"message","status":"completed","role":"assistant"}}"""),
            Event(
                "response.completed",
                $$$"""{"type":"response.completed","sequence_number":6,"response":{"id":"{{{responseId}}}","object":"response","created_at":1700000000,"model":"gpt-5-codex","status":"completed","output":[]}}"""),
        ];

    private static string Base64Url(string value)
        => Base64UrlBytes(Encoding.UTF8.GetBytes(value));

    private static string Base64UrlBytes(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Port of the codex tests' <c>mockCodexExecutor</c>.</summary>
internal sealed class CodexMockExecutor : IExecutor
{
    public List<StreamEvent?> StreamEvents { get; set; } = [];

    public int DoCalls { get; private set; }

    public int DoStreamCalls { get; private set; }

    public HttpRequestData? Request { get; private set; }

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        DoCalls++;
        return CodexTestSupport.NewCodexSseResponse(StreamEvents);
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        DoStreamCalls++;
        Request = request;
        return new SliceStream<StreamEvent>(
            StreamEvents.Where(static ev => ev is not null).Select(static ev => ev!));
    }
}

/// <summary>
/// Stands in for the reference tests' <c>httptest.Server</c>: records the built
/// request and answers through a scripted handler, so assertions observe the
/// same bytes <c>HttpExecutor</c> would put on the wire.
/// </summary>
internal sealed class CodexTestHttpHandler(
    Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
{
    private readonly List<HttpRequestMessage> _requests = [];

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int RequestCount
    {
        get
        {
            lock (_requests)
            {
                return _requests.Count;
            }
        }
    }

    public HttpRequestMessage? LastRequest
    {
        get
        {
            var requests = Requests;
            return requests.Count > 0 ? requests[^1] : null;
        }
    }

    public string LastHeader(string name)
    {
        var request = LastRequest;
        if (request is null)
        {
            return string.Empty;
        }

        // HttpClient parses a known header's wire value into typed header
        // values; User-Agent's product-info list comes back as one entry per
        // product/comment, so only joining reconstructs the wire string (the
        // reference asserts the exact value through Go's Header.Get).
        if (request.Headers.TryGetValues(name, out var values))
        {
            return name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)
                ? string.Join(" ", values)
                : values.First();
        }

        if (request.Content is not null
            && request.Content.Headers.TryGetValues(name, out var contentValues))
        {
            return contentValues.First();
        }

        return string.Empty;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Task.FromResult(responder(request, cancellationToken));
    }
}

/// <summary>gjson-style body probes for the ported Codex tests.</summary>
internal static class CodexJson
{
    internal static JsonElement Root(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    internal static bool Has(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _);

    internal static JsonElement? Path(JsonElement root, params object[] segments)
    {
        var current = root;
        foreach (var segment in segments)
        {
            if (segment is string name)
            {
                if (current.ValueKind != JsonValueKind.Object
                    || !current.TryGetProperty(name, out var next))
                {
                    return null;
                }

                current = next;
                continue;
            }

            if (segment is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return null;
                }

                current = current[index];
                continue;
            }

            throw new ArgumentException("json path segments must be strings or integers");
        }

        return current;
    }

    /// <summary>gjson's <c>.String()</c>: empty for missing, null and non-strings.</summary>
    internal static string PathString(JsonElement root, params object[] segments)
        => Path(root, segments) is { } value && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : string.Empty;
}
