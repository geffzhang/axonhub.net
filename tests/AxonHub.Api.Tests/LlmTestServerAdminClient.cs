using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Api.Tests;

internal interface ILlmTestServerAdminClient
{
    Task ConfigureAsync(object configuration, CancellationToken cancellationToken = default);

    Task ResetAsync(CancellationToken cancellationToken = default);

    Task<JsonDocument> GetAssertionsAsync(CancellationToken cancellationToken = default);

    Task<JsonDocument> GetRequestsAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<int> CountRequestsAsync(
        string provider,
        int? status = null,
        CancellationToken cancellationToken = default);
}

internal sealed class LlmTestServerAdminClient : ILlmTestServerAdminClient
{
    private static readonly JsonSerializerOptions ConfigurationJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _serverUrl;
    private readonly string _apiKey;

    internal LlmTestServerAdminClient(string serverUrl, string apiKey)
    {
        _serverUrl = serverUrl.TrimEnd('/');
        _apiKey = apiKey;
    }

    public async Task ConfigureAsync(
        object configuration,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/admin-api/configure")
        {
            Content = JsonContent.Create(configuration, options: ConfigurationJson),
        };
        var body = await SendAsync(request, cancellationToken);
        Assert.Contains("configured", body, StringComparison.Ordinal);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _serverUrl + "/admin-api/reset");
        _ = await SendAsync(request, cancellationToken);
    }

    public async Task<JsonDocument> GetAssertionsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _serverUrl + "/admin-api/assertions");
        var body = await SendAsync(request, cancellationToken);
        return JsonDocument.Parse(body);
    }

    public async Task<JsonDocument> GetRequestsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var queryString = query.TrimStart('?');
        var url = _serverUrl + "/admin-api/requests";
        if (queryString.Length > 0)
        {
            url += "?" + queryString;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var body = await SendAsync(request, cancellationToken);
        return JsonDocument.Parse(body);
    }

    public async Task<int> CountRequestsAsync(
        string provider,
        int? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = "provider=" + Uri.EscapeDataString(provider);
        if (status.HasValue)
        {
            query += "&status=" + status.Value;
        }

        using var requests = await GetRequestsAsync(query, cancellationToken);
        return requests.RootElement.GetArrayLength();
    }

    private async Task<string> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"LlmTestServer admin API request failed with status "
                + $"{(int)response.StatusCode} ({response.StatusCode}). Response body: {body}",
                inner: null,
                response.StatusCode);
        }

        return body;
    }
}
