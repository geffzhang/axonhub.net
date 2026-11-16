using AxonHub.Llm.Http;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of <c>responses.ContinuationAuthIdentity</c>
/// (websocket_lease_write.go:13-16): hashes everything that identifies the
/// upstream credential and the per-request headers so a continuation is only
/// forwarded to an endpoint reached with the same identity.
/// </summary>
/// <remarks>
/// The helper lives in the Llm assembly because it reuses the executor's
/// header/credential hashing; the orchestrator calls it from the Responses
/// verification middleware (difference C289).
/// </remarks>
public static class ResponsesContinuationAuth
{
    /// <summary>The identity hash of the request the middleware is about to send.</summary>
    public static string Identity(HttpRequestData? request)
    {
        var headers = request?.Headers;
        var auth = request?.Auth;

        return WebSocketExecutor.HashSecret(
            WebSocketExecutor.AuthPoolIdentity(auth, headers)
            + "\x00"
            + WebSocketExecutor.HeaderPoolIdentity(headers)
            + "\x00"
            + HttpClientUtils.HeaderGet(headers, WebSocketExecutor.AccountIdHeader)
            + "\x00"
            + HttpClientUtils.HeaderGet(headers, WebSocketExecutor.OrgHeader)
            + "\x00"
            + HttpClientUtils.HeaderGet(headers, WebSocketExecutor.ProjectHeader));
    }
}
