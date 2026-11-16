using AxonHub.Api.Auth;
using AxonHub.Api.Codex;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the runtime <c>/codex</c> handlers (codex_compatibility.go at
/// f272549f, §160/C244): the whoami/accounts shapes, the stable account id, the
/// catalog-filtered model list and the 403/500/503 mappings. Route-level gates
/// (Enabled, strict auth) live in their own middleware tests.
/// </summary>
public sealed class CodexEndpointTests
{
    [Fact]
    public async Task WhoamiReturnsTheStableSyntheticIdentity()
    {
        var context = Context();
        PublishKey(context, id: 7, name: "display name");

        await CodexEndpoint.WhoamiAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(
            """{"chatgpt_account_id":"axonhub-key-7","chatgpt_account_is_fedramp":false,"chatgpt_plan_type":"promax","chatgpt_user_id":"axonhub-key-7","email":"display name"}""",
            Body(context));
        Assert.DoesNotContain("sk-test", Body(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhoamiWithoutAKeyWritesUnauthorized()
    {
        var context = Context();

        await CodexEndpoint.WhoamiAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Unauthorized","message":"Invalid API key"}}""",
            Body(context));
    }

    [Fact]
    public async Task AccountsCheckUsesTheWhoamiIdentity()
    {
        var context = Context();
        PublishKey(context, id: 7, name: "display name");

        await CodexEndpoint.AccountsCheckAsync(context);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(
            """{"accounts":[{"id":"axonhub-key-7","plan_type":"promax","workspace_backend_origin":"NO_CONSTRAINT","account_routing_override":"NO_CONSTRAINT","name":"display name","structure":"personal"}],"account_ordering":["axonhub-key-7"],"default_account_id":"axonhub-key-7"}""",
            Body(context));
    }

    [Fact]
    public async Task ListModelsIntersectsTheVisibleCatalogAndForwardsTheVersion()
    {
        var catalogs = new FakeCatalogs
        {
            Catalog = new CodexCatalog(
                [new KeyValuePair<string, string>("models", "[]"), new KeyValuePair<string, string>("other", """{"z":1}""")],
                [
                    """{"slug":"gpt-4o","display_name":"GPT-4o"}""",
                    """{"slug":"gpt-hidden","display_name":"Hidden"}""",
                ],
                ["gpt-4o", "gpt-hidden"]),
        };
        var settings = new FakeSettings
        {
            Settings = new CodexCompatibilitySettings { Enabled = true, ChannelID = 9 },
        };
        var context = Context();
        context.Request.QueryString = QueryString.Create("client_version", "1.2.3");

        await CodexEndpoint.ListModelsAsync(
            context,
            ModelsWith("gpt-4o"),
            settings,
            catalogs);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(
            """{"models":[{"slug":"gpt-4o","display_name":"GPT-4o"}],"other":{"z":1}}""",
            Body(context));
        Assert.Equal(9, catalogs.ChannelId);
        Assert.Equal("1.2.3", catalogs.Version);
    }

    [Fact]
    public async Task ListModelsMapsAMissingCatalogSourceTo503()
    {
        var context = Context();

        await CodexEndpoint.ListModelsAsync(
            context,
            ModelsWith("gpt-4o"),
            new FakeSettings { Settings = new CodexCompatibilitySettings { Enabled = true } },
            new FakeCatalogs());

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Service Unavailable","message":"Codex catalog source is unavailable"}}""",
            Body(context));
    }

    [Fact]
    public async Task ListModelsMapsASettingsReadFailureTo500()
    {
        var settings = new FakeSettings
        {
            ReadError = new InvalidOperationException("database is down"),
        };
        var context = Context();

        await CodexEndpoint.ListModelsAsync(
            context,
            ModelsWith("gpt-4o"),
            settings,
            new FakeCatalogs());

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Internal Server Error","message":"failed to read Codex catalog settings"}}""",
            Body(context));
    }

    [Fact]
    public async Task ListModelsMapsACatalogFailureToItsStatus()
    {
        var catalogs = new FakeCatalogs
        {
            Error = new CodexCatalogException(502, "upstream unavailable"),
        };
        var context = Context();

        await CodexEndpoint.ListModelsAsync(
            context,
            ModelsWith("gpt-4o"),
            new FakeSettings
            {
                Settings = new CodexCompatibilitySettings { Enabled = true, ChannelID = 9 },
            },
            catalogs);

        Assert.Equal(502, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Bad Gateway","message":"upstream unavailable"}}""",
            Body(context));
    }

    [Fact]
    public async Task ListModelsMapsAPrivacyDenyTo403()
    {
        var identity = ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(scopes: "write_requests"));
        var context = Context();

        await CodexEndpoint.ListModelsAsync(
            context,
            ModelTestSupport.Service(DefaultChannels(), identity: identity),
            new FakeSettings(),
            new FakeCatalogs());

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Forbidden","message":"failed to list visible models"}}""",
            Body(context));
    }

    private static IReadOnlyList<ChannelRecord> DefaultChannels()
        => [ModelTestSupport.Channel(1, "openai", "gpt-4o", supportedModels: """["gpt-4o"]""")];

    private static ModelService ModelsWith(string modelId)
        => ModelTestSupport.Service([
            ModelTestSupport.Channel(1, "openai", modelId, supportedModels: "[\"" + modelId + "\"]"),
        ]);

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/codex/models";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void PublishKey(HttpContext context, long id, string name)
        => context.Items[RequestIdentity.HttpContextItemKey] = new RequestIdentity(1, id)
        {
            ApiKey = new ApiKeyRecord
            {
                Id = id,
                Key = "sk-test",
                Name = name,
                Type = "user",
                Status = "enabled",
                ProjectId = 1,
            },
        };

    private static string Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            encoding: System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: -1,
            leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class FakeSettings : ICodexCompatibilitySettingsService
    {
        public CodexCompatibilitySettings Settings { get; init; } = new();

        public Exception? ReadError { get; init; }

        public Task<CodexCompatibilitySettings> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Settings);

        public Task<CodexCompatibilitySettings> GetBypassAsync(
            CancellationToken cancellationToken = default)
            => ReadError is null
                ? Task.FromResult(Settings)
                : Task.FromException<CodexCompatibilitySettings>(ReadError);

        public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Settings.Enabled);

        public Task UpdateAsync(
            CodexCompatibilitySettings settings,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeCatalogs : ICodexCatalogService
    {
        public CodexCatalog Catalog { get; init; } = new([], [], []);

        public Exception? Error { get; init; }

        public long ChannelId { get; private set; }

        public string Version { get; private set; } = string.Empty;

        public Task<CodexCatalog> FetchAsync(
            long channelId,
            string version,
            CancellationToken cancellationToken = default)
        {
            ChannelId = channelId;
            Version = version;
            return Error is null
                ? Task.FromResult(Catalog)
                : Task.FromException<CodexCatalog>(Error);
        }
    }
}
