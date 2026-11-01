using AxonHub.Abstractions;

namespace AxonHub.Persistence;

/// <summary>
/// Reads the enabled-channel snapshot the load balancer selects from. Each call
/// creates a short-lived unit of work because the consumer (an Orleans grain) is
/// a singleton, not a scoped request.
/// </summary>
public sealed class PostgresEnabledChannelProvider(IAxonHubDbContextFactory dbFactory) : IEnabledChannelProvider
{
    public async Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var context = dbFactory.CreateDbContext();
        var store = new ChannelStore(context);
        var channels = await store.ListEnabledAsync(cancellationToken).ConfigureAwait(false);

        return [.. channels.Select(channel => new ChannelDescriptor
        {
            Id = channel.Id,
            Name = channel.Name,
            OrderingWeight = channel.OrderingWeight,
            // Npgsql reads timestamptz as a UTC DateTime.
            CreatedAt = channel.CreatedAt,
            Type = channel.Type,
            BaseUrl = channel.BaseUrl,
            Credentials = channel.Credentials,
            DisabledApiKeys = channel.DisabledApiKeys,
            SupportedModels = channel.SupportedModels,
            Settings = channel.Settings,
        })];
    }
}
