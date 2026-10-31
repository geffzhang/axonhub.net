using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Fixture loaders and comparison helpers shared by the Responses streaming
/// tests. The reference tests decode both sides into their structs and rely on
/// go-cmp; the port decodes into the same port model and compares canonical
/// JSON, which preserves go-cmp's field-set semantics while keeping the
/// comparison readable.
/// </summary>
internal static class ResponsesStreamTestSupport
{
    public static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "responses");

    /// <summary>Runtime-generated fields ignored by inbound_stream_test.go.</summary>
    public static readonly IReadOnlySet<string> InboundIgnoreFields =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "item_id",
            "obfuscation",
            "logprobs",
            "response",
        };

    /// <summary>The final-response comparison drops the response branch too.</summary>
    public static readonly IReadOnlySet<string> ResponseIgnoreFields =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "id",
            "item_id",
            "obfuscation",
            "logprobs",
        };

    public static List<StreamEvent> LoadStreamChunks(string fileName)
    {
        var events = new List<StreamEvent>();
        foreach (var line in File.ReadLines(Path.Combine(TestDataRoot, fileName)))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            events.Add(new StreamEvent
            {
                LastEventId = root.TryGetProperty("LastEventID", out var lastEventId)
                    ? lastEventId.GetString()
                    : null,
                Type = root.TryGetProperty("Type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                Data = Encoding.UTF8.GetBytes(
                    root.TryGetProperty("Data", out var data) ? data.GetString() ?? string.Empty : string.Empty),
            });
        }

        return events;
    }

    public static List<LlmResponse> LoadLlmResponses(string fileName)
    {
        var responses = new List<LlmResponse>();
        foreach (var chunk in LoadStreamChunks(fileName))
        {
            if (Encoding.UTF8.GetString(chunk.Data).Contains("[DONE]", StringComparison.Ordinal))
            {
                responses.Add(LlmResponse.Done());
                continue;
            }

            responses.Add(LlmJson.Deserialize<LlmResponse>(chunk.Data)!);
        }

        return responses;
    }

    public static T LoadFixture<T>(string fileName)
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestDataRoot, fileName));
        return LlmJson.Deserialize<T>(bytes)!;
    }

    public static StreamEvent Event(string type, string data)
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(data) };

    public static StreamEvent Event(string data)
        => new() { Data = Encoding.UTF8.GetBytes(data) };

    public static Dictionary<string, JsonElement> ReasoningItemMetadata(string id, bool done = false)
        => new(StringComparer.Ordinal)
        {
            [ResponsesKeys.ReasoningItemTransformerMetadataKey] =
                ResponsesStreamMetadata.EncodeReasoningItemMetadata(id, done),
        };

    public static Dictionary<string, JsonElement> WebSearchCallsMetadata(List<ResponsesItem> calls)
        => new(StringComparer.Ordinal)
        {
            [ResponsesKeys.WebSearchCallsTransformerMetadataKey] =
                JsonSerializer.SerializeToElement(calls, LlmJson.Options),
        };

    public static ResponsesStreamEvent ParseEvent(StreamEvent streamEvent)
        => LlmJson.Deserialize<ResponsesStreamEvent>(streamEvent.Data)
            ?? throw new InvalidOperationException("event data did not decode");

    public static List<ResponsesStreamEvent> ParseEvents(IEnumerable<StreamEvent> streamEvents)
        => [.. streamEvents.Select(ParseEvent)];

    public static bool IsDone(LlmResponse response) => ReferenceEquals(response, LlmResponse.DoneResponse);

    public static bool IsDone(StreamEvent streamEvent)
        => streamEvent.Data.AsSpan().SequenceEqual("[DONE]"u8);

    /// <summary>
    /// Compares two Response-model values by marshaling both with the Go-shaped
    /// serializer, so property omissions follow the same rules on both sides.
    /// </summary>
    public static void AssertResponsesStreamEventMatches(
        ResponsesStreamEvent expected, ResponsesStreamEvent actual, int index)
    {
        AssertJsonMatches(
            GoJson.Marshal(expected),
            GoJson.Marshal(actual),
            InboundIgnoreFields,
            $"event {index} ({expected.Type})");
    }

    public static void AssertResponsesResponseMatches(
        ResponsesResponse expected, ResponsesResponse actual, string context)
    {
        AssertJsonMatches(
            GoJson.Marshal(expected),
            GoJson.Marshal(actual),
            ResponseIgnoreFields,
            context);
    }

    /// <summary>Aggregator fixtures are compared with no ignored fields at all.</summary>
    public static void AssertResponsesResponseExact(
        ResponsesResponse expected, ResponsesResponse actual, string context)
    {
        AssertJsonMatches(
            GoJson.Marshal(expected),
            GoJson.Marshal(actual),
            null,
            context);
    }

    /// <summary>Deep JSON comparison with go-cmp-style ignored struct fields.</summary>
    public static void AssertJsonMatches(
        byte[] expected,
        byte[] actual,
        IReadOnlySet<string>? ignoreFields = null,
        string context = "")
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Compare(
            expectedDocument.RootElement,
            actualDocument.RootElement,
            "$",
            ignoreFields ?? new HashSet<string>(StringComparer.Ordinal),
            context);
    }

    public static void AssertJsonEquivalent(string expected, string actual, string context = "")
    {
        AssertJsonMatches(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual),
            new HashSet<string>(StringComparer.Ordinal),
            context);
    }

    private static void Compare(
        JsonElement expected,
        JsonElement actual,
        string path,
        IReadOnlySet<string> ignoreFields,
        string context)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            Assert.Fail($"{context}: {path} kind {expected.ValueKind} != {actual.ValueKind}");
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (ignoreFields.Contains(property.Name))
                    {
                        continue;
                    }

                    if (!actual.TryGetProperty(property.Name, out var actualProperty))
                    {
                        Assert.Fail($"{context}: {path}.{property.Name} missing in actual");
                    }

                    Compare(property.Value, actualProperty, path + "." + property.Name, ignoreFields, context);
                }

                foreach (var property in actual.EnumerateObject())
                {
                    if (ignoreFields.Contains(property.Name))
                    {
                        continue;
                    }

                    if (!expected.TryGetProperty(property.Name, out _))
                    {
                        Assert.Fail($"{context}: {path}.{property.Name} unexpected in actual");
                    }
                }

                return;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                if (expectedItems.Count != actualItems.Count)
                {
                    Assert.Fail(
                        $"{context}: {path} length {expectedItems.Count} != {actualItems.Count}");
                }

                for (var i = 0; i < expectedItems.Count; i++)
                {
                    Compare(expectedItems[i], actualItems[i], $"{path}[{i}]", ignoreFields, context);
                }

                return;

            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"{context}: {path} \"{expected.GetString()}\" != \"{actual.GetString()}\"");
                }

                return;

            case JsonValueKind.Number:
                if (!string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal))
                {
                    Assert.Fail($"{context}: {path} {expected.GetRawText()} != {actual.GetRawText()}");
                }

                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return;

            default:
                Assert.Fail($"{context}: {path} unsupported kind {expected.ValueKind}");
                return;
        }
    }

    // ------------------------------------------------------------------
    // llm.Response comparison (mirrors xtest.Equal's comparers)
    // ------------------------------------------------------------------

    public static void AssertLlmResponseMatches(LlmResponse expected, LlmResponse actual, string context)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Object, actual.Object);
        Assert.Equal(expected.Model, actual.Model);
        Assert.Equal(expected.Created, actual.Created);
        Assert.Equal(expected.PreviousResponseId, actual.PreviousResponseId);
        Assert.Equal(expected.SystemFingerprint, actual.SystemFingerprint);
        Assert.Equal(expected.ServiceTier, actual.ServiceTier);
        AssertMetadataMatches(expected.TransformerMetadata, actual.TransformerMetadata, context + ".transformer_metadata");

        Assert.Equal(expected.Choices.Count, actual.Choices.Count);
        for (var i = 0; i < expected.Choices.Count; i++)
        {
            AssertChoiceMatches(expected.Choices[i], actual.Choices[i], $"{context}.choices[{i}]");
        }

        AssertUsageMatches(expected.Usage, actual.Usage, context + ".usage");
    }

    private static void AssertChoiceMatches(Choice expected, Choice actual, string context)
    {
        Assert.Equal(expected.Index, actual.Index);
        Assert.Equal(expected.FinishReason, actual.FinishReason);
        Assert.Equal(expected.Logprobs is null, actual.Logprobs is null);
        Assert.Equal(expected.TransformerMetadata is null, actual.TransformerMetadata is null);
        AssertMessageMatches(expected.Message, actual.Message, context + ".message");
        AssertMessageMatches(expected.Delta, actual.Delta, context + ".delta");
    }

    private static void AssertMessageMatches(Message? expected, Message? actual, string context)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual!.Id);
        Assert.Equal(expected.Role, actual.Role);
        Assert.Equal(expected.ReasoningContent, actual.ReasoningContent);
        Assert.Equal(expected.ReasoningSignature, actual.ReasoningSignature);
        Assert.Equal(expected.Content?.Content, actual.Content?.Content);

        var expectedParts = expected.Content?.MultipleContent ?? [];
        var actualParts = actual.Content?.MultipleContent ?? [];
        Assert.Equal(expectedParts.Count, actualParts.Count);
        for (var i = 0; i < expectedParts.Count; i++)
        {
            Assert.Equal(expectedParts[i].Type, actualParts[i].Type);
            Assert.Equal(expectedParts[i].Text, actualParts[i].Text);
            Assert.Equal(expectedParts[i].Compact?.Id, actualParts[i].Compact?.Id);
            Assert.Equal(expectedParts[i].Compact?.EncryptedContent, actualParts[i].Compact?.EncryptedContent);
        }

        AssertAnnotationsMatch(expected.Annotations, actual.Annotations, context + ".annotations");

        var expectedCalls = expected.ToolCalls ?? [];
        var actualCalls = actual.ToolCalls ?? [];
        Assert.Equal(expectedCalls.Count, actualCalls.Count);
        for (var i = 0; i < expectedCalls.Count; i++)
        {
            var expectedCall = expectedCalls[i];
            var actualCall = actualCalls[i];
            Assert.Equal(expectedCall.Index, actualCall.Index);
            Assert.Equal(expectedCall.Id, actualCall.Id);
            Assert.Equal(expectedCall.Type, actualCall.Type);
            Assert.Equal(expectedCall.Function.Name, actualCall.Function.Name);
            AssertArgumentsMatch(
                expectedCall.Function.Arguments, actualCall.Function.Arguments, $"{context}.tool_calls[{i}]");

            if (expectedCall.ResponseCustomToolCall is null)
            {
                Assert.Null(actualCall.ResponseCustomToolCall);
            }
            else
            {
                Assert.NotNull(actualCall.ResponseCustomToolCall);
                Assert.Equal(
                    expectedCall.ResponseCustomToolCall.CallId, actualCall.ResponseCustomToolCall!.CallId);
                Assert.Equal(
                    expectedCall.ResponseCustomToolCall.Name, actualCall.ResponseCustomToolCall.Name);
                Assert.Equal(
                    expectedCall.ResponseCustomToolCall.Input, actualCall.ResponseCustomToolCall.Input);
            }
        }
    }

    private static void AssertAnnotationsMatch(
        List<Annotation>? expected, List<Annotation>? actual, string context)
    {
        var expectedItems = expected ?? [];
        var actualItems = actual ?? [];
        Assert.Equal(expectedItems.Count, actualItems.Count);
        for (var i = 0; i < expectedItems.Count; i++)
        {
            Assert.Equal(expectedItems[i].Type, actualItems[i].Type);
            Assert.Equal(expectedItems[i].StartIndex, actualItems[i].StartIndex);
            Assert.Equal(expectedItems[i].EndIndex, actualItems[i].EndIndex);
            Assert.Equal(expectedItems[i].UrlCitation?.Url, actualItems[i].UrlCitation?.Url);
            Assert.Equal(expectedItems[i].UrlCitation?.Title, actualItems[i].UrlCitation?.Title);
        }
    }

    /// <summary>xtest compares tool-call arguments as parsed JSON when possible.</summary>
    private static void AssertArgumentsMatch(string? expected, string? actual, string context)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return;
        }

        if (expected is not null && actual is not null && LooksLikeJson(expected) && LooksLikeJson(actual))
        {
            AssertJsonEquivalent(expected, actual, context + ".arguments");
            return;
        }

        Assert.Equal(expected, actual);
    }

    private static bool LooksLikeJson(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static void AssertMetadataMatches(
        Dictionary<string, JsonElement>? expected, Dictionary<string, JsonElement>? actual, string context)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        var expectedKeys = expected.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToList();
        var actualKeys = actual!.Keys.OrderBy(static k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            expectedKeys.SequenceEqual(actualKeys, StringComparer.Ordinal),
            $"{context}: metadata keys [{string.Join(",", expectedKeys)}] != [{string.Join(",", actualKeys)}]");

        foreach (var (key, value) in expected)
        {
            AssertJsonMatches(
                Encoding.UTF8.GetBytes(value.GetRawText()),
                Encoding.UTF8.GetBytes(actual[key].GetRawText()),
                context: $"{context}.{key}");
        }
    }

    /// <summary>nil detail objects compare equal to the zero struct, as in xtest.</summary>
    public static void AssertUsageMatches(Usage? expected, Usage? actual, string context)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.PromptTokens, actual!.PromptTokens);
        Assert.Equal(expected.CompletionTokens, actual.CompletionTokens);
        Assert.Equal(expected.TotalTokens, actual.TotalTokens);
        Assert.Equal(expected.Cost, actual.Cost);

        var expectedPrompt = expected.PromptTokensDetails;
        var actualPrompt = actual.PromptTokensDetails;
        Assert.Equal(expectedPrompt?.AudioTokens ?? 0, actualPrompt?.AudioTokens ?? 0);
        Assert.Equal(expectedPrompt?.CachedTokens ?? 0, actualPrompt?.CachedTokens ?? 0);
        Assert.Equal(expectedPrompt?.WriteCachedTokens ?? 0, actualPrompt?.WriteCachedTokens ?? 0);
        Assert.Equal(expectedPrompt?.WriteCached5MinTokens ?? 0, actualPrompt?.WriteCached5MinTokens ?? 0);
        Assert.Equal(expectedPrompt?.WriteCached1HourTokens ?? 0, actualPrompt?.WriteCached1HourTokens ?? 0);
        Assert.Equal(expectedPrompt?.ImageTokens ?? 0, actualPrompt?.ImageTokens ?? 0);
        Assert.Equal(expectedPrompt?.TextTokens ?? 0, actualPrompt?.TextTokens ?? 0);

        var expectedCompletion = expected.CompletionTokensDetails;
        var actualCompletion = actual.CompletionTokensDetails;
        Assert.Equal(expectedCompletion?.AudioTokens ?? 0, actualCompletion?.AudioTokens ?? 0);
        Assert.Equal(expectedCompletion?.ReasoningTokens ?? 0, actualCompletion?.ReasoningTokens ?? 0);
        Assert.Equal(
            expectedCompletion?.AcceptedPredictionTokens ?? 0,
            actualCompletion?.AcceptedPredictionTokens ?? 0);
        Assert.Equal(
            expectedCompletion?.RejectedPredictionTokens ?? 0,
            actualCompletion?.RejectedPredictionTokens ?? 0);
    }
}

/// <summary>
/// Port of responsesErrorAfterStream: yields fixed events and reports a
/// terminal error once every event has been consumed. <see cref="Index"/>
/// records how many events the transformer actually read.
/// </summary>
internal sealed class ResponsesEventErrorAfterStream : IStream<StreamEvent>
{
    private readonly List<StreamEvent> _items;

    public ResponsesEventErrorAfterStream(IEnumerable<StreamEvent> items, Exception terminalError)
    {
        _items = [.. items];
        TerminalError = terminalError;
    }

    public Exception TerminalError { get; }

    public int Index { get; private set; }

    public StreamEvent? Current { get; private set; }

    public Exception? Err => Index >= _items.Count ? TerminalError : null;

    public bool Next()
    {
        if (Index >= _items.Count)
        {
            return false;
        }

        Current = _items[Index];
        Index++;
        return true;
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}
