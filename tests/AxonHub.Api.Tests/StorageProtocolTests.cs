using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Storage;

namespace AxonHub.Api.Tests;

/// <summary>
/// Wire-shape coverage for the hand-rolled WebDAV and GCS clients (C199):
/// verbs, URLs, auth headers and payloads are captured with a scripted
/// HttpMessageHandler instead of a live server.
/// </summary>
public sealed class StorageProtocolTests
{
    // ---------------------------------------------------------------- WebDAV

    [Fact]
    public void WebDavClientUsesBasicAuthAndTheTenMinuteTimeout()
    {
        using var client = WebDavDataStorageBackend.CreateHttpClient(new WebDavStorageSettings
        {
            Username = "alice",
            Password = "secret",
        });

        Assert.Equal(TimeSpan.FromMinutes(10), client.Timeout);
        Assert.Equal(
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:secret")),
            client.DefaultRequestHeaders.Authorization?.ToString());
    }

    [Fact]
    public async Task WebDavWriteCreatesTheBaseCollectionAndPutsTheBytes()
    {
        var handler = new RecordingHandler((request, _) => request.Method.Method switch
        {
            "MKCOL" => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            "PUT" => new HttpResponseMessage(HttpStatusCode.Created),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var backend = NewWebDavBackend(handler, path: "/backups");

        await backend.WriteAsync(
            "/axonhub-backup-2026.json",
            Encoding.UTF8.GetBytes("payload"),
            CancellationToken.None);

        Assert.Collection(
            handler.Requests,
            request =>
            {
                Assert.Equal("MKCOL", request.Method);
                Assert.Equal("http://dav.example.com/dav/backups/", request.Url);
            },
            request =>
            {
                Assert.Equal("PUT", request.Method);
                // TrimPrefix(key, "/") then BasePathFs("/backups").
                Assert.Equal(
                    "http://dav.example.com/dav/backups/axonhub-backup-2026.json",
                    request.Url);
                Assert.Equal("payload", request.Body);
            });
    }

    [Fact]
    public async Task WebDavWriteCreatesEveryParentCollection()
    {
        var handler = new RecordingHandler((request, _) => request.Method.Method switch
        {
            "MKCOL" => new HttpResponseMessage(HttpStatusCode.Created),
            "PUT" => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var backend = NewWebDavBackend(handler, path: string.Empty);

        await backend.WriteAsync(
            "a/b/backup.json",
            [1, 2, 3],
            CancellationToken.None);

        Assert.Equal(
            [
                "MKCOL http://dav.example.com/dav/",
                "MKCOL http://dav.example.com/dav/a/",
                "MKCOL http://dav.example.com/dav/a/b/",
                "PUT http://dav.example.com/dav/a/b/backup.json",
            ],
            handler.Requests.Select(request => request.Method + " " + request.Url));
    }

    [Fact]
    public async Task WebDavReadSurfacesTheHttpStatus()
    {
        var handler = new RecordingHandler(
            (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var backend = NewWebDavBackend(handler, path: string.Empty);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => backend.ReadAsync("missing.json", CancellationToken.None));

        Assert.Equal("failed to read file: 404 Not Found", ex.Message);
    }

    [Fact]
    public async Task WebDavListParsesTheMultistatusAndSkipsTheCollection()
    {
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>/dav/backups/</D:href><D:propstat><D:prop>
                <D:resourcetype><D:collection/></D:resourcetype>
              </D:prop></D:propstat></D:response>
              <D:response><D:href>/dav/backups/axonhub-backup-a.json</D:href><D:propstat><D:prop>
                <D:getlastmodified>Fri, 02 Jan 2026 03:04:05 GMT</D:getlastmodified>
              </D:prop></D:propstat></D:response>
              <D:response><D:href>/dav/backups/notes.txt</D:href><D:propstat><D:prop>
                <D:getlastmodified>Sat, 03 Jan 2026 04:05:06 GMT</D:getlastmodified>
              </D:prop></D:propstat></D:response>
            </D:multistatus>
            """;

        var handler = new RecordingHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)207)
            {
                Content = new StringContent(Xml, Encoding.UTF8, "application/xml"),
            };
            return response;
        });
        using var backend = NewWebDavBackend(handler, path: "/backups");

        var entries = await backend.ListAsync(CancellationToken.None);

        Assert.Equal(1, handler.Requests[0].Depth);
        Assert.Equal("PROPFIND", handler.Requests[0].Method);
        Assert.Equal("http://dav.example.com/dav/backups/", handler.Requests[0].Url);
        Assert.Equal(
            ["axonhub-backup-a.json", "notes.txt"],
            entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            entries.Single(entry => entry.Name == "axonhub-backup-a.json").LastModified);
    }

    private static WebDavDataStorageBackend NewWebDavBackend(
        HttpMessageHandler handler,
        string path)
        => new(
            new DataStorageSettings
            {
                WebDav = new WebDavStorageSettings
                {
                    Url = "http://dav.example.com/dav",
                    Username = "u",
                    Password = "p",
                    Path = path,
                },
            },
            new HttpClient(handler));

    // -------------------------------------------------------------------- GCS

    [Fact]
    public async Task GcsAuthorizedUserRefreshesTheTokenAndUploadsWithMediaSemantics()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath switch
            {
                "/token" => JsonResponse("""{"access_token":"tok-1","expires_in":3600}"""),
                "/upload/storage/v1/b/bucket/o" => JsonResponse("{}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        using var backend = NewGcsBackend(
            handler,
            """
            {"type":"authorized_user","client_id":"cid","client_secret":"cs",
             "refresh_token":"rt","token_uri":"http://gcs.test/token"}
            """);

        await backend.WriteAsync(
            "axonhub-backup-2026.json",
            Encoding.UTF8.GetBytes("payload"),
            CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("http://gcs.test/token", handler.Requests[0].Url);
        Assert.Equal(
            "grant_type=refresh_token&client_id=cid&client_secret=cs&refresh_token=rt",
            handler.Requests[0].Body);

        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Equal(
            "http://gcs.test/upload/storage/v1/b/bucket/o?uploadType=media"
            + "&name=axonhub-backup-2026.json",
            handler.Requests[1].Url);
        Assert.Equal("Bearer tok-1", handler.Requests[1].Authorization);
        Assert.Equal("payload", handler.Requests[1].Body);
    }

    [Fact]
    public async Task GcsServiceAccountSignsAJwtBearerAssertion()
    {
        using var rsa = RSA.Create(2048);
        var credential = JsonSerializer.Serialize(new
        {
            type = "service_account",
            client_email = "svc@project.iam.gserviceaccount.com",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            token_uri = "http://gcs.test/token",
        });

        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath switch
            {
                "/token" => JsonResponse("""{"access_token":"sa-token","expires_in":3600}"""),
                "/upload/storage/v1/b/bucket/o" => JsonResponse("{}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        using var backend = NewGcsBackend(handler, credential);

        await backend.WriteAsync("backup.json", [7], CancellationToken.None);

        var form = ParseForm(handler.Requests[0].Body!);
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
        var segments = form["assertion"].Split('.');
        Assert.Equal(3, segments.Length);

        using var claims = JsonDocument.Parse(DecodeBase64Url(segments[1]));
        Assert.Equal(
            "svc@project.iam.gserviceaccount.com",
            claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(
            GcsDataStorageBackend.StorageScope,
            claims.RootElement.GetProperty("scope").GetString());
        Assert.Equal(
            "http://gcs.test/token",
            claims.RootElement.GetProperty("aud").GetString());
    }

    [Fact]
    public async Task GcsCachesTheAccessTokenAcrossRequests()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath switch
            {
                "/token" => JsonResponse("""{"access_token":"tok","expires_in":3600}"""),
                "/storage/v1/b/bucket/o/backup.json" => new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        using var backend = NewGcsBackend(
            handler,
            """
            {"type":"authorized_user","client_id":"c","client_secret":"s",
             "refresh_token":"r","token_uri":"http://gcs.test/token"}
            """);

        await backend.DeleteAsync("backup.json", CancellationToken.None);
        await backend.DeleteAsync("backup.json", CancellationToken.None);

        Assert.Equal(1, handler.Requests.Count(request => request.Url.EndsWith("/token", StringComparison.Ordinal)));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == "DELETE"));
    }

    [Fact]
    public async Task GcsReadAndDeleteMap404ToObjectNotExist()
    {
        var handler = new RecordingHandler((request, _) =>
            request.RequestUri!.AbsolutePath == "/token"
                ? JsonResponse("""{"access_token":"tok","expires_in":3600}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    """{"error":{"code":404,"message":"No such object","status":"NOT_FOUND"}}"""),
            });
        using var backend = NewGcsBackend(
            handler,
            """
            {"type":"authorized_user","client_id":"c","client_secret":"s",
             "refresh_token":"r","token_uri":"http://gcs.test/token"}
            """);

        var read = await Assert.ThrowsAsync<FileNotFoundException>(
            () => backend.ReadAsync("missing.json", CancellationToken.None));
        var deleted = await Assert.ThrowsAsync<FileNotFoundException>(
            () => backend.DeleteAsync("missing.json", CancellationToken.None));

        Assert.Equal("storage: object doesn't exist", read.Message);
        Assert.Equal("storage: object doesn't exist", deleted.Message);
    }

    [Fact]
    public async Task GcsListFollowsPagesAndParsesTheUpdatedTime()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/token", StringComparison.Ordinal))
            {
                return JsonResponse("""{"access_token":"tok","expires_in":3600}""");
            }

            return url.Contains("pageToken=next", StringComparison.Ordinal)
                ? JsonResponse(
                    """{"items":[{"name":"axonhub-backup-b.json","updated":"2026-01-03T04:05:06Z"}]}""")
                : JsonResponse(
                    """{"items":[{"name":"axonhub-backup-a.json","updated":"2026-01-02T03:04:05Z"}],
                        "nextPageToken":"next"}""");
        });
        using var backend = NewGcsBackend(
            handler,
            """
            {"type":"authorized_user","client_id":"c","client_secret":"s",
             "refresh_token":"r","token_uri":"http://gcs.test/token"}
            """);

        var entries = await backend.ListAsync(CancellationToken.None);

        Assert.Equal(
            ["axonhub-backup-a.json", "axonhub-backup-b.json"],
            entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            entries.Single(entry => entry.Name == "axonhub-backup-a.json").LastModified);
    }

    private static GcsDataStorageBackend NewGcsBackend(
        HttpMessageHandler handler,
        string credential)
        => new(
            new GcsStorageSettings { BucketName = "bucket", Credential = credential },
            new HttpClient(handler),
            "http://gcs.test",
            TimeProvider.System);

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static Dictionary<string, string> ParseForm(string body)
        => body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.Ordinal);

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record RecordedRequest(
        string Method,
        string Url,
        string? Authorization,
        string? Body,
        int? Depth);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            int? depth = null;
            if (request.Headers.TryGetValues("Depth", out var values)
                && int.TryParse(values.FirstOrDefault(), out var parsed))
            {
                depth = parsed;
            }

            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                body,
                depth));

            return respond(request, body ?? string.Empty);
        }
    }
}
