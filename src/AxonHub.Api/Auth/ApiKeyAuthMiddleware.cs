using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
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
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "Invalid API key")
                .ConfigureAwait(false);
            return;
        }
        catch (ClientIpForbiddenException)
        {
            await WriteErrorAsync(
                    context,
                    StatusCodes.Status403Forbidden,
                    "IP address is not allowed for this API key")
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            LogValidationFailed(logger, ex);
            await WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Failed to validate API key")
                .ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>gin's AbortWithStatusJSON: type is the HTTP status text.</summary>
    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("type", ReasonPhrases.GetReasonPhrase(statusCode));
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.ToArray(), context.RequestAborted)
            .ConfigureAwait(false);
    }
}
