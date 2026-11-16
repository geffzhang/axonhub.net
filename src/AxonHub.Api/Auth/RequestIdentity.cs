using System.Globalization;
using AxonHub.Api.Http;
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

    /// <summary>Port of contexts.SourcePlayground: the admin playground chat.</summary>
    public const string SourcePlayground = "playground";

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
    /// Port of contexts.GetRequestID: the <c>ar-&lt;uuid&gt;</c> id minted by
    /// <c>RequestTracingMiddleware</c>, filled in from <c>HttpContext.Items</c>
    /// by <see cref="FromHttpContext"/>. Empty when no tracing middleware ran.
    /// </summary>
    public string? RequestId { get; init; }

    /// <summary>
    /// Port of contexts.GetSourceOrDefault(ctx, contexts.SourceAPI): the
    /// requests.source column records client traffic unless a caller marks the
    /// request as a channel test.
    /// </summary>
    public string Source { get; init; } = SourceApi;

    /// <summary>
    /// Port of <c>withSessionScopeForAPIKey</c> (middleware/auth.go:326-332):
    /// <c>api_key:&lt;id&gt;</c> plus <c>:project:&lt;id&gt;</c> when the key's
    /// project edge was loaded. Null for unauthenticated callers (the
    /// playground's user principal has its own scope in the reference).
    /// </summary>
    public string? SessionScope => ApiKeyId is not { } keyId
        ? null
        : "api_key:" + keyId.ToString(CultureInfo.InvariantCulture)
            + (Project is { } project
                ? ":project:" + project.Id.ToString(CultureInfo.InvariantCulture)
                : string.Empty);

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
    /// Port of contexts.GetUser: the management user behind a playground
    /// request. Only the playground sets it, and it lands on the request row's
    /// user_id (internal/server/biz/request.go:279-283).
    /// </summary>
    public long? UserId { get; init; }

    /// <summary>
    /// Port of <c>ctx.Err() == context.DeadlineExceeded</c> for the request the
    /// persistence middleware is finalizing. Null when no timeout middleware
    /// installed a feature (direct calls and tests).
    /// </summary>
    public Func<bool>? RequestDeadlineExpired { get; init; }

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

        var identity = context.Items.TryGetValue(HttpContextItemKey, out var item)
            && item is RequestIdentity found
                ? WithRequestId(found, RequestIds.FromHttpContext(context))
                : Default;

        return WithRequestDeadline(identity, context);
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
            identity = WithRequestDeadline(
                WithRequestId(found, RequestIds.FromHttpContext(context)),
                context);
            return true;
        }

        identity = WithRequestDeadline(Default, context);
        return false;
    }

    private static RequestIdentity WithRequestId(RequestIdentity identity, string? requestId)
        => string.IsNullOrEmpty(requestId) || !string.IsNullOrEmpty(identity.RequestId)
            ? identity
            : identity with { RequestId = requestId };

    private static RequestIdentity WithRequestDeadline(
        RequestIdentity identity,
        HttpContext context)
    {
        if (context.Features.Get<IRequestTimeoutFeature>() is { } feature)
        {
            return identity with { RequestDeadlineExpired = () => feature.IsTimedOut };
        }

        return identity;
    }
}
