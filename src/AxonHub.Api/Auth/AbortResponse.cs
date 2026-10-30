using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace AxonHub.Api.Auth;

/// <summary>
/// gin's AbortWithStatusJSON envelope: <c>{"error":{"type":...,"message":...}}</c>
/// where type is the HTTP status text (middleware/error.go).
/// </summary>
internal static class AbortResponse
{
    public static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

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
        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }
}
