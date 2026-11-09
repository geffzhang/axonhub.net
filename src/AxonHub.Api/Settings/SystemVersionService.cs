using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Settings;

/// <summary>Port of build.Info (internal/build/info.go:30-38).</summary>
public sealed record SystemBuildInfo
{
    public string Version { get; init; } = string.Empty;

    public string Commit { get; init; } = string.Empty;

    public string BuildTime { get; init; } = string.Empty;

    public string GoVersion { get; init; } = string.Empty;

    public string Platform { get; init; } = string.Empty;

    public string Uptime { get; init; } = string.Empty;
}

/// <summary>Port of biz.VersionCheckResult (internal/server/biz/version.go:30-35).</summary>
public sealed record VersionCheckResult
{
    public string CurrentVersion { get; init; } = string.Empty;

    public string LatestVersion { get; init; } = string.Empty;

    public bool HasUpdate { get; init; }

    public string ReleaseUrl { get; init; } = string.Empty;
}

/// <summary>
/// The management version surface: the <c>systemVersion</c> build metadata and
/// the <c>checkForUpdate</c> GitHub release lookup.
/// </summary>
public interface ISystemVersionService
{
    /// <summary>Port of build.GetBuildInfo (internal/build/info.go:44-54).</summary>
    SystemBuildInfo GetBuildInfo();

    /// <summary>Port of SystemService.CheckForUpdate (version.go:39-59).</summary>
    Task<VersionCheckResult> CheckForUpdateAsync(
        bool includeBeta,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of the version-checking members of SystemService
/// (internal/server/biz/version.go). The build metadata values are
/// environment-specific (C235); the release selection and semver comparison
/// are ported verbatim.
/// </summary>
public sealed class SystemVersionService(
    TimeProvider timeProvider,
    HttpMessageHandler? transport = null) : ISystemVersionService
{
    /// <summary>
    /// The frozen reference ships <c>internal/build/VERSION</c> =
    /// <c>v1.0.0-beta9</c>; local builds use it as-is (info.go:14-27).
    /// </summary>
    public const string Version = "v1.0.0-beta9";

    /// <summary>Port of build.StartTime (info.go:22): the process start instant.</summary>
    private static readonly DateTimeOffset StartTime = CaptureProcessStart();

    /// <summary>releaseCooldownDuration (version.go:83).</summary>
    internal static readonly TimeSpan ReleaseCooldown = TimeSpan.FromMinutes(30);

    /// <summary>fetchLatestGitHubRelease target (version.go:98-114).</summary>
    internal const string GitHubReleasesUrl =
        "https://api.github.com/repos/looplj/axonhub/releases?page=1&per_page=100";

    public SystemBuildInfo GetBuildInfo() => new()
    {
        Version = Version,
        Commit = string.Empty,
        BuildTime = string.Empty,
        GoVersion = RuntimeInformation.FrameworkDescription,
        Platform = DetectPlatform(),
        Uptime = GoDuration.Format(timeProvider.GetUtcNow() - StartTime),
    };

    public async Task<VersionCheckResult> CheckForUpdateAsync(
        bool includeBeta,
        CancellationToken cancellationToken = default)
    {
        string latestVersion;
        try
        {
            latestVersion = await FetchLatestGitHubReleaseAsync(includeBeta, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to fetch latest release: " + ex.Message,
                ex);
        }

        var hasUpdate = IsNewerVersion(Version, latestVersion);

        return new VersionCheckResult
        {
            CurrentVersion = Version,
            LatestVersion = latestVersion,
            HasUpdate = hasUpdate,
            ReleaseUrl = "https://github.com/looplj/axonhub/releases/tag/" + latestVersion,
        };
    }

    /// <summary>
    /// Port of FetchLatestGitHubRelease (version.go:93-138): one GET to the
    /// GitHub releases API with a 10 second client timeout.
    /// </summary>
    private async Task<string> FetchLatestGitHubReleaseAsync(
        bool includeBeta,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(GitHubReleasesUrl));
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github.v3+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "AxonHub-Version-Checker");

        using var client = transport is null
            ? new HttpClient()
            : new HttpClient(transport, disposeHandler: false);
        client.Timeout = TimeSpan.FromSeconds(10);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            // The Go client wraps every transport failure, including its own
            // 10 second timeout.
            throw new InvalidOperationException(
                "failed to fetch releases: " + ex.Message,
                ex);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    "GitHub API returned status "
                    + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            List<GitHubReleaseJson>? stored;
            try
            {
                stored = JsonSerializer.Deserialize<List<GitHubReleaseJson>>(
                    body,
                    LlmJson.Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to decode releases: " + ex.Message,
                    ex);
            }

            var releases = stored is null
                ? new List<GitHubRelease>()
                : stored.Select(ToRelease).ToList();

            return SelectLatestGitHubRelease(
                releases,
                includeBeta,
                timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Port of selectLatestGitHubRelease (version.go:141-186). Filters drafts,
    /// service-prefixed tags, non-beta prereleases, the 30 minute post-publish
    /// cooldown and unparsable tags, then keeps the highest semantic version.
    /// </summary>
    /// <exception cref="InvalidOperationException">No release is eligible.</exception>
    internal static string SelectLatestGitHubRelease(
        IReadOnlyList<GitHubRelease> releases,
        bool includeBeta,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(releases);

        SemVersion? latestVersion = null;
        var latestTag = string.Empty;

        foreach (var release in releases)
        {
            if (release.Draft)
            {
                continue;
            }

            // Only consider axonhub tags (skip "service/vX.Y.Z").
            if (!IsAxonHubTag(release.TagName))
            {
                continue;
            }

            if (release.Prerelease || IsPreReleaseTag(release.TagName))
            {
                if (!includeBeta || !IsBetaReleaseTag(release.TagName))
                {
                    continue;
                }
            }

            if (now - release.PublishedAt < ReleaseCooldown)
            {
                continue;
            }

            if (!TryParseVersion(release.TagName, out var version))
            {
                continue;
            }

            if (latestVersion is null || CompareSemVersions(version, latestVersion) > 0)
            {
                latestVersion = version;
                latestTag = release.TagName;
            }
        }

        if (latestTag.Length > 0)
        {
            return latestTag;
        }

        throw new InvalidOperationException("no eligible release found");
    }

    /// <summary>Port of isAxonHubTag (version.go:190-193).</summary>
    internal static bool IsAxonHubTag(string tag)
        => tag.StartsWith('v');

    /// <summary>Port of isPreReleaseTag (version.go:195-209).</summary>
    internal static bool IsPreReleaseTag(string tag)
    {
        var lowerTag = tag.ToLowerInvariant();
        string[] patterns =
            ["-beta", "-rc", "-alpha", "-dev", "-preview", "-snapshot"];

        foreach (var pattern in patterns)
        {
            if (lowerTag.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of isBetaReleaseTag (version.go:211-214).</summary>
    internal static bool IsBetaReleaseTag(string tag)
        => tag.ToLowerInvariant().Contains("-beta", StringComparison.Ordinal);

    /// <summary>Port of IsNewerVersion (version.go:216-226): unparsable versions are not newer.</summary>
    internal static bool IsNewerVersion(string current, string latest)
    {
        try
        {
            return CompareVersions(latest, current) > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Port of CompareVersions (version.go:228-242).</summary>
    internal static int CompareVersions(string a, string b)
    {
        if (!TryParseVersion(a, out var versionA))
        {
            throw new FormatException(
                "failed to parse version \"" + a + "\": Invalid Semantic Version");
        }

        if (!TryParseVersion(b, out var versionB))
        {
            throw new FormatException(
                "failed to parse version \"" + b + "\": Invalid Semantic Version");
        }

        return CompareSemVersions(versionA, versionB);
    }

    /// <summary>Port of compareVersions (version.go:246-262).</summary>
    private static int CompareSemVersions(SemVersion a, SemVersion b)
    {
        if (CompareUInt64(a.Major, b.Major) is var majorResult && majorResult != 0)
        {
            return majorResult;
        }

        if (CompareUInt64(a.Minor, b.Minor) is var minorResult && minorResult != 0)
        {
            return minorResult;
        }

        if (CompareUInt64(a.Patch, b.Patch) is var patchResult && patchResult != 0)
        {
            return patchResult;
        }

        return ComparePrerelease(a.Prerelease, b.Prerelease);
    }

    /// <summary>
    /// Port of comparePrerelease (version.go:264-287): the SemVer precedence
    /// rules with each identifier split into digit and non-digit runs, so
    /// "beta10" compares numerically against "beta9".
    /// </summary>
    private static int ComparePrerelease(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return 1;
        }

        if (b.Length == 0)
        {
            return -1;
        }

        var tokensA = PrereleaseTokens(a);
        var tokensB = PrereleaseTokens(b);

        for (var i = 0; i < tokensA.Count && i < tokensB.Count; i++)
        {
            var result = ComparePrereleaseToken(tokensA[i], tokensB[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return CompareInt(tokensA.Count, tokensB.Count);
    }

    /// <summary>Port of prereleaseTokens (version.go:289-299).</summary>
    private static List<string> PrereleaseTokens(string value)
    {
        var tokens = new List<string>();
        foreach (var identifier in value.Split('.'))
        {
            tokens.AddRange(SplitDigitRuns(identifier));
        }

        return tokens;
    }

    /// <summary>Port of comparePrereleaseToken (version.go:301-317).</summary>
    private static int ComparePrereleaseToken(string a, string b)
    {
        var numericA = IsDigitRun(a);
        var numericB = IsDigitRun(b);

        if (numericA && numericB)
        {
            return CompareNumericRuns(a, b);
        }

        if (numericA != numericB)
        {
            // Numeric identifiers always have lower precedence.
            return numericA ? -1 : 1;
        }

        return string.CompareOrdinal(a, b);
    }

    /// <summary>Port of splitDigitRuns (version.go:319-349).</summary>
    private static List<string> SplitDigitRuns(string value)
    {
        if (value.Length == 0)
        {
            return [];
        }

        var runs = new List<string>();
        var start = 0;
        var digits = IsAsciiDigit(value[0]);

        for (var i = 1; i < value.Length; i++)
        {
            if (IsAsciiDigit(value[i]) == digits)
            {
                continue;
            }

            runs.Add(value[start..i]);
            start = i;
            digits = IsAsciiDigit(value[i]);
        }

        runs.Add(value[start..]);
        return runs;
    }

    /// <summary>Port of compareNumericRuns (version.go:351-361).</summary>
    private static int CompareNumericRuns(string a, string b)
    {
        var trimmedA = a.TrimStart('0');
        var trimmedB = b.TrimStart('0');

        var lengthResult = CompareInt(trimmedA.Length, trimmedB.Length);
        if (lengthResult != 0)
        {
            return lengthResult;
        }

        return string.CompareOrdinal(trimmedA, trimmedB);
    }

    private static bool IsDigitRun(string value)
        => value.Length > 0 && IsAsciiDigit(value[0]);

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static int CompareInt(int a, int b)
    {
        if (a < b)
        {
            return -1;
        }

        return a > b ? 1 : 0;
    }

    private static int CompareUInt64(ulong a, ulong b)
    {
        if (a < b)
        {
            return -1;
        }

        return a > b ? 1 : 0;
    }

    /// <summary>
    /// Port of ParseVersion (version.go:244-253) / Masterminds NewVersion:
    /// an optional lowercase "v", one to three numeric parts, an optional
    /// prerelease and ignored (but validated) build metadata.
    /// </summary>
    internal static bool TryParseVersion(string version, out SemVersion parsed)
    {
        parsed = default;

        var value = version.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value[0] == 'v')
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            // Build metadata is ignored by the comparison but its shape is
            // still validated by the reference regexp.
            var metadata = value[(plus + 1)..];
            value = value[..plus];
            if (!IsValidIdentifierList(metadata))
            {
                return false;
            }
        }

        var prerelease = string.Empty;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (!IsValidIdentifierList(prerelease))
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length is 0 or > 3)
        {
            return false;
        }

        var numbers = new ulong[3];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!IsAsciiDigit(c))
                {
                    return false;
                }
            }

            if (!ulong.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        parsed = new SemVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    private static bool IsValidIdentifierList(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0)
            {
                return false;
            }

            foreach (var c in identifier)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static GitHubRelease ToRelease(GitHubReleaseJson stored) => new(
        stored.TagName ?? string.Empty,
        stored.Prerelease ?? false,
        stored.Draft ?? false,
        stored.PublishedAt ?? default);

    private static DateTimeOffset CaptureProcessStart()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or NotSupportedException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException)
        {
            return DateTimeOffset.Now;
        }
    }

    /// <summary>
    /// runtime.GOOS/GOARCH vocabulary (internal/build/info.go:19) rebuilt from
    /// the host platform (C235).
    /// </summary>
    private static string DetectPlatform()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsFreeBSD() ? "freebsd"
            : "unknown";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.X86 => "386",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.Wasm => "wasm",
            Architecture.S390x => "s390x",
            Architecture.LoongArch64 => "loong64",
            Architecture.Ppc64le => "ppc64le",
            Architecture.RiscV64 => "riscv64",
            _ => "unknown",
        };

        return os + "/" + arch;
    }

    /// <summary>
    /// Port of biz.GitHubRelease (version.go:65-70): <c>published_at</c> may be
    /// absent or null, which Go decodes as the zero time.
    /// </summary>
    internal sealed record GitHubRelease(
        string TagName,
        bool Prerelease,
        bool Draft,
        DateTimeOffset PublishedAt);

    internal readonly record struct SemVersion(
        ulong Major,
        ulong Minor,
        ulong Patch,
        string Prerelease);

    private sealed class GitHubReleaseJson
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("prerelease")]
        public bool? Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool? Draft { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }
    }
}

/// <summary>
/// Port of Go's <c>time.Duration.String()</c>, used by the
/// <c>systemVersion.uptime</c> field. The value itself is process specific
/// (C235); the layout is the reference layout.
/// </summary>
internal static class GoDuration
{
    public static string Format(TimeSpan duration)
    {
        Int128 nanoseconds = (Int128)duration.Ticks * 100;
        var negative = nanoseconds < 0;
        var u = (ulong)(negative ? -nanoseconds : nanoseconds);

        var text = FormatPositive(u);
        return negative ? "-" + text : text;
    }

    private static string FormatPositive(ulong nanoseconds)
    {
        const ulong microsecond = 1_000;
        const ulong millisecond = 1_000_000;
        const ulong second = 1_000_000_000;

        if (nanoseconds == 0)
        {
            return "0s";
        }

        if (nanoseconds < second)
        {
            // Special case: durations smaller than a second use ns/µs/ms.
            ulong whole;
            int precision;
            string unit;

            if (nanoseconds < microsecond)
            {
                whole = nanoseconds;
                precision = 0;
                unit = "ns";
            }
            else if (nanoseconds < millisecond)
            {
                whole = nanoseconds / microsecond;
                precision = 3;
                unit = "µs";
            }
            else
            {
                whole = nanoseconds / millisecond;
                precision = 6;
                unit = "ms";
            }

            var fraction = FormatFraction(nanoseconds % Scale(precision), precision);
            return whole.ToString(CultureInfo.InvariantCulture) + fraction + unit;
        }

        var fractionOfSecond = FormatFraction(nanoseconds % second, 9);
        var secondsTotal = nanoseconds / second;
        var seconds = secondsTotal % 60;
        var minutesTotal = secondsTotal / 60;
        var minutes = minutesTotal % 60;
        var hours = minutesTotal / 60;

        var builder = new StringBuilder();
        if (hours > 0)
        {
            builder.Append(hours.ToString(CultureInfo.InvariantCulture)).Append('h');
        }

        if (hours > 0 || minutes > 0)
        {
            builder.Append(minutes.ToString(CultureInfo.InvariantCulture)).Append('m');
        }

        builder.Append(seconds.ToString(CultureInfo.InvariantCulture));
        builder.Append(fractionOfSecond);
        builder.Append('s');
        return builder.ToString();
    }

    /// <summary>10^digits for the three small units.</summary>
    private static ulong Scale(int precision) => precision switch
    {
        3 => 1_000,
        6 => 1_000_000,
        _ => 1,
    };

    /// <summary>
    /// Go fmtFrac: <paramref name="digits"/> decimal places of
    /// <paramref name="value"/> with trailing zeros omitted, including the
    /// decimal point when the fraction is zero.
    /// </summary>
    private static string FormatFraction(ulong value, int digits)
    {
        if (value == 0)
        {
            return string.Empty;
        }

        var text = value.ToString(CultureInfo.InvariantCulture)
            .PadLeft(digits, '0')
            .TrimEnd('0');
        return "." + text;
    }
}
