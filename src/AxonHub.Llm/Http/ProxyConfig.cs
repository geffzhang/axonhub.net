using System.Text.Json.Serialization;

namespace AxonHub.Llm.Http;

/// <summary>Port of httpclient.ProxyType (llm/httpclient/proxy.go:3-9).</summary>
public static class ProxyType
{
    public const string Disabled = "disabled";

    public const string Environment = "environment";

    public const string Url = "url";
}

/// <summary>Port of httpclient.ProxyConfig (llm/httpclient/proxy.go:11-17).</summary>
public sealed class ProxyConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    /// <summary>Go: <c>json:"disableConnectionReuse,omitempty"</c>.</summary>
    [JsonPropertyName("disableConnectionReuse")]
    public bool DisableConnectionReuse { get; set; }
}
