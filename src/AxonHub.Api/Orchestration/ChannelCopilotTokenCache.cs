using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI.Copilot;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// The per-channel <see cref="CopilotTokenExchanger"/> behind the
/// github_copilot branch of <c>buildChannelWithTransformer</c>
/// (channel_llm.go:1218-1276, §194).
/// </summary>
/// <remarks>
/// <para>
/// The reference keeps the Copilot <c>TokenProvider</c> — and therefore its
/// exchanger and the SHA-256 keyed Copilot-token cache — alive on the enabled
/// channel cache entry, and rebuilds it when the cache is invalidated by a
/// channel update. The port builds the outbound per attempt, so without this
/// cache every attempt would exchange the GitHub access token again, which is
/// an observable upstream call difference.
/// </para>
/// <para>
/// The entry is keyed by channel id and the row timestamp: any channel update
/// changes <c>UpdatedAt</c> and replaces the exchanger, mirroring the
/// reference's cache invalidation while keeping at most one entry per channel.
/// Credential updates also rotate the access token, which changes the
/// exchanger's own cache key, so a stale entry cannot serve an old token.
/// </para>
/// <para>
/// Unlike codex/claudecode/antigravity, Copilot deliberately does not use
/// <c>IChannelTokenGrain</c>: the reference never refreshes Copilot's OAuth
/// credentials on the request path — <c>DeviceFlowProvider.GetToken</c> goes
/// straight to the exchanger — so a grain would eventually mint a synthetic
/// one-hour expiry and run a GitHub refresh grant the reference never issues.
/// </para>
/// </remarks>
public sealed class ChannelCopilotTokenCache
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _channels = [];

    /// <summary>
    /// Returns the exchanger for <paramref name="channelId"/>, building it with
    /// the channel's proxy-aware client on first use and after a row update.
    /// </summary>
    public CopilotTokenExchanger ForChannel(
        long channelId,
        DateTime updatedAt,
        IExecutor? channelExecutor)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(channelId, out var entry) && entry.UpdatedAt == updatedAt)
            {
                return entry.Exchanger;
            }

            var exchanger = new CopilotTokenExchanger(new CopilotTokenExchangerParams
            {
                HttpClient = channelExecutor,
            });
            _channels[channelId] = new Entry(updatedAt, exchanger);
            return exchanger;
        }
    }

    private sealed record Entry(DateTime UpdatedAt, CopilotTokenExchanger Exchanger);
}
