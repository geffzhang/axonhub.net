using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the <c>channelProbeData</c> root over /admin/graphql
/// (channel_probe.graphql:1-20, channel_probe.resolvers.go:15-26,
/// biz/channel_probe.go:345-465): the frontend query shape, the Channel GUID
/// projection, the interval-aligned default and stored windows, the
/// read_channels fallback, the empty input and the unwrapped store error. The
/// root exists in the frozen baseline b8bb3421, so this slice follows the
/// baseline semantics.
/// </summary>
public sealed class AdminChannelProbeDataEndpointTests : IAsyncLifetime
{
    private const string ProbeQuery = """
        query GetChannelProbeData($input: GetChannelProbeDataInput!) {
          channelProbeData(input: $input) {
            channelID
            points {
              timestamp
              totalRequestCount
              successRequestCount
              avgTokensPerSecond
              avgTimeToFirstTokenMs
            }
          }
        }
        """;

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly RecordingChannelProbeStore _probes = new();
    private readonly TestTimeProvider _clock = new(new DateTimeOffset(
        2026,
        3,
        10,
        10,
        7,
        31,
        TimeSpan.Zero));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // The JWT user (id 9) is the owner by default; the deny test flips both
        // copies so the read_channels scope check fails.
        await _adminUsers.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
                Scopes = "[]",
            },
            roleIds: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_adminUsers);
        builder.Services.AddSingleton<IChannelProbeStore>(_probes);
        builder.Services.AddSingleton<IChannelStore>(new FakeChannelStore());
        builder.Services.AddSingleton<ChannelProbeRunState>();
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddScoped<ISystemSettingsAdminService, SystemSettingsAdminService>();
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddScoped<ChannelProbeService>();

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
    public async Task ProbeDataMatchesTheFrontendShape()
    {
        var end = new DateTimeOffset(2026, 3, 10, 10, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var start = end - 3600;
        _probes.Rows.Add(new ChannelProbeRecord
        {
            ChannelId = 42,
            Timestamp = start,
            TotalRequestCount = 10,
            SuccessRequestCount = 9,
            AvgTokensPerSecond = 12.5,
            AvgTimeToFirstTokenMs = 300,
        });

        var data = await QueryDataAsync(ChannelIds(Gid(42)));

        var channel = Assert.Single(data.GetProperty("channelProbeData").EnumerateArray());
        Assert.Equal("gid://axonhub/Channel/42", channel.GetProperty("channelID").GetString());

        var points = channel.GetProperty("points");
        Assert.Equal(13, points.GetArrayLength());
        var first = points[0];
        Assert.Equal(start, first.GetProperty("timestamp").GetInt32());
        Assert.Equal(10, first.GetProperty("totalRequestCount").GetInt32());
        Assert.Equal(9, first.GetProperty("successRequestCount").GetInt32());
        Assert.Equal(12.5, first.GetProperty("avgTokensPerSecond").GetDouble());
        Assert.Equal(300, first.GetProperty("avgTimeToFirstTokenMs").GetDouble());

        var gap = points[1];
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("avgTokensPerSecond").ValueKind);
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("avgTimeToFirstTokenMs").ValueKind);

        var query = Assert.Single(_probes.Queries);
        Assert.Equal([42L], query.ChannelIds);
        Assert.Equal(start, query.StartUnix);
        Assert.Equal(end, query.EndUnix);
    }

    [Fact]
    public async Task StoredFrequencyIsUsedForReadChannelsPrincipals()
    {
        _gate.Systems.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"probe":{"enabled":false,"frequency":"1h"}}""");

        var data = await QueryDataAsync(ChannelIds(Gid(1)));

        var channel = Assert.Single(data.GetProperty("channelProbeData").EnumerateArray());
        Assert.Equal(25, channel.GetProperty("points").GetArrayLength());
        var end = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(end - 1440 * 60, Assert.Single(_probes.Queries).StartUnix);
        Assert.Equal(end, Assert.Single(_probes.Queries).EndUnix);
    }

    [Fact]
    public async Task PrincipalsWithoutReadChannelsSeeTheDefaultWindow()
    {
        // ChannelSettingOrDefault swallows the privacy deny and returns the
        // default setting, so the query still succeeds.
        _gate.Systems.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            """{"probe":{"enabled":false,"frequency":"1h"}}""");
        _gate.Owner.IsOwner = false;
        var permissionUser = await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId)
            ?? throw new InvalidOperationException("gate user missing");
        permissionUser.IsOwner = false;

        var data = await QueryDataAsync(ChannelIds(Gid(1)));

        var channel = Assert.Single(data.GetProperty("channelProbeData").EnumerateArray());
        Assert.Equal(13, channel.GetProperty("points").GetArrayLength());
        var end = new DateTimeOffset(2026, 3, 10, 10, 5, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(end - 3600, Assert.Single(_probes.Queries).StartUnix);
        Assert.Equal(end, Assert.Single(_probes.Queries).EndUnix);
    }

    [Fact]
    public async Task EmptyChannelIdsReturnAnEmptyList()
    {
        var data = await QueryDataAsync(new { input = new { channelIDs = Array.Empty<string>() } });

        Assert.Equal("[]", data.GetProperty("channelProbeData").GetRawText());
        Assert.Empty(_probes.Queries);
    }

    [Fact]
    public async Task MalformedGuidsAreRejectedBeforeTheService()
    {
        var errors = await QueryErrorsAsync(ChannelIds("3"));

        Assert.Equal(
            "guid must start with gid://axonhub/",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_probes.Queries);
    }

    [Fact]
    public async Task TheInputGuidTypeIsNotValidated()
    {
        // GUID.UnmarshalGQL parses the type but ConvertGUIDPtrsToInts drops it,
        // so a non-Channel type still addresses the channel id.
        var data = await QueryDataAsync(ChannelIds("gid://axonhub/Whatever/42"));

        var channel = Assert.Single(data.GetProperty("channelProbeData").EnumerateArray());
        Assert.Equal("gid://axonhub/Channel/42", channel.GetProperty("channelID").GetString());
        Assert.Equal([42L], Assert.Single(_probes.Queries).ChannelIds);
    }

    [Fact]
    public async Task StoreErrorsSurfaceWithoutAPrefix()
    {
        _probes.Failure = new InvalidOperationException("probe query failed");

        var errors = await QueryErrorsAsync(ChannelIds(Gid(1)));

        Assert.Equal("probe query failed", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task NodeResolvesTheProbeRow()
    {
        _probes.Rows.Add(new ChannelProbeRecord
        {
            Id = 7,
            ChannelId = 42,
            Timestamp = 1_772_000_000,
            TotalRequestCount = 10,
            SuccessRequestCount = 9,
            AvgTokensPerSecond = 12.5,
            AvgTimeToFirstTokenMs = 300,
        });

        var root = await ExecuteRawAsync(
            """
            query Node($id: ID!) {
              node(id: $id) {
                id
                ... on ChannelProbe {
                  channelID
                  totalRequestCount
                  successRequestCount
                  avgTokensPerSecond
                  avgTimeToFirstTokenMs
                  timestamp
                }
              }
            }
            """,
            new { id = "gid://axonhub/ChannelProbe/7" });

        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        var node = root.GetProperty("data").GetProperty("node");
        Assert.Equal("gid://axonhub/ChannelProbe/7", node.GetProperty("id").GetString());
        Assert.Equal("gid://axonhub/Channel/42", node.GetProperty("channelID").GetString());
        Assert.Equal(10, node.GetProperty("totalRequestCount").GetInt32());
        Assert.Equal(9, node.GetProperty("successRequestCount").GetInt32());
        Assert.Equal(12.5, node.GetProperty("avgTokensPerSecond").GetDouble());
        Assert.Equal(300, node.GetProperty("avgTimeToFirstTokenMs").GetDouble());
        Assert.Equal(1_772_000_000, node.GetProperty("timestamp").GetInt32());
    }

    [Fact]
    public async Task NodeReportsAMissingProbeAsEntNotFound()
    {
        var root = await ExecuteRawAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/ChannelProbe/999" });

        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        Assert.Equal(
            "ent: channel_probe not found",
            errors[0].GetProperty("message").GetString());
    }

    private static string Gid(long id) => new GidValue("Channel", id).ToString();

    private static object ChannelIds(params string[] ids)
        => new { input = new { channelIDs = ids } };

    private async Task<JsonElement> QueryDataAsync(object variables)
    {
        var root = await ExecuteAsync(variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(object variables)
    {
        var root = await ExecuteAsync(variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return [.. errors.EnumerateArray().Select(static error => error.Clone())];
    }

    private async Task<JsonElement> ExecuteAsync(object variables)
        => await ExecuteRawAsync(ProbeQuery, variables);

    private async Task<JsonElement> ExecuteRawAsync(string query, object variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
