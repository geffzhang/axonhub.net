using AxonHub.Api.Management;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.WithAdminGraphqlAuth
/// (internal/server/middleware/auth.go:179-260): the admin GraphQL surface
/// accepts a user JWT (the admin UI's credential) or a service_account API key
/// so automation can read management data. The reference runs this middleware
/// ahead of the gqlgen handler on POST /admin/graphql; the port runs the same
/// decision tree ahead of MapGraphQL.
/// </summary>
/// <remarks>
/// The reference also attaches a session scope and an authz principal to the
/// context. The port records the caller identity/project and rebuilds the
/// principal on demand in <see cref="AxonHub.Api.Management.AuthorizationScope"/> (C014);
/// the session scope still has no ported consumer.
/// </remarks>
public sealed class AdminGraphqlAuthMiddleware(
    RequestDelegate next,
    ManagementAuthService auth,
    IApiKeyAuthenticator apiKeys,
    ApiKeyAuthOptions options)
{
    /// <summary>middleware.ErrAPIKeyRequired.</summary>
    public const string ApiKeyRequired = "API key is required";

    /// <summary>biz.ErrInvalidToken + ": Authorization header must start with 'Bearer '".</summary>
    public const string MustStartWithBearer =
        "invalid token: Authorization header must start with 'Bearer '";

    /// <summary>biz.ErrInvalidToken + ": API key is required".</summary>
    public const string BearerKeyRequired = "invalid token: API key is required";

    /// <summary>The reference's fixed literals in auth.go:223-239.</summary>
    public const string InvalidToken = "Invalid token";
    public const string FailedToValidateToken = "Failed to validate token";
    public const string ServiceAccountOnly =
        "Only service account API keys may access the admin GraphQL API";
    public const string IpNotAllowed = "IP address is not allowed for this API key";

    /// <summary>apikey.TypeServiceAccount.</summary>
    public const string ServiceAccountType = "service_account";

    public async Task InvokeAsync(HttpContext context)
    {
        // ExtractAPIKeyFromRequest with Headers: ["Authorization"],
        // RequireBearer: true (middleware/header.go:57-122). Header.Get in Go
        // returns the first line, so only the first value is inspected.
        var authorization = context.Request.Headers.Authorization;
        var headerValue = authorization.Count == 0 ? string.Empty : authorization[0] ?? string.Empty;
        if (headerValue.Length == 0)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, ApiKeyRequired)
                .ConfigureAwait(false);
            return;
        }

        if (!headerValue.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, MustStartWithBearer)
                .ConfigureAwait(false);
            return;
        }

        var token = headerValue["Bearer ".Length..];
        if (GoStrings.TrimSpace(token).Length == 0)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, BearerKeyRequired)
                .ConfigureAwait(false);
            return;
        }

        token = GoStrings.TrimSpace(token);

        // JWT first: only a token that fails JWT parsing falls through to API
        // key auth. A server-side JWT failure (for example an unreadable
        // secret key) is not an API key concern, so report it as such instead
        // of a bogus 401 (auth.go:212-218).
        try
        {
            var user = await auth
                .AuthenticateJwtTokenAsync(token, context.RequestAborted)
                .ConfigureAwait(false);

            context.Items[AdminGraphqlIdentity.ContextKey] = new AdminGraphqlIdentity(
                new ManagementUser(user.Id, user.IsOwner),
                ApiKeyId: null,
                ProjectId: null,
                IsApiKey: false);
            await next(context).ConfigureAwait(false);
            return;
        }
        catch (InvalidJwtException)
        {
            // Fall through to API-key authentication.
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
                    FailedToValidateToken)
                .ConfigureAwait(false);
            return;
        }

        // AuthService.AuthenticateAPIKey (auth.go:220-229): a lookup or
        // validation failure is 401 Invalid token, any other failure is 500.
        ApiKeyRecord apiKey;
        try
        {
            apiKey = await apiKeys
                .AuthenticateAdminKeyAsync(token, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidApiKeyException)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, InvalidToken)
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
                    FailedToValidateToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(apiKey.Type, ServiceAccountType, StringComparison.Ordinal))
        {
            await AbortAsync(context, StatusCodes.Status403Forbidden, ServiceAccountOnly)
                .ConfigureAwait(false);
            return;
        }

        // The reference checks the key type before the allowed_ips list
        // (auth.go:231-242).
        try
        {
            ApiKeyAuthenticator.CheckAllowedIps(context.Request, apiKey, options.TrustedProxies);
        }
        catch (ClientIpForbiddenException)
        {
            await AbortAsync(context, StatusCodes.Status403Forbidden, IpNotAllowed)
                .ConfigureAwait(false);
            return;
        }

        context.Items[AdminGraphqlIdentity.ContextKey] = new AdminGraphqlIdentity(
            User: null,
            ApiKeyId: apiKey.Id,
            ProjectId: apiKey.ProjectId,
            IsApiKey: true,
            ApiKey: apiKey);
        await next(context).ConfigureAwait(false);
    }

    private static Task AbortAsync(HttpContext context, int statusCode, string message)
        => AbortResponse.WriteErrorAsync(context, statusCode, message, context.RequestAborted);
}
