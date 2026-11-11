using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Catalog;
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
/// Locks the providers-catalog GraphQL surface over /admin/graphql: the
/// catalogSettings / providersCatalog reads and the updateCatalogSettings /
/// refreshProvidersCatalog mutations of system.resolvers.go, including their
/// scope gates, the Go <c>url.Parse</c> validation texts and the
/// invalidate/reschedule side effects.
/// </summary>
public sealed class AdminCatalogEndpointTests : IAsyncLifetime
{
    private const string CatalogSettingsQuery =
        "{ catalogSettings { upstreamURL refreshSeconds } }";

    private const string ProvidersCatalogQuery =
        "{ providersCatalog { data fetchedAt source filtered } }";

    private const string UpdateCatalogSettingsMutation =
        """
        mutation Update($input: UpdateCatalogSettingsInput!) {
          updateCatalogSettings(input: $input)
        }
        """;

    private const string RefreshProvidersCatalogMutation =
        "mutation { refreshProvidersCatalog { data fetchedAt source filtered } }";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeSystemStore _store = new();
    private readonly RecordingCatalogService _catalog = new();
    private readonly RecordingCatalogRescheduleSignal _reschedule = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _catalog.SnapshotResult = new CatalogSnapshot(
            CatalogData(
                """{"providers":{"openai":{"id":"openai","models":[{"id":"gpt-4o"}]}}}"""),
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            CatalogService.SourceFallback,
            true);
        _catalog.RefreshResult = new CatalogSnapshot(
            CatalogData(
                """{"providers":{"openai":{"id":"openai","models":[{"id":"gpt-hot"}]}}}"""),
            new DateTime(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc),
            CatalogService.SourceUpstream,
            true);

        var builder = CreateBuilder();
        _app = builder.Build();
        // CreateBuilder replaces the gate's ISystemStore with _store; the JWT
        // secret must live in the store the auth middleware actually reads.
        _gate.SeedJwtSecret(_app.Services);
        _app.MapAdminGraphql();
        await _app.StartAsync();

        _client = _app.GetTestClient();
        _gate.Authorize(_client);

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
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task CatalogSettingsQueryReturnsTheDefaults()
    {
        var data = await QueryDataAsync(CatalogSettingsQuery);
        var settings = data.GetProperty("catalogSettings");

        Assert.Equal("", settings.GetProperty("upstreamURL").GetString());
        Assert.Equal(3600, settings.GetProperty("refreshSeconds").GetInt32());
    }

    [Fact]
    public async Task CatalogSettingsQueryReturnsTheStoredNormalizedValues()
    {
        _store.Add(
            CatalogSettingsRules.Key,
            """{"upstream_url":"  https://example.com/catalog.json  ","refresh_seconds":10}""");

        var data = await QueryDataAsync(CatalogSettingsQuery);
        var settings = data.GetProperty("catalogSettings");

        Assert.Equal(
            "https://example.com/catalog.json",
            settings.GetProperty("upstreamURL").GetString());
        Assert.Equal(60, settings.GetProperty("refreshSeconds").GetInt32());
    }

    [Fact]
    public async Task CatalogSettingsQueryWrapsStorageFailures()
    {
        _store.GetFailuresByKey[CatalogSettingsRules.Key] =
            new InvalidOperationException("read rejected");

        var errors = await QueryErrorsAsync(CatalogSettingsQuery);

        Assert.Equal(
            "failed to get catalog settings: failed to get catalog settings: "
            + "failed to get system value: read rejected",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CatalogSettingsQueryRequiresTheReadSettingsScope()
    {
        var errors = await QueryErrorsAsync(CatalogSettingsQuery, token: AddNonOwnerToken());

        Assert.Equal(
            "permission denied: requires read_settings scope",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateCatalogSettingsPatchesOnlyTheSuppliedFields()
    {
        _store.Add(
            CatalogSettingsRules.Key,
            """{"upstream_url":"https://a.example/catalog.json","refresh_seconds":120}""");

        var data = await QueryDataAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { refreshSeconds = 300 } });

        Assert.True(data.GetProperty("updateCatalogSettings").GetBoolean());
        Assert.Equal(
            CatalogSettingsCodec.Serialize(
                new CatalogSettings("https://a.example/catalog.json", 300)),
            StoredCatalogSettings());
        Assert.Equal(1, _catalog.InvalidateCalls);
        Assert.Equal(1, _reschedule.Calls);

        data = await QueryDataAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { upstreamURL = "  https://b.example/catalog.json  " } });

        Assert.True(data.GetProperty("updateCatalogSettings").GetBoolean());
        Assert.Equal(
            CatalogSettingsCodec.Serialize(
                new CatalogSettings("https://b.example/catalog.json", 300)),
            StoredCatalogSettings());
        Assert.Equal(2, _catalog.InvalidateCalls);
        Assert.Equal(2, _reschedule.Calls);
    }

    [Fact]
    public async Task UpdateCatalogSettingsNormalizesZeroAndSmallIntervals()
    {
        var data = await QueryDataAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { refreshSeconds = 5 } });

        Assert.True(data.GetProperty("updateCatalogSettings").GetBoolean());
        Assert.Equal(
            CatalogSettingsCodec.Serialize(new CatalogSettings("", 60)),
            StoredCatalogSettings());

        data = await QueryDataAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { refreshSeconds = 0 } });

        Assert.True(data.GetProperty("updateCatalogSettings").GetBoolean());
        Assert.Equal(
            CatalogSettingsCodec.Serialize(new CatalogSettings("", 3600)),
            StoredCatalogSettings());
    }

    [Fact]
    public async Task UpdateCatalogSettingsRejectsNonHttpUrlsWithoutWriting()
    {
        var errors = await QueryErrorsAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { upstreamURL = "ftp://x" } });

        Assert.Equal(
            "failed to update catalog settings: catalog upstream url must be http or https",
            errors[0].GetProperty("message").GetString());
        AssertNoCatalogSettingsWritten();
        Assert.Equal(0, _catalog.InvalidateCalls);
        Assert.Equal(0, _reschedule.Calls);
    }

    [Fact]
    public async Task UpdateCatalogSettingsSurfacesTheGoUrlParseText()
    {
        var errors = await QueryErrorsAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { upstreamURL = "https://example.com:80:90/x" } });

        Assert.Equal(
            "failed to update catalog settings: invalid catalog upstream url: "
            + "parse \"https://example.com:80:90/x\": invalid port \":80:90\" after host",
            errors[0].GetProperty("message").GetString());
        AssertNoCatalogSettingsWritten();
        Assert.Equal(0, _catalog.InvalidateCalls);
        Assert.Equal(0, _reschedule.Calls);
    }

    [Fact]
    public async Task UpdateCatalogSettingsRequiresTheWriteSettingsScope()
    {
        var errors = await QueryErrorsAsync(
            UpdateCatalogSettingsMutation,
            new { input = new { refreshSeconds = 300 } },
            AddNonOwnerToken());

        Assert.Equal(
            "permission denied: requires write_settings scope",
            errors[0].GetProperty("message").GetString());
        AssertNoCatalogSettingsWritten();
        Assert.Equal(0, _catalog.InvalidateCalls);
        Assert.Equal(0, _reschedule.Calls);
    }

    [Fact]
    public async Task ProvidersCatalogDefaultsToTheFilteredSnapshot()
    {
        var data = await QueryDataAsync(ProvidersCatalogQuery);
        var catalog = data.GetProperty("providersCatalog");

        Assert.Equal([true], _catalog.SnapshotFilters);
        Assert.Equal(
            "gpt-4o",
            catalog.GetProperty("data").GetProperty("providers").GetProperty("openai")
                .GetProperty("models")[0].GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", catalog.GetProperty("fetchedAt").GetString());
        Assert.Equal("fallback", catalog.GetProperty("source").GetString());
        Assert.True(catalog.GetProperty("filtered").GetBoolean());
    }

    [Fact]
    public async Task ProvidersCatalogHonorsAnExplicitFilteredArgument()
    {
        var data = await QueryDataAsync(
            "{ providersCatalog(filtered: false) { source filtered } }");
        var catalog = data.GetProperty("providersCatalog");

        Assert.Equal([false], _catalog.SnapshotFilters);
        Assert.False(catalog.GetProperty("filtered").GetBoolean());
    }

    [Fact]
    public async Task ProvidersCatalogWrapsSnapshotFailures()
    {
        _catalog.SnapshotFailure = new InvalidOperationException(
            "fetch catalog: unexpected status 502");

        var errors = await QueryErrorsAsync(ProvidersCatalogQuery);

        Assert.Equal(
            "failed to load providers catalog: fetch catalog: unexpected status 502",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProvidersCatalogRequiresTheReadChannelsScope()
    {
        var errors = await QueryErrorsAsync(ProvidersCatalogQuery, token: AddNonOwnerToken());

        Assert.Equal(
            "permission denied: requires read_channels scope",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_catalog.SnapshotFilters);
    }

    [Fact]
    public async Task ProvidersCatalogDeniesServiceAccountKeys()
    {
        _gate.AddServiceAccountKey();
        using var client = _app.GetTestClient();
        AdminGraphqlGateServices.Authorize(client, "sk-service-account");

        var errors = await QueryErrorsAsync(client, ProvidersCatalogQuery);

        Assert.Equal(
            "permission denied: requires read_channels scope",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_catalog.SnapshotFilters);
    }

    [Fact]
    public async Task RefreshProvidersCatalogReturnsTheForcedSnapshot()
    {
        var data = await QueryDataAsync(RefreshProvidersCatalogMutation);
        var catalog = data.GetProperty("refreshProvidersCatalog");

        Assert.Equal(1, _catalog.RefreshCalls);
        Assert.Equal(
            "gpt-hot",
            catalog.GetProperty("data").GetProperty("providers").GetProperty("openai")
                .GetProperty("models")[0].GetProperty("id").GetString());
        Assert.Equal("2026-01-03T04:05:06Z", catalog.GetProperty("fetchedAt").GetString());
        Assert.Equal("upstream", catalog.GetProperty("source").GetString());
        Assert.True(catalog.GetProperty("filtered").GetBoolean());
    }

    [Fact]
    public async Task RefreshProvidersCatalogWrapsFetchFailures()
    {
        _catalog.RefreshFailure = new InvalidOperationException(
            "fetch catalog: unexpected status 503");

        var errors = await QueryErrorsAsync(RefreshProvidersCatalogMutation);

        Assert.Equal(
            "failed to refresh providers catalog: fetch catalog: unexpected status 503",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task RefreshProvidersCatalogRequiresTheWriteSettingsScope()
    {
        var errors = await QueryErrorsAsync(
            RefreshProvidersCatalogMutation,
            token: AddNonOwnerToken());

        Assert.Equal(
            "permission denied: requires write_settings scope",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(0, _catalog.RefreshCalls);
    }

    [Fact]
    public async Task CatalogReadsWithoutTheServiceReportNotConfigured()
    {
        var app = CreateBuilder(registerCatalog: false).Build();
        app.MapAdminGraphql();
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            _gate.Authorize(client);

            var errors = await QueryErrorsAsync(client, ProvidersCatalogQuery);
            Assert.Equal(
                "catalog service is not configured",
                errors[0].GetProperty("message").GetString());

            errors = await QueryErrorsAsync(client, RefreshProvidersCatalogMutation);
            Assert.Equal(
                "catalog service is not configured",
                errors[0].GetProperty("message").GetString());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpdateCatalogSettingsWithoutTheServiceStillPersists()
    {
        var app = CreateBuilder(registerCatalog: false).Build();
        app.MapAdminGraphql();
        await app.StartAsync();
        try
        {
            using var client = app.GetTestClient();
            _gate.Authorize(client);

            var data = await QueryDataAsync(
                client,
                UpdateCatalogSettingsMutation,
                new { input = new { refreshSeconds = 90 } });

            Assert.True(data.GetProperty("updateCatalogSettings").GetBoolean());
            Assert.Equal(
                CatalogSettingsCodec.Serialize(new CatalogSettings("", 90)),
                StoredCatalogSettings());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private WebApplicationBuilder CreateBuilder(bool registerCatalog = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<ISystemStore>(_store);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddScoped<ISystemSettingsAdminService>(
            _ => new SystemSettingsAdminService(_store));
        if (registerCatalog)
        {
            builder.Services.AddSingleton<ICatalogService>(_catalog);
            builder.Services.AddSingleton<ICatalogRescheduleSignal>(_reschedule);
        }

        return builder;
    }

    /// <summary>
    /// The fixture seeds the JWT secret into the same system store, so "nothing
    /// written" means "no catalog settings row", not "no rows at all".
    /// </summary>
    private void AssertNoCatalogSettingsWritten()
        => Assert.DoesNotContain(
            _store.Rows,
            row => string.Equals(row.Key, CatalogSettingsRules.Key, StringComparison.Ordinal));

    private string StoredCatalogSettings()
        => _store.Rows.Single(
            row => string.Equals(row.Key, CatalogSettingsRules.Key, StringComparison.Ordinal))
            .Value;

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

    private static JsonElement CatalogData(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
        => await QueryDataAsync(_client, query, variables, token);

    private async Task<JsonElement> QueryDataAsync(
        HttpClient client,
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(client, query, variables, token);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null)
        => await QueryErrorsAsync(_client, query, variables, token);

    private async Task<JsonElement[]> QueryErrorsAsync(
        HttpClient client,
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(client, query, variables, token);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        HttpClient client,
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
        // Without an explicit token, keep the client's own default header: the
        // service-account client sends its API key instead of the fixture JWT.
        if (token is not null)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private sealed class RecordingCatalogService : ICatalogService
    {
        public CatalogSnapshot? SnapshotResult { get; set; }

        public CatalogSnapshot? RefreshResult { get; set; }

        public Exception? SnapshotFailure { get; set; }

        public Exception? RefreshFailure { get; set; }

        public List<bool> SnapshotFilters { get; } = [];

        public int RefreshCalls { get; private set; }

        public int InvalidateCalls { get; private set; }

        public Task<CatalogSnapshot> SnapshotAsync(
            bool filtered,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            SnapshotFilters.Add(filtered);
            if (SnapshotFailure is not null)
            {
                return Task.FromException<CatalogSnapshot>(SnapshotFailure);
            }

            var result = SnapshotResult
                ?? throw new InvalidOperationException("snapshot result not configured");
            return Task.FromResult(result with { Filtered = filtered });
        }

        public Task<CatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            RefreshCalls++;
            if (RefreshFailure is not null)
            {
                return Task.FromException<CatalogSnapshot>(RefreshFailure);
            }

            return Task.FromResult(
                RefreshResult
                ?? throw new InvalidOperationException("refresh result not configured"));
        }

        public void Invalidate() => InvalidateCalls++;
    }

    private sealed class RecordingCatalogRescheduleSignal : ICatalogRescheduleSignal
    {
        public int Calls { get; private set; }

        public Task RescheduleAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Calls++;
            return Task.CompletedTask;
        }
    }
}
