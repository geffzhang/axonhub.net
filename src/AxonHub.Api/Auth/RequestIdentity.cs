using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Auth;

/// <summary>
/// The authenticated caller carried into the request chain, equivalent to the
/// reference's contexts.WithAPIKey + contexts.WithProjectID pair. The persistence
/// middleware reads it when creating request, execution and usage-log rows.
/// </summary>
public sealed record RequestIdentity(long ProjectId, long? ApiKeyId)
{
    /// <summary>Key used to carry the identity through <c>HttpContext.Items</c>.</summary>
    public const string HttpContextItemKey = "AxonHub.RequestIdentity";

    /// <summary>
    /// The persisted trace row id resolved by <c>TraceMiddleware</c>, or null
    /// when the request has no trace. This is the reference's contexts.Trace.
    /// </summary>
    public long? TraceId { get; init; }

    /// <summary>
    /// Identity used when no authentication middleware ran (internal callers and
    /// the slice's tests). The HTTP path always supplies the API key's project.
    /// </summary>
    public static RequestIdentity Default { get; } = new(1, null);

    /// <summary>
    /// Reads the identity published by <c>ApiKeyAuthMiddleware</c>; direct
    /// calls (tests/internal handlers) fall back to <see cref="Default"/>.
    /// </summary>
    public static RequestIdentity FromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(HttpContextItemKey, out var item)
            && item is RequestIdentity identity
                ? identity
                : Default;
    }

    /// <summary>
    /// Port of contexts.GetProjectID: the project only exists when the
    /// authentication middleware published it. Trace persistence is skipped
    /// without one, so this variant reports the difference instead of falling
    /// back to the default project.
    /// </summary>
    public static bool TryFromHttpContext(HttpContext context, out RequestIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(HttpContextItemKey, out var item)
            && item is RequestIdentity found)
        {
            identity = found;
            return true;
        }

        identity = Default;
        return false;
    }
}
