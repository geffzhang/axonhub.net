using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the <c>apiKeyQuotaUsages</c> root over /admin/graphql
/// (axonhub.graphql:1116, axonhub.resolvers.go:952-981, biz/quota.go:129-191):
/// the per-profile shape, the all-time / past-duration windows, the empty and
/// denied branches and the two error prefixes. The root exists in the frozen
/// baseline b8bb3421, so this slice follows the baseline semantics.
/// </summary>
public sealed class AdminApiKeyQuotaUsageEndpointTests : IAsyncLifetime
{
    private const string QuotaQuery = """
        query ($id: ID!) {
          apiKeyQuotaUsages(apiKeyId: $id) {
            profileName
            quota {
              requests
              totalTokens
              cost
              period {
                type
                pastDuration { value unit }
                calendarDuration { unit }
              }
            }
            window { start end }
            usage { requestCount totalTokens totalCost }
          }
        }
        """;

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly InMemoryUsageLogStore _usageLogs = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // The JWT user (id 9) is the owner by default; the privacy test flips
        // both copies so the API key query policy denies.
        await _adminUsers.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_adminUsers);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddSingleton<IUsageLogStore>(_usageLogs);
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<ApiKeyManagementService>();
        builder.Services.AddScoped<ApiKeyQuotaService>();
        builder.Services.AddScoped<IApiKeyQuotaChecker>(
            static services => services.GetRequiredService<ApiKeyQuotaService>());
        builder.Services.AddScoped<IApiKeyProfileQuotaReader>(
            static services => services.GetRequiredService<ApiKeyQuotaService>());
        _adminUsers.RolesSource = _roles;

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
    public async Task QuotaUsagesMatchTheFrontendShape()
    {
        var key = SeedKey(
            """
            {"activeProfile":"paid","profiles":[
              {"name":"paid","quota":{"requests":10,"totalTokens":100,"cost":"9","period":{"type":"all_time"}}},
              {"name":"free"}
            ]}
            """);
        var now = DateTimeOffset.UtcNow;
        AddUsage(key.Id, now.AddSeconds(-30), totalTokens: 10, totalCost: 0.5);
        AddUsage(key.Id, now.AddSeconds(-20), totalTokens: 20, totalCost: 0.25);

        var rows = (await QueryDataAsync(QuotaQuery, new { id = Gid(key.Id) }))
            .GetProperty("apiKeyQuotaUsages");

        var usage = Assert.Single(rows.EnumerateArray());
        Assert.Equal("paid", usage.GetProperty("profileName").GetString());

        var quota = usage.GetProperty("quota");
        Assert.Equal(10, quota.GetProperty("requests").GetInt32());
        Assert.Equal(100, quota.GetProperty("totalTokens").GetInt32());
        Assert.Equal("9", quota.GetProperty("cost").GetRawText());
        var period = quota.GetProperty("period");
        Assert.Equal("all_time", period.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, period.GetProperty("pastDuration").ValueKind);
        Assert.Equal(JsonValueKind.Null, period.GetProperty("calendarDuration").ValueKind);

        var window = usage.GetProperty("window");
        Assert.Equal(JsonValueKind.Null, window.GetProperty("start").ValueKind);
        var end = DateTimeOffset.Parse(
            window.GetProperty("end").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(
            (end - DateTimeOffset.UtcNow).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(5));

        var aggregate = usage.GetProperty("usage");
        Assert.Equal(2, aggregate.GetProperty("requestCount").GetInt32());
        Assert.Equal(30, aggregate.GetProperty("totalTokens").GetInt32());
        Assert.Equal("0.75", aggregate.GetProperty("totalCost").GetRawText());
    }

    [Fact]
    public async Task PastDurationProfilesUseTheirOwnWindow()
    {
        var key = SeedKey(
            """
            {"profiles":[{"name":"m","quota":{"requests":1,"period":{"type":"past_duration","pastDuration":{"value":1,"unit":"minute"}}}}]}
            """);
        var now = DateTimeOffset.UtcNow;
        AddUsage(key.Id, now.AddSeconds(-90), totalTokens: 10, totalCost: 0.5);
        AddUsage(key.Id, now.AddSeconds(-3), totalTokens: 5, totalCost: 0.125);

        var rows = (await QueryDataAsync(QuotaQuery, new { id = Gid(key.Id) }))
            .GetProperty("apiKeyQuotaUsages");

        var usage = Assert.Single(rows.EnumerateArray());
        Assert.Equal(1, usage.GetProperty("usage").GetProperty("requestCount").GetInt32());
        Assert.Equal(5, usage.GetProperty("usage").GetProperty("totalTokens").GetInt32());
        Assert.Equal("0.125", usage.GetProperty("usage").GetProperty("totalCost").GetRawText());
        Assert.NotEqual(
            JsonValueKind.Null,
            usage.GetProperty("window").GetProperty("start").ValueKind);
    }

    [Fact]
    public async Task KeysWithoutQuotaCarryingProfilesReturnAnEmptyList()
    {
        // The gate's default seed carries {"activeProfile":"","profiles":null}.
        var key = SeedKey(profiles: null);

        var data = await QueryDataAsync(QuotaQuery, new { id = Gid(key.Id) });

        Assert.Equal(
            "[]",
            data.GetProperty("apiKeyQuotaUsages").GetRawText());
    }

    [Fact]
    public async Task UnknownKeysUseTheGetApiKeyPrefix()
    {
        var errors = await QueryErrorsAsync(
            QuotaQuery,
            new { id = "gid://axonhub/APIKey/999" });

        Assert.Equal(
            "failed to get api key: ent: api_key not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task MalformedGuidsAreRejectedBeforeTheLookup()
    {
        var errors = await QueryErrorsAsync(QuotaQuery, new { id = "3" });

        Assert.Equal(
            "guid must start with gid://axonhub/",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task KeysThePrincipalCannotReadAreAPrivacyDenial()
    {
        var key = SeedKey(
            """{"profiles":[{"name":"p","quota":{"period":{"type":"all_time"}}}]}""");
        _gate.Owner.IsOwner = false;
        var permissionUser = await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId)
            ?? throw new InvalidOperationException("gate user missing");
        permissionUser.IsOwner = false;
        permissionUser.Scopes = "[]";

        var errors = await QueryErrorsAsync(QuotaQuery, new { id = Gid(key.Id) });

        // The presenter maps a privacy deny to the reference's FORBIDDEN error
        // (graphql.go:181-190), dropping the resolver's wrapper prefix.
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task WindowErrorsUseTheQuotaUsagePrefix()
    {
        var key = SeedKey(
            """{"profiles":[{"name":"p","quota":{"period":{"type":"weekly"}}}]}""");

        var errors = await QueryErrorsAsync(QuotaQuery, new { id = Gid(key.Id) });

        Assert.Equal(
            "failed to get api key quota usage: unknown period.type: weekly",
            errors[0].GetProperty("message").GetString());
    }

    private ApiKeyRecord SeedKey(string? profiles)
        => _gate.ApiKeys.Add(key =>
        {
            key.Key = "ah-quota";
            key.Name = "quota key";
            key.Type = "service_account";
            key.Status = "enabled";
            key.ProjectId = 1;
            key.UserId = AdminGraphqlGateServices.UserId;
            if (profiles is not null)
            {
                key.Profiles = profiles;
            }
        });

    private void AddUsage(
        long apiKeyId,
        DateTimeOffset createdAt,
        long totalTokens,
        double totalCost)
        => _usageLogs.Rows.Add(new UsageLogRecord
        {
            ApiKeyId = apiKeyId,
            CreatedAt = createdAt.UtcDateTime,
            UpdatedAt = createdAt.UtcDateTime,
            ModelId = "test-model",
            TotalTokens = totalTokens,
            TotalCost = totalCost,
        });

    private static string Gid(long id)
        => new GidValue("APIKey", id).ToString();

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
        return [.. errors.EnumerateArray().Select(static error => error.Clone())];
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
