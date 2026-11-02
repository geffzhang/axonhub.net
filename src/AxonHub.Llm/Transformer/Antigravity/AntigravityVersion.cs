using System.Text;
using System.Text.RegularExpressions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>
/// Port of the Antigravity IDE version state (llm/transformer/antigravity/version.go):
/// resolves the IDE client version once per process from the official changelog
/// (preferred) or the auto-updater endpoint, never downgrading below the
/// hardcoded fallback, and serves it to every request path.
/// </summary>
public static class AntigravityVersion
{
    /// <summary>Port of UserAgentVersionFallback: used when the remote fetch fails.</summary>
    public const string UserAgentVersionFallback = "2.5.5";

    /// <summary>Port of defaultVersionURL: the auto-updater endpoint returning plain text.</summary>
    private const string DefaultVersionURL = "https://antigravity-auto-updater-974169037036.us-central1.run.app";

    /// <summary>Port of defaultChangelogURL: a fallback page to scrape the version from.</summary>
    private const string DefaultChangelogURL = "https://antigravity.google/docs/changelog";

    /// <summary>Port of versionFetchTimeout: the maximum time allowed per fetch attempt.</summary>
    private static readonly TimeSpan VersionFetchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Port of changelogScanBytes: the number of bytes read from the changelog page.</summary>
    internal const int ChangelogScanBytes = 2 * 1024 * 1024;

    /// <summary>Port of versionRegex; Go \d is ASCII-only, hence the [0-9] class.</summary>
    internal static readonly Regex VersionRegex = new("[0-9]+\\.[0-9]+\\.[0-9]+");

    /// <summary>Port of ideVersionRegex: only IDE releases (the page also lists CLI/SDK/2.0).</summary>
    internal static readonly Regex IdeVersionRegex = new("id=\"rel-ide-([0-9]+\\.[0-9]+\\.[0-9]+)\"");

    private static readonly object VersionSync = new();

    private static string _currentVersion = UserAgentVersionFallback;
    private static bool _initialized;
    private static AntigravityVersionFetcher _fetcher = new(DefaultVersionURL, DefaultChangelogURL);

    /// <summary>Port of GetUserAgent: the resolved IDE client identity for OAuth requests.</summary>
    public static string GetUserAgent()
    {
        lock (VersionSync)
        {
            return "antigravity/" + _currentVersion + " windows/amd64";
        }
    }

    /// <summary>Port of GetVersion: the current IDE version under the shared version lock.</summary>
    public static string GetVersion()
    {
        lock (VersionSync)
        {
            return _currentVersion;
        }
    }

    /// <summary>
    /// Port of SetClientHeaders: keeps model discovery and inference on the
    /// same client identity.
    /// </summary>
    public static void SetClientHeaders(Dictionary<string, List<string>> headers)
    {
        var version = GetVersion();
        HttpClientUtils.HeaderSet(headers, "User-Agent", "antigravity/" + version + " windows/amd64");
        HttpClientUtils.HeaderSet(headers, "X-Client-Name", "antigravity");
        HttpClientUtils.HeaderSet(headers, "X-Client-Version", version);
        HttpClientUtils.HeaderSet(headers, "X-Goog-Api-Client", AntigravityConstants.ApiClient);
        HttpClientUtils.HeaderSet(headers, "Client-Metadata", AntigravityConstants.ClientMetadata);
    }

    /// <summary>Port of InitVersion: resolves the IDE version once per process.</summary>
    public static void InitVersion(CancellationToken cancellationToken = default)
    {
        AntigravityVersionFetcher fetcher;

        lock (VersionSync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            fetcher = _fetcher;
        }

        fetcher.Init(cancellationToken);
    }

    /// <summary>Port of setVersion: publishes the resolved version to all request paths.</summary>
    internal static void SetVersion(string version)
    {
        lock (VersionSync)
        {
            _currentVersion = version;
        }
    }

    /// <summary>Test hook: swaps the fetcher and re-arms the once guard.</summary>
    internal static void InitWithFetcher(AntigravityVersionFetcher fetcher)
    {
        lock (VersionSync)
        {
            _fetcher = fetcher;
            _initialized = false;
        }
    }

    /// <summary>Test hook: the Go tests reset the package-level state between tests.</summary>
    internal static void ResetForTests()
    {
        lock (VersionSync)
        {
            _currentVersion = UserAgentVersionFallback;
            _initialized = false;
            _fetcher = new AntigravityVersionFetcher(DefaultVersionURL, DefaultChangelogURL);
        }
    }

    /// <summary>
    /// Port of newerVersion: preserves the current version for malformed input
    /// and otherwise selects the newer release.
    /// </summary>
    internal static string NewerVersion(string current, string candidate)
    {
        var currentParts = current.Split('.');
        var candidateParts = candidate.Split('.');

        if (currentParts.Length != 3 || candidateParts.Length != 3)
        {
            return current;
        }

        var currentNumbers = new int[3];
        var candidateNumbers = new int[3];

        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(currentParts[i], out currentNumbers[i]) || currentNumbers[i] < 0)
            {
                return current;
            }

            if (!int.TryParse(candidateParts[i], out candidateNumbers[i]) || candidateNumbers[i] < 0)
            {
                return current;
            }
        }

        for (var i = 0; i < 3; i++)
        {
            var a = currentNumbers[i];
            var b = candidateNumbers[i];
            if (b > a)
            {
                return candidate;
            }

            if (b < a)
            {
                return current;
            }
        }

        return current;
    }
}

/// <summary>
/// Port of antigravity.versionFetcher (version.go:77-87): selects an IDE release
/// without downgrading below the supported fallback.
/// </summary>
internal sealed class AntigravityVersionFetcher
{
    private readonly IExecutor _executor;

    /// <summary>
    /// The Go default fetcher wraps an <c>http.Client{Timeout: 5s}</c>; the port
    /// reuses the executor seam so tests can script both version sources.
    /// </summary>
    internal AntigravityVersionFetcher(string versionUrl, string changelogUrl, IExecutor? executor = null)
    {
        VersionURL = versionUrl;
        ChangelogURL = changelogUrl;
        _executor = executor ?? new HttpExecutor(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        });
    }

    internal string VersionURL { get; }

    internal string ChangelogURL { get; }

    /// <summary>Port of (*versionFetcher).init: changelog first, then updater, then fallback.</summary>
    internal void Init(CancellationToken cancellationToken)
    {
        var fallback = AntigravityVersion.UserAgentVersionFallback;

        // Prefer the official IDE changelog over the updater's potentially stale fixed version.
        if (FetchVersion(cancellationToken, ChangelogURL, AntigravityVersion.ChangelogScanBytes) is { Length: > 0 } v)
        {
            AntigravityVersion.SetVersion(AntigravityVersion.NewerVersion(fallback, v));
            return;
        }

        if (FetchVersion(cancellationToken, VersionURL, 0) is { Length: > 0 } v2)
        {
            AntigravityVersion.SetVersion(AntigravityVersion.NewerVersion(fallback, v2));
            return;
        }

        AntigravityVersion.SetVersion(fallback);
    }

    /// <summary>
    /// Port of (*versionFetcher).fetchVersion: a bounded changelog scrape or the
    /// updater's plain-text response, returning "" when nothing usable is found.
    /// </summary>
    internal string FetchVersion(CancellationToken cancellationToken, string url, int maxBytes)
    {
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = url,
        };

        HttpResponseData response;
        try
        {
            // The real executor throws HttpClientError for >=400; scripted test
            // executors may return non-2xx responses directly. Both are fetch failures.
            response = _executor.Do(cancellationToken, request);
        }
        catch (Exception)
        {
            return "";
        }

        if (response.StatusCode != 200)
        {
            return "";
        }

        var body = response.Body ?? [];

        // Go io.ReadFull reads at most maxBytes; the remainder is never inspected.
        if (maxBytes > 0 && body.Length > maxBytes)
        {
            body = body[..maxBytes];
        }

        var text = Encoding.UTF8.GetString(body);
        if (maxBytes > 0)
        {
            // Go FindSubmatch returns the capture group, not the whole match.
            var ideMatch = AntigravityVersion.IdeVersionRegex.Match(text);
            return ideMatch.Success ? ideMatch.Groups[1].Value : "";
        }

        var versionMatch = AntigravityVersion.VersionRegex.Match(text);
        return versionMatch.Success ? versionMatch.Value : "";
    }
}
