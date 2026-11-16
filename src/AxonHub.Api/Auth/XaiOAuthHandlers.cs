using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of api.XAIHandlers' HTTP half (internal/server/api/xai.go): the three
/// admin routes of routes.go:117-119.
/// </summary>
/// <remarks>
/// gin's ShouldBindJSON decodes one JSON value with case-insensitive property
/// names and without rejecting trailing content; System.Text.Json rejects
/// trailing content (the same accepted difference as C201).
/// </remarks>
public static class XaiOAuthHandlers
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Port of XAIHandlers.StartOAuth (xai.go:74-122).</summary>
    public static async Task StartAsync(
        HttpContext context,
        XaiOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        // xai.go:75-81 reads the user from the context; StartOAuth has no
        // request struct and never touches the body.
        XaiStartResult result;
        try
        {
            result = await service.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (XaiOAuthException ex)
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

    /// <summary>Port of XAIHandlers.Exchange (xai.go:124-177).</summary>
    public static async Task ExchangeAsync(
        HttpContext context,
        XaiOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        ExchangeXaiOAuthRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ExchangeXaiOAuthRequest>(
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

        XaiCredentialsResult result;
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
        catch (XaiOAuthException ex)
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

    /// <summary>Port of XAIHandlers.DecodeSSO (xai.go:179-203).</summary>
    public static async Task DecodeSsoAsync(
        HttpContext context,
        XaiOAuthService service)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        var cancellationToken = context.RequestAborted;

        DecodeXaiSsoRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<DecodeXaiSsoRequest>(
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

        // `binding:"required"`: a missing or empty sso_token fails the bind.
        if (request is null || string.IsNullOrEmpty(request.SsoToken))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        XaiCredentialsResult result;
        try
        {
            result = service.DecodeSso(request.SsoToken, request.Proxy, cancellationToken);
        }
        catch (XaiOAuthException ex)
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

    /// <summary>The three xAI routes (routes.go:117-119).</summary>
    public static IEndpointRouteBuilder MapXaiOAuthRoutes(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/admin/xai/oauth/start", StartAsync);
        endpoints.MapPost("/admin/xai/oauth/exchange", ExchangeAsync);
        endpoints.MapPost("/admin/xai/oauth/sso", DecodeSsoAsync);
        return endpoints;
    }

    /// <summary>Port of api.ExchangeXAIOAuthRequest (xai.go:44-48).</summary>
    public sealed class ExchangeXaiOAuthRequest
    {
        [JsonPropertyName("session_id")]
        public string? SessionId { get; set; }

        [JsonPropertyName("callback_url")]
        public string? CallbackUrl { get; set; }

        [JsonPropertyName("proxy")]
        public ProxyConfig? Proxy { get; set; }
    }

    /// <summary>Port of api.DecodeXAISSORequest (xai.go:54-57).</summary>
    public sealed class DecodeXaiSsoRequest
    {
        [JsonPropertyName("sso_token")]
        public string? SsoToken { get; set; }

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
