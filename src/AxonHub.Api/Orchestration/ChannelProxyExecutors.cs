using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the per-channel HTTP client built by
/// <c>ChannelService.buildChannelWithTransformer</c>
/// (internal/server/biz/channel_llm.go:113-130, 700-774) and selected by
/// <c>p.getCustomizedExecutor</c> (internal/server/orchestrator/outbound.go:1164-1196).
/// </summary>
/// <remarks>
/// <para>
/// The reference hands every built channel a client: the shared client when its
/// settings carry no proxy, otherwise <c>svc.httpClient.WithProxy(settings.Proxy)</c>,
/// and keeps it for as long as the enabled-channel cache entry lives. The port
/// has no per-channel client on <see cref="ChannelCandidate"/>, so this service
/// owns that cache: the first attempt of a channel builds the proxy client and
/// later attempts reuse it until the persisted proxy value changes. A proxy of
/// <c>type: disabled</c> still gets a dedicated client, because it must suppress
/// the environment proxy rather than fall back to the shared client.
/// </para>
/// <para>
/// The tester's per-request override (axonhub.resolvers.go:344 →
/// <c>processor.proxy</c> → <c>state.Proxy</c>) always builds a fresh client,
/// exactly like the reference's <c>channel.HTTPClient.WithProxy(state.Proxy)</c>.
/// The per-attempt Command Code HTTPS-downgrade hook is applied by
/// <see cref="PersistentOutboundTransformer"/> on top of whichever executor
/// wins, reproducing <c>WithProxy</c>'s option preservation.
/// </para>
/// </remarks>
public sealed class ChannelProxyExecutors
{
    private readonly IExecutor _fallback;
    private readonly IProxyExecutorFactory _factory;
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _channels = [];

    public ChannelProxyExecutors(IExecutor fallback, IProxyExecutorFactory factory)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(factory);

        _fallback = fallback;
        _factory = factory;
    }

    /// <summary>
    /// <c>ChannelService.getHttpClient</c>: no proxy setting keeps the shared
    /// client; a configured proxy gets the channel's dedicated client.
    /// </summary>
    public IExecutor Resolve(ChannelCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var proxy = ChannelTransformOptions.SettingsOf(candidate)?.Proxy;
        if (proxy is null)
        {
            // The channel no longer configures a proxy (or never did): drop the
            // cached client so a later reconfiguration starts from a fresh one.
            lock (_gate)
            {
                _channels.Remove(candidate.ChannelId);
            }

            return _fallback;
        }

        var fingerprint = ChannelProxySettings.Fingerprint(proxy);
        lock (_gate)
        {
            if (_channels.TryGetValue(candidate.ChannelId, out var entry)
                && entry.Fingerprint == fingerprint)
            {
                return entry.Executor;
            }

            // The reference rebuilds the client with the channel record, so a
            // changed settings snapshot replaces the cached executor.
            var built = _factory.Create(ChannelProxySettings.ToProxyConfig(proxy)!);
            _channels[candidate.ChannelId] = new Entry(fingerprint, built);
            return built;
        }
    }

    /// <summary>
    /// <c>channel.HTTPClient.WithProxy(state.Proxy)</c>: a fresh client per test
    /// request. The override is not part of the channel's persisted settings
    /// and must not replace the cached channel client.
    /// </summary>
    public IExecutor CreateOverride(ProxyConfig proxyConfig)
    {
        ArgumentNullException.ThrowIfNull(proxyConfig);

        return _factory.Create(proxyConfig);
    }

    private sealed record Entry(string Fingerprint, IExecutor Executor);
}
