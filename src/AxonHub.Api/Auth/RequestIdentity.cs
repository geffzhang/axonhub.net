using AxonHub.Persistence;
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

    /// <summary>Port of contexts.SourceAPI (internal/contexts/source.go): client traffic.</summary>
    public const string SourceApi = "api";

    /// <summary>Port of contexts.SourceTest: the channel tester's synthetic requests.</summary>
    public const string SourceTest = "test";

    /// <summary>
    /// The persisted trace row id resolved by <c>TraceMiddleware</c>, or null
    /// when the request has no trace. This is the reference's contexts.Trace.
    /// </summary>
    public long? TraceId { get; init; }

    /// <summary>
    /// The thread row id published by <c>ThreadMiddleware</c> (the reference's
    /// contexts.GetThread). Trace sticky routing falls back to the thread scope
    /// when the trace has no cached channel.
    /// </summary>
    public long? ThreadId { get; init; }

    /// <summary>
    /// The thread id stored on the resolved trace row, when a trace was
    /// persisted. Sticky routing reads the current thread first and falls back
    /// to this value; the previous-channel cache writes the thread scope from
    /// this value only (request.go:1768-1774).
    /// </summary>
    public long? TraceThreadId { get; init; }

    /// <summary>
    /// Port of contexts.GetSourceOrDefault(ctx, contexts.SourceAPI): the
    /// requests.source column records client traffic unless a caller marks the
    /// request as a channel test.
    /// </summary>
    public string Source { get; init; } = SourceApi;

    /// <summary>
    /// Port of contexts.GetAPIKey: the authenticated API key row, including the
    /// project edge the reference's AuthenticateAPIKey loads. The models
    /// surface reads its active profile (ListEnabledModels).
    /// </summary>
    public ApiKeyRecord? ApiKey { get; init; }

    /// <summary>
    /// Port of contexts.WithProjectID: the key's project, loaded once during
    /// authentication. Null when the caller never authenticated.
    /// </summary>
    public ProjectRecord? Project { get; init; }

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
