using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>
/// Port of antigravity.Executor (llm/transformer/antigravity/executor.go):
/// wraps the transport executor with endpoint fallback (Daily -> Autopush ->
/// Prod) and per-model endpoint health cooldowns.
/// </summary>
public sealed class AntigravityExecutor : IExecutor
{
    private readonly IExecutor _inner;
    private readonly AntigravityHealthTracker _healthTracker;

    private string _modelName = string.Empty;

    /// <summary>Port of NewExecutor: nil inner becomes the default HTTP transport.</summary>
    public AntigravityExecutor(IExecutor? inner)
    {
        // Go's NewHttpClient has no total timeout (http.Client{}); the port keeps
        // the .NET default of 100s on this default transport (registered
        // difference). Redirects are followed by HttpExecutor with Go's loop.
        _inner = inner ?? new HttpExecutor(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
        }));
        _healthTracker = new AntigravityHealthTracker();
    }

    /// <summary>
    /// Port of SetModelName: sets the model name for the next request; used to
    /// determine the initial endpoint preference.
    /// </summary>
    public void SetModelName(string modelName)
    {
        _modelName = modelName;
    }

    /// <summary>Port of extractModelName: request metadata wins over the configured model.</summary>
    private string ExtractModelName(HttpRequestData? request)
    {
        var modelName = _modelName;

        if (request?.Metadata is { } metadata && metadata.TryGetValue("antigravity_model", out var model))
        {
            modelName = model;
        }

        return modelName;
    }

    /// <summary>
    /// Port of Do: executes with endpoint fallback. A thrown transport error
    /// that is not retryable stops the sequence immediately; scripted executors
    /// may also return non-2xx responses directly, which follow the same rules.
    /// </summary>
    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        var modelName = ExtractModelName(request);
        var availableEndpoints = AvailableEndpoints(modelName, out var skippedEndpoints);

        // If all endpoints are in cooldown, fail fast.
        if (availableEndpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "all antigravity endpoints in cooldown for model " + modelName);
        }

        HttpResponseData? lastResp = null;
        Exception? lastErr = null;

        for (var i = 0; i < availableEndpoints.Count; i++)
        {
            var endpoint = availableEndpoints[i];
            var reqCopy = CloneRequestForEndpoint(request, endpoint);

            HttpResponseData resp;
            Exception? err = null;

            try
            {
                resp = _inner.Do(cancellationToken, reqCopy);
            }
            catch (Exception ex)
            {
                err = ex;
                resp = null!;
            }

            // If successful (2xx), return immediately.
            if (err is null && resp.StatusCode >= 200 && resp.StatusCode < 300)
            {
                _healthTracker.RecordSuccess(modelName, endpoint);

                return resp;
            }

            // Store the error/response for potential return.
            lastErr = err;
            lastResp = err is null ? resp : null;

            // Check if we should try the next endpoint.
            var statusCode = 0;
            if (err is null)
            {
                statusCode = resp.StatusCode;
            }
            else if (GoErrors.As<HttpClientError>(err) is { } httpErr)
            {
                statusCode = httpErr.StatusCode;
            }

            if (statusCode > 0 && AntigravityRouter.ShouldRetryWithDifferentEndpoint(statusCode))
            {
                _healthTracker.RecordFailure(modelName, endpoint, statusCode);

                continue;
            }

            // Non-retryable error or network error - stop trying.
            if (err is not null)
            {
                throw err;
            }

            // Non-retryable status returned directly (scripted executor).
            return resp;
        }

        // All available endpoints exhausted.
        if (lastResp is not null)
        {
            return lastResp;
        }

        throw new InvalidOperationException("all antigravity endpoints failed: " + lastErr?.Message, lastErr);
    }

    /// <summary>
    /// Port of DoStream: streaming fallback. Generic transport errors (no
    /// status) retry the next endpoint; every failure is recorded.
    /// </summary>
    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        var modelName = ExtractModelName(request);
        var availableEndpoints = AvailableEndpoints(modelName, out _);

        // If all endpoints are in cooldown, fail fast.
        if (availableEndpoints.Count == 0)
        {
            throw new InvalidOperationException(
                "all antigravity endpoints in cooldown for model " + modelName);
        }

        Exception? lastErr = null;

        for (var i = 0; i < availableEndpoints.Count; i++)
        {
            var endpoint = availableEndpoints[i];
            var reqCopy = CloneRequestForEndpoint(request, endpoint);

            IStream<StreamEvent> stream;
            Exception? err = null;

            try
            {
                stream = _inner.DoStream(cancellationToken, reqCopy);
            }
            catch (Exception ex)
            {
                err = ex;
                stream = null!;
            }

            // If successful, return immediately.
            if (err is null)
            {
                _healthTracker.RecordSuccess(modelName, endpoint);

                return stream;
            }

            lastErr = err;

            var statusCode = 0;
            if (GoErrors.As<HttpClientError>(err) is { } httpErr)
            {
                statusCode = httpErr.StatusCode;
            }

            // Every streaming failure is recorded; status 0 means transport error.
            _healthTracker.RecordFailure(modelName, endpoint, statusCode);

            if (statusCode > 0 && AntigravityRouter.ShouldRetryWithDifferentEndpoint(statusCode))
            {
                continue;
            }

            if (statusCode > 0)
            {
                // Known non-retryable status (e.g. 400) - return the error immediately.
                throw err;
            }

            // Generic error without a status: preserve aggressive retry for streaming.
        }

        throw new InvalidOperationException("all antigravity stream endpoints failed: " + lastErr?.Message, lastErr);
    }

    /// <summary>
    /// Port of getEndpointsInOrder + the cooldown filter: the quota-preferred
    /// endpoint first, then the remaining fallbacks, minus endpoints in cooldown.
    /// </summary>
    private List<string> AvailableEndpoints(string modelName, out List<string> skippedEndpoints)
    {
        skippedEndpoints = [];

        var allEndpoints = GetEndpointsInOrder(modelName);
        var available = new List<string>(allEndpoints.Count);

        foreach (var endpoint in allEndpoints)
        {
            if (_healthTracker.ShouldSkip(modelName, endpoint))
            {
                skippedEndpoints.Add(endpoint);
            }
            else
            {
                available.Add(endpoint);
            }
        }

        return available;
    }

    /// <summary>Port of getEndpointsInOrder: the preferred endpoint first, then the rest.</summary>
    internal static List<string> GetEndpointsInOrder(string modelName)
    {
        if (modelName.Length == 0)
        {
            // No model specified, use default fallback order.
            return AntigravityRouter.GetFallbackEndpoints();
        }

        // Determine quota preference for this model.
        var quotaPreference = AntigravityRouter.DetermineQuotaPreference(modelName);
        var initialEndpoint = AntigravityRouter.GetInitialEndpoint(quotaPreference);

        // Reorder so the preferred endpoint is first.
        var ordered = new List<string> { initialEndpoint };

        foreach (var ep in AntigravityRouter.GetFallbackEndpoints())
        {
            if (ep != initialEndpoint)
            {
                ordered.Add(ep);
            }
        }

        return ordered;
    }

    /// <summary>
    /// Port of cloneRequestForEndpoint: a shallow copy with the mutable members
    /// deep-copied (Go <c>copied := *request</c> plus the slice/map clones), with
    /// the URL pointed at the given endpoint.
    /// </summary>
    internal static HttpRequestData CloneRequestForEndpoint(HttpRequestData request, string endpoint)
    {
        // Go copies the struct value; every slice and map member is shared
        // until explicitly cloned below.
        var copied = request.ShallowClone();

        if (request.Headers is { } headers)
        {
            copied.Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (k, v) in headers)
            {
                copied.Headers[k] = [.. v];
            }
        }

        if (request.Query is { Count: > 0 } query)
        {
            copied.Query = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (k, v) in query)
            {
                if (v is not null)
                {
                    copied.Query[k] = [.. v];
                }
            }
        }

        if (request.Body is { Length: > 0 } body)
        {
            copied.Body = (byte[])body.Clone();
        }

        if (request.JsonBody is { Length: > 0 } jsonBody)
        {
            copied.JsonBody = (byte[])jsonBody.Clone();
        }

        if (request.Metadata is { Count: > 0 } metadata)
        {
            copied.Metadata = new Dictionary<string, string>(metadata, StringComparer.Ordinal);
        }

        if (request.TransformerMetadata is { Count: > 0 } transformerMetadata)
        {
            copied.TransformerMetadata = new Dictionary<string, System.Text.Json.JsonElement>(
                transformerMetadata,
                StringComparer.Ordinal);
        }

        // Update the URL to use the new endpoint while preserving the path.
        copied.Url = ReplaceBaseURL(request.Url, endpoint);

        return copied;
    }

    /// <summary>
    /// Port of replaceBaseURL: replaces the origin of a
    /// <c>{endpoint}/v1internal:{action}</c> URL while preserving the path,
    /// query, and fragment. Returns the original URL on any parse surprise.
    /// </summary>
    internal static string ReplaceBaseURL(string originalURL, string newBase)
    {
        if (!Uri.TryCreate(originalURL, UriKind.Absolute, out var parsed))
        {
            return originalURL;
        }

        if (parsed.AbsolutePath.Length <= 1)
        {
            return originalURL;
        }

        if (!parsed.AbsolutePath.StartsWith("/v1internal", StringComparison.Ordinal))
        {
            return originalURL;
        }

        if (!Uri.TryCreate(newBase, UriKind.Absolute, out var newBaseParsed))
        {
            return originalURL;
        }

        var builder = new UriBuilder(newBaseParsed)
        {
            Path = parsed.AbsolutePath,
            Query = parsed.Query.Length > 0 ? parsed.Query[1..] : string.Empty,
            Fragment = parsed.Fragment.Length > 0 ? parsed.Fragment[1..] : string.Empty,
        };

        return builder.Uri.AbsoluteUri;
    }
}
