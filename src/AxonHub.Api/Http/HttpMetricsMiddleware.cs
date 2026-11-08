using System.Text;
using AxonHub.Api.Metrics;
using AxonHub.Api.Static;
using Microsoft.AspNetCore.Routing;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of <c>middleware.WithMetrics</c> (internal/server/middleware/metrics.go),
/// mounted at routes.go:64 after the logging/tracing middlewares.
/// </summary>
/// <remarks>
/// <para>
/// The reference records <c>method</c>, <c>path</c> (gin's
/// <c>c.FullPath()</c>), <c>status_code</c> and the elapsed seconds after
/// <c>c.Next()</c> returns. The port keeps the same point in the pipeline and
/// the same timing source (<see cref="TimeProvider"/>). A handler that panics
/// never reaches the recording lines, matching gin: the outer Recovery
/// middleware answers 500 but <c>WithMetrics</c> sits inside it.
/// </para>
/// <para>
/// <c>c.FullPath()</c> is the registered gin route without a leading slash
/// difference — parameters are <c>:name</c> and catch-alls are <c>*name</c> —
/// and is empty for the NoRoute/static fallback. ASP.NET route patterns use
/// <c>{name}</c>, <c>{*name}</c> and <c>{**name}</c>, so
/// <see cref="ToGinRoutePath"/> converts them back; the Gemini group is
/// registered with gin's <c>:gemini-api-version</c> name to keep the tag
/// identical.
/// </para>
/// </remarks>
public sealed class HttpMetricsMiddleware(
    RequestDelegate next,
    ServerMetrics metrics,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var start = timeProvider.GetTimestamp();

        await next(context).ConfigureAwait(false);

        metrics.RecordHttpRequest(
            context.Request.Method,
            RouteTemplate(context),
            context.Response.StatusCode,
            timeProvider.GetElapsedTime(start).TotalSeconds);
    }

    /// <summary>The gin FullPath() equivalent: empty for the static fallback.</summary>
    internal static string RouteTemplate(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is null
            || endpoint.Metadata.GetMetadata<StaticFallbackMetadata>() is not null)
        {
            return string.Empty;
        }

        return endpoint is RouteEndpoint routeEndpoint
            ? ToGinRoutePath(routeEndpoint.RoutePattern.RawText ?? string.Empty)
            : string.Empty;
    }

    /// <summary>Converts an ASP.NET route pattern to gin's FullPath syntax.</summary>
    internal static string ToGinRoutePath(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        if (rawText.IndexOf('{') < 0)
        {
            return rawText;
        }

        var builder = new StringBuilder(rawText.Length);

        for (var i = 0; i < rawText.Length; i++)
        {
            if (rawText[i] != '{')
            {
                builder.Append(rawText[i]);
                continue;
            }

            var end = rawText.IndexOf('}', i + 1);
            if (end < 0)
            {
                builder.Append(rawText[i]);
                continue;
            }

            var parameter = rawText.AsSpan(i + 1, end - i - 1);
            if (parameter.StartsWith("**"))
            {
                builder.Append('*').Append(parameter[2..]);
            }
            else if (parameter.StartsWith('*'))
            {
                builder.Append('*').Append(parameter[1..]);
            }
            else
            {
                // Drop ASP.NET constraints/defaults ("{id:int}", "{id?}").
                var delimiter = parameter.IndexOfAny(':', '?', '=');
                builder.Append(':')
                    .Append(delimiter < 0 ? parameter : parameter[..delimiter]);
            }

            i = end;
        }

        return builder.ToString();
    }
}
