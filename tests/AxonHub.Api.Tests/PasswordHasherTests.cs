using System.Text;
using AxonHub.Api.Auth;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.HashPassword / biz.VerifyPassword (internal/server/biz/auth.go:22-56):
/// hex(bcrypt(pw, cost 10, "$2a$")) and the reference error strings.
/// </summary>
public sealed class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void HashProducesHexEncodedBcryptWithTheGoRevisionAndCost()
    {
        var hashed = _hasher.Hash("s3cret");

        // 60 ASCII bytes of bcrypt become 120 lowercase hex characters.
        Assert.Equal(120, hashed.Length);
        Assert.Matches("^[0-9a-f]+$", hashed);

        var decoded = Encoding.ASCII.GetString(Convert.FromHexString(hashed));
        Assert.StartsWith("$2a$10$", decoded);

        _hasher.Verify(hashed, "s3cret");
    }

    [Fact]
    public void HashIsSaltedPerCall()
    {
        Assert.NotEqual(_hasher.Hash("s3cret"), _hasher.Hash("s3cret"));
    }

    [Fact]
    public void VerifyRejectsAWrongPasswordWithTheBcryptError()
    {
        var hashed = _hasher.Hash("s3cret");

        var error = Assert.Throws<InvalidOperationException>(
            () => _hasher.Verify(hashed, "wrong"));

        Assert.Equal(
            "crypto/bcrypt: hashedPassword is not the hash of the given password",
            error.Message);
    }

    [Fact]
    public void VerifyRejectsNonHexHashesWithTheHexError()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => _hasher.Verify("not-hex!", "s3cret"));

        Assert.Equal(
            "failed to decode hashed password: encoding/hex: invalid byte",
            error.Message);
    }

    [Fact]
    public void TheOidcPlaceholderIsStoredVerbatimAndNeverVerifies()
    {
        Assert.Equal(
            PasswordHasher.OidcOnlyPlaceholder,
            _hasher.Hash(PasswordHasher.OidcOnlyPlaceholder));

        var error = Assert.Throws<InvalidOperationException>(
            () => _hasher.Verify(PasswordHasher.OidcOnlyPlaceholder, "anything"));

        Assert.Equal(
            "OIDC user without password, please login via OIDC or set a password",
            error.Message);
    }

    [Fact]
    public void PasswordsLongerThan72BytesAreRejected()
    {
        var seventyThreeAscii = new string('a', 73);
        var error = Assert.Throws<InvalidOperationException>(() => _hasher.Hash(seventyThreeAscii));
        Assert.Equal(
            "failed to hash password: bcrypt: password length exceeds 72 bytes",
            error.Message);

        // The reference counts UTF-8 bytes, not characters: 24 four-byte
        // runes are 96 bytes and also exceed the limit.
        var seventyTwoBytes = new string('a', 72);
        _hasher.Verify(_hasher.Hash(seventyTwoBytes), seventyTwoBytes);

        var multibyte = string.Concat(Enumerable.Repeat("😀", 19));
        var multibyteError = Assert.Throws<InvalidOperationException>(
            () => _hasher.Hash(multibyte));
        Assert.Equal(
            "failed to hash password: bcrypt: password length exceeds 72 bytes",
            multibyteError.Message);
    }
}
