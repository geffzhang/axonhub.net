using System.Globalization;
using System.Text;
using Path = System.IO.Path;
using AxonHub.Api.Auth;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Management;

/// <summary>
/// Shared HTTP half of the two request-detail routes (routes.go:145-152):
/// the project gate, gin's <c>ShouldBindUri</c> of <c>:request_id</c>, the
/// reference's unscoped request lookup and the filepath helpers
/// request_content.go:99-159 uses to turn a storage key into a download.
/// </summary>
internal static class AdminRequestEndpoints
{
    public const string MissingProjectId = "Project ID not found in context";
    public const string RequestNotFound = "Request not found";
    public const string FailedToLoadRequest = "Failed to load request";

    /// <summary>
    /// Port of <c>contexts.GetProjectID</c> + the handler's guard: without an
    /// X-Project-ID header the admin group never publishes a project, so the
    /// request fails with the reference's 400.
    /// </summary>
    public static async Task<long?> RequireProjectIdAsync(
        HttpContext context,
        IManagementIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);

        var projectId = identity.GetCurrentProjectId();
        if (projectId is null or <= 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, MissingProjectId)
                .ConfigureAwait(false);
            return null;
        }

        return projectId;
    }

    /// <summary>
    /// Port of the shared request lookup in request_content.go:54-72 and
    /// request_live.go:111-129: an unscoped <c>ent.Request.Get</c> followed by
    /// the project comparison (a foreign request is indistinguishable from a
    /// missing one).
    /// </summary>
    public static async Task<RequestRecord?> ResolveRequestAsync(
        HttpContext context,
        string? rawRequestId,
        long projectId,
        IRequestStore requests)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requests);

        if (!TryParseRequestId(rawRequestId, out var requestId, out var bindError))
        {
            await WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "Invalid request body: " + bindError)
                .ConfigureAwait(false);
            return null;
        }

        RequestRecord? request;
        try
        {
            request = await requests.GetByIdAsync(requestId, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    FailedToLoadRequest)
                .ConfigureAwait(false);
            return null;
        }

        if (request is null || request.ProjectId != projectId)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, RequestNotFound)
                .ConfigureAwait(false);
            return null;
        }

        return request;
    }

    /// <summary>
    /// Port of gin's <c>ShouldBindUri</c> for <c>RequestID int</c>: an empty
    /// value becomes 0 (setIntField), and a non-numeric value reports the
    /// strconv.ParseInt error the handler wraps in "Invalid request body:".
    /// </summary>
    public static bool TryParseRequestId(string? raw, out long requestId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            requestId = 0;
            return true;
        }

        if (GoCast.TryParseInt64(raw, out requestId))
        {
            return true;
        }

        error = "strconv.ParseInt: parsing " + GoCast.GoQuote(raw) + ": "
            + (IsDecimal(raw) ? "value out of range" : "invalid syntax");
        return false;
    }

    public static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        ArgumentNullException.ThrowIfNull(context);
        return AbortResponse.WriteErrorAsync(
            context,
            statusCode,
            message,
            context.RequestAborted);
    }

    /// <summary>Port of <c>filenameFromKey</c> (request_content.go:151-159).</summary>
    public static string FilenameFromKey(string key, long requestId)
    {
        var filename = GoFilepath.Base(key);
        return filename is "." or "/" or ""
            ? "request-" + requestId.ToString(CultureInfo.InvariantCulture) + "-content"
            : filename;
    }

    /// <summary>
    /// The Content-Disposition gin's FileAttachment writes before ServeFile
    /// (context.go:1303-1310): quoted ASCII names, an RFC 5987 <c>filename*</c>
    /// otherwise.
    /// </summary>
    public static string FileAttachmentDisposition(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        return IsAscii(filename)
            ? "attachment; filename=\"" + EscapeQuotes(filename) + "\""
            : "attachment; filename*=UTF-8''" + QueryEscape(filename);
    }

    private static bool IsAscii(string value)
    {
        foreach (var character in value)
        {
            if (character >= 0x80)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>gin's escapeQuotes (context.go:1295-1299).</summary>
    private static string EscapeQuotes(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>
    /// Go's <c>url.QueryEscape</c>: unreserved bytes pass through, a space
    /// becomes '+', everything else is a percent-encoded UTF-8 byte.
    /// </summary>
    private static string QueryEscape(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var builder = new StringBuilder(bytes.Length * 3);
        ReadOnlySpan<char> upperHex = "0123456789ABCDEF";

        foreach (var byteValue in bytes)
        {
            if (IsQueryUnreserved(byteValue))
            {
                builder.Append((char)byteValue);
            }
            else if (byteValue == (byte)' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%');
                builder.Append(upperHex[byteValue >> 4]);
                builder.Append(upperHex[byteValue & 0xF]);
            }
        }

        return builder.ToString();
    }

    private static bool IsQueryUnreserved(byte value)
        => (value >= (byte)'a' && value <= (byte)'z')
            || (value >= (byte)'A' && value <= (byte)'Z')
            || (value >= (byte)'0' && value <= (byte)'9')
            || value == (byte)'-'
            || value == (byte)'_'
            || value == (byte)'.'
            || value == (byte)'~';

    /// <summary>Port of <c>safeRelativePath</c> (request_content.go:133-149).</summary>
    public static bool TryGetSafeRelativePath(string key, out string relativePath)
    {
        // filepath.FromSlash first, then strings.TrimPrefix(..., "/"). On
        // Windows FromSlash turns the leading slash into a backslash, so the
        // trim (which uses a literal "/") is a no-op there — the reference has
        // the same platform-dependent behaviour.
        var slashPath = FromSlash(key);
        if (slashPath.StartsWith('/'))
        {
            slashPath = slashPath[1..];
        }

        var trimmed = Clean(slashPath);
        if (trimmed is "." or "")
        {
            relativePath = string.Empty;
            return false;
        }

        if (string.Equals(trimmed, "..", StringComparison.Ordinal)
            || trimmed.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = trimmed;
        return true;
    }

    /// <summary>
    /// Go's <c>filepath.Join</c> for the fs fast path: join with the OS
    /// separator, then clean. Unlike <see cref="Path.Combine(string,string)"/>
    /// a rooted second element never discards the first one. The result is
    /// made absolute; os.Open resolves a relative join against the CWD, so the
    /// file being served is the same.
    /// </summary>
    public static string JoinStoragePath(string directory, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(relativePath);

        var combined = directory.Length == 0
            ? relativePath
            : relativePath.Length == 0
                ? directory
                : directory + Path.DirectorySeparatorChar + relativePath;

        return Path.GetFullPath(combined);
    }

    /// <summary>Go's <c>filepath.FromSlash</c>.</summary>
    public static string FromSlash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Path.DirectorySeparatorChar == '/'
            ? path
            : path.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Go's <c>filepath.Clean</c> for the separators of the running platform.
    /// The caller feeds storage keys (slash paths, optional leading "/"), so
    /// the Windows volume/UNC prefixes never occur beyond a bare drive or the
    /// shared root the helper still preserves.
    /// </summary>
    public static string Clean(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return ".";
        }

        var separator = Path.DirectorySeparatorChar;
        string prefix;
        var index = 0;

        if (separator == '\\'
            && path.Length >= 2
            && char.IsLetter(path[0])
            && path[1] == ':')
        {
            prefix = path[..2];
            index = 2;
            if (index < path.Length && IsSeparator(path[index]))
            {
                prefix += separator;
                index++;
            }
        }
        else if (IsSeparator(path[0]))
        {
            prefix = separator.ToString();
            while (index < path.Length && IsSeparator(path[index]))
            {
                index++;
            }
        }
        else
        {
            prefix = string.Empty;
        }

        var parts = new List<string>();
        while (index <= path.Length)
        {
            var end = index;
            while (end < path.Length && !IsSeparator(path[end]))
            {
                end++;
            }

            var part = path[index..end];
            if (part.Length > 0 && !string.Equals(part, ".", StringComparison.Ordinal))
            {
                if (string.Equals(part, "..", StringComparison.Ordinal))
                {
                    if (parts.Count > 0
                        && !string.Equals(parts[^1], "..", StringComparison.Ordinal))
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                    else if (prefix.Length == 0)
                    {
                        parts.Add("..");
                    }
                }
                else
                {
                    parts.Add(part);
                }
            }

            index = end + 1;
        }

        var cleaned = prefix + string.Join(separator, parts);
        if (cleaned.Length == 0)
        {
            return ".";
        }

        if (separator == '\\' && cleaned.Length == 2 && cleaned[1] == ':')
        {
            // filepath.Clean keeps a bare volume relative ("C:.").
            return cleaned + ".";
        }

        return cleaned;
    }

    private static bool IsSeparator(char value)
        => value == '/'
            || (Path.DirectorySeparatorChar == '\\' && value == '\\');

    private static bool IsDecimal(string text)
    {
        var start = text.Length > 0 && (text[0] == '+' || text[0] == '-') ? 1 : 0;
        if (start == text.Length)
        {
            return false;
        }

        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
