using AxonHub.Grains.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;

namespace AxonHub.Api.Metrics;

/// <summary>
/// Port of <c>metrics.NewProvider</c> (internal/metrics/provider.go): builds
/// the OpenTelemetry MeterProvider from <c>metrics.enabled</c> and
/// <c>metrics.exporter.*</c>.
/// </summary>
/// <remarks>
/// <para>
/// The reference registers a five-second periodic reader around one of three
/// exporters (stdout, OTLP/gRPC, OTLP/HTTP) and also collects the global
/// "axonhub" meter used by the channel limiter. The port mirrors that with the
/// OpenTelemetry .NET SDK: the same two meter names are added to the provider,
/// so enabling metrics exports the general instruments and the channel-limiter
/// instruments through one pipeline.
/// </para>
/// <para>
/// A disabled configuration builds a provider without readers, matching
/// <c>sdk.NewMeterProvider()</c> in the reference: instruments exist but
/// nothing is aggregated or exported. The exporter type is only inspected when
/// metrics are enabled.
/// </para>
/// </remarks>
public static class MetricsProvider
{
    /// <summary>sdk.WithInterval(5 * time.Second).</summary>
    public const int ExportIntervalMilliseconds = 5_000;

    /// <summary>Builds the provider for the given options and meter name.</summary>
    public static MeterProvider Create(MetricsOptions options, string meterName)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);

        var builder = Sdk.CreateMeterProviderBuilder().AddMeter(meterName);
        if (!string.Equals(meterName, ChannelLimiterMetrics.MeterName, StringComparison.Ordinal))
        {
            builder.AddMeter(ChannelLimiterMetrics.MeterName);
        }

        if (!options.Enabled)
        {
            return builder.Build();
        }

        options.Validate();
        builder.AddReader(new PeriodicExportingMetricReader(
            CreateExporter(options),
            ExportIntervalMilliseconds,
            exportTimeoutMilliseconds: 30_000));

        return builder.Build();
    }

    /// <summary>Creates the exporter selected by metrics.exporter.type.</summary>
    internal static BaseExporter<Metric> CreateExporter(MetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.ExporterType switch
        {
            "stdout" => new ConsoleMetricExporter(new ConsoleExporterOptions
            {
                Targets = ConsoleExporterOutputTargets.Console,
            }),
            "otlpgrpc" => new OtlpMetricExporter(new OtlpExporterOptions
            {
                Protocol = OtlpExportProtocol.Grpc,
                Endpoint = ResolveEndpoint(options),
            }),
            "otlphttp" => new OtlpMetricExporter(new OtlpExporterOptions
            {
                Protocol = OtlpExportProtocol.HttpProtobuf,
                Endpoint = ResolveEndpoint(options),
            }),
            _ => throw new InvalidOperationException(
                $"unsupported metrics exporter type: \"{options.ExporterType}\""),
        };
    }

    /// <summary>
    /// Maps metrics.exporter.endpoint (a Go-style host:port) and
    /// metrics.exporter.insecure onto an OTLP URI. Go's exporters default to
    /// localhost:4317 (gRPC) and localhost:4318 (HTTP), with TLS unless
    /// insecure is set; an endpoint that already carries a scheme is honoured
    /// as-is (a leniency over <c>WithEndpoint</c>, which expects host:port).
    /// </summary>
    internal static Uri ResolveEndpoint(MetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var defaultPort = options.ExporterType == "otlpgrpc" ? 4317 : 4318;
        var endpoint = string.IsNullOrWhiteSpace(options.ExporterEndpoint)
            ? $"localhost:{defaultPort}"
            : options.ExporterEndpoint;

        if (endpoint.Contains("://", StringComparison.Ordinal))
        {
            return new Uri(endpoint, UriKind.Absolute);
        }

        var scheme = options.ExporterInsecure ? "http" : "https";
        return new Uri($"{scheme}://{endpoint}");
    }
}
