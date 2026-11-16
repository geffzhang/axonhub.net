using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// A failure of an OIDC protocol call whose text mirrors the reference's
/// error message. The login service wraps it exactly like the reference wraps
/// the go-oidc/oauth2 error (for example
/// <c>failed to exchange authorization code: oauth2: "invalid_grant"</c>).
/// </summary>
public sealed class OidcProtocolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>The fields oauth2.Config.Exchange sends to the token endpoint.</summary>
public sealed record OidcTokenRequest(
    string TokenUrl,
    string ClientId,
    string ClientSecret,
    string RedirectUrl,
    string Code,
    string? CodeVerifier);

/// <summary>The subset of oauth2.Token the login flow reads.</summary>
public sealed record OidcTokenResult(
    string AccessToken,
    string TokenType,
    string? IdToken);

/// <summary>
/// HTTP seam for the token exchange. Kept behind an interface so the login
/// service tests can script token-endpoint responses.
/// </summary>
public interface IOidcTokenClient
{
    Task<OidcTokenResult> ExchangeAsync(
        OidcTokenRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of golang.org/x/oauth2 v0.36.0's <c>Config.Exchange</c> and
/// <c>internal.RetrieveToken</c>: the form body (<c>grant_type</c>,
/// <c>code</c>, optional <c>redirect_uri</c>/<c>code_verifier</c>), the
/// Basic-then-params authentication-style probe with its successful-style
/// cache, the token-response parsing rules (form or JSON) and the exact error
/// renderings of <c>oauth2.RetrieveError</c>.
/// </summary>
public sealed class OidcTokenClient(HttpClient client) : IOidcTokenClient
{
    private const int MaxResponseBytes = 1 << 20;

    private const int AuthStyleInParams = 1;
    private const int AuthStyleInHeader = 2;

    // oauth2.Config's LazyAuthStyleCache: per process. The key includes the
    // token URL and client id, so a shared map is indistinguishable from the
    // reference's per-config map.
    private static readonly ConcurrentDictionary<(string TokenUrl, string ClientId), int> AuthStyles = new();

    /// <inheritdoc />
    public async Task<OidcTokenResult> ExchangeAsync(
        OidcTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = request.Code,
        };

        if (request.RedirectUrl.Length > 0)
        {
            values["redirect_uri"] = request.RedirectUrl;
        }

        if (request.CodeVerifier is { Length: > 0 } verifier)
        {
            values["code_verifier"] = verifier;
        }

        var authStyle = AuthStyleInHeader;
        var needsProbe = true;
        if (AuthStyles.TryGetValue((request.TokenUrl, request.ClientId), out var cachedStyle))
        {
            authStyle = cachedStyle;
            needsProbe = false;
        }

        var (token, error) = await SendAsync(request, values, authStyle, cancellationToken)
            .ConfigureAwait(false);

        if (error is not null && needsProbe)
        {
            // Any first-attempt failure triggers the second form, exactly like
            // RetrieveToken; the final error is the second attempt's error.
            authStyle = AuthStyleInParams;
            (token, error) = await SendAsync(request, values, authStyle, cancellationToken)
                .ConfigureAwait(false);
        }

        if (needsProbe && error is null)
        {
            AuthStyles[(request.TokenUrl, request.ClientId)] = authStyle;
        }

        if (error is not null)
        {
            throw error;
        }

        return token!;
    }

    private async Task<(OidcTokenResult? Token, OidcProtocolException? Error)> SendAsync(
        OidcTokenRequest request,
        IReadOnlyDictionary<string, string> baseValues,
        int authStyle,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in baseValues)
        {
            values[pair.Key] = pair.Value;
        }
        if (authStyle == AuthStyleInParams)
        {
            if (request.ClientId.Length > 0)
            {
                values["client_id"] = request.ClientId;
            }

            if (request.ClientSecret.Length > 0)
            {
                values["client_secret"] = request.ClientSecret;
            }
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, request.TokenUrl)
        {
            Content = new StringContent(OidcProtocol.FormEncode(values), Encoding.UTF8),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/x-www-form-urlencoded");

        if (authStyle == AuthStyleInHeader)
        {
            var credentials = OidcProtocol.QueryEscape(request.ClientId)
                + ":" + OidcProtocol.QueryEscape(request.ClientSecret);
            message.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)));
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, new OidcProtocolException(ex.Message, ex));
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = await ReadAtMostAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (null, new OidcProtocolException(
                    "oauth2: cannot fetch token: " + ex.Message, ex));
            }

            var failureStatus = (int)response.StatusCode is < 200 or > 299;
            var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                + " " + ReasonPhrases.GetReasonPhrase((int)response.StatusCode);
            var text = Encoding.UTF8.GetString(body);

            string errorCode = string.Empty;
            string errorDescription = string.Empty;
            string errorUri = string.Empty;
            string accessToken;
            string tokenType;
            string? idToken = null;

            var mediaType = ParseMediaType(response.Content.Headers.ContentType?.MediaType);
            if (mediaType is "application/x-www-form-urlencoded" or "text/plain")
            {
                var form = OidcProtocol.ParseQuery(text, out var parseError);
                if (parseError is not null)
                {
                    if (failureStatus)
                    {
                        return (null, RetrieveError(
                            status, text, string.Empty, string.Empty, string.Empty));
                    }

                    return (null, new OidcProtocolException(
                        "oauth2: cannot parse response: " + parseError));
                }

                errorCode = Get(form, "error");
                errorDescription = Get(form, "error_description");
                errorUri = Get(form, "error_uri");
                accessToken = Get(form, "access_token");
                tokenType = Get(form, "token_type");
                idToken = EmptyToNull(Get(form, "id_token"));
            }
            else
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(text);
                }
                catch (JsonException ex)
                {
                    if (failureStatus)
                    {
                        return (null, RetrieveError(
                            status, text, string.Empty, string.Empty, string.Empty));
                    }

                    return (null, new OidcProtocolException(
                        "oauth2: cannot parse json: " + ex.Message, ex));
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object)
                    {
                        if (failureStatus)
                        {
                            return (null, RetrieveError(
                                status, text, string.Empty, string.Empty, string.Empty));
                        }

                        return (null, new OidcProtocolException(
                            "oauth2: cannot parse json: json: cannot unmarshal "
                            + root.ValueKind.ToString().ToLowerInvariant()
                            + " into Go value of type internal.tokenJSON"));
                    }

                    errorCode = GetString(root, "error");
                    errorDescription = GetString(root, "error_description");
                    errorUri = GetString(root, "error_uri");
                    accessToken = GetString(root, "access_token");
                    tokenType = GetString(root, "token_type");
                    idToken = EmptyToNull(GetString(root, "id_token"));
                }
            }

            if (failureStatus || errorCode.Length > 0)
            {
                return (null, RetrieveError(status, text, errorCode, errorDescription, errorUri));
            }

            if (accessToken.Length == 0)
            {
                return (null, new OidcProtocolException(
                    "oauth2: server response missing access_token"));
            }

            return (new OidcTokenResult(accessToken, tokenType, idToken), null);
        }
    }

    private static OidcProtocolException RetrieveError(
        string status,
        string body,
        string errorCode,
        string errorDescription,
        string errorUri)
    {
        if (errorCode.Length > 0)
        {
            var message = "oauth2: " + GoCast.GoQuote(errorCode);
            if (errorDescription.Length > 0)
            {
                message += " " + GoCast.GoQuote(errorDescription);
            }

            if (errorUri.Length > 0)
            {
                message += " " + GoCast.GoQuote(errorUri);
            }

            return new OidcProtocolException(message);
        }

        return new OidcProtocolException(
            "oauth2: cannot fetch token: " + status + "\nResponse: " + body);
    }

    /// <summary>mime.ParseMediaType: lowercases the media type, empty on failure.</summary>
    private static string? ParseMediaType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return string.Empty;
        }

        var separator = contentType.IndexOf(';');
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return mediaType.ToLowerInvariant();
    }

    private static string Get(Dictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : string.Empty;

    private static string GetString(JsonElement root, string name)
    {
        // encoding/json matches object keys case-insensitively and leaves the
        // field untouched on a type mismatch.
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : string.Empty;
            }
        }

        return string.Empty;
    }

    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static async Task<byte[]> ReadAtMostAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length < MaxResponseBytes)
        {
            var want = (int)Math.Min(chunk.Length, MaxResponseBytes - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, want), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
