using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of <c>middleware.WithStrictAPIKeyConfig(auth, nil)</c>
/// (middleware/auth.go:31-91 with <c>allowNoAuth=false</c>): the regular header
/// extraction, no no-auth fallback, and the no-auth sentinel key rejected
/// before the lookup. Every rejection uses the reference's fixed messages.
/// </summary>
public sealed class StrictApiKeyAuthMiddleware(
    RequestDelegate next,
    ILogger<StrictApiKeyAuthMiddleware> logger)
{
    private static readonly Action<ILogger, Exception?> LogValidationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "StrictValidateApiKeyFailed"),
            "Failed to validate API key");

    public async Task InvokeAsync(HttpContext context, IApiKeyAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);

        var key = ApiKeyHeaderExtractor.Extract(context.Request.Headers);

        // auth.go:38-46: a missing key and the no-auth sentinel are both
        // "Invalid API key" on a strict surface.
        if (string.IsNullOrEmpty(key)
            || string.Equals(key, ApiKeyAuthenticator.NoAuthApiKeyValue, StringComparison.Ordinal))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid API key",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var identity = await authenticator
                .AuthenticateKeyAsync(context.Request, key, context.RequestAborted)
                .ConfigureAwait(false);
            context.Items[RequestIdentity.HttpContextItemKey] = identity;
        }
        catch (InvalidApiKeyException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid API key",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        catch (ClientIpForbiddenException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "IP address is not allowed for this API key",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            LogValidationFailed(logger, ex);
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Failed to validate API key",
                    context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }
}
