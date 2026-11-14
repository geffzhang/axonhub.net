using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of the <c>server.cors.*</c> block (conf/conf.go:277-285) and its
/// rs/cors wiring in routes.go:67-79. <c>debug</c> only controls rs/cors'
/// logging, which the port leaves to ASP.NET Core logging, so it has no
/// observable request-path effect and is not carried here.
/// </summary>
public sealed record CorsSettings(
    bool Enabled,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedMethods,
    IReadOnlyList<string> AllowedHeaders,
    IReadOnlyList<string> ExposedHeaders,
    bool AllowCredentials,
    TimeSpan MaxAge)
{
    /// <summary>conf/conf.go:280.</summary>
    public static readonly string[] DefaultAllowedOrigins = ["http://localhost:8090"];

    /// <summary>conf/conf.go:281.</summary>
    public static readonly string[] DefaultAllowedMethods =
        ["GET", "POST", "DELETE", "PATCH", "PUT", "OPTIONS", "HEAD"];

    /// <summary>conf/conf.go:282.</summary>
    public static readonly string[] DefaultAllowedHeaders =
    [
        "Content-Type",
        "Authorization",
        "X-API-Key",
        "X-Goog-Api-Key",
        "X-Project-ID",
        "X-Thread-ID",
        "X-Trace-ID",
    ];

    /// <summary>conf/conf.go:285: server.cors.max_age defaults to "30m".</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(30);

    public static CorsSettings Default { get; } = new(
        Enabled: false,
        AllowedOrigins: DefaultAllowedOrigins,
        AllowedMethods: DefaultAllowedMethods,
        AllowedHeaders: DefaultAllowedHeaders,
        ExposedHeaders: Array.Empty<string>(),
        AllowCredentials: false,
        MaxAge: DefaultMaxAge);

    /// <summary>
    /// Reads the <c>Server:Cors</c> section first, then the reference's
    /// <c>AXONHUB_SERVER_CORS_*</c> environment spelling. Scalar list values are
    /// split on commas; a bound array (including an explicitly empty one) wins.
    /// </summary>
    public static CorsSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabledValue = configuration["Server:Cors:Enabled"]
            ?? configuration["AXONHUB_SERVER_CORS_ENABLED"];
        var enabled = bool.TryParse(enabledValue, out var parsed) ? parsed : false;

        var credentialsValue = configuration["Server:Cors:AllowCredentials"]
            ?? configuration["AXONHUB_SERVER_CORS_ALLOW_CREDENTIALS"];
        var allowCredentials = bool.TryParse(credentialsValue, out var credentials) && credentials;

        var maxAgeValue = configuration["Server:Cors:MaxAge"]
            ?? configuration["AXONHUB_SERVER_CORS_MAX_AGE"];
        var maxAge = string.IsNullOrEmpty(maxAgeValue)
            ? DefaultMaxAge
            : GoDurationParser.Parse(maxAgeValue);

        return new CorsSettings(
            enabled,
            ReadList(
                configuration,
                "Server:Cors:AllowedOrigins",
                "AXONHUB_SERVER_CORS_ALLOWED_ORIGINS",
                DefaultAllowedOrigins),
            ReadList(
                configuration,
                "Server:Cors:AllowedMethods",
                "AXONHUB_SERVER_CORS_ALLOWED_METHODS",
                DefaultAllowedMethods),
            ReadList(
                configuration,
                "Server:Cors:AllowedHeaders",
                "AXONHUB_SERVER_CORS_ALLOWED_HEADERS",
                DefaultAllowedHeaders),
            ReadList(
                configuration,
                "Server:Cors:ExposedHeaders",
                "AXONHUB_SERVER_CORS_EXPOSED_HEADERS",
                Array.Empty<string>()),
            allowCredentials,
            maxAge);
    }

    /// <summary>
    /// routes.go:69-76: every rs/cors field maps one-to-one onto the ASP.NET
    /// Core policy. <c>AllowAllOrigins</c> becomes an origin echo so the policy
    /// stays usable together with <c>AllowCredentials</c>.
    /// </summary>
    public void ConfigurePolicy(CorsPolicyBuilder policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (AllowedOrigins.Contains("*", StringComparer.Ordinal))
        {
            policy.SetIsOriginAllowed(static _ => true);
        }
        else
        {
            policy.WithOrigins([.. AllowedOrigins]);
        }

        policy.WithMethods([.. AllowedMethods]);
        policy.WithHeaders([.. AllowedHeaders]);

        if (ExposedHeaders.Count > 0)
        {
            policy.WithExposedHeaders([.. ExposedHeaders]);
        }

        if (AllowCredentials)
        {
            policy.AllowCredentials();
        }

        policy.SetPreflightMaxAge(MaxAge);
    }

    private static IReadOnlyList<string> ReadList(
        IConfiguration configuration,
        string key,
        string environmentKey,
        IReadOnlyList<string> fallback)
    {
        var section = configuration.GetSection(key);
        if (section.GetChildren().Any())
        {
            return section.Get<string[]>() ?? [];
        }

        var scalar = configuration[environmentKey] ?? configuration[key];
        if (scalar is null)
        {
            return fallback;
        }

        return scalar.Length == 0
            ? Array.Empty<string>()
            : scalar.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
