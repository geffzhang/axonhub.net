using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the candidate build shared by the reference's
/// <c>SpecifiedChannelSelector.Select</c> (candidates.go:1026-1078) and the
/// tester's per-attempt candidate factory (tester.go): load the channel again
/// for every attempt, resolve the request model against its entries, apply the
/// forced API formats and pick the format for this request.
/// </summary>
internal static class SpecifiedChannelCandidateFactory
{
    /// <summary>
    /// Port of the SpecifiedChannelSelector.Select candidate build. The channel
    /// is re-read per attempt, exactly like the reference's selector, so a
    /// concurrent edit is observed; an empty API key means the selector's plain
    /// GetChannel path.
    /// </summary>
    public static async Task<ChannelCandidate?> BuildCandidateAsync(
        IChannelWriteStore channels,
        long channelId,
        LlmRequest llmRequest,
        string? apiKeyOverride,
        CancellationToken cancellationToken)
    {
        ChannelRecord channel;
        try
        {
            channel = await LoadChannelAsync(channels, channelId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get channel for test: " + ex.Message,
                ex);
        }

        var descriptor = ChannelMapping.ToDescriptor(channel);
        var model = llmRequest.Model;

        if (!ChannelModelEntries.Build(descriptor).TryGetValue(model, out var entry)
            && !ChannelModelEntries.BuildDirect(descriptor).TryGetValue(model, out entry))
        {
            throw new InvalidOperationException(
                "model " + model + " not supported in channel " + descriptor.Name);
        }

        var endpoints = ChannelEndpointSelection.ApplyForcedApiFormatsForRequest(
            descriptor,
            [entry],
            model,
            llmRequest.RequestType,
            ChannelEndpoints.Resolve(descriptor.Type, descriptor.Endpoints, descriptor.BaseUrl));

        // A rejected explicit-format request has no endpoints; SelectAPIFormat
        // then yields "" and the candidate is rejected below.
        var apiFormat = ChannelEndpointSelection.SelectApiFormat(
            endpoints ?? [],
            llmRequest);

        if (ChannelEndpointSelection.RequiresExplicitEndpoint(llmRequest.RequestType)
            && apiFormat.Length == 0)
        {
            // The reference returns an empty candidate list, which the caller
            // reports as the invalid-model error.
            return null;
        }

        return new ChannelCandidate
        {
            ChannelId = descriptor.Id,
            ChannelName = descriptor.Name,
            ActualModel = entry.ActualModel,
            OrderingWeight = descriptor.OrderingWeight,
            Score = 0,
            UpdatedAt = descriptor.UpdatedAt,
            Type = descriptor.Type,
            BaseUrl = descriptor.BaseUrl,
            Credentials = descriptor.Credentials,
            DisabledApiKeys = descriptor.DisabledApiKeys,
            RetrySettings = ChannelRetrySettings.Parse(descriptor.Settings),
            OverrideSettings = ChannelOverrideSettings.Parse(descriptor.Settings),
            RateLimitSettings = ChannelRateLimitSettings.Parse(descriptor.Settings),
            PassThroughSettings = ChannelPassThroughSettings.Parse(descriptor.Settings),
            DefaultTestModel = ResolveDefaultTestModel(descriptor),
            ApiFormat = apiFormat,
            ApiKeyOverride = string.IsNullOrEmpty(apiKeyOverride) ? null : apiKeyOverride,
            Settings = descriptor.Settings,
            Endpoints = descriptor.Endpoints,
            Tags = descriptor.Tags,
            Policies = descriptor.Policies,
        };
    }

    /// <summary>
    /// Port of the default-test-model resolution used by ChannelPoolGrain
    /// (channel_llm.go:304-317), shared here because the tester's candidate
    /// carries the same field.
    /// </summary>
    public static string ResolveDefaultTestModel(ChannelDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var model = channel.DefaultTestModel.Trim();
        if (model.Length == 0)
        {
            return model;
        }

        return ChannelModelEntries.ChooseModel(channel, model) ?? model;
    }

    /// <summary>
    /// Port of the reference's channel load in ChannelService.GetChannel
    /// (channel.go:397-418): ent.Get, with not-found and store failures carrying
    /// the ent wording.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist.</exception>
    public static async Task<ChannelRecord> LoadChannelAsync(
        IChannelWriteStore channels,
        long channelId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);

        try
        {
            return await channels.GetAsync(channelId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "channel not found: ent: channel not found");
        }
        catch (StoreFailureException ex)
        {
            throw new InvalidOperationException("channel not found: " + ex.Message, ex);
        }
    }
}
