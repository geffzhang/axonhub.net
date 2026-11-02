using System.Net;
using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Vertex;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Vertex AI executor
/// (llm/vertex/executor.go): credential validation, request rewriting and the
/// Google OAuth bearer token the transport attaches.
/// </summary>
public sealed class VertexExecutorTests
{
    // ------------------------------------------------------------------
    // Construction
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_validates_region_project_and_credentials()
    {
        var region = Assert.Throws<InvalidOperationException>(
            () => new VertexExecutor(string.Empty, "proj", AuthorizedUserCreds()));
        Assert.Equal("region must be provided", region.Message);

        var project = Assert.Throws<InvalidOperationException>(
            () => new VertexExecutor("us-central1", string.Empty, AuthorizedUserCreds()));
        Assert.Equal("projectID must be provided", project.Message);

        var credentials = Assert.Throws<InvalidOperationException>(
            () => new VertexExecutor("us-central1", "proj", null));
        Assert.Equal("credentials must be provided", credentials.Message);
    }

    [Fact]
    public void FromJson_wraps_credential_parse_errors()
    {
        var empty = Assert.Throws<InvalidOperationException>(
            () => VertexExecutor.FromJson("us-central1", "proj", string.Empty));
        Assert.Equal(
            "failed to parse credentials: unexpected end of JSON input",
            empty.Message);

        var unknown = Assert.Throws<InvalidOperationException>(
            () => VertexExecutor.FromJson("us-central1", "proj", """{"type":"mystery"}"""));
        Assert.Equal(
            "failed to parse credentials: unknown credential type: \"mystery\"",
            unknown.Message);
    }

    // ------------------------------------------------------------------
    // Request rewriting
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequest_rewrites_messages_to_rawPredict()
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Anthropic-Version"] = ["vertex-2023-10-16"],
        };
        var observer = new Action<IReadOnlyDictionary<string, List<string>>>(_ => { });
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "/v1/messages",
            Headers = headers,
            Body = """{"model":"claude-3-sonnet","stream":true,"max_tokens":16}"""u8.ToArray(),
            Auth = new AuthConfig { Type = AuthTypes.ApiKey, ApiKey = "removed" },
            RequestId = "req-1",
            ContentType = "application/json",
            OnResponseHeaders = observer,
        };

        var transformed = Executor().TransformRequest(request);

        Assert.Equal("POST", transformed.Method);
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/proj/locations/us-central1/publishers/anthropic/models/claude-3-sonnet:streamRawPredict",
            transformed.Url);
        Assert.Same(headers, transformed.Headers);
        Assert.Equal("req-1", transformed.RequestId);
        Assert.Same(observer, transformed.OnResponseHeaders);
        Assert.Null(transformed.Auth);

        // The reference copies only these fields; the port's richer members do
        // not travel to the transport request.
        Assert.Equal(string.Empty, transformed.ContentType);

        var body = Parse(transformed.Body);
        Assert.False(body.TryGetProperty("model", out _));
        Assert.Equal("vertex-2023-10-16", body.GetProperty("anthropic_version").GetString());
    }

    [Fact]
    public void TransformRequest_preserves_an_existing_anthropic_version()
    {
        var transformed = Executor().TransformRequest(Messages(
            """{"model":"m","anthropic_version":"custom-version"}"""));

        var body = Parse(transformed.Body);
        Assert.Equal("custom-version", body.GetProperty("anthropic_version").GetString());
    }

    [Fact]
    public void TransformRequest_deletes_model_with_sjson_byte_semantics()
    {
        var transformed = Executor().TransformRequest(Messages(
            """{"model": "m" , "stream":false, "max_tokens":1}"""));

        // sjson deletes the first property together with the comma that follows
        // it, and appends the missing anthropic_version before the closing brace.
        Assert.Equal(
            """{ "stream":false, "max_tokens":1,"anthropic_version":"vertex-2023-10-16"}""",
            Encoding.UTF8.GetString(transformed.Body!));
    }

    [Fact]
    public void TransformRequest_requires_a_model_for_messages()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Executor().TransformRequest(Messages("""{"stream":true}""")));

        Assert.Equal("model field is required", error.Message);
    }

    [Fact]
    public void TransformRequest_rewrites_count_tokens()
    {
        var transformed = Executor().TransformRequest(new HttpRequestData
        {
            Method = "POST",
            Url = "/v1/messages/count_tokens",
            Body = """{"model":"m"}"""u8.ToArray(),
        });

        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com/v1/projects/proj/locations/us-central1/publishers/anthropic/models/count-tokens:rawPredict",
            transformed.Url);

        // Only the /v1/messages branch strips model; count_tokens keeps it.
        Assert.Equal("m", Parse(transformed.Body).GetProperty("model").GetString());
    }

    [Fact]
    public void TransformRequest_prepends_the_base_url_for_other_endpoints()
    {
        var transformed = Executor().TransformRequest(new HttpRequestData
        {
            Method = "GET",
            Url = "/v1/models",
            Body = "{}"u8.ToArray(),
        });

        // The reference base URL ends with a slash and is concatenated with the
        // request path, so a path that already starts with "/" yields "//".
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com//v1/models",
            transformed.Url);
    }

    [Fact]
    public void TransformRequest_does_not_rewrite_when_the_body_is_null()
    {
        var transformed = Executor().TransformRequest(new HttpRequestData
        {
            Method = "POST",
            Url = "/v1/messages",
        });

        Assert.Equal("/v1/messages", transformed.Url);
        Assert.Null(transformed.Body);
    }

    [Fact]
    public void Do_uses_the_global_endpoint_and_attaches_the_bearer_token()
    {
        var apiRequests = new List<(string Url, string Authorization, string Body)>();
        var apiHandler = new ScriptedHandler(request =>
        {
            apiRequests.Add((
                request.RequestUri!.ToString(),
                Header(request, "Authorization"),
                BodyText(request)));
            return Json(HttpStatusCode.OK, "{}");
        });
        var tokenRequests = 0;
        var tokenHandler = new ScriptedHandler(request =>
        {
            tokenRequests++;
            return Json(
                HttpStatusCode.OK,
                """{"access_token":"token-1","token_type":"Bearer","expires_in":3600}""");
        });

        var executor = VertexExecutor.FromJson(
            "global",
            "proj",
            CredentialsJson,
            new VertexExecutorOptions
            {
                Transport = apiHandler,
                TokenTransport = tokenHandler,
            });

        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "/v1/messages/count_tokens",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                // BuildHttpRequest strips this; the GCP handler replaces it.
                ["Authorization"] = ["Bearer stale"],
            },
            Body = """{"model":"m"}"""u8.ToArray(),
        };

        executor.Do(CancellationToken.None, request);
        executor.Do(CancellationToken.None, request);

        // The cached token is reused for the second call.
        Assert.Equal(1, tokenRequests);
        Assert.Collection(
            apiRequests,
            first => AssertApiRequest(first, "Bearer token-1"),
            second => AssertApiRequest(second, "Bearer token-1"));
    }

    [Fact]
    public void Do_reports_token_endpoint_errors_verbatim()
    {
        var apiHandler = new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var tokenHandler = new ScriptedHandler(_ => Json(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant","error_description":"Bad Request"}"""));

        var executor = VertexExecutor.FromJson(
            "us-central1",
            "proj",
            CredentialsJson,
            new VertexExecutorOptions
            {
                Transport = apiHandler,
                TokenTransport = tokenHandler,
            });

        var error = Assert.Throws<InvalidOperationException>(
            () => executor.Do(CancellationToken.None, new HttpRequestData
            {
                Method = "POST",
                Url = "/v1/messages/count_tokens",
                Body = "{}"u8.ToArray(),
            }));

        Assert.Equal("oauth2: \"invalid_grant\" \"Bad Request\"", error.Message);
    }

    private static void AssertApiRequest(
        (string Url, string Authorization, string Body) request,
        string expectedAuthorization)
    {
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/proj/locations/global/publishers/anthropic/models/count-tokens:rawPredict",
            request.Url);
        Assert.Equal(expectedAuthorization, request.Authorization);
        Assert.Equal(
            "m",
            Parse(Encoding.UTF8.GetBytes(request.Body)).GetProperty("model").GetString());
    }

    private const string CredentialsJson =
        """{"type":"authorized_user","client_id":"cid","client_secret":"secret","refresh_token":"rt"}""";

    private static VertexExecutor Executor() => new(
        "us-central1",
        "proj",
        AuthorizedUserCreds());

    private static GcpCredentials AuthorizedUserCreds() => GcpCredentials.FromJson(CredentialsJson);

    private static HttpRequestData Messages(string json) => new()
    {
        Method = "POST",
        Url = "/v1/messages",
        Body = Encoding.UTF8.GetBytes(json),
    };

    private static System.Text.Json.JsonElement Parse(byte[]? body)
    {
        Assert.NotNull(body);
        using var document = System.Text.Json.JsonDocument.Parse(body!);
        return document.RootElement.Clone();
    }

    private static string Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;

    private static string BodyText(HttpRequestMessage request)
        => request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
