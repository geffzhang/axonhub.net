using AxonHub.Api.Auth;
using AxonHub.Api.Settings;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Codex;

/// <summary>
/// Port of <c>CodexCompatibilityHandlers.Enabled</c>
/// (internal/server/api/codex_compatibility.go): every <c>/codex</c> response
/// carries <c>Cache-Control: no-store</c>; the settings are read under the
/// system bypass (no <c>read_settings</c> scope), a read failure answers 500
/// and a disabled surface answers 404 with the reference's fixed messages.
/// </summary>
public sealed class CodexCompatibilityEnabledMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ICodexCompatibilitySettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        context.Response.Headers.CacheControl = "no-store";

        CodexCompatibilitySettings current;
        try
        {
            // authz.RunWithSystemBypass("codex-compatibility-switch"): the gate
            // is server-owned, so no principal scope is consulted.
            current = await settings.GetBypassAsync(context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "failed to read Codex compatibility settings",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (!current.Enabled)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "Codex compatibility is disabled",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
