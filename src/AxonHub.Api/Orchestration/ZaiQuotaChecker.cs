using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Z.ai quota checker
/// (internal/server/biz/provider_quota/zai_checker.go): a thin specialization
/// of the ZhiPu-family collector. Z.ai serves the same payload shape from its
/// fixed api.z.ai host, so per-key collection, decoding and aggregation are
/// shared with <see cref="ZhipuQuotaChecker"/>.
/// </summary>
public sealed class ZaiQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "zai";

    /// <summary>zaiDefaultQuotaBaseURL (zai_checker.go:11).</summary>
    internal const string DefaultQuotaBaseUrl = "https://api.z.ai";

    /// <summary>
    /// zai_checker.go:22 appends the family quota path to the fixed host and
    /// ignores the channel base URL.
    /// </summary>
    internal const string QuotaUrl = DefaultQuotaBaseUrl + ZhipuQuotaChecker.QuotaPath;

    private readonly ZhipuQuotaChecker _family;

    public ZaiQuotaChecker(ZhipuQuotaChecker family)
    {
        ArgumentNullException.ThrowIfNull(family);

        _family = family;
    }

    /// <summary>
    /// Port of (*ZaiQuotaChecker).CheckQuota (zai_checker.go:21-23).
    /// </summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Task.FromResult(_family.CollectFamilyQuota(
            channel,
            ProviderType,
            QuotaUrl,
            cancellationToken));
    }

    /// <summary>
    /// Port of (*ZaiQuotaChecker).SupportsChannel (zai_checker.go:25-28).
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type is ChannelOutboundFactory.ZaiChannelType
            or ChannelOutboundFactory.ZaiAnthropicChannelType;
    }

    /// <summary>
    /// Port of parseZaiQuotaResponse (zai_checker.go:30-33): single-account
    /// parsing delegates to the shared family parser.
    /// </summary>
    internal static QuotaData ParseZaiQuotaResponse(byte[] body, DateTimeOffset now)
        => ZhipuQuotaChecker.ParseFamilyQuotaResponse(body, ProviderType, now);
}
