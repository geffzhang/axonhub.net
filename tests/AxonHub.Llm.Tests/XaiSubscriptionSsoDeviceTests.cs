using System.Net;
using System.Text;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for <c>ConvertSSOToBuild</c>
/// (llm/transformer/xai/subscription/sso_device_test.go): the device
/// authorization flow validates the SSO cookie, opens the verification page,
/// approves the code and polls the token endpoint over manual redirects with
/// the browser identity headers.
/// </summary>
public sealed class XaiSubscriptionSsoDeviceTests
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string VerifyUrl =
        "https://auth.x.ai/oauth2/device/verify?user_code=user";

    [Fact]
    public void ConvertSsoToBuild_completes_device_flow()
    {
        var slept = TimeSpan.Zero;
        var handler = new ScriptedHandler(request =>
        {
            // .NET parses these known headers into product/quality values on
            // read; the joined forms are the wire values.
            Assert.Equal(BrowserUserAgent, string.Join(" ", request.Headers.UserAgent));
            Assert.Collection(
                request.Headers.AcceptLanguage,
                value => Assert.Equal("en-US", value.ToString()),
                value => Assert.Equal("en; q=0.9", value.ToString()));
            switch (request.RequestUri!.ToString())
            {
                case SubscriptionConstants.SsoAccountsUrl:
                    Assert.Contains("sso=synthetic-sso", Header(request, "Cookie"));
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.SsoDeviceUrl:
                    return Json(
                        HttpStatusCode.OK,
                        """{"device_code":"device","user_code":"user","verification_uri_complete":"https://auth.x.ai/oauth2/device/verify?user_code=user","interval":3600,"expires_in":1}""");
                case VerifyUrl:
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.SsoVerifyUrl:
                    return Redirect("https://auth.x.ai/oauth2/device/consent");
                case "https://auth.x.ai/oauth2/device/consent":
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.SsoApproveUrl:
                    return Redirect("https://auth.x.ai/oauth2/device/done");
                case "https://auth.x.ai/oauth2/device/done":
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.TokenUrl:
                    return Json(
                        HttpStatusCode.OK,
                        """{"access_token":"access","refresh_token":"refresh","expires_in":3600}""");
                default:
                    throw new InvalidOperationException(
                        "unexpected request: " + request.Method + " " + request.RequestUri);
            }
        });

        var credentials = SubscriptionSsoDevice.ConvertSsoToBuild(
            CancellationToken.None,
            "sso=synthetic-sso",
            new SsoDeviceOptions
            {
                Transport = handler,
                Sleep = (_, duration) =>
                {
                    slept = duration;
                    return null;
                },
            });

        Assert.Equal("access", credentials.AccessToken);
        Assert.Equal("refresh", credentials.RefreshToken);
        Assert.Equal(SubscriptionConstants.ClientId, credentials.ClientId);
        Assert.Equal("Bearer", credentials.TokenType);
        Assert.True(slept > TimeSpan.Zero);
        Assert.True(slept <= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ConvertSsoToBuild_reports_verification_HTTP_status()
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.ToString() switch
            {
                SubscriptionConstants.SsoAccountsUrl => Json(HttpStatusCode.OK, "{}"),
                SubscriptionConstants.SsoDeviceUrl => Json(
                    HttpStatusCode.OK,
                    """{"device_code":"device","user_code":"user","verification_uri_complete":"https://auth.x.ai/oauth2/device/verify?user_code=user","interval":1,"expires_in":60}"""),
                VerifyUrl => Json(HttpStatusCode.InternalServerError, "{}"),
                _ => throw new InvalidOperationException(
                    "unexpected request: " + request.Method + " " + request.RequestUri),
            });

        var error = Assert.Throws<InvalidOperationException>(
            () => SubscriptionSsoDevice.ConvertSsoToBuild(
                CancellationToken.None,
                "synthetic-sso",
                new SsoDeviceOptions { Transport = handler }));

        Assert.Equal("open xAI device verification: HTTP 500", error.Message);
    }

    // ------------------------------------------------------------------
    // Port-added: polling and validation branches with no reference test.
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertSsoToBuild_requires_a_token()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => SubscriptionSsoDevice.ConvertSsoToBuild(
                CancellationToken.None,
                "   ",
                new SsoDeviceOptions()));

        Assert.Equal("xAI SSO token is required", error.Message);
    }

    [Fact]
    public void ConvertSsoToBuild_rejects_untrusted_verification_url()
    {
        var handler = new ScriptedHandler(request =>
            request.RequestUri!.ToString() switch
            {
                SubscriptionConstants.SsoAccountsUrl => Json(HttpStatusCode.OK, "{}"),
                SubscriptionConstants.SsoDeviceUrl => Json(
                    HttpStatusCode.OK,
                    """{"device_code":"device","user_code":"user","verification_uri_complete":"https://evil.example/verify"}"""),
                _ => throw new InvalidOperationException(
                    "unexpected request: " + request.Method + " " + request.RequestUri),
            });

        var error = Assert.Throws<InvalidOperationException>(
            () => SubscriptionSsoDevice.ConvertSsoToBuild(
                CancellationToken.None,
                "synthetic-sso",
                new SsoDeviceOptions { Transport = handler }));

        Assert.Equal("xAI device flow response is incomplete", error.Message);
    }

    [Fact]
    public void ConvertSsoToBuild_continues_on_authorization_pending()
    {
        var polls = 0;
        var handler = HappyFlow(request =>
        {
            if (request.RequestUri!.ToString() == SubscriptionConstants.TokenUrl)
            {
                polls++;
                return polls == 1
                    ? Json(HttpStatusCode.BadRequest, """{"error":"authorization_pending"}""")
                    : Json(HttpStatusCode.OK, """{"access_token":"access"}""");
            }

            return null;
        });

        var credentials = SubscriptionSsoDevice.ConvertSsoToBuild(
            CancellationToken.None,
            "synthetic-sso",
            new SsoDeviceOptions
            {
                Transport = handler,
                Sleep = (_, _) => null,
            });

        Assert.Equal(2, polls);
        Assert.Equal("access", credentials.AccessToken);
        Assert.Equal(string.Empty, credentials.RefreshToken);
        Assert.Equal("Bearer", credentials.TokenType);
    }

    [Fact]
    public void ConvertSsoToBuild_slow_down_extends_the_interval()
    {
        var slept = new List<TimeSpan>();
        var polls = 0;
        var handler = HappyFlow(request =>
        {
            if (request.RequestUri!.ToString() == SubscriptionConstants.TokenUrl)
            {
                polls++;
                return polls == 1
                    ? Json(HttpStatusCode.BadRequest, """{"error":"slow_down"}""")
                    : Json(HttpStatusCode.OK, """{"access_token":"access"}""");
            }

            return null;
        });

        SubscriptionSsoDevice.ConvertSsoToBuild(
            CancellationToken.None,
            "synthetic-sso",
            new SsoDeviceOptions
            {
                Transport = handler,
                Sleep = (_, duration) =>
                {
                    slept.Add(duration);
                    return null;
                },
            });

        Assert.Equal(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(6) },
            slept);
    }

    /// <summary>
    /// The pre-token half of the device flow with the reference test's fixture:
    /// accounts 200, device code (interval 1s, 60s deadline), verification 200,
    /// consent and done redirects. <paramref name="intercept"/> handles the
    /// token request and returns null to fall through to a successful token.
    /// </summary>
    private static ScriptedHandler HappyFlow(
        Func<HttpRequestMessage, HttpResponseMessage?> intercept)
        => new(request =>
        {
            switch (request.RequestUri!.ToString())
            {
                case SubscriptionConstants.SsoAccountsUrl:
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.SsoDeviceUrl:
                    return Json(
                        HttpStatusCode.OK,
                        """{"device_code":"device","user_code":"user","verification_uri_complete":"https://auth.x.ai/oauth2/device/verify?user_code=user","interval":1,"expires_in":60}""");
                case VerifyUrl:
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.SsoVerifyUrl:
                    return Redirect("https://auth.x.ai/oauth2/device/consent");
                case "https://auth.x.ai/oauth2/device/consent":
                    return Json(HttpStatusCode.OK, "{}");
                case SubscriptionConstants.SsoApproveUrl:
                    return Redirect("https://auth.x.ai/oauth2/device/done");
                case "https://auth.x.ai/oauth2/device/done":
                    return Json(HttpStatusCode.OK, "{}");
            }

            return intercept(request)
                ?? Json(HttpStatusCode.OK, """{"access_token":"access"}""");
        });

    private static string Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new ByteArrayContent([]),
        };
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
