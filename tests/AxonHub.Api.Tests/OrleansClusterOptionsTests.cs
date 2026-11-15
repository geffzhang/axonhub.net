using AxonHub.Api.Lifecycle;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// The single-silo default must stay unchanged; enabling
/// OrleansCluster builds the AdoNet cluster settings and uses the app's
/// PostgreSQL connection unless an explicit cluster connection is configured.
/// </summary>
public sealed class OrleansClusterOptionsTests
{
    [Fact]
    public void DisabledByDefaultKeepsTheSingleSiloMode()
    {
        var options = OrleansClusterOptions.FromConfiguration(
            Configuration(),
            "Host=postgres;Database=axonhub");

        Assert.False(options.Enabled);
        Assert.Null(options.AdvertisedAddress);
        Assert.Equal("axonhub", options.ClusterId);
        Assert.Equal("axonhub", options.ServiceId);
        Assert.Equal(11111, options.SiloPort);
        Assert.Equal(30000, options.GatewayPort);
    }

    [Fact]
    public void EnabledBuildsTheAdoNetClusterConfiguration()
    {
        var options = OrleansClusterOptions.FromConfiguration(
            Configuration(
                ("OrleansCluster:Enabled", "true"),
                ("OrleansCluster:Provider", "adonet"),
                ("OrleansCluster:ClusterId", "axonhub-test"),
                ("OrleansCluster:ServiceId", "axonhub-api"),
                ("OrleansCluster:SiloName", "silo-0"),
                ("OrleansCluster:AdvertisedIP", "127.0.0.1"),
                ("OrleansCluster:SiloPort", "11112"),
                ("OrleansCluster:GatewayPort", "0")),
            "Host=postgres;Database=axonhub");

        Assert.True(options.Enabled);
        Assert.Equal("axonhub-test", options.ClusterId);
        Assert.Equal("axonhub-api", options.ServiceId);
        Assert.Equal("silo-0", options.SiloName);
        Assert.Equal("127.0.0.1", options.AdvertisedAddress?.ToString());
        Assert.Equal(11112, options.SiloPort);
        Assert.Equal(0, options.GatewayPort);
        Assert.Equal("Host=postgres;Database=axonhub", options.ConnectionString);
    }

    [Fact]
    public void ExplicitClusterConnectionWinsOverTheAppDatabase()
    {
        var options = OrleansClusterOptions.FromConfiguration(
            Configuration(
                ("OrleansCluster:Enabled", "true"),
                ("OrleansCluster:AdvertisedIP", "127.0.0.1"),
                ("OrleansCluster:ConnectionString", "Host=cluster;Database=orleans")),
            "Host=app;Database=axonhub");

        Assert.Equal("Host=cluster;Database=orleans", options.ConnectionString);
    }

    [Theory]
    [InlineData("kubernetes", "unsupported Orleans clustering provider")]
    [InlineData("sqlite", "unsupported Orleans clustering provider")]
    public void UnknownProvidersFailFast(string provider, string expected)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => OrleansClusterOptions.FromConfiguration(
                Configuration(
                    ("OrleansCluster:Enabled", "true"),
                    ("OrleansCluster:Provider", provider),
                    ("OrleansCluster:AdvertisedIP", "127.0.0.1")),
                "Host=postgres"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledRequiresAConnectionString()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => OrleansClusterOptions.FromConfiguration(
                Configuration(
                    ("OrleansCluster:Enabled", "true"),
                    ("OrleansCluster:AdvertisedIP", "127.0.0.1")),
                string.Empty));

        Assert.Contains("ConnectionString", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OrleansCluster:SiloPort", "0")]
    [InlineData("OrleansCluster:SiloPort", "70000")]
    [InlineData("OrleansCluster:GatewayPort", "70000")]
    public void InvalidPortsFailFast(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => OrleansClusterOptions.FromConfiguration(
                Configuration(
                    ("OrleansCluster:Enabled", "true"),
                    ("OrleansCluster:AdvertisedIP", "127.0.0.1"),
                    (key, value)),
                "Host=postgres"));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                values.Select(pair =>
                    new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
