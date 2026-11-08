using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Gemini;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>Port of antigravity.Config (llm/transformer/antigravity/transformer.go:48-52).</summary>
public sealed class AntigravityConfig
{
    [JsonPropertyName("base_url")]
    public string BaseURL { get; set; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string APIKey { get; set; } = string.Empty;

    /// <summary>Google Cloud Project ID.</summary>
    [JsonPropertyName("project")]
    public string Project { get; set; } = string.Empty;
}

/// <summary>Port of the WithHTTPClient/WithOnTokenRefreshed options (transformer.go:26-40).</summary>
public sealed class AntigravityTransformerOptions
{
    /// <summary>The token-exchange HTTP seam (proxy settings, test doubles).</summary>
    public IExecutor? HttpClient { get; set; }

    /// <summary>Called after a token refresh succeeds; a failure is logged and ignored.</summary>
    public Func<CancellationToken, OAuthCredentials, Exception?>? OnTokenRefreshed { get; set; }
}

/// <summary>
/// Port of antigravity.Transformer (llm/transformer/antigravity/transformer.go):
/// wraps the Gemini outbound transformer in the project-aware Antigravity
/// envelope, patches schemas/tools/thinking for the Antigravity API, and
/// authenticates through the OAuth token provider.
/// </summary>
public sealed class AntigravityTransformer : IOutboundTransformer, IChannelCustomizedExecutor
{
    /// <summary>Port of tokenExchangeTimeout: maximum time to wait for token refresh/retrieval.</summary>
    private static readonly TimeSpan TokenExchangeTimeout = TimeSpan.FromSeconds(30);

    private readonly AntigravityConfig _config;
    private readonly GeminiOutboundTransformer _geminiTransformer;
    private readonly Func<CancellationToken, OAuthCredentials, Exception?>? _onTokenRefreshed;
    private readonly IExecutor? _httpClient;

    private TokenProvider? _tokenProvider;

    private AntigravityTransformer(
        AntigravityConfig config,
        GeminiOutboundTransformer geminiTransformer,
        Func<CancellationToken, OAuthCredentials, Exception?>? onTokenRefreshed,
        IExecutor? httpClient)
    {
        _config = config;
        _geminiTransformer = geminiTransformer;
        _onTokenRefreshed = onTokenRefreshed;
        _httpClient = httpClient;
    }

    /// <summary>Port of NewTransformer(config, opts...).</summary>
    public static AntigravityTransformer Create(AntigravityConfig config, AntigravityTransformerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Initialize a Gemini transformer for internal use.
        GeminiOutboundTransformer gt;
        try
        {
            gt = GeminiOutboundTransformer.Create(config.BaseURL, config.APIKey);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to create internal gemini transformer: " + ex.Message, ex);
        }

        var t = new AntigravityTransformer(config, gt, options?.OnTokenRefreshed, options?.HttpClient);

        if (config.APIKey.Length > 0)
        {
            t.InitTokenProvider(config.APIKey);

            // If Project is not explicitly set, try to extract it from credentials.
            if (t._config.Project.Length == 0)
            {
                var (_, projectID) = ParseCredentials(config.APIKey);
                if (projectID.Length > 0)
                {
                    t._config.Project = projectID;
                }
            }
        }

        return t;
    }

    /// <summary>Port of (*Transformer).initTokenProvider (transformer.go:94-122).</summary>
    private void InitTokenProvider(string apiKey)
    {
        var (refreshToken, _) = ParseCredentials(apiKey);
        if (refreshToken.Length == 0)
        {
            return;
        }

        var httpClient = _httpClient ?? new HttpExecutor(new HttpClient(new SocketsHttpHandler
        {
            // Redirects are followed by HttpExecutor with Go's loop.
            AllowAutoRedirect = false,
        })
        {
            // Appropriate timeouts for token exchange (Go's tokenExchangeTimeout).
            Timeout = TokenExchangeTimeout,
        });

        _tokenProvider = AntigravityTokenProviderFactory.NewTokenProvider(new TokenProviderParams
        {
            Credentials = new OAuthCredentials
            {
                RefreshToken = refreshToken,
                ClientId = AntigravityConstants.ClientID,
                Scopes = AntigravityConstants.Scopes,
            },
            HttpClient = httpClient,
            OnRefreshed = _onTokenRefreshed,
        });
    }

    /// <summary>
    /// Port of parseCredentials (transformer.go:124-131): "&lt;refreshToken&gt;|&lt;projectID&gt;".
    /// Go <c>strings.Split</c> keeps trailing empty parts, so the port must too
    /// (<c>SplitOptions.None</c>; the C# default drops them).
    /// </summary>
    internal static (string RefreshToken, string ProjectID) ParseCredentials(string creds)
    {
        var parts = creds.Split('|', StringSplitOptions.None);
        if (parts.Length >= 2)
        {
            return (parts[0], parts[1]);
        }

        return (creds, "");
    }

    /// <summary>Port of APIFormat: the Antigravity payload is Gemini-shaped.</summary>
    public string ApiFormat => ApiFormats.GeminiContents;

    /// <summary>Port of GetTokenProvider.</summary>
    public TokenProvider? GetTokenProvider() => _tokenProvider;

    /// <summary>Port of TransformRequest (transformer.go:139-247).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        // Aggressively strip client headers that shouldn't leak or be merged back by the pipeline.
        if (request.RawRequest?.Headers is { } rawHeaders)
        {
            foreach (var header in HeadersToStrip)
            {
                HttpClientUtils.HeaderDel(rawHeaders, header);
            }
        }

        // 1. Use the Gemini transformer to get the base request body (serialized).
        HttpRequestData geminiHttpReq;
        try
        {
            geminiHttpReq = _geminiTransformer.TransformRequest(request);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("gemini conversion failed: " + ex.Message, ex);
        }

        // 2. Unmarshal back to GenerateContentRequest to patch it.
        GenerateContentRequest geminiReq;
        try
        {
            geminiReq = LlmJson.Deserialize<GenerateContentRequest>(geminiHttpReq.Body)
                ?? throw new JsonException("body unmarshaled to null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("failed to unmarshal gemini request body: " + ex.Message, ex);
        }

        // 3. Patch the Gemini request for Antigravity.
        PatchGeminiRequest(geminiReq, request);

        // 4. Transform the model name for Antigravity API compatibility.
        // Antigravity requires tier suffixes for gemini-3-pro (e.g. gemini-3-pro-low).
        // The original model name is stored in metadata for the executor to route with.
        var transformedModel = AntigravityRouter.TransformModelForAntigravity(request.Model);

        // 5. Wrap in the Antigravity envelope.
        var envelope = AntigravityEnvelopeFactory.NewAntigravityEnvelope(_config.Project, transformedModel, geminiReq);

        byte[] body;
        try
        {
            body = GoJson.Marshal(envelope);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to marshal antigravity envelope: " + ex.Message, ex);
        }

        // 5. Build new headers.
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        AntigravityVersion.SetClientHeaders(headers);
        HttpClientUtils.HeaderSet(headers, "X-Opencode-Tools-Debug", "1");
        // DO NOT set X-Goog-Api-Key when using OAuth - it must be absent entirely;
        // setting it to an empty string triggers license error #3501.

        if (request.Stream == true)
        {
            HttpClientUtils.HeaderSet(headers, "Accept", "text/event-stream");
        }
        else
        {
            HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        }

        // Auth - OAuth only, no API key fallback.
        AuthConfig? authConfig;

        if (_tokenProvider is { } tokenProvider)
        {
            OAuthCredentials creds;
            try
            {
                creds = tokenProvider.Get(CancellationToken.None);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to get OAuth token: " + ex.Message, ex);
            }

            authConfig = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = creds.AccessToken,
            };
        }
        else
        {
            throw new InvalidOperationException("no OAuth token provider configured");
        }

        // URL.
        var url = BuildURL(request);

        var httpReq = new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = authConfig,
            ApiFormat = ApiFormats.GeminiContents,
        };

        // Store the original model name in metadata for executor routing.
        httpReq.Metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
        httpReq.Metadata["antigravity_model"] = request.Model;

        return httpReq;
    }

    /// <summary>Port of the 12 headersToStrip (transformer.go:146-159).</summary>
    private static readonly string[] HeadersToStrip =
    [
        "User-Agent",
        "Authorization",
        "Content-Type",
        "Accept",
        "Content-Length",
        "Host",
        "Connection",
        "Pragma",
        "Cache-Control",
        "Client-Metadata",
        "X-Goog-Api-Client",
        "X-Goog-Api-Key",
    ];

    /// <summary>Port of (*Transformer).patchGeminiRequest (transformer.go:249-403).</summary>
    private static void PatchGeminiRequest(GenerateContentRequest req, LlmRequest llmReq)
    {
        // A. Schema sanitization.
        // Priority: ResponseJsonSchema first (set by the Gemini transformer), then ResponseSchema.
        if (req.GenerationConfig is { } generationConfig)
        {
            var schemaElement = generationConfig.ResponseJsonSchema ?? generationConfig.ResponseSchema;
            if (schemaElement is { } schema)
            {
                var sanitized = SanitizeSchemaElement(schema);
                if (sanitized is { } sanitizedElement)
                {
                    // Set to ResponseSchema (what Antigravity expects) and clear
                    // ResponseJsonSchema to avoid sending both.
                    generationConfig.ResponseSchema = sanitizedElement;
                    generationConfig.ResponseJsonSchema = null;
                }
            }
        }

        // Sanitize tool schemas and convert to Antigravity format.
        var hasTools = false;

        foreach (var tool in req.Tools ?? [])
        {
            foreach (var fd in tool.FunctionDeclarations ?? [])
            {
                hasTools = true;

                // CRITICAL: Antigravity expects "parameters", not "parametersJsonSchema".
                // Priority: ParametersJsonSchema first (set by the Gemini transformer), then Parameters.
                var schemaElement = fd.ParametersJsonSchema ?? fd.Parameters;
                if (schemaElement is { } schema)
                {
                    var sanitized = SanitizeSchemaElement(schema);
                    if (sanitized is { } sanitizedElement)
                    {
                        fd.Parameters = sanitizedElement;
                        fd.ParametersJsonSchema = null;
                    }
                }
            }
        }

        // B. Tool config (VALIDATED mode for Claude/Antigravity).
        if (hasTools)
        {
            req.ToolConfig ??= new ToolConfig();
            req.ToolConfig.FunctionCallingConfig ??= new FunctionCallingConfig();
            req.ToolConfig.FunctionCallingConfig.Mode = "VALIDATED";

            // C. Tool hardening instruction.
            const string hardeningMsg = "CRITICAL: DO NOT guess tool parameters. ONLY use the exact parameter structure defined in the tool schema. Parameter names are EXACT.";

            if (req.SystemInstruction is null)
            {
                req.SystemInstruction = new Content
                {
                    Parts = [new Part { Text = hardeningMsg }],
                };
            }
            else
            {
                // Append to the existing system instruction.
                req.SystemInstruction.Parts ??= [];
                req.SystemInstruction.Parts.Add(new Part { Text = "\n\n" + hardeningMsg });
            }
        }

        // Inject the Antigravity system instruction (required for CLIProxy
        // compatibility): prepended, with the role set to "user".
        if (req.SystemInstruction is null)
        {
            req.SystemInstruction = new Content
            {
                Parts = [new Part { Text = AntigravityConstants.AntigravitySystemInstruction }],
            };
        }
        else if (req.SystemInstruction.Parts is { Count: > 0 } parts)
        {
            var firstPart = parts[0];
            firstPart.Text = AntigravityConstants.AntigravitySystemInstruction + "\n\n" + (firstPart.Text ?? "");
        }
        else
        {
            req.SystemInstruction.Parts = [new Part { Text = AntigravityConstants.AntigravitySystemInstruction }];
        }

        // Crucial: set role to "user" for the system instruction.
        req.SystemInstruction.Role = "user";

        // D. Thinking / cross-model sanitization.
        var lowerModel = llmReq.Model.ToLowerInvariant();
        var isClaude = lowerModel.Contains("claude", StringComparison.Ordinal);
        var isGemini3 = lowerModel.Contains("gemini-3", StringComparison.Ordinal);

        // Fix the thinking config for Gemini 3 vs 2.5.
        if (isGemini3)
        {
            req.GenerationConfig ??= new GenerationConfig();

            req.GenerationConfig.ThinkingConfig ??= new ThinkingConfig
            {
                // Default to 'low' thinking level for Gemini 3 as per reference:
                // Gemini 3 Flash/Pro require thinkingLevel (string), not budget.
                IncludeThoughts = true,
                ThinkingLevel = "low",
            };
        }

        if (isGemini3 && req.GenerationConfig?.ThinkingConfig is { } thinkingConfig)
        {
            // Gemini 3 uses ThinkingLevel (string); the budget must be nil.
            thinkingConfig.ThinkingBudget = null;
        }

        if (isClaude && req.Contents is { } contents)
        {
            foreach (var content in contents)
            {
                if (content.Parts is null)
                {
                    continue;
                }

                // Strip thinking parts.
                content.Parts = content.Parts.Where(static part => !part.Thought).ToList();
            }
        }
    }

    /// <summary>
    /// Port of the sanitize+uppercase+remarshal sequence applied to each schema
    /// (transformer.go:252-311): returns the sanitized element, or null when the
    /// schema is not an object and must be left untouched.
    /// </summary>
    internal static JsonElement? SanitizeSchemaElement(JsonElement element)
    {
        var goAny = AntigravitySanitizer.ToGoAny(element);
        if (goAny is not Dictionary<string, object?> schemaMap)
        {
            // Go json.Unmarshal into map[string]any fails for non-object roots and
            // the schema is left untouched; the literal null unmarshals into a nil
            // map, which sanitizes to nil and remarshals as "null".
            if (goAny is null && element.ValueKind == JsonValueKind.Null)
            {
                return AntigravitySanitizer.MarshalToElement(null);
            }

            return null;
        }

        // Sanitize, then uppercase all type values for the Antigravity API
        // (protobuf spec requires OBJECT/STRING/...).
        var sanitized = AntigravitySanitizer.SanitizeJSONSchema(schemaMap);
        sanitized = AntigravitySanitizer.UppercaseSchemaTypes(sanitized);

        return AntigravitySanitizer.MarshalToElement(sanitized);
    }

    /// <summary>Port of (*Transformer).buildURL (transformer.go:405-412).</summary>
    private string BuildURL(LlmRequest llmReq)
    {
        var action = llmReq.Stream == true ? "streamGenerateContent?alt=sse" : "generateContent";

        return TrimSingleTrailingSlash(_config.BaseURL) + "/v1internal:" + action;
    }

    private static string TrimSingleTrailingSlash(string url)
        => url.EndsWith('/') ? url[..^1] : url;

    /// <summary>Port of TransformResponse (transformer.go:415-447): unwrap, then delegate.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                "HTTP error " + response.StatusCode + ": " + Encoding.UTF8.GetString(response.Body ?? []));
        }

        // Antigravity returns { "response": { "candidates": [...] } };
        // unwrap it before passing to the Gemini transformer.
        byte[]? innerBody = null;

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;

            // Go json.Unmarshal into the envelope struct errors on non-object roots;
            // the error text is a deliberate simplification and a null body diverges
            // from Go's "empty response field" path (registered difference).
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "failed to unmarshal antigravity response envelope: cannot unmarshal non-object into envelope");
            }

            if (root.TryGetProperty("response", out var responseField))
            {
                innerBody = Encoding.UTF8.GetBytes(responseField.GetRawText());
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("failed to unmarshal antigravity response envelope: " + ex.Message, ex);
        }

        if (innerBody is null || innerBody.Length == 0)
        {
            throw new InvalidOperationException("empty response field in antigravity response");
        }

        // Create a fake HTTP response with the unwrapped body.
        var fakeResp = new HttpResponseData
        {
            StatusCode = response.StatusCode,
            Headers = response.Headers,
            Body = innerBody,
        };

        // Use the Gemini transformer to convert the response.
        return _geminiTransformer.TransformResponse(fakeResp);
    }

    /// <summary>Port of TransformError: delegates to the Gemini transformer.</summary>
    public ResponseError TransformError(HttpClientError? error)
        => _geminiTransformer.TransformError(error);

    /// <summary>Port of SetAPIKey.</summary>
    public void SetAPIKey(string apiKey)
    {
        _config.APIKey = apiKey;
        InitTokenProvider(apiKey);

        // Also update the internal gemini transformer.
        _geminiTransformer.SetAPIKey(apiKey);
    }

    /// <summary>Port of SetBaseURL.</summary>
    public void SetBaseURL(string baseURL)
    {
        _config.BaseURL = baseURL;
        _geminiTransformer.SetBaseURL(baseURL);
    }

    /// <summary>
    /// Port of AggregateStreamChunks: unwraps chunk copies (never mutating the
    /// input chunks) before delegating to the Gemini transformer.
    /// </summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        var unwrappedChunks = new StreamEvent[chunks.Count];

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            if (chunk is null)
            {
                continue;
            }

            // Copy the chunk.
            var newChunk = new StreamEvent
            {
                LastEventId = chunk.LastEventId,
                Type = chunk.Type,
                Data = chunk.Data,
                CleanEofCompletionEvidence = chunk.CleanEofCompletionEvidence,
                Size = chunk.Size,
                Headers = chunk.Headers,
            };
            unwrappedChunks[i] = newChunk;

            if (chunk.Data.Length == 0)
            {
                continue;
            }

            // Check for the DONE marker.
            if (Encoding.UTF8.GetString(chunk.Data) == "[DONE]")
            {
                continue;
            }

            // Unwrap logic.
            unwrappedChunks[i].Data = AntigravityStreamProcessor.UnwrapChunk(chunk.Data);
        }

        return _geminiTransformer.AggregateStreamChunks(request, unwrappedChunks, out meta);
    }

    /// <summary>Port of TransformStream: intercept events to unwrap the "response" envelope.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
    {
        var unwrappedStream = new MapStream<StreamEvent, StreamEvent>(stream, UnwrapStreamEvent);

        return _geminiTransformer.TransformStream(request, unwrappedStream);
    }

    private static StreamEvent? UnwrapStreamEvent(StreamEvent? ev)
    {
        if (ev is null || ev.Data.Length == 0)
        {
            return ev;
        }

        // Check if it's the DONE event.
        if (Encoding.UTF8.GetString(ev.Data) == "[DONE]")
        {
            return ev;
        }

        var inner = AntigravityStreamProcessor.UnwrapChunk(ev.Data);
        if (ReferenceEquals(inner, ev.Data))
        {
            return ev;
        }

        // Create a copy of the event to avoid mutating the original stream.
        return new StreamEvent
        {
            LastEventId = ev.LastEventId,
            Type = ev.Type,
            Data = inner,
            CleanEofCompletionEvidence = ev.CleanEofCompletionEvidence,
            Size = ev.Size,
            Headers = ev.Headers,
        };
    }

    /// <summary>
    /// Port of CustomizeExecutor: wraps the standard executor with
    /// Antigravity-specific endpoint fallback logic.
    /// </summary>
    public IExecutor CustomizeExecutor(IExecutor executor)
        => new AntigravityExecutor(executor);
}
