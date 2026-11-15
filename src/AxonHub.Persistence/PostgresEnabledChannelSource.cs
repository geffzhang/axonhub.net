using AxonHub.Abstractions;

namespace AxonHub.Persistence;

/// <summary>
/// Database half of the enabled-channel cache: the fingerprint query and the
/// enabled-channel projection consumed by <c>EnabledChannelsGrain</c>.
/// <para>
/// The reference's <c>reloadEnabledChannels</c> runs both queries inside the
/// cache's refresh function; the grain cannot reference this assembly, so it
/// receives them through <see cref="IEnabledChannelSource"/>. Each call creates
/// a short-lived unit of work because the consumer is a grain, not a scoped
/// request.
/// </para>
/// </summary>
public sealed class PostgresEnabledChannelSource(IAxonHubDbContextFactory dbFactory) : IEnabledChannelSource
{
    public async Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default)
    {
        await using var context = dbFactory.CreateDbContext();
        var store = new ChannelStore(context);
        return await store.GetLatestUpdatedAtAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var context = dbFactory.CreateDbContext();
        var store = new ChannelStore(context);
        var channels = await store.ListEnabledAsync(cancellationToken).ConfigureAwait(false);

        return [.. channels.Select(ToDescriptor)];
    }

    /// <summary>Projects a stored channel row into the candidate descriptor.</summary>
    internal static ChannelDescriptor ToDescriptor(ChannelRecord channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        OrderingWeight = channel.OrderingWeight,
        // Npgsql reads timestamptz as a UTC DateTime.
        CreatedAt = channel.CreatedAt,
        UpdatedAt = channel.UpdatedAt,
        Type = channel.Type,
        BaseUrl = channel.BaseUrl,
        Credentials = channel.Credentials,
        DisabledApiKeys = channel.DisabledApiKeys,
        SupportedModels = channel.SupportedModels,
        Settings = channel.Settings,
        Endpoints = channel.Endpoints,
        DefaultTestModel = channel.DefaultTestModel,
        Tags = channel.Tags,
        Policies = channel.Policies,
    };
}
