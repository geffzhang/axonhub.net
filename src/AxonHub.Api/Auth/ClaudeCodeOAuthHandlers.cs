using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of api.ClaudeCodeHandlers' HTTP half
/// (internal/server/api/claudecode.go): the two admin routes of
/// routes.go:120-121.
/// </summary>
/// <remarks>
/// gin's ShouldBindJSON decodes one JSON value with case-insensitive property
/// names and without rejecting trailing content; System.Text.Json rejects
/// trailing content (the same accepted difference as C201).
/// </remarks>
public static class ClaudeCodeOAuthHandlers
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of ClaudeCodeHandlers.StartOAuth (claudecode.go:82-125).</summary>
    public static async Task StartAsync(
        HttpContext context,
        ClaudeCodeOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        try
        {
            // StartClaudeCodeOAuthRequest is an empty struct, but the bind
            // still rejects a malformed body (claudecode.go:87-91).
            _ = await JsonSerializer.DeserializeAsync<StartClaudeCodeOAuthRequest>(
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

        ClaudeCodeStartResult result;
        try
        {
            result = await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ClaudeCodeOAuthException ex)
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

    /// <summary>Port of ClaudeCodeHandlers.Exchange (claudecode.go:171-234).</summary>
    public static async Task ExchangeAsync(
        HttpContext context,
        ClaudeCodeOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        ExchangeClaudeCodeOAuthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ExchangeClaudeCodeOAuthRequest>(
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

        ClaudeCodeCredentialsResult result;
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
        catch (ClaudeCodeOAuthException ex)
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

    /// <summary>The two Claude Code routes (routes.go:120-121).</summary>
    public static IEndpointRouteBuilder MapClaudeCodeOAuthRoutes(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/admin/claudecode/oauth/start", StartAsync);
        endpoints.MapPost("/admin/claudecode/oauth/exchange", ExchangeAsync);
        return endpoints;
    }

    /// <summary>Port of api.StartClaudeCodeOAuthRequest (claudecode.go:43).</summary>
    public sealed class StartClaudeCodeOAuthRequest
    {
    }

    /// <summary>Port of api.ExchangeClaudeCodeOAuthRequest (claudecode.go:127-131).</summary>
    public sealed class ExchangeClaudeCodeOAuthRequest
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; set; }

        [JsonPropertyName("proxy")]
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
