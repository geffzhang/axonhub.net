using AxonHub.Api.Auth;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Security;

/// <summary>
/// Port of middleware.WithIPBlocklist (internal/server/middleware/ip_blocklist.go).
/// The reference mounts it on the API group before WithAPIKeyConfig, so a blocked
/// client is rejected with 403 before any API key is examined.
/// </summary>
public sealed class IpBlocklistMiddleware(
    RequestDelegate next,
    ApiKeyAuthOptions options)
{
    public async Task InvokeAsync(HttpContext context, ISecuritySettingsProvider securitySettings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(securitySettings);

        // clientIPCandidates: a single trimmed gin ClientIP; no candidates means
        // the settings row is never read.
        var clientIp = ClientAddress.Resolve(context.Request, options.TrustedProxies).Trim();
        if (clientIp.Length == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var settings = await securitySettings
            .GetSecuritySettingsAsync(context.RequestAborted)
            .ConfigureAwait(false);

        if (!ClientAddress.IsBlockedIp([clientIp], settings.BlockedIps))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        await AbortResponse.WriteErrorAsync(
                context,
                StatusCodes.Status403Forbidden,
                "IP address is blocked",
                context.RequestAborted)
            .ConfigureAwait(false);
    }
}
