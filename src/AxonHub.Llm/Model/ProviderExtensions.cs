using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.ProviderExtensions (llm/provider_extensions.go): provider and
/// API-format private sidecar data that never travels through the unified
/// request/response JSON model.
/// </summary>
public sealed class ProviderExtensions
{
    [JsonIgnore]
    public OpenAIResponsesProviderExtensions? OpenAIResponses { get; set; }
}

/// <summary>Port of llm.OpenAIResponsesProviderExtensions.</summary>
public sealed class OpenAIResponsesProviderExtensions
{
    [JsonIgnore]
    public OpenAIResponsesRequestExtensions? Request { get; set; }
}

/// <summary>
/// Port of llm.OpenAIResponsesRequestExtensions: the raw request fragments the
/// Responses outbound transformer replays so unsupported-by-the-unified-model
/// fields survive the inbound/outbound round trip.
/// </summary>
public sealed class OpenAIResponsesRequestExtensions
{
    [JsonIgnore]
    public string ReasoningContext { get; set; } = string.Empty;

    /// <summary>Go: <c>map[string]json.RawMessage</c> of the raw request fields.</summary>
    [JsonIgnore]
    public Dictionary<string, JsonElement>? RawFields { get; set; }

    [JsonIgnore]
    public List<OpenAIResponsesRawFragment>? RawTools { get; set; }

    [JsonIgnore]
    public List<string>? ToolSignatures { get; set; }

    [JsonIgnore]
    public JsonElement? RawToolChoice { get; set; }

    [JsonIgnore]
    public List<OpenAIResponsesRawFragment>? RawInputItems { get; set; }
}

/// <summary>
/// Port of llm.OpenAIResponsesRawFragment: one raw tool or input item kept for
/// replay, plus the position bookkeeping the merge needs.
/// </summary>
public sealed class OpenAIResponsesRawFragment
{
    [JsonIgnore]
    public string Type { get; set; } = string.Empty;

    [JsonIgnore]
    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public string CallId { get; set; } = string.Empty;

    [JsonIgnore]
    public int OriginalIndex { get; set; }

    /// <summary>Number of structured tools replaced when <see cref="Raw"/> is replayed.</summary>
    [JsonIgnore]
    public int RepresentedToolCount { get; set; }

    /// <summary>Go: <c>json.RawMessage</c>; null is the nil raw message.</summary>
    [JsonIgnore]
    public JsonElement? Raw { get; set; }
}

/// <summary>Port of llm.EnsureOpenAIResponsesProviderExtensions.</summary>
public static class OpenAIResponsesExtensions
{
    public static OpenAIResponsesProviderExtensions? EnsureOpenAIResponsesProviderExtensions(LlmRequest? request)
    {
        if (request is null)
        {
            return null;
        }

        request.ProviderExtensions ??= new ProviderExtensions();
        request.ProviderExtensions.OpenAIResponses ??= new OpenAIResponsesProviderExtensions();
        return request.ProviderExtensions.OpenAIResponses;
    }

    /// <summary>Port of llm.CloneProviderExtensions.</summary>
    public static ProviderExtensions? CloneProviderExtensions(ProviderExtensions? source)
    {
        if (source is null)
        {
            return null;
        }

        var cloned = new ProviderExtensions();
        if (source.OpenAIResponses is { } openAiResponses)
        {
            cloned.OpenAIResponses = new OpenAIResponsesProviderExtensions();
            if (openAiResponses.Request is { } request)
            {
                cloned.OpenAIResponses.Request = new OpenAIResponsesRequestExtensions
                {
                    ReasoningContext = request.ReasoningContext,
                    RawFields = CloneRawElementMap(request.RawFields),
                    RawTools = CloneRawFragments(request.RawTools),
                    ToolSignatures = request.ToolSignatures is null ? null : [.. request.ToolSignatures],
                    RawToolChoice = request.RawToolChoice,
                    RawInputItems = CloneRawFragments(request.RawInputItems),
                };
            }
        }

        return cloned;
    }

    /// <summary>
    /// Port of cloneRawMessageMap. <see cref="JsonElement"/> is an immutable
    /// snapshot, so the "copy" is the value itself.
    /// </summary>
    public static Dictionary<string, JsonElement>? CloneRawElementMap(Dictionary<string, JsonElement>? source)
        => source is { Count: > 0 } ? new Dictionary<string, JsonElement>(source, StringComparer.Ordinal) : null;

    /// <summary>Port of cloneOpenAIResponsesRawFragments.</summary>
    public static List<OpenAIResponsesRawFragment>? CloneRawFragments(List<OpenAIResponsesRawFragment>? source)
    {
        if (source is not { Count: > 0 })
        {
            return null;
        }

        var cloned = new List<OpenAIResponsesRawFragment>(source.Count);
        foreach (var fragment in source)
        {
            cloned.Add(new OpenAIResponsesRawFragment
            {
                Type = fragment.Type,
                Name = fragment.Name,
                CallId = fragment.CallId,
                OriginalIndex = fragment.OriginalIndex,
                RepresentedToolCount = fragment.RepresentedToolCount,
                Raw = fragment.Raw,
            });
        }

        return cloned;
    }
}
