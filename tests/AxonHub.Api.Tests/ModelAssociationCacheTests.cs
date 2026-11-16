using System.Text.Json.Nodes;
using AxonHub.Api.Auth;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the five-minute association resolution cache
/// (orchestrator/candidates.go:250-394): the cached intermediate form keeps the
/// <c>When</c> conditions for per-request filtering, and a hit requires the same
/// association signature, channel count, latest channel update time, latest
/// model update time, channel cache version and an age below the TTL.
/// </summary>
public sealed class ModelAssociationCacheTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    private static readonly DateTime ChannelUpdatedAt =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static readonly DateTime ModelUpdatedAt =
        new(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly FakeModelStore _models = new();
    private readonly FakeEnabledChannelCacheSnapshotProvider _snapshots = new();
    private readonly FakeChannelStore _channelStore = new();
    private readonly ModelAssociationCache _cache = new();
    private readonly TestTimeProvider _clock = new(Now);

    [Fact]
    public async Task SelectionReadsTheEnabledSnapshotAndPopulatesTheSharedCache()
    {
        SeedModel();
        SeedSnapshotWithVersion([Channel(1, "Alpha", ChannelUpdatedAt, "upstream")], version: 7);

        var candidates = await Select();

        var candidate = Assert.Single(candidates);
        Assert.Equal(1, candidate.ChannelId);
        Assert.Equal("Alpha", candidate.ChannelName);

        // The association path reads the enabled-channel grain snapshot, not a
        // per-request database query.
        Assert.Equal(0, _channelStore.ListEnabledCalls);

        var entry = Assert.Single(_cache.ReadAssociationCache());
        Assert.Equal("alias", entry.ModelId);
        Assert.Equal(1, entry.CandidateCount);
        Assert.Equal(1, entry.ChannelCount);
        Assert.Equal(7, entry.ChannelCacheVersion);
        Assert.Equal(Now, entry.CachedAt);
        Assert.Equal(new DateTimeOffset(ChannelUpdatedAt, TimeSpan.Zero), entry.LatestChannelUpdateTime);
        Assert.Equal(new DateTimeOffset(ModelUpdatedAt, TimeSpan.Zero), entry.LatestModelUpdateTime);
        var association = Assert.Single(entry.Associations);
        Assert.Equal(ModelAssociation.TypeChannelModel, association.Type);
    }

    [Fact]
    public async Task CacheHitReusesTheResolvedChannelsUntilTheirUpdateTimeMoves()
    {
        SeedModel();
        SeedSnapshot(Channel(1, "Alpha", ChannelUpdatedAt, "upstream"));

        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);

        // Same count, version and update time: the cached candidate keeps the
        // channel object it was resolved with, exactly like the reference.
        _snapshots.Snapshot.Channels[0].Name = "Beta";
        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);
        Assert.Equal(1, Assert.Single(_cache.ReadAssociationCache()).CandidateCount);

        // A newer channel update time invalidates the entry.
        _snapshots.Snapshot.Channels[0].UpdatedAt = ChannelUpdatedAt.AddHours(1);
        Assert.Equal("Beta", Assert.Single(await Select()).ChannelName);

        var entry = Assert.Single(_cache.ReadAssociationCache());
        Assert.Equal(
            new DateTimeOffset(ChannelUpdatedAt.AddHours(1), TimeSpan.Zero),
            entry.LatestChannelUpdateTime);
    }

    [Fact]
    public async Task ChannelCacheVersionChangeInvalidatesTheEntry()
    {
        SeedModel();
        SeedSnapshotWithVersion([Channel(1, "Alpha", ChannelUpdatedAt, "upstream")], version: 3);

        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);

        _snapshots.Snapshot.Channels[0].Name = "Beta";
        _snapshots.Snapshot.Version = 4;

        Assert.Equal("Beta", Assert.Single(await Select()).ChannelName);
        Assert.Equal(4, Assert.Single(_cache.ReadAssociationCache()).ChannelCacheVersion);
    }

    [Fact]
    public async Task AssociationSignatureChangeInvalidatesTheEntry()
    {
        SeedModel();
        SeedSnapshot(Channel(1, "Alpha", ChannelUpdatedAt, "upstream"));

        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);

        // The model's updated_at stays put: only the association document
        // changes, so the signature alone must invalidate the entry.
        SetModelAssociations(new ModelAssociation
        {
            Type = ModelAssociation.TypeChannelModel,
            Priority = 9,
            ChannelModel = new ChannelModelAssociation(1, "upstream"),
        });
        _snapshots.Snapshot.Channels[0].Name = "Beta";

        var candidate = Assert.Single(await Select());
        Assert.Equal("Beta", candidate.ChannelName);
        Assert.Equal(9, candidate.Priority);
    }

    [Fact]
    public async Task ModelUpdateTimeChangeInvalidatesTheEntry()
    {
        SeedModel();
        SeedSnapshot(Channel(1, "Alpha", ChannelUpdatedAt, "upstream"));

        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);

        _models.Rows[0].UpdatedAt = ModelUpdatedAt.AddHours(2);
        _snapshots.Snapshot.Channels[0].Name = "Beta";

        Assert.Equal("Beta", Assert.Single(await Select()).ChannelName);
        Assert.Equal(
            new DateTimeOffset(ModelUpdatedAt.AddHours(2), TimeSpan.Zero),
            Assert.Single(_cache.ReadAssociationCache()).LatestModelUpdateTime);
    }

    [Fact]
    public async Task EntryExpiresAfterFiveMinutesEvenWithoutChanges()
    {
        SeedModel();
        SeedSnapshot(Channel(1, "Alpha", ChannelUpdatedAt, "upstream"));

        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);

        _snapshots.Snapshot.Channels[0].Name = "Beta";
        Assert.Equal("Alpha", Assert.Single(await Select()).ChannelName);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("Beta", Assert.Single(await Select()).ChannelName);
        Assert.Equal(Now.AddMinutes(5), Assert.Single(_cache.ReadAssociationCache()).CachedAt);
    }

    [Fact]
    public async Task ConditionalAssociationsShareOneCachedResolution()
    {
        SeedSnapshot(
            Channel(1, "Small", ChannelUpdatedAt, "small-model"),
            Channel(2, "Large", ChannelUpdatedAt, "large-model"));
        SeedModel(
            new ModelAssociation
            {
                Type = ModelAssociation.TypeChannelModel,
                ChannelModel = new ChannelModelAssociation(1, "small-model"),
                When = new ModelAssociationWhen(
                    true,
                    new FilterCondition
                    {
                        Type = FilterCondition.TypeGroup,
                        Logic = "and",
                        Conditions =
                        [
                            new FilterCondition
                            {
                                Type = FilterCondition.TypeCondition,
                                Field = ModelAssociationConditionField.PromptTokens,
                                Operator = "lt",
                                Value = JsonValue.Create(100),
                            },
                        ],
                    }),
            },
            new ModelAssociation
            {
                Type = ModelAssociation.TypeChannelModel,
                ChannelModel = new ChannelModelAssociation(2, "large-model"),
                When = new ModelAssociationWhen(
                    true,
                    new FilterCondition
                    {
                        Type = FilterCondition.TypeGroup,
                        Logic = "and",
                        Conditions =
                        [
                            new FilterCondition
                            {
                                Type = FilterCondition.TypeCondition,
                                Field = ModelAssociationConditionField.PromptTokens,
                                Operator = "gt",
                                Value = JsonValue.Create(99),
                            },
                        ],
                    }),
            });

        var small = Assert.Single(await Select(prompt: "hello"));
        Assert.Equal(1, small.ChannelId);

        var large = Assert.Single(
            await Select(prompt: string.Concat(Enumerable.Repeat("0123456789", 50))));
        Assert.Equal(2, large.ChannelId);

        // Both requests share the same structurally resolved candidates; only
        // the request-level condition filter runs per request.
        var entry = Assert.Single(_cache.ReadAssociationCache());
        Assert.Equal(2, entry.CandidateCount);
        Assert.Equal(2, entry.Associations.Count);
    }

    private ModelCandidateSelector Selector() => new(
        _models,
        new StaticModelSettingsProvider(ModelSettings.Default),
        _channelStore,
        new FakeEnabledChannelProvider(),
        _clock,
        bypassModelPrivacy: true,
        enabledChannelSnapshots: _snapshots,
        associationCache: _cache);

    private async Task<IReadOnlyList<ChannelCandidate>> Select(string? prompt = null)
    {
        var request = new LlmRequest { Model = "alias" };
        if (prompt is not null)
        {
            request.Messages = [new Message { Role = "user", Content = MessageContent.FromText(prompt) }];
        }

        var selection = await Selector().SelectAsync(request, new RequestIdentity(0, null));
        return selection.Candidates;
    }

    private void SeedModel(params ModelAssociation[] associations)
    {
        ModelAssociation[] effective = associations.Length > 0
            ? associations
            :
            [
                new ModelAssociation
                {
                    Type = ModelAssociation.TypeChannelModel,
                    ChannelModel = new ChannelModelAssociation(1, "upstream"),
                },
            ];

        _models.Seed(row =>
        {
            row.ModelId = "alias";
            row.Status = "enabled";
            row.UpdatedAt = ModelUpdatedAt;
            SetAssociations(row, effective);
        });
    }

    private void SetModelAssociations(params ModelAssociation[] associations)
        => SetAssociations(_models.Rows[0], associations);

    private static void SetAssociations(ModelRecord row, IReadOnlyList<ModelAssociation> associations)
    {
        row.Settings = ModelSettingsCodec.SerializeEntitySettings(new ModelEntitySettings
        {
            Associations = [.. associations],
        });
    }

    private void SeedSnapshot(params EnabledChannelSnapshot[] channels)
        => SeedSnapshotWithVersion(channels, version: 1);

    private void SeedSnapshotWithVersion(EnabledChannelSnapshot[] channels, long version)
    {
        _snapshots.Snapshot = new EnabledChannelsSnapshot
        {
            Channels = [.. channels],
            Version = version,
            LastUpdatedAt = ChannelUpdatedAt,
        };
    }

    private static EnabledChannelSnapshot Channel(
        long id,
        string name,
        DateTime updatedAt,
        string model) => new()
    {
        Id = id,
        Name = name,
        Type = "openai",
        BaseUrl = "http://channel-" + id + ".test",
        Credentials = """{"apiKey":"sk-test"}""",
        SupportedModels = "[\"" + model + "\"]",
        DefaultTestModel = model,
        UpdatedAt = updatedAt,
    };
}
