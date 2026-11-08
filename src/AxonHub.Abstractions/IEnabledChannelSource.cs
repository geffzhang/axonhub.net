namespace AxonHub.Abstractions;

/// <summary>
/// Raw database reads behind the enabled-channel cache
/// (<c>biz.ChannelService.reloadEnabledChannels</c>, channel.go:241-309).
/// <para>
/// The cache itself lives in <c>EnabledChannelsGrain</c>, and the grains project
/// cannot reference the persistence project, so the two queries the reference's
/// refresh function runs are exposed here and implemented by
/// <c>PostgresEnabledChannelSource</c> in the silo container.
/// </para>
/// </summary>
public interface IEnabledChannelSource
{
    /// <summary>
    /// The largest <c>updated_at</c> across every channel row, including
    /// soft-deleted ones, or <see langword="null"/> when the table is empty.
    /// This is the reference's change fingerprint
    /// (<c>Order(ent.Desc(channel.FieldUpdatedAt)).First(schematype.SkipSoftDelete)</c>).
    /// </summary>
    Task<DateTime?> GetLatestUpdatedAtAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Live channels with <c>status = enabled</c>, ordered by
    /// <c>ordering_weight</c> descending, exactly like the reference query at
    /// channel.go:259-265.
    /// </summary>
    Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default);
}
