using System.Text;

namespace AxonHub.Llm.Transformer.Shared;

/// <summary>Port of the <c>net/url</c> escaping transformers rely on.</summary>
public static class GoUrl
{
    /// <summary>
    /// Port of <c>url.PathEscape</c> (Go's <c>encodePathSegment</c> mode):
    /// unreserved characters and the reserved set <c>$&amp;+:=@</c> stay literal,
    /// <c>/ ; , ?</c> and everything else are percent-encoded by UTF-8 byte.
    /// </summary>
    public static string PathEscape(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);

        foreach (var b in bytes)
        {
            if (ShouldEscape(b))
            {
                builder.Append('%');
                builder.Append(UpperHex[b >> 4]);
                builder.Append(UpperHex[b & 0xF]);
            }
            else
            {
                builder.Append((char)b);
            }
        }

        return builder.ToString();
    }

    private const string UpperHex = "0123456789ABCDEF";

    private static bool ShouldEscape(byte b)
    {
        // §2.3 Unreserved characters (alphanumerics and mark).
        if (b is >= (byte)'a' and <= (byte)'z'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
        {
            return false;
        }

        // §2.2 Reserved characters: path segments keep only these literal.
        if (b is (byte)'$' or (byte)'&' or (byte)'+' or (byte)',' or (byte)'/'
            or (byte)':' or (byte)';' or (byte)'=' or (byte)'?' or (byte)'@')
        {
            return b is (byte)'/' or (byte)';' or (byte)',' or (byte)'?';
        }

        // Everything else, including non-ASCII bytes, must be escaped.
        return true;
    }
}
