using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Metrics;

/// <summary>
/// Port of <c>metrics.Config</c> and <c>metrics.ExporterConfig</c>
/// (internal/metrics/config.go) with the <c>metrics.*</c> defaults from
/// conf/conf.go:293-300.
/// </summary>
/// <remarks>
/// <para>
/// The reference reads these keys through Viper: <c>metrics.enabled</c>,
/// <c>metrics.exporter.type</c>, <c>metrics.exporter.endpoint</c> and
/// <c>metrics.exporter.insecure</c>, with <c>AXONHUB_METRICS_*</c> environment
/// overrides (dots become underscores). Viper gives the environment precedence
/// over the config file, so <see cref="FromConfiguration"/> does the same: the
/// flat environment spelling wins when it is present.
/// </para>
/// <para>
/// The exporter type is validated only when metrics are enabled. The reference
/// has no startup validation either — <c>metrics.NewProvider</c> rejects an
/// unknown type when it builds the exporter, and a disabled config never looks
/// at the type.
/// </para>
/// </remarks>
public sealed record MetricsOptions
{
    /// <summary>metrics.enabled. Default false.</summary>
    public bool Enabled { get; init; }

    /// <summary>metrics.exporter.type: stdout, otlpgrpc or otlphttp. Default empty.</summary>
    public string ExporterType { get; init; } = string.Empty;

    /// <summary>metrics.exporter.endpoint (host:port). Default empty.</summary>
    public string ExporterEndpoint { get; init; } = string.Empty;

    /// <summary>metrics.exporter.insecure. Default false.</summary>
    public bool ExporterInsecure { get; init; }

    /// <summary>Binds the process configuration and the AXONHUB_METRICS_* environment.</summary>
    public static MetricsOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Metrics");

        return new MetricsOptions
        {
            Enabled = ReadBool(
                configuration["AXONHUB_METRICS_ENABLED"],
                section["Enabled"]),
            ExporterType = ReadString(
                configuration["AXONHUB_METRICS_EXPORTER_TYPE"],
                section["Exporter:Type"]),
            ExporterEndpoint = ReadString(
                configuration["AXONHUB_METRICS_EXPORTER_ENDPOINT"],
                section["Exporter:Endpoint"]),
            ExporterInsecure = ReadBool(
                configuration["AXONHUB_METRICS_EXPORTER_INSECURE"],
                section["Exporter:Insecure"]),
        };
    }

    /// <summary>
    /// Mirrors the exporter switch in <c>metrics.NewProvider</c>: an unknown
    /// type fails startup with <c>unsupported metrics exporter type: "x"</c>.
    /// A disabled provider ignores the type, so validation is a no-op there.
    /// </summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (ExporterType is "stdout" or "otlpgrpc" or "otlphttp")
        {
            return;
        }

        throw new InvalidOperationException(
            $"unsupported metrics exporter type: \"{ExporterType}\"");
    }

    private static string ReadString(string? environmentValue, string? configurationValue)
        => environmentValue ?? configurationValue ?? string.Empty;

    /// <summary>
    /// Viper's GetBool uses spf13/cast (strconv.ParseBool): "1"/"t"/"T"/"TRUE"/
    /// "true"/"True" are true, everything else is false. An environment value
    /// that is present but invalid therefore yields false instead of falling
    /// back to the configuration file, exactly like Viper.
    /// </summary>
    private static bool ReadBool(string? environmentValue, string? configurationValue)
    {
        var raw = environmentValue ?? configurationValue;
        return raw switch
        {
            "1" or "t" or "T" or "TRUE" or "true" or "True" => true,
            _ => false,
        };
    }
}
