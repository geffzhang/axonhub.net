using AxonHub.Api.Management;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.WithOpenAPIAuth (internal/server/middleware/auth.go:125-177):
/// the OpenAPI GraphQL surface accepts only service_account API keys and
/// publishes the key principal so the privacy layer can enforce per-project,
/// scope-gated access for every query and mutation.
/// </summary>
/// <remarks>
/// Unlike <see cref="AdminGraphqlAuthMiddleware"/> there is no JWT fallback, the
/// key-type failure is a 401 (not 403), and the lookup failure reads
/// "Invalid API key". The reference also attaches a session scope and an authz
/// principal; the port records the caller identity/project and rebuilds the
/// principal on demand in <see cref="AxonHub.Api.Management.AuthorizationScope"/> (C014).
/// </remarks>
public sealed class OpenApiAuthMiddleware(
    RequestDelegate next,
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

    /// <summary>The reference's fixed literals in auth.go:139-157.</summary>
    public const string InvalidApiKey = "Invalid API key";
    public const string FailedToValidateApiKey = "Failed to validate API key";
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

        // AuthService.AuthenticateAPIKey (auth.go:137-146): a lookup or
        // validation failure is 401 Invalid API key, any other failure is 500.
        ApiKeyRecord apiKey;
        try
        {
            apiKey = await apiKeys
                .AuthenticateAdminKeyAsync(token, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (InvalidApiKeyException)
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, InvalidApiKey)
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
                    FailedToValidateApiKey)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(apiKey.Type, ServiceAccountType, StringComparison.Ordinal))
        {
            await AbortAsync(context, StatusCodes.Status401Unauthorized, InvalidApiKey)
                .ConfigureAwait(false);
            return;
        }

        // The reference checks the key type before the allowed_ips list
        // (auth.go:148-159).
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

        // Port of contexts.WithAPIKey + contexts.WithProjectID (auth.go:161-164):
        // the key and its project id feed the privacy rules and the model list.
        context.Items[RequestIdentity.HttpContextItemKey] = new RequestIdentity(
            apiKey.ProjectId,
            apiKey.Id)
        {
            ApiKey = apiKey,
        };

        await next(context).ConfigureAwait(false);
    }

    private static Task AbortAsync(HttpContext context, int statusCode, string message)
        => AbortResponse.WriteErrorAsync(context, statusCode, message, context.RequestAborted);
}
