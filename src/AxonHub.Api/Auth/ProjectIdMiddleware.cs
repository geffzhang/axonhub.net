using AxonHub.Api.GraphQL;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of middleware.WithProjectID
/// (internal/server/middleware/project.go:14-40): the X-Project-ID header
/// selects a project for user JWTs, but must not let an API key read another
/// project's data. Mounted after the auth gate on /admin/graphql, exactly like
/// the reference's adminGraphqlGroup middleware chain.
/// </summary>
public sealed class ProjectIdMiddleware(RequestDelegate next)
{
    public const string InvalidProjectId = "Invalid project ID";
    public const string ProjectNotAllowed = "Project ID is not allowed for this API key";

    /// <summary>ent.TypeProject.</summary>
    public const string ProjectType = "Project";

    public async Task InvokeAsync(HttpContext context)
    {
        // Go's c.GetHeader returns the first header line.
        var header = context.Request.Headers["X-Project-ID"];
        var value = header.Count == 0 ? string.Empty : header[0] ?? string.Empty;
        if (value.Length == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        GidValue gid;
        try
        {
            gid = GidValue.Parse(value);
        }
        catch (InvalidOperationException)
        {
            await AbortAsync(context, StatusCodes.Status400BadRequest, InvalidProjectId)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(gid.Type, ProjectType, StringComparison.Ordinal))
        {
            await AbortAsync(context, StatusCodes.Status400BadRequest, InvalidProjectId)
                .ConfigureAwait(false);
            return;
        }

        if (context.Items[AdminGraphqlIdentity.ContextKey] is AdminGraphqlIdentity identity)
        {
            // An API key is pinned to its own project (project.go:30-33).
            if (identity.IsApiKey && identity.ProjectId != gid.Id)
            {
                await AbortAsync(context, StatusCodes.Status403Forbidden, ProjectNotAllowed)
                    .ConfigureAwait(false);
                return;
            }

            context.Items[AdminGraphqlIdentity.ContextKey] = identity.WithProject(gid.Id);
        }

        await next(context).ConfigureAwait(false);
    }

    private static Task AbortAsync(HttpContext context, int statusCode, string message)
        => AbortResponse.WriteErrorAsync(context, statusCode, message, context.RequestAborted);
}
