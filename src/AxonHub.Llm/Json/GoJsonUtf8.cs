using System.Buffers;
using System.Text;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of the UTF-8 repair Go's <c>json.Unmarshal</c> performs while unquoting
/// string literals.
/// </summary>
/// <remarks>
/// <para>
/// Go never rejects invalid UTF-8 inside a JSON string. Unquoting decodes each
/// sequence with <c>utf8.DecodeRune</c> and writes <c>utf8.RuneError</c>
/// (U+FFFD) for every ill-formed byte; a lone surrogate escape
/// (<c>\ud800</c>) is repaired the same way, while a high/low surrogate pair
/// decodes to one astral rune. System.Text.Json rejects both cases, so bodies
/// are rewritten before decoding: invalid bytes and invalid surrogate escapes
/// inside string literals become U+FFFD, and nothing else is touched.
/// </para>
/// <para>
/// Invalid bytes outside a string literal are structural errors and are left
/// for <see cref="GoJsonSyntax"/> to report with the original byte.
/// </para>
/// <para>
/// Residual difference (C008): Go copies <c>json.RawMessage</c> fragments byte
/// for byte, so ill-formed bytes inside them survive a marshal round-trip.
/// Raw fragments here are <c>JsonElement</c>s, which cannot hold invalid
/// UTF-8, so those bytes are repaired in fragments too.
/// </para>
/// </remarks>
public static class GoJsonUtf8
{
    private static readonly byte[] Replacement = [0xEF, 0xBF, 0xBD];

    /// <summary>
    /// Returns a repaired copy of <paramref name="json"/>, or
    /// <see langword="null"/> when every string literal is already valid UTF-8
    /// with well-formed surrogate escapes.
    /// </summary>
    public static byte[]? SanitizeStringLiterals(ReadOnlySpan<byte> json)
    {
        ArrayBufferWriter<byte>? dst = null;
        var copied = 0;
        var inString = false;
        var i = 0;

        while (i < json.Length)
        {
            var b = json[i];

            if (!inString)
            {
                inString = b == (byte)'"';
                i++;
                continue;
            }

            if (b == (byte)'"')
            {
                inString = false;
                i++;
                continue;
            }

            if (b == (byte)'\\')
            {
                if (i + 6 <= json.Length
                    && json[i + 1] == (byte)'u'
                    && TryReadHex4(json.Slice(i + 2, 4), out var code))
                {
                    if (IsHighSurrogate(code))
                    {
                        // A valid pair decodes to the same rune on both sides;
                        // anything else makes Go emit one replacement rune and
                        // then process the following escape on its own.
                        if (IsLowSurrogateEscapeAt(json, i + 6))
                        {
                            i += 12;
                            continue;
                        }
                    }
                    else if (!IsLowSurrogate(code))
                    {
                        i += 6;
                        continue;
                    }

                    dst ??= new ArrayBufferWriter<byte>(json.Length + 3);
                    dst.Write(json[copied..i]);
                    dst.Write(Replacement);
                    i += 6;
                    copied = i;
                    continue;
                }

                // Not a complete \uXXXX escape: invalid JSON, left for the
                // syntax scanner to reject.
                i += 2;
                continue;
            }

            if (b < 0x80)
            {
                i++;
                continue;
            }

            if (Rune.DecodeFromUtf8(json[i..], out _, out var consumed) == OperationStatus.Done)
            {
                i += consumed;
                continue;
            }

            // Go's utf8.DecodeRune reports every byte of an ill-formed sequence
            // as RuneError with size 1, so both sides emit one replacement per
            // byte (an encoded U+FFFD is valid and is copied unchanged).
            dst ??= new ArrayBufferWriter<byte>(json.Length + 3);
            dst.Write(json[copied..i]);
            dst.Write(Replacement);
            i++;
            copied = i;
        }

        if (dst is null)
        {
            return null;
        }

        dst.Write(json[copied..]);
        return dst.WrittenSpan.ToArray();
    }

    private static bool IsHighSurrogate(int code) => code is >= 0xD800 and <= 0xDBFF;

    private static bool IsLowSurrogate(int code) => code is >= 0xDC00 and <= 0xDFFF;

    private static bool IsLowSurrogateEscapeAt(ReadOnlySpan<byte> json, int i)
        => i + 6 <= json.Length
            && json[i] == (byte)'\\'
            && json[i + 1] == (byte)'u'
            && TryReadHex4(json.Slice(i + 2, 4), out var code)
            && IsLowSurrogate(code);

    private static bool TryReadHex4(ReadOnlySpan<byte> span, out int value)
    {
        value = 0;
        foreach (var b in span)
        {
            int digit;
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                digit = b - '0';
            }
            else if (b >= (byte)'a' && b <= (byte)'f')
            {
                digit = b - 'a' + 10;
            }
            else if (b >= (byte)'A' && b <= (byte)'F')
            {
                digit = b - 'A' + 10;
            }
            else
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }
}
