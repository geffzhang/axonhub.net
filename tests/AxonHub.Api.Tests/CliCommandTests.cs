using System.Text.Json;
using AxonHub.Api.Cli;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// C299: the argv dispatch, command texts and exit codes of
/// cmd/axonhub/main.go:33-52,196-380.
/// </summary>
public sealed class CliCommandTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    [InlineData("-v")]
    public void VersionPrintsTheBuildVersion(string command)
    {
        var (code, stdout, stderr) = Run(command);

        Assert.Equal(0, code);
        Assert.Equal("v1.0.0-beta9\n", stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void BuildInfoPrintsTheReferenceStringShape()
    {
        var (code, stdout, stderr) = Run("build-info");

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr);
        Assert.StartsWith("Version: v1.0.0-beta9\n", stdout, StringComparison.Ordinal);
        Assert.Contains("\nGo Version: ", stdout, StringComparison.Ordinal);
        Assert.Contains("\nPlatform: ", stdout, StringComparison.Ordinal);
        Assert.Contains("\nUptime: ", stdout, StringComparison.Ordinal);
        // fmt.Println(Info.String()) adds a newline after the string's own.
        Assert.EndsWith("\n\n", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpPrintsTheReferenceUsage(string command)
    {
        var (code, stdout, stderr) = Run(command);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr);
        Assert.StartsWith("AxonHub AI Gateway\n\nUsage:\n", stdout, StringComparison.Ordinal);
        Assert.Contains("  axonhub reload             Send SIGHUP", stdout, StringComparison.Ordinal);
        Assert.Contains(
            "  -f, --format FORMAT       Output format for config preview (yml, json)\n",
            stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCommandFallsThroughToTheServer()
    {
        var (code, stdout, stderr) = Run("not-a-command");

        Assert.Null(code);
        Assert.Equal(string.Empty, stdout);
        Assert.Equal(string.Empty, stderr);
    }

    [Fact]
    public void ConfigWithoutASubcommandPrintsUsage()
    {
        var (code, stdout, _) = Run("config");

        Assert.Equal(1, code);
        Assert.Equal("Usage: axonhub config <preview|validate|get>\n", stdout);
    }

    [Fact]
    public void UnknownConfigSubcommandPrintsUsage()
    {
        var (code, stdout, _) = Run("config", "unknown");

        Assert.Equal(1, code);
        Assert.Equal("Usage: axonhub config <preview|validate|get>\n", stdout);
    }

    [Fact]
    public void PreviewJsonRendersTheConfDefaults()
    {
        var stdout = new StringWriter();

        var code = CliCommand.RunPreview(EmptyConfiguration(), "json", stdout);

        Assert.Equal(0, code);
        using var document = JsonDocument.Parse(stdout.ToString());
        var root = document.RootElement;
        var server = root.GetProperty("server");
        Assert.Equal("0.0.0.0", server.GetProperty("host").GetString());
        Assert.Equal(8090, server.GetProperty("port").GetInt32());
        Assert.Equal("AxonHub", server.GetProperty("name").GetString());
        Assert.Equal(30000000000L, server.GetProperty("request_timeout").GetInt64());
        Assert.Equal(600000000000L, server.GetProperty("llm_request_timeout").GetInt64());
        Assert.Equal(33554432L, server.GetProperty("max_multipart_memory").GetInt64());
        Assert.False(server.GetProperty("sse_keep_alive").GetProperty("enabled").GetBoolean());
        Assert.Equal(15000000000L, server.GetProperty("sse_keep_alive").GetProperty("interval").GetInt64());
        Assert.Equal(
            1800000000000L,
            server.GetProperty("cors").GetProperty("max_age").GetInt64());
        Assert.Equal("postgres", root.GetProperty("db").GetProperty("dialect").GetString());
        Assert.Equal("axonhub", root.GetProperty("log").GetProperty("name").GetString());
        Assert.Equal("json", root.GetProperty("log").GetProperty("encoding").GetString());
    }

    [Fact]
    public void PreviewYamlRendersMappingsAndSequences()
    {
        var stdout = new StringWriter();

        var code = CliCommand.RunPreview(EmptyConfiguration(), "yml", stdout);

        Assert.Equal(0, code);
        var text = stdout.ToString();
        Assert.StartsWith("db:\n    dialect: \"postgres\"\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "server:\n    host: \"0.0.0.0\"\n    port: 8090\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "    cors:\n        enabled: false\n        allowed_origins:\n"
            + "            - \"http://localhost:8090\"\n",
            text,
            StringComparison.Ordinal);
        Assert.EndsWith("    max_multipart_memory: 33554432\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewSupportsTheYamlAlias()
    {
        var stdout = new StringWriter();

        var code = CliCommand.RunPreview(EmptyConfiguration(), "yaml", stdout);

        Assert.Equal(0, code);
        Assert.Contains("port: 8090", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRejectsAnUnsupportedFormat()
    {
        var (code, stdout, _) = Run("config", "preview", "--format", "xml");

        Assert.Equal(1, code);
        Assert.Equal("Unsupported format: xml\n", stdout);
    }

    [Fact]
    public void ValidateAcceptsAValidConfiguration()
    {
        var configuration = NewConfiguration(
            ("ConnectionStrings:Default", "Host=localhost"),
            ("Server:Port", "8090"),
            ("Log:Name", "axonhub"));
        var stdout = new StringWriter();

        var code = CliCommand.RunValidate(configuration, stdout);

        Assert.Equal(0, code);
        Assert.Equal("Configuration is valid!\n", stdout.ToString());
    }

    [Fact]
    public void ValidateReportsAllFourReferenceRules()
    {
        var configuration = NewConfiguration(
            ("Server:Port", "70000"),
            ("Log:Name", string.Empty),
            ("Server:Cors:Enabled", "true"),
            ("Server:Cors:AllowedOrigins", string.Empty));
        var stdout = new StringWriter();

        var code = CliCommand.RunValidate(configuration, stdout);

        Assert.Equal(1, code);
        Assert.Equal(
            "Configuration validation failed:\n"
            + "  - server.port must be between 1 and 65535\n"
            + "  - db.dsn cannot be empty\n"
            + "  - log.name cannot be empty\n"
            + "  - server.cors.allowed_origins cannot be empty when CORS is enabled\n",
            stdout.ToString());
    }

    [Fact]
    public void ValidateReportsMalformedValuesAsLoadFailures()
    {
        var configuration = NewConfiguration(
            ("ConnectionStrings:Default", "Host=localhost"),
            ("Server:RequestTimeout", "not-a-duration"));
        var stdout = new StringWriter();

        var code = CliCommand.RunValidate(configuration, stdout);

        Assert.Equal(1, code);
        Assert.StartsWith("Failed to load config: time: invalid duration", stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server.port", "8090")]
    [InlineData("server.name", "AxonHub")]
    [InlineData("server.base_path", "")]
    [InlineData("server.debug", "false")]
    [InlineData("db.dialect", "postgres")]
    [InlineData("db.dsn", "Host=localhost")]
    public void GetPrintsTheReferenceKeys(string key, string expected)
    {
        var configuration = NewConfiguration(
            ("ConnectionStrings:Default", "Host=localhost"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = CliCommand.RunGet(configuration, key, stdout, stderr);

        Assert.Equal(0, code);
        Assert.Equal(expected + "\n", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void GetRejectsAnUnknownKey()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = CliCommand.RunGet(
            NewConfiguration(),
            "server.unknown",
            stdout,
            stderr);

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal("Unknown config key: server.unknown\n", stderr.ToString());
    }

    [Fact]
    public void GetWithoutAKeyPrintsTheKeyList()
    {
        var (code, stdout, _) = Run("config", "get");

        Assert.Equal(1, code);
        Assert.Equal(
            "Usage: axonhub config get <key>\n"
            + "\n"
            + "Available keys:\n"
            + "  server.port    Server port number\n"
            + "  server.name    Server name\n"
            + "  db.dialect     Database dialect\n"
            + "  db.dsn         Database DSN\n",
            stdout);
    }

    [Fact]
    public void ReloadWithoutPidFileReportsTheReferenceError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = CliCommand.RunReload(
            NewConfiguration(),
            stdout,
            stderr,
            _ => Assert.Fail("no signal should be sent"));

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal("Reload failed: pid_file not configured\n", stderr.ToString());
    }

    [Fact]
    public void ReloadExpandsHomeAndSendsTheSignal()
    {
        string? signalled = null;
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = CliCommand.RunReload(
            NewConfiguration(("Server:PidFile", "~/axonhub.pid")),
            stdout,
            stderr,
            path => signalled = path);

        Assert.Equal(0, code);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "axonhub.pid"),
            signalled);
        Assert.Equal("Config reload signal sent successfully\n", stdout.ToString());
        Assert.Equal(string.Empty, stderr.ToString());
    }

    [Fact]
    public void ReloadReportsSignalFailures()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = CliCommand.RunReload(
            NewConfiguration(("Server:PidFile", "/tmp/axonhub.pid")),
            stdout,
            stderr,
            _ => throw new InvalidOperationException("send SIGHUP to process 42: denied"));

        Assert.Equal(1, code);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal(
            "Reload failed: send SIGHUP to process 42: denied\n",
            stderr.ToString());
    }

    [Theory]
    [InlineData("32M", 33554432L)]
    [InlineData("32MB", 33554432L)]
    [InlineData("1G", 1073741824L)]
    [InlineData("1.5K", 1536L)]
    [InlineData("536870912", 536870912L)]
    [InlineData("", 0L)]
    public void MemorySizeParsingMatchesTheReference(string text, long expected)
    {
        Assert.Equal(expected, CliConfiguration.ParseMemorySize(text));
    }

    [Theory]
    [InlineData("-1", "memory size must be non-negative: \"-1\"")]
    [InlineData("-1K", "memory size must be non-negative: \"-1K\"")]
    [InlineData("12X", "unknown memory size suffix: \"12X\" (use K, M, or G)")]
    [InlineData("M", "invalid memory size: \"M\"")]
    [InlineData("abcM", "invalid memory size number: \"abcM\"")]
    // C305: Go's ParseFloat accepts digit separators; .NET does not.
    [InlineData("1_0M", "invalid memory size number: \"1_0M\"")]
    public void MemorySizeParsingReportsTheReferenceErrors(string text, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => CliConfiguration.ParseMemorySize(text));

        Assert.Equal(expected, error.Message);
    }

    private static (int? Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = CliCommand.TryRun(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static IConfiguration EmptyConfiguration() => NewConfiguration();

    private static IConfiguration NewConfiguration(
        params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                values.Select(
                    pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
