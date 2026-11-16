using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Responses API inbound stream transformer
/// (llm/transformer/openai/responses/inbound_stream.go and its tests).
/// </summary>
public sealed class ResponsesInboundStreamTests
{
    [Theory]
    [InlineData("EOF")]
    [InlineData("unexpected EOF")]
    [InlineData("wrapped unexpected EOF")]
    public void ClassifyStreamErrorTreatsEndOfStreamAsUpstreamEof(string name)
    {
        _ = name;
        var err = new EndOfStreamException("eof");

        var (code, message) = ResponsesInboundStream.ClassifyStreamError(err);

        Assert.Equal("upstream_eof", code);
        Assert.Equal("upstream connection closed unexpectedly", message);
    }

    [Fact]
    public void ClassifyStreamErrorUnwrapsWrappedEndOfStream()
    {
        var err = new InvalidOperationException(
            "read body: unexpected EOF", new EndOfStreamException("unexpected EOF"));

        var (code, message) = ResponsesInboundStream.ClassifyStreamError(err);

        Assert.Equal("upstream_eof", code);
        Assert.Equal("upstream connection closed unexpectedly", message);
    }

    public static TheoryData<string, string, string, string> FixtureCases => new()
    {
        {
            "stream transformation with text and multiple tool calls",
            "llm-tool-2.stream.jsonl",
            "tool-2.stream.jsonl",
            "tool-2.response.json"
        },
        {
            "stream transformation with custom tool call",
            "llm-custom_tool.stream.jsonl",
            "custom_tool.stream.jsonl",
            "custom_tool.stream.response.json"
        },
        {
            "stream transformation with encrypted reasoning only (no summary items)",
            "llm-encrypted_only.stream.jsonl",
            "encrypted_only.stream.jsonl",
            "encrypted_only.response.json"
        },
    };

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void StreamTransformationMatchesFixtures(
        string name, string inputStreamFile, string expectedStreamFile, string expectedResponseFile)
    {
        _ = name;
        var llmResponses = ResponsesStreamTestSupport.LoadLlmResponses(inputStreamFile);
        var expectedEvents = ResponsesStreamTestSupport.ParseEvents(
            ResponsesStreamTestSupport.LoadStreamChunks(expectedStreamFile));

        var (actualEvents, err) = Run(llmResponses);

        Assert.Null(err);
        Assert.Equal(expectedEvents.Count, actualEvents.Count);
        for (var i = 0; i < expectedEvents.Count; i++)
        {
            ResponsesStreamTestSupport.AssertResponsesStreamEventMatches(expectedEvents[i], actualEvents[i], i);
        }

        var lastEvent = Assert.Single(actualEvents.TakeLast(1));
        Assert.Equal("response.completed", lastEvent.Type);
        Assert.NotNull(lastEvent.Response);

        var expectedResponse = ResponsesStreamTestSupport.LoadFixture<ResponsesResponse>(expectedResponseFile);
        ResponsesStreamTestSupport.AssertResponsesResponseMatches(
            expectedResponse, lastEvent.Response!, expectedResponseFile);
    }

    [Fact]
    public void KeepsResponsesReasoningItemsSeparate()
    {
        var (events, err) = Run(
        [
            Chunk("resp_reasoning_multi", delta: new Message { Role = "assistant" }),
            Chunk(
                "resp_reasoning_multi",
                delta: new Message { Id = "rs_1", ReasoningSignature = "gAAAA_done_1" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_1", done: true)),
            Chunk(
                "resp_reasoning_multi",
                delta: new Message { Id = "rs_2", ReasoningSignature = "gAAAA_done_2" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_2", done: true)),
            Chunk("resp_reasoning_multi", delta: new Message(), finishReason: "stop"),
            new LlmResponse
            {
                Id = "resp_reasoning_multi",
                Created = 1700000000,
                Model = "gpt-5",
                Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 },
            },
        ]);

        Assert.Null(err);
        var doneItems = events
            .Where(static e => e.Type == "response.output_item.done" && e.Item?.Type == "reasoning")
            .Select(static e => e.Item!)
            .ToList();

        Assert.Equal(2, doneItems.Count);
        Assert.Equal("rs_1", doneItems[0].Id);
        Assert.Equal("gAAAA_done_1", doneItems[0].EncryptedContent);
        Assert.Equal("rs_2", doneItems[1].Id);
        Assert.Equal("gAAAA_done_2", doneItems[1].EncryptedContent);

        var lastEvent = events[^1];
        Assert.Equal("response.completed", lastEvent.Type);
        Assert.NotNull(lastEvent.Response?.Output);
        Assert.Equal(2, lastEvent.Response!.Output!.Count);
        Assert.Equal("rs_1", lastEvent.Response.Output[0].Id);
        Assert.Equal("gAAAA_done_1", lastEvent.Response.Output[0].EncryptedContent);
        Assert.Equal("rs_2", lastEvent.Response.Output[1].Id);
        Assert.Equal("gAAAA_done_2", lastEvent.Response.Output[1].EncryptedContent);
    }

    [Fact]
    public void ReplacesItemScopedProvisionalSignature()
    {
        var (events, err) = Run(
        [
            Chunk(
                delta: new Message { Id = "rs_1", ReasoningSignature = "gAAAA_PROVISIONAL_BLOB" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_1")),
            Chunk(
                delta: new Message { Id = "rs_1", ReasoningSignature = "gAAAA_FINAL_BLOB" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_1", done: true)),
            Chunk(delta: new Message(), finishReason: "stop"),
            new LlmResponse { Usage = new Usage() },
        ]);

        Assert.Null(err);
        var doneItems = events
            .Where(static e => e.Type == "response.output_item.done" && e.Item?.Type == "reasoning")
            .Select(static e => e.Item!)
            .ToList();

        var doneItem = Assert.Single(doneItems);
        Assert.Equal("rs_1", doneItem.Id);
        Assert.Equal("gAAAA_FINAL_BLOB", doneItem.EncryptedContent);
        Assert.NotEqual("gAAAA_PROVISIONAL_BLOBgAAAA_FINAL_BLOB", doneItem.EncryptedContent);
    }

    [Fact]
    public void UsesItemMetadataForSummaryDeltas()
    {
        var (events, err) = Run(
        [
            Chunk(
                delta: new Message { ReasoningContent = "first" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_first")),
            Chunk(
                delta: new Message { ReasoningContent = "second" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_second")),
            Chunk(
                delta: new Message { Id = "rs_first", ReasoningSignature = "gAAAA_FIRST_BLOB" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_first", done: true)),
            Chunk(
                delta: new Message { Id = "rs_second", ReasoningSignature = "gAAAA_SECOND_BLOB" },
                metadata: ResponsesStreamTestSupport.ReasoningItemMetadata("rs_second", done: true)),
            Chunk(delta: new Message(), finishReason: "stop"),
            new LlmResponse { Usage = new Usage() },
        ]);

        Assert.Null(err);
        var doneItems = events
            .Where(static e => e.Type == "response.output_item.done" && e.Item?.Type == "reasoning")
            .Select(static e => e.Item!)
            .ToList();

        Assert.Equal(2, doneItems.Count);
        Assert.Equal("rs_first", doneItems[0].Id);
        Assert.Equal("first", doneItems[0].Summary![0].Text);
        Assert.Equal("gAAAA_FIRST_BLOB", doneItems[0].EncryptedContent);
        Assert.Equal("rs_second", doneItems[1].Id);
        Assert.Equal("second", doneItems[1].Summary![0].Text);
        Assert.Equal("gAAAA_SECOND_BLOB", doneItems[1].EncryptedContent);
    }

    [Fact]
    public void PreservesWebSearchCallsFromChunkMetadata()
    {
        var webSearchCall = new ResponsesItem
        {
            Id = "ws_456",
            Type = "web_search_call",
            Status = "completed",
            Action = ResponsesItemAction.NewWebSearchAction(new ResponsesWebSearchAction
            {
                Type = "search",
                Query = "latest ai news",
                Sources =
                [
                    new ResponsesWebSearchSource
                    {
                        Type = "url",
                        Url = "https://example.com/source",
                        Title = "Example Source",
                    },
                ],
            }),
        };

        var (events, err) = Run(
        [
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_stream_web_search_no_annotations",
                Created = 1700000000,
                Model = "gpt-4o-search-preview",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message
                        {
                            Content = MessageContent.FromText("Search result without inline citations"),
                        },
                    },
                ],
            },
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_stream_web_search_no_annotations",
                Created = 1700000000,
                Model = "gpt-4o-search-preview",
                TransformerMetadata = ResponsesStreamTestSupport.WebSearchCallsMetadata([webSearchCall]),
                Choices =
                [
                    new Choice { Index = 0, FinishReason = "stop" },
                ],
            },
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_stream_web_search_no_annotations",
                Created = 1700000000,
                Model = "gpt-4o-search-preview",
                Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 },
            },
        ]);

        Assert.Null(err);
        Assert.NotEmpty(events);
        var lastEvent = events[^1];
        Assert.Equal("response.completed", lastEvent.Type);
        Assert.NotNull(lastEvent.Response?.Output);
        Assert.Equal(2, lastEvent.Response!.Output!.Count);
        Assert.Equal("web_search_call", lastEvent.Response.Output[0].Type);
        Assert.Equal("ws_456", lastEvent.Response.Output[0].Id);
        Assert.Equal("latest ai news", lastEvent.Response.Output[0].Action?.WebSearch?.Query);
        Assert.Equal("message", lastEvent.Response.Output[1].Type);
        Assert.Equal(
            "Search result without inline citations",
            lastEvent.Response.Output[1].GetContentItems()![0].Text);
    }

    [Fact]
    public void EmitsErrorEventBeforeResponseStarts()
    {
        var source = new LlmErrorAfterStream
        {
            TerminalError = new InvalidOperationException("upstream boom"),
        };

        var (events, err) = RunStream(source);

        Assert.Null(err);
        var errorEvent = Assert.Single(events);
        Assert.Equal("error", errorEvent.Type);
        Assert.Equal("stream_error", errorEvent.Code);
        Assert.Equal("upstream boom", errorEvent.Message);
    }

    [Fact]
    public void EmitsResponseFailedAfterResponseStarts()
    {
        var source = new LlmErrorAfterStream
        {
            Items =
            [
                new LlmResponse { Id = "resp_123", Model = "gpt-test", Created = 123 },
            ],
            TerminalError = new InvalidOperationException("upstream boom"),
        };

        var (events, err) = RunStream(source);

        Assert.Null(err);
        Assert.Equal(3, events.Count);
        Assert.Equal("response.created", events[0].Type);
        Assert.Equal("response.in_progress", events[1].Type);
        Assert.Equal("response.failed", events[2].Type);
        Assert.Equal("failed", events[2].Response?.Status);
        Assert.Equal("stream_error", events[2].Response?.Error?.Code);
        Assert.Equal("upstream boom", events[2].Response?.Error?.Message);
    }

    [Fact]
    public void DoesNotEmitFailureAfterCompleted()
    {
        var source = new LlmErrorAfterStream
        {
            Items =
            [
                Chunk("resp_completed_before_error", delta: new Message { Role = "assistant" }),
                Chunk("resp_completed_before_error", delta: new Message(), finishReason: "stop"),
                new LlmResponse
                {
                    Object = "chat.completion.chunk",
                    Id = "resp_completed_before_error",
                    Created = 1700000000,
                    Model = "gpt-5",
                    Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 },
                },
            ],
            TerminalError = new EndOfStreamException("read body: unexpected EOF"),
        };

        var (events, err) = RunStream(source);

        Assert.Null(err);
        var types = events.Select(static e => e.Type).ToList();
        Assert.Contains("response.completed", types);
        Assert.DoesNotContain("response.failed", types);
        Assert.DoesNotContain("error", types);
    }

    [Fact]
    public void UsesUsageFromFinalUsageOnlyChunk()
    {
        var (events, err) = Run(
        [
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_modelscope_usage",
                Created = 1700000000,
                Model = "deepseek-v4.1-flash",
                Choices = [new Choice { Index = 0, Delta = new Message { Role = "assistant" } }],
                Usage = new Usage(),
            },
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_modelscope_usage",
                Created = 1700000000,
                Model = "deepseek-v4.1-flash",
                Choices = [new Choice { Index = 0, Delta = new Message(), FinishReason = "stop" }],
                Usage = new Usage(),
            },
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_modelscope_usage",
                Created = 1700000000,
                Model = "deepseek-v4.1-flash",
                Usage = new Usage
                {
                    PromptTokens = 338678,
                    CompletionTokens = 5795,
                    TotalTokens = 344473,
                    PromptTokensDetails = new PromptTokensDetails { CachedTokens = 100 },
                },
            },
        ]);

        Assert.Null(err);
        var terminal = events
            .Where(static e => e.Type == "response.completed" && e.Response is not null)
            .Select(static e => e.Response!)
            .LastOrDefault();

        Assert.NotNull(terminal);
        Assert.NotNull(terminal!.Usage);
        Assert.Equal(338678L, terminal.Usage!.InputTokens);
        Assert.Equal(100L, terminal.Usage.InputTokensDetails.CachedTokens);
        Assert.Equal(5795L, terminal.Usage.OutputTokens);
    }

    public static TheoryData<string, string, string, string?> FinishReasonCases => new()
    {
        { "length", "incomplete", "response.incomplete", "max_output_tokens" },
        { "content_filter", "incomplete", "response.incomplete", "content_filter" },
        { "error", "failed", "response.failed", null },
        { "cancelled", "cancelled", "response.completed", null },
        { "canceled", "cancelled", "response.completed", null },
        { "bogus", "completed", "response.completed", null },
        { "stop", "completed", "response.completed", null },
        { "tool_calls", "completed", "response.completed", null },
    };

    [Theory]
    [MemberData(nameof(FinishReasonCases))]
    public void MapsFinishReasonToTerminalEvent(
        string finishReason, string expectedStatus, string expectedType, string? expectedIncompleteReason)
    {
        var (events, err) = Run(
        [
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_finish_reason_map",
                Created = 1700000000,
                Model = "gpt-5",
                Choices =
                [
                    new Choice
                    {
                        Index = 0,
                        Delta = new Message { Role = "assistant" },
                        FinishReason = finishReason,
                    },
                ],
            },
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_finish_reason_map",
                Created = 1700000000,
                Model = "gpt-5",
                Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 },
            },
        ]);

        Assert.Null(err);
        var terminal = events.Where(static e => e.Response is not null).LastOrDefault();
        Assert.NotNull(terminal);
        Assert.Equal(expectedType, terminal!.Type);
        Assert.Equal(expectedStatus, terminal.Response!.Status);
        Assert.Equal(expectedIncompleteReason, terminal.Response.IncompleteDetails?.Reason);
    }

    [Fact]
    public void UsageBeforeFinishReasonKeepsMappedStatus()
    {
        var (events, err) = Run(
        [
            Chunk("resp_usage_first", delta: new Message { Role = "assistant" }),
            new LlmResponse
            {
                Object = "chat.completion.chunk",
                Id = "resp_usage_first",
                Created = 1700000000,
                Model = "gpt-5",
                Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 },
            },
            Chunk("resp_usage_first", delta: new Message(), finishReason: "length"),
        ]);

        Assert.Null(err);
        var completed = events
            .Where(static e => e.Type == "response.incomplete" && e.Response is not null)
            .Select(static e => e.Response!)
            .LastOrDefault();

        Assert.NotNull(completed);
        Assert.Equal("incomplete", completed!.Status);
        Assert.Equal("max_output_tokens", completed.IncompleteDetails?.Reason);
    }

    private static LlmResponse Chunk(
        string id = "",
        Message? delta = null,
        string? finishReason = null,
        Dictionary<string, System.Text.Json.JsonElement>? metadata = null)
        => new()
        {
            Object = "chat.completion.chunk",
            Id = id,
            Created = id.Length > 0 ? 1700000000 : 0,
            Model = id.Length > 0 ? "gpt-5" : string.Empty,
            TransformerMetadata = metadata,
            Choices =
            [
                new Choice { Index = 0, Delta = delta, FinishReason = finishReason },
            ],
        };

    private static (List<ResponsesStreamEvent> Events, Exception? Err) Run(IEnumerable<LlmResponse> items)
        => RunStream(new SliceStream<LlmResponse>(items));

    private static (List<ResponsesStreamEvent> Events, Exception? Err) RunStream(IStream<LlmResponse> source)
    {
        using var stream = new ResponsesInboundTransformer().TransformStream(source);
        var rawEvents = GoStreams.All(stream, out var err);
        return (ResponsesStreamTestSupport.ParseEvents(rawEvents), err);
    }
}
