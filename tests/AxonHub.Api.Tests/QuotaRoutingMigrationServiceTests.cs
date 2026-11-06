using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.quotaRoutingMigrator (internal/server/biz/quota_routing_migration.go)
/// against the port: the claim marker, the legacy quota enforcement mapping, the
/// per-channel settings rewrite through ChannelService.UpdateChannel, the
/// skip-missing-channel rule and every error prefix. The shared-table
/// transaction composition (system keys and the channel settings update commit
/// or roll back together) is locked against PostgreSQL in
/// ChannelWriteStoreTests and SystemStoreTests; these cases drive the service
/// over in-memory stores.
/// </summary>
public sealed class QuotaRoutingMigrationServiceTests
{
    private const string LegacyKey = "quota_enforcement_settings";
    private const string RoutingKey = "quota_routing_settings";
    private const string MarkerKey = QuotaRoutingMigrationService.MigrationDoneKey;

    private readonly FakeSystemStore _systems = new();
    private readonly FakeChannelWriteStore _channels = new();
    private readonly FakeChannelModelPriceStore _prices = new();
    private readonly FakeMigrationChannelStore _channelReads;
    private readonly QuotaRoutingMigrationService _migration;

    public QuotaRoutingMigrationServiceTests()
    {
        _channelReads = new FakeMigrationChannelStore(_channels);
        _migration = NewMigration();
    }

    // ------------------------------------------------------- reference cases

    [Fact]
    public async Task FreshMigrationUsesSystemBypassAndPreservesSettings()
    {
        SeedLegacy(enabled: true, dePrioritize: true, allowed: [1, 2]);
        var first = SeedChannel(1, """{"modelMappings":[{"from":"alias","to":"model"}]}""");
        var second = SeedChannel(2, """{"modelMappings":[]}""");

        await _migration.MigrateAsync();

        Assert.Equal("""{"defaultMode":"backpressure"}""", StoredRoutingSettings());
        Assert.Equal("ignore_quota", ChannelMode(first));
        Assert.Equal("ignore_quota", ChannelMode(second));
        AssertModelMappingPreserved(first);
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task RerunIsIdempotent()
    {
        SeedLegacy(enabled: true, exhaustedOnly: true, allowed: [1]);
        var channel = SeedChannel(1, """{"modelMappings":[]}""");

        await _migration.MigrateAsync();
        await _migration.MigrateAsync();

        Assert.Equal("remove_on_exhausted", DefaultMode());
        Assert.Equal("ignore_quota", ChannelMode(channel));
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task ConcurrentStartupMigrationIsIdempotent()
    {
        SeedLegacy(enabled: true, allowed: [1]);
        var channel = SeedChannel(1, """{"modelMappings":[]}""");

        // Four starts over the same database, like the reference's real-Postgres
        // test: the fake transaction runner serializes them the way the claim
        // insert's row lock does.
        var starts = Enumerable.Range(0, 4)
            .Select(_ => NewMigration().MigrateAsync())
            .ToArray();
        await Task.WhenAll(starts);

        Assert.Equal("ignore_quota", DefaultMode());
        Assert.Equal("ignore_quota", ChannelMode(channel));
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task MissingChannelsAreSkipped()
    {
        SeedLegacy(enabled: true, exhaustedOnly: true, allowed: [1, 99]);
        var channel = SeedChannel(1, """{"modelMappings":[]}""");

        await _migration.MigrateAsync();

        Assert.Equal("ignore_quota", ChannelMode(channel));
        Assert.Equal("remove_on_exhausted", DefaultMode());
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task NilChannelSettingsAreMigrated()
    {
        SeedLegacy(enabled: true, allowed: [1]);
        var channel = SeedChannel(1, settings: null);

        await _migration.MigrateAsync();

        Assert.Equal("ignore_quota", ChannelMode(channel));
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task ExistingNewSettingsDoNotSuppressChannelMigration()
    {
        SeedLegacy(enabled: true, dePrioritize: true, allowed: [1]);
        _systems.Add(RoutingKey, """{"defaultMode":"ignore_quota"}""");
        var channel = SeedChannel(1, """{"modelMappings":[]}""");

        await _migration.MigrateAsync();

        Assert.Equal("ignore_quota", DefaultMode());
        Assert.Equal("ignore_quota", ChannelMode(channel));
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task EmptyAllowedChannelsOnlyWritesModeAndMarker()
    {
        SeedLegacy(enabled: false);

        await _migration.MigrateAsync();

        Assert.Equal("""{"defaultMode":"ignore_quota"}""", StoredRoutingSettings());
        Assert.Equal("true", MarkerValue());
        Assert.Empty(_channels.UpdateCalls);
    }

    [Fact]
    public async Task AbsentLegacySettingsLeaveDefaultsUntouched()
    {
        await _migration.MigrateAsync();

        Assert.DoesNotContain(_systems.Rows, row => row.Key == RoutingKey);
        Assert.Equal("true", MarkerValue());
    }

    [Fact]
    public async Task ChannelFailureRollsBackAllMigrationWrites()
    {
        SeedLegacy(enabled: true, dePrioritize: true, allowed: [1, 2]);
        SeedChannel(1, """{"modelMappings":[{"from":"alias","to":"model"}]}""");
        SeedChannel(2, """{"modelMappings":[]}""");
        _channels.UpdateFailuresById[2] = new InvalidOperationException(
            "injected channel update failure");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());
        Assert.Equal(
            "failed to migrate channel 2: injected channel update failure",
            error.Message);

        // The transaction owned by the system store rolled back its claim and
        // its routing settings; the channel-table half of the same rollback is
        // locked by ChannelWriteStoreTests.UpdateJoinsAnAmbientTransaction.
        Assert.DoesNotContain(_systems.Rows, row => row.Key == MarkerKey);
        Assert.DoesNotContain(_systems.Rows, row => row.Key == RoutingKey);

        // The failure is transient: the next startup migrates everything.
        _channels.UpdateFailuresById.Clear();
        await _migration.MigrateAsync();

        Assert.Equal("backpressure", DefaultMode());
        Assert.Equal("ignore_quota", ChannelMode(_channels.LiveRows[0]));
        Assert.Equal("ignore_quota", ChannelMode(_channels.LiveRows[1]));
        Assert.Equal("true", MarkerValue());
    }

    // ----------------------------------------------------- error wrappers

    [Fact]
    public async Task ClaimFailureSurfacesTheClaimPrefix()
    {
        _systems.CreateIfAbsentFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal("failed to claim quota routing migration: boom", error.Message);
    }

    [Fact]
    public async Task MarkerReadFailureSurfacesTheMarkerPrefix()
    {
        _systems.GetFailuresByKey[MarkerKey] = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal("failed to read quota routing migration marker: boom", error.Message);
    }

    [Fact]
    public async Task ASoftDeletedMarkerIsReportedAsNotFound()
    {
        // The unique index includes soft-deleted rows, so the claim insert does
        // nothing and ent's Only() reports not found.
        _systems.Add(MarkerKey, "old", row => row.DeletedAt = 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal(
            "failed to read quota routing migration marker: ent: system not found",
            error.Message);
    }

    [Fact]
    public async Task DecodeFailureSurfacesTheDecodePrefix()
    {
        _systems.Add(LegacyKey, "{");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.StartsWith(
            "failed to decode legacy quota enforcement settings: ",
            error.Message,
            StringComparison.Ordinal);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task LegacyReadFailureSurfacesTheDoublePrefix()
    {
        _systems.GetFailuresByKey[LegacyKey] = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal(
            "failed to read legacy quota enforcement settings: "
            + "failed to get system value: boom",
            error.Message);
    }

    [Fact]
    public async Task RoutingSettingsCheckFailureSurfacesItsPrefix()
    {
        SeedLegacy(enabled: true);
        _systems.GetFailuresByKey[RoutingKey] = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal(
            "failed to check quota routing settings: failed to get system value: boom",
            error.Message);
    }

    [Fact]
    public async Task RoutingSettingsWriteFailureSurfacesTheDoublePrefix()
    {
        SeedLegacy(enabled: true);
        _systems.SetFailuresByKey[RoutingKey] = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal(
            "failed to migrate quota routing settings: "
            + "failed to create system setting: boom",
            error.Message);
    }

    [Fact]
    public async Task FinalMarkerWriteFailureSurfacesItsPrefix()
    {
        SeedLegacy(enabled: true);
        _systems.SetFailuresByKey[MarkerKey] = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal(
            "failed to write quota routing migration marker: "
            + "failed to create system setting: boom",
            error.Message);
    }

    [Fact]
    public async Task AbsentLegacyMarkerWriteFailureIsNotDoubleWrapped()
    {
        _systems.SetFailuresByKey[MarkerKey] = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal("failed to create system setting: boom", error.Message);
    }

    [Fact]
    public async Task ChannelLoadFailureSurfacesItsPrefix()
    {
        SeedLegacy(enabled: true, allowed: [1]);
        SeedChannel(1, """{"modelMappings":[]}""");
        _channelReads.GetFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _migration.MigrateAsync());

        Assert.Equal(
            "failed to load channel 1 during quota routing migration: boom",
            error.Message);
    }

    // ------------------------------------------------------------- helpers

    private QuotaRoutingMigrationService NewMigration()
        => new(
            _systems,
            _channelReads,
            new ChannelLifecycleService(_channels, new ChannelLimiterManager(), _prices),
            NullLogger<QuotaRoutingMigrationService>.Instance);

    private void SeedLegacy(
        bool enabled,
        bool exhaustedOnly = false,
        bool dePrioritize = false,
        string mode = "",
        params long[] allowed)
        => _systems.Add(
            LegacyKey,
            JsonSerializer.Serialize(new
            {
                enabled,
                exhaustedOnly,
                dePrioritize,
                allowedChannelIDs = allowed,
                mode,
            }));

    private ChannelRecord SeedChannel(long id, string? settings)
    {
        var channel = new ChannelRecord
        {
            Name = "channel-" + id,
            Type = "openai",
            BaseUrl = "https://api.openai.com",
            Status = "enabled",
            Credentials = """{"apiKeys":["key"]}""",
            SupportedModels = """["gpt-4"]""",
            DefaultTestModel = "gpt-4",
            Settings = settings,
            Policies = """{"stream":"unlimited"}""",
            Endpoints = "[]",
        };
        _channels.Seed(channel);
        Assert.Equal(id, channel.Id);
        return channel;
    }

    private string? MarkerValue()
        => _systems.Rows
            .SingleOrDefault(row => row.Key == MarkerKey && row.DeletedAt == 0)
            ?.Value;

    /// <summary>The raw stored value, as Go's json.Marshal would write it.</summary>
    private string? StoredRoutingSettings()
        => _systems.Rows
            .SingleOrDefault(row => row.Key == RoutingKey && row.DeletedAt == 0)
            ?.Value;

    private string? DefaultMode()
    {
        var value = StoredRoutingSettings();
        if (value is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value);
        return document.RootElement.GetProperty("defaultMode").GetString();
    }

    private static string? ChannelMode(ChannelRecord channel)
    {
        if (channel.Settings is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(channel.Settings);
        return document.RootElement.TryGetProperty("quotaRoutingMode", out var mode)
            ? mode.GetString()
            : null;
    }

    private static void AssertModelMappingPreserved(ChannelRecord channel)
    {
        Assert.NotNull(channel.Settings);
        using var document = JsonDocument.Parse(channel.Settings);
        var mappings = document.RootElement.GetProperty("modelMappings");
        Assert.Equal(1, mappings.GetArrayLength());
        Assert.Equal("alias", mappings[0].GetProperty("from").GetString());
        Assert.Equal("model", mappings[0].GetProperty("to").GetString());
    }

    /// <summary>
    /// The migration reads channels through the plain Channel.Query().Only
    /// equivalent; this adapter shares the write store's in-memory table so
    /// both halves see the same rows.
    /// </summary>
    private sealed class FakeMigrationChannelStore(FakeChannelWriteStore rows) : IChannelStore
    {
        public Exception? GetFailure { get; set; }

        public Task<long> CreateAsync(
            ChannelRecord channel,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ChannelRecord?> GetByIdAsync(
            long id,
            CancellationToken cancellationToken = default)
        {
            if (GetFailure is not null)
            {
                return Task.FromException<ChannelRecord?>(GetFailure);
            }

            return Task.FromResult(
                rows.LiveRows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
        }

        public Task<IReadOnlyList<ChannelRecord>> ListEnabledAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ChannelRecord>> ListEnabledByTypesAsync(
            IReadOnlyCollection<string> types,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> UpdateStatusAsync(
            long id,
            string status,
            string? errorMessage,
            DateTime updatedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
