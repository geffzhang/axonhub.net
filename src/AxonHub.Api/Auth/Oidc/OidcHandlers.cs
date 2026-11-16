using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of api.OIDCHandlers (internal/server/api/oidc.go). This slice covers
/// <c>GET /oauth/oidc/providers</c> (the login page's provider list); the
/// authorize/callback/exchange routes follow in the login-flow slice.
/// </summary>
public static class OidcHandlers
{
    private static readonly JsonSerializerOptions ResponseJson = new();

    /// <summary>Port of <c>OIDCHandlers.GetProviders</c> (oidc.go:57-71).</summary>
    public static async Task GetProvidersAsync(
        HttpContext context,
        OidcService oidc,
        ManagementAuthService auth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(oidc);
        ArgumentNullException.ThrowIfNull(auth);

        // The reference extracts the bearer token with strings.CutPrefix and
        // silently ignores an invalid token: the provider list stays anonymous.
        long? userId = null;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var token = authorization["Bearer ".Length..];
            try
            {
                var user = await auth.AuthenticateJwtTokenAsync(token, cancellationToken)
                    .ConfigureAwait(false);
                userId = user.Id;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Invalid JWTs do not fail the provider listing.
            }
        }

        var providers = await oidc.GetProvidersAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new ProvidersResponse(providers.Count == 0 ? null : providers),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int statusCode,
        T value,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                value,
                ResponseJson,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record ProvidersResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<OidcProviderInfo>? Data);
}
