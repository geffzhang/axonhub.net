using AxonHub.Abstractions;
using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// One cached channel entry. <see cref="ChannelDescriptor"/> is an in-process
/// view without Orleans attributes, so the grain keeps this serializable twin
/// and the API-side provider maps it back on each read.
/// </summary>
[GenerateSerializer]
public sealed class EnabledChannelSnapshot
{
    [Id(0)]
    public long Id { get; set; }

    [Id(1)]
    public string Name { get; set; } = string.Empty;

    [Id(2)]
    public long OrderingWeight { get; set; }

    /// <summary>Ent <c>created_at</c> as read from PostgreSQL (UTC).</summary>
    [Id(3)]
    public DateTime CreatedAt { get; set; }

    [Id(4)]
    public string Type { get; set; } = string.Empty;

    [Id(5)]
    public string? BaseUrl { get; set; }

    /// <summary>Raw <c>credentials</c> jsonb text.</summary>
    [Id(6)]
    public string Credentials { get; set; } = "{}";

    /// <summary>Raw <c>disabled_api_keys</c> jsonb text.</summary>
    [Id(7)]
    public string? DisabledApiKeys { get; set; }

    /// <summary>Raw <c>supported_models</c> jsonb text.</summary>
    [Id(8)]
    public string SupportedModels { get; set; } = "null";

    /// <summary>Raw <c>settings</c> jsonb text.</summary>
    [Id(9)]
    public string? Settings { get; set; }

    /// <summary>Raw <c>endpoints</c> jsonb text.</summary>
    [Id(10)]
    public string? Endpoints { get; set; }

    [Id(11)]
    public string DefaultTestModel { get; set; } = string.Empty;

    /// <summary>Raw <c>tags</c> jsonb text.</summary>
    [Id(12)]
    public string? Tags { get; set; }

    /// <summary>Raw <c>policies</c> jsonb text.</summary>
    [Id(13)]
    public string? Policies { get; set; }

    public static EnabledChannelSnapshot FromDescriptor(ChannelDescriptor channel) => new()
    {
        Id = channel.Id,
        Name = channel.Name,
        OrderingWeight = channel.OrderingWeight,
        CreatedAt = channel.CreatedAt,
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

    /// <summary>Rebuilds the in-process view consumed by the load balancer.</summary>
    public ChannelDescriptor ToDescriptor() => new()
    {
        Id = Id,
        Name = Name,
        OrderingWeight = OrderingWeight,
        CreatedAt = CreatedAt,
        Type = Type,
        BaseUrl = BaseUrl,
        Credentials = Credentials,
        DisabledApiKeys = DisabledApiKeys,
        SupportedModels = SupportedModels,
        Settings = Settings,
        Endpoints = Endpoints,
        DefaultTestModel = DefaultTestModel,
        Tags = Tags,
        Policies = Policies,
    };
}

/// <summary>
/// The enabled-channel cache contents at one instant
/// (<c>live.Cache[[*Channel]].GetData()</c> plus <c>ChannelService.cacheVersion</c>).
/// The grain returns a fresh copy per call; callers must treat it as immutable.
/// </summary>
[GenerateSerializer]
public sealed class EnabledChannelsSnapshot
{
    /// <summary>
    /// Enabled channels in the reference query order
    /// (<c>ordering_weight DESC</c>), already filtered to what the reference
    /// would have built successfully.
    /// </summary>
    [Id(0)]
    public List<EnabledChannelSnapshot> Channels { get; set; } = [];

    /// <summary>
    /// <c>ChannelService.cacheVersion</c>: incremented on every cache swap,
    /// including forced reloads that return unchanged data.
    /// </summary>
    [Id(1)]
    public long Version { get; set; }

    /// <summary>
    /// <c>live.Cache.lastUpdate</c>: the fingerprint timestamp stored with the
    /// current contents, or <see langword="null"/> for the reference's zero
    /// <see cref="DateTime"/>.
    /// </summary>
    [Id(2)]
    public DateTime? LastUpdatedAt { get; set; }
}

/// <summary>
/// Well-known grain key. The reference keeps exactly one
/// <c>enabledChannelsCache</c> per process; the port keeps one activation for
/// the whole cluster (C226).
/// </summary>
public static class EnabledChannelsGrainKey
{
    public const string Default = "enabled-channels";
}
