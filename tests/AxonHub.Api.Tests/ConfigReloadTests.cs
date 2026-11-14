using System.Globalization;
using AxonHub.Api.Lifecycle;
using AxonHub.Api.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// C298: server.pid_file (main.go:70-100) and the Unix SIGHUP reload contract
/// (config_reload_unix.go): only server.ip_access_control is re-applied, and a
/// rejected reload keeps the previous snapshot.
/// </summary>
public sealed class ConfigReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "axonhub-reloade2e-" + Guid.NewGuid().ToString("N"));

    public ConfigReloadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory must not fail the test.
        }
    }

    [Fact]
    public void WriteCreatesPidFileWithTrailingNewline()
    {
        var path = Path.Combine(_root, "axonhub.pid");

        PidFile.Write(path);

        Assert.Equal(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void WriteCreatesOwnerOnlyFileOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_root, "axonhub-mode.pid");

        PidFile.Write(path);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }

    [Fact]
    public void DeleteIgnoresMissingFile()
    {
        PidFile.Delete(Path.Combine(_root, "absent.pid"));
    }

    [Fact]
    public void ExpandHomeOnlyExpandsLeadingTilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(
            Path.Combine(home, "axonhub/axonhub.pid"),
            PidFile.ExpandHome("~/axonhub/axonhub.pid"));
        Assert.Equal("/var/run/axonhub.pid", PidFile.ExpandHome("/var/run/axonhub.pid"));
        Assert.Equal("relative/pid", PidFile.ExpandHome("relative/pid"));
    }

    [Fact]
    public async Task HostedServiceWritesPidOnStartAndRemovesItOnStop()
    {
        var path = Path.Combine(_root, "hosted.pid");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:PidFile"] = path,
            })
            .Build();
        var service = new PidFileHostedService(
            configuration,
            NullLogger<PidFileHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n",
            File.ReadAllText(path));

        await service.StopAsync(CancellationToken.None);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task HostedServiceLeavesNoFileWhenPidFileIsEmpty()
    {
        var configuration = new ConfigurationBuilder().Build();
        var service = new PidFileHostedService(
            configuration,
            NullLogger<PidFileHostedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public void ReloadReappliesIpAccessControlFromTheStartupFile()
    {
        var path = WriteConfig(
            "config.json",
            """
            {
              "Server": {
                "Name": "first",
                "IpAccessControl": {
                  "Enabled": true,
                  "AllowedIps": ["127.0.0.1"],
                  "RedirectUrl": "https://example.test/denied"
                }
              }
            }
            """);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        var snapshot = new IpAccessControlConfig(IpAccessControlSettings.Default);
        using var service = new ConfigReloadService(
            configuration,
            snapshot,
            NullLogger<ConfigReloadService>.Instance,
            path);

        Assert.True(service.TryReload());
        Assert.True(snapshot.Current.Enabled);
        Assert.Equal(new[] { "127.0.0.1" }, snapshot.Current.AllowedIps);
        Assert.Equal("https://example.test/denied", snapshot.Current.RedirectUrl);

        File.WriteAllText(
            path,
            """
            {
              "Server": {
                "Name": "second",
                "IpAccessControl": {
                  "Enabled": false,
                  "AllowedIps": []
                }
              }
            }
            """);

        Assert.True(service.TryReload());
        Assert.False(snapshot.Current.Enabled);
        Assert.Empty(snapshot.Current.AllowedIps);

        // The rest of the configuration is reloaded but not pushed into any
        // runtime snapshot; the reload loop only calls Apply for IP control.
        Assert.Equal("second", configuration["Server:Name"]);
    }

    [Fact]
    public void RejectedReloadKeepsThePreviousIpAccessControlSnapshot()
    {
        var path = WriteConfig(
            "config.json",
            """
            {
              "Server": {
                "IpAccessControl": {
                  "Enabled": true,
                  "AllowedIps": ["127.0.0.1"]
                }
              }
            }
            """);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        var snapshot = new IpAccessControlConfig(IpAccessControlSettings.Default);
        using var service = new ConfigReloadService(
            configuration,
            snapshot,
            NullLogger<ConfigReloadService>.Instance,
            path);

        Assert.True(service.TryReload());
        var before = snapshot.Current;

        File.WriteAllText(
            path,
            """
            {
              "Server": {
                "IpAccessControl": {
                  "Enabled": true,
                  "AllowedIps": ["not-an-ip"]
                }
              }
            }
            """);

        Assert.False(service.TryReload());
        Assert.Same(before, snapshot.Current);
        Assert.Equal(new[] { "127.0.0.1" }, snapshot.Current.AllowedIps);
    }

    [Fact]
    public void ReloadWithoutAStartupFileIsRejected()
    {
        var configuration = new ConfigurationBuilder().Build();
        var snapshot = new IpAccessControlConfig(IpAccessControlSettings.Default);
        using var service = new ConfigReloadService(
            configuration,
            snapshot,
            NullLogger<ConfigReloadService>.Instance,
            configFile: null);

        Assert.False(service.TryReload());
        Assert.False(snapshot.Current.Enabled);
    }

    [Fact]
    public void FindConfigFileUsesTheFirstExistingAppSettingsFile()
    {
        var contentRoot = Path.Combine(_root, "content");
        Directory.CreateDirectory(contentRoot);

        Assert.Null(ConfigReloadService.FindConfigFile(contentRoot, "Production"));

        var environmentSpecific = Path.Combine(contentRoot, "appsettings.Production.json");
        File.WriteAllText(environmentSpecific, "{}");
        Assert.Equal(
            Path.GetFullPath(environmentSpecific),
            ConfigReloadService.FindConfigFile(contentRoot, "Production"));

        var primary = Path.Combine(contentRoot, "appsettings.json");
        File.WriteAllText(primary, "{}");
        Assert.Equal(
            Path.GetFullPath(primary),
            ConfigReloadService.FindConfigFile(contentRoot, "Production"));
    }

    [Fact]
    public void SendReloadSignalReportsAnInvalidPid()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_root, "invalid.pid");
        File.WriteAllText(path, "  abc \n");

        var error = Assert.Throws<InvalidOperationException>(
            () => PidFile.SendReloadSignal(path));

        Assert.Equal(
            "invalid PID in " + path
            + ": strconv.Atoi: parsing \"abc\": invalid syntax",
            error.Message);
    }

    [Fact]
    public void SendReloadSignalReportsAMissingPidFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_root, "missing.pid");

        var error = Assert.Throws<InvalidOperationException>(
            () => PidFile.SendReloadSignal(path));

        Assert.StartsWith("read PID file " + path + ": ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendReloadSignalUsesTheReferenceWindowsMessage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => PidFile.SendReloadSignal(Path.Combine(_root, "ignored.pid")));

        Assert.Equal(
            "reload is not available on Windows — use a Unix-like OS and send SIGHUP manually",
            error.Message);
    }

    private string WriteConfig(string name, string json)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, json);
        return path;
    }
}
