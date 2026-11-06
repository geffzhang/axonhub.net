using System.Net;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Http;

/// <summary>
/// Seam for <c>httpclient.HttpClient.WithProxy</c> (llm/httpclient/client.go:96-102).
/// The reference swaps the concrete client's transport and keeps the caller's
/// options; the port's <see cref="IExecutor"/> carries no transport or options,
/// so the proxy variant is built by a factory instead (difference C202).
/// </summary>
public interface IProxyExecutorFactory
{
    IExecutor Create(ProxyConfig proxyConfig);
}

/// <summary>
/// Port of NewHttpClientWithProxy (llm/httpclient/client.go:71-114): a transport
/// whose proxy function mirrors <c>getProxyFunc</c> and whose keep-alive
/// disabling mirrors <c>DisableKeepAlives</c>.
/// </summary>
public sealed class HttpProxyExecutorFactory : IProxyExecutorFactory
{
    /// <summary>Go: <c>net.Dialer{Timeout: 30s, KeepAlive: 30s}</c>.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Go: <c>IdleConnTimeout: 90s</c>.</summary>
    private static readonly TimeSpan IdleConnectionTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Go: <c>ExpectContinueTimeout: 1s</c>.</summary>
    private static readonly TimeSpan ExpectContinueTimeout = TimeSpan.FromSeconds(1);

    public IExecutor Create(ProxyConfig proxyConfig)
    {
        ArgumentNullException.ThrowIfNull(proxyConfig);

        // client.go:76-78: only a URL proxy can disable connection reuse.
        var disableConnectionReuse =
            proxyConfig.Type == ProxyType.Url && proxyConfig.DisableConnectionReuse;

        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            PooledConnectionIdleTimeout = disableConnectionReuse
                ? TimeSpan.Zero
                : IdleConnectionTimeout,
            Expect100ContinueTimeout = ExpectContinueTimeout,
        };

        switch (proxyConfig.Type)
        {
            case ProxyType.Disabled:
                handler.UseProxy = false;
                break;
            case ProxyType.Url:
                handler.Proxy = BuildUrlProxy(proxyConfig);
                break;
            default:
                // ProxyTypeEnvironment, and unknown types, fall back to
                // http.ProxyFromEnvironment (client.go:171-174, 198-201).
                handler.Proxy = HttpClient.DefaultProxy;
                break;
        }

        // Go's http.Client carries no total timeout (client.go:106).
        return new HttpExecutor(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
    }

    /// <summary>
    /// Port of the ProxyTypeURL branch of getProxyFunc (client.go:175-196). A
    /// bad configuration reports its error when the request is made, exactly
    /// like the closure the reference returns.
    /// </summary>
    private static IWebProxy BuildUrlProxy(ProxyConfig proxyConfig)
    {
        if (string.IsNullOrEmpty(proxyConfig.Url))
        {
            return new ThrowingProxy("proxy URL is required when type is 'url'");
        }

        if (!Uri.TryCreate(proxyConfig.Url, UriKind.Absolute, out var proxyUri))
        {
            // Go wraps url.Parse's error; the port has no Go-style parser
            // (difference C202).
            return new ThrowingProxy("invalid proxy URL: " + proxyConfig.Url);
        }

        var proxy = new WebProxy(proxyUri);

        // client.go:190-192: configured credentials override URL userinfo, and
        // only when both fields are present.
        var credentials = CredentialsFromUserInfo(proxyUri);
        if (!string.IsNullOrEmpty(proxyConfig.Username)
            && !string.IsNullOrEmpty(proxyConfig.Password))
        {
            credentials = new NetworkCredential(proxyConfig.Username, proxyConfig.Password);
        }

        if (credentials is not null)
        {
            proxy.Credentials = credentials;
        }

        return proxy;
    }

    private static NetworkCredential? CredentialsFromUserInfo(Uri proxyUri)
    {
        var userInfo = proxyUri.UserInfo;
        if (userInfo.Length == 0)
        {
            return null;
        }

        var separator = userInfo.IndexOf(':', StringComparison.Ordinal);
        var user = separator < 0 ? userInfo : userInfo[..separator];
        var password = separator < 0 ? string.Empty : userInfo[(separator + 1)..];

        return new NetworkCredential(
            Uri.UnescapeDataString(user),
            Uri.UnescapeDataString(password));
    }

    /// <summary>A proxy whose configuration error surfaces at request time.</summary>
    private sealed class ThrowingProxy(string message) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination)
        {
            _ = destination;
            throw new InvalidOperationException(message);
        }

        public bool IsBypassed(Uri host)
        {
            _ = host;
            return false;
        }
    }
}
