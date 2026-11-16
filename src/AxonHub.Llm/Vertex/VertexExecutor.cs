using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Vertex;

/// <summary>Port-only seams for <see cref="VertexExecutor"/> (tests inject transports).</summary>
public sealed class VertexExecutorOptions
{
    /// <summary>The handler used for Vertex AI API requests.</summary>
    public HttpMessageHandler? Transport { get; set; }

    /// <summary>The handler used to fetch Google Cloud access tokens.</summary>
    public HttpMessageHandler? TokenTransport { get; set; }

    /// <summary>The clock used for token expiry (tests inject one).</summary>
    public Func<DateTimeOffset>? Clock { get; set; }
}

/// <summary>
/// Port of vertex.Executor (llm/vertex/executor.go): a Vertex AI-specific
/// executor that signs requests with Google Cloud credentials and rewrites the
/// Anthropic Messages path into the Vertex rawPredict shape.
/// </summary>
public sealed class VertexExecutor : IExecutor
{
    private const string AnthropicVersion = "vertex-2023-10-16";

    private readonly string _region;
    private readonly string _projectId;
    private readonly string _baseUrl;
    private readonly HttpExecutor _inner;

    /// <summary>Port of NewExecutor.</summary>
    /// <exception cref="InvalidOperationException">
    /// Validation and client-construction failures, with the reference's exact
    /// messages.
    /// </exception>
    public VertexExecutor(
        string region,
        string projectId,
        GcpCredentials? credentials,
        VertexExecutorOptions? options = null)
    {
        if (string.IsNullOrEmpty(region))
        {
            throw new InvalidOperationException("region must be provided");
        }

        if (string.IsNullOrEmpty(projectId))
        {
            throw new InvalidOperationException("projectID must be provided");
        }

        if (credentials is null)
        {
            throw new InvalidOperationException("credentials must be provided");
        }

        options ??= new VertexExecutorOptions();

        try
        {
            // BuildHttpRequest strips Authorization and applies the request's
            // AuthConfig, so the OAuth bearer token is attached below HttpClient
            // (the reference's transport.NewHTTPClient does the same with an
            // oauth2.Transport).
            var handler = new GcpAuthHandler(
                credentials.TokenSource,
                options.Transport ?? new SocketsHttpHandler
                {
                    UseCookies = false,
                    // HttpExecutor follows redirects with Go's loop, so the
                    // default transport must not follow them itself.
                    AllowAutoRedirect = false,
                });
            _inner = new HttpExecutor(new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to create HTTP client: " + ex.Message, ex);
        }

        _region = region;
        _projectId = projectId;
        _baseUrl = region == "global"
            ? "https://aiplatform.googleapis.com/"
            : "https://" + region + "-aiplatform.googleapis.com/";
    }

    /// <summary>Port of NewExecutorFromJSON.</summary>
    public static VertexExecutor FromJson(
        string region,
        string projectId,
        string jsonData,
        VertexExecutorOptions? options = null)
    {
        options ??= new VertexExecutorOptions();

        GcpCredentials credentials;
        try
        {
            credentials = GcpCredentials.FromJson(jsonData, new GcpTokenOptions
            {
                Transport = options.TokenTransport,
                Clock = options.Clock,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to parse credentials: " + ex.Message, ex);
        }

        return new VertexExecutor(region, projectId, credentials, options);
    }

    /// <summary>Port of Do.</summary>
    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        var transformed = TransformOrThrow(request);
        return _inner.Do(cancellationToken, transformed);
    }

    /// <summary>Port of DoStream.</summary>
    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        var transformed = TransformOrThrow(request);
        return _inner.DoStream(cancellationToken, transformed);
    }

    private HttpRequestData TransformOrThrow(HttpRequestData request)
    {
        try
        {
            return TransformRequest(request);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to transform request: " + ex.Message, ex);
        }
    }

    /// <summary>Port of transformRequest (executor.go:101-154).</summary>
    internal HttpRequestData TransformRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The reference copies exactly these fields; everything else on the
        // port's richer request type is dropped.
        var transformed = new HttpRequestData
        {
            Method = request.Method,
            Url = request.Url,
            Headers = request.Headers,
            Body = request.Body,
            // Auth is removed because the HTTP client handles it.
            Auth = null,
            RequestId = request.RequestId,
            OnResponseHeaders = request.OnResponseHeaders,
        };

        var body = request.Body;
        if (body is not null)
        {
            if (!RawJsonGjson.HasTopLevelKey(body, "anthropic_version"u8))
            {
                // sjson.SetBytes errors are ignored by the reference.
                body = RawJsonPatch.SetTopLevelRaw(
                    body,
                    "anthropic_version"u8,
                    GoJson.Marshal(AnthropicVersion));
            }

            if (request.Url == "/v1/messages" && request.Method == "POST")
            {
                var model = RawJsonGjson.GetTopLevelString(body, "model"u8);
                if (model.Length == 0)
                {
                    throw new InvalidOperationException("model field is required");
                }

                // Remove model from the body: on Vertex it is part of the URL.
                body = RawJsonGjson.DeleteTopLevelKey(body, "model"u8);

                var stream = RawJsonGjson.GetTopLevelBool(body, "stream"u8);
                var specifier = stream ? "streamRawPredict" : "rawPredict";

                transformed.Url = _baseUrl + "v1/projects/" + _projectId
                    + "/locations/" + _region
                    + "/publishers/anthropic/models/" + model + ":" + specifier;
            }
            else if (request.Url == "/v1/messages/count_tokens" && request.Method == "POST")
            {
                transformed.Url = _baseUrl + "v1/projects/" + _projectId
                    + "/locations/" + _region
                    + "/publishers/anthropic/models/count-tokens:rawPredict";
            }
            else
            {
                // For other endpoints, just prepend the base URL.
                transformed.Url = _baseUrl + request.Url;
            }
        }

        transformed.Body = body;

        return transformed;
    }
}

/// <summary>
/// Port of the oauth2.Transport the reference's HTTP client wraps around the
/// token source: every request carries <c>Authorization: &lt;type&gt; &lt;token&gt;</c>,
/// and an empty access token leaves the request unauthenticated.
/// </summary>
internal sealed class GcpAuthHandler(IGcpTokenSource tokenSource, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = tokenSource.GetToken();
        if (token.AccessToken.Length > 0)
        {
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                token.Type + " " + token.AccessToken);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
