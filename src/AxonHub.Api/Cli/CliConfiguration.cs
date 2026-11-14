using System.Globalization;
using AxonHub.Api.Http;
using AxonHub.Api.Lifecycle;
using AxonHub.Api.Security;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Cli;

/// <summary>
/// One-shot configuration view used by the CLI subcommands. The reference's
/// <c>conf.Load</c> reads config.yml plus <c>AXONHUB_*</c> environment
/// variables through Viper; the port reads the ported server/db/log keys
/// through <see cref="IConfiguration"/> (appsettings.json when present, then
/// the environment).
/// </summary>
public static class CliConfiguration
{
    /// <summary>conf.Load (conf/conf.go:184-187) for commands outside the host.</summary>
    public static IConfigurationRoot Load()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrEmpty(environment))
        {
            environment = "Production";
        }

        return new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile(
                "appsettings." + environment + ".json",
                optional: true,
                reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();
    }

    /// <summary>
    /// conf/conf.go defaults plus the reference's automatic environment
    /// spelling. A present-but-empty value stays empty, like Viper's env
    /// override.
    /// </summary>
    internal static string GetString(
        IConfiguration configuration,
        string key,
        string environmentKey,
        string fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            value = configuration[environmentKey];
        }

        return value ?? fallback;
    }

    /// <summary>server.port (conf/conf.go:237, default 8090).</summary>
    public static int GetPort(IConfiguration configuration)
        => int.TryParse(
            GetString(configuration, "Server:Port", "AXONHUB_SERVER_PORT", "8090"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var port)
            ? port
            : 0;

    /// <summary>server.name (conf/conf.go:241).</summary>
    public static string GetName(IConfiguration configuration)
        => GetString(configuration, "Server:Name", "AXONHUB_SERVER_NAME", "AxonHub");

    /// <summary>server.base_path (conf/conf.go:242): read-only, never a route prefix.</summary>
    public static string GetBasePath(IConfiguration configuration)
        => GetString(configuration, "Server:BasePath", "AXONHUB_SERVER_BASE_PATH", string.Empty);

    /// <summary>server.debug (conf/conf.go:270).</summary>
    public static bool GetDebug(IConfiguration configuration)
        => bool.TryParse(
            GetString(configuration, "Server:Debug", "AXONHUB_SERVER_DEBUG", "false"),
            out var debug)
            && debug;

    /// <summary>
    /// server.max_multipart_memory (conf/conf.go:275, default 32M). The same
    /// MemorySize decoder as the reference; an empty value is 0, which leaves
    /// gin's 32 MiB default in place when the server is built.
    /// </summary>
    public static long GetMaxMultipartMemory(IConfiguration configuration)
        => ParseMemorySize(
            GetString(
                configuration,
                "Server:MaxMultipartMemory",
                "AXONHUB_SERVER_MAX_MULTIPART_MEMORY",
                "32M"));

    /// <summary>
    /// db.dialect (conf/conf.go:290): the port's only dialect is PostgreSQL.
    /// </summary>
    public static string GetDialect(IConfiguration configuration)
        => GetString(configuration, "DB:Dialect", "AXONHUB_DB_DIALECT", "postgres");

    /// <summary>
    /// db.dsn (conf/conf.go:291): the port's database is the PostgreSQL
    /// <c>ConnectionStrings:Default</c>, which is the effective DSN when the
    /// reference spelling is absent.
    /// </summary>
    public static string GetDsn(IConfiguration configuration)
    {
        var dsn = GetString(configuration, "DB:DSN", "AXONHUB_DB_DSN", string.Empty);
        return dsn.Length > 0
            ? dsn
            : configuration.GetConnectionString("Default") ?? string.Empty;
    }

    /// <summary>log.name (conf/conf.go:304).</summary>
    public static string GetLogName(IConfiguration configuration)
        => GetString(configuration, "Log:Name", "AXONHUB_LOG_NAME", "axonhub");

    /// <summary>log.level (conf/conf.go:307).</summary>
    public static string GetLogLevel(IConfiguration configuration)
        => GetString(configuration, "Log:Level", "AXONHUB_LOG_LEVEL", "info");

    /// <summary>log.debug (conf/conf.go:305).</summary>
    public static bool GetLogDebug(IConfiguration configuration)
        => bool.TryParse(
            GetString(configuration, "Log:Debug", "AXONHUB_LOG_DEBUG", "false"),
            out var debug)
            && debug;

    /// <summary>log.encoding (conf/conf.go:313).</summary>
    public static string GetLogEncoding(IConfiguration configuration)
        => GetString(configuration, "Log:Encoding", "AXONHUB_LOG_ENCODING", "json");

    /// <summary>log.output (conf/conf.go:316).</summary>
    public static string GetLogOutput(IConfiguration configuration)
        => GetString(configuration, "Log:Output", "AXONHUB_LOG_OUTPUT", "stdio");

    /// <summary>
    /// The ported configuration surface rendered by <c>config preview</c>.
    /// Values use the same defaults as conf/conf.go and the same YAML/JSON
    /// encoding as the reference (durations are int64 nanoseconds, memory
    /// sizes are int64 bytes).
    /// </summary>
    public static IReadOnlyDictionary<string, object?> BuildPreview(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var timeouts = RequestTimeoutSettings.FromConfiguration(configuration);
        var cors = CorsSettings.FromConfiguration(configuration);
        var ipAccessControl = IpAccessControlSettings.FromConfiguration(configuration);
        var sseKeepAlive = SseKeepAliveSettings.FromConfiguration(configuration);

        return new Dictionary<string, object?>
        {
            ["db"] = new Dictionary<string, object?>
            {
                ["dialect"] = GetDialect(configuration),
                ["dsn"] = GetDsn(configuration),
            },
            ["log"] = new Dictionary<string, object?>
            {
                ["name"] = GetLogName(configuration),
                ["debug"] = GetLogDebug(configuration),
                ["level"] = GetLogLevel(configuration),
                ["encoding"] = GetLogEncoding(configuration),
                ["output"] = GetLogOutput(configuration),
            },
            ["server"] = new Dictionary<string, object?>
            {
                ["host"] = GetString(configuration, "Server:Host", "AXONHUB_SERVER_HOST", "0.0.0.0"),
                ["port"] = GetPort(configuration),
                ["public_url"] = GetString(
                    configuration,
                    "Server:PublicUrl",
                    "AXONHUB_SERVER_PUBLIC_URL",
                    string.Empty),
                ["trusted_proxies"] = GetList(
                    configuration,
                    "Server:TrustedProxies",
                    "AXONHUB_SERVER_TRUSTED_PROXIES"),
                ["name"] = GetName(configuration),
                ["base_path"] = GetBasePath(configuration),
                ["pid_file"] = PidFile.FromConfiguration(configuration),
                ["request_timeout"] = Nanoseconds(timeouts.RequestTimeout),
                ["llm_request_timeout"] = Nanoseconds(timeouts.LlmRequestTimeout),
                ["sse_keep_alive"] = new Dictionary<string, object?>
                {
                    ["enabled"] = sseKeepAlive.Enabled,
                    ["interval"] = Nanoseconds(sseKeepAlive.Interval),
                },
                ["debug"] = GetDebug(configuration),
                ["cors"] = new Dictionary<string, object?>
                {
                    ["enabled"] = cors.Enabled,
                    ["allowed_origins"] = cors.AllowedOrigins,
                    ["allowed_methods"] = cors.AllowedMethods,
                    ["allowed_headers"] = cors.AllowedHeaders,
                    ["exposed_headers"] = cors.ExposedHeaders,
                    ["allow_credentials"] = cors.AllowCredentials,
                    ["max_age"] = Nanoseconds(cors.MaxAge),
                },
                ["api"] = new Dictionary<string, object?>
                {
                    ["auth"] = new Dictionary<string, object?>
                    {
                        ["allow_no_auth"] = bool.TryParse(
                            GetString(
                                configuration,
                                "Server:Api:Auth:AllowNoAuth",
                                "AXONHUB_SERVER_API_AUTH_ALLOW_NO_AUTH",
                                "false"),
                            out var allowNoAuth) && allowNoAuth,
                        ["key_prefix"] = GetString(
                            configuration,
                            "Server:Api:Auth:KeyPrefix",
                            "AXONHUB_SERVER_API_AUTH_KEY_PREFIX",
                            string.Empty),
                    },
                },
                ["ip_access_control"] = new Dictionary<string, object?>
                {
                    ["enabled"] = ipAccessControl.Enabled,
                    ["allowed_ips"] = ipAccessControl.AllowedIps,
                    ["redirect_url"] = ipAccessControl.RedirectUrl,
                },
                ["max_multipart_memory"] = GetMaxMultipartMemory(configuration),
            },
        };
    }

    /// <summary>
    /// Port of server.MemorySize.UnmarshalText (internal/server/config.go:17-70):
    /// plain int64 bytes or a K/M/G suffix, case-insensitive with an optional
    /// trailing B.
    /// </summary>
    internal static long ParseMemorySize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var value = text.Trim();
        if (value.Length == 0)
        {
            return 0;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
        {
            return plain >= 0
                ? plain
                : throw new InvalidOperationException(
                    "memory size must be non-negative: \"" + text + "\"");
        }

        var upper = value.ToUpperInvariant();
        if (upper.EndsWith('B'))
        {
            upper = upper[..^1];
        }

        if (upper.Length < 2)
        {
            throw new InvalidOperationException("invalid memory size: \"" + text + "\"");
        }

        var suffix = upper[^1];
        var number = upper[..^1];
        if (!double.TryParse(
                number,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
        {
            throw new InvalidOperationException(
                "invalid memory size number: \"" + text + "\"");
        }

        if (parsed < 0)
        {
            throw new InvalidOperationException(
                "memory size must be non-negative: \"" + text + "\"");
        }

        var multiplier = suffix switch
        {
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            _ => throw new InvalidOperationException(
                "unknown memory size suffix: \"" + text + "\" (use K, M, or G)"),
        };

        var scaled = parsed * multiplier;
        // float64(long.MaxValue) rounds to exactly 2^63, so reject >= to also
        // catch sizes that scale to 2^63 (the first unrepresentable int64).
        if (scaled >= (double)long.MaxValue)
        {
            throw new InvalidOperationException("memory size overflows int64: \"" + text + "\"");
        }

        return (long)scaled;
    }

    private static long Nanoseconds(TimeSpan duration) => duration.Ticks * 100;

    private static IReadOnlyList<string> GetList(
        IConfiguration configuration,
        string key,
        string environmentKey)
    {
        var section = configuration.GetSection(key);
        if (section.GetChildren().Any())
        {
            return section.Get<string[]>() ?? [];
        }

        var scalar = configuration[environmentKey] ?? configuration[key];
        if (scalar is null || scalar.Length == 0)
        {
            return [];
        }

        return scalar.Split(
            ',',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
