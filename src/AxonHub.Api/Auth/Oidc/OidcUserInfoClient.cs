using System.Text.Json;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>The inputs of a UserInfo request (oauth2 token plus provider mode).</summary>
public sealed record OidcUserInfoRequest(
    bool UseDiscovery,
    string? Endpoint,
    string AccessToken,
    string TokenType);

/// <summary>The decoded claims plus the raw claim map for group extraction.</summary>
public sealed record OidcUserInfo(
    OidcClaims Claims,
    IReadOnlyDictionary<string, object?> Raw);

/// <summary>HTTP seam for <c>fetchUserInfo</c> (oidc.go:804-866).</summary>
public interface IOidcUserInfoClient
{
    Task<OidcUserInfo> GetAsync(
        OidcUserInfoRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of <c>OIDCService.fetchUserInfo</c>: the discovery path models
/// go-oidc's <c>Provider.UserInfo</c>, the manual path the plain GET with the
/// reference's 10s context / 15s client timeouts and its GitHub
/// <c>id</c>/<c>login</c> subject fallback.
/// </summary>
public sealed class OidcUserInfoClient(HttpClient client) : IOidcUserInfoClient
{
    /// <inheritdoc />
    public async Task<OidcUserInfo> GetAsync(
        OidcUserInfoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var endpoint = request.Endpoint ?? string.Empty;
        if (request.UseDiscovery)
        {
            if (endpoint.Length == 0)
            {
                throw new OidcProtocolException(
                    "oidc: user info endpoint is not supported by this provider");
            }

            return await FetchAsync(request, endpoint, isDiscovery: true, cancellationToken)
                .ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await FetchAsync(request, endpoint, isDiscovery: false, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OidcProtocolException("context deadline exceeded");
        }
    }

    private async Task<OidcUserInfo> FetchAsync(
        OidcUserInfoRequest request,
        string endpoint,
        bool isDiscovery,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, endpoint);
        message.Headers.TryAddWithoutValidation(
            "Authorization",
            TokenTypeName(request.TokenType) + " " + request.AccessToken);

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
            throw new OidcProtocolException(ex.Message, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (isDiscovery)
                {
                    throw new OidcProtocolException(
                        "oidc: failed to query userinfo: " + (int)response.StatusCode + ": " + body);
                }

                throw new OidcProtocolException(
                    "userinfo request failed with status: " + (int)response.StatusCode);
            }

            Dictionary<string, object?> raw;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new OidcProtocolException(isDiscovery
                        ? "oidc: failed to decode userinfo: json: cannot unmarshal "
                            + document.RootElement.ValueKind.ToString().ToLowerInvariant()
                            + " into Go value of type oidc.UserInfo"
                        : "json: cannot unmarshal "
                            + document.RootElement.ValueKind.ToString().ToLowerInvariant()
                            + " into Go value of type map[string]interface {}");
                }

                raw = OidcClaims.FromJson(document.RootElement);
            }
            catch (JsonException ex)
            {
                throw new OidcProtocolException(
                    isDiscovery
                        ? "oidc: failed to decode userinfo: " + ex.Message
                        : ex.Message,
                    ex);
            }

            var claims = OidcClaims.FromRaw(raw);

            if (!isDiscovery && claims.Sub.Length == 0)
            {
                // GitHub fallback: sub may be called id or login.
                if (raw.TryGetValue("id", out var id) && id is double number)
                {
                    claims = claims with { Sub = FormatNumber(number) };
                }
                else if (raw.TryGetValue("login", out var login) && login is string loginText)
                {
                    claims = claims with { Sub = loginText };
                }
            }

            return new OidcUserInfo(claims, raw);
        }
    }

    /// <summary>Port of oauth2.Token.Type (token.go:70-84).</summary>
    public static string TokenTypeName(string tokenType)
    {
        if (string.Equals(tokenType, "bearer", StringComparison.OrdinalIgnoreCase))
        {
            return "Bearer";
        }

        if (string.Equals(tokenType, "mac", StringComparison.OrdinalIgnoreCase))
        {
            return "MAC";
        }

        if (string.Equals(tokenType, "basic", StringComparison.OrdinalIgnoreCase))
        {
            return "Basic";
        }

        return tokenType.Length > 0 ? tokenType : "Bearer";
    }

    /// <summary>Go <c>fmt.Sprintf("%.0f", v)</c>: decimal, no fraction, ties to even.</summary>
    private static string FormatNumber(double value)
        => Math.Round(value, MidpointRounding.ToEven)
            .ToString("0", System.Globalization.CultureInfo.InvariantCulture);
}
