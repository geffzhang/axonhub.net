using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks ChannelService.cleanupExpiredDisabledAPIKeys
/// (channel_apikey.go:393-516, C285): elapsed temporary disable records are
/// pruned, a channel disabled only because every credential was unavailable is
/// re-enabled, and a disabled channel whose auto-disable deadline passed is
/// recovered. The pass runs under the reference's system bypass, keeps going
/// after per-channel failures, and requests exactly one enabled-channel reload
/// when anything changed.
/// </summary>
public sealed class ChannelCredentialRecoveryTests
{
    private readonly FakeChannelWriteStore _channels = new();
    private readonly RecordingEnabledChannelsRefresher _refresher = new();

    [Fact]
    public async Task ExpiredDisableRecordsArePrunedAndThePassRequestsOneReload()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "prune",
            "enabled",
            """{"apiKey":"sk-live"}""",
            [
                Disabled("sk-old", now.AddHours(-1)),
                Disabled("sk-live", now.AddHours(1)),
            ]);

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        var state = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Equal(channel.Id, state.Id);
        Assert.Null(state.CredentialsJson);
        Assert.Null(state.Status);

        var remaining = ChannelWriteSerialization.ParseDisabledApiKeys(
            state.DisabledApiKeysJson);
        Assert.Equal("sk-live", Assert.Single(remaining).Key);
        Assert.Equal("enabled", channel.Status);
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task AChannelDisabledBecauseAllKeysWereUnavailableIsReEnabled()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "recover-keys",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [Disabled("sk-live", now.AddHours(-1))],
            errorMessage: "All API keys disabled (last error: 429)",
            autoDisabledAt: now.AddHours(-2));

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        var state = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Equal("enabled", state.Status);
        Assert.True(state.ClearErrorMessage);
        Assert.True(state.ClearAutoDisabledAt);
        Assert.True(state.ClearAutoDisableExpiresAt);

        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);
        Assert.Null(channel.AutoDisableExpiresAt);
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task AnActiveDisableRecordKeepsTheAllKeysDisabledStatus()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "keep-disabled",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [
                Disabled("sk-old", now.AddHours(-1)),
                Disabled("sk-live", now.AddHours(1)),
            ],
            errorMessage: "All API keys disabled (last error: 500)",
            autoDisabledAt: now.AddHours(-2));

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        // The expired record is pruned, but sk-live still has an active
        // disable record, so applyRecoveredChannelStatus leaves the row alone.
        var state = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Null(state.Status);
        Assert.False(state.ClearErrorMessage);
        Assert.Equal("disabled", channel.Status);
        Assert.Equal("All API keys disabled (last error: 500)", channel.ErrorMessage);
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task AChannelWithAnotherErrorKeepsItsStatusWhenExpiredRecordsArePruned()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "keep-other-error",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [Disabled("sk-old", now.AddHours(-1))],
            errorMessage: "boom",
            autoDisabledAt: now.AddHours(-2));

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        var state = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Null(state.Status);
        Assert.False(state.ClearErrorMessage);
        Assert.Equal("disabled", channel.Status);
        Assert.Equal("boom", channel.ErrorMessage);
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task AnExpiredAutoDisableIsRecoveredAndClearsTheMarkers()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "auto-disabled",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [],
            errorMessage: "boom",
            autoDisabledAt: now.AddHours(-2),
            autoDisableExpiresAt: now.AddHours(-1));

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        var recovery = Assert.Single(_channels.ExpiredAutoDisableRecoveries);
        Assert.Equal(channel.Id, recovery.Id);
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);
        Assert.Null(channel.AutoDisableExpiresAt);
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task AnAutoDisableWithoutADeadlineIsLeftAlone()
    {
        var channel = Seed(
            "no-deadline",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [],
            errorMessage: "boom",
            autoDisabledAt: DateTime.UtcNow.AddHours(-2));

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        Assert.Empty(_channels.ExpiredAutoDisableRecoveries);
        Assert.Equal("disabled", channel.Status);
        Assert.Equal(0, _refresher.RequestCount);
    }

    [Fact]
    public async Task AnAutoDisableInTheFutureIsLeftAlone()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "still-disabled",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [],
            errorMessage: "boom",
            autoDisabledAt: now,
            autoDisableExpiresAt: now.AddHours(1));

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        Assert.Empty(_channels.ExpiredAutoDisableRecoveries);
        Assert.Equal("disabled", channel.Status);
        Assert.Equal(0, _refresher.RequestCount);
    }

    [Fact]
    public async Task ArchivedAndDeletedChannelsAreNotPartOfTheCandidateQueries()
    {
        var now = DateTime.UtcNow;
        var archived = Seed(
            "archived",
            "archived",
            """{"apiKey":"sk-live"}""",
            [Disabled("sk-old", now.AddHours(-1))]);
        var deleted = Seed(
            "deleted",
            "enabled",
            """{"apiKey":"sk-live"}""",
            [Disabled("sk-old", now.AddHours(-1))],
            deletedAt: 1234);

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        Assert.Empty(_channels.ApiKeyStateCalls);
        Assert.Equal("archived", archived.Status);
        Assert.Single(
            ChannelWriteSerialization.ParseDisabledApiKeys(archived.DisabledApiKeys));
        Assert.Equal("enabled", deleted.Status);
        Assert.Single(
            ChannelWriteSerialization.ParseDisabledApiKeys(deleted.DisabledApiKeys));
        Assert.Equal(0, _refresher.RequestCount);
    }

    [Fact]
    public async Task ACandidateQueryFailureSkipsTheWholePass()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "query-failure",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [],
            errorMessage: "boom",
            autoDisabledAt: now.AddHours(-2),
            autoDisableExpiresAt: now.AddHours(-1));
        _channels.CleanupQueryFailure =
            new StoreFailureException("query failed", new InvalidOperationException());

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        // The reference returns before the auto-disable pass, so even an
        // expired auto-disable row is untouched.
        Assert.Empty(_channels.ExpiredAutoDisableRecoveries);
        Assert.Equal("disabled", channel.Status);
        Assert.Equal(0, _refresher.RequestCount);
    }

    [Fact]
    public async Task APerChannelFailureDoesNotStopTheRemainingChannels()
    {
        var now = DateTime.UtcNow;
        var failing = Seed(
            "failing",
            "enabled",
            """{"apiKey":"sk-live"}""",
            [Disabled("sk-old", now.AddHours(-1))]);
        var healthy = Seed(
            "healthy",
            "enabled",
            """{"apiKey":"sk-live"}""",
            [Disabled("sk-old", now.AddHours(-1))]);
        _channels.GetFailuresById[failing.Id] =
            new StoreFailureException("boom", new InvalidOperationException());

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        var state = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Equal(healthy.Id, state.Id);
        Assert.Empty(
            ChannelWriteSerialization.ParseDisabledApiKeys(state.DisabledApiKeysJson));
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task AnAutoDisableRecoveryFailureLeavesTheChannelDisabled()
    {
        var now = DateTime.UtcNow;
        var channel = Seed(
            "recovery-failure",
            "disabled",
            """{"apiKey":"sk-live"}""",
            [],
            errorMessage: "boom",
            autoDisabledAt: now.AddHours(-2),
            autoDisableExpiresAt: now.AddHours(-1));
        _channels.ExpiredAutoDisableRecoverFailure =
            new StoreFailureException("boom", new InvalidOperationException());

        await CreateService().CleanupExpiredDisabledApiKeysAsync();

        Assert.Empty(_channels.ExpiredAutoDisableRecoveries);
        Assert.Equal("disabled", channel.Status);
        Assert.Equal(0, _refresher.RequestCount);
    }

    private ChannelLifecycleService CreateService()
        => new(
            _channels,
            new ChannelLimiterManager(),
            new FakeChannelModelPriceStore(),
            enabledChannelsRefresher: _refresher);

    private ChannelRecord Seed(
        string name,
        string status,
        string credentials,
        IReadOnlyList<DisabledApiKey> disabledKeys,
        string? errorMessage = null,
        DateTime? autoDisabledAt = null,
        DateTime? autoDisableExpiresAt = null,
        long deletedAt = 0)
    {
        var now = DateTime.UtcNow;
        var channel = new ChannelRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deletedAt,
            Type = "openai",
            Name = name,
            Status = status,
            Credentials = credentials,
            DisabledApiKeys =
                ChannelWriteSerialization.SerializeDisabledApiKeys(disabledKeys),
            SupportedModels = "null",
            Policies = ChannelWriteSerialization.DefaultPoliciesJson,
            Settings = "null",
            Endpoints = "[]",
            ErrorMessage = errorMessage,
            AutoDisabledAt = autoDisabledAt,
            AutoDisableExpiresAt = autoDisableExpiresAt,
        };

        _channels.Seed(channel);
        return channel;
    }

    private static DisabledApiKey Disabled(string key, DateTime expiresAt)
        => new()
        {
            Key = key,
            DisabledAt = expiresAt.AddHours(-1),
            ErrorCode = 429,
            ExpiresAt = expiresAt,
        };
}
