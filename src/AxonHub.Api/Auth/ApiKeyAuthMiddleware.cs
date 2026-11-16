using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.WithAPIKeyConfig(auth, nil) for the ported API surface.
/// Runs before the handler, writes gin's AbortWithStatusJSON body on rejection,
/// and publishes the authenticated identity for the persistence middleware.
/// </summary>
public sealed class ApiKeyAuthMiddleware(
    RequestDelegate next,
    ILogger<ApiKeyAuthMiddleware> logger)
{
    private static readonly Action<ILogger, Exception?> LogValidationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ValidateApiKeyFailed"),
            "Failed to validate API key");

    public async Task InvokeAsync(HttpContext context, IApiKeyAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);

        try
        {
            var identity = await authenticator
                .AuthenticateAsync(context.Request, context.RequestAborted)
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
