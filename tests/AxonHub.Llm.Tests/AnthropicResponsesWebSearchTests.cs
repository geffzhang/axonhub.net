using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/anthropic/responses_web_search_test.go and the
/// Anthropic-side Responses search coverage added by live 513a7822 (#2750).
/// </summary>
public sealed class AnthropicResponsesWebSearchTests
{
    [Fact]
    public void BlocksTrackOnlyObservedCalls()
    {
        var seen = new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal);

        Assert.Empty(Blocks("""[{"status":"completed"}]""", seen));

        var started = Blocks("""[{"id":"ws_a","status":"in_progress"}]""", seen);
        var block = Assert.Single(started);
        Assert.Equal("server_tool_use", block.Type);
        Assert.Equal("{}", block.Input!.Value.GetRawText());

        Assert.Empty(Blocks("""[{"id":"ws_a","status":"in_progress"}]""", seen));

        var completed = Blocks("""[{"id":"ws_a","status":"completed"}]""", seen);
        Assert.Equal("web_search_tool_result", Assert.Single(completed).Type);
        Assert.Equal("[]", Encoding.UTF8.GetString(completed[0].Content!.Raw!));

        Assert.Empty(Blocks("""[{"id":"ws_a","status":"completed"}]""", seen));

        var failed = Blocks("""[{"id":"ws_b","status":"failed"}]""", seen);
        Assert.Equal(2, failed.Count);
        Assert.Equal(
            "web_search_tool_result_error",
            JsonDocument.Parse(failed[1].Content!.Raw!).RootElement.GetProperty("type").GetString());
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void BlocksPreserveSourcesWithoutFabricatingEncryption()
    {
        var blocks = Blocks(
            """[{"id":"ws_a","status":"completed","action":{"queries":["Debian"],"sources":[{"url":"https://debian.org","title":"Debian"},{"url":"https://debian.org","title":"Duplicate"},{"title":"No URL"}]}}]""",
            new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal));

        Assert.Equal(2, blocks.Count);
        Assert.Equal("""{"query":"Debian"}""", blocks[0].Input!.Value.GetRawText());
        AssertJsonEqual(
            """[{"type":"web_search_result","url":"https://debian.org","title":"Debian"}]""",
            Encoding.UTF8.GetString(blocks[1].Content!.Raw!));
    }

    [Fact]
    public void BlocksFillMissingSourceTitlesFromCitations()
    {
        var seen = new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal);
        var metadata = Metadata(
            """
            [{"id":"a","status":"completed","action":{"query":"Debian","sources":[{"url":"https://debian.org/","title":"Original"},{"url":"https://debian.org/releases/"},{"url":"https://debian.org/distrib/","title":" \t"}]}},
             {"id":"b","status":"completed","action":{"query":"Other","sources":[{"url":"https://example.org/"}]}}]
            """);

        AnthropicResponsesWebSearch.Blocks(metadata, seen, final: false, []);
        var cited = new List<ResponsesSearchSource>
        {
            new() { Url = "https://debian.org/", Title = "Replacement" },
            new() { Url = "https://debian.org/releases/?utm_source=openai", Title = "Debian Releases" },
            new() { Url = "https://debian.org/distrib", Title = "Not an exact match" },
            new() { Url = "https://example.org/", Title = "Example" },
            new() { Url = "https://unrelated.org/", Title = "Unrelated" },
        };

        var blocks = AnthropicResponsesWebSearch.Blocks(null, seen, final: true, cited);

        Assert.Equal(2, blocks.Count);
        AssertJsonEqual(
            """[{"type":"web_search_result","url":"https://debian.org/","title":"Original"},{"type":"web_search_result","url":"https://debian.org/releases/","title":"Debian Releases"},{"type":"web_search_result","url":"https://debian.org/distrib/","title":"https://debian.org/distrib/"}]""",
            Encoding.UTF8.GetString(blocks[0].Content!.Raw!));
        AssertJsonEqual(
            """[{"type":"web_search_result","url":"https://example.org/","title":"Example"}]""",
            Encoding.UTF8.GetString(blocks[1].Content!.Raw!));
    }

    [Theory]
    [InlineData("https://example.org/?page=2", "https://example.org/?utm_source=openai&page=2&utm_medium=search", true)]
    [InlineData("https://example.org/?a=1&b=2", "https://example.org/?b=2&a=1", true)]
    [InlineData("https://example.org/?page=2", "https://example.org/?page=3&utm_source=openai", false)]
    [InlineData("https://example.org/", "https://example.org/?source=other", false)]
    [InlineData("https://example.org/#one", "https://example.org/#two", false)]
    [InlineData("https://example.org/one", "https://example.org/two", false)]
    [InlineData("https://example.org/", "https://other.org/", false)]
    [InlineData("https://example.org/", "http://example.org/", false)]
    [InlineData("https://example.org/", "https://example.org/?utm_source=%zz", false)]
    public void TitleUrlIgnoresOnlyKnownTrackingParameters(
        string source,
        string citation,
        bool match)
    {
        Assert.Equal(
            match,
            AnthropicResponsesWebSearch.ResponsesSearchTitleUrl(source)
                == AnthropicResponsesWebSearch.ResponsesSearchTitleUrl(citation));
    }

    [Fact]
    public void BlocksWaitForLateDetails()
    {
        var seen = new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal);
        Assert.Empty(Blocks("""[{"id":"late","status":"in_progress"}]""", seen, final: false));

        var started = Blocks(
            """[{"id":"late","status":"completed","action":{"query":"actual query","sources":[{"url":"https://example.org/one"}]}}]""",
            seen,
            final: false);
        Assert.Equal("server_tool_use", Assert.Single(started).Type);
        Assert.Equal("""{"query":"actual query"}""", started[0].Input!.Value.GetRawText());

        Assert.Empty(Blocks(
            """[{"id":"late","status":"in_progress","action":{"sources":[{"url":"https://example.org/two","title":"Two"},{"url":"https://example.org/one","title":"One"}]}}]""",
            seen,
            final: false));

        var results = AnthropicResponsesWebSearch.Blocks(null, seen, final: true, []);
        Assert.Equal("web_search_tool_result", Assert.Single(results).Type);
        AssertJsonEqual(
            """[{"type":"web_search_result","url":"https://example.org/one","title":"One"},{"type":"web_search_result","url":"https://example.org/two","title":"Two"}]""",
            Encoding.UTF8.GetString(results[0].Content!.Raw!));
        Assert.Empty(AnthropicResponsesWebSearch.Blocks(null, seen, final: true, []));
    }

    [Fact]
    public void BlocksFlushUnknownQueryInCallOrder()
    {
        var seen = new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal);
        Assert.Empty(Blocks(
            """[{"id":"second_alphabetically","status":"completed"},{"id":"first_alphabetically","status":"failed"}]""",
            seen,
            final: false));

        var blocks = AnthropicResponsesWebSearch.Blocks(null, seen, final: true, []);
        Assert.Equal(4, blocks.Count);
        Assert.Equal("srvtoolu_second_alphabetically", blocks[0].Id);
        Assert.Equal("srvtoolu_first_alphabetically", blocks[2].Id);
        Assert.Equal("{}", blocks[0].Input!.Value.GetRawText());
        Assert.Empty(AnthropicResponsesWebSearch.Blocks(null, seen, final: true, []));
    }

    [Fact]
    public void CitationFallbackBoundaries()
    {
        var cited = new List<ResponsesSearchSource>
        {
            new() { Url = "https://example.org/cited", Title = "Cited" },
        };

        AssertFallback("""[{"id":"a","status":"completed"}]""", "https://example.org/cited", 1, cited);
        AssertFallback(
            """[{"id":"a","status":"completed","action":{"sources":[{"url":"https://example.org/explicit","title":"Explicit"}]}}]""",
            "https://example.org/explicit",
            1,
            cited);
        AssertFallback(
            """[{"id":"a","status":"completed"},{"id":"b","status":"completed"}]""",
            string.Empty,
            2,
            cited);
        AssertFallback("[]", string.Empty, 0, cited);
        AssertFallback("""[{"id":"a","status":"failed"}]""", string.Empty, 1, cited);
    }

    [Fact]
    public void CitationsOnlyUseStructuredWebUrls()
    {
        var annotations = new List<Annotation>
        {
            new() { Type = "url_citation", UrlCitation = new UrlCitation { Url = "https://example.org" } },
            new() { Type = "url_citation", UrlCitation = new UrlCitation { Url = "https://example.org", Title = "Example" } },
            new() { Type = "url_citation", UrlCitation = new UrlCitation { Url = "javascript:alert(1)" } },
            new() { Type = "url_citation", UrlCitation = new UrlCitation { Url = "file:///tmp/private" } },
            new() { Type = "url_citation", UrlCitation = new UrlCitation { Url = "/relative" } },
            new() { Type = "file_citation", UrlCitation = new UrlCitation { Url = "https://example.org/wrong-kind" } },
        };

        var sources = AnthropicResponsesWebSearch.AppendCitations([], annotations);

        var source = Assert.Single(sources);
        Assert.Equal("https://example.org", source.Url);
        Assert.Equal("Example", source.Title);
        var again = AnthropicResponsesWebSearch.AppendCitations(sources, annotations);
        Assert.Same(sources, again);
        Assert.Single(again);
    }

    [Fact]
    public void ResponseFromLlmPrependsSearchBlocks()
    {
        var response = new LlmResponse
        {
            Id = "resp_1",
            Model = "gpt-6-luna",
            TransformerMetadata = Metadata(
                """[{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"Debian","sources":[{"type":"url","url":"https://debian.org","title":"Debian"}]}}]"""),
            Choices =
            [
                new Choice
                {
                    Message = new Message { Role = "assistant", Content = MessageContent.FromText("Answer") },
                    FinishReason = "stop",
                },
            ],
        };

        var converted = AnthropicConvertFromLlm.ResponseFromLlm(response);

        Assert.Collection(
            converted.Content!,
            static block =>
            {
                Assert.Equal("server_tool_use", block.Type);
                Assert.Equal("srvtoolu_ws_1", block.Id);
                Assert.Equal("web_search", block.Name);
                Assert.Equal("""{"query":"Debian"}""", block.Input!.Value.GetRawText());
            },
            static block =>
            {
                Assert.Equal("web_search_tool_result", block.Type);
                Assert.Equal("srvtoolu_ws_1", block.ToolUseId);
                AssertJsonEqual(
                    """[{"type":"web_search_result","url":"https://debian.org","title":"Debian"}]""",
                    Encoding.UTF8.GetString(block.Content!.Raw!));
            },
            static block =>
            {
                Assert.Equal("text", block.Type);
                Assert.Equal("Answer", block.Text);
            });
        Assert.Equal("end_turn", converted.StopReason);
    }

    [Fact]
    public void ResponseFromLlmUsesCitationsWhenSourcesOmitted()
    {
        var response = new LlmResponse
        {
            Id = "resp_cited",
            TransformerMetadata = Metadata(
                """[{"id":"ws_cited","type":"web_search_call","status":"completed"}]"""),
            Choices =
            [
                new Choice
                {
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = MessageContent.FromText("Cited answer"),
                        Annotations =
                        [
                            new Annotation
                            {
                                Type = "url_citation",
                                UrlCitation = new UrlCitation
                                {
                                    Url = "https://example.org/cited",
                                    Title = "Cited source",
                                },
                            },
                        ],
                    },
                    FinishReason = "stop",
                },
            ],
        };

        var converted = AnthropicConvertFromLlm.ResponseFromLlm(response);
        var result = converted.Content!.Single(static block => block.Type == "web_search_tool_result");
        AssertJsonEqual(
            """[{"type":"web_search_result","url":"https://example.org/cited","title":"Cited source"}]""",
            Encoding.UTF8.GetString(result.Content!.Raw!));
    }

    [Fact]
    public void StreamProgressIsEmittedOnceAndLateSourcesAreMerged()
    {
        var inProgress = Metadata(
            """[{"id":"ws_late","type":"web_search_call","status":"in_progress"}]""");
        var done = Metadata(
            """[{"id":"ws_late","type":"web_search_call","status":"completed","action":{"type":"search","query":"late query"}}]""");
        var terminal = Metadata(
            """[{"id":"ws_late","type":"web_search_call","status":"completed","action":{"type":"search","sources":[{"type":"url","url":"https://example.org/late","title":"Late source"}]}}]""");

        var chunks = new List<LlmResponse>
        {
            new() { Object = "chat.completion.chunk", TransformerMetadata = inProgress },
            new()
            {
                Object = "chat.completion.chunk",
                TransformerMetadata = done,
            },
            new()
            {
                Object = "chat.completion.chunk",
                TransformerMetadata = terminal,
                Choices =
                [
                    new Choice
                    {
                        Delta = new Message { Content = MessageContent.FromText("Answer") },
                        FinishReason = "stop",
                    },
                ],
            },
            new()
            {
                Object = "chat.completion.chunk",
                Choices = [],
                Usage = new Usage { PromptTokens = 1, CompletionTokens = 1, TotalTokens = 2 },
            },
        };

        var transformer = new AnthropicInboundTransformer();
        using var stream = transformer.TransformStream(new SliceStream<LlmResponse>(chunks));
        var events = GoStreams.All(stream, out var error);
        Assert.Null(error);

        var starts = 0;
        var stops = 0;
        var uses = 0;
        var results = 0;
        var query = new StringBuilder();
        var urls = new List<string>();
        var stopReason = string.Empty;
        foreach (var @event in events)
        {
            using var document = JsonDocument.Parse(@event.Data);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "content_block_start":
                    starts++;
                    var block = root.GetProperty("content_block");
                    switch (block.GetProperty("type").GetString())
                    {
                        case "server_tool_use":
                            uses++;
                            break;
                        case "web_search_tool_result":
                            results++;
                            urls.Add(block.GetProperty("content")[0].GetProperty("url").GetString()!);
                            break;
                    }

                    break;
                case "content_block_delta":
                    if (root.GetProperty("delta").GetProperty("type").GetString()
                        == "input_json_delta")
                    {
                        query.Append(root.GetProperty("delta").GetProperty("partial_json").GetString());
                    }

                    break;
                case "content_block_stop":
                    stops++;
                    break;
                case "message_delta":
                    stopReason = root.GetProperty("delta").GetProperty("stop_reason").GetString()!;
                    break;
            }
        }

        Assert.Equal(1, uses);
        Assert.Equal(1, results);
        Assert.Equal(3, starts);
        Assert.Equal(starts, stops);
        Assert.Equal("late query", JsonDocument.Parse(query.ToString()).RootElement.GetProperty("query").GetString());
        Assert.Equal(["https://example.org/late"], urls);
        Assert.Equal("end_turn", stopReason);
    }

    [Fact]
    public void MetadataSnapshotsMergeSparseCallsById()
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        ResponsesOutboundConvert.AppendResponseWebSearchCallMetadata(
            metadata,
            new ResponsesItem
            {
                Id = "ws_1",
                Type = "web_search_call",
                Status = "completed",
                Action = ResponsesItemAction.NewWebSearchAction(
                    new ResponsesWebSearchAction
                    {
                        Type = "search",
                        Query = "actual query",
                        Sources =
                        [
                            new ResponsesWebSearchSource
                            {
                                Url = "https://example.org/one",
                                Title = "One",
                            },
                        ],
                    }),
            });

        using var before = JsonDocument.Parse(
            metadata[ResponsesKeys.WebSearchCallsTransformerMetadataKey].GetRawText());
        Assert.Single(
            before.RootElement.EnumerateArray().ToArray()[0]
                .GetProperty("action").GetProperty("sources").EnumerateArray());

        ResponsesOutboundConvert.AppendResponseWebSearchCallMetadata(
            metadata,
            new ResponsesItem
            {
                Id = "ws_1",
                Type = "web_search_call",
                Status = "in_progress",
                Action = ResponsesItemAction.NewWebSearchAction(
                    new ResponsesWebSearchAction
                    {
                        Sources =
                        [
                            new ResponsesWebSearchSource
                            {
                                Url = "https://example.org/two",
                                Title = "Two",
                            },
                        ],
                    }),
            });

        using var after = JsonDocument.Parse(
            metadata[ResponsesKeys.WebSearchCallsTransformerMetadataKey].GetRawText());
        var call = Assert.Single(after.RootElement.EnumerateArray().ToArray());
        Assert.Equal("completed", call.GetProperty("status").GetString());
        var action = call.GetProperty("action");
        Assert.Equal("search", action.GetProperty("type").GetString());
        Assert.Equal("actual query", action.GetProperty("query").GetString());
        Assert.Equal(2, action.GetProperty("sources").GetArrayLength());
    }

    private static void AssertJsonEqual(string expected, string actual)
        => Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            "expected " + expected + " but got " + actual);

    private static void AssertFallback(
        string raw,
        string wantUrl,
        int wantResults,
        List<ResponsesSearchSource> cited)
    {
        var blocks = AnthropicResponsesWebSearch.Blocks(
            Metadata(raw),
            new Dictionary<string, ResponsesSearchState>(StringComparer.Ordinal),
            final: true,
            cited);
        var count = 0;
        foreach (var block in blocks)
        {
            if (block.Type != "web_search_tool_result")
            {
                continue;
            }

            count++;
            using var document = JsonDocument.Parse(block.Content!.Raw!);
            var root = document.RootElement;
            if (wantUrl.Length > 0)
            {
                Assert.Equal(wantUrl, root[0].GetProperty("url").GetString());
            }

            if (raw.Contains("\"failed\"", StringComparison.Ordinal))
            {
                Assert.Equal(
                    "web_search_tool_result_error",
                    root.GetProperty("type").GetString());
            }
        }

        Assert.Equal(wantResults, count);
    }

    private static List<AnthropicMessageContentBlock> Blocks(
        string raw,
        Dictionary<string, ResponsesSearchState> seen,
        bool final = true)
        => AnthropicResponsesWebSearch.Blocks(Metadata(raw), seen, final, []);

    private static Dictionary<string, JsonElement> Metadata(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [AnthropicResponsesWebSearch.CallsMetadataKey] = document.RootElement.Clone(),
        };
    }
}
