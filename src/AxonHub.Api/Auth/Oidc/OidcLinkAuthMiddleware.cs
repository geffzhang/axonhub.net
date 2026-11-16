using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>Context key under which the link route's JWT gate stores the caller.</summary>
public static class OidcLinkAuth
{
    /// <summary>HttpContext.Items key holding the authenticated <see cref="UserRecord"/>.</summary>
    public const string ContextKey = "axonhub.oidc.link.user";
}

/// <summary>
/// Port of middleware.WithJWTAuth (internal/server/middleware/auth.go:82-118)
/// for <c>GET /admin/oidc/link/:provider</c> (routes.go:106,131): the plain
/// user-JWT gate of the admin group, without the API-key fallback of
/// WithAdminGraphqlAuth. The session scope and authz principal the reference
/// attaches have no ported consumers yet (C014).
/// </summary>
public sealed class OidcLinkAuthMiddleware(
    RequestDelegate next,
    ManagementAuthService auth)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ExtractAPIKeyFromRequest (Headers: Authorization, RequireBearer).
        var authorization = context.Request.Headers.Authorization;
        var headerValue = authorization.Count == 0 ? string.Empty : authorization[0] ?? string.Empty;
        if (headerValue.Length == 0)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, AdminGraphqlAuthMiddleware.ApiKeyRequired)
                .ConfigureAwait(false);
            return;
        }

        if (!headerValue.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, AdminGraphqlAuthMiddleware.MustStartWithBearer)
                .ConfigureAwait(false);
            return;
        }

        var token = GoStrings.TrimSpace(headerValue["Bearer ".Length..]);
        if (token.Length == 0)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, AdminGraphqlAuthMiddleware.BearerKeyRequired)
                .ConfigureAwait(false);
            return;
        }

        UserRecord user;
        try
        {
            user = await auth.AuthenticateJwtTokenAsync(token, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidJwtException)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, AdminGraphqlAuthMiddleware.InvalidToken)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await AbortAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    AdminGraphqlAuthMiddleware.FailedToValidateToken)
                .ConfigureAwait(false);
            return;
        }

        context.Items[OidcLinkAuth.ContextKey] = user;
        await next(context).ConfigureAwait(false);
    }

    private static Task AbortAsync(HttpContext context, int statusCode, string message)
        => AbortResponse.WriteErrorAsync(context, statusCode, message, context.RequestAborted);
}
