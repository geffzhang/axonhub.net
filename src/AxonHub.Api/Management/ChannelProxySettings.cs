using System.Text.Json;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Management;

/// <summary>
/// Maps the persisted <c>channels.settings.proxy</c> shape
/// (<see cref="ProxyConfigDraft"/>, the ent-marshaled
/// <c>objects.ProxyConfig</c>) to the transport's <see cref="ProxyConfig"/>.
/// </summary>
/// <remarks>
/// The reference keeps one struct type for both sides and hands
/// <c>ChannelSettings.Proxy</c> straight to <c>HttpClient.WithProxy</c>
/// (internal/server/biz/channel_llm.go:113-130). The port keeps the settings
/// draft separate because it preserves Go's <c>omitempty</c> null-vs-false
/// distinction (<c>disableConnectionReuse</c>), so this is the single
/// conversion seam. When the stored proxy carries no value, the transport
/// default (<c>false</c>) is used, exactly like Go's zero-value bool.
/// </remarks>
public static class ChannelProxySettings
{
    /// <summary>Builds the transport config, or null when the channel has none.</summary>
    public static ProxyConfig? ToProxyConfig(ProxyConfigDraft? proxy)
        => proxy is null
            ? null
            : new ProxyConfig
            {
                Type = proxy.Type,
                Url = proxy.Url,
                Username = proxy.Username,
                Password = proxy.Password,
                DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
            };

    /// <summary>
    /// Cache identity of a channel proxy configuration. The reference rebuilds
    /// the channel's HTTP client whenever the enabled-channel cache rebuilds,
    /// so the port treats any changed proxy value as a new client.
    /// </summary>
    public static string Fingerprint(ProxyConfigDraft proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);

        // The settings draft carries JsonPropertyName attributes and omits
        // nulls, so this string is stable for equal proxy values.
        return JsonSerializer.Serialize(proxy);
    }
}
