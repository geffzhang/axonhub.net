using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Vertex;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Google credential port
/// (golang.org/x/oauth2/google + oauth2/jwt: credentials.go, jwt/jwt.go,
/// jws/jws.go, oauth2.go). Covers the two flows the AxonHub UI stores and, for
/// the unsupported ones, the C128 gap.
/// </summary>
public sealed class GcpTokenSourceTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly string[] ImpersonationDelegates =
        ["first@example.test", "second@example.test"];

    // ------------------------------------------------------------------
    // Service account: signed JWT exchanged for an access token
    // ------------------------------------------------------------------

    [Fact]
    public void ServiceAccount_exchanges_a_signed_assertion()
    {
        using var rsa = RSA.Create(2048);
        var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        const string email = "sa@proj.iam.gserviceaccount.com";
        const string tokenUrl = "https://oauth.example.test/token";

        var captured = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            captured.Add(BodyText(request));
            return Text(
                HttpStatusCode.OK,
                """{"access_token":"at-1","token_type":"Bearer","expires_in":3600}""");
        });

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = email,
                ["private_key_id"] = "kid-1",
                ["private_key"] = privateKeyPem,
                ["token_uri"] = tokenUrl,
            },
            handler);

        var token = credentials.TokenSource.GetToken();

        Assert.Equal("at-1", token.AccessToken);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(FixedNow.AddSeconds(3600), token.Expiry);

        var form = ParseForm(Assert.Single(captured));
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);

        var parts = form["assertion"].Split('.');
        Assert.Equal(3, parts.Length);

        // jws.Encode header with the key id from the credentials file.
        Assert.Equal(
            """{"alg":"RS256","typ":"JWT","kid":"kid-1"}""",
            Encoding.UTF8.GetString(Base64UrlDecode(parts[0])));

        var issuedAt = FixedNow.AddSeconds(-10).ToUnixTimeSeconds();
        var expiresAt = issuedAt + 3600;

        // ClaimSet field order is Go's struct order; aud defaults to TokenURL.
        Assert.Equal(
            $$"""{"iss":"{{email}}","aud":"{{tokenUrl}}","exp":{{expiresAt}},"iat":{{issuedAt}}}""",
            Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));

        // RS256 over the signing input with the generated key.
        using var publicKey = RSA.Create();
        publicKey.ImportSubjectPublicKeyInfo(rsa.ExportSubjectPublicKeyInfo(), out _);
        Assert.True(publicKey.VerifyData(
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
            Base64UrlDecode(parts[2]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void ServiceAccount_uses_an_explicit_audience()
    {
        using var rsa = RSA.Create(2048);

        var captured = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            captured.Add(BodyText(request));
            return Text(HttpStatusCode.OK, """{"access_token":"at"}""");
        });

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = "sa@example.test",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
                ["audience"] = "https://audience.example.test",
            },
            handler);

        credentials.TokenSource.GetToken();

        var form = ParseForm(Assert.Single(captured));
        var payload = JsonDocument.Parse(Base64UrlDecode(form["assertion"].Split('.')[1]));
        Assert.Equal(
            "https://audience.example.test",
            payload.RootElement.GetProperty("aud").GetString());

        // The default token URL is still the request target.
        Assert.Equal(
            "https://oauth2.googleapis.com/token",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void ServiceAccount_does_not_require_an_access_token()
    {
        using var rsa = RSA.Create(2048);

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = "sa@example.test",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            },
            new ScriptedHandler(_ => Text(HttpStatusCode.OK, """{"token_type":"Bearer"}""")));

        // jwtSource decodes the response without checking access_token.
        var token = credentials.TokenSource.GetToken();
        Assert.Equal(string.Empty, token.AccessToken);
    }

    [Fact]
    public void ServiceAccount_reports_the_http_status_line_with_the_body()
    {
        using var rsa = RSA.Create(2048);

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = "sa@example.test",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            },
            new ScriptedHandler(_ => Text(HttpStatusCode.BadRequest, "nope")));

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.TokenSource.GetToken());

        Assert.Equal(
            "oauth2: cannot fetch token: 400 Bad Request\nResponse: nope",
            error.Message);
    }

    [Fact]
    public void ServiceAccount_rejects_an_unparsable_private_key()
    {
        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = "sa@example.test",
                ["private_key"] = "not-a-key",
            },
            new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}")));

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.TokenSource.GetToken());

        Assert.StartsWith(
            "private key should be a PEM or plain PKCS1 or PKCS8; parse error: ",
            error.Message);
    }

    [Fact]
    public void ServiceAccount_caches_the_access_token()
    {
        using var rsa = RSA.Create(2048);
        var calls = 0;

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["client_email"] = "sa@example.test",
                ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            },
            new ScriptedHandler(_ =>
            {
                calls++;
                return Text(HttpStatusCode.OK, """{"access_token":"at","expires_in":3600}""");
            }));

        credentials.TokenSource.GetToken();
        credentials.TokenSource.GetToken();

        Assert.Equal(1, calls);
    }

    // ------------------------------------------------------------------
    // gcloud user credentials: refresh-token grant
    // ------------------------------------------------------------------

    [Fact]
    public void AuthorizedUser_refreshes_with_form_encoded_client_credentials()
    {
        var forms = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            forms.Add(BodyText(request));
            return Text(
                HttpStatusCode.OK,
                "access_token=at-1&refresh_token=rt-2&token_type=bearer&expires_in=1800",
                "text/plain; charset=utf-8");
        });

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
                ["refresh_token"] = "rt-1",
            },
            handler);

        var first = credentials.TokenSource.GetToken();

        Assert.Equal("at-1", first.AccessToken);
        Assert.Equal("bearer", first.TokenType);
        Assert.Equal("Bearer", first.Type);
        Assert.Equal(FixedNow.AddSeconds(1800), first.Expiry);
        Assert.Equal(
            "client_id=cid&client_secret=secret&grant_type=refresh_token&refresh_token=rt-1",
            Assert.Single(forms));
    }

    [Fact]
    public void AuthorizedUser_refreshes_when_the_cached_token_expires_and_keeps_the_new_refresh_token()
    {
        var forms = new List<string>();
        var calls = 0;
        var now = FixedNow;
        var handler = new ScriptedHandler(request =>
        {
            forms.Add(BodyText(request));
            calls++;
            return Text(
                HttpStatusCode.OK,
                calls == 1
                    ? """{"access_token":"at-1","refresh_token":"rt-2","expires_in":1800}"""
                    : """{"access_token":"at-2","expires_in":1800}""");
        });

        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
                ["refresh_token"] = "rt-1",
            },
            handler,
            () => now);

        credentials.TokenSource.GetToken();

        // Still inside the 10s expiry delta: served from the cache.
        now = now.AddSeconds(1789);
        Assert.Equal("at-1", credentials.TokenSource.GetToken().AccessToken);
        Assert.Equal(1, calls);

        now = now.AddSeconds(2);
        Assert.Equal("at-2", credentials.TokenSource.GetToken().AccessToken);
        Assert.Equal(2, calls);

        // The response's refresh_token replaces the stored one; a response
        // without refresh_token keeps it.
        Assert.Contains("refresh_token=rt-1", forms[0], StringComparison.Ordinal);
        Assert.Contains("refresh_token=rt-2", forms[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizedUser_without_a_refresh_token_reports_the_reference_error()
    {
        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
            },
            new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}")));

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.TokenSource.GetToken());

        Assert.Equal("oauth2: token expired and refresh token is not set", error.Message);
    }

    [Fact]
    public void AuthorizedUser_reports_a_json_error_verbatim()
    {
        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
                ["refresh_token"] = "rt",
            },
            new ScriptedHandler(_ => Text(
                HttpStatusCode.BadRequest,
                """{"error":"invalid_grant","error_description":"Bad Request","error_uri":"https://docs.example" }""")));

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.TokenSource.GetToken());

        Assert.Equal(
            "oauth2: \"invalid_grant\" \"Bad Request\" \"https://docs.example\"",
            error.Message);
    }

    [Fact]
    public void AuthorizedUser_requires_an_access_token()
    {
        var credentials = Credentials(
            new Dictionary<string, string>
            {
                ["type"] = "authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
                ["refresh_token"] = "rt",
            },
            new ScriptedHandler(_ => Text(HttpStatusCode.OK, """{"token_type":"Bearer"}""")));

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.TokenSource.GetToken());

        Assert.Equal("oauth2: server response missing access_token", error.Message);
    }

    // ------------------------------------------------------------------
    // Workforce-pool user credentials and service-account impersonation
    // ------------------------------------------------------------------

    [Fact]
    public void ExternalAccountAuthorizedUser_refreshes_with_basic_auth()
    {
        var bodies = new List<string>();
        var authorizations = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            bodies.Add(BodyText(request));
            authorizations.Add(request.Headers.GetValues("Authorization").Single());
            return Text(
                HttpStatusCode.OK,
                """{"access_token":"at-1","token_type":"Bearer","expires_in":3600,"refresh_token":"rt-2"}""");
        });

        var credentials = CredentialsObject(
            new Dictionary<string, object>
            {
                ["type"] = "external_account_authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
                ["refresh_token"] = "rt-1",
                ["token_url"] = "https://sts.example.test/token",
            },
            handler);

        var token = credentials.TokenSource.GetToken();

        Assert.Equal("at-1", token.AccessToken);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(FixedNow.AddSeconds(3600), token.Expiry);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://sts.example.test/token", request.RequestUri!.ToString());
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("cid:secret")),
            Assert.Single(authorizations));
        var form = ParseForm(Assert.Single(bodies));
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt-1", form["refresh_token"]);
    }

    [Fact]
    public void ExternalAccountAuthorizedUser_validates_refresh_fields()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account_authorized_user",
                    ["client_id"] = "cid",
                    ["refresh_token"] = "rt",
                    ["token_url"] = "https://sts.example.test/token",
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));

        Assert.Equal(
            "oauth2/google: Token should be created with fields to make it valid "
            + "(`token` and `expiry`), or fields to allow it to refresh "
            + "(`refresh_token`, `token_url`, `client_id`, `client_secret`).",
            error.Message);
    }

    [Fact]
    public void ExternalAccountAuthorizedUser_rejects_a_negative_expiry()
    {
        var credentials = CredentialsObject(
            new Dictionary<string, object>
            {
                ["type"] = "external_account_authorized_user",
                ["client_id"] = "cid",
                ["client_secret"] = "secret",
                ["refresh_token"] = "rt-1",
                ["token_url"] = "https://sts.example.test/token",
            },
            new ScriptedHandler(_ => Text(
                HttpStatusCode.OK,
                """{"access_token":"at","expires_in":-1}""")));

        var error = Assert.Throws<InvalidOperationException>(
            () => credentials.TokenSource.GetToken());

        Assert.Equal(
            "oauth2/google: got invalid expiry from security token service",
            error.Message);
    }

    [Fact]
    public void ImpersonatedServiceAccount_exchanges_the_source_token()
    {
        var calls = 0;
        var impersonationBodies = new List<string>();
        var impersonationAuthorizations = new List<string>();
        var handler = new ScriptedHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                return Text(
                    HttpStatusCode.OK,
                    """{"access_token":"source-at","token_type":"Bearer","expires_in":3600}""");
            }

            impersonationBodies.Add(BodyText(request));
            impersonationAuthorizations.Add(
                request.Headers.GetValues("Authorization").Single());
            return Text(
                HttpStatusCode.OK,
                """{"accessToken":"imp-at","expireTime":"2026-01-02T04:04:05Z"}""");
        });

        var credentials = CredentialsObject(
            new Dictionary<string, object>
            {
                ["type"] = "impersonated_service_account",
                ["service_account_impersonation_url"] = "https://iam.example.test/token",
                ["service_account_impersonation"] = new Dictionary<string, object>
                {
                    ["token_lifetime_seconds"] = 1200,
                },
                ["delegates"] = ImpersonationDelegates,
                ["source_credentials"] = new Dictionary<string, object>
                {
                    ["type"] = "authorized_user",
                    ["client_id"] = "cid",
                    ["client_secret"] = "secret",
                    ["refresh_token"] = "rt-1",
                },
            },
            handler);

        var token = credentials.TokenSource.GetToken();

        Assert.Equal("imp-at", token.AccessToken);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 4, 4, 5, TimeSpan.Zero),
            token.Expiry);

        Assert.Equal(2, handler.Requests.Count);
        var impersonation = handler.Requests[1];
        Assert.Equal("https://iam.example.test/token", impersonation.RequestUri!.ToString());
        Assert.Equal("Bearer source-at", Assert.Single(impersonationAuthorizations));

        using var body = JsonDocument.Parse(Assert.Single(impersonationBodies));
        Assert.Equal("1200s", body.RootElement.GetProperty("lifetime").GetString());
        Assert.Equal(
            ImpersonationDelegates,
            body.RootElement.GetProperty("delegates").EnumerateArray()
                .Select(static item => item.GetString())
                .ToArray());
        Assert.False(body.RootElement.TryGetProperty("scope", out _));
    }

    [Fact]
    public void ImpersonatedServiceAccount_requires_source_credentials_and_url()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "impersonated_service_account",
                    ["service_account_impersonation_url"] = "https://iam.example.test/token",
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));

        Assert.Equal(
            "missing 'source_credentials' field or "
            + "'service_account_impersonation_url' in credentials",
            error.Message);
    }

    // ------------------------------------------------------------------
    // Workload identity federation: file/URL subject tokens + STS exchange
    // ------------------------------------------------------------------

    [Fact]
    public void ExternalAccount_exchanges_a_json_file_subject_token()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """{"subject":"subject-token"}""");
            var forms = new List<string>();
            var handler = new ScriptedHandler(request =>
            {
                forms.Add(BodyText(request));
                return Text(
                    HttpStatusCode.OK,
                    """{"access_token":"at","token_type":"Bearer","expires_in":3600}""");
            });

            var credentials = CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/dev",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["file"] = file,
                        ["format"] = new Dictionary<string, object>
                        {
                            ["type"] = "json",
                            ["subject_token_field_name"] = "subject",
                        },
                    },
                },
                handler);

            var token = credentials.TokenSource.GetToken();

            Assert.Equal("at", token.AccessToken);
            Assert.Equal(FixedNow.AddSeconds(3600), token.Expiry);
            var form = ParseForm(Assert.Single(forms));
            Assert.Equal("urn:ietf:params:oauth:grant-type:token-exchange", form["grant_type"]);
            Assert.Equal("subject-token", form["subject_token"]);
            Assert.Equal(
                "urn:ietf:params:oauth:token-type:access_token",
                form["requested_token_type"]);
            Assert.Equal(
                "https://sts.googleapis.com/v1/token",
                handler.Requests[0].RequestUri!.ToString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExternalAccount_reads_a_url_subject_token_with_headers()
    {
        var calls = 0;
        string? metadata = null;
        var handler = new ScriptedHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                metadata = request.Headers.GetValues("Metadata").Single();
                return Text(HttpStatusCode.OK, "subject-from-url", "text/plain");
            }

            return Text(
                HttpStatusCode.OK,
                """{"access_token":"at-url","token_type":"Bearer","expires_in":60}""");
        });

        var credentials = CredentialsObject(
            new Dictionary<string, object>
            {
                ["type"] = "external_account",
                ["audience"] = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/dev",
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                ["credential_source"] = new Dictionary<string, object>
                {
                    ["url"] = "http://metadata.example.test/token",
                    ["headers"] = new Dictionary<string, string> { ["Metadata"] = "true" },
                    ["format"] = new Dictionary<string, object> { ["type"] = "text" },
                },
            },
            handler);

        var token = credentials.TokenSource.GetToken();

        Assert.Equal("at-url", token.AccessToken);
        Assert.Equal("true", metadata);
    }

    [Fact]
    public void ExternalAccount_validates_the_configuration()
    {
        var withoutAudience = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));
        Assert.Equal(
            "oauth2/google/externalaccount: Audience must be set",
            withoutAudience.Message);

        var withoutType = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));
        Assert.Equal(
            "oauth2/google/externalaccount: Subject token type must be set",
            withoutType.Message);

        var withoutSource = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));
        Assert.Contains("One of CredentialSource", withoutSource.Message, StringComparison.Ordinal);

        var aws = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["environment_id"] = "aws2",
                    },
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));
        Assert.Equal(
            "oauth2/google/externalaccount: aws version '2' is not supported in the current build",
            aws.Message);
    }

    [Fact]
    public void ExternalAccount_aws_environment_credentials_sign_a_caller_identity_request()
    {
        var previous = new Dictionary<string, string?>
        {
            ["AWS_ACCESS_KEY_ID"] = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
            ["AWS_SECRET_ACCESS_KEY"] = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
            ["AWS_SESSION_TOKEN"] = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"),
            ["AWS_REGION"] = Environment.GetEnvironmentVariable("AWS_REGION"),
            ["AWS_DEFAULT_REGION"] = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"),
        };
        try
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", "AKIDEXAMPLE");
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", "secret");
            Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", null);
            Environment.SetEnvironmentVariable("AWS_REGION", "us-east-1");
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", null);

            var forms = new List<string>();
            var handler = new ScriptedHandler(request =>
            {
                forms.Add(BodyText(request));
                return Text(
                    HttpStatusCode.OK,
                    """{"access_token":"aws-at","token_type":"Bearer","expires_in":60}""");
            });
            const string audience =
                "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/aws";
            var credentials = CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = audience,
                    ["subject_token_type"] = "urn:ietf:params:aws:token-type:aws4_request",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["environment_id"] = "aws1",
                    },
                },
                handler);

            var token = credentials.TokenSource.GetToken();

            Assert.Equal("aws-at", token.AccessToken);
            var form = ParseForm(Assert.Single(forms));
            // url.QueryEscape wraps the signed JSON; the form layer decodes it once.
            using var subject = JsonDocument.Parse(
                Uri.UnescapeDataString(form["subject_token"].Replace('+', ' ')));
            var root = subject.RootElement;
            Assert.Equal(
                "https://sts.us-east-1.amazonaws.com?Action=GetCallerIdentity&Version=2011-06-15",
                root.GetProperty("url").GetString());
            Assert.Equal("POST", root.GetProperty("method").GetString());
            var headers = root.GetProperty("headers").EnumerateArray()
                .ToDictionary(
                    static header => header.GetProperty("key").GetString()!,
                    static header => header.GetProperty("value").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal("sts.us-east-1.amazonaws.com", headers["Host"]);
            Assert.Equal("20260102T030405Z", headers["X-Amz-Date"]);
            Assert.Equal(audience, headers["X-Goog-Cloud-Target-Resource"]);
            Assert.StartsWith(
                "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260102/us-east-1/sts/aws4_request",
                headers["Authorization"],
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void ExternalAccount_aws_metadata_reads_imdsv2_region_and_credentials()
    {
        var previous = new Dictionary<string, string?>
        {
            ["AWS_ACCESS_KEY_ID"] = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID"),
            ["AWS_SECRET_ACCESS_KEY"] = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY"),
            ["AWS_SESSION_TOKEN"] = Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"),
            ["AWS_REGION"] = Environment.GetEnvironmentVariable("AWS_REGION"),
            ["AWS_DEFAULT_REGION"] = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION"),
        };
        try
        {
            Environment.SetEnvironmentVariable("AWS_ACCESS_KEY_ID", null);
            Environment.SetEnvironmentVariable("AWS_SECRET_ACCESS_KEY", null);
            Environment.SetEnvironmentVariable("AWS_SESSION_TOKEN", null);
            Environment.SetEnvironmentVariable("AWS_REGION", null);
            Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", null);

            var forms = new List<string>();
            var handler = new ScriptedHandler(request =>
            {
                var uri = request.RequestUri!.ToString();
                if (uri == "http://metadata.test/token")
                {
                    Assert.Equal(HttpMethod.Put, request.Method);
                    Assert.Equal(
                        "300",
                        request.Headers.GetValues("X-aws-ec2-metadata-token-ttl-seconds").Single());
                    return Text(HttpStatusCode.OK, "imds-token");
                }

                if (uri == "http://metadata.test/region")
                {
                    return Text(HttpStatusCode.OK, "us-west-2b");
                }

                if (uri == "http://metadata.test/credentials")
                {
                    return Text(HttpStatusCode.OK, "role-name");
                }

                if (uri == "http://metadata.test/credentials/role-name")
                {
                    return Text(
                        HttpStatusCode.OK,
                        """{"AccessKeyID":"AKIDMETA","SecretAccessKey":"metasecret","Token":"metatoken"}""");
                }

                forms.Add(BodyText(request));
                return Text(
                    HttpStatusCode.OK,
                    """{"access_token":"meta-at","token_type":"Bearer","expires_in":60}""");
            });
            var credentials = CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                    ["subject_token_type"] = "urn:ietf:params:aws:token-type:aws4_request",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["environment_id"] = "aws1",
                        ["url"] = "http://metadata.test/credentials",
                        ["region_url"] = "http://metadata.test/region",
                        ["imdsv2_session_token_url"] = "http://metadata.test/token",
                    },
                },
                handler);

            var token = credentials.TokenSource.GetToken();

            Assert.Equal("meta-at", token.AccessToken);
            var form = ParseForm(Assert.Single(forms));
            using var subject = JsonDocument.Parse(
                Uri.UnescapeDataString(form["subject_token"].Replace('+', ' ')));
            var root = subject.RootElement;
            Assert.Equal(
                "https://sts.us-west-2.amazonaws.com?Action=GetCallerIdentity&Version=2011-06-15",
                root.GetProperty("url").GetString());
            var headers = root.GetProperty("headers").EnumerateArray()
                .ToDictionary(
                    static header => header.GetProperty("key").GetString()!,
                    static header => header.GetProperty("value").GetString()!,
                    StringComparer.Ordinal);
            Assert.Equal("metatoken", headers["X-Amz-Security-Token"]);
            Assert.StartsWith(
                "AWS4-HMAC-SHA256 Credential=AKIDMETA/20260102/us-west-2/sts/aws4_request",
                headers["Authorization"],
                StringComparison.Ordinal);

            // The IMDSv2 token header travels on every metadata request.
            foreach (var request in handler.Requests
                         .Where(static request => request.RequestUri!.Host == "metadata.test")
                         .Skip(1))
            {
                Assert.Equal(
                    "imds-token",
                    request.Headers.GetValues("X-aws-ec2-metadata-token").Single());
            }
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    [Fact]
    public void ExternalAccount_executable_reads_a_cached_output_file()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(
                file,
                """{"version":1,"success":true,"token_type":"urn:ietf:params:oauth:token-type:jwt","id_token":"id-token","expiration_time":"""
                + FixedNow.AddMinutes(5).ToUnixTimeSeconds()
                + "}");
            var forms = new List<string>();
            var handler = new ScriptedHandler(request =>
            {
                forms.Add(BodyText(request));
                return Text(
                    HttpStatusCode.OK,
                    """{"access_token":"exec-at","token_type":"Bearer","expires_in":60}""");
            });
            var credentials = CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["executable"] = new Dictionary<string, object>
                        {
                            ["command"] = "ignored",
                            ["timeout_millis"] = 5000,
                            ["output_file"] = file,
                        },
                    },
                },
                handler);

            var token = credentials.TokenSource.GetToken();

            Assert.Equal("exec-at", token.AccessToken);
            var form = ParseForm(Assert.Single(forms));
            Assert.Equal("id-token", form["subject_token"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void ExternalAccount_executable_validates_configuration_and_responses()
    {
        var missingCommand = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["executable"] = new Dictionary<string, object>(),
                    },
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));
        Assert.Contains("missing `command` field", missingCommand.Message, StringComparison.Ordinal);

        var badTimeout = Assert.Throws<InvalidOperationException>(() =>
            CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "aud",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["executable"] = new Dictionary<string, object>
                        {
                            ["command"] = "ignored",
                            ["timeout_millis"] = 4999,
                        },
                    },
                },
                new ScriptedHandler(_ => Text(HttpStatusCode.OK, "{}"))));
        Assert.Contains("`timeout_millis`", badTimeout.Message, StringComparison.Ordinal);

        var now = FixedNow.ToUnixTimeSeconds();
        var missingVersion = Assert.Throws<InvalidOperationException>(() =>
            ExternalAccountExecutableGcpTokenSource.ParseSubjectToken(
                Encoding.UTF8.GetBytes("""{"success":true}"""),
                "response",
                string.Empty,
                now));
        Assert.Contains("missing `version` field", missingVersion.Message, StringComparison.Ordinal);

        var malformedFailure = Assert.Throws<GcpNonCacheableException>(() =>
            ExternalAccountExecutableGcpTokenSource.ParseSubjectToken(
                Encoding.UTF8.GetBytes(
                    """{"version":1,"success":false}"""),
                "response",
                string.Empty,
                now));
        Assert.Contains("must include `error` and `message`", malformedFailure.Message, StringComparison.Ordinal);

        var expired = Assert.Throws<GcpNonCacheableException>(() =>
            ExternalAccountExecutableGcpTokenSource.ParseSubjectToken(
                Encoding.UTF8.GetBytes(
                    """{"version":1,"success":true,"token_type":"urn:ietf:params:oauth:token-type:jwt","id_token":"t","expiration_time":"""
                    + (now - 1)
                    + "}"),
                "response",
                string.Empty,
                now));
        Assert.Contains("executable is expired", expired.Message, StringComparison.Ordinal);

        var token = ExternalAccountExecutableGcpTokenSource.ParseSubjectToken(
            Encoding.UTF8.GetBytes(
                """{"version":1,"success":true,"token_type":"urn:ietf:params:oauth:token-type:saml2","saml_response":"<assertion/>","expiration_time":"""
                + (now + 60)
                + "}"),
            "response",
            string.Empty,
            now);
        Assert.Equal("<assertion/>", token);
    }

    [Fact]
    public void ExternalAccount_impersonation_uses_the_sts_source()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "subject-token");
            var calls = 0;
            var stsForms = new List<string>();
            var handler = new ScriptedHandler(request =>
            {
                calls++;
                if (calls == 1)
                {
                    stsForms.Add(BodyText(request));
                    return Text(
                        HttpStatusCode.OK,
                        """{"access_token":"sts-at","token_type":"Bearer","expires_in":3600}""");
                }

                return Text(
                    HttpStatusCode.OK,
                    """{"accessToken":"imp-at","expireTime":"2026-01-02T04:04:05Z"}""");
            });

            var credentials = CredentialsObject(
                new Dictionary<string, object>
                {
                    ["type"] = "external_account",
                    ["audience"] = "//iam.googleapis.com/projects/1/locations/global/workloadIdentityPools/p/providers/dev",
                    ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt",
                    ["service_account_impersonation_url"] =
                        "https://iam.example.test/token",
                    ["credential_source"] = new Dictionary<string, object>
                    {
                        ["file"] = file,
                        ["format"] = new Dictionary<string, object> { ["type"] = "text" },
                    },
                },
                handler);

            var token = credentials.TokenSource.GetToken();

            Assert.Equal("imp-at", token.AccessToken);
            var stsForm = ParseForm(Assert.Single(stsForms));
            Assert.Equal(
                "https://www.googleapis.com/auth/cloud-platform",
                stsForm["scope"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ------------------------------------------------------------------
    // Detection and the C128 gap
    // ------------------------------------------------------------------

    [Fact]
    public void Credentials_without_a_type_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => GcpCredentials.FromJson("{}"));

        Assert.Equal("missing 'type' field in credentials", error.Message);
    }

    [Fact]
    public void Client_configurations_are_rejected_at_token_time()
    {
        var credentials = GcpCredentials.FromJson(
            """{"web":{"client_id":"cid","client_secret":"secret","redirect_uris":["http://localhost"]}}""");

        var error = Assert.Throws<NotSupportedException>(
            () => credentials.TokenSource.GetToken());

        Assert.Contains("difference C128", error.Message, StringComparison.Ordinal);
    }

    private static GcpCredentials Credentials(
        Dictionary<string, string> fields,
        HttpMessageHandler handler,
        Func<DateTimeOffset>? clock = null)
        => CredentialsObject(fields, handler, clock);

    private static GcpCredentials CredentialsObject(
        object fields,
        HttpMessageHandler handler,
        Func<DateTimeOffset>? clock = null)
        => GcpCredentials.FromJson(
            JsonSerializer.Serialize(fields),
            new GcpTokenOptions { Transport = handler, Clock = clock ?? (() => FixedNow) });

    private static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&'))
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            result[Uri.UnescapeDataString(pair[..index])] =
                Uri.UnescapeDataString(pair[(index + 1)..].Replace('+', ' '));
        }

        return result;
    }

    private static byte[] Base64UrlDecode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(
            base64 + new string('=', (4 - (base64.Length % 4)) % 4));
    }

    private static string BodyText(HttpRequestMessage request)
        => request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage Text(
        HttpStatusCode status,
        string body,
        string mediaType = "application/json")
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        return new(status) { Content = content };
    }

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
