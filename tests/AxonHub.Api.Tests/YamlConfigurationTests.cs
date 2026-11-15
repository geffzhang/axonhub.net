using AxonHub.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// config.yml compatibility: snake_case reference keys become the
/// Section:Key names the port already reads, lists keep their indexed children
/// and a scalar comma-joined form, and a configuration reload re-reads the file
/// for SIGHUP.
/// </summary>
public sealed class YamlConfigurationTests
{
    [Fact]
    public void ParsesReferenceKeysIntoSections()
    {
        var values = YamlConfigurationParser.Parse(
            """
            server:
              host: "0.0.0.0"
              port: 8090
              request_timeout: "30s"
              sse_keep_alive:
                enabled: true
              trace:
                thread_header: "AH-Thread-Id"
              cors:
                enabled: true
                allowed_origins:
                  - "http://localhost:5173"
                  - "http://localhost:3000"
              api:
                auth:
                  allow_no_auth: false
                  key_prefix: "ah"
            db:
              dialect: postgres
              dsn: "Host=postgres;Database=axonhub"
            gc:
              cron: "0 2 * * *"
            """);

        Assert.Equal("0.0.0.0", values["Server:Host"]);
        Assert.Equal("8090", values["Server:Port"]);
        Assert.Equal("30s", values["Server:RequestTimeout"]);
        Assert.Equal("true", values["Server:SseKeepAlive:Enabled"]);
        Assert.Equal("AH-Thread-Id", values["Server:Trace:ThreadHeader"]);
        Assert.Equal("true", values["Server:Cors:Enabled"]);
        Assert.Equal("http://localhost:5173", values["Server:Cors:AllowedOrigins:0"]);
        Assert.Equal("http://localhost:3000", values["Server:Cors:AllowedOrigins:1"]);
        Assert.Equal(
            "http://localhost:5173,http://localhost:3000",
            values["Server:Cors:AllowedOrigins"]);
        Assert.Equal("false", values["Server:Api:Auth:AllowNoAuth"]);
        Assert.Equal("ah", values["Server:Api:Auth:KeyPrefix"]);
        Assert.Equal("postgres", values["Db:Dialect"]);
        Assert.Equal("Host=postgres;Database=axonhub", values["Db:Dsn"]);
        Assert.Equal("0 2 * * *", values["Gc:Cron"]);
    }

    [Fact]
    public void EmptyListsRemainExplicitlyEmpty()
    {
        var values = YamlConfigurationParser.Parse(
            """
            server:
              trusted_proxies: []
              cors:
                exposed_headers: []
            """);

        Assert.Equal(string.Empty, values["Server:TrustedProxies"]);
        Assert.Equal(string.Empty, values["Server:Cors:ExposedHeaders"]);
        Assert.DoesNotContain(
            values.Keys,
            key => key.StartsWith("Server:TrustedProxies:", StringComparison.Ordinal));
    }

    [Fact]
    public void FindsConfigFileInTheAppOrWorkingDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "axonhub-yaml-find-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var cwd = Path.Combine(root, "cwd");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(cwd);
        try
        {
            Assert.Null(YamlConfigurationFiles.Find(app, cwd));

            var workingYaml = Path.Combine(cwd, "config.yml");
            File.WriteAllText(workingYaml, "server:\n  port: 8090\n");
            Assert.Equal(Path.GetFullPath(workingYaml), YamlConfigurationFiles.Find(app, cwd));

            var appYaml = Path.Combine(app, "config.yaml");
            File.WriteAllText(appYaml, "server:\n  port: 8090\n");
            Assert.Equal(Path.GetFullPath(appYaml), YamlConfigurationFiles.Find(app, cwd));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProviderReloadsTheFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "axonhub-yaml-" + Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            File.WriteAllText(path, "server:\n  port: 8090\n");
            var configuration = new ConfigurationBuilder()
                .Add(new YamlConfigurationSource { Path = path })
                .Build();

            Assert.Equal("8090", configuration["Server:Port"]);

            File.WriteAllText(path, "server:\n  port: 9090\n");
            configuration.Reload();

            Assert.Equal("9090", configuration["Server:Port"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
