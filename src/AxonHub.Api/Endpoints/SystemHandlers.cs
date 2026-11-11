using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Api.Assets;
using AxonHub.Api.Auth;
using AxonHub.Api.Settings;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Ports the REST half of internal/server/api/system.go: the public pair
/// (health and favicon) and the unauthenticated admin pair for first-run
/// initialization (routes.go:81-99).
/// </summary>
public static class SystemHandlers
{
    private static readonly JsonSerializerOptions RequestJson =
        new(JsonSerializerDefaults.Web);

    private static readonly Action<ILogger, Exception?> LogBrandLogoReadFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "BrandLogoReadFailed"),
            "Failed to get brand logo");

    /// <summary>routes.go:84,86,94-95.</summary>
    public static IEndpointRouteBuilder MapSystemRoutes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/favicon", GetFaviconAsync);
        endpoints.MapGet("/health", HealthAsync);
        endpoints.MapGet("/admin/system/status", GetSystemStatusAsync);
        endpoints.MapPost("/admin/system/initialize", InitializeSystemAsync);
        return endpoints;
    }

    /// <summary>Port of SystemHandlers.GetSystemStatus (system.go:76-88).</summary>
    public static async Task GetSystemStatusAsync(
        HttpContext context,
        ISystemInitializationService initialization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(initialization);

        bool isInitialized;
        try
        {
            isInitialized = await initialization.IsInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Failed to check system status",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("isInitialized", isInitialized);
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of SystemHandlers.Health (system.go:90-101).</summary>
    public static async Task HealthAsync(
        HttpContext context,
        ISystemVersionService versions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var build = versions.GetBuildInfo();

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            // HealthResponse field order (system.go:43-50) and build.Info's
            // json tags (internal/build/info.go:35-42): commit and build_time
            // carry omitempty.
            writer.WriteStartObject();
            writer.WriteString("status", "healthy");
            writer.WriteString("timestamp", GoTime.Rfc3339Nano(timeProvider.GetLocalNow()));
            writer.WriteString("version", build.Version);
            writer.WritePropertyName("build");
            writer.WriteStartObject();
            writer.WriteString("version", build.Version);
            if (!string.IsNullOrEmpty(build.Commit))
            {
                writer.WriteString("commit", build.Commit);
            }

            if (!string.IsNullOrEmpty(build.BuildTime))
            {
                writer.WriteString("build_time", build.BuildTime);
            }

            writer.WriteString("go_version", build.GoVersion);
            writer.WriteString("platform", build.Platform);
            writer.WriteString("uptime", build.Uptime);
            writer.WriteEndObject();
            writer.WriteString("uptime", build.Uptime);
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body
            .WriteAsync(GoJson.Escape(buffer.ToArray()), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of SystemHandlers.InitializeSystem (system.go:132-185).</summary>
    public static async Task InitializeSystemAsync(
        HttpContext context,
        ISystemInitializationService initialization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(initialization);

        InitializeSystemRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<InitializeSystemRequest>(
                    context.Request.Body,
                    RequestJson,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await WriteInitializeResponseAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    success: false,
                    "Invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // gin's ShouldBindJSON with binding:"required,email" /
        // "required,min=6" / "required" (system.go:53-60). The email regex is
        // the same approximation documented in C166; min counts runes like
        // go-playground validator.
        if (request is null
            || string.IsNullOrEmpty(request.OwnerEmail)
            || !ManagementAuthHandlers.EmailRegex().IsMatch(request.OwnerEmail)
            || string.IsNullOrEmpty(request.OwnerPassword)
            || RuneCount(request.OwnerPassword) < 6
            || string.IsNullOrEmpty(request.OwnerFirstName)
            || string.IsNullOrEmpty(request.OwnerLastName)
            || string.IsNullOrEmpty(request.BrandName))
        {
            await WriteInitializeResponseAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    success: false,
                    "Invalid request format",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        bool isInitialized;
        try
        {
            isInitialized = await initialization.IsInitializedAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    "Failed to check initialization status",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (isInitialized)
        {
            await WriteInitializeResponseAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    success: false,
                    "System is already initialized",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await initialization.InitializeAsync(
                    new InitializeSystemParameters
                    {
                        OwnerEmail = request.OwnerEmail,
                        OwnerPassword = request.OwnerPassword,
                        OwnerFirstName = request.OwnerFirstName,
                        OwnerLastName = request.OwnerLastName,
                        BrandName = request.BrandName,
                        PreferLanguage = request.PreferLanguage ?? string.Empty,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteInitializeResponseAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    success: false,
                    "Failed to initialize system: " + ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteInitializeResponseAsync(
                context,
                StatusCodes.Status200OK,
                success: true,
                "System initialized successfully",
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of SystemHandlers.GetFavicon (system.go:187-251).</summary>
    public static async Task GetFaviconAsync(
        HttpContext context,
        ISystemSettingsAdminService settings,
        IFaviconProvider favicons,
        ILoggerFactory logging,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(favicons);
        ArgumentNullException.ThrowIfNull(logging);

        string brandLogo;
        try
        {
            brandLogo = await settings.GetBrandLogoAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // system.go:191-194: the read failure is logged, not surfaced; the
            // empty logo then falls through to the default icon.
            LogBrandLogoReadFailed(
                logging.CreateLogger(typeof(SystemHandlers).FullName!), ex);
            brandLogo = string.Empty;
        }

        if (string.IsNullOrEmpty(brandLogo))
        {
            var defaultFavicon = favicons.ReadDefaultFavicon();
            if (defaultFavicon is null)
            {
                await AbortResponse.WriteErrorAsync(
                        context,
                        StatusCodes.Status500InternalServerError,
                        "Failed to read default favicon",
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // The reference sets no Content-Length here; the embedded icon is
            // larger than net/http's 2048-byte buffer, so it streams chunked.
            await WriteFaviconAsync(
                    context,
                    "image/x-icon",
                    defaultFavicon,
                    setContentLength: false,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!brandLogo.StartsWith("data:", StringComparison.Ordinal))
        {
            await WriteBrandLogoErrorAsync(context, invalidFormat: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // strings.Split(brandLogo, ","): exactly one comma is required.
        var parts = brandLogo.Split(',', StringSplitOptions.None);
        if (parts.Length != 2)
        {
            await WriteBrandLogoErrorAsync(context, invalidFormat: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // "data:<mime>;base64,<payload>": the MIME type sits between the first
        // ':' and the first ';' of the header part.
        var headerPart = parts[0];
        var mimeStart = headerPart.IndexOf(':');
        var mimeEnd = headerPart.IndexOf(';');
        if (mimeStart == -1 || mimeEnd == -1)
        {
            await WriteBrandLogoErrorAsync(context, invalidFormat: true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var mimeType = headerPart[(mimeStart + 1)..mimeEnd];

        // base64.StdEncoding.DecodeString (base64.go decodeQuantum) accepts
        // only the standard alphabet, '=' padding and CR/LF; it requires
        // complete padding and ignores non-zero trailing bits. The pre-check
        // narrows Convert.FromBase64String's broader whitespace tolerance
        // (it ignores spaces and tabs too) to the reference's set.
        if (!IsGoBase64(parts[1]))
        {
            await WriteBrandLogoErrorAsync(context, invalidFormat: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        byte[] imageData;
        try
        {
            imageData = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            await WriteBrandLogoErrorAsync(context, invalidFormat: false, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteFaviconAsync(
                context,
                mimeType,
                imageData,
                setContentLength: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The two brand-logo 400s (system.go:214,240).</summary>
    private static Task WriteBrandLogoErrorAsync(
        HttpContext context,
        bool invalidFormat,
        CancellationToken cancellationToken)
        => AbortResponse.WriteErrorAsync(
            context,
            StatusCodes.Status400BadRequest,
            invalidFormat ? "Invalid brand logo format" : "Failed to decode brand logo",
            cancellationToken);

    /// <summary>Characters encoding/base64's decodeMap accepts (plus CR/LF).</summary>
    private static bool IsGoBase64(string value)
    {
        foreach (var ch in value)
        {
            if (ch is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9'
                or '+'
                or '/'
                or '='
                or '\r'
                or '\n')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static async Task WriteFaviconAsync(
        HttpContext context,
        string contentType,
        byte[] data,
        bool setContentLength,
        CancellationToken cancellationToken)
    {
        // system.go:204-206,245-250: every favicon response carries the image
        // type and a one-hour public cache; only the brand-logo branch sets
        // Content-Length explicitly.
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.Headers.CacheControl = "public, max-age=3600";
        if (setContentLength)
        {
            context.Response.ContentLength = data.Length;
        }

        await context.Response.Body.WriteAsync(data, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of api.InitializeSystemResponse (system.go:62-66).</summary>
    private static async Task WriteInitializeResponseAsync(
        HttpContext context,
        int statusCode,
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", success);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body
            .WriteAsync(GoJson.Escape(buffer.ToArray()), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>utf8.RuneCountInString, which go-playground's min uses.</summary>
    private static int RuneCount(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>Port of api.InitializeSystemRequest (system.go:52-60).</summary>
    private sealed record InitializeSystemRequest(
        string? OwnerEmail,
        string? OwnerPassword,
        string? OwnerFirstName,
        string? OwnerLastName,
        string? BrandName,
        string? PreferLanguage);
}
