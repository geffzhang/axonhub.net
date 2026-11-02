using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>Port of <c>SSODeviceOptions</c> (sso_device.go:23-26).</summary>
public sealed class SsoDeviceOptions
{
    /// <summary>
    /// Port of <c>HTTPClient</c>. When null the flow builds its own client
    /// (90s timeout, manual redirects). An injected handler must not follow
    /// redirects: the reference overrides <c>CheckRedirect</c> on a client
    /// copy, which .NET cannot impose on a caller-provided handler — the flow
    /// resolves redirects manually either way.
    /// </summary>
    public HttpMessageHandler? Transport { get; set; }

    /// <summary>
    /// Port of <c>Sleep</c> (<c>func(context.Context, time.Duration) error</c>):
    /// return an exception to abort the flow, null when the delay completed.
    /// </summary>
    public Func<CancellationToken, TimeSpan, Exception?>? Sleep { get; set; }
}

/// <summary>Port of <c>ssoDeviceResponse</c> (sso_device.go:34-40).</summary>
public sealed class SsoDeviceResponse
{
    [JsonPropertyName("device_code")]
    public string? DeviceCode { get; set; }

    [JsonPropertyName("user_code")]
    public string? UserCode { get; set; }

    [JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    /// <summary>Go's <c>int</c> is 64-bit on the supported platforms.</summary>
    [JsonPropertyName("interval")]
    public long? Interval { get; set; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; set; }
}

/// <summary>Port of <c>ssoTokenResponse</c> (sso_device.go:42-51).</summary>
public sealed class SsoTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("id_token")]
    public string? IdToken { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_description")]
    public string? ErrorDescription { get; set; }
}

/// <summary>
/// Port of <c>ConvertSSOToBuild</c> and the device flow behind it
/// (sso_device.go): turn an xAI web SSO cookie into OAuth credentials through
/// the CLI's device authorization flow, with manual redirect resolution and a
/// 2 MiB response cap.
/// </summary>
/// <remarks>
/// The cookie jar is managed explicitly (manual <c>Cookie</c> header plus
/// response <c>Set-Cookie</c> parsing) because an injected
/// <see cref="SsoDeviceOptions.Transport"/> cannot be configured for cookies.
/// Go's unreachable <c>create xAI SSO cookie jar</c> error has no .NET
/// equivalent: <see cref="CookieContainer"/> construction cannot fail. Cookie
/// values that .NET rejects outright (for example a space in the SSO token)
/// surface as <see cref="CookieException"/> where the reference stores and
/// sends them verbatim and fails later at the authorization check.
/// </remarks>
public static class SubscriptionSsoDevice
{
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string AcceptHeader = "application/json, text/html;q=0.9, */*;q=0.8";
    private const string AcceptLanguage = "en-US,en;q=0.9";
    private const int MaxResponseBody = 2 << 20;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private const int MaxRedirects = 9;

    /// <summary>Port of <c>ConvertSSOToBuild</c> (sso_device.go:53-78).</summary>
    public static OAuthCredentials ConvertSsoToBuild(
        CancellationToken cancellationToken,
        string rawToken,
        SsoDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var token = SubscriptionSso.NormalizeSsoToken(rawToken);
        if (token.Length == 0)
        {
            throw new InvalidOperationException("xAI SSO token is required");
        }

        var jar = new CookieContainer();
        SeedSsoCookies(jar, token);

        HttpClient client;
        if (options.Transport is not null)
        {
            client = new HttpClient(options.Transport, disposeHandler: false) { Timeout = Timeout };
        }
        else
        {
            client = new HttpClient(
                new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            {
                Timeout = Timeout,
            };
        }

        using (client)
        {
            var flow = new SsoDeviceFlow(client, jar, options.Sleep ?? DefaultSleep);
            return flow.Convert(cancellationToken);
        }
    }

    private sealed class SsoDeviceFlow
    {
        private readonly HttpClient _client;
        private readonly CookieContainer _jar;
        private readonly Func<CancellationToken, TimeSpan, Exception?> _sleep;

        public SsoDeviceFlow(
            HttpClient client,
            CookieContainer jar,
            Func<CancellationToken, TimeSpan, Exception?> sleep)
        {
            _client = client;
            _jar = jar;
            _sleep = sleep;
        }

        /// <summary>Port of <c>(*ssoDeviceFlow).convert</c> (sso_device.go:80-143).</summary>
        public OAuthCredentials Convert(CancellationToken cancellationToken)
        {
            var result = Do(cancellationToken, "GET", SubscriptionConstants.SsoAccountsUrl, null);
            if (result.Error is not null)
            {
                throw Rethrow(result.Error);
            }

            if (result.Status == (int)HttpStatusCode.Unauthorized
                || result.FinalUrl.Contains("sign-in", StringComparison.Ordinal)
                || result.FinalUrl.Contains("sign-up", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("xAI SSO token is unauthorized");
            }

            if (result.Status < 200 || result.Status >= 400)
            {
                throw new InvalidOperationException(StatusCodeMessage("validate xAI SSO", result.Status));
            }

            result = Do(
                cancellationToken,
                "POST",
                SubscriptionConstants.SsoDeviceUrl,
                Form(
                    ("client_id", SubscriptionConstants.ClientId),
                    ("scope", SubscriptionConstants.SsoScopes)));
            if (result.Error is not null)
            {
                throw Rethrow(result.Error);
            }

            if (result.Status < 200 || result.Status >= 300)
            {
                throw new InvalidOperationException(StatusCodeMessage("start xAI device flow", result.Status));
            }

            SsoDeviceResponse device;
            try
            {
                // Go: unmarshaling JSON null into a struct is a no-op.
                device = LlmJson.Deserialize<SsoDeviceResponse>(result.Body) ?? new SsoDeviceResponse();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("parse xAI device flow response: " + ex.Message, ex);
            }

            if (string.IsNullOrEmpty(device.DeviceCode)
                || string.IsNullOrEmpty(device.UserCode)
                || !TrustedXaiUrl(device.VerificationUriComplete ?? string.Empty))
            {
                throw new InvalidOperationException("xAI device flow response is incomplete");
            }

            if (device.Interval is null or <= 0)
            {
                device.Interval = 5;
            }

            if (device.ExpiresIn is null or <= 0)
            {
                device.ExpiresIn = 1800;
            }

            result = Do(cancellationToken, "GET", device.VerificationUriComplete!, null);
            if (result.Error is not null)
            {
                throw new InvalidOperationException(
                    "open xAI device verification: " + result.Error.Message,
                    result.Error);
            }

            if (result.Status < 200 || result.Status >= 400)
            {
                throw new InvalidOperationException(
                    StatusCodeMessage("open xAI device verification", result.Status));
            }

            result = Do(
                cancellationToken,
                "POST",
                SubscriptionConstants.SsoVerifyUrl,
                Form(("user_code", device.UserCode!)));
            if (result.Error is not null)
            {
                throw new InvalidOperationException(
                    "verify xAI device code: " + result.Error.Message,
                    result.Error);
            }

            if (result.Status < 200 || result.Status >= 400)
            {
                throw new InvalidOperationException(
                    StatusCodeMessage("verify xAI device code", result.Status));
            }

            if (!result.FinalUrl.Contains("consent", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("verify xAI device code did not reach consent");
            }

            result = Do(
                cancellationToken,
                "POST",
                SubscriptionConstants.SsoApproveUrl,
                Form(
                    ("user_code", device.UserCode!),
                    ("action", "allow"),
                    ("principal_type", "User"),
                    ("principal_id", string.Empty)));
            if (result.Error is not null)
            {
                throw new InvalidOperationException(
                    "approve xAI device code: " + result.Error.Message,
                    result.Error);
            }

            if (result.Status < 200 || result.Status >= 400)
            {
                throw new InvalidOperationException(
                    StatusCodeMessage("approve xAI device code", result.Status));
            }

            if (!result.FinalUrl.Contains("done", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("approve xAI device code did not reach done");
            }

            return PollToken(cancellationToken, device);
        }

        /// <summary>Port of <c>(*ssoDeviceFlow).pollToken</c> (sso_device.go:145-188).</summary>
        private OAuthCredentials PollToken(CancellationToken cancellationToken, SsoDeviceResponse device)
        {
            var interval = TimeSpan.FromSeconds(device.Interval!.Value);
            // min(expires_in, 75s); the reference multiplies by time.Second first.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Min(device.ExpiresIn!.Value, 75));
            while (DateTimeOffset.UtcNow < deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                var sleepFor = interval < remaining ? interval : remaining;
                var sleepError = _sleep(cancellationToken, sleepFor);
                if (sleepError is not null)
                {
                    throw Rethrow(sleepError);
                }

                var result = Do(
                    cancellationToken,
                    "POST",
                    SubscriptionConstants.TokenUrl,
                    Form(
                        ("grant_type", DeviceCodeGrantType),
                        ("client_id", SubscriptionConstants.ClientId),
                        ("device_code", device.DeviceCode!)));
                if (result.Error is not null)
                {
                    throw Rethrow(result.Error);
                }

                SsoTokenResponse token;
                try
                {
                    // Parsed before the status check, exactly like the reference.
                    token = LlmJson.Deserialize<SsoTokenResponse>(result.Body) ?? new SsoTokenResponse();
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        "parse xAI device token response: " + ex.Message,
                        ex);
                }

                if (result.Status >= 200 && result.Status < 300
                    && !string.IsNullOrEmpty(token.AccessToken))
                {
                    var expiresIn = token.ExpiresIn is null or <= 0
                        ? (long)TimeSpan.FromHours(6).TotalSeconds
                        : token.ExpiresIn.Value;
                    var tokenType = GoStrings.TrimSpace(token.TokenType ?? string.Empty).Length == 0
                        ? "Bearer"
                        : token.TokenType!;

                    return new OAuthCredentials
                    {
                        AccessToken = token.AccessToken,
                        RefreshToken = token.RefreshToken ?? string.Empty,
                        IdToken = token.IdToken ?? string.Empty,
                        ClientId = SubscriptionConstants.ClientId,
                        TokenType = tokenType,
                        Scopes = OAuthCredentialsJson.SplitFields(token.Scope ?? string.Empty),
                        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
                    };
                }

                switch (token.Error)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval += TimeSpan.FromSeconds(5);
                        continue;
                    case "access_denied":
                    case "expired_token":
                        throw new InvalidOperationException("xAI device authorization failed: " + token.Error);
                    default:
                        throw new InvalidOperationException(
                            "xAI device token polling failed: "
                            + GoStrings.TrimSpace((token.ErrorDescription ?? string.Empty) + " " + (token.Error ?? string.Empty)));
                }
            }

            throw new InvalidOperationException("xAI device token polling timed out");
        }

        /// <summary>Port of <c>(*ssoDeviceFlow).do</c> (sso_device.go:190-237).</summary>
        private SsoHttpResult Do(
            CancellationToken cancellationToken,
            string method,
            string endpoint,
            Dictionary<string, List<string>>? form)
        {
            var currentUrl = endpoint;
            var currentMethod = method;
            var currentForm = form;

            for (var attempt = 0; attempt < MaxRedirects; attempt++)
            {
                if (!TrustedXaiUrl(currentUrl))
                {
                    return new SsoHttpResult(
                        0,
                        currentUrl,
                        [],
                        new InvalidOperationException("xAI OAuth URL is not trusted"));
                }

                HttpRequestMessage request;
                try
                {
                    request = BuildRequest(currentMethod, currentUrl, currentForm);
                }
                catch (Exception ex)
                {
                    return new SsoHttpResult(0, currentUrl, [], ex);
                }

                using (request)
                {
                    HttpResponseMessage response;
                    try
                    {
                        // SendAsync so an injected handler only has to override
                        // the async entry point (HttpExecutor's convention).
                        response = _client
                            .SendAsync(request, cancellationToken)
                            .GetAwaiter()
                            .GetResult();
                    }
                    catch (Exception ex)
                    {
                        return new SsoHttpResult(0, currentUrl, [], ex);
                    }

                    using (response)
                    {
                        StoreCookies(request.RequestUri!, response);

                        var status = (int)response.StatusCode;
                        byte[] data;
                        try
                        {
                            // Go's Client substitutes http.NoBody when a
                            // response carries no Content.
                            data = ReadLimited(
                                response.Content is null
                                    ? Stream.Null
                                    : response.Content.ReadAsStream(cancellationToken));
                        }
                        catch (Exception ex)
                        {
                            return new SsoHttpResult(status, currentUrl, [], ex);
                        }

                        if (data.Length > MaxResponseBody)
                        {
                            return new SsoHttpResult(
                                status,
                                currentUrl,
                                [],
                                new InvalidOperationException("xAI OAuth response exceeds 2 MiB"));
                        }

                        if (status < 300 || status > 399)
                        {
                            return new SsoHttpResult(status, currentUrl, data, null);
                        }

                        var location = response.Headers.Location;
                        if (location is null)
                        {
                            return new SsoHttpResult(
                                status,
                                currentUrl,
                                data,
                                new InvalidOperationException(
                                    "resolve xAI OAuth redirect: http: no Location header in response"));
                        }

                        currentUrl = new Uri(request.RequestUri!, location).ToString();
                        if (status == (int)HttpStatusCode.SeeOther
                            || ((status == (int)HttpStatusCode.MovedPermanently
                                    || status == (int)HttpStatusCode.Found)
                                && currentMethod != "GET"))
                        {
                            currentMethod = "GET";
                            currentForm = null;
                        }
                    }
                }
            }

            return new SsoHttpResult(
                0,
                currentUrl,
                [],
                new InvalidOperationException("xAI OAuth redirected too many times"));
        }

        private HttpRequestMessage BuildRequest(
            string method,
            string url,
            Dictionary<string, List<string>>? form)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), url);

            if (form is not null)
            {
                var content = new ByteArrayContent(
                    Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)));
                content.Headers.TryAddWithoutValidation(
                    "Content-Type",
                    "application/x-www-form-urlencoded");
                request.Content = content;
            }

            request.Headers.TryAddWithoutValidation("Accept", AcceptHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);

            var cookieHeader = _jar.GetCookieHeader(request.RequestUri!);
            if (cookieHeader.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            return request;
        }

        private void StoreCookies(Uri requestUri, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            {
                return;
            }

            foreach (var value in values)
            {
                try
                {
                    _jar.SetCookies(requestUri, value);
                }
                catch (CookieException)
                {
                    // Go's jar silently drops cookies it cannot store.
                }
            }
        }

        private static byte[] ReadLimited(Stream stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            var remaining = MaxResponseBody + 1;
            while (remaining > 0)
            {
                var read = stream.Read(chunk, 0, Math.Min(chunk.Length, remaining));
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
                remaining -= read;
            }

            return buffer.ToArray();
        }
    }

    /// <summary>Go's <c>(int, string, []byte, error)</c> quadruple from <c>do</c>.</summary>
    private readonly record struct SsoHttpResult(
        int Status,
        string FinalUrl,
        byte[] Body,
        Exception? Error);

    private static void SeedSsoCookies(CookieContainer jar, string token)
    {
        foreach (var endpoint in new[] { SubscriptionConstants.SsoAccountsUrl, "https://auth.x.ai/" })
        {
            var uri = new Uri(endpoint);
            jar.Add(uri, new Cookie("sso", token, "/", uri.Host) { Secure = true, HttpOnly = true });
            jar.Add(uri, new Cookie("sso-rw", token, "/", uri.Host) { Secure = true, HttpOnly = true });
        }
    }

    /// <summary>Port of <c>trustedXAIURL</c> (sso_device.go:252-259).</summary>
    private static bool TrustedXaiUrl(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        // Go rejects any URL whose parser recorded userinfo, including the
        // empty form "https://@x.ai"; .NET reports "" for that form, so the
        // authority delimiter is checked on the raw text too.
        if (parsed.Scheme != Uri.UriSchemeHttps || parsed.UserInfo.Length > 0 || HasAuthorityDelimiter(raw))
        {
            return false;
        }

        var host = parsed.Host.ToLowerInvariant();
        return host == "x.ai" || host.EndsWith(".x.ai", StringComparison.Ordinal);
    }

    private static bool HasAuthorityDelimiter(string raw)
    {
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return false;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = authorityStart;
        while (authorityEnd < raw.Length && raw[authorityEnd] is not ('/' or '?' or '#'))
        {
            authorityEnd++;
        }

        return raw.IndexOf('@', authorityStart, authorityEnd - authorityStart) >= 0;
    }

    private static Dictionary<string, List<string>> Form(
        params (string Key, string Value)[] entries)
    {
        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            form[key] = [value];
        }

        return form;
    }

    private static string StatusCodeMessage(string prefix, int status)
        => prefix + ": HTTP " + status.ToString(CultureInfo.InvariantCulture);

    /// <summary>Port of <c>sleepContext</c> (sso_device.go:261-270).</summary>
    private static OperationCanceledException? DefaultSleep(
        CancellationToken cancellationToken,
        TimeSpan duration)
    {
        try
        {
            Task.Delay(duration, cancellationToken).GetAwaiter().GetResult();
            return null;
        }
        catch (OperationCanceledException ex)
        {
            return ex;
        }
    }

    private static Exception Rethrow(Exception error)
    {
        ExceptionDispatchInfo.Capture(error).Throw();
        return error;
    }
}
