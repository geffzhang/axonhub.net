using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Responses API outbound stream transformer
/// (llm/transformer/openai/responses/outbound_stream.go and its tests).
/// </summary>
public sealed class ResponsesOutboundStreamTests
{
    public static TheoryData<string, string, string, string> FixtureCases => new()
    {
        {
            "stream transformation with text and multiple tool calls",
            "tool-2.stream.jsonl",
            "llm-tool-2.stream.jsonl",
            "llm-tool-2.response.json"
        },
        {
            "stream transformation with encrypted reasoning",
            "encrypted_content.stream.jsonl",
            "llm-encrypted_content.stream.jsonl",
            "llm-encrypted_content.response.json"
        },
        {
            "stream transformation with custom tool call",
            "custom_tool.stream.jsonl",
            "llm-custom_tool.stream.jsonl",
            "llm-custom_tool.stream.response.json"
        },
    };

    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void StreamTransformationMatchesExpectedLlmChunks(
        string name, string inputStreamFile, string expectedStreamFile, string expectedResponseFile)
    {
        _ = name;
        var expected = ResponsesStreamTestSupport.LoadLlmResponses(expectedStreamFile);
        var (actual, err) = RunEvents(ResponsesStreamTestSupport.LoadStreamChunks(inputStreamFile));

        Assert.Null(err);
        Assert.NotEmpty(actual);
        Assert.True(ResponsesStreamTestSupport.IsDone(actual[^1]), "last event should be DONE");
        Assert.All(
            actual.Where(static r => !ResponsesStreamTestSupport.IsDone(r)),
            static r => Assert.True(
                r.Object is "chat.completion" or "chat.completion.chunk",
                $"unexpected object {r.Object}"));

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count - 1; i++)
        {
            ResponsesStreamTestSupport.AssertLlmResponseMatches(
                expected[i], actual[i], $"{inputStreamFile} event {i}");
        }

        var last = actual.Last(static r => !ResponsesStreamTestSupport.IsDone(r));
        var expectedFinal = ResponsesStreamTestSupport.LoadFixture<LlmResponse>(expectedResponseFile);
        Assert.Equal(expectedFinal.Model, last.Model);
        Assert.Equal(expectedFinal.Id, last.Id);
    }

    [Fact]
    public void PreservesResponseMetadata()
    {
        const string metadataJson =
            """{"type":"response.metadata","headers":{"x-codex-turn-state":"ts-1"}}""";
        const string createdJson =
            """{"type":"response.created","response":{"id":"resp-1","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}""";
        const string completedJson =
            """{"type":"response.completed","response":{"id":"resp-1","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}""";

        var (responses, err) = Run(
            new StreamEvent
            {
                Type = "response.metadata",
                Data = Encoding.UTF8.GetBytes(metadataJson),
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["X-Codex-Turn-State"] = ["ts-1"],
                },
            },
            ResponsesStreamTestSupport.Event(createdJson),
            ResponsesStreamTestSupport.Event(completedJson));

        Assert.Null(err);
        var metadataResponse = responses.FirstOrDefault(
            static r => r.TransformerMetadata?.ContainsKey(ResponsesKeys.ResponseMetadataTransformerMetadataKey) == true);
        Assert.NotNull(metadataResponse);

        using var downstream = new ResponsesInboundTransformer()
            .TransformStream(new SliceStream<LlmResponse>(responses));
        var clientEvents = GoStreams.All(downstream, out var downstreamErr);
        Assert.Null(downstreamErr);

        var metadataEvent = clientEvents.FirstOrDefault(static e => e.Type == "response.metadata");
        Assert.NotNull(metadataEvent);
        ResponsesStreamTestSupport.AssertJsonEquivalent(
            metadataJson, Encoding.UTF8.GetString(metadataEvent!.Data));
        Assert.NotNull(metadataEvent.Headers);
        Assert.Equal("ts-1", metadataEvent.Headers!["X-Codex-Turn-State"][0]);
    }

    [Fact]
    public void IncompleteResponseDoesNotEmitDone()
    {
        var cases = new (string Name, StreamEvent[] Events)[]
        {
            ("empty upstream stream", []),
            (
                "with response ID",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.created","response":{"id":"resp_incomplete","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
                ]
            ),
            (
                "with empty response ID",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.created","response":{"id":"","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
                ]
            ),
            (
                "text delta without response ID",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_text.delta","item_id":"msg_partial","output_index":0,"content_index":0,"delta":"partial"}"""),
                ]
            ),
            (
                "reasoning delta without response ID",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.reasoning_summary_text.delta","item_id":"rs_partial","output_index":0,"summary_index":0,"delta":"partial"}"""),
                ]
            ),
            (
                "function call without response ID",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_partial","type":"function_call","call_id":"call_partial","name":"write_file","arguments":""}}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.function_call_arguments.delta","item_id":"fc_partial","output_index":0,"delta":"{\"path\":\"/tmp/x\"}"}"""),
                ]
            ),
        };

        foreach (var (name, events) in cases)
        {
            var (responses, err) = RunEvents(events);

            Assert.True(
                GoErrors.Is(err, LlmErrors.StreamIncomplete),
                $"{name}: expected stream incomplete, got {err?.Message}");
            Assert.DoesNotContain(responses, ResponsesStreamTestSupport.IsDone);
        }
    }

    [Fact]
    public void ProviderDoneRequiresSemanticTerminal()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_done","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event("[DONE]"));

        Assert.True(GoErrors.Is(err, LlmErrors.StreamIncomplete));
        Assert.Equal(0, responses.Count(ResponsesStreamTestSupport.IsDone));
    }

    [Fact]
    public void ProviderDoneBeforeSemanticTerminalPreservesLateSourceError()
    {
        var sourceErr = new InvalidOperationException("late source failure after bare provider done");
        var source = new ResponsesEventErrorAfterStream(
        [
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_done","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event("[DONE]"),
        ],
        sourceErr);

        var (responses, err) = RunStream(source);

        Assert.Same(sourceErr, err);
        Assert.Equal(0, responses.Count(ResponsesStreamTestSupport.IsDone));
    }

    [Fact]
    public void ProviderDoneAfterSemanticTerminal()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_done","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
            ResponsesStreamTestSupport.Event("[DONE]"));

        Assert.Null(err);
        Assert.Equal(1, responses.Count(ResponsesStreamTestSupport.IsDone));
    }

    [Fact]
    public void DuplicateProviderDoneEmitsOnce()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_done","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
            ResponsesStreamTestSupport.Event("[DONE]"),
            ResponsesStreamTestSupport.Event("[DONE]"));

        Assert.Null(err);
        Assert.Equal(1, responses.Count(ResponsesStreamTestSupport.IsDone));
    }

    [Fact]
    public void SemanticTerminalWinsOverLateSourceError()
    {
        var sourceErr = new InvalidOperationException("late source failure");
        var source = new ResponsesEventErrorAfterStream(
        [
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_done","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
            ResponsesStreamTestSupport.Event("[DONE]"),
            ResponsesStreamTestSupport.Event("[DONE]"),
        ],
        sourceErr);

        var (responses, err) = RunStream(source);

        Assert.Null(err);
        Assert.Equal(1, responses.Count(ResponsesStreamTestSupport.IsDone));
        Assert.Equal(1, source.Index);
    }

    [Fact]
    public void DuplicateSemanticTerminalEmitsOnce()
    {
        const string completed =
            """{"type":"response.completed","response":{"id":"resp_done","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}""";
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(completed),
            ResponsesStreamTestSupport.Event(completed));

        Assert.Null(err);
        Assert.Equal(1, responses.Count(ResponsesStreamTestSupport.IsDone));
        Assert.Equal(
            1,
            responses.Count(static r => r.Choices.Count > 0 && r.Choices[0].FinishReason is not null));
    }

    [Fact]
    public void SemanticTerminalSynthesizesOneDone()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_complete","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));

        Assert.Null(err);
        Assert.Equal(1, responses.Count(ResponsesStreamTestSupport.IsDone));
    }

    [Fact]
    public void ErrorEventSurfacesAsStreamError()
    {
        var (_, err) = RunEvents(
            ResponsesStreamTestSupport.LoadStreamChunks("error.response.stream.jsonl"));

        Assert.NotNull(err);
        var responseErr = Assert.IsType<ResponseError>(err);
        Assert.Equal("Something went wrong", responseErr.Detail.Message);
        Assert.Equal("ERR_SOMETHING", responseErr.Detail.Code);
    }

    [Fact]
    public void UsesFinalEncryptedContentPerReasoningItem()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_reasoning_multi","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[],"encrypted_content":"gAAAA_added_1"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[],"encrypted_content":"gAAAA_done_1"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":1,"item":{"id":"rs_2","type":"reasoning","summary":[],"encrypted_content":"gAAAA_added_2"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":1,"item":{"id":"rs_2","type":"reasoning","summary":[],"encrypted_content":"gAAAA_done_2"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_reasoning_multi","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));

        Assert.Null(err);
        var signatures = new List<string>();
        var sourceIds = new List<string>();
        foreach (var response in responses)
        {
            if (ResponsesStreamTestSupport.IsDone(response)
                || response.Choices.Count == 0
                || response.Choices[0].Delta?.ReasoningSignature is null)
            {
                continue;
            }

            signatures.Add(response.Choices[0].Delta!.ReasoningSignature!);
            Assert.True(
                ResponsesStreamMetadata.TryGetReasoningItemMetadata(
                    response.TransformerMetadata, out var metadata),
                "reasoning metadata missing");
            Assert.True(metadata.Done, "reasoning metadata should be marked done");
            sourceIds.Add(metadata.Id!);
        }

        Assert.Equal(["gAAAA_done_1", "gAAAA_done_2"], signatures);
        Assert.Equal(["rs_1", "rs_2"], sourceIds);
    }

    [Fact]
    public void AssociatesReasoningSummaryWithItem()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_reasoning_summary","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"rs_first","type":"reasoning","summary":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.reasoning_summary_text.delta","item_id":"rs_first","output_index":0,"summary_index":0,"delta":"first"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_first","type":"reasoning","summary":[],"encrypted_content":"gAAAA_FIRST_BLOB"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":1,"item":{"id":"rs_second","type":"reasoning","summary":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.reasoning_summary_text.delta","item_id":"rs_second","output_index":1,"summary_index":0,"delta":"second"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":1,"item":{"id":"rs_second","type":"reasoning","summary":[],"encrypted_content":"gAAAA_SECOND_BLOB"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_reasoning_summary","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));

        Assert.Null(err);
        var summaries = new List<string>();
        var summaryItemIds = new List<string>();
        foreach (var response in responses)
        {
            if (ResponsesStreamTestSupport.IsDone(response)
                || response.Choices.Count == 0
                || response.Choices[0].Delta?.ReasoningContent is null)
            {
                continue;
            }

            summaries.Add(response.Choices[0].Delta!.ReasoningContent!);
            Assert.True(
                ResponsesStreamMetadata.TryGetReasoningItemMetadata(
                    response.TransformerMetadata, out var metadata),
                "reasoning metadata missing");
            summaryItemIds.Add(metadata.Id!);
        }

        Assert.Equal(["first", "second"], summaries);
        Assert.Equal(["rs_first", "rs_second"], summaryItemIds);
    }

    [Fact]
    public void HandlesReasoningText()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_reasoning_text","object":"response","created_at":1700000000,"model":"deepseek-v4-flash","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"rs_text","type":"reasoning","status":"in_progress","summary":[],"content":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.reasoning_text.delta","item_id":"rs_text","output_index":0,"content_index":0,"delta":"first"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.reasoning_text.delta","item_id":"rs_text","output_index":0,"content_index":0,"delta":" second"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.reasoning_text.done","item_id":"rs_text","output_index":0,"content_index":0,"text":"first second"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_text","type":"reasoning","status":"completed","summary":[],"content":[{"type":"reasoning_text","text":"first second"}]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_reasoning_text","object":"response","created_at":1700000000,"model":"deepseek-v4-flash","status":"completed","output":[]}}"""));

        Assert.Null(err);
        var deltas = new List<string>();
        var itemIds = new List<string>();
        foreach (var response in responses)
        {
            if (ResponsesStreamTestSupport.IsDone(response)
                || response.Choices.Count == 0
                || response.Choices[0].Delta?.ReasoningContent is null)
            {
                continue;
            }

            deltas.Add(response.Choices[0].Delta!.ReasoningContent!);
            Assert.True(
                ResponsesStreamMetadata.TryGetReasoningItemMetadata(
                    response.TransformerMetadata, out var metadata),
                "reasoning metadata missing");
            itemIds.Add(metadata.Id!);
        }

        Assert.Equal(["first", " second"], deltas);
        Assert.Equal(["rs_text", "rs_text"], itemIds);
    }

    [Fact]
    public void StreamRoundTripPreservesCompactionSummary()
    {
        var upstreamEvents = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_compact_1","object":"response.compaction","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1","type":"message","status":"completed","content":[{"type":"input_text","text":"Preserve this fact."}],"role":"user"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":1,"item":{"id":"cmp_1","type":"compaction_summary","encrypted_content":"encrypted-summary"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_compact_1","object":"response.compaction","created_at":1700000000,"model":"gpt-5","status":"completed","output":[{"id":"msg_1","type":"message","status":"completed","content":[{"type":"input_text","text":"Preserve this fact."}],"role":"user"},{"id":"cmp_1","type":"compaction_summary","encrypted_content":"encrypted-summary"}],"usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12}}}"""),
        };

        var (llmResponses, err) = RunEvents(upstreamEvents);
        Assert.Null(err);

        MessageContentPart? compactPart = null;
        foreach (var response in llmResponses)
        {
            if (ResponsesStreamTestSupport.IsDone(response) || response.Choices.Count == 0)
            {
                continue;
            }

            foreach (var part in response.Choices[0].Delta?.Content?.MultipleContent ?? [])
            {
                if (part.Type == "compaction_summary")
                {
                    compactPart = part;
                }
            }
        }

        Assert.NotNull(compactPart);
        Assert.NotNull(compactPart!.Compact);
        Assert.Equal("cmp_1", compactPart.Compact!.Id);
        Assert.Equal("encrypted-summary", compactPart.Compact.EncryptedContent);

        var (clientEvents, clientErr) = RunInbound(llmResponses);
        Assert.Null(clientErr);

        var compactDone = clientEvents
            .LastOrDefault(static e => e.Type == "response.output_item.done" && e.Item?.Type == "compaction_summary");
        var completed = clientEvents.LastOrDefault(static e => e.Type == "response.completed");

        Assert.NotNull(compactDone);
        Assert.Equal("cmp_1", compactDone!.Item!.Id);
        Assert.Equal("encrypted-summary", compactDone.Item.EncryptedContent);
        Assert.NotNull(completed);
        Assert.Equal("resp_compact_1", completed!.Response?.Id);
        Assert.NotNull(completed.Response?.Output);
        var output = Assert.Single(completed.Response!.Output!);
        Assert.Equal("compaction_summary", output.Type);
        Assert.Equal("encrypted-summary", output.EncryptedContent);
    }

    [Fact]
    public void ResponseCancelledCompletes()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_cancelled","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.cancelled","response":{"id":"resp_cancelled","object":"response","created_at":1700000000,"model":"gpt-5","status":"canceled","output":[]}}"""));

        Assert.Null(err);
        Assert.Equal(3, responses.Count);
        Assert.True(ResponsesStreamTestSupport.IsDone(responses[2]));
        Assert.Equal("resp_cancelled", responses[1].Id);
        Assert.Equal("gpt-5", responses[1].Model);
        Assert.Equal(1700000000L, responses[1].Created);
        Assert.NotEmpty(responses[1].Choices);
        Assert.Equal("cancelled", responses[1].Choices[0].FinishReason);
    }

    [Fact]
    public void EmitsArgumentsProvidedOnlyInDone()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_done_arguments","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_done_arguments","type":"function_call","call_id":"call_done_arguments","name":"collaboration.spawn_agent","arguments":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.done","item_id":"fc_done_arguments","output_index":0,"name":"collaboration.spawn_agent","arguments":"{\"task\":\"delegate this task\"}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_done_arguments","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));

        Assert.Null(err);
        var arguments = new StringBuilder();
        foreach (var response in responses)
        {
            if (ResponsesStreamTestSupport.IsDone(response) || response.Choices.Count == 0)
            {
                continue;
            }

            foreach (var toolCall in response.Choices[0].Delta?.ToolCalls ?? [])
            {
                arguments.Append(toolCall.Function.Arguments);
            }
        }

        ResponsesStreamTestSupport.AssertJsonEquivalent(
            """{"task":"delegate this task"}""", arguments.ToString());
    }

    // Chat Completions clients such as the OpenAI SDK join every string field of a tool call
    // delta, so the done event must not send the id and name again (#2648).
    public static TheoryData<
        string, string, string, string[], string, string, string, string> DoneIdentityCases => new()
    {
        { "arguments only in done", "get_weather", "", [], "get_weather", "", "get_weather", "" },
        { "remaining arguments in done", "get_weather", "", ["""{"city":"""], "get_weather", "", "get_weather", "" },
        { "namespace only in done", "get_weather", "", [], "get_weather", "weather_tools", "get_weather", "weather_tools" },
        { "name only in done keeps namespace", "", "collaboration", [], "spawn_agent", "collaboration", "collaboration__spawn_agent", "collaboration" },
    };

    [Theory]
    [MemberData(nameof(DoneIdentityCases))]
    public void DoneDoesNotRepeatToolCallIdentity(
        string name,
        string addedName,
        string addedNamespace,
        string[] deltas,
        string doneName,
        string doneNamespace,
        string wantName,
        string wantNamespace)
    {
        _ = name;
        var events = new List<StreamEvent>
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_identity","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                $$$"""{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_identity","type":"function_call","call_id":"call_identity","name":{{{JsonSerializer.Serialize(addedName)}}},"namespace":{{{JsonSerializer.Serialize(addedNamespace)}}},"arguments":""}}"""),
        };
        foreach (var delta in deltas)
        {
            events.Add(ResponsesStreamTestSupport.Event(
                $$$"""{"type":"response.function_call_arguments.delta","item_id":"fc_identity","output_index":0,"delta":{{{JsonSerializer.Serialize(delta)}}}}"""));
        }

        events.Add(ResponsesStreamTestSupport.Event(
            $$$"""{"type":"response.function_call_arguments.done","item_id":"fc_identity","output_index":0,"name":{{{JsonSerializer.Serialize(doneName)}}},"namespace":{{{JsonSerializer.Serialize(doneNamespace)}}},"arguments":"{\"city\":\"Paris\"}"}"""));
        events.Add(ResponsesStreamTestSupport.Event(
            """{"type":"response.completed","response":{"id":"resp_identity","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));

        var (responses, err) = RunEvents(events);
        Assert.Null(err);

        // Join string fields the way Chat Completions clients do. Namespace has no Chat
        // Completions field; the Responses inbound stream replaces it, so keep the last one.
        var id = new StringBuilder();
        var toolType = new StringBuilder();
        var joinedName = new StringBuilder();
        var @namespace = string.Empty;
        var arguments = new StringBuilder();
        foreach (var response in responses)
        {
            if (ResponsesStreamTestSupport.IsDone(response)
                || response.Choices.Count == 0
                || response.Choices[0].Delta is not { } delta)
            {
                continue;
            }

            foreach (var toolCall in delta.ToolCalls ?? [])
            {
                id.Append(toolCall.Id);
                toolType.Append(toolCall.Type);
                joinedName.Append(toolCall.Function.Name);
                if (!string.IsNullOrEmpty(toolCall.Function.Namespace))
                {
                    @namespace = toolCall.Function.Namespace;
                }

                arguments.Append(toolCall.Function.Arguments);
            }
        }

        Assert.Equal("call_identity", id.ToString());
        Assert.Equal("function", toolType.ToString());
        Assert.Equal(wantName, joinedName.ToString());
        Assert.Equal(wantNamespace, @namespace);
        ResponsesStreamTestSupport.AssertJsonEquivalent("""{"city":"Paris"}""", arguments.ToString());
    }

    [Fact]
    public void AcceptsEquivalentFinalArguments()
    {
        const string forwardedArguments = """{"task":"delegate this task","priority":1}""";
        const string finalArguments = """
            {
              "priority": 1,
              "task": "delegate this task"
            }
            """;

        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_equivalent_arguments","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_equivalent_arguments","type":"function_call","call_id":"call_equivalent_arguments","name":"spawn_agent","arguments":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.delta","item_id":"fc_equivalent_arguments","output_index":0,"delta":"{\"task\":\"delegate this task\",\"priority\":1}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.done","item_id":"fc_equivalent_arguments","output_index":0,"arguments":"{\n  \"priority\": 1,\n  \"task\": \"delegate this task\"\n}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_equivalent_arguments","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));

        Assert.Null(err);
        var arguments = new StringBuilder();
        foreach (var response in responses)
        {
            if (ResponsesStreamTestSupport.IsDone(response) || response.Choices.Count == 0)
            {
                continue;
            }

            foreach (var toolCall in response.Choices[0].Delta?.ToolCalls ?? [])
            {
                arguments.Append(toolCall.Function.Arguments);
            }
        }

        Assert.Equal(forwardedArguments, arguments.ToString());
        ResponsesStreamTestSupport.AssertJsonEquivalent(finalArguments, arguments.ToString());
    }

    [Fact]
    public void RejectsDistinctLargeJsonNumbers()
    {
        var (_, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_large_numbers","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_large_numbers","type":"function_call","call_id":"call_large_numbers","name":"spawn_agent","arguments":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.delta","item_id":"fc_large_numbers","output_index":0,"delta":"{\"value\":9007199254740992}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.done","item_id":"fc_large_numbers","output_index":0,"arguments":"{\"value\":9007199254740993}"}"""));

        Assert.NotNull(err);
        Assert.Contains(
            "function call arguments mismatch for call_id \"call_large_numbers\"",
            err!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTripPreservesToolIdentityProvidedOnlyInDone()
    {
        var upstreamEvents = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_done_identity","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_done_identity","type":"function_call","call_id":"call_done_identity","arguments":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.done","item_id":"fc_done_identity","output_index":0,"name":"spawn_agent","namespace":"collaboration","arguments":"{\"task\":\"delegate this task\"}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_done_identity","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
        };

        var (clientEvents, err) = RoundTrip(upstreamEvents);
        Assert.Null(err);

        var addedItem = clientEvents.LastOrDefault(
            static e => e.Type == "response.output_item.added" && e.Item?.Type == "function_call")?.Item;
        var completedItem = clientEvents.LastOrDefault(
            static e => e.Type == "response.output_item.done" && e.Item?.Type == "function_call")?.Item;
        var completedArguments = clientEvents.LastOrDefault(
            static e => e.Type == "response.function_call_arguments.done");

        Assert.NotNull(addedItem);
        Assert.NotNull(completedItem);
        Assert.NotNull(completedArguments);
        Assert.Equal("call_done_identity", completedArguments!.CallId);
        Assert.Equal("spawn_agent", completedArguments.Name);
        Assert.Equal("collaboration", completedArguments.Namespace);
        ResponsesStreamTestSupport.AssertJsonEquivalent(
            """{"task":"delegate this task"}""", completedArguments.Arguments!);
        Assert.Equal("spawn_agent", addedItem!.Name);
        Assert.Equal("collaboration", addedItem.Namespace);
        Assert.Equal("spawn_agent", completedItem!.Name);
        Assert.Equal("collaboration", completedItem.Namespace);
        ResponsesStreamTestSupport.AssertJsonEquivalent(
            """{"task":"delegate this task"}""", completedItem.Arguments);
    }

    [Fact]
    public void RoundTripDefersFunctionCallUntilLateIdentityArrives()
    {
        const string arguments = """{"task":"delegate this task"}""";
        var upstreamEvents = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_late_identity","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_late_identity","type":"function_call","call_id":"call_late_identity","arguments":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.delta","item_id":"fc_late_identity","output_index":0,"delta":"{\"task\":\"delegate this task\"}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.function_call_arguments.done","item_id":"fc_late_identity","output_index":0,"name":"spawn_agent","namespace":"collaboration","arguments":"{\"task\":\"delegate this task\"}"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_late_identity","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
        };

        var (clientEvents, err) = RoundTrip(upstreamEvents);
        Assert.Null(err);

        var addedItems = clientEvents
            .Where(static e => e.Type == "response.output_item.added" && e.Item?.Type == "function_call")
            .Select(static e => e.Item!)
            .ToList();
        var receivedArguments = string.Concat(
            clientEvents
                .Where(static e => e.Type == "response.function_call_arguments.delta")
                .Select(static e => e.Delta));

        var addedItem = Assert.Single(addedItems);
        Assert.Equal("spawn_agent", addedItem.Name);
        Assert.Equal("collaboration", addedItem.Namespace);
        ResponsesStreamTestSupport.AssertJsonEquivalent(arguments, receivedArguments);
    }

    [Fact]
    public void RoundTripPreservesCustomToolInputProvidedOnlyInFinalEvents()
    {
        const string input = "*** Begin Patch\n*** Add File: /tmp/probe.txt\n+hello\n*** End Patch\n";
        var upstreamEvents = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_custom_done_input","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"ct_custom_done_input","type":"custom_tool_call","call_id":"call_custom_done_input","name":"apply_patch","input":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.custom_tool_call_input.done","item_id":"ct_custom_done_input","output_index":0,"input":"*** Begin Patch\n*** Add File: /tmp/probe.txt\n+hello\n*** End Patch\n"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"ct_custom_done_input","type":"custom_tool_call","status":"completed","call_id":"call_custom_done_input","name":"apply_patch","input":"*** Begin Patch\n*** Add File: /tmp/probe.txt\n+hello\n*** End Patch\n"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_custom_done_input","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""),
        };

        var (clientEvents, err) = RoundTrip(upstreamEvents);
        Assert.Null(err);

        var completedInput = clientEvents
            .Where(static e => e.Type == "response.output_item.done" && e.Item?.Type == "custom_tool_call")
            .Select(static e => e.Item!.Input)
            .LastOrDefault();

        Assert.Equal(input, completedInput);
    }

    [Fact]
    public void ReconcilesCustomToolFinalInput()
    {
        var cases = new (string Name, StreamEvent[] Events, Dictionary<string, string> Expected)[]
        {
            (
                "custom tool input done is the only final input source",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.added","output_index":0,"item":{"id":"ct_input_done","type":"custom_tool_call","call_id":"call_input_done","name":"apply_patch","input":""}}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.custom_tool_call_input.done","item_id":"ct_input_done","output_index":0,"input":"complete input"}"""),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["call_input_done"] = "complete input" }
            ),
            (
                "output item done is the only final input source",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.added","output_index":0,"item":{"id":"ct_item_done","type":"custom_tool_call","call_id":"call_item_done","name":"apply_patch","input":""}}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.done","output_index":0,"item":{"id":"ct_item_done","type":"custom_tool_call","status":"completed","call_id":"call_item_done","name":"apply_patch","input":"complete input"}}"""),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["call_item_done"] = "complete input" }
            ),
            (
                "final input adds only the suffix missing from deltas",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.added","output_index":0,"item":{"id":"ct_partial","type":"custom_tool_call","call_id":"call_partial","name":"apply_patch","input":""}}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.custom_tool_call_input.delta","item_id":"ct_partial","output_index":0,"delta":"partial"}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.custom_tool_call_input.done","item_id":"ct_partial","output_index":0,"input":"partial input"}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.done","output_index":0,"item":{"id":"ct_partial","type":"custom_tool_call","status":"completed","call_id":"call_partial","name":"apply_patch","input":"partial input"}}"""),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal) { ["call_partial"] = "partial input" }
            ),
            (
                "multiple calls keep their final inputs isolated",
                [
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.added","output_index":0,"item":{"id":"ct_first","type":"custom_tool_call","call_id":"call_first","name":"apply_patch","input":""}}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.output_item.added","output_index":1,"item":{"id":"ct_second","type":"custom_tool_call","call_id":"call_second","name":"write_file","input":""}}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.custom_tool_call_input.done","item_id":"ct_second","output_index":1,"input":"second input"}"""),
                    ResponsesStreamTestSupport.Event(
                        """{"type":"response.custom_tool_call_input.done","item_id":"ct_first","output_index":0,"input":"first input"}"""),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["call_first"] = "first input",
                    ["call_second"] = "second input",
                }
            ),
        };

        foreach (var (name, events, expected) in cases)
        {
            var withTerminal = events.Append(
                ResponsesStreamTestSupport.Event(
                    """{"type":"response.completed","response":{"id":"resp_custom_final_input","object":"response","created_at":1700000000,"model":"gpt-5","status":"completed","output":[]}}"""));
            var (responses, err) = RunEvents(withTerminal);

            Assert.Null(err);
            var actual = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var response in responses)
            {
                if (ResponsesStreamTestSupport.IsDone(response) || response.Choices.Count == 0)
                {
                    continue;
                }

                foreach (var toolCall in response.Choices[0].Delta?.ToolCalls ?? [])
                {
                    if (toolCall.ResponseCustomToolCall is { Input.Length: > 0 } custom)
                    {
                        actual[custom.CallId] = actual.GetValueOrDefault(custom.CallId, string.Empty) + custom.Input;
                    }
                }
            }

            Assert.Equal(RenderMap(expected), RenderMap(actual));
            _ = name;
        }
    }

    [Fact]
    public void RejectsConflictingCustomToolFinalInput()
    {
        var (_, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"ct_conflict","type":"custom_tool_call","call_id":"call_conflict","name":"apply_patch","input":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.custom_tool_call_input.delta","item_id":"ct_conflict","output_index":0,"delta":"forwarded input"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.custom_tool_call_input.done","item_id":"ct_conflict","output_index":0,"input":"different input"}"""));

        Assert.NotNull(err);
        Assert.Contains(
            "custom tool call input mismatch for call_id \"call_conflict\"",
            err!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesFinalItemAnnotations()
    {
        var events = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_stream_annotations","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.added","output_index":0,"item":{"id":"msg_stream_annotations","type":"message","status":"in_progress","role":"assistant"}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.content_part.added","item_id":"msg_stream_annotations","output_index":0,"content_index":0,"part":{"type":"output_text","text":""}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_text.delta","item_id":"msg_stream_annotations","output_index":0,"content_index":0,"delta":"Search result"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_text.done","item_id":"msg_stream_annotations","output_index":0,"content_index":0,"text":"Search result"}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_stream_annotations","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Search result","annotations":[{"type":"url_citation","start_index":0,"end_index":6,"url_citation":{"url":"https://example.com/result","title":"Example Result"}}]}]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_stream_annotations","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"completed","output":[]}}"""),
        };

        var (actual, err) = RunEvents(events);
        Assert.Null(err);
        Assert.NotEmpty(actual);

        var found = new List<Annotation>();
        foreach (var response in actual)
        {
            if (ResponsesStreamTestSupport.IsDone(response))
            {
                continue;
            }

            foreach (var choice in response.Choices)
            {
                if (choice.Message?.Annotations is { Count: > 0 } messageAnnotations)
                {
                    found = messageAnnotations;
                    break;
                }

                if (choice.Delta?.Annotations is { Count: > 0 } deltaAnnotations)
                {
                    found = deltaAnnotations;
                    break;
                }
            }

            if (found.Count > 0)
            {
                break;
            }
        }

        var annotation = Assert.Single(found);
        Assert.Equal("url_citation", annotation.Type);
        Assert.NotNull(annotation.UrlCitation);
        Assert.Equal("https://example.com/result", annotation.UrlCitation!.Url);
        Assert.Equal("Example Result", annotation.UrlCitation.Title);
        Assert.Equal(0L, annotation.StartIndex ?? -1L);
        Assert.Equal(6L, annotation.EndIndex ?? -1L);
    }

    [Fact]
    public void PreservesWebSearchMetadataOnAnnotationChunk()
    {
        var events = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_stream_web_search_annotations","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"ws_123","type":"web_search_call","status":"completed","action":{"type":"search","query":"latest ai news","sources":[{"type":"url","url":"https://example.com/source","title":"Example Source"}]}}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":1,"item":{"id":"msg_stream_web_search_annotations","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Search result","annotations":[{"type":"url_citation","url_citation":{"url":"https://example.com/result","title":"Example Result"}}]}]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_stream_web_search_annotations","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"completed","output":[]}}"""),
        };

        var (actual, err) = RunEvents(events);
        Assert.Null(err);

        var annotationChunk = actual.FirstOrDefault(
            static r => !ResponsesStreamTestSupport.IsDone(r)
                && r.Choices.Any(static c => c.Delta?.Annotations is { Count: > 0 }));

        Assert.NotNull(annotationChunk);
        Assert.NotNull(annotationChunk!.TransformerMetadata);
        Assert.True(
            annotationChunk.TransformerMetadata!
                .ContainsKey(ResponsesKeys.WebSearchCallsTransformerMetadataKey));
    }

    [Fact]
    public void PreservesWebSearchMetadataWithoutAnnotations()
    {
        var events = new[]
        {
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_stream_web_search_no_annotations","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"in_progress","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"ws_456","type":"web_search_call","status":"completed","action":{"type":"search","query":"latest ai news","sources":[{"type":"url","url":"https://example.com/source","title":"Example Source"}]}}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_item.done","output_index":1,"item":{"id":"msg_stream_web_search_no_annotations","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Search result without inline citations"}]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_stream_web_search_no_annotations","object":"response","created_at":1700000000,"model":"gpt-4o-search-preview","status":"completed","output":[]}}"""),
        };

        var (actual, err) = RunEvents(events);
        Assert.Null(err);

        var metadataChunk = actual.FirstOrDefault(
            static r => r.TransformerMetadata?.ContainsKey(ResponsesKeys.WebSearchCallsTransformerMetadataKey) == true);

        Assert.NotNull(metadataChunk);
    }

    [Fact]
    public void PreservesPreviousResponseId()
    {
        var (actual, err) = Run(
            ResponsesStreamTestSupport.Event(
                """{"type":"response.created","response":{"id":"resp_stream_prev","object":"response","created_at":1700000000,"model":"gpt-5.4","status":"in_progress","previous_response_id":"resp_prev_123","output":[]}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.completed","response":{"id":"resp_stream_prev","object":"response","created_at":1700000000,"model":"gpt-5.4","status":"completed","previous_response_id":"resp_prev_123","output":[],"usage":{"input_tokens":1,"output_tokens":1,"total_tokens":2}}}"""));

        Assert.Null(err);
        Assert.Equal(4, actual.Count);
        Assert.All(
            actual.Take(3),
            static r => Assert.Equal("resp_prev_123", r.PreviousResponseId));
        Assert.True(ResponsesStreamTestSupport.IsDone(actual[3]));
    }

    [Fact]
    public void MapsCompletedStatusToFinishReason()
    {
        var cases = new (string Name, string Status, string? IncompleteReason, string Expected, bool WithToolCalls)[]
        {
            ("incomplete maps to length", "incomplete", null, "length", false),
            ("incomplete with content_filter reason maps to content_filter", "incomplete", "content_filter", "content_filter", false),
            ("failed maps to error", "failed", null, "error", false),
            ("cancelled maps to cancelled", "cancelled", null, "cancelled", false),
            ("canceled (US spelling) maps to cancelled", "canceled", null, "cancelled", false),
            ("nil status falls back to stop", "", null, "stop", false),
            ("completed falls back to stop", "completed", null, "stop", false),
            ("completed with tool calls maps to tool_calls", "completed", null, "tool_calls", true),
        };

        foreach (var (name, status, incompleteReason, expectedReason, withToolCalls) in cases)
        {
            var events = new List<StreamEvent>
            {
                ResponsesStreamTestSupport.Event(
                    """{"type":"response.created","response":{"id":"resp_status_map","object":"response","created_at":1700000000,"model":"gpt-5","status":"in_progress","output":[]}}"""),
            };
            if (withToolCalls)
            {
                events.Add(ResponsesStreamTestSupport.Event(
                    """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_status_map","type":"function_call","call_id":"call_status_map","name":"get_weather","arguments":""}}"""));
                events.Add(ResponsesStreamTestSupport.Event(
                    """{"type":"response.function_call_arguments.done","item_id":"fc_status_map","output_index":0,"name":"get_weather","arguments":"{}"}"""));
            }

            var completePayload = incompleteReason is not null
                ? $$$"""{"type":"response.completed","response":{"id":"resp_status_map","object":"response","created_at":1700000000,"model":"gpt-5","status":"incomplete","incomplete_details":{"reason":"{{{incompleteReason}}}"},"output":[]}}"""
                : status.Length == 0
                    ? """{"type":"response.completed","response":{"id":"resp_status_map","object":"response","created_at":1700000000,"model":"gpt-5","output":[]}}"""
                    : $$$"""{"type":"response.completed","response":{"id":"resp_status_map","object":"response","created_at":1700000000,"model":"gpt-5","status":"{{{status}}}","output":[]}}""";
            events.Add(ResponsesStreamTestSupport.Event(completePayload));

            var (responses, err) = RunEvents(events);
            Assert.Null(err);

            var finishReasons = responses
                .Where(static r => !ResponsesStreamTestSupport.IsDone(r) && r.Choices.Count > 0)
                .Select(static r => r.Choices[0].FinishReason)
                .Where(static reason => reason is not null)
                .ToList();

            var actualReason = Assert.Single(finishReasons);
            Assert.Equal(expectedReason, actualReason);
            _ = name;
        }
    }

    [Fact]
    public void MapsIncompleteReasonToFinishReason()
    {
        var cases = new (string Reason, string Finish)[]
        {
            ("max_output_tokens", "length"),
            ("content_filter", "content_filter"),
            ("provider_limit", "length"),
        };

        foreach (var (reason, finish) in cases)
        {
            var payload =
                "{\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\","
                + "\"incomplete_details\":{\"reason\":\"" + reason + "\"}}}";
            var (chunks, err) = Run(ResponsesStreamTestSupport.Event(payload));

            Assert.Null(err);
            Assert.Equal(2, chunks.Count);
            var choice = Assert.Single(chunks[0].Choices);
            Assert.Equal(finish, choice.FinishReason);
            Assert.True(ResponsesStreamTestSupport.IsDone(chunks[1]));
        }
    }

    [Fact]
    public void CreatedAtCompatibility()
    {
        foreach (var createdAt in new[] { "1786360449", "1786360449.0" })
        {
            var (responses, err) = Run(
                ResponsesStreamTestSupport.Event(
                    $$$"""{"type":"response.created","response":{"id":"resp_parallel","object":"response","created_at":{{{createdAt}}},"model":"parallel","status":"in_progress","output":[]}}"""),
                ResponsesStreamTestSupport.Event(
                    """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"今日长鑫科技"}"""),
                ResponsesStreamTestSupport.Event(
                    """{"type":"response.output_text.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"的收盘价是多少。"}"""),
                ResponsesStreamTestSupport.Event(
                    $$$"""{"type":"response.completed","response":{"id":"resp_parallel","object":"response","created_at":{{{createdAt}}},"model":"parallel","status":"completed","output":[]}}"""));

            Assert.Null(err);
            Assert.True(responses.Count >= 4);
            Assert.Equal(1786360449L, responses[0].Created);
            Assert.Equal("resp_parallel", responses[0].Id);
            Assert.Equal("parallel", responses[0].Model);

            var streamedText = new StringBuilder();
            foreach (var response in responses)
            {
                if (response.Choices.Count > 0 && response.Choices[0].Delta?.Content?.Content is { } content)
                {
                    streamedText.Append(content);
                }
            }

            Assert.Equal("今日长鑫科技的收盘价是多少。", streamedText.ToString());

            var last = responses.Last(static r => !ResponsesStreamTestSupport.IsDone(r));
            var choice = Assert.Single(last.Choices);
            Assert.Equal("stop", choice.FinishReason);
            Assert.Equal(1786360449L, last.Created);
        }
    }

    [Fact]
    public void PreservesOfficialWebSocketError()
    {
        var cases = new (string Name, string Data, string ExpectedType)[]
        {
            (
                "official nested error",
                """{"type":"error","status":400,"error":{"type":"invalid_request_error","code":"invalid_value","message":"invalid websocket request","param":"input"}}""",
                "invalid_request_error"
            ),
            (
                "partial nested error retains flattened fields",
                """{"type":"error","status":400,"code":"invalid_value","message":"invalid websocket request","param":"input","error":{"type":"invalid_request_error"}}""",
                "invalid_request_error"
            ),
            (
                "legacy flattened error has a type",
                """{"type":"error","status":400,"code":"invalid_value","message":"invalid websocket request","param":"input"}""",
                "error"
            ),
        };

        foreach (var (name, data, expectedType) in cases)
        {
            var transformer = ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");
            using var stream = transformer.TransformStream(
                null,
                new SliceStream<StreamEvent>(
                [
                    new StreamEvent { Type = "error", Data = Encoding.UTF8.GetBytes(data) },
                ]));

            Assert.False(stream.Next());
            var responseErr = Assert.IsType<ResponseError>(stream.Err);
            Assert.Equal(400, responseErr.StatusCode);
            Assert.Equal(expectedType, responseErr.Detail.Type);
            Assert.Equal("invalid_value", responseErr.Detail.Code);
            Assert.Equal("invalid websocket request", responseErr.Detail.Message);
            Assert.Equal("input", responseErr.Detail.Param);
            _ = name;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ResponsesOutboundTransformer Transformer()
        => ResponsesOutboundTransformer.Create("https://api.openai.com", "test-api-key");

    /// <summary>Renders a call-id to input map so Assert.Equal compares values, not references.</summary>
    private static string RenderMap(Dictionary<string, string> map)
        => string.Join(
            "|",
            map.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => pair.Key + "=" + pair.Value));

    public static TheoryData<string, int> ResponseFailedStatuses => new()
    {
        { "null", 502 },
        { "0", 502 },
        { "200", 502 },
        { "399", 502 },
        { "600", 502 },
        { "-1", 502 },
        { "\"failed\"", 502 },
        { "400", 400 },
        { "429", 429 },
        { "500", 500 },
        { "599", 599 },
    };

    // Port of TestResponsesOutboundStreamResponseFailedStatus.
    [Theory]
    [MemberData(nameof(ResponseFailedStatuses))]
    public void StructuredResponseFailedCarriesStatusAndEmptyResponseCause(string status, int want)
    {
        var data = $$$$"""{"type":"response.failed","status":{{{{status}}}},"response":{"error":{"type":"server_error","code":"server_is_overloaded","message":"server overloaded"}}}""";
        using var stream = new ResponsesOutboundStream(
            new SliceStream<StreamEvent>([ResponsesStreamTestSupport.Event(data)]));

        var chunks = GoStreams.All(stream, out var err);

        Assert.Empty(chunks);
        var responseError = GoErrors.As<ResponseError>(err);
        Assert.NotNull(responseError);
        Assert.Equal(want, responseError.StatusCode);
        Assert.True(GoErrors.Is(err, PipelineErrors.EmptyResponse));
        Assert.Equal(
            "error: server overloaded, code: server_is_overloaded, type: server_error",
            responseError.ToString());
    }

    // Port of TestResponsesOutboundStreamResponseFailedUsageBeforeError.
    [Fact]
    public void StructuredResponseFailedEmitsUsageBeforeTheErrorAndStopsReading()
    {
        var source = new CountingEventStream(
        [
            ResponsesStreamTestSupport.Event(
                """{"type":"response.failed","response":{"usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12,"input_tokens_details":{"cached_tokens":3},"output_tokens_details":{"reasoning_tokens":1}},"error":{"type":"server_error","code":"server_is_overloaded","message":"server overloaded"}}}"""),
            ResponsesStreamTestSupport.Event(
                """{"type":"response.output_text.delta","delta":"late content"}"""),
        ]);
        using var stream = new ResponsesOutboundStream(source);

        Assert.True(stream.Next(), "usage must be available before stream termination");
        var chunk = stream.Current!;
        Assert.Empty(chunk.Choices);
        Assert.NotNull(chunk.Usage);
        Assert.Equal(10L, chunk.Usage!.PromptTokens);
        Assert.Equal(2L, chunk.Usage.CompletionTokens);
        Assert.Equal(12L, chunk.Usage.TotalTokens);
        Assert.Equal(3L, chunk.Usage.PromptTokensDetails!.CachedTokens);
        Assert.Equal(1L, chunk.Usage.CompletionTokensDetails!.ReasoningTokens);

        Assert.False(stream.Next());
        Assert.True(GoErrors.Is(stream.Err, PipelineErrors.EmptyResponse));
        Assert.False(stream.Next(), "the failure must remain terminal after usage drains");
        Assert.True(source.Index == 1, "do not consume events after the failure");
    }

    // Port of TestResponsesOutboundStreamResponseFailedMalformed: a failure
    // without a structured code keeps the finish_reason terminal.
    [Fact]
    public void MalformedResponseFailedKeepsFinishReasonTerminal()
    {
        var (responses, err) = Run(
            ResponsesStreamTestSupport.Event("""{"type":"response.failed","response":{}}"""));

        Assert.Null(err);
        Assert.NotEmpty(responses);
        Assert.Contains(
            responses,
            static r => r.Choices.Count > 0 && r.Choices[0].FinishReason == "error");
    }

    private sealed class CountingEventStream(StreamEvent[] items) : IStream<StreamEvent>
    {
        public int Index { get; private set; }

        public StreamEvent? Current { get; private set; }

        public Exception? Err => null;

        public bool Next()
        {
            if (Index >= items.Length)
            {
                return false;
            }

            Current = items[Index++];
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static (List<LlmResponse> Responses, Exception? Err) Run(params StreamEvent[] events)
        => RunEvents(events);

    private static (List<LlmResponse> Responses, Exception? Err) RunEvents(IEnumerable<StreamEvent> events)
        => RunStream(new SliceStream<StreamEvent>(events));

    private static (List<LlmResponse> Responses, Exception? Err) RunStream(IStream<StreamEvent> source)
    {
        using var stream = Transformer().TransformStream(null, source);
        var responses = GoStreams.All(stream, out var err);
        return (responses, err);
    }

    private static (List<ResponsesStreamEvent> Events, Exception? Err) RoundTrip(IEnumerable<StreamEvent> events)
    {
        var (unified, err) = RunEvents(events);
        Assert.Null(err);
        return RunInbound(unified);
    }

    private static (List<ResponsesStreamEvent> Events, Exception? Err) RunInbound(
        IEnumerable<LlmResponse> responses)
    {
        using var stream = new ResponsesInboundTransformer()
            .TransformStream(new SliceStream<LlmResponse>(responses));
        var clientEvents = GoStreams.All(stream, out var err);
        return (ResponsesStreamTestSupport.ParseEvents(clientEvents), err);
    }
}
