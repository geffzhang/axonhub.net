using System.Runtime.InteropServices;
using System.Threading.Channels;
using AxonHub.Api.Configuration;
using AxonHub.Api.Security;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Lifecycle;

/// <summary>
/// Port of cmd/axonhub/config_reload_unix.go and
/// cmd/axonhub/config_reload_windows.go: SIGHUP re-reads the startup config
/// file and re-applies only <c>server.ip_access_control</c>. Every other
/// section keeps its startup values; a rejected reload leaves the current
/// snapshot untouched.
/// </summary>
public sealed class ConfigReloadService : IDisposable
{
    private static readonly Action<ILogger, Exception?> WindowsUnavailable =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "ConfigReloadUnavailableOnWindows"),
            "SIGHUP config reload is unavailable on Windows");

    private static readonly Action<ILogger, Exception?> NoConfigFile =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "ConfigReloadDisabled"),
            "SIGHUP config reload disabled because no config file was loaded at startup");

    private static readonly Action<ILogger, string, Exception?> ReloadEnabled =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(0, "ConfigReloadEnabled"),
            "SIGHUP config reload enabled {ConfigFile}");

    private static readonly Action<ILogger, string, Exception?> ReloadRejected =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(0, "ConfigReloadRejected"),
            "SIGHUP config reload rejected {Error}");

    private static readonly Action<ILogger, string, Exception?> ReloadApplied =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(0, "ConfigReloadApplied"),
            "SIGHUP config reload applied {Section}");

    private static readonly Action<ILogger, Exception?> WorkerPanicked =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ConfigReloadWorkerPanicked"),
            "config reload signal worker panicked");

    private readonly IConfigurationRoot _configuration;
    private readonly IpAccessControlConfig _ipAccessControl;
    private readonly ILogger<ConfigReloadService> _logger;
    private readonly string? _configFile;
    private readonly object _sync = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private PosixSignalRegistration? _registration;

    public ConfigReloadService(
        IConfigurationRoot configuration,
        IpAccessControlConfig ipAccessControl,
        ILogger<ConfigReloadService> logger,
        string? configFile)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(ipAccessControl);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _ipAccessControl = ipAccessControl;
        _logger = logger;
        _configFile = configFile;
    }

    /// <summary>
    /// The config file selected at startup, or null when the process uses
    /// defaults/environment only. Reload is disabled in that case, matching
    /// Loader.Reload.
    /// </summary>
    public string? ConfigFile => _configFile;

    /// <summary>
    /// Finds the application configuration file the host loaded at startup.
    /// config.yml (the reference's file) wins; appsettings.json and its
    /// environment-specific sibling are the port-native fallbacks.
    /// </summary>
    public static string? FindConfigFile(string contentRootPath, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(contentRootPath);
        ArgumentNullException.ThrowIfNull(environmentName);

        var yaml = YamlConfigurationFiles.Find(
            contentRootPath,
            Directory.GetCurrentDirectory());
        if (yaml is not null)
        {
            return yaml;
        }

        var candidates = new[]
        {
            "appsettings.json",
            "appsettings." + environmentName + ".json",
        };

        foreach (var candidate in candidates)
        {
            var path = System.IO.Path.Combine(contentRootPath, candidate);
            if (File.Exists(path))
            {
                return System.IO.Path.GetFullPath(path);
            }
        }

        return null;
    }

    /// <summary>fx OnStart: register the Unix signal loop.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_registration is not null || _cancellation is not null)
            {
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                WindowsUnavailable(_logger, null);
                return;
            }

            if (string.IsNullOrEmpty(_configFile))
            {
                NoConfigFile(_logger, null);
                return;
            }

            // A one-element buffer coalesces bursts of HUP signals while a
            // reload is in progress. The reload loop is deliberately serial.
            var signals = Channel.CreateBounded<bool>(
                new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var cancellation = new CancellationTokenSource();
            var registration = PosixSignalRegistration.Create(
                PosixSignal.SIGHUP,
                context =>
                {
                    context.Cancel = true;
                    signals.Writer.TryWrite(true);
                });

            _cancellation = cancellation;
            _registration = registration;
            _loop = Task.Run(() => RunAsync(signals.Reader, cancellation.Token));
            ReloadEnabled(_logger, _configFile, null);
        }
    }

    /// <summary>fx OnStop: stop accepting signals, then drain the loop.</summary>
    public void Stop()
    {
        PosixSignalRegistration? registration;
        CancellationTokenSource? cancellation;
        Task? loop;

        lock (_sync)
        {
            registration = _registration;
            cancellation = _cancellation;
            loop = _loop;
            _registration = null;
            _cancellation = null;
            _loop = null;
        }

        registration?.Dispose();
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        if (loop is not null)
        {
            try
            {
                // fx.StopTimeout(30s) bounds the reference's OnStop wait.
                loop.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                // The loop reports its own failures; the wait only bounds it.
            }
        }

        cancellation.Dispose();
    }

    /// <summary>
    /// reloadIPAccessControl: reload the startup file, then replace the
    /// validated IP allowlist snapshot. Returns false and keeps the previous
    /// snapshot when either step fails.
    /// </summary>
    public bool TryReload()
    {
        if (string.IsNullOrEmpty(_configFile))
        {
            ReloadRejected(
                _logger,
                "load config: config reload is unavailable without a config file",
                null);
            return false;
        }

        try
        {
            _configuration.Reload();
        }
        catch (Exception ex)
        {
            ReloadRejected(_logger, "load config: " + ex.Message, null);
            return false;
        }

        try
        {
            _ipAccessControl.Apply(IpAccessControlSettings.FromConfiguration(_configuration));
        }
        catch (Exception ex)
        {
            ReloadRejected(
                _logger,
                "apply IP access control configuration: " + ex.Message,
                null);
            return false;
        }

        ReloadApplied(_logger, "server.ip_access_control", null);
        return true;
    }

    private async Task RunAsync(ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out _))
                {
                    TryReload();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            WorkerPanicked(_logger, ex);
        }
    }

    public void Dispose() => Stop();
}
