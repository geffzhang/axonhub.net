using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of api.AntigravityHandlers' HTTP half (internal/server/api/antigravity.go:87-247):
/// the two admin routes of routes.go:124-125.
/// </summary>
/// <remarks>
/// gin's ShouldBindJSON decodes one JSON value with case-insensitive property
/// names and without rejecting trailing content; System.Text.Json rejects
/// trailing content (the same accepted difference as C201).
/// </remarks>
public static class AntigravityOAuthHandlers
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of AntigravityHandlers.StartOAuth (antigravity.go:87-138).</summary>
    public static async Task StartAsync(
        HttpContext context,
        AntigravityOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        StartAntigravityOAuthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<StartAntigravityOAuthRequest>(
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

        // ShouldBindJSON into a struct without required fields accepts a JSON
        // null body as the zero value; only malformed JSON fails the bind.
        AntigravityStartResult result;
        try
        {
            result = await service
                .StartAsync(request?.ProjectId ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
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
                    writer.WriteString("auth_url", result.AuthUrl);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of AntigravityHandlers.Exchange (antigravity.go:176-247).</summary>
    public static async Task ExchangeAsync(
        HttpContext context,
        AntigravityOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        ExchangeAntigravityOAuthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ExchangeAntigravityOAuthRequest>(
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
        if (request is null
            || string.IsNullOrEmpty(request.SessionId)
            || string.IsNullOrEmpty(request.CallbackUrl))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        AntigravityExchangeResult result;
        try
        {
            result = await service
                .ExchangeAsync(
                    request.SessionId,
                    request.CallbackUrl,
                    request.Proxy,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AntigravityOAuthException ex)
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
                writer => writer.WriteString("credentials", result.Credentials),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The two Antigravity routes (routes.go:124-125).</summary>
    public static IEndpointRouteBuilder MapAntigravityOAuthRoutes(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/admin/antigravity/oauth/start", StartAsync);
        endpoints.MapPost("/admin/antigravity/oauth/exchange", ExchangeAsync);
        return endpoints;
    }

    /// <summary>Port of api.StartAntigravityOAuthRequest (antigravity.go:45-47).</summary>
    public sealed class StartAntigravityOAuthRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("project_id")]
        public string? ProjectId { get; set; }
    }

    /// <summary>Port of api.ExchangeAntigravityOAuthRequest (antigravity.go:140-144).</summary>
    public sealed class ExchangeAntigravityOAuthRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("proxy")]
        public ProxyConfig? Proxy { get; set; }
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
