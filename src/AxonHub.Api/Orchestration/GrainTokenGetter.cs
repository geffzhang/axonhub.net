using AxonHub.Grains.Abstractions;
using AxonHub.Llm.OAuth;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// The outbound transformers' synchronous <see cref="ITokenGetter"/> seam over
/// <see cref="IChannelTokenGrain"/>: one grain per channel owns the cached
/// credentials, the refresh singleflight and the background refresh (§146).
/// </summary>
/// <remarks>
/// The reference's <c>TokenProvider.Get(ctx)</c> is synchronous, so the grain
/// call is awaited on the calling thread. The request's cancellation token
/// cannot cancel a refresh that is already queued or running in the grain; the
/// refresh completes and is persisted instead (difference §146).
/// </remarks>
public sealed class GrainTokenGetter(IGrainFactory grainFactory, ChannelTokenSeed seed) : ITokenGetter
{
    public OAuthCredentials Get(CancellationToken cancellationToken)
    {
        if (grainFactory is null)
        {
            throw new InvalidOperationException("grain factory is nil");
        }

        var grain = grainFactory.GetGrain<IChannelTokenGrain>(seed.ChannelId);
        var token = grain.GetAccessTokenAsync(seed).GetAwaiter().GetResult();
        return ChannelOAuthTokenMapping.ToCredentials(token);
    }
}
