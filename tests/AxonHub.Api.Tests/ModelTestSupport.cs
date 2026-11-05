using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Shared fixtures for the models surface (C178): live channel and model rows,
/// the API-key/owner identities the reference reads from the request context,
/// and the five-dependency ModelService wiring.
/// </summary>
internal static class ModelTestSupport
{
    /// <summary>The reference fixtures' <c>time.Now()</c> row timestamp.</summary>
    public static readonly DateTime CreatedAt =
        new(2024, 4, 5, 19, 34, 58, DateTimeKind.Utc);

    public static ChannelRecord Channel(
        long id,
        string type = "openai",
        string model = "gpt-4o",
        string? supportedModels = null,
        DateTime? createdAt = null,
        string? tags = null,
        string? endpoints = null,
        string? settings = null)
        => new()
        {
            Id = id,
            CreatedAt = createdAt ?? CreatedAt,
            UpdatedAt = createdAt ?? CreatedAt,
            Type = type,
            Name = "channel-" + id,
            Status = "enabled",
            SupportedModels = supportedModels ?? "[\"" + model + "\"]",
            DefaultTestModel = model,
            Tags = tags,
            Endpoints = endpoints,
            Settings = settings,
        };

    public static ModelRecord ConfiguredModel(
        string modelId,
        string type = "chat",
        string developer = "openai",
        string? name = null,
        string? icon = null,
        string? remark = null,
        string modelCard = "{}",
        string? settings = null,
        DateTime? createdAt = null)
        => new()
        {
            ModelId = modelId,
            Developer = developer,
            Name = name ?? modelId,
            Icon = icon ?? string.Empty,
            Group = "test",
            Type = type,
            ModelCard = modelCard,
            Settings = settings ?? "{}",
            Status = "enabled",
            Remark = remark,
            CreatedAt = createdAt ?? CreatedAt,
            UpdatedAt = createdAt ?? CreatedAt,
        };

    /// <summary>objects.ModelSettings JSON with one channel_model association.</summary>
    public static string ChannelModelSettings(long channelId, string modelId)
        => "{\"associations\":[{\"type\":\"channel_model\",\"channelModel\":{\"channelId\":"
            + channelId + ",\"modelId\":\"" + modelId + "\"}}]}";

    /// <summary>
    /// objects.ModelSettings JSON with one model association. The outer
    /// <c>modelId</c> is the association object, the inner one is the model id
    /// (objects/model.go:77,108).
    /// </summary>
    public static string ModelIdSettings(string modelId)
        => "{\"associations\":[{\"type\":\"model\",\"modelId\":{\"modelId\":\""
            + modelId + "\"}}]}";

    /// <summary>
    /// An API-key principal, which is what contexts.GetAPIKey returns during
    /// API requests. Empty <paramref name="scopes"/> selects the ent create
    /// default (read_channels + write_requests).
    /// </summary>
    public static ApiKeyRecord ApiKey(string? profiles = null, params string[] scopes)
        => new()
        {
            Id = 1,
            Key = "sk-test",
            Name = "test-api-key",
            Type = "user",
            Status = "enabled",
            ProjectId = 1,
            Scopes = System.Text.Json.JsonSerializer.Serialize(
                scopes.Length == 0 ? ["read_channels", "write_requests"] : scopes),
            Profiles = profiles,
        };

    /// <summary>The API-key identity with its project edge (apiKey.Edges.Project).</summary>
    public static IManagementIdentity ApiKeyIdentity(
        ApiKeyRecord? apiKey = null,
        string? projectProfiles = null)
    {
        var identity = new FakeManagementIdentity(null)
        {
            ApiKey = apiKey ?? ApiKey(),
        };

        if (projectProfiles is not null)
        {
            identity.Project = new ProjectRecord
            {
                Id = 1,
                Name = "project-1",
                Profiles = projectProfiles,
            };
        }

        return identity;
    }

    /// <summary>An owner user, whose authz decision bypasses every policy.</summary>
    public static IManagementIdentity OwnerIdentity()
        => new FakeManagementIdentity(new ManagementUser(1, IsOwner: true));

    /// <summary>
    /// Builds the reference's ModelService over in-memory stores. The zero-
    /// argument identity is the owner, mirroring the reference's test bypass.
    /// </summary>
    public static ModelService Service(
        IReadOnlyList<ChannelRecord> channels,
        IReadOnlyList<ModelRecord>? models = null,
        ModelSettings? settings = null,
        IManagementIdentity? identity = null)
        => ServiceWithStores(channels, models, settings, identity).Service;

    /// <summary>Builds a service plus its underlying stores for assertions.</summary>
    public static (ModelService Service, FakeChannelStore Channels, FakeModelStore Models)
        ServiceWithStores(
            IReadOnlyList<ChannelRecord> channels,
            IReadOnlyList<ModelRecord>? models = null,
            ModelSettings? settings = null,
            IManagementIdentity? identity = null)
    {
        var channelStore = new FakeChannelStore();
        foreach (var channel in channels)
        {
            channelStore.Add(channel);
        }

        var modelStore = new FakeModelStore();
        foreach (var model in models ?? [])
        {
            modelStore.Add(model);
        }

        identity ??= OwnerIdentity();
        var service = new ModelService(
            channelStore,
            modelStore,
            new StaticModelSettingsProvider(settings ?? ModelSettings.Default),
            identity,
            new AuthorizationScope(identity, new FakeUserAdminStore()));

        return (service, channelStore, modelStore);
    }
}
