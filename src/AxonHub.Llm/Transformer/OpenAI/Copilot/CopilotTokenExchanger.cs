using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Concurrency;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.OpenAI.Copilot;

/// <summary>Port of copilot.TokenExchangerParams (token_exchanger.go:46-49).</summary>
public sealed class CopilotTokenExchangerParams
{
    /// <summary>The channel's proxy-aware client; null falls back to the shared one.</summary>
    public IExecutor? HttpClient { get; set; }

    /// <summary>Overrides <see cref="CopilotConstants.DefaultCopilotTokenEndpoint"/> when set.</summary>
    public string Endpoint { get; set; } = string.Empty;
}

/// <summary>Port of the anonymous <c>copilotTokenResponse</c> (token_exchanger.go:27-30).</summary>
/// <remarks>
/// Both members are nullable so a JSON <c>null</c> decodes to Go's zero value
/// (empty token / zero expiry) instead of failing the whole decode.
/// </remarks>
internal sealed class CopilotTokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("expires_at")]
    public long? ExpiresAt { get; set; }
}

/// <summary>Port of copilotTokenCacheEntry (token_exchanger.go:32-44).</summary>
internal sealed class CopilotTokenCacheEntry
{
    public string CopilotToken { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; init; }

    public DateTimeOffset CachedAt { get; init; }

    public bool IsExpired(DateTimeOffset now)
        => ExpiresAt == DateTimeOffset.MinValue
            || now > ExpiresAt.Subtract(CopilotConstants.TokenExpiryBuffer);
}

/// <summary>
/// Port of copilot.TokenExchanger (token_exchanger.go:51-195): exchanges the
/// GitHub OAuth access token for a Copilot token, with a SHA-256 keyed cache
/// and singleflight coalescing.
/// </summary>
public sealed class CopilotTokenExchanger : ITokenExchanger
{
    private readonly IExecutor? _httpClient;
    private readonly string _endpoint;
    private readonly Dictionary<string, CopilotTokenCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheSync = new();
    private readonly SingleFlight<CopilotTokenResponse> _singleFlight = new();

    public CopilotTokenExchanger(CopilotTokenExchangerParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        _httpClient = parameters.HttpClient;
        _endpoint = parameters.Endpoint.Length == 0
            ? CopilotConstants.DefaultCopilotTokenEndpoint
            : parameters.Endpoint;
    }

    /// <summary>Port of <c>(*TokenExchanger).Exchange</c>.</summary>
    public TokenExchangeResult Exchange(CancellationToken cancellationToken, string accessToken)
        => ExchangeWithClient(cancellationToken, null, accessToken);

    /// <summary>Port of <c>(*TokenExchanger).ExchangeWithClient</c>.</summary>
    public TokenExchangeResult ExchangeWithClient(
        CancellationToken cancellationToken,
        IExecutor? httpClient,
        string accessToken)
    {
        // The reference runs the shared exchange under context.WithoutCancel so
        // one caller's cancellation cannot cancel the in-flight request.
        _ = cancellationToken;

        if (accessToken.Length == 0)
        {
            return TokenExchangeResult.Failure(new InvalidOperationException("access token is empty"));
        }

        var client = httpClient ?? _httpClient;
        var cacheKey = TokenCacheKey(accessToken);

        CopilotTokenCacheEntry? entry;
        lock (_cacheSync)
        {
            _cache.TryGetValue(cacheKey, out entry);
        }

        if (entry is not null && !entry.IsExpired(DateTimeOffset.UtcNow))
        {
            return TokenExchangeResult.Success(entry.CopilotToken, entry.ExpiresAt.ToUnixTimeSeconds());
        }

        CopilotTokenResponse response;
        try
        {
            var singleflightKey = ExchangeSingleflightKey(cacheKey, client, _endpoint);
            response = _singleFlight.Do(singleflightKey, () => ExchangeCore(client, accessToken));
        }
        catch (Exception ex)
        {
            return TokenExchangeResult.Failure(ex);
        }

        var expiresAtUnix = response.ExpiresAt ?? 0;
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnix);

        lock (_cacheSync)
        {
            _cache[cacheKey] = new CopilotTokenCacheEntry
            {
                CopilotToken = response.Token ?? string.Empty,
                ExpiresAt = expiresAt,
                CachedAt = DateTimeOffset.UtcNow,
            };
        }

        return TokenExchangeResult.Success(response.Token ?? string.Empty, expiresAtUnix);
    }

    /// <summary>Port of <c>tokenCacheKey</c>: lowercase SHA-256 hex of the access token.</summary>
    internal static string TokenCacheKey(string accessToken)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));

    /// <summary>
    /// Port of <c>exchangeSingleflightKey</c>. Go formats the client pointer with
    /// <c>%p</c>; the CLR identity hash is the closest stable identity (C107).
    /// </summary>
    internal static string ExchangeSingleflightKey(string cacheKey, IExecutor? client, string endpoint)
        => cacheKey + ":" + ClientKey(client) + ":" + endpoint;

    private static string ClientKey(IExecutor? client)
    {
        if (client is null)
        {
            return "0x0";
        }

        var identity = RuntimeHelpers.GetHashCode(client);
        return "0x" + identity.ToString("x", CultureInfo.InvariantCulture);
    }

    private CopilotTokenResponse ExchangeCore(IExecutor? httpClient, string accessToken)
    {
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = _endpoint,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Authorization"] = ["token " + accessToken],
                ["Accept"] = ["application/json"],
            },
        };

        if (httpClient is null)
        {
            // The reference builds a default httpclient.HttpClient here; the port
            // has no transport without an injected executor (C107).
            throw new InvalidOperationException("http client is nil");
        }

        // tokenExchangeTimeout: the reference derives this from an
        // already-cancellation-free context, so only the timeout remains.
        using var timeout = new CancellationTokenSource(CopilotConstants.TokenExchangeTimeout);

        HttpResponseData response;
        try
        {
            response = httpClient.Do(timeout.Token, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "token exchange request failed: ");
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new InvalidOperationException(
                "token exchange returned non-2xx status: "
                + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        CopilotTokenResponse tokenResponse;
        try
        {
            tokenResponse = LlmJson.Deserialize<CopilotTokenResponse>(response.Body ?? [])
                ?? new CopilotTokenResponse();
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, "failed to parse token response: ");
        }

        if (string.IsNullOrEmpty(tokenResponse.Token))
        {
            throw new InvalidOperationException("copilot token is empty in response");
        }

        if ((tokenResponse.ExpiresAt ?? 0) == 0)
        {
            throw new InvalidOperationException("expires_at is missing in response");
        }

        return tokenResponse;
    }
}
