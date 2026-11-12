using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of api.CodexHandlers' HTTP half (internal/server/api/codex.go): the
/// three admin routes of routes.go:114-116.
/// </summary>
/// <remarks>
/// gin's ShouldBindJSON decodes one JSON value with case-insensitive property
/// names and without rejecting trailing content; System.Text.Json rejects
/// trailing content (the same accepted difference as C201).
/// </remarks>
public static class CodexOAuthHandlers
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of CodexHandlers.StartOAuth (codex.go:58-101).</summary>
    public static async Task StartAsync(
        HttpContext context,
        CodexOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        try
        {
            // StartCodexOAuthRequest has no fields; the bind only rejects values
            // that are not a JSON object or null (codex.go:61-65).
            _ = await JsonSerializer.DeserializeAsync<StartCodexOAuthRequest>(
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

        CodexStartResult result;
        try
        {
            result = await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CodexOAuthException ex)
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
                    writer.WriteString("auth_url", result.AuthUrl);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of CodexHandlers.Exchange (codex.go:171-238).</summary>
    public static async Task ExchangeAsync(
        HttpContext context,
        CodexOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        ExchangeCodexOAuthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ExchangeCodexOAuthRequest>(
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

        CodexCredentialsResult result;
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
        catch (CodexOAuthException ex)
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

    /// <summary>Port of CodexHandlers.DecodeAuthJSON (codex.go:147-169).</summary>
    public static async Task DecodeAuthJsonAsync(
        HttpContext context,
        CodexOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        DecodeCodexAuthJsonRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<DecodeCodexAuthJsonRequest>(
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

        // `binding:"required"`: a missing or empty auth_json fails the bind.
        if (request is null || string.IsNullOrEmpty(request.AuthJson))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CodexCredentialsResult result;
        try
        {
            result = service.DecodeAuthJson(request.AuthJson);
        }
        catch (CodexOAuthException ex)
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

    /// <summary>The three Codex routes (routes.go:114-116).</summary>
    public static IEndpointRouteBuilder MapCodexOAuthRoutes(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/admin/codex/oauth/start", StartAsync);
        endpoints.MapPost("/admin/codex/oauth/exchange", ExchangeAsync);
        endpoints.MapPost("/admin/codex/auth/decode", DecodeAuthJsonAsync);
        return endpoints;
    }

    /// <summary>Port of api.StartCodexOAuthRequest (codex.go:40).</summary>
    public sealed class StartCodexOAuthRequest
    {
    }

    /// <summary>Port of api.ExchangeCodexOAuthRequest (codex.go:103-107).</summary>
    public sealed class ExchangeCodexOAuthRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("proxy")]
        public ProxyConfig? Proxy { get; set; }
    }

    /// <summary>Port of api.DecodeCodexAuthJSONRequest (codex.go:113-115).</summary>
    public sealed class DecodeCodexAuthJsonRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("auth_json")]
        public string? AuthJson { get; set; }
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
