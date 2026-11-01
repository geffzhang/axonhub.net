using AxonHub.Llm.Http;

namespace AxonHub.Llm.Transformer.OpenCode;

/// <summary>Port of llm/transformer/opencode/headers.go.</summary>
public static class OpenCodeHeaders
{
    /// <summary>The current OpenCode conversation identifier header.</summary>
    public const string SessionHeader = "X-Opencode-Session";

    /// <summary>Used by OpenCode clients that expose their session id directly.</summary>
    public const string SessionIdHeader = "X-Session-Id";

    /// <summary>The legacy OpenCode session affinity header.</summary>
    public const string SessionAffinityHeader = "X-Session-Affinity";

    private static readonly string[] Headers = [SessionHeader, SessionIdHeader, SessionAffinityHeader];

    /// <summary>Port of opencode.GetSessionIDFromHeaders: the first non-empty id, trimmed.</summary>
    public static string GetSessionIdFromHeaders(Dictionary<string, List<string>>? headers)
    {
        foreach (var header in Headers)
        {
            var sessionId = HttpClientUtils.HeaderGet(headers, header).Trim();
            if (sessionId.Length > 0)
            {
                return sessionId;
            }
        }

        return string.Empty;
    }
}
