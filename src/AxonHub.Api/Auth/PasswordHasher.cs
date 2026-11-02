using System.Text;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of HashPassword / VerifyPassword (internal/server/biz/auth.go:22-56).
/// The reference stores <c>hex(bcrypt(password))</c> with the Go default cost
/// 10; the OIDC-only placeholder is stored verbatim and never hashed.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Port of biz.HashPassword.</summary>
    string Hash(string password);

    /// <summary>
    /// Port of biz.VerifyPassword. Throws <see cref="InvalidOperationException"/>
    /// with the reference's message when the hash cannot be decoded or does not
    /// match.
    /// </summary>
    void Verify(string hashedPassword, string password);
}

/// <inheritdoc cref="IPasswordHasher"/>
public sealed class PasswordHasher : IPasswordHasher
{
    /// <summary>biz.OIDC_ONLY_PLACEHOLDER.</summary>
    public const string OidcOnlyPlaceholder = "!OIDC_SSO_ONLY!";

    /// <summary>golang.org/x/crypto/bcrypt.DefaultCost.</summary>
    public const int DefaultCost = 10;

    /// <summary>bcrypt.ErrPasswordTooLong.</summary>
    private const string PasswordTooLong = "bcrypt: password length exceeds 72 bytes";

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password == OidcOnlyPlaceholder)
        {
            return OidcOnlyPlaceholder;
        }

        if (Encoding.UTF8.GetByteCount(password) > 72)
        {
            throw new InvalidOperationException("failed to hash password: " + PasswordTooLong);
        }

        // Go's bcrypt always emits the "$2a$" revision; BCrypt.Net-Next defaults
        // to "$2b$", so request the revision explicitly and hex-encode the
        // ASCII hash exactly like hex.EncodeToString.
        var salt = BCrypt.Net.BCrypt.GenerateSalt(DefaultCost, 'a');
        var hashed = BCrypt.Net.BCrypt.HashPassword(password, salt);
        return Convert.ToHexStringLower(Encoding.ASCII.GetBytes(hashed));
    }

    public void Verify(string hashedPassword, string password)
    {
        ArgumentNullException.ThrowIfNull(hashedPassword);
        ArgumentNullException.ThrowIfNull(password);

        if (hashedPassword == OidcOnlyPlaceholder)
        {
            // biz.ErrOIDCLoginRequired
            throw new InvalidOperationException(
                "OIDC user without password, please login via OIDC or set a password");
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromHexString(hashedPassword);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "failed to decode hashed password: encoding/hex: invalid byte");
        }

        if (Encoding.UTF8.GetByteCount(password) > 72)
        {
            throw new InvalidOperationException(PasswordTooLong);
        }

        var hash = Encoding.ASCII.GetString(decoded);
        if (!BCrypt.Net.BCrypt.Verify(password, hash))
        {
            // bcrypt.ErrMismatchedHashAndPassword
            throw new InvalidOperationException(
                "crypto/bcrypt: hashedPassword is not the hash of the given password");
        }
    }
}
