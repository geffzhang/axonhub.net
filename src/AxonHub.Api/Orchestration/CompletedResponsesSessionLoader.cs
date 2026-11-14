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

        return new ResponsesSessionLoaded(storedRequest, storedResponse, true);
    }

    private static bool IsEmptyJson(byte[] data)
        => data.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(RequestPayloadStorage.EmptyJson));

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
