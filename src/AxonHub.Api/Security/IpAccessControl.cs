using System.Net;
using AxonHub.Api.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Security;

/// <summary>
/// Port of server.IPAccessControl (internal/server/config.go:155-159),
/// <c>server.ip_access_control.*</c>. The reference has no explicit defaults,
/// so the zero value is disabled with an empty allowlist and no redirect.
/// </summary>
public sealed record IpAccessControlSettings(
    bool Enabled,
    IReadOnlyList<string> AllowedIps,
    string RedirectUrl)
{
    public static IpAccessControlSettings Default { get; } = new(
        Enabled: false,
        AllowedIps: Array.Empty<string>(),
        RedirectUrl: string.Empty);

    /// <summary>
    /// Reads the <c>Server:IpAccessControl</c> section first, then the
    /// reference's <c>AXONHUB_SERVER_IP_ACCESS_CONTROL_*</c> environment
    /// spelling. A scalar <c>allowed_ips</c> value is split on commas.
    /// </summary>
    public static IpAccessControlSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabledValue = configuration["Server:IpAccessControl:Enabled"]
            ?? configuration["AXONHUB_SERVER_IP_ACCESS_CONTROL_ENABLED"];
        var enabled = bool.TryParse(enabledValue, out var parsed) ? parsed : false;

        var redirectUrl = configuration["Server:IpAccessControl:RedirectUrl"]
            ?? configuration["AXONHUB_SERVER_IP_ACCESS_CONTROL_REDIRECT_URL"]
            ?? string.Empty;

        return new IpAccessControlSettings(
            enabled,
            ReadAllowedIps(configuration),
            redirectUrl);
    }

    private static IReadOnlyList<string> ReadAllowedIps(IConfiguration configuration)
    {
        var section = configuration.GetSection("Server:IpAccessControl:AllowedIps");
        if (section.GetChildren().Any())
        {
            return section.Get<string[]>() ?? [];
        }

        var scalar = configuration["AXONHUB_SERVER_IP_ACCESS_CONTROL_ALLOWED_IPS"]
            ?? configuration["Server:IpAccessControl:AllowedIps"];
        if (string.IsNullOrEmpty(scalar))
        {
            return Array.Empty<string>();
        }

        return scalar.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}

/// <summary>
/// Port of middleware.IPAccessControlConfig (ip_access_control.go:25-58): the
/// validated, immutable snapshot is replaced atomically so a reload never
/// exposes a partially parsed allowlist.
/// </summary>
public sealed class IpAccessControlConfig
{
    private volatile IpAccessControlState _state;

    public IpAccessControlConfig(IpAccessControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _state = IpAccessControlState.Create(settings);
    }

    public IpAccessControlState Current => _state;

    /// <summary>
    /// Validates a replacement before publishing it. A rejected reload leaves
    /// the previous snapshot active.
    /// </summary>
    public void Apply(IpAccessControlSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _state = IpAccessControlState.Create(settings);
    }
}

/// <summary>Port of ipAccessControlState.</summary>
public sealed record IpAccessControlState(
    bool Enabled,
    IReadOnlyList<string> AllowedIps,
    string RedirectUrl)
{
    internal static IpAccessControlState Create(IpAccessControlSettings settings)
    {
        var validated = new List<string>(settings.AllowedIps.Count);

        foreach (var raw in settings.AllowedIps)
        {
            var item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (item.Contains('/', StringComparison.Ordinal))
            {
                if (!ClientAddress.TryParsePrefix(item, out _, out _))
                {
                    throw new InvalidOperationException(
                        "invalid allowed IP prefix \"" + item + "\"");
                }
            }
            else if (!ClientAddress.TryParseAddress(item, out _))
            {
                throw new InvalidOperationException(
                    "invalid allowed IP \"" + item + "\"");
            }

            validated.Add(item);
        }

        return new IpAccessControlState(settings.Enabled, validated, settings.RedirectUrl);
    }
}

/// <summary>
/// Port of middleware.WithIPAccessControl (ip_access_control.go:60-84). It is
/// the first global middleware, so denied requests never reach tracing, auth or
/// any route handler, matching routes.go:57.
/// </summary>
public sealed class IpAccessControlMiddleware(
    RequestDelegate next,
    IpAccessControlConfig config,
    ApiKeyAuthOptions authOptions,
    ILogger<IpAccessControlMiddleware> logger)
{
    private static readonly Action<ILogger, string, string, string, Exception?> DeniedRequest =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Warning,
            new EventId(0, "IpAccessControlDenied"),
            "IP access control denied request {ClientIp} {Method} {Path}");

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var state = config.Current;
        if (!state.Enabled)
        {
            return next(context);
        }

        var clientIp = ClientAddress.Resolve(context.Request, authOptions.TrustedProxies).Trim();
        if (clientIp.Length == 0
            || !ClientAddress.IsAnyAllowedIp([clientIp], state.AllowedIps))
        {
            return DenyAsync(context, clientIp, state.RedirectUrl);
        }

        return next(context);
    }

    private Task DenyAsync(HttpContext context, string clientIp, string redirectUrl)
    {
        DeniedRequest(
            logger,
            clientIp,
            context.Request.Method,
            context.Request.Path.Value ?? string.Empty,
            null);

        if (redirectUrl.Length > 0)
        {
            // gin's c.Redirect(http.StatusFound, redirectURL).
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = redirectUrl;
            return Task.CompletedTask;
        }

        // c.AbortWithStatus(http.StatusNotFound): empty 404 body.
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}
