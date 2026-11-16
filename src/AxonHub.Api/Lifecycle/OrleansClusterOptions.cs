using System.Globalization;
using System.Net;

namespace AxonHub.Api.Lifecycle;

/// <summary>
/// Opt-in Orleans cluster configuration. The default single-silo host keeps
/// <c>UseLocalhostClustering</c> + in-memory reminders; setting
/// <c>OrleansCluster:Enabled=true</c> switches to AdoNet membership and
/// AdoNet reminders in PostgreSQL, which is the supported multi-pod mode for
/// Kubernetes or any other multi-instance deployment.
/// </summary>
public sealed record OrleansClusterOptions
{
    public bool Enabled { get; init; }

    public string ClusterId { get; init; } = "axonhub";

    public string ServiceId { get; init; } = "axonhub";

    public string SiloName { get; init; } = Environment.MachineName;

    public IPAddress? AdvertisedAddress { get; init; }

    public int SiloPort { get; init; } = 11111;

    public int GatewayPort { get; init; } = 30000;

    public string ConnectionString { get; init; } = string.Empty;

    public static OrleansClusterOptions FromConfiguration(
        IConfiguration configuration,
        string fallbackConnectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabled = ReadBool(
            First(
                configuration["OrleansCluster:Enabled"],
                configuration["ORLEANS_CLUSTER_ENABLED"]),
            fallback: false);

        var provider = First(
            configuration["OrleansCluster:Provider"],
            configuration["ORLEANS_CLUSTER_PROVIDER"]);
        if (provider.Length > 0
            && !provider.Equals("adonet", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "unsupported Orleans clustering provider \"" + provider
                + "\": axonhub.net supports \"adonet\" (PostgreSQL)");
        }

        var connectionString = First(
            configuration["OrleansCluster:ConnectionString"],
            configuration["ORLEANS_CLUSTER_CONNECTIONSTRING"],
            fallbackConnectionString);

        var siloPort = ReadPort(
            First(
                configuration["OrleansCluster:SiloPort"],
                configuration["ORLEANS_CLUSTER_SILO_PORT"]),
            11111,
            "OrleansCluster:SiloPort");
        var gatewayPort = ReadPort(
            First(
                configuration["OrleansCluster:GatewayPort"],
                configuration["ORLEANS_CLUSTER_GATEWAY_PORT"]),
            30000,
            "OrleansCluster:GatewayPort",
            allowZero: true);

        var advertised = First(
            configuration["OrleansCluster:AdvertisedIP"],
            configuration["ORLEANS_CLUSTER_ADVERTISED_IP"],
            Environment.GetEnvironmentVariable("POD_IP"));

        if (enabled && string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "OrleansCluster:ConnectionString (or ConnectionStrings:Default) "
                + "is required when OrleansCluster:Enabled=true");
        }

        return new OrleansClusterOptions
        {
            Enabled = enabled,
            ClusterId = First(
                configuration["OrleansCluster:ClusterId"],
                configuration["ORLEANS_CLUSTER_ID"],
                "axonhub"),
            ServiceId = First(
                configuration["OrleansCluster:ServiceId"],
                configuration["ORLEANS_CLUSTER_SERVICE_ID"],
                "axonhub"),
            SiloName = First(
                configuration["OrleansCluster:SiloName"],
                configuration["ORLEANS_CLUSTER_SILO_NAME"],
                Environment.GetEnvironmentVariable("HOSTNAME"),
                Environment.MachineName),
            AdvertisedAddress = enabled ? ResolveAdvertisedAddress(advertised) : null,
            SiloPort = siloPort,
            GatewayPort = gatewayPort,
            ConnectionString = connectionString,
        };
    }

    private static IPAddress ResolveAdvertisedAddress(string configured)
    {
        if (configured.Length > 0)
        {
            if (IPAddress.TryParse(configured, out var parsed))
            {
                return parsed;
            }

            var resolved = Dns.GetHostAddresses(configured)
                .FirstOrDefault(address => address.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetwork);
            if (resolved is null)
            {
                throw new InvalidOperationException(
                    "Orleans advertised address \"" + configured
                    + "\" did not resolve to an IPv4 address");
            }

            return resolved;
        }

        var host = Dns.GetHostName();
        var local = Dns.GetHostAddresses(host)
            .FirstOrDefault(address => address.AddressFamily
                == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address));
        if (local is null)
        {
            throw new InvalidOperationException(
                "cannot determine the Orleans advertised IPv4 address for host \""
                + host + "\"; set OrleansCluster:AdvertisedIP (POD_IP in Kubernetes)");
        }

        return local;
    }

    private static int ReadPort(
        string value,
        int fallback,
        string key,
        bool allowZero = false)
    {
        if (value.Length == 0)
        {
            return fallback;
        }

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port < (allowZero ? 0 : 1)
            || port > 65535)
        {
            throw new InvalidOperationException(
                key + " must be a valid port number");
        }

        return port;
    }

    private static bool ReadBool(string value, bool fallback)
        => value.Length == 0
            ? fallback
            : bool.TryParse(value, out var parsed)
                ? parsed
                : throw new InvalidOperationException(
                    "invalid boolean value for Orleans clustering: " + value);

    private static string First(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }
}
