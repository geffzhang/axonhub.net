using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

/// <summary>Port of claudecode.Params (outbound.go:51-57).</summary>
public sealed class ClaudeCodeParams
{
    /// <summary>OAuth token provider, required.</summary>
    public ITokenGetter? TokenProvider { get; set; }

    /// <summary>Base URL for the Anthropic API; defaults to the official endpoint.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Whether the channel uses official OAuth credentials.</summary>
    public bool IsOfficial { get; set; }

    /// <summary>Stable channel identity for the deterministic fake user id.</summary>
    public string AccountIdentity { get; set; } = string.Empty;

    /// <summary>
    /// Port-only (C096): record the token actually sent upstream so the request
    /// execution can audit the selected channel key. The reference publishes it
    /// through the wrapped API-key provider's context value; OAuth tokens are
    /// not channel keys and are never audited.
    /// </summary>
    public bool AuditApiKey { get; set; }

    /// <summary>
    /// Port-only: 1-based index over the channel's full key list for the audit
    /// row; null for single-key channels and non-audited tokens.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/anthropic/claudecode/outbound.go: wraps the Anthropic
/// messages outbound with Claude Code OAuth, headers, system message and tool
/// prefix conventions.
/// </summary>
public sealed class ClaudeCodeOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";

    private readonly AnthropicOutboundTransformer _inner;
    private readonly ITokenGetter _tokens;
    private readonly bool _isOfficial;
    private readonly string _accountIdentity;
    private readonly bool _auditApiKey;
    private readonly int? _channelApiKeyIndex;

    /// <summary>The token the latest TransformRequest sent; null when not audited.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformer.</summary>
    public ClaudeCodeOutboundTransformer(ClaudeCodeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.TokenProvider is null)
        {
            throw new InvalidOperationException("TokenProvider is required");
        }

        var baseUrl = parameters.BaseUrl.Length == 0 ? DefaultBaseUrl : parameters.BaseUrl;

        try
        {
            _inner = new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.ClaudeCode,
                BaseUrl = baseUrl,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to create outbound transformer: " + ex.Message,
                ex);
        }

        _tokens = parameters.TokenProvider;
        _isOfficial = parameters.IsOfficial;
        _accountIdentity = parameters.AccountIdentity;
        _auditApiKey = parameters.AuditApiKey;
        _channelApiKeyIndex = parameters.ChannelApiKeyIndex;
    }

    public string ApiFormat => ApiFormats.AnthropicMessage;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _channelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        var rawUserAgent = string.Empty;
        var keepClientUserAgent = false;
        Dictionary<string, List<string>>? rawHeaders = null;

        if (request.RawRequest?.Headers is { } inboundHeaders)
        {
            rawHeaders = inboundHeaders;
            rawUserAgent = HttpClientUtils.HeaderGet(rawHeaders, "User-Agent");
            keepClientUserAgent = ClaudeCodeUtils.IsClaudeCliUserAgent(rawUserAgent);

            if (!keepClientUserAgent)
            {
                // Go mutates the shared inbound headers; the reference deletes
                // the client UA so the SDK UA below is not overridden upstream.
                HttpClientUtils.HeaderDel(rawHeaders, "User-Agent");
            }
        }

        // Clone the request to avoid mutating the original.
        var requestCopy = request.ShallowClone();

        OAuthCredentials credentials;
        try
        {
            // Go passes ctx to the token provider (outbound.go); the port carries
            // it on the request (C094 (6)).
            credentials = _tokens.Get(request.RequestCancellationToken);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get oauth token: " + ex.Message,
                ex);
        }

        var apiKey = credentials.AccessToken;
        if (_auditApiKey)
        {
            _selectedApiKey = apiKey;
        }

        // Structured transformations before serialization.
        requestCopy = ClaudeCodeUtils.DisableThinkingIfToolChoiceForced(requestCopy);
        requestCopy = ClaudeCodeUtils.InjectClaudeCodeSystemMessage(requestCopy);
        requestCopy = ClaudeCodeUtils.InjectFakeUserId(
            requestCopy,
            _accountIdentity,
            request.RawRequest?.SessionId);

        if (_isOfficial && !keepClientUserAgent)
        {
            requestCopy = ClaudeCodeUtils.ApplyClaudeToolPrefix(
                requestCopy,
                ClaudeCodeConstants.ToolPrefix);
        }

        var httpRequest = _inner.TransformRequest(requestCopy);

        // Add beta=true when the base transformer did not already set it.
        httpRequest.Query ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!httpRequest.Query.TryGetValue("beta", out var betaValues)
            || betaValues.Count == 0
            || betaValues[0].Length == 0)
        {
            httpRequest.Query["beta"] = ["true"];
        }

        // Record whether the tool prefix was applied, for response processing.
        httpRequest.Metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
        if (_isOfficial && !keepClientUserAgent)
        {
            httpRequest.Metadata["strip_tool_prefix"] = "true";
        }

        // Add/overwrite the Claude Code specific headers.
        httpRequest.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, value) in ClaudeCodeConstants.Headers)
        {
            HttpClientUtils.HeaderSet(httpRequest.Headers, name, value);
        }

        // Passthrough inbound request headers, overriding the defaults.
        // Anthropic-Beta is merged instead of replaced to keep required betas.
        if (rawHeaders is not null)
        {
            foreach (var header in ClaudeCodeConstants.PassthroughHeaders)
            {
                var value = HttpClientUtils.HeaderGet(rawHeaders, header);
                if (value.Length == 0)
                {
                    continue;
                }

                if (header == "Anthropic-Beta")
                {
                    HttpClientUtils.HeaderSet(
                        httpRequest.Headers,
                        header,
                        ClaudeCodeUtils.MergeBetasIntoHeader(
                            HttpClientUtils.HeaderGet(httpRequest.Headers, header),
                            [value]));
                }
                else
                {
                    HttpClientUtils.HeaderSet(httpRequest.Headers, header, value);
                }
            }
        }

        // Accept depends on whether the caller streams.
        HttpClientUtils.HeaderSet(
            httpRequest.Headers,
            "Accept",
            request.Stream == true ? "text/event-stream" : "application/json");

        HttpClientUtils.HeaderSet(
            httpRequest.Headers,
            "User-Agent",
            keepClientUserAgent && rawUserAgent.Length > 0
                ? rawUserAgent
                : ClaudeCodeConstants.UserAgent);

        // Claude Code OAuth always uses Bearer authentication.
        httpRequest.Auth = new AuthConfig
        {
            Type = AuthTypes.Bearer,
            ApiKey = apiKey,
        };

        return httpRequest;
    }

    /// <summary>Port of TransformResponse: strips the tool prefix when applied.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        var shouldStripPrefix = response?.Request?.Metadata is { } metadata
            && metadata.TryGetValue("strip_tool_prefix", out var value)
            && value == "true";

        if (!shouldStripPrefix)
        {
            return _inner.TransformResponse(response);
        }

        if (response?.Body is { Length: > 0 } body)
        {
            response.Body = ClaudeCodeUtils.StripClaudeToolPrefixFromResponse(
                body,
                ClaudeCodeConstants.ToolPrefix);
        }

        return _inner.TransformResponse(response);
    }

    /// <summary>
    /// Port of TransformStream: wraps the Anthropic stream so tool names lose
    /// the prefix. The prefix is stripped unconditionally, like the reference:
    /// only non-CLI clients behind official channels ever receive prefixed
    /// names, so a stream without them is untouched.
    /// </summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
    {
        var baseStream = _inner.TransformStream(request, stream);

        return new ToolPrefixStripperStream(baseStream, ClaudeCodeConstants.ToolPrefix);
    }

    /// <summary>
    /// Port of AggregateStreamChunks: chunks that start a tool_use block have
    /// the prefix removed before aggregation.
    /// </summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (chunk is not null
                && chunk.Data.Length > 0
                && Encoding.UTF8.GetString(chunk.Data)
                    .Contains("\"type\":\"tool_use\"", StringComparison.Ordinal))
            {
                chunk.Data = ClaudeCodeUtils.StripClaudeToolPrefixFromStreamLine(
                    chunk.Data,
                    ClaudeCodeConstants.ToolPrefix);
            }
        }

        return _inner.AggregateStreamChunks(request, chunks, out meta);
    }

    /// <summary>Port of TransformError: delegates to the Anthropic outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);
}

/// <summary>Port of claudecode.toolPrefixStripperStream.</summary>
internal sealed class ToolPrefixStripperStream : IStream<LlmResponse>
{
    private readonly IStream<LlmResponse> _base;
    private readonly string _prefix;

    public ToolPrefixStripperStream(IStream<LlmResponse> baseStream, string prefix)
    {
        _base = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _prefix = prefix;
    }

    public LlmResponse? Current { get; private set; }

    public Exception? Err => _base.Err;

    public bool Next()
    {
        if (!_base.Next())
        {
            return false;
        }

        var response = _base.Current;
        if (response is null)
        {
            Current = null;
            return true;
        }

        foreach (var choice in response.Choices)
        {
            if (choice.Delta?.ToolCalls is not { Count: > 0 } toolCalls)
            {
                continue;
            }

            foreach (var toolCall in toolCalls)
            {
                var name = toolCall.Function.Name;
                if (name.Length > 0 && name.StartsWith(_prefix, StringComparison.Ordinal))
                {
                    toolCall.Function.Name = name[_prefix.Length..];
                }
            }
        }

        Current = response;
        return true;
    }

    public void Dispose() => _base.Dispose();
}
