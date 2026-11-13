using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the build half of <c>ChannelService.reloadEnabledChannels</c>
/// (internal/server/biz/channel.go:265-285): the reference builds every enabled
/// row with <c>buildChannelWithOutbounds</c> and logs a warning, skipping the
/// row, when that build fails. The port's descriptors are pure projections, so
/// the failure half of that loop lives here: the raw source's rows are filtered
/// through <see cref="ChannelBuildValidation"/> before the enabled-channel cache
/// grain swaps them in.
/// </summary>
/// <remarks>
/// The reference's cache is the only channel set the model list, the load
/// balancer and the diagnostics export read (C178); a channel whose outbound
/// cannot be built (missing credentials, an unknown type, a Command Code
/// non-HTTPS base URL, an unbuildable endpoint) is invisible until a row change
/// makes the refresh try again. The port builds outbounds per attempt, so this
/// decorator constructs and discards one outbound per endpoint at refresh time;
/// the reference keeps them and reuses them. That difference is registered as
/// C287.
/// </remarks>
public sealed class BuildFilteringEnabledChannelSource(
    IEnabledChannelSource inner,
    TimeProvider timeProvider,
    ILogger<BuildFilteringEnabledChannelSource>? logger = null) : IEnabledChannelSource
{
    private static readonly Action<ILogger, string, string, Exception?> LogBuildFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(0, "EnabledChannelBuildFailed"),
            "Failed to build channel, skipping it from the enabled-channel cache channel={Channel} type={Type}");

    public Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default)
        => inner.GetLatestUpdatedAtAsync(cancellationToken);

    public async Task<ChannelDescriptor[]> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        var channels = await inner.ListEnabledAsync(cancellationToken).ConfigureAwait(false);
        if (channels.Length == 0)
        {
            return channels;
        }

        // reloadEnabledChannels runs one refresh; the reference's build reads
        // time.Now() per channel. A single timestamp keeps the key-expiry
        // decisions of one refresh consistent.
        var now = timeProvider.GetUtcNow();
        var built = new List<ChannelDescriptor>(channels.Length);
        foreach (var channel in channels)
        {
            var error = ChannelBuildValidation.Validate(channel, now);
            if (error is null)
            {
                built.Add(channel);
                continue;
            }

            if (logger is not null)
            {
                LogBuildFailed(logger, channel.Name, channel.Type, error);
            }
        }

        return [.. built];
    }
}

/// <summary>
/// Port of the pass/fail half of <c>buildChannelWithOutbounds</c>
/// (internal/server/biz/channel_llm.go:208-258): builds the primary outbound,
/// the separately-built default endpoints (xai's responses entry) and every
/// user-configured endpoint, returning the first construction failure.
/// </summary>
/// <remarks>
/// Only the observable outcome matters here: the reference keeps the built
/// outbound map, while the port discards it and rebuilds per attempt. The
/// primary build runs through <see cref="ChannelOutboundFactory.Create"/>, which
/// reproduces the reference's fail-fast credential checks and Command Code URL
/// validation; a user endpoint is built through the same dispatch so its
/// endpoint base URL, path and transport take part in construction. Duplicate
/// user endpoints that share an API format are all validated against the last
/// stored one (the factory's format-keyed selection), so a failure in a shadowed
/// duplicate is not detected (C287).
/// </remarks>
public static class ChannelBuildValidation
{
    /// <summary>
    /// Returns the build failure for one enabled row, or <see langword="null"/>
    /// when the reference would have built it successfully.
    /// </summary>
    public static Exception? Validate(ChannelDescriptor channel, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var candidate = new ChannelCandidate
        {
            ChannelId = channel.Id,
            ChannelName = channel.Name,
            OrderingWeight = channel.OrderingWeight,
            Type = channel.Type,
            BaseUrl = channel.BaseUrl,
            Credentials = channel.Credentials,
            DisabledApiKeys = channel.DisabledApiKeys,
            DefaultTestModel = channel.DefaultTestModel,
            Settings = channel.Settings,
            Endpoints = channel.Endpoints,
            Tags = channel.Tags,
            Policies = channel.Policies,
            UpdatedAt = channel.UpdatedAt,
        };

        try
        {
            var primaryFormat = ChannelOutboundFactory.PrimaryApiFormat(channel.Type);

            // buildChannelWithTransformer always builds the primary first and
            // its failure aborts the whole channel (channel_llm.go:650-1440).
            Build(candidate, primaryFormat ?? ApiFormats.OpenAIChatCompletion, now);

            // channel_llm.go:222-235: every default endpoint reuses the primary
            // outbound except xai's extra responses entry, which is built
            // separately by buildNonDefaultEndpointOutbound.
            if (channel.Type == ChannelOutboundFactory.XaiChannelType)
            {
                foreach (var endpoint in ChannelEndpoints.DefaultsForChannelType(channel.Type))
                {
                    if (endpoint.ApiFormat.Length == 0 || endpoint.ApiFormat == primaryFormat)
                    {
                        continue;
                    }

                    Build(candidate, endpoint.ApiFormat, now);
                }
            }

            // channel_llm.go:237-258: every user endpoint is built by API
            // format, and any failure skips the channel.
            foreach (var endpoint in ChannelEndpoints.ParseUser(channel.Endpoints))
            {
                if (endpoint.ApiFormat.Length == 0)
                {
                    continue;
                }

                Build(candidate, endpoint.ApiFormat, now);
            }

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex;
        }
    }

    private static void Build(ChannelCandidate candidate, string apiFormat, DateTimeOffset now)
        => _ = ChannelOutboundFactory.Create(
            candidate,
            new LlmRequest { ApiFormat = apiFormat },
            now);
}
