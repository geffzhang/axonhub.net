using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.RequestService.LoadCompletedResponsesSession
/// (internal/server/biz/request.go:1438-1489): reloads the original request and
/// final response of a completed Responses turn so
/// <c>previous_response_id</c> keeps working after a restart or on another
/// instance.
/// </summary>
/// <remarks>
/// The reference reads the API key and project from the request context; the
/// port receives the authenticated <see cref="RequestIdentity"/>. The lookup is
/// scoped to that API key (and project) because provider response ids are
/// supplied by the client. Any loading error propagates so the store can log it
/// and fall back to the client-provided history.
/// </remarks>
public sealed class CompletedResponsesSessionLoader(
    IRequestStore requestStore,
    RequestPayloadStorage payloads)
{
    private static readonly string[] Formats =
    [
        ApiFormats.OpenAIResponse,
        ApiFormats.OpenAIResponseWebSocket,
    ];

    public async Task<ResponsesSessionLoaded> LoadAsync(
        string responseId,
        RequestIdentity identity,
        CancellationToken cancellationToken)
    {
        var trimmed = (responseId ?? string.Empty).Trim();
        if (trimmed.Length == 0 || identity.ApiKeyId is not { } apiKeyId)
        {
            return ResponsesSessionLoaded.NotFound;
        }

        var request = await requestStore.FindLatestCompletedResponsesSessionAsync(
                trimmed,
                apiKeyId,
                Formats,
                identity.Project?.Id,
                cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return ResponsesSessionLoaded.NotFound;
        }

        var storedRequest = Encoding.UTF8.GetBytes(
            await payloads.LoadRequestBodyAsync(request, cancellationToken).ConfigureAwait(false));
        var storedResponse = Encoding.UTF8.GetBytes(
            await payloads.LoadResponseBodyAsync(request, cancellationToken).ConfigureAwait(false));

        if (IsEmptyJson(storedRequest)
            || IsEmptyJson(storedResponse)
            || !IsValidJson(storedRequest)
            || !IsValidJson(storedResponse))
        {
            return ResponsesSessionLoaded.NotFound;
        }

        // request_responses_certification.go:20-46: only a self-contained turn
        // (no previous_response_id, matching id and completed status, object
        // items on both sides) may be replayed from persistence.
        if (!CertifiedResponsesSnapshot(storedRequest, storedResponse, trimmed))
        {
            return ResponsesSessionLoaded.NotFound;
        }

        return new ResponsesSessionLoaded(storedRequest, storedResponse, true);
    }

    private static bool IsEmptyJson(byte[] data)
        => data.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(RequestPayloadStorage.EmptyJson));

    /// <summary>Port of <c>certifiedResponsesSnapshot</c> (request_responses_certification.go:20-46).</summary>
    private static bool CertifiedResponsesSnapshot(
        byte[] requestBody,
        byte[] responseBody,
        string responseId)
    {
        using var request = JsonDocument.Parse(requestBody);
        using var response = JsonDocument.Parse(responseBody);
        var requestRoot = request.RootElement;
        var responseRoot = response.RootElement;

        if (requestRoot.ValueKind != JsonValueKind.Object
            || responseRoot.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // gjson.Exists() is true for an explicit null, but only a non-empty
        // string disqualifies the snapshot.
        if (requestRoot.TryGetProperty("previous_response_id", out var previous)
            && GoString(previous).Length != 0)
        {
            return false;
        }

        if (!responseRoot.TryGetProperty("id", out var id)
            || GoString(id) != responseId
            || !responseRoot.TryGetProperty("status", out var status)
            || GoString(status) != "completed"
            || !responseRoot.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (!requestRoot.TryGetProperty("input", out var input)
            || (input.ValueKind != JsonValueKind.String && input.ValueKind != JsonValueKind.Array))
        {
            return false;
        }

        if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }
            }
        }

        foreach (var item in output.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of <c>gjson.Result.String()</c> for a decoded element.</summary>
    private static string GoString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => string.Empty,
        };

    /// <summary>Go's json.Valid: syntax only, values do not need to be decoded.</summary>
    private static bool IsValidJson(byte[] data)
    {
        try
        {
            using var _ = JsonDocument.Parse(data);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
