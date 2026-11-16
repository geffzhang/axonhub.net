namespace AxonHub.Api.Management;

/// <summary>
/// Port of the request/execution external-storage key generators
/// (internal/server/biz/request.go:171-224). Keys are rooted at the data
/// storage's base path; the backends translate them.
/// </summary>
public static class RequestDataKeys
{
    public static string RequestBody(long projectId, long requestId)
        => "/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/requests/" + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/request_body.json";

    public static string ResponseBody(long projectId, long requestId)
        => "/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/requests/" + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/response_body.json";

    public static string ResponseChunks(long projectId, long requestId)
        => "/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/requests/" + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/response_chunks.json";

    public static string RequestDir(long projectId, long requestId)
        => "/" + projectId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/requests/" + requestId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string RequestExecutionsDir(long projectId, long requestId)
        => RequestDir(projectId, requestId) + "/executions";

    public static string ExecutionRequestBody(long projectId, long requestId, long executionId)
        => ExecutionDir(projectId, requestId, executionId) + "/request_body.json";

    public static string ExecutionResponseBody(long projectId, long requestId, long executionId)
        => ExecutionDir(projectId, requestId, executionId) + "/response_body.json";

    public static string ExecutionResponseChunks(long projectId, long requestId, long executionId)
        => ExecutionDir(projectId, requestId, executionId) + "/response_chunks.json";

    public static string ExecutionRequestDir(long projectId, long requestId, long executionId)
        => ExecutionDir(projectId, requestId, executionId);

    private static string ExecutionDir(long projectId, long requestId, long executionId)
        => RequestDir(projectId, requestId)
            + "/executions/" + executionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
