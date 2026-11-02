using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the createChannel mutation against the reference resolver and
/// biz.ChannelService.CreateChannel (axonhub.resolvers.go:193-201,
/// channel.go:525-671): ent's presence rules for every column, the Go JSON
/// shape of settings/credentials, the frozen validator order and messages, the
/// live write extensions the migrated frontend sends (C139/C144), and the coded
/// duplicate-name error.
/// </summary>
public sealed class AdminChannelCreateEndpointTests : IAsyncLifetime
{
    private static readonly string[] ScopesS1 = ["s1"];
    private static readonly string[] ApiKeysK1K2 = ["k1", "k2"];
    private static readonly string[] SupportedModelsA = ["a"];
    private static readonly string[] ManualModelsM1 = ["m1"];
    private static readonly string[] TagsT1T2 = ["t1", "t2"];
    private static readonly string[] AutoTrimPrefixesP = ["p"];
    private static readonly int[] RetryableStatusCodesOutOfOrder = [503, 500, 503];
    private static readonly string[] ChatCompletionFormat = ["openai/chat_completions"];
    private static readonly int[] RetryableStatusCodes399 = [399];
    private static readonly string[] BogusFormat = ["bogus/format"];

    private const string CreateMutation =
        """
        mutation Create($input: CreateChannelInput!) {
          createChannel(input: $input) {
            id name type status orderingWeight autoSyncSupportedModels
            autoSyncModelPattern defaultTestModel baseURL remark
          }
        }
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        builder.Services.AddSingleton<IChannelWriteStore>(_channels);
        builder.Services.AddSingleton(_limiters);
        builder.Services.AddSingleton<ChannelLifecycleService>();

        _app = builder.Build();
        _app.MapGraphQL("/admin/graphql");
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task CreateChannelStoresEntDefaultsForAMinimalInput()
    {
        var data = await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "Minimal",
                    credentials = new { apiKey = "sk-minimal" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            });

        var node = data.GetProperty("createChannel");
        Assert.Equal("gid://axonhub/Channel/1", node.GetProperty("id").GetString());
        Assert.Equal("Minimal", node.GetProperty("name").GetString());
        Assert.Equal("openai", node.GetProperty("type").GetString());
        Assert.Equal("disabled", node.GetProperty("status").GetString());
        Assert.Equal(0, node.GetProperty("orderingWeight").GetInt32());
        Assert.False(node.GetProperty("autoSyncSupportedModels").GetBoolean());
        Assert.Equal(string.Empty, node.GetProperty("autoSyncModelPattern").GetString());

        var record = Assert.Single(_channels.CreatedChannels);
        Assert.Equal(0, record.DeletedAt);
        Assert.Equal("disabled", record.Status);
        Assert.Equal("openai", record.Type);
        Assert.Equal("Minimal", record.Name);
        Assert.Null(record.BaseUrl);
        Assert.Null(record.Remark);
        Assert.Equal("""{"apiKey":"sk-minimal"}""", record.Credentials);
        Assert.Equal("[]", record.DisabledApiKeys);
        Assert.Equal("[]", record.SupportedModels);

        // SetManualModels(nil) and SetSettings(nil) mark the columns present
        // with JSON null, suppressing the ent schema defaults.
        Assert.Equal("null", record.ManualModels);
        Assert.Equal("null", record.Settings);

        // The module defaults apply when the pointers stay nil.
        Assert.Equal("[]", record.Tags);
        Assert.Equal("""{"stream":"unlimited"}""", record.Policies);
        Assert.Equal("[]", record.Endpoints);
        Assert.Equal(0, record.OrderingWeight);
        Assert.Equal(string.Empty, record.AutoSyncModelPattern);
        Assert.False(record.AutoSyncSupportedModels);
        Assert.Equal(record.CreatedAt, record.UpdatedAt);
        Assert.NotEqual(default, record.CreatedAt);
    }

    [Fact]
    public async Task CreateChannelSerializesSettingsAndCredentialsInGoFieldOrder()
    {
        var data = await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "gemini_vertex",
                    name = "Full",
                    baseURL = "https://vertex.example/v1",
                    credentials = new
                    {
                        apiKey = "sk-full",
                        oauth = new
                        {
                            accessToken = "at",
                            refreshToken = "rt",
                            clientID = "cid",
                            expiresAt = "2026-01-02T03:04:05Z",
                            tokenType = "Bearer",
                            scopes = ScopesS1,
                        },
                        apiKeys = ApiKeysK1K2,
                        vertexApiKeys = new[]
                        {
                            new { projectID = "proj-1", apiKey = "vk", region = "us-central1" },
                        },
                        managementApiKey = "mk",
                        gcp = new { region = "us", projectID = "p1", jsonData = "{}" },
                    },
                    supportedModels = SupportedModelsA,
                    manualModels = ManualModelsM1,
                    autoSyncSupportedModels = true,
                    autoSyncModelPattern = "gpt-*",
                    tags = TagsT1T2,
                    defaultTestModel = "a",
                    orderingWeight = 42,
                    remark = "full",
                    settings = new
                    {
                        extraModelPrefix = "p",
                        autoTrimedModelPrefixes = AutoTrimPrefixesP,
                        modelMappings = new[] { new { from = "a", to = "b" } },
                        hideOriginalModels = true,
                        hideMappedModels = true,
                        lowercaseModelId = true,
                        bodyOverrideOperations = new[]
                        {
                            new { op = "set", path = "temperature", value = "1" },
                        },
                        headerOverrideOperations = new[]
                        {
                            new { op = "delete", path = "x-remove" },
                        },
                        proxy = new
                        {
                            type = "url",
                            url = "http://proxy.local:8080",
                            username = "u",
                            password = "p",
                            disableConnectionReuse = true,
                        },
                        transformOptions = new
                        {
                            forceArrayInstructions = true,
                            forceArrayInputs = true,
                            replaceDeveloperRoleWithSystem = true,
                            reasoningEffortMapping = new[] { new { from = "low", to = "high" } },
                        },
                        passThroughUserAgent = true,
                        passThroughBody = false,
                        rateLimit = new
                        {
                            rpm = 1,
                            tpm = 2,
                            maxConcurrent = 3,
                            queueSize = 4,
                            queueTimeoutMs = 5,
                        },
                        retryableStatusCodes = RetryableStatusCodesOutOfOrder,
                        retryableErrorPatterns = new[]
                        {
                            new { pattern = " timeout ", regex = true },
                            new { pattern = "timeout", regex = true },
                            new { pattern = "  ", regex = false },
                        },
                        maxSingleChannelRetries = 7,
                        responseTimeout = new
                        {
                            mode = "CUSTOM",
                            streamFirstEventTimeoutSeconds = 10,
                            nonStreamResponseTimeoutSeconds = 20,
                        },
                        modelProtocols = new[]
                        {
                            new
                            {
                                model = "a",
                                apiFormats = ChatCompletionFormat,
                                enabled = false,
                            },
                        },
                        apiKeyStrategy = "round_robin",
                        apiKeyRoundRobinSwitchAfter = 3,
                        providerQuota = new { ollama = new { authCookie = "token" } },
                        quotaRoutingMode = "IGNORE_QUOTA",
                    },
                },
            });

        var node = data.GetProperty("createChannel");
        Assert.Equal("gemini_vertex", node.GetProperty("type").GetString());
        Assert.Equal("https://vertex.example/v1", node.GetProperty("baseURL").GetString());
        Assert.Equal("gpt-*", node.GetProperty("autoSyncModelPattern").GetString());
        Assert.True(node.GetProperty("autoSyncSupportedModels").GetBoolean());

        var record = Assert.Single(_channels.CreatedChannels);

        // orderingWeight is never passed to the builder: ent stores its default.
        Assert.Equal(0, record.OrderingWeight);
        Assert.Equal("full", record.Remark);
        Assert.Equal("""["a"]""", record.SupportedModels);
        Assert.Equal("""["m1"]""", record.ManualModels);
        Assert.Equal("""["t1","t2"]""", record.Tags);
        Assert.Equal("a", record.DefaultTestModel);
        Assert.True(record.AutoSyncSupportedModels);
        Assert.Equal("gpt-*", record.AutoSyncModelPattern);

        Assert.Equal(
            """{"apiKey":"sk-full","oauth":{"client_id":"cid","access_token":"at","refresh_token":"rt","expires_at":"2026-01-02T03:04:05Z","token_type":"Bearer","scopes":["s1"]},"apiKeys":["k1","k2"],"vertexApiKeys":[{"projectID":"proj-1","apiKey":"vk","region":"us-central1"}],"managementApiKey":"mk","gcp":{"region":"us","projectID":"p1","jsonData":"{}"}}""",
            record.Credentials);

        Assert.Equal(
            """{"extraModelPrefix":"p","autoTrimedModelPrefixes":["p"],"modelMappings":[{"from":"a","to":"b"}],"hideOriginalModels":true,"hideMappedModels":true,"lowercaseModelId":true,"overrideParameters":"","bodyOverrideOperations":[{"op":"set","path":"temperature","value":"1"}],"overrideHeaders":null,"headerOverrideOperations":[{"op":"delete","path":"x-remove"}],"proxy":{"type":"url","url":"http://proxy.local:8080","username":"u","password":"p","disableConnectionReuse":true},"transformOptions":{"forceArrayInstructions":true,"forceArrayInputs":true,"replaceDeveloperRoleWithSystem":true,"reasoningEffortMapping":[{"from":"low","to":"high"}]},"passThroughUserAgent":true,"passThroughBody":false,"rateLimit":{"rpm":1,"tpm":2,"maxConcurrent":3,"queueSize":4,"queueTimeoutMs":5},"retryableStatusCodes":[500,503],"retryableErrorPatterns":[{"pattern":"timeout","regex":true}],"maxSingleChannelRetries":7,"responseTimeout":{"mode":"custom","streamFirstEventTimeoutSeconds":10,"nonStreamResponseTimeoutSeconds":20},"modelProtocols":[{"model":"a","apiFormats":["openai/chat_completions"],"enabled":false}],"apiKeyStrategy":"round_robin","apiKeyRoundRobinSwitchAfter":3,"providerQuota":{"ollama":{"authCookie":"token"}},"quotaRoutingMode":"ignore_quota"}""",
            record.Settings);
    }

    [Fact]
    public async Task EmptyOptionalListsStayArraysWhileOmittedOnesStoreNull()
    {
        // An empty slice is not nil in Go: SetManualModels([]) stores [],
        // SetTags([]) stores [], and SetEndpoints([]) stores [].
        await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "EmptyLists",
                    credentials = new { },
                    supportedModels = Array.Empty<string>(),
                    manualModels = Array.Empty<string>(),
                    tags = Array.Empty<string>(),
                    endpoints = Array.Empty<object>(),
                    defaultTestModel = string.Empty,
                },
            });

        var record = Assert.Single(_channels.CreatedChannels);
        Assert.Equal("[]", record.ManualModels);
        Assert.Equal("[]", record.Tags);
        Assert.Equal("[]", record.Endpoints);
    }

    [Fact]
    public async Task XaiSubscriptionForcesTheOfficialBaseUrlAndDropsEndpoints()
    {
        var data = await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "xai_subscription",
                    name = "Xai",
                    baseURL = "https://caller.example/v1",
                    credentials = new { apiKey = "xai" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                    endpoints = new[] { new { apiFormat = "openai/responses" } },
                },
            });

        Assert.Equal(
            "https://cli-chat-proxy.grok.com/v1",
            data.GetProperty("createChannel").GetProperty("baseURL").GetString());

        var record = Assert.Single(_channels.CreatedChannels);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", record.BaseUrl);
        Assert.Equal("[]", record.Endpoints);
    }

    [Fact]
    public async Task CommandCodeCookiesAreCanonicalizedAndOtherTypesDropTheBlock()
    {
        await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "commandcode",
                    name = "CommandCode",
                    baseURL = "https://api.commandcode.ai",
                    credentials = new { apiKey = "cc" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                    settings = new
                    {
                        providerQuota = new
                        {
                            commandCode = new
                            {
                                authCookie =
                                    "Cookie: __secure-commandcode_prod_.session_token = abc ; other=1",
                            },
                        },
                    },
                },
            });

        using (var settings = JsonDocument.Parse(_channels.CreatedChannels[0].Settings!))
        {
            Assert.Equal(
                "__secure-commandcode_prod_.session_token=abc",
                settings.RootElement
                    .GetProperty("providerQuota")
                    .GetProperty("commandCode")
                    .GetProperty("authCookie")
                    .GetString());
        }

        _channels.CreatedChannels.Clear();

        // Only the two Command Code types keep the block; everywhere else it is
        // removed and an otherwise-empty providerQuota disappears entirely.
        await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "NotCommandCode",
                    credentials = new { apiKey = "x" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                    settings = new
                    {
                        providerQuota = new
                        {
                            commandCode = new
                            {
                                authCookie = "commandcode_prod_.session_token=abc",
                            },
                            ollama = new { authCookie = "ollama-token" },
                        },
                    },
                },
            });

        using (var settings = JsonDocument.Parse(_channels.CreatedChannels[0].Settings!))
        {
            var quota = settings.RootElement.GetProperty("providerQuota");
            Assert.False(quota.TryGetProperty("commandCode", out _));
            Assert.Equal(
                "ollama-token",
                quota.GetProperty("ollama").GetProperty("authCookie").GetString());
        }
    }

    [Fact]
    public async Task CommandCodeErrorsKeepTheReferenceWrapping()
    {
        var errors = await QueryErrorsAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "commandcode",
                    name = "BadCookie",
                    baseURL = "https://api.commandcode.ai",
                    credentials = new { apiKey = "cc" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                    settings = new
                    {
                        providerQuota = new { commandCode = new { authCookie = "   " } },
                    },
                },
            });
        Assert.Equal(
            "invalid Command Code quota auth cookie: cookie is empty",
            errors[0].GetProperty("message").GetString());

        var https = await QueryErrorsAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "commandcode",
                    name = "BadUrl",
                    baseURL = "http://api.commandcode.ai",
                    credentials = new { apiKey = "cc" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            });
        Assert.Equal(
            "Command Code base URL must use HTTPS",
            https[0].GetProperty("message").GetString());

        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task SettingsValidatorsKeepTheReferenceOrderAndPrefixes()
    {
        async Task<string> ErrorAsync(object settings)
        {
            var errors = await QueryErrorsAsync(
                CreateMutation,
                new
                {
                    input = new
                    {
                        type = "openai",
                        name = "Invalid",
                        credentials = new { apiKey = "k" },
                        supportedModels = Array.Empty<string>(),
                        defaultTestModel = string.Empty,
                        settings,
                    },
                });
            return errors[0].GetProperty("message").GetString()!;
        }

        Assert.Equal(
            "invalid body override operations: body operation at index 0 (set) has an empty path",
            await ErrorAsync(new
            {
                bodyOverrideOperations = new[] { new { op = "set", path = "  " } },
            }));

        Assert.Equal(
            "invalid header override operations: header operation at index 0 (set_if_absent) is not supported on headers; it only applies to the body",
            await ErrorAsync(new
            {
                headerOverrideOperations = new[] { new { op = "set_if_absent", path = "x" } },
            }));

        Assert.Equal(
            "invalid rate limit: rpm must be >= 0",
            await ErrorAsync(new { rateLimit = new { rpm = -1 } }));

        Assert.Equal(
            "invalid rate limit: queueSize requires maxConcurrent > 0",
            await ErrorAsync(new { rateLimit = new { queueSize = 1 } }));

        Assert.Equal(
            "invalid response timeout: streamFirstEventTimeoutSeconds must be <= 600",
            await ErrorAsync(new
            {
                responseTimeout = new
                {
                    mode = "CUSTOM",
                    streamFirstEventTimeoutSeconds = 601,
                },
            }));

        Assert.Equal(
            "invalid retryable status code 399: must be between 400 and 599",
            await ErrorAsync(new { retryableStatusCodes = RetryableStatusCodes399 }));

        var regex = await ErrorAsync(new
        {
            retryableErrorPatterns = new[] { new { pattern = "(", regex = true } },
        });
        Assert.StartsWith("invalid retryable error regex \"(\": ", regex);

        Assert.Equal(
            "invalid max single channel retries 65536: must be between 0 and 65535",
            await ErrorAsync(new { maxSingleChannelRetries = 65536 }));

        Assert.Equal(
            "invalid model protocols: modelProtocols[0] (m): api_format \"bogus/format\" is not configured on this channel",
            await ErrorAsync(new
            {
                modelProtocols = new[]
                {
                    new { model = "m", apiFormats = BogusFormat },
                },
            }));

        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task ResponseTimeoutIsDroppedUnlessItsModeIsCustom()
    {
        await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "InheritedTimeout",
                    credentials = new { apiKey = "k" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                    settings = new
                    {
                        responseTimeout = new
                        {
                            mode = "INHERIT",
                            streamFirstEventTimeoutSeconds = 10,
                        },
                    },
                },
            });

        using var settings = JsonDocument.Parse(_channels.CreatedChannels[0].Settings!);
        Assert.False(settings.RootElement.TryGetProperty("responseTimeout", out _));
    }

    [Fact]
    public async Task EndpointValidationRunsAfterTheSettingsBlock()
    {
        async Task<string> ErrorAsync(object endpoints, object? settings = null)
        {
            var errors = await QueryErrorsAsync(
                CreateMutation,
                new
                {
                    input = new
                    {
                        type = "openai",
                        name = "BadEndpoints",
                        credentials = new { apiKey = "k" },
                        supportedModels = Array.Empty<string>(),
                        defaultTestModel = string.Empty,
                        endpoints,
                        settings,
                    },
                });
            return errors[0].GetProperty("message").GetString()!;
        }

        Assert.Equal(
            "invalid endpoints: endpoint[1]: duplicate api_format \"openai/chat_completions\"",
            await ErrorAsync(new[]
            {
                new { apiFormat = "openai/chat_completions" },
                new { apiFormat = "openai/chat_completions" },
            }));

        Assert.Equal(
            "invalid endpoints: endpoint[0]: unsupported transport \"ftp\"",
            await ErrorAsync(new[]
            {
                new { apiFormat = "openai/chat_completions", transport = "ftp" },
            }));

        Assert.Equal(
            "invalid endpoints: endpoint[0]: websocket transport only supports api_format \"openai/responses\"",
            await ErrorAsync(new[]
            {
                new { apiFormat = "openai/chat_completions", transport = "websocket" },
            }));

        Assert.Equal(
            "invalid endpoints: endpoint[0]: path must not be a full URL, got \"https://x/v1\"",
            await ErrorAsync(new[]
            {
                new { apiFormat = "openai/chat_completions", path = "https://x/v1" },
            }));

        Assert.Equal(
            "invalid endpoints: endpoint[0]: path must start with '/', got \"v1\"",
            await ErrorAsync(new[]
            {
                new { apiFormat = "openai/chat_completions", path = "v1" },
            }));

        // The settings block runs first, so its error wins over an endpoint one.
        Assert.Equal(
            "invalid rate limit: rpm must be >= 0",
            await ErrorAsync(
                new[] { new { apiFormat = "bogus/format" } },
                new { rateLimit = new { rpm = -1 } }));

        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task VertexBindingsAreValidatedAfterTheFrozenSteps()
    {
        async Task<string> ErrorAsync(string type, object vertexApiKeys)
        {
            var errors = await QueryErrorsAsync(
                CreateMutation,
                new
                {
                    input = new
                    {
                        type,
                        name = "Vertex",
                        credentials = new { vertexApiKeys },
                        supportedModels = Array.Empty<string>(),
                        defaultTestModel = string.Empty,
                    },
                });
            return errors[0].GetProperty("message").GetString()!;
        }

        Assert.Equal(
            "vertex api keys are only supported by gemini_vertex channels",
            await ErrorAsync(
                "openai",
                new[] { new { projectID = "proj", apiKey = "k", region = "us" } }));

        Assert.Equal(
            "vertex api key, project ID, and region are all required",
            await ErrorAsync(
                "gemini_vertex",
                new[] { new { projectID = " ", apiKey = "k", region = "us" } }));

        Assert.Equal(
            "vertex project ID \"bad project\" is not a valid Google project ID",
            await ErrorAsync(
                "gemini_vertex",
                new[] { new { projectID = "bad project", apiKey = "k", region = "us" } }));

        Assert.Equal(
            "vertex api key is bound to both proj-a/us and proj-b/us",
            await ErrorAsync(
                "gemini_vertex",
                new[]
                {
                    new { projectID = "proj-a", apiKey = "same", region = "us" },
                    new { projectID = "proj-b", apiKey = "same", region = "us" },
                }));

        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task DuplicateLiveNameReturnsTheCodedError()
    {
        _channels.Seed(new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Type = "openai",
            Name = "Taken",
            Status = "disabled",
            Credentials = "{}",
            SupportedModels = "[]",
            AutoSyncModelPattern = string.Empty,
            DefaultTestModel = string.Empty,
            Policies = """{"stream":"unlimited"}""",
            Tags = "[]",
            Endpoints = "[]",
        });

        var errors = await QueryErrorsAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "Taken",
                    credentials = new { apiKey = "k" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            });

        Assert.Equal("channel name 'Taken' already exists", errors[0].GetProperty("message").GetString());
        var extensions = errors[0].GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("channel", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("Taken", extensions.GetProperty("value").GetString());
        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task SoftDeletedNamesAreReusable()
    {
        _channels.Seed(new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DeletedAt = 1234,
            Type = "openai",
            Name = "Recycled",
            Status = "disabled",
            Credentials = "{}",
            SupportedModels = "[]",
            AutoSyncModelPattern = string.Empty,
            DefaultTestModel = string.Empty,
            Policies = """{"stream":"unlimited"}""",
            Tags = "[]",
            Endpoints = "[]",
        });

        var data = await QueryDataAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "Recycled",
                    credentials = new { apiKey = "k" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            });

        Assert.Equal("Recycled", data.GetProperty("createChannel").GetProperty("name").GetString());
        Assert.Single(_channels.CreatedChannels);
    }

    [Fact]
    public async Task StoreFailuresKeepTheReferencePrefix()
    {
        _channels.CreateFailure = new StoreFailureException(
            "failed to create channel: boom",
            new InvalidOperationException("boom"));

        var errors = await QueryErrorsAsync(
            CreateMutation,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "Broken",
                    credentials = new { apiKey = "k" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            });

        Assert.Equal("failed to create channel: boom", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesCreateChannelAndTheLiveInputExtensions()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              input: __type(name: "CreateChannelInput") {
                inputFields { name }
              }
              settings: __type(name: "ChannelSettingsInput") {
                inputFields { name }
              }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("createChannel", mutations);

        var inputFields = data.GetProperty("input").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("credentials", inputFields);
        Assert.Contains("settings", inputFields);
        Assert.Contains("endpoints", inputFields);
        Assert.Contains("orderingWeight", inputFields);

        var settingsFields = data.GetProperty("settings").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("responseTimeout", settingsFields);
        Assert.Contains("apiKeyStrategy", settingsFields);
        Assert.Contains("apiKeyRoundRobinSwitchAfter", settingsFields);
        Assert.Contains("providerQuota", settingsFields);
    }

    private async Task<JsonElement> QueryDataAsync(string query, object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(string query, object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(string query, object? variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
