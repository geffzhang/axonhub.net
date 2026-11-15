using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of httpclient.HttpClient's transport methods (llm/httpclient/client.go):
/// executes the built request over <see cref="HttpClient"/> and reports a non-2xx
/// upstream response as <see cref="HttpClientError"/>, exactly where the Go
/// client does.
/// </summary>
/// <remarks>
/// The reference interface is synchronous (Go goroutines), so the async .NET
/// transport is bridged with <c>GetAwaiter().GetResult()</c>. The pipeline is
/// ported synchronously; there is no synchronization context under ASP.NET Core.
///
/// Redirects are followed here rather than by the transport, reproducing
/// <c>net/http.Client.do</c> (client.go:597-755) so that the
/// <see cref="HttpRedirectPolicy"/> hooks of client.go:110-151 can refuse a hop
/// before it is sent. The transport must therefore be created with
/// <c>AllowAutoRedirect = false</c>.
/// </remarks>
public sealed class HttpExecutor : IExecutor
{
    /// <summary>Go: <c>httpclient.MaxErrorBodySize</c> (1 MB).</summary>
    public const int MaxErrorBodySize = 1 << 20;

    /// <summary>Go: <c>defaultCheckRedirect</c>'s 10-hop cap (net/http/client.go:833-838).</summary>
    private const int MaxRedirects = 10;

    /// <summary>Go: <c>maxBodySlurpSize</c>, the redirect body drain cap (client.go:713-714).</summary>
    private const int MaxBodySlurpSize = 2 << 10;

    private readonly HttpClient _client;
    private readonly bool _rejectHttpsDowngrade;
    private readonly bool _rejectAll;
    private readonly bool _sameOrigin;

    public HttpExecutor(HttpClient client)
        : this(client, rejectHttpsDowngrade: false, rejectAll: false, sameOrigin: false)
    {
    }

    internal HttpClient Client => _client;

    /// <summary>
    /// Builds an executor over a transport with no total timeout, like Go's
    /// <c>http.Client</c> (llm/httpclient/client.go:106). The transport must not
    /// follow redirects itself; see the class remarks.
    /// </summary>
    public HttpExecutor(HttpMessageHandler transport)
        : this(
            new HttpClient(transport, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan },
            rejectHttpsDowngrade: false,
            rejectAll: false,
            sameOrigin: false)
    {
    }

    private HttpExecutor(
        HttpClient client,
        bool rejectHttpsDowngrade,
        bool rejectAll,
        bool sameOrigin)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _rejectHttpsDowngrade = rejectHttpsDowngrade;
        _rejectAll = rejectAll;
        _sameOrigin = sameOrigin;
    }

    /// <summary>
    /// client.go:110-130: a copy of this executor that follows redirects but
    /// refuses an HTTPS to HTTP hop. Installing the hook replaces Go's default
    /// 10-hop check, so this policy deliberately has no hop cap.
    /// </summary>
    public HttpExecutor WithRejectHttpsDowngrade()
        => new(_client, rejectHttpsDowngrade: true, _rejectAll, sameOrigin: false);

    /// <summary>
    /// client.go:132-144: a copy of this executor that refuses every redirect.
    /// The reference installs this hook on its own, replacing the previous one,
    /// so the downgrade check is not chained.
    /// </summary>
    public HttpExecutor WithRejectRedirects()
        => new(_client, rejectHttpsDowngrade: false, rejectAll: true, sameOrigin: false);

    /// <summary>
    /// codex_catalog.go:150-161: a copy of this executor that follows a
    /// redirect only while it stays on the initial request's scheme and
    /// authority, stopping the chain after ten hops. Both refusals use Go's
    /// <c>ErrUseLastResponse</c>, so the caller receives the 3xx response (a
    /// normal response for the port, like Go) instead of a redirect error.
    /// </summary>
    public HttpExecutor WithSameOriginRedirects()
        => new(_client, _rejectHttpsDowngrade, _rejectAll, sameOrigin: true);

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var built = HttpClientUtils.BuildHttpRequest(request);

        // Only set the default Accept when the transformer did not specify one
        // (e.g. TTS sets Accept: */* to receive binary audio).
        if (string.IsNullOrEmpty(HttpClientUtils.HeaderGet(built.Headers, "Accept")))
        {
            HttpClientUtils.HeaderSet(built.Headers, "Accept", "application/json");
        }

        var raw = SendObservingRedirectRefusal(request, built, cancellationToken, "HTTP request failed: ");

        Dictionary<string, List<string>> headers;
        try
        {
            headers = ReadHeaders(raw);
        }
        catch
        {
            raw.Dispose();
            throw;
        }

        request.ObserveResponseHeaders(headers);

        if ((int)raw.StatusCode >= 400)
        {
            var statusCode = (int)raw.StatusCode;
            var statusLine = StatusLine(raw);
            byte[] errorBody;
            try
            {
                errorBody = ReadCapped(raw.Content.ReadAsStream(cancellationToken));
            }
            finally
            {
                raw.Dispose();
            }

            throw new HttpClientError(
                built.Method,
                built.Url,
                statusCode,
                statusLine,
                errorBody)
            {
                Headers = headers,
            };
        }

        try
        {
            var body = ReadAll(raw.Content.ReadAsStream(cancellationToken));
            return new HttpResponseData
            {
                StatusCode = (int)raw.StatusCode,
                Headers = headers,
                Body = body,
                Request = request,
                RawRequest = raw.RequestMessage,
                // The body is fully consumed above, so the response stays usable
                // as metadata (Go keeps the closed-body *http.Response too).
                RawResponse = raw,
            };
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var built = HttpClientUtils.BuildHttpRequest(request);

        // Force SSE Accept unless the outbound transformer explicitly opted into
        // a non-JSON Accept (e.g. "*/*" for binary TTS chunks).
        var accept = HttpClientUtils.HeaderGet(built.Headers, "Accept");
        if (string.IsNullOrEmpty(accept) || string.Equals(accept, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            HttpClientUtils.HeaderSet(built.Headers, "Accept", "text/event-stream");
        }

        HttpClientUtils.HeaderSet(built.Headers, "Cache-Control", "no-cache");
        HttpClientUtils.HeaderSet(built.Headers, "Connection", "keep-alive");

        var raw = SendObservingRedirectRefusal(request, built, cancellationToken, "HTTP stream request failed: ");

        var headers = ReadHeaders(raw);
        request.ObserveResponseHeaders(headers);

        if ((int)raw.StatusCode >= 400)
        {
            var statusCode = (int)raw.StatusCode;
            var statusLine = StatusLine(raw);
            var errorBody = ReadCapped(raw.Content.ReadAsStream(cancellationToken));
            raw.Dispose();

            throw new HttpClientError(
                built.Method,
                built.Url,
                statusCode,
                statusLine,
                errorBody)
            {
                Headers = headers,
            };
        }

        // client.go:408-426 — decoder selection: the response Content-Type is
        // looked up case-sensitively, then as a parsed (lowercased) media type,
        // then falls back to SSE. The Ollama decoder registers
        // application/x-ndjson (decoder.go:14-16).
        var contentType = HttpClientUtils.HeaderGet(headers, "Content-Type");
        if (contentType.Length == 0)
        {
            contentType = "text/event-stream";
        }

        var bodyStream = raw.Content.ReadAsStream(cancellationToken);
        var binaryContentType = SelectBinaryDecoderContentType(contentType);
        IStream<StreamEvent> stream = IsAwsEventStreamContentType(contentType)
            ? new HttpAwsEventStream(bodyStream, raw)
            : binaryContentType is not null
                ? new HttpBinaryEventStream(bodyStream, binaryContentType, cancellationToken, raw)
                : IsNdjsonContentType(contentType)
                    ? new HttpNdjsonEventStream(bodyStream, raw)
                    : new HttpSseEventStream(bodyStream, raw);

        // Codex turn-state propagation: only the first event carries it.
        var firstEventHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (TryGetHeader(headers, "X-Codex-Turn-State", out var turnState))
        {
            firstEventHeaders["X-Codex-Turn-State"] = turnState;
        }

        IStream<StreamEvent> events = new FirstEventHeadersStream(stream, firstEventHeaders);

        return ResponseHeaders.WithResponseHeaders(
            events,
            ResponseHeaders.MergeForwardResponseHeaders(null, headers));
    }

    /// <summary>
    /// Bridges a refused redirect to the reference's error shape: client.go:287-293
    /// (and 381-387 for streams) observes the redirect response's headers — Go
    /// returns the 3xx response alongside the CheckRedirect error — and wraps the
    /// error as <c>HTTP request failed: %w</c>/<c>HTTP stream request failed: %w</c>.
    /// </summary>
    private HttpResponseMessage SendObservingRedirectRefusal(
        HttpRequestData request,
        BuiltHttpRequest built,
        CancellationToken cancellationToken,
        string errorPrefix)
    {
        try
        {
            return Send(built, cancellationToken);
        }
        catch (HttpRedirectRefusal refusal)
        {
            // A nil response (client.go:650-654, Location parse failure) has no
            // headers to observe.
            if (refusal.ResponseHeaders is not null)
            {
                request.ObserveResponseHeaders(refusal.ResponseHeaders);
            }

            throw new HttpRedirectException(errorPrefix + refusal.Message);
        }
    }

    /// <summary>
    /// Port of <c>net/http.Client.do</c>'s redirect loop (client.go:597-755).
    /// Every hop reuses the initial request's headers with Go's stripping rules,
    /// resolves Location against the current URL, rewrites POST-like methods to
    /// GET after 301/302/303, and replays the body after 307/308.
    /// </summary>
    private HttpResponseMessage Send(BuiltHttpRequest built, CancellationToken cancellationToken)
    {
        var request = BuildMessage(built.Method, built.Url, built.Headers, built.Body);
        var initialUri = request.RequestUri!;
        var sentUris = new List<Uri>();
        var currentUri = initialUri;
        var currentMethod = built.Method;
        var includeBody = true;
        var stripSensitiveHeaders = false;
        HttpResponseMessage? response = null;

        try
        {
            while (true)
            {
                if (sentUris.Count > 0)
                {
                    // client.go:642-649: evaluated on the iteration after the
                    // send. A 3xx without Location is the final response.
                    var location = FirstHeaderValue(response!, "Location");
                    if (location.Length == 0)
                    {
                        return response!;
                    }

                    Uri nextUri;
                    try
                    {
                        nextUri = new Uri(currentUri, location);
                    }
                    catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                    {
                        // client.go:650-654 with uerr at 622-638: resp becomes
                        // nil, and the URL is the current request's, password
                        // masked. The caller observes no headers.
                        throw new HttpRedirectRefusal(
                            RedirectError(
                                built.Method,
                                StripPassword(currentUri),
                                "failed to parse Location header " + GoCast.GoQuote(location)
                                    + ": parse " + GoCast.GoQuote(location) + ": " + ex.Message),
                            responseHeaders: null);
                    }

                    // client.go:688-693: sticky for the rest of the chain. Go
                    // compares the *initial* host with the destination; there
                    // is no port or scheme component in this check.
                    if (!stripSensitiveHeaders
                        && !string.Equals(initialUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase)
                        && !ShouldCopyHeaderOnRedirect(initialUri, nextUri))
                    {
                        stripSensitiveHeaders = true;
                    }

                    var nextHeaders = CopyRedirectHeaders(built.Headers, stripSensitiveHeaders, !includeBody);

                    // client.go:657-661: the explicit Referer copied from the
                    // initial headers wins over the computed one; the computed
                    // referer is never sent on an HTTPS to HTTP hop.
                    var referer = RefererFor(currentUri, nextUri, HttpClientUtils.HeaderGet(nextHeaders, "Referer"));
                    if (referer.Length > 0)
                    {
                        HttpClientUtils.HeaderSet(nextHeaders, "Referer", referer);
                    }

                    var nextRequest = BuildMessage(currentMethod, nextUri, nextHeaders, includeBody ? built.Body : null);

                    // client.go:696-705: the hook runs after the hop request is
                    // assembled. ErrUseLastResponse hands the redirect response
                    // back untouched: the next request is never sent and the
                    // redirect body stays unclosed.
                    var check = CheckRedirect(nextUri, sentUris);
                    if (check.Action == RedirectAction.UseLastResponse)
                    {
                        request.Dispose();
                        nextRequest.Dispose();
                        return response!;
                    }

                    // client.go:705-727: any other refusal drains and closes the
                    // redirect response, then reports the hook's error (Go still
                    // returns that response alongside it).
                    Dictionary<string, List<string>>? refusalHeaders = null;
                    if (check.Action == RedirectAction.Refuse)
                    {
                        refusalHeaders = ReadHeaders(response!);
                    }

                    request.Dispose();
                    SlurpAndDispose(response!);
                    response = null;

                    if (check.Action == RedirectAction.Refuse)
                    {
                        nextRequest.Dispose();

                        // client.go:724-726: the url.Error carries the raw
                        // Location value, not the resolved URL.
                        throw new HttpRedirectRefusal(
                            RedirectError(built.Method, location, check.Reason!),
                            refusalHeaders);
                    }

                    request = nextRequest;
                    currentUri = nextUri;
                }

                sentUris.Add(currentUri);
                response = _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .GetAwaiter()
                    .GetResult();

                var (method, shouldRedirect, hopIncludesBody) = RedirectBehaviorOf(currentMethod, response);
                if (!shouldRedirect)
                {
                    return response;
                }

                // client.go:684-687: includeBody is sticky for the chain — once
                // a hop drops the body, later hops do not resurrect it (and the
                // body headers stay stripped).
                includeBody &= hopIncludesBody;
                currentMethod = method;
            }
        }
        catch
        {
            request.Dispose();
            response?.Dispose();
            throw;
        }
    }

    private static HttpRequestMessage BuildMessage(
        string method,
        string url,
        Dictionary<string, List<string>> headers,
        byte[]? body)
    {
        var raw = new HttpRequestMessage(new HttpMethod(method), url);

        // Always attach content: Content-Type (and the other content headers)
        // cannot be added to HttpRequestMessage.Headers, and Go writes them on
        // bodyless requests too (e.g. the quota checkers' GET requests).
        raw.Content = new ByteArrayContent(body ?? []);

        foreach (var (key, values) in headers)
        {
            // Content headers (Content-Type, Content-Length, ...) are rejected by
            // HttpRequestMessage.Headers; they belong to the content.
            if (!raw.Headers.TryAddWithoutValidation(key, values) && raw.Content is not null)
            {
                raw.Content.Headers.TryAddWithoutValidation(key, values);
            }
        }

        return raw;
    }

    private static HttpRequestMessage BuildMessage(
        string method,
        Uri uri,
        Dictionary<string, List<string>> headers,
        byte[]? body)
        => BuildMessage(method, uri.AbsoluteUri, headers, body);

    /// <summary>
    /// net/http/client.go:512-540: which statuses redirect, what method the hop
    /// uses, and whether the body survives.
    /// </summary>
    private static (string Method, bool ShouldRedirect, bool IncludeBody) RedirectBehaviorOf(
        string requestMethod,
        HttpResponseMessage response)
    {
        switch ((int)response.StatusCode)
        {
            case 301:
            case 302:
            case 303:
                // Go keeps the historical browser rule: everything except
                // GET/HEAD becomes GET and loses its body.
                var method = string.Equals(requestMethod, "GET", StringComparison.Ordinal)
                    || string.Equals(requestMethod, "HEAD", StringComparison.Ordinal)
                        ? requestMethod
                        : "GET";
                return (method, true, false);
            case 307:
            case 308:
                // Go drops the hop when the initial body has no GetBody; the
                // port keeps the bytes, so GetBody always exists.
                return (requestMethod, true, true);
            default:
                return (requestMethod, false, true);
        }
    }

    /// <summary>
    /// The CheckRedirect chains of client.go:87-90, 112-144 and 146-151, plus the
    /// catalog's own hook (codex_catalog.go:150-161). <paramref name="via"/> is
    /// the list of requests already sent, oldest first.
    /// </summary>
    private RedirectCheck CheckRedirect(Uri next, List<Uri> via)
    {
        if (_sameOrigin)
        {
            // codex_catalog.go:150-155: a hop that leaves the initial request's
            // scheme or authority stops the chain with ErrUseLastResponse.
            if (via.Count > 0
                && (!string.Equals(via[0].Scheme, next.Scheme, StringComparison.Ordinal)
                    || !string.Equals(via[0].Authority, next.Authority, StringComparison.Ordinal)))
            {
                return RedirectCheck.UseLastResponse();
            }

            // codex_catalog.go:156-158: the hook chains the client's previous
            // CheckRedirect when it has one. The downgrade and reject-all hooks
            // are the only ones the port installs.
            if (_rejectHttpsDowngrade)
            {
                return IsHttpsDowngrade(via, next)
                    ? RedirectCheck.Refuse("refusing HTTPS to HTTP redirect")
                    : RedirectCheck.Allow();
            }

            if (_rejectAll)
            {
                return RedirectCheck.Refuse("refusing redirect for credential-bearing request");
            }

            // codex_catalog.go:159-161: with no previous hook, ten hops is the
            // last response, again via ErrUseLastResponse.
            return via.Count >= MaxRedirects
                ? RedirectCheck.UseLastResponse()
                : RedirectCheck.Allow();
        }

        if (_rejectHttpsDowngrade && IsHttpsDowngrade(via, next))
        {
            return RedirectCheck.Refuse("refusing HTTPS to HTTP redirect");
        }

        if (_rejectAll)
        {
            return RedirectCheck.Refuse("refusing redirect for credential-bearing request");
        }

        // Installing a CheckRedirect replaces defaultCheckRedirect, so only the
        // plain follower keeps the 10-hop cap (client.go:504-507, 833-838).
        if (!_rejectHttpsDowngrade && via.Count >= MaxRedirects)
        {
            return RedirectCheck.Refuse("stopped after 10 redirects");
        }

        return RedirectCheck.Allow();
    }

    /// <summary>client.go:146-151: HTTPS to HTTP on the most recent hop.</summary>
    private static bool IsHttpsDowngrade(List<Uri> via, Uri next)
        => via.Count > 0
            && string.Equals(via[^1].Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.Equals(next.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal);

    /// <summary>Go's <c>*url.Error</c> rendering: <c>%s %q: %s</c>.</summary>
    private static string RedirectError(string method, string url, string reason)
        => UrlErrorOp(method) + " " + GoCast.GoQuote(url) + ": " + reason;

    /// <summary>net/http/client.go:544-552.</summary>
    private static string UrlErrorOp(string method)
    {
        if (method.Length == 0)
        {
            return "Get";
        }

        foreach (var c in method)
        {
            if (c >= 0x80)
            {
                return method;
            }
        }

        // client.go:544-552: the first byte is preserved, the rest is lowered
        // when the whole method is ASCII (a non-ASCII method is returned as-is).
        return method[0] + method[1..].ToLowerInvariant();
    }

    /// <summary>Go's <c>Header.Get</c>: the first value under the canonical key.</summary>
    private static string FirstHeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            foreach (var value in values)
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Go's redirect bookkeeping (client.go:713-722): drain at most 2 KB of the
    /// redirect body, ignore read failures, and close it.
    /// </summary>
    private static void SlurpAndDispose(HttpResponseMessage response)
    {
        try
        {
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength is null or <= MaxBodySlurpSize)
            {
                using var stream = response.Content.ReadAsStream();
                var buffer = new byte[MaxBodySlurpSize];
                _ = stream.Read(buffer, 0, buffer.Length);
            }
        }
        catch
        {
            // net/http swallows errors from the redirect-body drain.
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// net/http/client.go:808-828: copy the initial request's headers, minus the
    /// credential-bearing set once the destination left the initial domain, and
    /// minus the body-describing set once a hop dropped the body.
    /// </summary>
    private static Dictionary<string, List<string>> CopyRedirectHeaders(
        Dictionary<string, List<string>> initial,
        bool stripSensitiveHeaders,
        bool stripBodyHeaders)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (key, values) in initial)
        {
            var canonical = HttpClientUtils.CanonicalHeaderKey(key);
            if (stripSensitiveHeaders && SensitiveHeaders.Contains(canonical))
            {
                continue;
            }

            if (stripBodyHeaders && BodyHeaders.Contains(canonical))
            {
                continue;
            }

            headers[canonical] = [.. values];
        }

        return headers;
    }

    /// <summary>net/http/client.go:814-816.</summary>
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.Ordinal)
    {
        "Authorization",
        "Www-Authenticate",
        "Cookie",
        "Cookie2",
        "Proxy-Authorization",
        "Proxy-Authenticate",
    };

    /// <summary>net/http/client.go:818-823.</summary>
    private static readonly HashSet<string> BodyHeaders = new(StringComparer.Ordinal)
    {
        "Content-Encoding",
        "Content-Language",
        "Content-Location",
        "Content-Type",
    };

    /// <summary>
    /// net/http/client.go:1005-1045: a header may follow the redirect only when
    /// the destination is the initial host or one of its subdomains.
    /// </summary>
    private static bool ShouldCopyHeaderOnRedirect(Uri initial, Uri dest)
        => IsDomainOrSubdomain(RedirectHostname(dest), RedirectHostname(initial));

    private static string RedirectHostname(Uri uri)
    {
        var host = uri.HostNameType is UriHostNameType.Dns ? uri.IdnHost : uri.Host;
        return host.Length > 1 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
    }

    private static bool IsDomainOrSubdomain(string sub, string parent)
    {
        if (string.Equals(sub, parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // client.go:1032-1034: a literal IP or zoned host never matches by
        // suffix.
        if (sub.Contains(':') || sub.Contains('%'))
        {
            return false;
        }

        return sub.EndsWith("." + parent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// net/http/client.go:147-171: the reference decision for a hop's Referer.
    /// </summary>
    private static string RefererFor(Uri last, Uri next, string explicitReferer)
    {
        // Never leak a referer across an HTTPS to HTTP hop.
        if (string.Equals(last.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            && string.Equals(next.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // An explicitly set Referer (copied from the initial headers) wins.
        if (explicitReferer.Length > 0)
        {
            return explicitReferer;
        }

        return StripUserInfo(last);
    }

    /// <summary>Go's <c>URL.String()</c> minus userinfo (client.go:159-169).</summary>
    private static string StripUserInfo(Uri uri)
    {
        if (uri.UserInfo.Length == 0)
        {
            return uri.AbsoluteUri;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// net/http/client.go:1047-1053: userinfo survives, the password becomes
    /// <c>***</c>. This is the URL form <c>uerr</c> reports for a Location the
    /// client cannot parse.
    /// </summary>
    private static string StripPassword(Uri uri)
    {
        if (uri.UserInfo.Length == 0 || !uri.UserInfo.Contains(':'))
        {
            return uri.AbsoluteUri;
        }

        var builder = new UriBuilder(uri)
        {
            Password = "***",
        };
        return builder.Uri.AbsoluteUri;
    }

    /// <summary>
    /// A hop the policy refuses, or a Location the port cannot parse, carrying
    /// the redirect response's headers when Go still hands that response back
    /// to the caller (client.go:719-727).
    /// </summary>
    private sealed class HttpRedirectRefusal(string message, Dictionary<string, List<string>>? responseHeaders)
        : Exception(message)
    {
        public Dictionary<string, List<string>>? ResponseHeaders { get; } = responseHeaders;
    }

    /// <summary>The three outcomes of a Go <c>CheckRedirect</c> hook.</summary>
    private enum RedirectAction
    {
        /// <summary>The hook returned nil: send the hop.</summary>
        Allow,

        /// <summary>The hook returned an error: close the 3xx body, surface the error.</summary>
        Refuse,

        /// <summary>
        /// The hook returned <c>ErrUseLastResponse</c>: do not send the hop and
        /// return the 3xx response with its body still open (client.go:699-705).
        /// </summary>
        UseLastResponse,
    }

    private readonly record struct RedirectCheck(RedirectAction Action, string? Reason)
    {
        public static RedirectCheck Allow() => new(RedirectAction.Allow, null);

        public static RedirectCheck Refuse(string reason) => new(RedirectAction.Refuse, reason);

        public static RedirectCheck UseLastResponse() => new(RedirectAction.UseLastResponse, null);
    }

    private static Dictionary<string, List<string>> ReadHeaders(HttpResponseMessage response)
    {
        // Go's net/http stores response headers under textproto canonical keys;
        // .NET uses its own casing for known descriptors (X-Request-ID), so
        // re-canonicalize to keep HeaderGet's ordinal lookup equivalent.
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (key, values) in response.Headers)
        {
            headers[HttpClientUtils.CanonicalHeaderKey(key)] = [.. values];
        }

        foreach (var (key, values) in response.Content.Headers)
        {
            var canonical = HttpClientUtils.CanonicalHeaderKey(key);
            if (headers.TryGetValue(canonical, out var existing))
            {
                existing.AddRange(values);
            }
            else
            {
                headers[canonical] = [.. values];
            }
        }

        return headers;
    }

    private static bool TryGetHeader(
        Dictionary<string, List<string>> headers,
        string name,
        out List<string> values)
    {
        foreach (var (key, headerValues) in headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                values = headerValues;
                return true;
            }
        }

        values = [];
        return false;
    }

    private static string StatusLine(HttpResponseMessage response)
    {
        var reason = response.ReasonPhrase ?? string.Empty;
        return ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (reason.Length > 0 ? " " + reason : string.Empty);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private static byte[] ReadCapped(Stream stream)
    {
        using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (buffer.Length < MaxErrorBodySize)
            {
                var toRead = (int)Math.Min(chunk.Length, MaxErrorBodySize - buffer.Length);
                var read = stream.Read(chunk, 0, toRead);
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    /// <summary>
    /// decoder.go init: content types routed to the binary chunk decoder,
    /// keyed by the exact registered string. Everything else (including the
    /// SSE entries) falls back to the default SSE decoder.
    /// </summary>
    private static readonly Dictionary<string, string> BinaryDecoderContentTypes = new(StringComparer.Ordinal)
    {
        ["audio/mpeg"] = "audio/mpeg",
        ["audio/mp3"] = "audio/mpeg",
        ["audio/wav"] = "audio/wav",
        ["audio/x-wav"] = "audio/wav",
        ["audio/opus"] = "audio/opus",
        ["audio/aac"] = "audio/aac",
        ["audio/flac"] = "audio/flac",
        ["audio/pcm"] = "audio/pcm",
        ["application/octet-stream"] = "application/octet-stream",
    };

    /// <summary>
    /// Port of the decoder lookup at client.go:412-422: exact registry hit
    /// first, then the parsed (lowercased) media type. Null means SSE.
    /// </summary>
    private static string? SelectBinaryDecoderContentType(string contentType)
    {
        if (BinaryDecoderContentTypes.TryGetValue(contentType, out var canonical))
        {
            return canonical;
        }

        if (GoMimeTypes.TryParseMediaType(contentType, out var mediaType, out _)
            && BinaryDecoderContentTypes.TryGetValue(mediaType, out canonical))
        {
            return canonical;
        }

        return null;
    }

    /// <summary>
    /// decoder.go:14-16: the Ollama package registers the NDJSON decoder under
    /// these two exact keys; the lookup below also parses the media type.
    /// </summary>
    private const string NdjsonContentType = "application/x-ndjson";

    private const string NdjsonCharsetContentType = "application/x-ndjson; charset=utf-8";

    /// <summary>
    /// llm/bedrock/register.go: the Bedrock package registers this content type
    /// with the AWS EventStream decoder.
    /// </summary>
    private const string AwsEventStreamContentType = "application/vnd.amazon.eventstream";

    private static bool IsAwsEventStreamContentType(string contentType)
        => string.Equals(contentType, AwsEventStreamContentType, StringComparison.Ordinal)
            || (GoMimeTypes.TryParseMediaType(contentType, out var mediaType, out _)
                && string.Equals(mediaType, AwsEventStreamContentType, StringComparison.Ordinal));

    private static bool IsNdjsonContentType(string contentType)
        => string.Equals(contentType, NdjsonContentType, StringComparison.Ordinal)
            || string.Equals(contentType, NdjsonCharsetContentType, StringComparison.Ordinal)
            || (GoMimeTypes.TryParseMediaType(contentType, out var mediaType, out _)
                && string.Equals(mediaType, NdjsonContentType, StringComparison.Ordinal));

    /// <summary>Attaches headers only to the first event, like the reference map.</summary>
    private sealed class FirstEventHeadersStream(
        IStream<StreamEvent> stream,
        Dictionary<string, List<string>> firstEventHeaders) : IStream<StreamEvent>
    {
        private bool _first = true;

        public StreamEvent? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool Next()
        {
            if (!stream.Next())
            {
                return false;
            }

            if (_first && stream.Current is { } current)
            {
                if (firstEventHeaders.Count > 0)
                {
                    current.Headers = firstEventHeaders;
                }

                _first = false;
            }

            return true;
        }

        public void Dispose() => stream.Dispose();
    }
}
