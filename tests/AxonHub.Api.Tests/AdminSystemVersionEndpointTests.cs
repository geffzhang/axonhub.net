using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the systemVersion/checkForUpdate GraphQL surface, the outbound
/// GitHub releases request shape, the release-selection rules ported from
/// version_test.go and the Go duration layout used by uptime (C235).
/// </summary>
public sealed class AdminSystemVersionEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly RecordingReleaseHandler _releases = new();
    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<ISystemVersionService>(
            new SystemVersionService(TimeProvider.System, _releases));

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _releases.Dispose();
    }

    /// <summary>The handler owns no unmanaged state; this only satisfies CA1001.</summary>
    public void Dispose() => _releases.Dispose();

    [Fact]
    public async Task SystemVersionReturnsTheReferenceBuildInfoShape()
    {
        var data = await QueryAsync(
            """
            {
              systemVersion { version commit buildTime goVersion platform uptime }
            }
            """);

        var version = data.GetProperty("systemVersion");
        Assert.Equal("v1.0.0-beta9", version.GetProperty("version").GetString());
        Assert.Equal("", version.GetProperty("commit").GetString());
        Assert.Equal("", version.GetProperty("buildTime").GetString());
        Assert.False(
            string.IsNullOrWhiteSpace(version.GetProperty("goVersion").GetString()));

        var platform = version.GetProperty("platform").GetString() ?? string.Empty;
        Assert.Matches(
            "^(windows|linux|darwin|freebsd|unknown)/"
            + "(amd64|386|arm64|arm|wasm|s390x|loong64|ppc64le|riscv64|unknown)$",
            platform);

        // Go time.Duration.String(): sub-second units or h/m/s with an
        // optional trimmed fraction.
        var uptime = version.GetProperty("uptime").GetString() ?? string.Empty;
        Assert.Matches(
            "^-?(\\d+(\\.\\d+)?(ns|µs|ms)"
            + "|\\d+h\\d+m\\d+(\\.\\d+)?s"
            + "|\\d+m\\d+(\\.\\d+)?s"
            + "|\\d+(\\.\\d+)?s)$",
            uptime);
    }

    [Fact]
    public async Task CheckForUpdateSelectsTheHighestEligibleStableRelease()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-10);
        _releases.Body = JsonSerializer.Serialize(new object[]
        {
            new { tag_name = "v1.0.0-beta10", draft = false, prerelease = true, published_at = old },
            new { tag_name = "v1.2.0", draft = false, prerelease = false, published_at = old },
            new { tag_name = "v2.0.0", draft = true, prerelease = false, published_at = old },
            new { tag_name = "axonclaw/v9.9.9", draft = false, prerelease = false, published_at = old },
            new { tag_name = "v3.0.0", draft = false, prerelease = false, published_at = recent },
        });

        var data = await QueryAsync(
            "{ checkForUpdate { currentVersion latestVersion hasUpdate releaseUrl } }");

        var check = data.GetProperty("checkForUpdate");
        Assert.Equal("v1.0.0-beta9", check.GetProperty("currentVersion").GetString());
        Assert.Equal("v1.2.0", check.GetProperty("latestVersion").GetString());
        Assert.True(check.GetProperty("hasUpdate").GetBoolean());
        Assert.Equal(
            "https://github.com/looplj/axonhub/releases/tag/v1.2.0",
            check.GetProperty("releaseUrl").GetString());

        // url.Values.Encode sorts the query keys; the headers are the
        // reference constants.
        Assert.Equal(HttpMethod.Get, _releases.Method);
        Assert.Equal(
            "https://api.github.com/repos/looplj/axonhub/releases?page=1&per_page=100",
            _releases.RequestUri);
        Assert.Equal("application/vnd.github.v3+json", _releases.Accept);
        Assert.Equal("AxonHub-Version-Checker", _releases.UserAgent);
    }

    [Fact]
    public async Task CheckForUpdateIncludesBetasOnlyWhenRequested()
    {
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        _releases.Body = JsonSerializer.Serialize(new object[]
        {
            new { tag_name = "v1.0.0", draft = false, prerelease = false, published_at = old },
            new { tag_name = "v1.1.0-beta9", draft = false, prerelease = true, published_at = old },
            new { tag_name = "v1.1.0-beta10", draft = false, prerelease = true, published_at = old },
            new { tag_name = "v9.0.0-rc1", draft = false, prerelease = true, published_at = old },
        });

        var data = await QueryAsync(
            "{ checkForUpdate(includeBeta: true) { latestVersion hasUpdate } }");

        var check = data.GetProperty("checkForUpdate");
        Assert.Equal("v1.1.0-beta10", check.GetProperty("latestVersion").GetString());
        Assert.True(check.GetProperty("hasUpdate").GetBoolean());
    }

    [Fact]
    public async Task CheckForUpdateSurfacesTheReferenceErrorChain()
    {
        _releases.StatusCode = HttpStatusCode.InternalServerError;

        var response = await PostAsync("{ checkForUpdate { latestVersion } }");

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to check for update: failed to fetch latest release: "
            + "GitHub API returned status 500",
            error.GetProperty("message").GetString());

        _releases.StatusCode = HttpStatusCode.OK;
        _releases.Body = "not json";

        response = await PostAsync("{ checkForUpdate { latestVersion } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.StartsWith(
            "failed to check for update: failed to fetch latest release: "
            + "failed to decode releases: ",
            error.GetProperty("message").GetString());

        _releases.Body = "[]";

        response = await PostAsync("{ checkForUpdate { latestVersion } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to check for update: failed to fetch latest release: "
            + "no eligible release found",
            error.GetProperty("message").GetString());

        _releases.Failure = new HttpRequestException("boom");

        response = await PostAsync("{ checkForUpdate { latestVersion } }");

        error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "failed to check for update: failed to fetch latest release: "
            + "failed to fetch releases: boom",
            error.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("v1.0.0", "v2.0.0", true)]
    [InlineData("v1.0.0", "v1.1.0", true)]
    [InlineData("v1.0.0", "v1.0.1", true)]
    [InlineData("v1.0.0", "v1.0.0", false)]
    [InlineData("v2.0.0", "v1.0.0", false)]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("v1.0.0", "1.1.0", true)]
    [InlineData("1.0.0", "v1.1.0", true)]
    [InlineData("v1.2.3", "v2.0.0", true)]
    [InlineData("v1.5.0", "v1.6.0", true)]
    [InlineData("v1.5.2", "v1.5.3", true)]
    [InlineData("invalid", "v1.0.0", false)]
    [InlineData("v1.0.0", "invalid", false)]
    [InlineData("invalid", "invalid", false)]
    [InlineData("", "v1.0.0", false)]
    [InlineData("v1.0.0", "", false)]
    [InlineData("", "", false)]
    [InlineData("v1.0.0-beta", "v1.0.0", true)]
    [InlineData("v1.0.0", "v1.0.1-beta", true)]
    [InlineData("v1.0.0-beta5", "v1.0.0-beta6", true)]
    [InlineData("v1.0.0-beta9", "v1.0.0-beta10", true)]
    [InlineData("v1.0.0-beta10", "v1.0.0-beta9", false)]
    [InlineData("v1.0.0-beta99", "v1.0.0-beta100", true)]
    [InlineData("v1.0.0-beta.9", "v1.0.0-beta.10", true)]
    [InlineData("v1.0.0-beta10", "v1.0.0-beta.10", false)]
    [InlineData("v1.0.0-rc9", "v1.0.0-rc10", true)]
    [InlineData("v1.0.0-beta10", "v1.0.0-beta10-unstable.20260907", true)]
    [InlineData("v1.0.0-beta9-unstable.20260907", "v1.0.0-beta10", true)]
    [InlineData("v1.0.0-beta10", "v1.0.0", true)]
    [InlineData("v1.0.0+build.1", "v1.0.0+build.2", false)]
    [InlineData("v1.2.3", "v1.2.3.4", false)]
    public void IsNewerVersionMatchesTheReferenceTable(
        string current,
        string latest,
        bool expected)
        => Assert.Equal(expected, SystemVersionService.IsNewerVersion(current, latest));

    [Theory]
    [InlineData("v1.0.0", "v1.0.0", 0)]
    [InlineData("v2.0.0", "v1.0.0", 1)]
    [InlineData("v1.1.0", "v1.2.0", -1)]
    [InlineData("v1.0.2", "v1.0.1", 1)]
    [InlineData("v1.0.0-beta5", "v1.0.0-beta6", -1)]
    [InlineData("v1.0.0-beta10", "v1.0.0-beta9", 1)]
    [InlineData("v1.0.0-beta100", "v1.0.0-beta99", 1)]
    [InlineData("v1.0.0-beta10", "v1.0.0-beta.10", 0)]
    [InlineData("v1.0.0-beta010", "v1.0.0-beta10", 0)]
    [InlineData("v1.0.0-beta99999999999999999999", "v1.0.0-beta99999999999999999998", 1)]
    [InlineData("v1.0.0-beta", "v1.0.0-beta1", -1)]
    [InlineData("v1.0.0-alpha10", "v1.0.0-beta1", -1)]
    [InlineData("v1.0.0-beta10", "v1.0.0-rc1", -1)]
    [InlineData("v1.0.0", "v1.0.0-beta10", 1)]
    [InlineData("v1.0.0+build.2", "v1.0.0+build.1", 0)]
    [InlineData("1.0.0-beta10", "v1.0.0-beta9", 1)]
    public void CompareVersionsMatchesTheReferenceTable(string a, string b, int expected)
    {
        Assert.Equal(expected, SystemVersionService.CompareVersions(a, b));

        // The comparison must be antisymmetric.
        Assert.Equal(-expected, SystemVersionService.CompareVersions(b, a));
    }

    [Fact]
    public void CompareVersionsRejectsInvalidVersions()
    {
        Assert.Throws<FormatException>(
            () => SystemVersionService.CompareVersions("invalid", "v1.0.0"));
        Assert.Throws<FormatException>(
            () => SystemVersionService.CompareVersions("v1.0.0", ""));

        // Masterminds NewVersion accepts only the lowercase "v" prefix and
        // validates build metadata even though it is ignored.
        Assert.Throws<FormatException>(
            () => SystemVersionService.CompareVersions("V1.0.0", "v1.0.0"));
        Assert.Throws<FormatException>(
            () => SystemVersionService.CompareVersions("v1.0.0+bad_meta", "v1.0.0"));
    }

    [Fact]
    public void SelectLatestGitHubReleaseMatchesTheReferenceTable()
    {
        var now = DateTimeOffset.UtcNow;
        SystemVersionService.GitHubRelease[] releases =
        [
            new("v1.0.0-beta6", false, false, now.AddHours(-1)),
            new("v1.0.0-rc1", false, false, now.AddHours(-2)),
            new("v0.9.43", false, false, now.AddHours(-3)),
        ];

        Assert.Equal(
            "v0.9.43",
            SystemVersionService.SelectLatestGitHubRelease(releases, false, now));
        Assert.Equal(
            "v1.0.0-beta6",
            SystemVersionService.SelectLatestGitHubRelease(releases, true, now));

        // A higher stable release outranks a lower beta when betas are on.
        Assert.Equal(
            "v2.0.0",
            SystemVersionService.SelectLatestGitHubRelease(
                [
                    new("v1.9.0-beta1", false, false, now.AddHours(-1)),
                    new("v2.0.0", false, false, now.AddHours(-2)),
                ],
                true,
                now));

        // Drafts, cooldown-window releases, other-service tags and non-beta
        // prereleases are skipped.
        Assert.Equal(
            "v1.0.0-beta4",
            SystemVersionService.SelectLatestGitHubRelease(
                [
                    new("v1.0.0-beta6", false, true, now.AddHours(-1)),
                    new("v1.0.0-beta5", false, false, now.AddMinutes(-15)),
                    new("axonclaw/v2.0.0", false, false, now.AddHours(-1)),
                    new("v1.0.0-beta4", true, false, now.AddHours(-2)),
                ],
                true,
                now));

        // The beta counter compares numerically across the ten boundary.
        Assert.Equal(
            "v1.0.0-beta10",
            SystemVersionService.SelectLatestGitHubRelease(
                [
                    new("v1.0.0-beta9", true, false, now.AddHours(-96)),
                    new("v1.0.0-beta10", true, false, now.AddHours(-24)),
                ],
                true,
                now));

        var error = Assert.Throws<InvalidOperationException>(
            () => SystemVersionService.SelectLatestGitHubRelease(
                [new("v1.0.0-beta6", false, false, now.AddHours(-1))],
                false,
                now));
        Assert.Equal("no eligible release found", error.Message);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(100, "100ns")]
    [InlineData(1_500, "1.5µs")]
    [InlineData(100_000, "100µs")]
    [InlineData(1_000_000, "1ms")]
    [InlineData(1_500_000, "1.5ms")]
    [InlineData(1_000_000_000, "1s")]
    [InlineData(1_500_000_000, "1.5s")]
    [InlineData(59_000_000_000, "59s")]
    [InlineData(60_000_000_000, "1m0s")]
    [InlineData(90_000_000_000, "1m30s")]
    [InlineData(3_600_000_000_000, "1h0m0s")]
    [InlineData(3_661_000_000_000, "1h1m1s")]
    [InlineData(-1_500_000_000, "-1.5s")]
    public void GoDurationMatchesTheReferenceLayout(long nanoseconds, string expected)
        => Assert.Equal(
            expected,
            GoDuration.Format(TimeSpan.FromTicks(nanoseconds / 100)));

    private async Task<JsonElement> QueryAsync(string query)
    {
        var response = await PostAsync(query);
        Assert.False(response.TryGetProperty("errors", out _), response.ToString());
        return response.GetProperty("data");
    }

    private async Task<JsonElement> PostAsync(string query)
    {
        var payload = JsonSerializer.Serialize(new { query });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Serves a scripted GitHub releases response and records the outbound
    /// request so the wire shape can be asserted.
    /// </summary>
    private sealed class RecordingReleaseHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";

        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        public Exception? Failure { get; set; }

        public HttpMethod? Method { get; private set; }

        public string? RequestUri { get; private set; }

        public string? Accept { get; private set; }

        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            Accept = request.Headers.Accept.ToString();
            UserAgent = request.Headers.UserAgent.ToString();

            if (Failure is not null)
            {
                return Task.FromException<HttpResponseMessage>(Failure);
            }

            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
