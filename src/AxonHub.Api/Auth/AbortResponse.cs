using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Llm.Json;
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

        // gin writes this envelope with encoding/json, which leaves characters
        // such as ' alone and HTML-escapes <, > and &. The relaxed encoder plus
        // GoJson.Escape reproduces those bytes; the default encoder would emit
        // ' for the apostrophe in "must start with 'Bearer '".
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
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
        await context.Response.Body.WriteAsync(GoJson.Escape(buffer.ToArray()), cancellationToken)
            .ConfigureAwait(false);
    }
}
