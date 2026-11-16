using AxonHub.Api.Metrics;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/metrics/config.go and the metrics.exporter switch in
/// internal/metrics/provider.go (difference ledger C223).
/// </summary>
public sealed class MetricsOptionsTests
{
    [Fact]
    public void DefaultsMatchTheReference()
    {
        var options = MetricsOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(options.Enabled);
        Assert.Equal(string.Empty, options.ExporterType);
        Assert.Equal(string.Empty, options.ExporterEndpoint);
        Assert.False(options.ExporterInsecure);

        // A disabled config ignores the (empty) exporter type, like NewProvider.
        options.Validate();
    }

    [Fact]
    public void ReadsTheAxonHubEnvironmentSpelling()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AXONHUB_METRICS_ENABLED"] = "1",
                ["AXONHUB_METRICS_EXPORTER_TYPE"] = "otlphttp",
                ["AXONHUB_METRICS_EXPORTER_ENDPOINT"] = "alloy.alloy.svc.cluster.local:4318",
                ["AXONHUB_METRICS_EXPORTER_INSECURE"] = "true",
            })
            .Build();

        var options = MetricsOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal("otlphttp", options.ExporterType);
        Assert.Equal("alloy.alloy.svc.cluster.local:4318", options.ExporterEndpoint);
        Assert.True(options.ExporterInsecure);
    }

    /// <summary>Viper gives AutomaticEnv precedence over the config file.</summary>
    [Fact]
    public void EnvironmentOverridesConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Metrics:Enabled"] = "false",
                ["Metrics:Exporter:Type"] = "stdout",
                ["AXONHUB_METRICS_ENABLED"] = "true",
                ["AXONHUB_METRICS_EXPORTER_TYPE"] = "otlpgrpc",
            })
            .Build();

        var options = MetricsOptions.FromConfiguration(configuration);

        Assert.True(options.Enabled);
        Assert.Equal("otlpgrpc", options.ExporterType);
    }

    /// <summary>spf13/cast accepts 1/t/T/TRUE/true/True and nothing else.</summary>
    [Fact]
    public void InvalidEnvironmentBooleanMeansFalse()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AXONHUB_METRICS_ENABLED"] = "yes",
            })
            .Build();

        Assert.False(MetricsOptions.FromConfiguration(configuration).Enabled);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("otlpgrpc")]
    [InlineData("otlphttp")]
    public void KnownExporterTypesPassValidation(string exporterType)
    {
        var options = new MetricsOptions { Enabled = true, ExporterType = exporterType };

        options.Validate();

        Assert.Equal(exporterType, options.ExporterType);
    }

    [Fact]
    public void UnknownExporterFailsLikeTheReference()
    {
        var options = new MetricsOptions { Enabled = true, ExporterType = "prometheus" };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Equal(
            "unsupported metrics exporter type: \"prometheus\"",
            exception.Message);
    }

    [Theory]
    [InlineData("otlpgrpc", "", true, "http", "localhost", 4317, "/")]
    [InlineData("otlpgrpc", "", false, "https", "localhost", 4317, "/")]
    [InlineData("otlphttp", "", true, "http", "localhost", 4318, "/")]
    [InlineData("otlphttp", "collector:4318", true, "http", "collector", 4318, "/")]
    [InlineData("otlpgrpc", "collector:4317", false, "https", "collector", 4317, "/")]
    [InlineData("otlphttp", "https://collector.example/otlp", false, "https", "collector.example", 443, "/otlp")]
    public void ExporterEndpointFollowsInsecureAndProtocolDefaults(
        string exporterType,
        string endpoint,
        bool insecure,
        string expectedScheme,
        string expectedHost,
        int expectedPort,
        string expectedPath)
    {
        var options = new MetricsOptions
        {
            Enabled = true,
            ExporterType = exporterType,
            ExporterEndpoint = endpoint,
            ExporterInsecure = insecure,
        };

        var uri = MetricsProvider.ResolveEndpoint(options);

        Assert.Equal(expectedScheme, uri.Scheme);
        Assert.Equal(expectedHost, uri.Host);
        Assert.Equal(expectedPort, uri.Port);
        Assert.Equal(expectedPath, uri.AbsolutePath);
    }
}
