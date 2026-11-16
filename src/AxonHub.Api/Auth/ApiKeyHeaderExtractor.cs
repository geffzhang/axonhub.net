using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.ExtractAPIKeyFromRequest with the default APIKeyConfig
/// (internal/server/middleware/header.go): the same header priority, optional
/// Bearer/Token/Api-Key prefixes and TrimSpace semantics.
/// </summary>
public static class ApiKeyHeaderExtractor
{
    /// <summary>DefaultAPIKeyConfig.Headers, in priority order.</summary>
    public static readonly string[] DefaultHeaders =
    [
        "Authorization",
        "X-API-Key",
        "X-Api-Key",
        "API-Key",
        "Api-Key",
        "X-Goog-Api-Key",
        "X-Google-Api-Key",
    ];

    private static readonly string[] DefaultPrefixes =
    [
        "Bearer ",
        "Token ",
        "Api-Key ",
        "API-Key ",
    ];

    /// <summary>
    /// Returns the extracted key, or null when no header carries one. The
    /// reference also reports ErrAPIKeyRequired; the auth middleware treats both
    /// the same way (it falls back to no-auth).
    /// </summary>
    public static string? Extract(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var headerName in DefaultHeaders)
        {
            // Go's Header.Get returns the first value; StringValues.ToString()
            // would join multiple values with a comma.
            var values = headers[headerName];
            if (values.Count == 0)
            {
                continue;
            }

            var headerValue = values[0];
            if (string.IsNullOrEmpty(headerValue))
            {
                continue;
            }

            var apiKey = headerValue;
            foreach (var prefix in DefaultPrefixes)
            {
                if (headerValue.StartsWith(prefix, StringComparison.Ordinal))
                {
                    apiKey = headerValue[prefix.Length..];
                    break;
                }
            }

            // DefaultAPIKeyConfig has RequireBearer=false, so Authorization also
            // accepts a bare key; all values are trimmed before use.
            apiKey = apiKey.Trim();
            if (apiKey.Length == 0)
            {
                continue;
            }

            return apiKey;
        }

        return null;
    }
}
