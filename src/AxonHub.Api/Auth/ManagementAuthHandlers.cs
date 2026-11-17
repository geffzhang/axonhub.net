using System.Text.Json;
using System.Text.RegularExpressions;
using AxonHub.Api.Management;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Auth;

/// <summary>
/// Ports AuthHandlers.SignIn / AuthHandlers.Refresh
/// (internal/server/api/auth.go): the two unauthenticated admin endpoints in
/// routes.go:97-98.
/// </summary>
public static partial class ManagementAuthHandlers
{
    private static readonly JsonSerializerOptions RequestJson =
        new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, Exception?> LogInvalidRefreshAuthorization =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1010, "AdminAuthRefreshInvalidAuthorization"),
            "Admin auth refresh rejected: missing or malformed Authorization header");

    private static readonly Action<ILogger, Exception?> LogEmptyRefreshCredential =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(1011, "AdminAuthRefreshEmptyCredential"),
            "Admin auth refresh rejected: empty bearer credential");

    private static readonly Action<ILogger, string, Exception?> LogRefreshInvalidJwt =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1012, "AdminAuthRefreshInvalidJwt"),
            "Admin auth refresh rejected invalid JWT (cause type: {CauseType})");

    private static readonly Action<ILogger, string, string, Exception?> LogRefreshFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Error,
            new EventId(1013, "AdminAuthRefreshFailed"),
            "Admin auth refresh failed (exception type: {ExceptionType}, inner exception type: {InnerExceptionType})");

    /// <summary>Port of AuthHandlers.SignIn.</summary>
    public static async Task SignInAsync(
        HttpContext context,
        ManagementAuthService auth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);

        SignInRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<SignInRequest>(
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
                    "Invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // gin's ShouldBindJSON: email is required + email, password is required.
        if (request is null
            || string.IsNullOrEmpty(request.Email)
            || !EmailRegex().IsMatch(request.Email)
            || string.IsNullOrEmpty(request.Password))
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        UserRecordDto user;
        string token;
        try
        {
            var authenticated = await auth.AuthenticateUserAsync(
                    request.Email,
                    request.Password,
                    cancellationToken)
                .ConfigureAwait(false);

            var info = await auth.GetSignInUserInfoAsync(authenticated, cancellationToken)
                .ConfigureAwait(false);
            user = UserRecordDto.From(info);

            token = await auth.GenerateJwtTokenAsync(authenticated, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidPasswordException)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid email or password",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Internal server error",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        await context.Response.WriteAsJsonAsync(
                new { user, token },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of AuthHandlers.Refresh.</summary>
    public static async Task RefreshAsync(
        HttpContext context,
        ManagementAuthService auth,
        ILogger<ManagementAuthService> logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(logger);

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            LogInvalidRefreshAuthorization(logger, null);
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid authorization token",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            LogEmptyRefreshCredential(logger, null);
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid authorization token",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        JwtRefreshResult result;
        try
        {
            result = await auth.RefreshJwtTokenAsync(
                    token,
                    DateTimeOffset.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidJwtException exception)
        {
            LogRefreshInvalidJwt(
                logger,
                exception.InnerException?.GetType().Name ?? "invalid claims",
                null);
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Invalid authorization token",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogRefreshFailed(
                logger,
                exception.GetType().Name,
                exception.InnerException?.GetType().Name ?? "none",
                null);
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Internal server error",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!result.Renewed)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await context.Response.WriteAsJsonAsync(
                new { token = result.Token },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of api.SignInRequest.</summary>
    public sealed record SignInRequest(string? Email, string? Password);

    // go-playground/validator's email tag requires a local part and a
    // dotted hostname (IP literals and quoted locals are not reproduced; see the
    // sign-in difference entry). Shared with the invitation registration
    // binding, whose request carries the same `binding:"required,email"` tag.
    [GeneratedRegex(
        @"^[a-zA-Z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(?:\.[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)+$",
        RegexOptions.CultureInvariant)]
    internal static partial Regex EmailRegex();

    /// <summary>objects.UserInfo's JSON shape (internal/objects/user.go).</summary>
    internal sealed record UserRecordDto(
        UserIdDto Id,
        string Email,
        string FirstName,
        string LastName,
        bool IsOwner,
        string PreferLanguage,
        string Avatar,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<RoleDto> Roles,
        IReadOnlyList<ProjectDto> Projects,
        IReadOnlyList<OidcIdentityDto> OidcIdentities,
        bool HasPassword)
    {
        public static UserRecordDto From(UserProfileInfo info)
        {
            ArgumentNullException.ThrowIfNull(info);

            return new UserRecordDto(
                new UserIdDto("User", info.Id),
                info.Email,
                info.FirstName,
                info.LastName,
                info.IsOwner,
                info.PreferLanguage,
                info.Avatar,
                info.Scopes,
                [.. info.Roles.Select(static role => new RoleDto(role.Name))],
                [.. info.Projects.Select(static project => new ProjectDto(
                    new ProjectIdDto("Project", project.ProjectId),
                    project.IsOwner,
                    project.Scopes,
                    project.EffectiveScopes,
                    [.. project.Roles.Select(static role => new RoleDto(role.Name))]))],
                [.. info.OidcIdentities.Select(static identity => new OidcIdentityDto(
                    new OidcIdentityIdDto("OIDCIdentity", identity.Id),
                    identity.IdpName,
                    identity.Issuer,
                    identity.Subject,
                    identity.Email))],
                info.HasPassword);
        }
    }

    /// <summary>objects.GUID's JSON shape.</summary>
    internal sealed record UserIdDto(string Type, long Id);

    internal sealed record ProjectIdDto(string Type, long Id);

    internal sealed record OidcIdentityIdDto(string Type, long Id);

    internal sealed record RoleDto(string Name);

    internal sealed record ProjectDto(
        ProjectIdDto ProjectID,
        bool IsOwner,
        IReadOnlyList<string> Scopes,
        IReadOnlyList<string> EffectiveScopes,
        IReadOnlyList<RoleDto> Roles);

    internal sealed record OidcIdentityDto(
        OidcIdentityIdDto Id,
        string IdpName,
        string Issuer,
        string Subject,
        string Email);
}
