using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of api.CopilotHandlers' HTTP half (internal/server/api/copilot.go): the
/// two admin routes of routes.go:127-128.
/// </summary>
/// <remarks>
/// gin's ShouldBindJSON decodes one JSON value with case-insensitive property
/// names and without rejecting trailing content; System.Text.Json rejects
/// trailing content (the same accepted difference as C201). StartOAuth
/// tolerates only the exact <c>EOF</c> bind error, meaning an empty or
/// whitespace-only body (copilot.go:159-166).
/// </remarks>
public static class CopilotOAuthHandlers
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of CopilotHandlers.StartOAuth (copilot.go:153-215).</summary>
    public static async Task StartAsync(
        HttpContext context,
        CopilotOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            body = buffer.ToArray();
        }

        StartCopilotOAuthRequest? request = null;
        if (!IsEmptyJsonDocument(body))
        {
            try
            {
                request = JsonSerializer.Deserialize<StartCopilotOAuthRequest>(
                    body,
                    RequestJson);
            }
            catch (JsonException)
            {
                await AbortResponse.WriteErrorAsync(
                        context,
                        StatusCodes.Status400BadRequest,
                        "invalid request format",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        CopilotStartResult result;
        try
        {
            result = await service
                .StartAsync(request?.Proxy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CopilotOAuthException ex)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    ex.StatusCode,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                writer =>
                {
                    writer.WriteString("session_id", result.SessionId);
                    writer.WriteString("user_code", result.UserCode);
                    writer.WriteString("verification_uri", result.VerificationUri);
                    writer.WriteNumber("expires_in", result.ExpiresIn);
                    writer.WriteNumber("interval", result.Interval);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of CopilotHandlers.PollOAuth (copilot.go:268-356).</summary>
    public static async Task PollAsync(
        HttpContext context,
        CopilotOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        PollCopilotOAuthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<PollCopilotOAuthRequest>(
                    context.Request.Body,
                    RequestJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // `binding:"required"` rejects the empty string; a JSON null body or a
        // missing member leaves the zero value and therefore also fails.
        if (request is null || string.IsNullOrEmpty(request.SessionId))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CopilotPollResult result;
        try
        {
            result = await service
                .PollAsync(request.SessionId, request.Proxy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CopilotOAuthException ex)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    ex.StatusCode,
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                writer =>
                {
                    // Go's omitempty drops the empty token fields; status is
                    // always present and message is non-empty in every result.
                    if (result.Token.Length > 0)
                    {
                        writer.WriteString("access_token", result.Token);
                    }

                    if (result.TokenType.Length > 0)
                    {
                        writer.WriteString("token_type", result.TokenType);
                    }

                    if (result.Scope.Length > 0)
                    {
                        writer.WriteString("scope", result.Scope);
                    }

                    writer.WriteString("status", result.Status);

                    if (result.Message.Length > 0)
                    {
                        writer.WriteString("message", result.Message);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The two Copilot routes (routes.go:127-128).</summary>
    public static IEndpointRouteBuilder MapCopilotOAuthRoutes(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/admin/copilot/oauth/start", StartAsync);
        endpoints.MapPost("/admin/copilot/oauth/poll", PollAsync);
        return endpoints;
    }

    /// <summary>Port of api.StartCopilotOAuthRequest (copilot.go:139-142).</summary>
    public sealed class StartCopilotOAuthRequest
    {
        [JsonPropertyName("proxy")]
        public ProxyConfig? Proxy { get; set; }
    }

    /// <summary>Port of api.PollCopilotOAuthRequest (copilot.go:253-257).</summary>
    public sealed class PollCopilotOAuthRequest
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("proxy")]
        public ProxyConfig? Proxy { get; set; }
    }

    /// <summary>
    /// True for an empty body and for a body made only of JSON whitespace:
    /// Go's decoder reports io.EOF, whose text is exactly "EOF", in both cases.
    /// </summary>
    private static bool IsEmptyJsonDocument(byte[] body)
    {
        foreach (var value in body)
        {
            if (value is not (0x20 or 0x09 or 0x0A or 0x0D))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task WriteJsonAsync(
        HttpContext context,
        int statusCode,
        Action<Utf8JsonWriter> writeProperties,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writeProperties(writer);
            writer.WriteEndObject();
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body
            .WriteAsync(buffer.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }
}
