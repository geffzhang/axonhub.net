using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the reference ModelService semantics (internal/server/biz/model.go)
/// against the port: duplicate detection before insert, the bulk-create input
/// and existence messages, settings validation, NotFound strings, bulk status
/// mutations and the queryModels branch selection.
/// </summary>
public sealed class ModelManagementServiceTests
{
    private const long UserId = 9;

    private readonly FakeModelStore _models = new();
    private readonly FakeChannelAdminStore _channels = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeManagementIdentity _identity = new(new ManagementUser(UserId, IsOwner: true));

    public ModelManagementServiceTests()
    {
        _users.CreateAsync(
                new UserRecord { Id = UserId, Status = "activated", IsOwner = true },
                roleIds: null)
            .GetAwaiter()
            .GetResult();
    }

    private ModelManagementService NewService(ModelSettings? modelSettings = null) => new(
        _models,
        _channels,
        new StaticModelSettingsProvider(modelSettings ?? ModelSettings.Default),
        new ModelPrivacy(_identity, _users),
        new AuthorizationScope(_identity, _users),
        _identity,
        new ModelFetcher(
            new ScriptedExecutor(),
            new FakeChannelModelCatalogStore(),
            TimeProvider.System));

    [Fact]
    public async Task CreateRejectsAnExistingModelIdBeforeInserting()
    {
        _models.Seed(model =>
        {
            model.Developer = "openai";
            model.ModelId = "gpt-4o";
        });

        var error = await Assert.ThrowsAsync<CodedException>(
            () => NewService().CreateAsync(NewCommand("gpt-4o")));

        Assert.Equal(CodedException.DuplicateName, error.Code);
        Assert.Equal("model name 'gpt-4o' already exists", error.Message);
        Assert.Equal("model", error.Extensions["resource"]);
        Assert.Equal("name", error.Extensions["field"]);
        Assert.Equal("gpt-4o", error.Extensions["value"]);
        Assert.Single(_models.Rows);
    }

    [Fact]
    public async Task CreateValidatesSettingsBeforeCheckingExistence()
    {
        _models.Seed(model => model.ModelId = "gpt-4o");

        var command = NewCommand("gpt-4o") with
        {
            Settings = new ModelEntitySettings { LoadBalancerStrategy = "bogus" },
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().CreateAsync(command));

        Assert.Equal("invalid load balancer strategy \"bogus\"", error.Message);
        Assert.Single(_models.Rows);
    }

    [Fact]
    public async Task CreatePersistsTheNormalizedDefaults()
    {
        var command = NewCommand("gpt-4o") with
        {
            Type = null,
            Settings = new ModelEntitySettings
            {
                LoadBalancerStrategy = "",
                TraceStickyMode = "system_default",
            },
        };

        var created = await NewService().CreateAsync(command);

        Assert.True(created.Id > 0);
        Assert.Equal("openai", created.Developer);
        Assert.Equal("gpt-4o", created.ModelId);
        // Command type null reaches the store as "", and ent applies chat.
        Assert.Equal("chat", created.Type);
        Assert.Equal("disabled", created.Status);

        var stored = Assert.Single(_models.Rows);
        Assert.Equal(created.Id, stored.Id);

        var settings = ModelSettingsCodec.DeserializeEntitySettings(created.Settings);
        Assert.NotNull(settings);
        Assert.Equal("default", settings.LoadBalancerStrategy);
        Assert.Equal("default", settings.TraceStickyMode);
    }

    [Fact]
    public async Task BulkCreateRejectsDuplicateKeysInTheInput()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().BulkCreateAsync(
                [NewCommand("gpt-4o"), NewCommand("gpt-4o")]));

        Assert.Equal(
            "duplicate model in input: developer 'openai' and modelId 'gpt-4o'",
            error.Message);
        Assert.Empty(_models.Rows);
    }

    [Fact]
    public async Task BulkCreateRejectsExistingModelsWithGoSliceFormatting()
    {
        _models.Seed(model =>
        {
            model.Developer = "openai";
            model.ModelId = "a";
        });
        _models.Seed(model =>
        {
            model.Developer = "anthropic";
            model.ModelId = "b";
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().BulkCreateAsync(
                [NewCommand("a", "openai"), NewCommand("b", "anthropic")]));

        Assert.Equal("models already exist: [openai:a anthropic:b]", error.Message);
        Assert.Equal(2, _models.Rows.Count);
    }

    [Fact]
    public async Task BulkCreatePersistsEveryRow()
    {
        var created = await NewService().BulkCreateAsync(
            [NewCommand("gpt-4o"), NewCommand("gpt-4o-mini")]);

        Assert.Equal(2, created.Count);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], created.Select(model => model.ModelId));
        Assert.All(created, model => Assert.True(model.Id > 0));
        Assert.Equal(2, _models.Rows.Count);
    }

    [Fact]
    public async Task UpdateReportsTheEntNotFoundString()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().UpdateAsync(new UpdateModelCommand { Id = 404 }));

        Assert.Equal("failed to update model: ent: model not found", error.Message);
    }

    [Fact]
    public async Task UpdateAppliesSetFieldsAndClearRemark()
    {
        var seeded = _models.Seed(model =>
        {
            model.Developer = "openai";
            model.ModelId = "gpt-4o";
            model.Name = "before";
            model.Remark = "note";
        });

        var updated = await NewService().UpdateAsync(new UpdateModelCommand
        {
            Id = seeded.Id,
            Name = "after",
            ClearRemark = true,
        });

        Assert.Equal("after", updated.Name);
        Assert.Null(updated.Remark);
        // Omitted setters keep the stored values.
        Assert.Equal("openai", updated.Developer);
        Assert.Equal("gpt-4o", updated.ModelId);
    }

    [Fact]
    public async Task UpdateValidatesSettingsBeforeWriting()
    {
        var seeded = _models.Seed(model =>
        {
            model.Developer = "openai";
            model.ModelId = "gpt-4o";
            model.Name = "before";
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().UpdateAsync(new UpdateModelCommand
            {
                Id = seeded.Id,
                Name = "after",
                Settings = new ModelEntitySettings { TraceStickyMode = "sideways" },
            }));

        Assert.Equal("invalid trace sticky mode \"sideways\"", error.Message);
        Assert.Equal("before", seeded.Name);
    }

    [Fact]
    public async Task UpdateStatusReportsTheEntNotFoundString()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().UpdateStatusAsync(404, "enabled"));

        Assert.Equal(
            "failed to update model status: ent: model not found",
            error.Message);
    }

    [Fact]
    public async Task DeleteSoftDeletesOnceAndReportsNotFoundTheSecondTime()
    {
        var seeded = _models.Seed(model => model.ModelId = "gpt-4o");
        var service = NewService();

        await service.DeleteAsync(seeded.Id);

        Assert.NotEqual(0, seeded.DeletedAt);
        Assert.Null(await _models.GetByIdAsync(seeded.Id));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(seeded.Id));
        Assert.Equal("failed to delete model: ent: model not found", error.Message);
    }

    [Fact]
    public async Task BulkStatusMutationsSetTheTargetStatus()
    {
        var first = _models.Seed(model => model.ModelId = "gpt-4o");
        var second = _models.Seed(model => model.ModelId = "gpt-4o-mini");
        var service = NewService();

        await service.BulkEnableAsync([first.Id, second.Id]);
        Assert.All(_models.Rows, model => Assert.Equal("enabled", model.Status));

        await service.BulkDisableAsync([first.Id]);
        Assert.Equal("disabled", first.Status);
        Assert.Equal("enabled", second.Status);

        await service.BulkArchiveAsync([second.Id]);
        Assert.Equal("archived", second.Status);

        await service.BulkDeleteAsync([first.Id]);
        Assert.NotEqual(0, first.DeletedAt);
    }

    [Fact]
    public async Task PrivacyDeniesMutationForANonOwnerWithoutScopes()
    {
        // The policy loads the stored user for the scope rule; the owner row is
        // not the caller here, so set up a second, scope-less account.
        await _users.CreateAsync(
            new UserRecord { Id = UserId + 1, Status = "activated", IsOwner = false },
            roleIds: null);
        _identity.User = new ManagementUser(UserId + 1, IsOwner: false);

        await Assert.ThrowsAsync<PrivacyDenyException>(
            () => NewService().CreateAsync(NewCommand("gpt-4o")));

        Assert.Empty(_models.Rows);
    }

    [Fact]
    public async Task QueryModelsUsesChannelModelsWhenIncludeAllChannelModelsIsSet()
    {
        _channels.Seed(Channel(supportedModels: """["gpt-4o","gpt-4o-mini"]"""));

        var models = await NewService(ModelSettings.Default with { QueryAllChannelModels = false })
            .QueryModelsAsync(new ListModelsCommand { IncludeAllChannelModels = true });

        Assert.Equal(["gpt-4o", "gpt-4o-mini"], models.Select(model => model.Id));
        Assert.All(models, model => Assert.Equal("enabled", model.Status));
    }

    [Fact]
    public async Task QueryModelsListsConfiguredModelsWhenAllChannelModelsAreOff()
    {
        _models.Seed(model =>
        {
            model.ModelId = "gpt-4o";
            model.Status = "enabled";
        });
        _models.Seed(model =>
        {
            model.ModelId = "gpt-4o-mini";
            model.Status = "disabled";
        });
        _channels.Seed(Channel(supportedModels: """["channel-model"]"""));

        var models = await NewService(ModelSettings.Default with { QueryAllChannelModels = false })
            .QueryModelsAsync(new ListModelsCommand());

        var model = Assert.Single(models);
        Assert.Equal("gpt-4o", model.Id);
        Assert.Equal("enabled", model.Status);
    }

    [Fact]
    public async Task QueryModelsHonoursTheStatusFilter()
    {
        _models.Seed(model =>
        {
            model.ModelId = "gpt-4o";
            model.Status = "enabled";
        });
        _models.Seed(model =>
        {
            model.ModelId = "gpt-4o-mini";
            model.Status = "disabled";
        });

        var models = await NewService(ModelSettings.Default with { QueryAllChannelModels = false })
            .QueryModelsAsync(new ListModelsCommand { StatusIn = ["disabled"] });

        Assert.Equal("gpt-4o-mini", Assert.Single(models).Id);
    }

    [Fact]
    public async Task CountModelAssociatedChannelsMatchesTheSettingsAssociations()
    {
        var settings = new ModelEntitySettings
        {
            Associations =
            [
                new ModelAssociation
                {
                    Type = ModelAssociation.TypeChannelModel,
                    ChannelModel = new ChannelModelAssociation(1, "gpt-4o"),
                },
            ],
        };
        var model = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Settings = ModelSettingsCodec.SerializeEntitySettings(settings);
        });
        _channels.Seed(Channel(supportedModels: """["gpt-4o"]"""));
        _channels.Seed(Channel(id: 2, supportedModels: """["other"]"""));

        var count = await NewService().CountModelAssociatedChannelsAsync(model);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task QueryUnassociatedChannelsReportsModelsNoAssociationCovers()
    {
        _channels.Seed(Channel(supportedModels: """["b","a"]"""));

        var channels = await NewService().QueryUnassociatedChannelsAsync();

        var channel = Assert.Single(channels);
        Assert.Equal(1, channel.Channel.Id);
        // Go map iteration is sorted by the port (C177).
        Assert.Equal(["a", "b"], channel.Models);
    }

    private static CreateModelCommand NewCommand(
        string modelId,
        string developer = "openai") => new()
        {
            Developer = developer,
            ModelId = modelId,
            Name = modelId,
            Icon = "openai",
            Group = "default",
            ModelCard = ModelCard.Default,
            Settings = new ModelEntitySettings(),
        };

    private static ChannelRecord Channel(
        string supportedModels,
        long id = 1) => new()
        {
            Id = id,
            Name = "channel-" + id,
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = supportedModels,
        };
}
