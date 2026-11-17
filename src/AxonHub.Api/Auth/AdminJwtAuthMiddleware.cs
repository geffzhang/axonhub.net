using AxonHub.Api.Management;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.WithJWTAuth (internal/server/middleware/auth.go:82-118)
/// for <c>POST /admin/invitations</c> (routes.go:106,112): the plain user-JWT
/// gate of the admin group, without the API-key fallback of
/// WithAdminGraphqlAuth. The reference attaches the session scope and authz
/// principal; neither has ported consumers (C014). The caller is published as
/// an <see cref="AdminGraphqlIdentity"/> so <see cref="ProjectIdMiddleware"/>
/// and <see cref="HttpContextManagementIdentity"/> read it exactly like the
/// GraphQL surface.
/// </summary>
public sealed class AdminJwtAuthMiddleware(
    RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ManagementAuthService auth)
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

        context.Items[AdminGraphqlIdentity.ContextKey] = new AdminGraphqlIdentity(
            new ManagementUser(user.Id, user.IsOwner),
            ApiKeyId: null,
            ProjectId: null,
            IsApiKey: false);

        await next(context).ConfigureAwait(false);
    }

    private static Task AbortAsync(HttpContext context, int statusCode, string message)
        => AbortResponse.WriteErrorAsync(context, statusCode, message, context.RequestAborted);
}
