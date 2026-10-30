using System.Text;

namespace AxonHub.Llm.Transformer.Shared;

/// <summary>Port of shared.SignatureProvider (llm/transformer/shared/signature.go).</summary>
public enum SignatureProvider
{
    Unknown,
    Anthropic,
    Gemini,
    OpenAI,
}

/// <summary>Port of shared.GuessResult. The reasons are diagnostic only.</summary>
public sealed record SignatureGuess(SignatureProvider Provider, IReadOnlyList<string> Reasons);

/// <summary>
/// Port of llm/transformer/shared/base64.go, signature.go and anthropic.go:
/// provider signature detection and the base64 normalization the Anthropic
/// transformers apply to thinking signatures.
/// </summary>
public static class AnthropicSignatures
{
    /// <summary>
    /// Port of shared.EnsureBase64Encoding: returns the input when it already
    /// decodes as standard base64, otherwise base64-encodes the UTF-8 bytes.
    /// </summary>
    public static string EnsureBase64Encoding(string str)
        => TryDecodeStdBase64(str, out _, out _)
            ? str
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(str));

    /// <summary>Port of shared.EncodeAnthropicSignature.</summary>
    public static string? EncodeAnthropicSignature(string? signature)
        => signature is null ? null : EnsureBase64Encoding(signature);

    /// <summary>
    /// Port of shared.DecodeAnthropicSignature: returns the blob only when the
    /// heuristic recognizes Anthropic, so foreign provider signatures are
    /// dropped rather than sent upstream as thinking signatures.
    /// </summary>
    public static string? DecodeAnthropicSignature(string? signature)
        => signature is not null && GuessSignatureProvider(signature).Provider == SignatureProvider.Anthropic
            ? signature
            : null;

    /// <summary>Port of shared.GuessSignatureProvider.</summary>
    public static SignatureGuess GuessSignatureProvider(string raw)
    {
        var s = raw.Trim();
        s = s.Trim('"');
        s = s.Trim();

        var reasons = new List<string>(3);

        // OpenAI encrypted_content common pattern: gAAAA... (base64url Fernet-like).
        if (s.StartsWith("gAAAA", StringComparison.Ordinal) || s.StartsWith("gAAA", StringComparison.Ordinal))
        {
            reasons.Add("starts with gAAA*, commonly seen in OpenAI reasoning.encrypted_content");
            return new SignatureGuess(SignatureProvider.OpenAI, reasons);
        }

        if (IsStdBase64String(s))
        {
            var decoded = DecodeStdBase64AllowUnpadded(s);
            if (decoded is not null && ContainsAnthropicModel(decoded))
            {
                reasons.Add("standard base64 decoded bytes contain an Anthropic model marker");
                return new SignatureGuess(SignatureProvider.Anthropic, reasons);
            }

            // Valid standard base64 is never OpenAI — OpenAI uses base64url.
            if (decoded is not null && LooksLikeProto(decoded))
            {
                reasons.Add(
                    "standard base64 and decoded bytes look protobuf-like, which fits Gemini thoughtSignature");
                return new SignatureGuess(SignatureProvider.Gemini, reasons);
            }

            reasons.Add("standard base64 without known provider prefix");
            return new SignatureGuess(SignatureProvider.Unknown, reasons);
        }

        reasons.Add("not a recognized base64/base64url thinking signature shape");
        return new SignatureGuess(SignatureProvider.Unknown, reasons);
    }

    /// <summary>
    /// Port of decodeStdBase64: padded and unpadded standard base64 are both
    /// accepted when the string carries no padding character.
    /// </summary>
    private static byte[]? DecodeStdBase64AllowUnpadded(string s)
        => s.Contains('=', StringComparison.Ordinal)
            ? (TryDecodeStdBase64(s, out var padded, out _) ? padded : null)
            : (TryDecodeStdBase64(s + new string('=', (4 - (s.Length % 4)) % 4), out var raw, out _) ? raw : null);

    /// <summary>
    /// Go <c>base64.StdEncoding.DecodeString</c>: standard alphabet, padding
    /// required except for the ignored CR/LF bytes. Unlike
    /// <see cref="Convert.FromBase64String(string)"/> it does not ignore other
    /// whitespace, so the shape is validated before decoding.
    /// </summary>
    private static bool TryDecodeStdBase64(string str, out byte[] decoded, out int errorIndex)
    {
        decoded = [];
        errorIndex = -1;

        var filtered = new StringBuilder(str.Length);
        foreach (var c in str)
        {
            if (c is '\r' or '\n')
            {
                continue;
            }

            filtered.Append(c);
        }

        var text = filtered.ToString();
        if (text.Length % 4 != 0)
        {
            errorIndex = 0;
            return false;
        }

        var paddingStarted = false;
        var paddingCount = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
            {
                if (paddingStarted)
                {
                    errorIndex = i;
                    return false;
                }

                continue;
            }

            if (c == '=')
            {
                if (i < text.Length - 2)
                {
                    // '=' may only appear in the final two positions.
                    errorIndex = i;
                    return false;
                }

                paddingStarted = true;
                paddingCount++;
                if (paddingCount > 2)
                {
                    errorIndex = i;
                    return false;
                }

                continue;
            }

            errorIndex = i;
            return false;
        }

        try
        {
            decoded = Convert.FromBase64String(text);
            return true;
        }
        catch (FormatException)
        {
            errorIndex = 0;
            return false;
        }
    }

    /// <summary>Port of containsAnthropicModel (ASCII-lowercased substring search).</summary>
    private static bool ContainsAnthropicModel(byte[] buffer)
    {
        var lower = new byte[buffer.Length];
        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];
            lower[i] = b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + ('a' - 'A')) : b;
        }

        return IndexOf(lower, "claude"u8) >= 0 || IndexOf(lower, "anthropic"u8) >= 0;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return needle.Length == 0 ? 0 : -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Port of isStdBase64String: <c>[A-Za-z0-9+/]+={0,2}</c>, padding only at
    /// the end.
    /// </summary>
    private static bool IsStdBase64String(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        var paddingStarted = false;
        var paddingCount = 0;
        foreach (var c in s)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
            {
                if (paddingStarted)
                {
                    return false;
                }

                continue;
            }

            if (c == '=')
            {
                paddingStarted = true;
                paddingCount++;
                if (paddingCount > 2)
                {
                    return false;
                }

                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Port of looksLikeProto: walk as many protobuf fields as possible,
    /// rejecting invalid wire types, field number 0, and out-of-bounds
    /// length-delimited fields.
    /// </summary>
    private static bool LooksLikeProto(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return false;
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var (tag, tagLength) = ReadVarint64(buffer[offset..]);
            if (tagLength == 0)
            {
                return offset > 0;
            }

            offset += tagLength;

            var wireType = (int)(tag & 0x07);
            var fieldNumber = tag >> 3;

            if (fieldNumber == 0)
            {
                return false;
            }

            // Deprecated group wire types.
            if (wireType is 3 or 4)
            {
                return false;
            }

            switch (wireType)
            {
                case 0: // varint
                    var (_, valueLength) = ReadVarint64(buffer[offset..]);
                    if (valueLength == 0)
                    {
                        return false;
                    }

                    offset += valueLength;
                    break;
                case 1: // 64-bit
                    if (offset + 8 > buffer.Length)
                    {
                        return false;
                    }

                    offset += 8;
                    break;
                case 2: // length-delimited
                    var (length, lengthLength) = ReadVarint64(buffer[offset..]);
                    if (lengthLength == 0 || length > (ulong)(buffer.Length - offset - lengthLength))
                    {
                        return false;
                    }

                    offset += lengthLength + (int)length;
                    break;
                case 5: // 32-bit
                    if (offset + 4 > buffer.Length)
                    {
                        return false;
                    }

                    offset += 4;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    /// <summary>Port of readVarint64; returns (0, 0) on invalid input.</summary>
    private static (ulong Value, int Length) ReadVarint64(ReadOnlySpan<byte> buffer)
    {
        ulong result = 0;
        for (var i = 0; i < buffer.Length && i < 10; i++)
        {
            result |= (ulong)(buffer[i] & 0x7F) << (i * 7);
            if ((buffer[i] & 0x80) == 0)
            {
                return (result, i + 1);
            }
        }

        return (0, 0);
    }
}
