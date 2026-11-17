using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the GC / storage-policy GraphQL surface the frontend sends
/// (frontend/src/features/system storage-policy + GC cleanup dialogs): the
/// merged default policy, the Go-zero decoding of an update input, the
/// read:settings / write:settings gates of internal/server/gql/
/// system.resolvers.go, the preview item shape (Time scalar) and the
/// fire-and-forget trigger mutation.
/// </summary>
public sealed class AdminGcEndpointTests : IAsyncLifetime
{
    private const string StoragePolicyQuery =
        """
        query StoragePolicy {
          storagePolicy {
            storeChunks
            livePreview
            storeRequestBody
            storeResponseBody
            cleanupOptions {
              resourceType
              enabled
              cleanupDays
            }
          }
        }
        """;

    private const string UpdateStoragePolicyMutation =
        """
        mutation Update($input: UpdateStoragePolicyInput!) {
          updateStoragePolicy(input: $input)
        }
        """;

    private const string PreviewQuery =
        """
        query Preview($input: TriggerGcCleanupInput!) {
          previewGcCleanup(input: $input) {
            resourceType
            estimatedCount
            cutoffTime
            retentionDays
          }
        }
        """;

    private const string TriggerMutation =
        """
        mutation Trigger($input: TriggerGcCleanupInput!) {
          triggerGcCleanup(input: $input)
        }
        """;

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeGcCleanupService _gc = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddScoped<ISystemSettingsAdminService, SystemSettingsAdminService>();
        builder.Services.AddSingleton<IGcCleanupService>(_gc);

        // Scope checks load the user through IUserAdminStore, not through the
        // gate's JWT user row.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task StoragePolicyQueryReturnsTheMergedDefaultPolicy()
    {
        var data = await QueryDataAsync(StoragePolicyQuery);
        var policy = data.GetProperty("storagePolicy");

        Assert.False(policy.GetProperty("storeChunks").GetBoolean());
        Assert.False(policy.GetProperty("livePreview").GetBoolean());
        Assert.True(policy.GetProperty("storeRequestBody").GetBoolean());
        Assert.True(policy.GetProperty("storeResponseBody").GetBoolean());

        var options = policy.GetProperty("cleanupOptions").EnumerateArray().ToArray();
        Assert.Equal(5, options.Length);
        Assert.Equal(
            [
                ("requests", false, 3),
                ("usage_logs", false, 30),
                ("request_bodies", false, 7),
                ("response_bodies", false, 7),
                ("response_chunks", false, 3),
            ],
            options
                .Select(static option => (
                    option.GetProperty("resourceType").GetString()!,
                    option.GetProperty("enabled").GetBoolean(),
                    option.GetProperty("cleanupDays").GetInt32()))
                .ToArray());
    }

    [Fact]
    public async Task UpdateStoragePolicyDecodesAbsentFieldsAsGoZeroValues()
    {
        var data = await QueryDataAsync(
            UpdateStoragePolicyMutation,
            new
            {
                input = new
                {
                    storeChunks = true,
                    cleanupOptions = new[]
                    {
                        new { resourceType = "requests", enabled = true, cleanupDays = 4 },
                    },
                },
            });

        Assert.True(data.GetProperty("updateStoragePolicy").GetBoolean());

        var stored = await new SystemSettingsAdminService(_gate.Systems)
            .GetStoragePolicyAsync();

        Assert.True(stored.StoreChunks);
        Assert.False(stored.LivePreview);

        // gqlgen decodes straight into biz.StoragePolicy: absent booleans are
        // false, so the two body flags are cleared by this update.
        Assert.False(stored.StoreRequestBody);
        Assert.False(stored.StoreResponseBody);

        var requests = stored.CleanupOptions!
            .Single(static option => option.ResourceType == CleanupResourceTypes.Requests);
        Assert.True(requests.Enabled);
        Assert.Equal(4, requests.CleanupDays);
    }

    [Fact]
    public async Task UpdateStoragePolicyKeepsTheReferenceWrapper()
    {
        _gate.Systems.SetFailure = new InvalidOperationException("write rejected");

        var errors = await QueryErrorsAsync(
            UpdateStoragePolicyMutation,
            new { input = new { storeChunks = true } });

        Assert.Equal(
            "failed to update storage policy: failed to create system setting: write rejected",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task PreviewGcCleanupReturnsTheWorkerItems()
    {
        _gc.PreviewResult =
        [
            new GcCleanupPreviewItem
            {
                ResourceType = CleanupResourceTypes.Requests,
                EstimatedCount = 10,
                CutoffTime = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc),
                RetentionDays = 3,
            },
            new GcCleanupPreviewItem
            {
                ResourceType = CleanupResourceTypes.UsageLogs,
                EstimatedCount = 7,
                CutoffTime = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
                RetentionDays = 5,
            },
        ];

        var data = await QueryDataAsync(
            PreviewQuery,
            new
            {
                input = new
                {
                    requestsCleanupDays = 3,
                    usageLogsCleanupDays = 5,
                    requestBodiesCleanupDays = 7,
                },
            });

        var request = Assert.Single(_gc.PreviewInputs);
        Assert.Equal(3, request.RequestsCleanupDays);
        Assert.Equal(5, request.UsageLogsCleanupDays);
        Assert.Equal(7, request.RequestBodiesCleanupDays);
        Assert.Equal(0, request.ResponseBodiesCleanupDays);
        Assert.Equal(0, request.ResponseChunksCleanupDays);

        var items = data.GetProperty("previewGcCleanup").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("requests", items[0].GetProperty("resourceType").GetString());
        Assert.Equal(10, items[0].GetProperty("estimatedCount").GetInt32());
        Assert.Equal("2026-11-03T12:00:00Z", items[0].GetProperty("cutoffTime").GetString());
        Assert.Equal(3, items[0].GetProperty("retentionDays").GetInt32());
        Assert.Equal("usage_logs", items[1].GetProperty("resourceType").GetString());
        Assert.Equal("2026-11-01T12:00:00Z", items[1].GetProperty("cutoffTime").GetString());
    }

    [Fact]
    public async Task PreviewGcCleanupRequiresTheReadSettingsScope()
    {
        var errors = await QueryErrorsAsync(
            PreviewQuery,
            new
            {
                input = new { requestsCleanupDays = 3, usageLogsCleanupDays = 5 },
            },
            AddNonOwnerToken());

        Assert.Equal(
            "permission denied: requires read:settings scope",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_gc.PreviewInputs);
    }

    [Fact]
    public async Task TriggerGcCleanupStartsTheDetachedRunAndReturnsImmediately()
    {
        var data = await QueryDataAsync(
            TriggerMutation,
            new
            {
                input = new
                {
                    requestsCleanupDays = 4,
                    usageLogsCleanupDays = 30,
                    requestBodiesCleanupDays = 6,
                },
            });

        Assert.True(data.GetProperty("triggerGcCleanup").GetBoolean());

        var started = await _gc.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, started.RequestsCleanupDays);
        Assert.Equal(30, started.UsageLogsCleanupDays);
        Assert.Equal(6, started.RequestBodiesCleanupDays);
        Assert.Equal(0, started.ResponseBodiesCleanupDays);
        Assert.Equal(0, started.ResponseChunksCleanupDays);
        Assert.Single(_gc.CleanupRequests);
    }

    [Fact]
    public async Task TriggerGcCleanupRequiresTheWriteSettingsScope()
    {
        var errors = await QueryErrorsAsync(
            TriggerMutation,
            new
            {
                input = new { requestsCleanupDays = 4, usageLogsCleanupDays = 30 },
            },
            AddNonOwnerToken());

        Assert.Equal(
            "permission denied: requires write:settings scope",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_gc.CleanupRequests);
        Assert.False(_gc.CleanupStarted.Task.IsCompleted);
    }

    private string AddNonOwnerToken()
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = "member@example.com";
            user.Status = "activated";
            user.IsOwner = false;
        });

        _users.CreateAsync(
                new UserRecord
                {
                    Id = 77,
                    Email = "member@example.com",
                    Status = "activated",
                    IsOwner = false,
                },
                roleIds: null)
            .GetAwaiter()
            .GetResult();

        return _gate.IssueJwt(member);
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            token ?? _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
