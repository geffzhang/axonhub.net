using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Groups the tests that share <see cref="AntigravityVersion"/>'s static
/// version state (the reference keeps everything in one Go package, so its
/// state is never mutated concurrently).
/// </summary>
[CollectionDefinition("antigravity-version-state", DisableParallelization = true)]
public sealed class AntigravityVersionStateCollection;

/// <summary>
/// Stands in for the version fetcher's HTTP client: answers each GET by URL so
/// tests can script changelog/updater responses. The reference uses
/// <c>httptest.Server</c> instances; this fake locks the same observable
/// behavior - which URL is fetched, in which order, and how non-200 answers
/// surface - without a network listener.
/// </summary>
internal sealed class ScriptedVersionExecutor : IExecutor
{
    public Func<string, HttpResponseData>? Handler { get; set; }

    private int _callCount;

    public int CallCount => _callCount;

    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
    {
        Interlocked.Increment(ref _callCount);
        return Handler!(request.Url);
    }

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
        => throw new NotSupportedException("the version fetcher never opens a stream");
}

/// <summary>
/// Port of llm/transformer/antigravity/version_test.go: changelog-first IDE
/// version resolution, the once guard, the bounded changelog scan, and the
/// never-downgrade rule.
/// </summary>
[Collection("antigravity-version-state")]
public sealed class AntigravityVersionTests
{
    private const string VersionURL = "http://version.test/updater";
    private const string ChangelogURL = "http://version.test/changelog";

    private static HttpResponseData Text(string body, int statusCode = 200) => new()
    {
        StatusCode = statusCode,
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static AntigravityVersionFetcher NewFetcher(IExecutor executor)
        => new(VersionURL, ChangelogURL, executor);

    // ------------------------------------------------------------------
    // FetchVersion (reference TestFetchVersion_*)
    // ------------------------------------------------------------------

    [Fact]
    public void TestFetchVersion_AutoUpdaterSuccess()
    {
        AntigravityVersion.ResetForTests();

        // The reference passes a nil changelog server (the fetch fails).
        var executor = new ScriptedVersionExecutor
        {
            Handler = url => url == ChangelogURL ? Text("", 404) : Text("3.99.0"),
        };

        NewFetcher(executor).Init(CancellationToken.None);

        Assert.Equal("3.99.0", AntigravityVersion.GetVersion());
        Assert.Equal("antigravity/3.99.0 windows/amd64", AntigravityVersion.GetUserAgent());
    }

    [Fact]
    public void TestFetchVersion_ChangelogFallback()
    {
        AntigravityVersion.ResetForTests();

        var executor = new ScriptedVersionExecutor
        {
            Handler = url => url == ChangelogURL
                ? Text("<html><article id=\"rel-ide-3.21.0\">IDE</article></html>")
                : Text("", 500),
        };

        NewFetcher(executor).Init(CancellationToken.None);

        Assert.Equal("3.21.0", AntigravityVersion.GetVersion());
    }

    [Fact]
    public void TestFetchVersion_HardcodedFallback()
    {
        AntigravityVersion.ResetForTests();

        var executor = new ScriptedVersionExecutor
        {
            Handler = _ => Text("", 503),
        };

        NewFetcher(executor).Init(CancellationToken.None);

        Assert.Equal(AntigravityVersion.UserAgentVersionFallback, AntigravityVersion.GetVersion());
    }

    [Fact]
    public void TestFetchVersion_NoSemverInResponse()
    {
        AntigravityVersion.ResetForTests();

        var executor = new ScriptedVersionExecutor
        {
            Handler = url => url == ChangelogURL
                ? Text("<article id=\"rel-ide-3.0.1\">IDE</article>")
                : Text("no version here"),
        };

        NewFetcher(executor).Init(CancellationToken.None);

        Assert.Equal("3.0.1", AntigravityVersion.GetVersion());
    }

    [Fact]
    public async Task TestInitVersion_OnceGuard()
    {
        AntigravityVersion.ResetForTests();

        var executor = new ScriptedVersionExecutor
        {
            Handler = _ => Text("3.50.0"),
        };

        AntigravityVersion.InitWithFetcher(NewFetcher(executor));

        var tasks = new Task[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(() => AntigravityVersion.InitVersion());
        }

        await Task.WhenAll(tasks);

        Assert.Equal("3.50.0", AntigravityVersion.GetVersion());
        // One initialization queries the changelog, then the updater when no IDE release is present.
        Assert.Equal(2, executor.CallCount);
    }

    [Fact]
    public void TestFetchVersion_ChangelogMaxBytes()
    {
        AntigravityVersion.ResetForTests();

        // The reference writes changelogScanBytes+100 padding bytes before the IDE release.
        var padding = new string('x', AntigravityVersion.ChangelogScanBytes + 100);

        var executor = new ScriptedVersionExecutor
        {
            Handler = url => url == ChangelogURL
                ? Text(padding + "<article id=\"rel-ide-9.9.9\">IDE</article>")
                : Text("", 500),
        };

        NewFetcher(executor).Init(CancellationToken.None);

        Assert.Equal(AntigravityVersion.UserAgentVersionFallback, AntigravityVersion.GetVersion());
    }

    [Theory]
    [InlineData(
        "IDE only",
        "<article id=\"rel-agy-9.0.0\"></article><article id=\"rel-ide-2.10.0\"></article>",
        "2.0.6",
        "2.10.0")]
    [InlineData(
        "stale updater",
        "<article id=\"rel-cli-9.0.0\"></article>",
        "2.0.6",
        AntigravityVersion.UserAgentVersionFallback)]
    [InlineData(
        "stale changelog",
        "<article id=\"rel-ide-1.23.2\"></article>",
        "2.0.6",
        AntigravityVersion.UserAgentVersionFallback)]
    public void TestFetchVersion_PrefersIDEAndNeverDowngrades(string name, string changelog, string updater, string expected)
    {
        _ = name;
        AntigravityVersion.ResetForTests();

        var executor = new ScriptedVersionExecutor
        {
            Handler = url => url == ChangelogURL ? Text(changelog) : Text(updater),
        };

        NewFetcher(executor).Init(CancellationToken.None);

        Assert.Equal(expected, AntigravityVersion.GetVersion());

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        AntigravityVersion.SetClientHeaders(headers);
        Assert.Equal(AntigravityVersion.GetUserAgent(), HttpClientUtils.HeaderGet(headers, "User-Agent"));
        Assert.Equal(expected, HttpClientUtils.HeaderGet(headers, "X-Client-Version"));
    }

    // ------------------------------------------------------------------
    // NewerVersion (reference TestNewerVersionMalformedInputs)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("2.5.5", "2.5", "2.5.5")]
    [InlineData("2.5", "3.0.0", "2.5")]
    [InlineData("2.5.5", "3.0.bad", "2.5.5")]
    [InlineData("2.5.bad", "3.0.0", "2.5.bad")]
    [InlineData("2.5.5", "2.5.5-beta", "2.5.5")]
    [InlineData("2.5.5", "2.5.5.1", "2.5.5")]
    [InlineData("2.5.5", "-3.0.0", "2.5.5")]
    [InlineData("2.5.5", "", "2.5.5")]
    [InlineData("2.5.5", "2.10.0", "2.10.0")]
    [InlineData("2.5.5", "3.0.0", "3.0.0")]
    [InlineData("2.5.5", "2.5.6", "2.5.6")]
    [InlineData("2.5.5", "2.0.6", "2.5.5")]
    public void TestNewerVersionMalformedInputs(string current, string candidate, string expected)
        => Assert.Equal(expected, AntigravityVersion.NewerVersion(current, candidate));
}
