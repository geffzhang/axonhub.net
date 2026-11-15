using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Port of llm/transformer/openai/codex/alpha_search.go.</summary>
public sealed partial class CodexOutboundTransformer
{
    /// <summary>
    /// Go: <c>ErrAlphaSearchRequest</c> — the sentinel is carried as the
    /// exception message.
    /// </summary>
    public const string ErrAlphaSearchRequest = "invalid alpha search request";

    /// <summary>Port of <c>transformAlphaSearchRequest</c>.</summary>
    private HttpRequestData TransformAlphaSearchRequest(LlmRequest llmRequest)
    {
        if (llmRequest.AlphaSearch is null || llmRequest.AlphaSearch.Body.Length == 0)
        {
            throw new InvalidOperationException(ErrAlphaSearchRequest);
        }

        var credentials = _tokens.Get(llmRequest.RequestCancellationToken);
        if (_auditApiKey)
        {
            _selectedApiKey = credentials.AccessToken;
        }

        // The opaque body is replayed with only the routed model patched in.
        var body = RawJsonModelPatch.SetModel(llmRequest.AlphaSearch.Body, llmRequest.Model);

        var accountId = CodexUtils.ExtractChatGptAccountIdFromJwt(credentials.AccessToken);

        var rawHeaders = llmRequest.RawRequest?.Headers;

        // Keep client attribution headers off the wire: the later inbound
        // header merge would otherwise forward them upstream.
        CodexHeaders.ScrubClientFingerprintHeaders(rawHeaders);

        var sessionId = CodexHeaders.GetSessionIdFromHeaders(rawHeaders);
        if (sessionId.Length == 0)
        {
            var envelope = TryDecodeAlphaSearchEnvelope(llmRequest.AlphaSearch.Body);
            sessionId = (envelope?.Id ?? string.Empty).Trim();
        }

        if (sessionId.Length == 0)
        {
            sessionId = _sessionIdProvider?.Invoke() ?? Guid.NewGuid().ToString();
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        foreach (var name in CodexHeaders.PassthroughHeaders)
        {
            foreach (var value in CodexHeaders.HeaderValues(rawHeaders, name))
            {
                HttpClientUtils.HeaderAdd(headers, name, value);
            }
        }

        var rawOriginator = HttpClientUtils.HeaderGet(rawHeaders, "Originator");
        HttpClientUtils.HeaderSet(
            headers,
            "Originator",
            rawOriginator.Length > 0 ? rawOriginator : CodexConstants.AxonHubOriginator);

        HttpClientUtils.HeaderSet(headers, "User-Agent", CodexConstants.DefaultUserAgent);

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.SessionHeaderHyphen).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, sessionId);
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.ThreadIdHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.ThreadIdHeader, sessionId);
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.WindowIdHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.WindowIdHeader, sessionId + ":0");
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.ClientRequestIdHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.ClientRequestIdHeader, Guid.NewGuid().ToString());
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.BetaFeaturesHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.BetaFeaturesHeader, CodexConstants.FabricatedBetaFeatures);
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.TurnMetadataHeader).Length == 0)
        {
            var installationId = accountId.Length > 0
                ? CodexUtils.NewUuidV5(CodexUtils.NameSpaceOid, System.Text.Encoding.UTF8.GetBytes(accountId))
                : string.Empty;

            // The reference never sets TurnStartedAtUnixMS here, so the header
            // carries the zero value.
            var metadata = new TurnMetadata
            {
                InstallationId = installationId,
                SessionId = sessionId,
                ThreadId = sessionId,
                TurnId = Guid.NewGuid().ToString(),
                WindowId = HttpClientUtils.HeaderGet(headers, CodexHeaders.WindowIdHeader),
                RequestKind = "turn",
                ThreadSource = "user",
                Sandbox = "none",
            };

            HttpClientUtils.HeaderSet(headers, CodexHeaders.TurnMetadataHeader, GoJson.MarshalToString(metadata));
        }

        if (accountId.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, "Chatgpt-Account-Id", accountId);
        }

        if (HttpClientUtils.HeaderGet(headers, "Version").Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, "Version", CodexConstants.DefaultVersion);
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl.TrimEnd('#', '/') + _alphaSearchPath,
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = credentials.AccessToken,
            },
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
        };
    }

    /// <summary>Port of <c>transformAlphaSearchResponse</c>.</summary>
    private LlmResponse TransformAlphaSearchResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException(ErrAlphaSearchRequest);
        }

        if (response.StatusCode >= 400)
        {
            // Go passes only the status code and body; the helper text fills the
            // unused transport fields.
            var httpError = new HttpClientError(
                string.Empty,
                string.Empty,
                response.StatusCode,
                GoHttpStatus.Text(response.StatusCode),
                response.Body);
            throw _responsesOutbound.TransformError(httpError);
        }

        // Unlike the OpenAI alpha search outbound, the reference does not reject
        // an empty body here; the upstream JSON is wrapped verbatim.
        return new LlmResponse
        {
            RequestType = RequestTypes.AlphaSearch,
            ApiFormat = ApiFormats.OpenAIAlphaSearch,
            AlphaSearch = new AlphaSearchResponse
            {
                Body = (byte[])(response.Body ?? []).Clone(),
            },
        };
    }

    /// <summary>
    /// Go: <c>json.Unmarshal(llmReq.AlphaSearch.Body, &amp;envelope)</c> with the
    /// error ignored; any failure means "no id".
    /// </summary>
    private static AlphaSearchIdEnvelope? TryDecodeAlphaSearchEnvelope(byte[] body)
    {
        try
        {
            return LlmJson.Deserialize<AlphaSearchIdEnvelope>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of the anonymous <c>struct { ID string `json:"id"` }</c> envelope.</summary>
    private sealed class AlphaSearchIdEnvelope
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
