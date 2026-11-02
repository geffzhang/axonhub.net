using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.WithGeminiKeyAuth (auth.go:262-310) for the native
/// Gemini surface: the query <c>key</c> parameter wins over the regular
/// header extractor, and authentication is strict — there is no no-auth
/// fallback on this surface, unlike WithAPIKeyConfig.
/// </summary>
public sealed class GeminiKeyAuthMiddleware(
    RequestDelegate next,
    ILogger<GeminiKeyAuthMiddleware> logger)
{
    private static readonly Action<ILogger, Exception?> LogValidationFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ValidateGeminiKeyFailed"),
            "Failed to validate Gemini API key");

    public async Task InvokeAsync(HttpContext context, IApiKeyAuthenticator authenticator)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authenticator);

        // middleware.ExtractAPIKeyFromRequest checks Authorization and x-api-key;
        // the Gemini surface additionally accepts the query `key` parameter first.
        var key = context.Request.Query["key"].FirstOrDefault();
        if (string.IsNullOrEmpty(key))
        {
            key = ApiKeyHeaderExtractor.Extract(context.Request.Headers);
        }

        if (string.IsNullOrEmpty(key))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "API key is required",
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
                    "invalid api key",
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
