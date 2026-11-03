namespace AxonHub.Api.Auth;

/// <summary>
/// Typed counterparts of the sentinel errors in internal/server/biz/errors.go
/// that biz.AuthService wraps. Handlers classify on the type instead of
/// matching strings, the way the reference uses errors.Is.
/// </summary>
public sealed class InvalidPasswordException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Port of biz.ErrInvalidJWT wrappers.</summary>
public sealed class InvalidJwtException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Port of biz.ErrOIDCLoginRequired.</summary>
public sealed class OidcLoginRequiredException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>Port of the sentinel error messages in internal/server/biz/errors.go.</summary>
public static class ManagementAuthErrors
{
    public const string InvalidPassword = "invalid password";
    public const string InvalidJwt = "invalid jwt token";
    public const string Internal = "server internal error, please try again later";
    public const string OidcLoginRequired =
        "OIDC user without password, please login via OIDC or set a password";
    public const string SystemNotInitialized = "system not initialized";
}
