using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Lifecycle;

/// <summary>
/// Port of the reference's PID-file hook (cmd/axonhub/main.go:70-100) and the
/// Unix-only reload sender (cmd/axonhub/reload_unix.go,
/// cmd/axonhub/reload_windows.go).
/// </summary>
public static class PidFile
{
    /// <summary>syscall.SIGHUP.</summary>
    private const int Sighup = 1;

    /// <summary>server.pid_file (conf/conf.go:243, empty by default).</summary>
    public static string FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return configuration["Server:PidFile"]
            ?? configuration["AXONHUB_SERVER_PID_FILE"]
            ?? string.Empty;
    }

    /// <summary>main.go:239-247: only a leading <c>~/</c> expands.</summary>
    public static string ExpandHome(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!path.StartsWith("~/", StringComparison.Ordinal))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length == 0 ? path : System.IO.Path.Combine(home, path[2..]);
    }

    /// <summary>main.go:233-235: PID plus newline, mode 0600.</summary>
    public static void Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
        };

        if (!OperatingSystem.IsWindows())
        {
            // os.WriteFile(path, data, 0o600) applies the mode on creation.
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        writer.Write('\n');
    }

    /// <summary>main.go:92-96: ENOENT is not an error.</summary>
    public static void Delete(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.Delete(path);
    }

    /// <summary>
    /// Port of reloadRunningServer: read the PID file, parse the PID, send
    /// SIGHUP. Windows returns the reference's fixed error instead.
    /// </summary>
    public static void SendReloadSignal(string pidFile)
    {
        ArgumentNullException.ThrowIfNull(pidFile);

        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "reload is not available on Windows — use a Unix-like OS and send SIGHUP manually");
        }

        string data;
        try
        {
            data = File.ReadAllText(pidFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "read PID file " + pidFile + ": " + ex.Message,
                ex);
        }

        var text = data.Trim();
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
        {
            throw new InvalidOperationException(
                "invalid PID in " + pidFile + ": strconv.Atoi: parsing "
                + Quote(text) + ": invalid syntax");
        }

        if (Kill(pid, Sighup) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException(
                "send SIGHUP to process " + pid.ToString(CultureInfo.InvariantCulture)
                + ": " + new Win32Exception(error).Message);
        }
    }

#pragma warning disable SYSLIB1054 // LibraryImport needs AllowUnsafeBlocks; this blittable call needs no marshalling.
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int Kill(int pid, int signal);
#pragma warning restore SYSLIB1054

    private static string Quote(string value)
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
                    builder.Append(character);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}

/// <summary>
/// main.go:70-100 as a hosted service: write the PID before the server
/// starts, remove it when the host stops. An empty <c>server.pid_file</c>
/// registers the service but does nothing, like the reference's hook.
/// </summary>
public sealed class PidFileHostedService(
    IConfiguration configuration,
    ILogger<PidFileHostedService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, string, Exception?> RemoveFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(0, "PidFileRemoveFailed"),
            "remove PID file {Path}: {Error}");

    private string? _path;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = PidFile.FromConfiguration(configuration);
        if (configured.Length == 0)
        {
            return Task.CompletedTask;
        }

        _path = PidFile.ExpandHome(configured);
        try
        {
            PidFile.Write(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "write PID file " + _path + ": " + ex.Message,
                ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_path is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            PidFile.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The reference returns the error from its OnStop hook; fx logs it
            // and continues shutdown.
            RemoveFailed(logger, _path, ex.Message, ex);
        }

        return Task.CompletedTask;
    }
}
