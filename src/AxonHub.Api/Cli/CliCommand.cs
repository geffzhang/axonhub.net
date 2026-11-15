using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Http;
using AxonHub.Api.Lifecycle;
using AxonHub.Api.Settings;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Cli;

/// <summary>
/// Port of the reference's argv dispatch (cmd/axonhub/main.go:33-52). Returns
/// null when the arguments do not select a command, in which case the host
/// starts normally.
/// </summary>
public static class CliCommand
{
    private const string ConfigUsage = "Usage: axonhub config <preview|validate|get>";

    private static readonly JsonSerializerOptions PreviewJson = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Runs a CLI command and returns its exit code, or null to start the
    /// server. <paramref name="args"/> excludes the executable name.
    /// </summary>
    public static int? TryRun(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Length == 0)
        {
            return null;
        }

        return args[0] switch
        {
            "reload" => RunReload(stdout, stderr),
            "config" => RunConfig(args, stdout, stderr),
            "admission" => AdmissionCommand.Run(args, stdout, stderr),
            "version" or "--version" or "-v" => RunVersion(stdout),
            "help" or "--help" or "-h" => RunHelp(stdout),
            "build-info" => RunBuildInfo(stdout),
            _ => null,
        };
    }

    internal static int RunVersion(TextWriter stdout)
    {
        WriteLine(stdout, SystemVersionService.Version);
        return 0;
    }

    internal static int RunBuildInfo(TextWriter stdout)
    {
        var info = new SystemVersionService(TimeProvider.System).GetBuildInfo();

        // main.go:55: fmt.Println(build.GetBuildInfo()); String() already ends
        // with a newline, so the command prints a trailing blank line.
        stdout.Write(FormatBuildInfo(info));
        stdout.Write('\n');
        return 0;
    }

    /// <summary>Port of build.Info.String (internal/build/info.go:57-76).</summary>
    internal static string FormatBuildInfo(SystemBuildInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var builder = new StringBuilder();
        builder.Append("Version: ").Append(info.Version).Append('\n');
        if (info.Commit.Length > 0)
        {
            builder.Append("Commit: ").Append(info.Commit).Append('\n');
        }

        if (info.BuildTime.Length > 0)
        {
            builder.Append("Build Time: ").Append(info.BuildTime).Append('\n');
        }

        builder.Append("Go Version: ").Append(info.GoVersion).Append('\n');
        builder.Append("Platform: ").Append(info.Platform).Append('\n');
        builder.Append("Uptime: ").Append(info.Uptime).Append('\n');
        return builder.ToString();
    }

    internal static int RunHelp(TextWriter stdout)
    {
        WriteLine(stdout, "AxonHub AI Gateway");
        WriteLine(stdout, string.Empty);
        WriteLine(stdout, "Usage:");
        WriteLine(stdout, "  axonhub                    Start the server (default)");
        WriteLine(stdout, "  axonhub reload             Send SIGHUP to reload configuration");
        WriteLine(stdout, "  axonhub config preview     Preview configuration");
        WriteLine(stdout, "  axonhub config validate    Validate configuration");
        WriteLine(stdout, "  axonhub config get <key>   Get a specific config value");
        WriteLine(stdout, "  axonhub admission list     Inspect durable concurrency permits");
        WriteLine(stdout, "  axonhub admission release <id> --confirm-upstream-stopped");
        WriteLine(stdout, "  axonhub version            Show version");
        WriteLine(stdout, "  axonhub help               Show this help message");
        WriteLine(stdout, string.Empty);
        WriteLine(stdout, "Options:");
        WriteLine(stdout, "  -f, --format FORMAT       Output format for config preview (yml, json)");
        return 0;
    }

    private static int RunReload(TextWriter stdout, TextWriter stderr)
    {
        IConfigurationRoot configuration;
        try
        {
            configuration = CliConfiguration.Load();
        }
        catch (Exception ex)
        {
            WriteLine(stderr, "Reload failed: load config: " + ex.Message);
            return 1;
        }

        return RunReload(configuration, stdout, stderr, PidFile.SendReloadSignal);
    }

    /// <summary>Port of handleReload (main.go:196-211).</summary>
    internal static int RunReload(
        IConfiguration configuration,
        TextWriter stdout,
        TextWriter stderr,
        Action<string> sendSignal)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        ArgumentNullException.ThrowIfNull(sendSignal);

        var pidFile = PidFile.FromConfiguration(configuration);
        if (pidFile.Length == 0)
        {
            WriteLine(stderr, "Reload failed: pid_file not configured");
            return 1;
        }

        try
        {
            sendSignal(PidFile.ExpandHome(pidFile));
        }
        catch (Exception ex)
        {
            WriteLine(stderr, "Reload failed: " + ex.Message);
            return 1;
        }

        WriteLine(stdout, "Config reload signal sent successfully");
        return 0;
    }

    private static int RunConfig(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 2)
        {
            WriteLine(stdout, ConfigUsage);
            return 1;
        }

        switch (args[1])
        {
            case "preview":
                return RunPreview(args, stdout);
            case "validate":
                return RunValidate(stdout);
            case "get":
                return RunGet(args, stdout, stderr);
            default:
                WriteLine(stdout, ConfigUsage);
                return 1;
        }
    }

    private static int RunPreview(string[] args, TextWriter stdout)
    {
        var format = "yml";
        for (var index = 2; index < args.Length; index++)
        {
            if (args[index] is "--format" or "-f" && index + 1 < args.Length)
            {
                format = args[index + 1];
            }
        }

        IConfigurationRoot configuration;
        try
        {
            configuration = CliConfiguration.Load();
        }
        catch (Exception ex)
        {
            WriteLine(stdout, "Failed to load config: " + ex.Message);
            return 1;
        }

        return RunPreview(configuration, format, stdout);
    }

    /// <summary>Port of configPreview (main.go:243-283).</summary>
    internal static int RunPreview(IConfiguration configuration, string format, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(stdout);

        IReadOnlyDictionary<string, object?> preview;
        try
        {
            // conf.Load decodes every section before the format switch.
            preview = CliConfiguration.BuildPreview(configuration);
        }
        catch (Exception ex)
        {
            WriteLine(stdout, "Failed to load config: " + ex.Message);
            return 1;
        }

        try
        {
            switch (format)
            {
                case "json":
                    WriteLine(stdout, JsonSerializer.Serialize(preview, PreviewJson));
                    return 0;
                case "yml":
                case "yaml":
                    // yaml.Marshal ends with a newline and fmt.Println adds
                    // another, so the reference prints a trailing blank line.
                    stdout.Write(RenderYaml(preview));
                    stdout.Write('\n');
                    return 0;
                default:
                    WriteLine(stdout, "Unsupported format: " + format);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            WriteLine(stdout, "Failed to preview config: " + ex.Message);
            return 1;
        }
    }

    private static int RunValidate(TextWriter stdout)
    {
        IConfigurationRoot configuration;
        try
        {
            configuration = CliConfiguration.Load();
        }
        catch (Exception ex)
        {
            WriteLine(stdout, "Failed to load config: " + ex.Message);
            return 1;
        }

        return RunValidate(configuration, stdout);
    }

    /// <summary>Port of configValidate (main.go:285-307).</summary>
    internal static int RunValidate(IConfiguration configuration, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stdout);

        IReadOnlyList<string> errors;
        try
        {
            errors = Validate(configuration);
        }
        catch (Exception ex)
        {
            // conf.Load decodes every section before validation; a decode
            // failure is reported as a load failure.
            WriteLine(stdout, "Failed to load config: " + ex.Message);
            return 1;
        }

        if (errors.Count == 0)
        {
            WriteLine(stdout, "Configuration is valid!");
            return 0;
        }

        WriteLine(stdout, "Configuration validation failed:");
        foreach (var error in errors)
        {
            WriteLine(stdout, "  - " + error);
        }

        return 1;
    }

    /// <summary>Port of validateConfig (main.go:309-330).</summary>
    internal static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Decodes the same sections as conf.Load so malformed durations or
        // memory sizes fail before the rules run.
        _ = CliConfiguration.BuildPreview(configuration);

        var errors = new List<string>();

        var port = CliConfiguration.GetPort(configuration);
        if (port <= 0 || port > 65535)
        {
            errors.Add("server.port must be between 1 and 65535");
        }

        if (CliConfiguration.GetDsn(configuration).Length == 0)
        {
            errors.Add("db.dsn cannot be empty");
        }

        if (CliConfiguration.GetLogName(configuration).Length == 0)
        {
            errors.Add("log.name cannot be empty");
        }

        var cors = CorsSettings.FromConfiguration(configuration);
        if (cors.Enabled && cors.AllowedOrigins.Count == 0)
        {
            errors.Add(
                "server.cors.allowed_origins cannot be empty when CORS is enabled");
        }

        return errors;
    }

    private static int RunGet(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 3)
        {
            WriteLine(stdout, "Usage: axonhub config get <key>");
            WriteLine(stdout, string.Empty);
            WriteLine(stdout, "Available keys:");
            WriteLine(stdout, "  server.port    Server port number");
            WriteLine(stdout, "  server.name    Server name");
            WriteLine(stdout, "  db.dialect     Database dialect");
            WriteLine(stdout, "  db.dsn         Database DSN");
            return 1;
        }

        IConfigurationRoot configuration;
        try
        {
            configuration = CliConfiguration.Load();
        }
        catch (Exception ex)
        {
            WriteLine(stderr, "Failed to load config: " + ex.Message);
            return 1;
        }

        return RunGet(configuration, args[2], stdout, stderr);
    }

    /// <summary>Port of configGet (main.go:332-377).</summary>
    internal static int RunGet(
        IConfiguration configuration,
        string key,
        TextWriter stdout,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string? value = key switch
        {
            "server.port" => CliConfiguration.GetPort(configuration)
                .ToString(CultureInfo.InvariantCulture),
            "server.name" => CliConfiguration.GetName(configuration),
            "server.base_path" => CliConfiguration.GetBasePath(configuration),
            "server.debug" => CliConfiguration.GetDebug(configuration) ? "true" : "false",
            "db.dialect" => CliConfiguration.GetDialect(configuration),
            "db.dsn" => CliConfiguration.GetDsn(configuration),
            _ => null,
        };

        if (value is null)
        {
            WriteLine(stderr, "Unknown config key: " + key);
            return 1;
        }

        WriteLine(stdout, value);
        return 0;
    }

    private static string RenderYaml(IReadOnlyDictionary<string, object?> preview)
    {
        var builder = new StringBuilder();
        WriteYamlMapping(builder, preview, 0);
        return builder.ToString();
    }

    private static void WriteYamlMapping(
        StringBuilder builder,
        IReadOnlyDictionary<string, object?> mapping,
        int indent)
    {
        var prefix = new string(' ', indent);
        foreach (var pair in mapping)
        {
            if (pair.Value is IReadOnlyDictionary<string, object?> nested)
            {
                builder.Append(prefix).Append(pair.Key).Append(":\n");
                WriteYamlMapping(builder, nested, indent + 4);
            }
            else if (pair.Value is IEnumerable<string> list)
            {
                WriteYamlList(builder, pair.Key, list, indent);
            }
            else
            {
                builder.Append(prefix)
                    .Append(pair.Key)
                    .Append(": ")
                    .Append(YamlScalar(pair.Value))
                    .Append('\n');
            }
        }
    }

    private static void WriteYamlList(
        StringBuilder builder,
        string key,
        IEnumerable<string> values,
        int indent)
    {
        var list = values as IReadOnlyList<string> ?? values.ToList();
        var prefix = new string(' ', indent);
        if (list.Count == 0)
        {
            builder.Append(prefix).Append(key).Append(": []\n");
            return;
        }

        builder.Append(prefix).Append(key).Append(":\n");
        foreach (var value in list)
        {
            builder.Append(prefix)
                .Append("    - ")
                .Append(YamlScalar(value))
                .Append('\n');
        }
    }

    private static string YamlScalar(object? value) => value switch
    {
        null => "null",
        bool boolean => boolean ? "true" : "false",
        int integer => integer.ToString(CultureInfo.InvariantCulture),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        string text => YamlString(text),
        _ => YamlString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    private static string YamlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        builder.Append("\\u")
                            .Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>fmt.Println writes a bare newline on every platform.</summary>
    private static void WriteLine(TextWriter writer, string text) => writer.Write(text + "\n");
}
