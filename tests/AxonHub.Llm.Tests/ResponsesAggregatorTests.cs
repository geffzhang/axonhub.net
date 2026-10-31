using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the shared Responses stream aggregator
/// (llm/transformer/openai/responses/aggregator.go and aggregator_test.go).
/// </summary>
public sealed class ResponsesAggregatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EmptyChunksAreRejected(int emptyKind)
    {
        List<AxonHub.Llm.Http.StreamEvent> chunks = emptyKind == 0 ? [] : null!;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ResponsesAggregate.AggregateStreamChunks(chunks, out _));
        Assert.Contains("empty stream chunks", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledFallbackUsesCanonicalStatus()
    {
        var body = Aggregate(
            """{"type":"response.cancelled","response":{"id":"resp_canceled","object":"response","created_at":1700000000,"model":"gpt-5","output":[]}}""",
            out _);

        Assert.Equal("canceled", body.Status);
    }

    public static TheoryData<string, string, string> CompletedSnapshots => new()
    {
        { "no status", "", "completed" },
        { "empty status", @",""status"":""""", "completed" },
        { "stale in-progress status", @",""status"":""in_progress""", "completed" },
        { "completed", @",""status"":""completed""", "completed" },
        {
            "incomplete",
            @",""status"":""incomplete"",""incomplete_details"":{""reason"":""max_output_tokens""}",
            "incomplete"
        },
        {
            "content filter",
            @",""status"":""incomplete"",""incomplete_details"":{""reason"":""content_filter""}",
            "incomplete"
        },
        {
            "failed",
            @",""status"":""failed"",""error"":{""type"":""server_error"",""code"":""provider_error"",""message"":""provider failed""}",
            "failed"
        },
        { "canceled", @",""status"":""canceled""", "canceled" },
        { "canceled again", @",""status"":""canceled""", "canceled" },
    };

    [Theory]
    [MemberData(nameof(CompletedSnapshots))]
    public void CompletedEventPreservesResponseSnapshot(string name, string fields, string expectedStatus)
    {
        _ = name;
        var snapshot =
            """{"id":"resp_terminal","model":"gpt-5","created_at":1700000001,"previous_response_id":"resp_previous","output":[],"usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12}"""
            + fields + "}";
        var body = Aggregate(
            """{"type":"response.completed","response":""" + snapshot + "}",
            out var meta);

        var expected = LlmJson.Deserialize<ResponsesResponse>(Encoding.UTF8.GetBytes(snapshot))!;

        Assert.Equal(expectedStatus, body.Status);
        Assert.Equal(expected.Id, body.Id);
        Assert.Equal(expected.Model, body.Model);
        Assert.Equal(expected.CreatedAt, body.CreatedAt);
        Assert.Equal(expected.PreviousResponseId, body.PreviousResponseId);
        Assert.Equal(expected.Error?.Type, body.Error?.Type);
        Assert.Equal(expected.Error?.Code, body.Error?.Code);
        Assert.Equal(expected.Error?.Message, body.Error?.Message);
        Assert.Equal(expected.IncompleteDetails?.Reason, body.IncompleteDetails?.Reason);
        Assert.Equal(expected.Usage?.InputTokens, body.Usage?.InputTokens);
        Assert.Equal(expected.Usage?.OutputTokens, body.Usage?.OutputTokens);
        Assert.Equal(expected.Usage?.TotalTokens, body.Usage?.TotalTokens);

        Assert.Equal(expected.Id, meta.Id);
        ResponsesStreamTestSupport.AssertUsageMatches(expected.Usage?.ToUsage(), meta.Usage, "meta usage");
    }

    [Fact]
    public void CancelledSnapshotPreservesStatus()
    {
        var body = Aggregate(
            """{"type":"response.created","response":{"id":"resp_canceled","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}""",
            """{"type":"response.cancelled","response":{"id":"resp_canceled","object":"response","created_at":1700000001,"model":"gpt-5-codex","status":"canceled","output":[]}}""",
            out _);

        Assert.Equal("resp_canceled", body.Id);
        Assert.Equal("gpt-5-codex", body.Model);
        Assert.Equal(1700000001L, body.CreatedAt);
        Assert.Equal("canceled", body.Status);
    }

    public static TheoryData<string, string, string> FixtureCases => new()
    {
        {
            "tool stream with text and multiple function calls",
            "tool-2.stream.jsonl",
            "tool-2.response.json"
        },
        {
            "custom tool call stream",
            "custom_tool.stream.jsonl",
            "custom_tool.stream.response.json"
        },
    };

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void AggregateMatchesFixtureResponse(string name, string streamFile, string expectedFile)
    {
        _ = name;
        var chunks = ResponsesStreamTestSupport.LoadStreamChunks(streamFile);
        Assert.NotEmpty(chunks);

        var bytes = ResponsesAggregate.AggregateStreamChunks(chunks, out var meta);
        var actual = LlmJson.Deserialize<ResponsesResponse>(bytes)!;
        var expected = ResponsesStreamTestSupport.LoadFixture<ResponsesResponse>(expectedFile);

        ResponsesStreamTestSupport.AssertResponsesResponseExact(expected, actual, expectedFile);
        Assert.False(string.IsNullOrEmpty(meta.Id));
        Assert.NotNull(meta.Usage);
    }

    [Fact]
    public void AggregatePreservesFixtureResponseMetaId()
    {
        var toolBytes = ResponsesAggregate.AggregateStreamChunks(
            ResponsesStreamTestSupport.LoadStreamChunks("tool-2.stream.jsonl"), out var toolMeta);
        Assert.NotEmpty(toolBytes);
        Assert.Equal(
            "resp_020592949fb9ce090069355e9a54788196911d78a6360a88f2", toolMeta.Id);

        var customBytes = ResponsesAggregate.AggregateStreamChunks(
            ResponsesStreamTestSupport.LoadStreamChunks("custom_tool.stream.jsonl"), out var customMeta);
        Assert.NotEmpty(customBytes);
        Assert.Equal("resp_custom_tool_stream_001", customMeta.Id);
    }

    [Fact]
    public void BasicEventsBuildMessageOutputAndUsage()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_test_123","object":"response","created_at":1700000000,"model":"gpt-4o","status":"in_progress","output":[]}}
            """,
            """
            {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"msg_test_456","type":"message","status":"in_progress","role":"assistant"}}
            """,
            """
            {"type":"response.content_part.added","sequence_number":2,"item_id":"msg_test_456","output_index":0,"content_index":0,"part":{"type":"output_text","text":""}}
            """,
            """
            {"type":"response.output_text.delta","sequence_number":3,"item_id":"msg_test_456","output_index":0,"content_index":0,"delta":"Hello"}
            """,
            """
            {"type":"response.output_text.delta","sequence_number":4,"item_id":"msg_test_456","output_index":0,"content_index":0,"delta":" World!"}
            """,
            """
            {"type":"response.output_text.done","sequence_number":5,"item_id":"msg_test_456","output_index":0,"content_index":0,"text":"Hello World!"}
            """,
            """
            {"type":"response.output_item.done","sequence_number":6,"output_index":0,"item":{"id":"msg_test_456","type":"message","status":"completed","role":"assistant"}}
            """,
            """
            {"type":"response.completed","sequence_number":7,"response":{"id":"resp_test_123","object":"response","created_at":1700000000,"model":"gpt-4o","status":"completed","output":[],"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15}}}
            """,
            out var meta);

        Assert.Equal("response", body.Object);
        Assert.Equal("resp_test_123", body.Id);
        Assert.Equal("gpt-4o", body.Model);
        Assert.Equal("completed", body.Status);

        Assert.NotNull(body.Output);
        Assert.Single(body.Output!);
        var output = body.Output![0];
        Assert.Equal("message", output.Type);
        Assert.Equal("assistant", output.Role);
        var contentItems = output.GetContentItems();
        Assert.NotNull(contentItems);
        Assert.Equal("Hello World!", contentItems![0].Text);

        Assert.NotNull(body.Usage);
        Assert.Equal(10L, body.Usage!.InputTokens);
        Assert.Equal(5L, body.Usage.OutputTokens);
        Assert.Equal(15L, body.Usage.TotalTokens);

        Assert.Equal("resp_test_123", meta.Id);
        Assert.NotNull(meta.Usage);
        Assert.Equal(10L, meta.Usage!.PromptTokens);
        Assert.Equal(5L, meta.Usage.CompletionTokens);
    }

    [Fact]
    public void PreservesAnnotationsFromFinalOutputItem()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_annotations_123","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"in_progress","output":[]}}
            """,
            """
            {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"msg_annotations_456","type":"message","status":"in_progress","role":"assistant"}}
            """,
            """
            {"type":"response.content_part.added","sequence_number":2,"item_id":"msg_annotations_456","output_index":0,"content_index":0,"part":{"type":"output_text","text":""}}
            """,
            """
            {"type":"response.output_text.delta","sequence_number":3,"item_id":"msg_annotations_456","output_index":0,"content_index":0,"delta":"Search result"}
            """,
            """
            {"type":"response.output_text.done","sequence_number":4,"item_id":"msg_annotations_456","output_index":0,"content_index":0,"text":"Search result"}
            """,
            """
            {"type":"response.output_item.done","sequence_number":5,"output_index":0,"item":{"id":"msg_annotations_456","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Search result","annotations":[{"type":"url_citation","start_index":0,"end_index":6,"url_citation":{"url":"https://example.com/result","title":"Example Result"}}]}]}}
            """,
            """
            {"type":"response.completed","sequence_number":6,"response":{"id":"resp_annotations_123","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"completed","output":[]}}
            """,
            out _);

        Assert.NotNull(body.Output);
        Assert.Single(body.Output!);
        var contentItems = body.Output![0].GetContentItems();
        Assert.NotNull(contentItems);
        Assert.Single(contentItems!);
        Assert.Equal("Search result", contentItems![0].Text);

        Assert.NotNull(contentItems[0].Annotations);
        Assert.Single(contentItems[0].Annotations!);
        var annotation = contentItems[0].Annotations![0];
        Assert.Equal("url_citation", annotation.Type);
        Assert.Equal(0L, annotation.StartIndex ?? -1L);
        Assert.Equal(6L, annotation.EndIndex ?? -1L);
        Assert.Equal("https://example.com/result", annotation.UrlCitation?.Url);
        Assert.Equal("Example Result", annotation.UrlCitation?.Title);
    }

    [Fact]
    public void PreservesEarlierAnnotationsWhenFinalItemOmitsThem()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_annotations_preserved_123","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"in_progress","output":[]}}
            """,
            """
            {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"msg_annotations_preserved_456","type":"message","status":"in_progress","role":"assistant"}}
            """,
            """
            {"type":"response.content_part.added","sequence_number":2,"item_id":"msg_annotations_preserved_456","output_index":0,"content_index":0,"part":{"type":"output_text","text":"","annotations":[{"type":"url_citation","start_index":0,"end_index":6,"url_citation":{"url":"https://example.com/earlier","title":"Earlier Result"}}]}}
            """,
            """
            {"type":"response.output_text.delta","sequence_number":3,"item_id":"msg_annotations_preserved_456","output_index":0,"content_index":0,"delta":"Search result"}
            """,
            """
            {"type":"response.output_text.done","sequence_number":4,"item_id":"msg_annotations_preserved_456","output_index":0,"content_index":0,"text":"Search result"}
            """,
            """
            {"type":"response.output_item.done","sequence_number":5,"output_index":0,"item":{"id":"msg_annotations_preserved_456","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Search result"}]}}
            """,
            """
            {"type":"response.completed","sequence_number":6,"response":{"id":"resp_annotations_preserved_123","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"completed","output":[]}}
            """,
            out _);

        Assert.NotNull(body.Output);
        Assert.Single(body.Output!);
        var contentItems = body.Output![0].GetContentItems();
        Assert.NotNull(contentItems);
        Assert.Single(contentItems!);
        Assert.Single(contentItems![0].Annotations!);
        var annotation = contentItems[0].Annotations![0];
        Assert.Equal("url_citation", annotation.Type);
        Assert.Equal(0L, annotation.StartIndex ?? -1L);
        Assert.Equal(6L, annotation.EndIndex ?? -1L);
        Assert.Equal("https://example.com/earlier", annotation.UrlCitation?.Url);
        Assert.Equal("Earlier Result", annotation.UrlCitation?.Title);
    }

    [Fact]
    public void PreservesPreviousResponseId()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_test_prev","object":"response","created_at":1700000000,"model":"gpt-5.4","status":"in_progress","previous_response_id":"resp_prev_123","output":[]}}
            """,
            """
            {"type":"response.completed","sequence_number":1,"response":{"id":"resp_test_prev","object":"response","created_at":1700000000,"model":"gpt-5.4","status":"completed","previous_response_id":"resp_prev_123","output":[]}}
            """,
            out _);

        Assert.Equal("resp_prev_123", body.PreviousResponseId);
    }

    [Fact]
    public void SkipsInvalidJsonAndDoneMarkers()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_test","object":"response","created_at":1700000000,"model":"gpt-4o","status":"in_progress"}}
            """,
            "{invalid json}",
            "[DONE]",
            """
            {"type":"response.completed","sequence_number":1,"response":{"id":"resp_test","status":"completed"}}
            """,
            out _);

        Assert.Equal("completed", body.Status);
        Assert.Equal("resp_test", body.Id);
    }

    [Fact]
    public void ReasoningSummaryMultipleParts()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_test_reasoning_multi_summary","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}
            """,
            """
            {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"rs_test_multi_001","type":"reasoning","status":"in_progress","summary":[]}}
            """,
            """
            {"type":"response.reasoning_summary_part.done","sequence_number":2,"item_id":"rs_test_multi_001","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":"**Analyzing output logic**"}}
            """,
            """
            {"type":"response.reasoning_summary_part.added","sequence_number":3,"item_id":"rs_test_multi_001","output_index":0,"summary_index":1,"part":{"type":"summary_text","text":""}}
            """,
            """
            {"type":"response.output_item.done","sequence_number":4,"output_index":0,"item":{"id":"rs_test_multi_001","type":"reasoning","status":"completed","summary":[]}}
            """,
            """
            {"type":"response.completed","sequence_number":5,"response":{"id":"resp_test_reasoning_multi_summary","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}
            """,
            out _);

        Assert.NotNull(body.Output);
        var outputItem = Assert.Single(body.Output!);
        Assert.Equal("reasoning", outputItem.Type);
        var summary = outputItem.Summary!;
        Assert.Equal(2, summary.Count);
        Assert.Equal("summary_text", summary[0].Type);
        Assert.Equal("**Analyzing output logic**", summary[0].Text);
        Assert.Equal("summary_text", summary[1].Type);
        Assert.Equal("", summary[1].Text);
    }

    [Fact]
    public void ReasoningTextBecomesReasoningContentPart()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_reasoning_text","object":"response","created_at":1700000000,"model":"deepseek-v4-flash","status":"in_progress","output":[]}}
            """,
            """
            {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"rs_text","type":"reasoning","status":"in_progress","summary":[],"content":[]}}
            """,
            """
            {"type":"response.reasoning_text.delta","sequence_number":2,"item_id":"rs_text","output_index":0,"content_index":0,"delta":"first"}
            """,
            """
            {"type":"response.reasoning_text.delta","sequence_number":3,"item_id":"rs_text","output_index":0,"content_index":0,"delta":" second"}
            """,
            """
            {"type":"response.reasoning_text.done","sequence_number":4,"item_id":"rs_text","output_index":0,"content_index":0,"text":"first second"}
            """,
            """
            {"type":"response.output_item.done","sequence_number":5,"output_index":0,"item":{"id":"rs_text","type":"reasoning","status":"completed","summary":[]}}
            """,
            """
            {"type":"response.completed","sequence_number":6,"response":{"id":"resp_reasoning_text","object":"response","created_at":1700000000,"model":"deepseek-v4-flash","status":"completed","output":[]}}
            """,
            out _);

        Assert.NotNull(body.Output);
        var outputItem = Assert.Single(body.Output!);
        Assert.Equal("reasoning", outputItem.Type);
        Assert.NotNull(outputItem.Content);
        var contentItems = outputItem.Content!.Items!;
        Assert.Single(contentItems);
        Assert.Equal("reasoning_text", contentItems[0].Type);
        Assert.Equal("first second", contentItems[0].Text);
    }

    public static TheoryData<string, string> InvalidContentIndexEvents => new()
    {
        {
            "negative delta index",
            """{"type":"response.reasoning_text.delta","sequence_number":2,"item_id":"rs_text","output_index":0,"content_index":-1,"delta":"ignored"}"""
        },
        {
            "negative done index",
            """{"type":"response.reasoning_text.done","sequence_number":2,"item_id":"rs_text","output_index":0,"content_index":-1,"text":"ignored"}"""
        },
        {
            "oversized delta index",
            """{"type":"response.reasoning_text.delta","sequence_number":2,"item_id":"rs_text","output_index":0,"content_index":1024,"delta":"ignored"}"""
        },
        {
            "oversized done index",
            """{"type":"response.reasoning_text.done","sequence_number":2,"item_id":"rs_text","output_index":0,"content_index":1024,"text":"ignored"}"""
        },
    };

    [Theory]
    [MemberData(nameof(InvalidContentIndexEvents))]
    public void ReasoningTextRejectsInvalidContentIndex(string name, string eventJson)
    {
        _ = name;
        var body = Aggregate(
            """{"type":"response.created","sequence_number":0,"response":{"id":"resp_reasoning_text","object":"response","created_at":1700000000,"model":"deepseek-v4-flash","status":"in_progress","output":[]}}""",
            """{"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"rs_text","type":"reasoning","status":"in_progress","summary":[],"content":[]}}""",
            eventJson,
            """{"type":"response.completed","sequence_number":3,"response":{"id":"resp_reasoning_text","object":"response","created_at":1700000000,"model":"deepseek-v4-flash","status":"completed","output":[]}}""",
            out _);

        Assert.NotNull(body.Output);
        Assert.Single(body.Output!);
        Assert.Null(body.Output![0].Content);
    }

    [Fact]
    public void ImageGenerationCallOutputItem()
    {
        var body = Aggregate(
            """
            {"type":"response.created","sequence_number":0,"response":{"id":"resp_img_gen_001","object":"response","created_at":1760000000,"model":"gpt-image-2","status":"in_progress","output":[]}}
            """,
            """
            {"type":"response.output_item.added","sequence_number":1,"output_index":0,"item":{"id":"img_call_001","type":"image_generation_call","status":"in_progress","call_id":"call_abc"}}
            """,
            """
            {"type":"response.output_item.done","sequence_number":2,"output_index":0,"item":{"id":"img_call_001","type":"image_generation_call","status":"completed","call_id":"call_abc","result":"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="}}
            """,
            """
            {"type":"response.completed","sequence_number":3,"response":{"id":"resp_img_gen_001","object":"response","created_at":1760000000,"model":"gpt-image-2","status":"completed","output":[]}}
            """,
            out _);

        Assert.Equal("resp_img_gen_001", body.Id);
        Assert.NotNull(body.Output);
        Assert.Single(body.Output!);
        Assert.Equal("image_generation_call", body.Output![0].Type);
        Assert.Equal("completed", body.Output[0].Status);
        Assert.Equal("call_abc", body.Output[0].CallId);
        Assert.Equal(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==",
            body.Output[0].Result);
    }

    private static ResponsesResponse Aggregate(
        string event1, out ResponseMeta meta)
        => Aggregate([event1], out meta);

    private static ResponsesResponse Aggregate(
        string event1, string event2, out ResponseMeta meta)
        => Aggregate([event1, event2], out meta);

    private static ResponsesResponse Aggregate(
        string event1, string event2, string event3, out ResponseMeta meta)
        => Aggregate([event1, event2, event3], out meta);

    private static ResponsesResponse Aggregate(
        string event1, string event2, string event3, string event4, out ResponseMeta meta)
        => Aggregate([event1, event2, event3, event4], out meta);

    private static ResponsesResponse Aggregate(
        string event1,
        string event2,
        string event3,
        string event4,
        string event5,
        out ResponseMeta meta)
        => Aggregate([event1, event2, event3, event4, event5], out meta);

    private static ResponsesResponse Aggregate(
        string event1,
        string event2,
        string event3,
        string event4,
        string event5,
        string event6,
        out ResponseMeta meta)
        => Aggregate([event1, event2, event3, event4, event5, event6], out meta);

    private static ResponsesResponse Aggregate(
        string event1,
        string event2,
        string event3,
        string event4,
        string event5,
        string event6,
        string event7,
        out ResponseMeta meta)
        => Aggregate([event1, event2, event3, event4, event5, event6, event7], out meta);

    private static ResponsesResponse Aggregate(
        string event1,
        string event2,
        string event3,
        string event4,
        string event5,
        string event6,
        string event7,
        string event8,
        out ResponseMeta meta)
        => Aggregate([event1, event2, event3, event4, event5, event6, event7, event8], out meta);

    private static ResponsesResponse Aggregate(
        string[] eventJsons, out ResponseMeta meta)
    {
        var chunks = eventJsons
            .Select(json => ResponsesStreamTestSupport.Event(json))
            .ToList();
        var bytes = ResponsesAggregate.AggregateStreamChunks(chunks, out meta);
        return LlmJson.Deserialize<ResponsesResponse>(bytes)!;
    }
}
