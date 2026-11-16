using AxonHub.Api.Cli;
using AxonHub.Api.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AxonHub.Api.Tests;

/// <summary>
/// C302/C303: the request-path configuration surface. C302 now feeds the
/// multipart memory threshold HotChocolate's reader consumes; C303 remains an
/// accepted configuration-only surface and is locked here.
/// </summary>
public sealed class ConfigurationSurfaceTests
{
    [Fact]
    public void MaxMultipartMemoryDefaultsToGinDefault()
    {
        var options = BuildFormOptions(EmptyConfiguration());

        Assert.Equal(32 * 1024 * 1024, options.MemoryBufferThreshold);
    }

    [Theory]
    [InlineData("1M", 1 * 1024 * 1024)]
    [InlineData("512K", 512 * 1024)]
    [InlineData("1048576", 1048576)]
    public void MaxMultipartMemoryFeedsTheFormOptionsThreshold(string value, int expected)
    {
        var configuration = NewConfiguration(("Server:MaxMultipartMemory", value));

        var options = BuildFormOptions(configuration);

        Assert.Equal(expected, options.MemoryBufferThreshold);
    }

    [Fact]
    public void EnvironmentSpellingIsUsedWhenTheServerKeyIsAbsent()
    {
        var configuration = NewConfiguration(
            ("AXONHUB_SERVER_MAX_MULTIPART_MEMORY", "2M"));

        Assert.Equal(2 * 1024 * 1024, CliConfiguration.GetMaxMultipartMemory(configuration));
    }

    [Fact]
    public void ServerKeyWinsOverTheEnvironmentSpelling()
    {
        var configuration = NewConfiguration(
            ("Server:MaxMultipartMemory", "1M"),
            ("AXONHUB_SERVER_MAX_MULTIPART_MEMORY", "2M"));

        Assert.Equal(1 * 1024 * 1024, CliConfiguration.GetMaxMultipartMemory(configuration));
    }

    [Fact]
    public void ZeroUsesGinDefault()
    {
        var configuration = NewConfiguration(("Server:MaxMultipartMemory", "0"));

        var options = BuildFormOptions(configuration);

        Assert.Equal(32 * 1024 * 1024, options.MemoryBufferThreshold);
    }

    [Fact]
    public void InvalidMemorySizeFailsLikeTheReference()
    {
        var configuration = NewConfiguration(("Server:MaxMultipartMemory", "12X"));

        var error = Assert.Throws<InvalidOperationException>(
            () => MultipartMemoryOptions.Configure(new ServiceCollection(), configuration));

        Assert.Equal(
            "unknown memory size suffix: \"12X\" (use K, M, or G)",
            error.Message);
    }

    [Fact]
    public void LogSettingsSurfaceMatchesTheReferenceKeys()
    {
        var empty = EmptyConfiguration();
        Assert.Equal("axonhub", CliConfiguration.GetLogName(empty));
        Assert.Equal("info", CliConfiguration.GetLogLevel(empty));
        Assert.False(CliConfiguration.GetLogDebug(empty));
        Assert.Equal("json", CliConfiguration.GetLogEncoding(empty));
        Assert.Equal("stdio", CliConfiguration.GetLogOutput(empty));

        var overridden = NewConfiguration(
            ("Log:Name", "gateway"),
            ("Log:Level", "warn"),
            ("Log:Debug", "true"),
            ("Log:Encoding", "console"),
            ("Log:Output", "file"));
        Assert.Equal("gateway", CliConfiguration.GetLogName(overridden));
        Assert.Equal("warn", CliConfiguration.GetLogLevel(overridden));
        Assert.True(CliConfiguration.GetLogDebug(overridden));
        Assert.Equal("console", CliConfiguration.GetLogEncoding(overridden));
        Assert.Equal("file", CliConfiguration.GetLogOutput(overridden));
    }

    [Fact]
    public void LogEnvironmentSpellingsAreReadWhenTheSectionKeyIsAbsent()
    {
        var configuration = NewConfiguration(
            ("AXONHUB_LOG_LEVEL", "error"),
            ("AXONHUB_LOG_ENCODING", "console"));

        Assert.Equal("error", CliConfiguration.GetLogLevel(configuration));
        Assert.Equal("console", CliConfiguration.GetLogEncoding(configuration));
    }

    private static FormOptions BuildFormOptions(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        MultipartMemoryOptions.Configure(services, configuration);

        return services.BuildServiceProvider()
            .GetRequiredService<IOptions<FormOptions>>()
            .Value;
    }

    private static IConfiguration EmptyConfiguration() => NewConfiguration();

    private static IConfiguration NewConfiguration(
        params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(
                values.Select(
                    pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();
}
