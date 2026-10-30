using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Anthropic Messages inbound slice
/// (llm/transformer/anthropic/inbound.go, inbound_convert.go, usage.go).
/// </summary>
public sealed class AnthropicInboundTransformerTests
{
    private const string Messages = "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]";

    private const string Base = "\"model\":\"claude\",\"max_tokens\":1";

    private static readonly AnthropicInboundTransformer Transformer = new();

    private static readonly string[] WebSearchCallIds = ["call-1"];

    // ------------------------------------------------------------------
    // Request conversion (convertToLLMRequest)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertsStringSystemAndBasicFields()
    {
        var request = Transform(
            "{\"model\":\"claude-3\",\"max_tokens\":128,\"temperature\":0.2,\"top_p\":0.9,"
            + "\"service_tier\":\"standard\",\"system\":\"be concise\","
            + "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}");

        Assert.Equal("claude-3", request.Model);
        Assert.Equal(128, request.MaxTokens);
        Assert.Equal(0.2, request.Temperature);
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(RequestTypes.Chat, request.RequestType);
        Assert.Equal(ApiFormats.AnthropicMessage, request.ApiFormat);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("be concise", request.Messages[0].Content!.Content);
        Assert.Equal("user", request.Messages[1].Role);
        Assert.Equal("hello", request.Messages[1].Content!.Content);
    }

    [Fact]
    public void ConvertsSystemArrayWithCacheControl()
    {
        var request = Transform(
            "{" + Base + "," + Messages + ","
            + "\"system\":["
            + "{\"type\":\"text\",\"text\":\"a\",\"cache_control\":{\"type\":\"ephemeral\",\"ttl\":\"5m\"}},"
            + "{\"type\":\"text\",\"text\":\"b\"}]}");

        Assert.True(request.TransformOptions!.ArrayInstructions);
        Assert.Equal(3, request.Messages.Count);
        Assert.Equal("a", request.Messages[0].Content!.Content);
        Assert.Equal("ephemeral", request.Messages[0].CacheControl!.Type);
        Assert.Equal("5m", request.Messages[0].CacheControl!.Ttl);
        Assert.Equal("b", request.Messages[1].Content!.Content);
        Assert.Null(request.Messages[1].CacheControl);
    }

    [Fact]
    public void ConvertsUserMetadataAndTopLevelCacheControl()
    {
        var request = Transform(
            "{" + Base + "," + Messages + ","
            + "\"metadata\":{\"user_id\":\"u-1\"},"
            + "\"cache_control\":{\"type\":\"ephemeral\"}}");

        Assert.Equal("u-1", request.Metadata!["user_id"]);
        Assert.True(request.TransformerMetadata!.ContainsKey("anthropic_cache_control"));
        var cacheControl = request.TransformerMetadata!["anthropic_cache_control"];
        Assert.Equal("ephemeral", cacheControl.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":4000}", "low", 4000L)]
    [InlineData("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":10000}", "medium", 10000L)]
    [InlineData("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":20000}", "high", 20000L)]
    [InlineData("\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":40000}", "xhigh", 40000L)]
    [InlineData("\"thinking\":{\"type\":\"enabled\"}", "medium", null)]
    public void MapsThinkingBudgetToReasoningEffort(string thinking, string effort, long? budget)
    {
        var request = Transform("{" + Base + "," + Messages + "," + thinking + "}");

        Assert.Equal(effort, request.ReasoningEffort);
        Assert.Equal(budget, request.ReasoningBudget);
        Assert.Equal("enabled", request.TransformerMetadata!["thinking_type"].GetString());
    }

    [Fact]
    public void MapsDisabledAndAdaptiveThinking()
    {
        var disabled = Transform("{" + Base + "," + Messages + ",\"thinking\":{\"type\":\"disabled\"}}");
        Assert.Equal("none", disabled.ReasoningEffort);
        Assert.Equal("disabled", disabled.TransformerMetadata!["thinking_type"].GetString());

        var adaptive = Transform(
            "{" + Base + "," + Messages + ","
            + "\"thinking\":{\"type\":\"adaptive\",\"display\":\"omitted\"},"
            + "\"output_config\":{\"effort\":\"xhigh\"}}");
        Assert.Equal("xhigh", adaptive.ReasoningEffort);
        Assert.Equal("adaptive", adaptive.TransformerMetadata!["thinking_type"].GetString());
        Assert.Equal("omitted", adaptive.TransformerMetadata["thinking_display"].GetString());
        Assert.Equal("xhigh", adaptive.TransformerMetadata["output_config_effort"].GetString());
    }

    [Fact]
    public void MapsToolChoiceAndParallelFlag()
    {
        var auto = Transform("{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":\"auto\"}}");
        Assert.Equal("auto", auto.ToolChoice!.ToolChoiceMode);

        var none = Transform("{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":\"none\"}}");
        Assert.Equal("none", none.ToolChoice!.ToolChoiceMode);

        var any = Transform(
            "{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":\"any\",\"disable_parallel_tool_use\":true}}");
        Assert.Equal("required", any.ToolChoice!.ToolChoiceMode);
        Assert.False(any.ParallelToolCalls);

        var named = Transform(
            "{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":\"tool\",\"name\":\"f\"}}");
        Assert.Equal("function", named.ToolChoice!.NamedToolChoice!.Type);
        Assert.Equal("f", named.ToolChoice.NamedToolChoice.Function.Name);
    }

    [Fact]
    public void ConvertsFunctionAndWebSearchTools()
    {
        var request = Transform(
            "{" + Base + "," + Messages + ",\"tools\":["
            + "{\"name\":\"f\",\"description\":\"d\",\"input_schema\":{\"type\":\"object\"},\"strict\":true},"
            + "{\"type\":\"web_search_20250305\",\"name\":\"web_search\",\"max_uses\":3,"
            + "\"allowed_domains\":[\"example.com\"],"
            + "\"user_location\":{\"city\":\"Paris\",\"type\":\"approximate\"}},"
            + "{\"type\":\"unsupported\",\"name\":\"x\"}]}");

        Assert.Equal(2, request.Tools!.Count);
        Assert.Equal(ToolTypes.Function, request.Tools[0].Type);
        Assert.Equal("f", request.Tools[0].Function!.Name);
        Assert.True(request.Tools[0].Function!.Strict);
        Assert.Equal("{\"type\":\"object\"}", request.Tools[0].Function!.Parameters!.Value.GetRawText());

        Assert.Equal(ToolTypes.WebSearch, request.Tools[1].Type);
        Assert.Equal(3L, request.Tools[1].WebSearch!.MaxUses);
        Assert.Equal(["example.com"], request.Tools[1].WebSearch!.AllowedDomains);
        Assert.Equal("Paris", request.Tools[1].WebSearch!.UserLocation!.City);
        Assert.Equal("approximate", request.Tools[1].WebSearch!.UserLocation!.Type);
    }

    /// <summary>
    /// Accepted difference C051: Go's json.RawMessage keeps the literal
    /// "null" (so the OpenAI outbound body carries "parameters":null), while
    /// the port's JsonElement? collapses an explicit null into "absent".
    /// </summary>
    [Fact]
    public void ExplicitNullInputSchemaCollapsesToAbsent()
    {
        var request = Transform(
            "{" + Base + "," + Messages + ",\"tools\":[{\"name\":\"t\",\"input_schema\":null}]}");

        var tool = Assert.Single(request.Tools!);
        Assert.Equal(ToolTypes.Function, tool.Type);
        Assert.Null(tool.Function!.Parameters);
    }

    [Fact]
    public void ConvertsStopSequences()
    {
        var single = Transform(
            "{" + Base + "," + Messages + ",\"stop_sequences\":[\"END\"]}");
        Assert.Equal("END", single.Stop!.Single);
        Assert.Null(single.Stop.Multiple);

        var multiple = Transform(
            "{" + Base + "," + Messages + ",\"stop_sequences\":[\"END\",\"STOP\"]}");
        Assert.Equal(["END", "STOP"], multiple.Stop!.Multiple);
    }

    [Fact]
    public void CollapsesSingleTextBlockAndLiftsCacheControl()
    {
        var request = Transform(
            "{" + Base + "," + Messages + ",\"messages\":[{\"role\":\"user\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"only\",\"cache_control\":{\"type\":\"ephemeral\"}}]}]}");

        var message = Assert.Single(request.Messages);
        Assert.Equal("only", message.Content!.Content);
        Assert.Null(message.Content.MultipleContent);
        Assert.Equal("ephemeral", message.CacheControl!.Type);
    }

    [Fact]
    public void ConvertsTextImageToolUseAndToolResultBlocks()
    {
        var request = Transform(
            "{" + Base + ",\"messages\":[{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"look\"},"
            + "{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AAA\"}},"
            + "{\"type\":\"image\",\"source\":{\"type\":\"url\",\"url\":\"https://x/y.png\"}},"
            + "{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"f\",\"input\":{\"a\":1}}"
            + "]},{\"role\":\"user\",\"content\":["
            + "{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_1\",\"is_error\":true,"
            + "\"content\":[{\"type\":\"text\",\"text\":\"boom\"}]}]}]}");

        var assistant = request.Messages[0];
        Assert.Equal(3, assistant.Content!.MultipleContent!.Count);
        Assert.Equal("text", assistant.Content.MultipleContent[0].Type);

        var base64 = assistant.Content.MultipleContent[1];
        Assert.Equal("data:image/png;base64,AAA", base64.ImageUrl!.Url);

        var url = assistant.Content.MultipleContent[2];
        Assert.Equal("https://x/y.png", url.ImageUrl!.Url);

        var toolCall = Assert.Single(assistant.ToolCalls!);
        Assert.Equal("toolu_1", toolCall.Id);
        Assert.Equal("f", toolCall.Function.Name);
        Assert.Equal("{\"a\":1}", toolCall.Function.Arguments);

        var toolMessage = request.Messages[1];
        Assert.Equal("tool", toolMessage.Role);
        Assert.Equal("toolu_1", toolMessage.ToolCallId);
        Assert.True(toolMessage.ToolCallIsError);
        // MessageIndex is the zero-based request message carrying the result.
        Assert.Equal(1, toolMessage.MessageIndex);
        // Tool-result arrays stay as parts (the reference keeps MultipleContent
        // for tool results; only request-message text collapses to a string).
        Assert.Null(toolMessage.Content!.Content);
        Assert.Equal("boom", Assert.Single(toolMessage.Content.MultipleContent!).Text);
    }

    [Fact]
    public void ConvertsThinkingAndRedactedBlocksToReasoning()
    {
        var request = Transform(
            "{" + Base + ",\"messages\":[{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"thinking\",\"thinking\":\"a\",\"signature\":\"s1\"},"
            + "{\"type\":\"redacted_thinking\",\"data\":\"enc\"},"
            + "{\"type\":\"thinking\",\"thinking\":\"b\"},"
            + "{\"type\":\"text\",\"text\":\"answer\"}]}]}");

        var message = Assert.Single(request.Messages);
        Assert.Equal(2, message.ReasoningItems!.Count);
        Assert.Equal("a", message.ReasoningItems[0].Content);
        Assert.Equal("s1", message.ReasoningItems[0].Signature);
        Assert.Equal("b", message.ReasoningItems[1].Content);
        Assert.Null(message.ReasoningItems[1].Signature);
        Assert.Equal("ab", message.ReasoningContent);
        // The scalar fallback keeps only the LAST reasoning item's signature,
        // and the last block here has none (reference: reasoningItems[len-1]).
        Assert.Null(message.ReasoningSignature);
        Assert.Equal("enc", message.RedactedReasoningContent);
    }

    [Fact]
    public void ConvertsSpecialToolUseAndResultBlocksInline()
    {
        var request = Transform(
            "{" + Base + ",\"messages\":[{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"web_search_tool_use\",\"id\":\"srvtoolu_1\",\"name\":\"web_search\","
            + "\"input\":{\"query\":\"x\"},\"caller\":{\"type\":\"direct\"}},"
            + "{\"type\":\"web_search_tool_result\",\"tool_use_id\":\"srvtoolu_1\","
            + "\"content\":[{\"type\":\"web_search_result\",\"url\":\"https://x\"}]}"
            + "]}]}");

        var message = Assert.Single(request.Messages);
        Assert.Equal(0, message.MessageIndex);

        var toolCall = Assert.Single(message.ToolCalls!);
        Assert.Equal("web_search_tool_use", toolCall.TransformerMetadata!["anthropic_type"].GetString());
        Assert.Equal("direct", toolCall.TransformerMetadata["anthropic_caller"].GetProperty("type").GetString());

        var inline = Assert.Single(message.InlineToolResults!);
        Assert.Equal("srvtoolu_1", inline.ToolCallId);
        Assert.Contains("web_search_result", inline.Output, StringComparison.Ordinal);
        Assert.Equal("web_search_tool_result", inline.TransformerMetadata!["anthropic_type"].GetString());
    }

    // ------------------------------------------------------------------
    // Validation (inbound.go:54-118)
    // ------------------------------------------------------------------

    public static TheoryData<string, string> ValidationErrors => new()
    {
        { "{\"max_tokens\":1," + Messages + "}", "invalid request: model is required" },
        { "{\"model\":\"m\",\"max_tokens\":1}", "invalid request: messages are required" },
        { "{\"model\":\"m\"," + Messages + "}", "invalid request: max_tokens is required and must be positive" },
        { "{\"model\":\"m\",\"max_tokens\":0," + Messages + "}", "invalid request: max_tokens is required and must be positive" },
        {
            "{" + Base + "," + Messages + ",\"system\":[{\"type\":\"image\"}]}",
            "invalid request: system prompt must be text"
        },
        {
            "{" + Base + "," + Messages + ",\"thinking\":{\"type\":\"weird\"}}",
            "invalid request: thinking.type must be one of: enabled, disabled, adaptive"
        },
        {
            "{" + Base + "," + Messages + ",\"thinking\":{\"type\":\"enabled\",\"budget_tokens\":-1}}",
            "invalid request: budget_tokens must be positive when thinking type is enabled"
        },
        {
            "{" + Base + "," + Messages + ",\"thinking\":{\"type\":\"adaptive\"},\"output_config\":{\"effort\":\"nope\"}}",
            "invalid request: output_config.effort must be one of: low, medium, high, xhigh, max"
        },
        {
            "{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":\"weird\"}}",
            "invalid request: tool_choice.type must be one of: auto, none, any, tool"
        },
        {
            "{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":\"tool\"}}",
            "invalid request: tool_choice.name is required when type is tool"
        },
    };

    [Theory]
    [MemberData(nameof(ValidationErrors))]
    public void ReportsReferenceValidationErrors(string body, string expected)
        => Assert.Equal(expected, ErrorOf(body));

    [Fact]
    public void AcceptsStreamingRequest()
    {
        var request = Transform("{" + Base + "," + Messages + ",\"stream\":true}");

        Assert.True(request.Stream);
    }

    [Fact]
    public void RejectsNonJsonContentType()
    {
        var ex = Assert.Throws<TransformerException>(() => Transformer.TransformRequest(new HttpRequestData
        {
            Method = "POST",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["text/plain"],
            },
            Body = Encoding.UTF8.GetBytes("{" + Base + "," + Messages + "}"),
        }));

        Assert.Equal("invalid request: unsupported content type: text/plain", ex.Message);
    }

    [Fact]
    public void RejectsEmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(() => Transformer.TransformRequest(new HttpRequestData
        {
            Method = "POST",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = [],
        }));

        Assert.Equal("invalid request: request body is empty", ex.Message);
    }

    // ------------------------------------------------------------------
    // Decode errors (encoding/json, C003)
    // ------------------------------------------------------------------

    private const string DecodePrefix = "invalid request: failed to decode anthropic request: ";

    public static TheoryData<string, string> GoDecodeErrors => new()
    {
        // Non-object roots report "Go value of type".
        { "[]", Decode("json: cannot unmarshal array into Go value of type anthropic.MessageRequest") },
        { "\"x\"", Decode("json: cannot unmarshal string into Go value of type anthropic.MessageRequest") },
        { "5", Decode("json: cannot unmarshal number into Go value of type anthropic.MessageRequest") },
        { "true", Decode("json: cannot unmarshal bool into Go value of type anthropic.MessageRequest") },

        // Root fields.
        { "{\"model\":5," + Messages + "}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.model of type string") },
        { "{\"model\":\"m\",\"max_tokens\":\"x\"," + Messages + "}", Decode("json: cannot unmarshal string into Go struct field MessageRequest.max_tokens of type int64") },
        { "{\"model\":\"m\",\"max_tokens\":1.5," + Messages + "}", Decode("json: cannot unmarshal number 1.5 into Go struct field MessageRequest.max_tokens of type int64") },
        { "{\"model\":\"m\",\"max_tokens\":99999999999999999999," + Messages + "}", Decode("json: cannot unmarshal number 99999999999999999999 into Go struct field MessageRequest.max_tokens of type int64") },
        { "{" + Base + "," + Messages + ",\"temperature\":true}", Decode("json: cannot unmarshal bool into Go struct field MessageRequest.temperature of type float64") },
        { "{" + Base + "," + Messages + ",\"top_k\":1.5}", Decode("json: cannot unmarshal number 1.5 into Go struct field MessageRequest.top_k of type int64") },
        { "{" + Base + "," + Messages + ",\"top_p\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field MessageRequest.top_p of type float64") },
        { "{" + Base + "," + Messages + ",\"stream\":1}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.stream of type bool") },
        { "{" + Base + "," + Messages + ",\"stop_sequences\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field MessageRequest.stop_sequences of type []string") },
        { "{" + Base + "," + Messages + ",\"stop_sequences\":[5]}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.stop_sequences of type string") },
        { "{" + Base + "," + Messages + ",\"anthropic_beta\":[5]}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.anthropic_beta of type string") },
        { "{" + Base + "," + Messages + ",\"metadata\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field MessageRequest.metadata of type anthropic.AnthropicMetadata") },
        { "{" + Base + "," + Messages + ",\"metadata\":{\"user_id\":5}}", Decode("json: cannot unmarshal number into Go struct field AnthropicMetadata.metadata.user_id of type string") },
        { "{" + Base + "," + Messages + ",\"cache_control\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field MessageRequest.cache_control of type anthropic.CacheControl") },
        { "{" + Base + "," + Messages + ",\"cache_control\":{\"ttl\":5}}", Decode("json: cannot unmarshal number into Go struct field CacheControl.cache_control.ttl of type string") },

        // messages.
        { "{\"model\":\"m\",\"max_tokens\":1,\"messages\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field MessageRequest.messages of type []anthropic.MessageParam") },
        { "{\"model\":\"m\",\"max_tokens\":1,\"messages\":{}}", Decode("json: cannot unmarshal object into Go struct field MessageRequest.messages of type []anthropic.MessageParam") },
        { "{\"model\":\"m\",\"max_tokens\":1,\"messages\":[5]}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.messages of type anthropic.MessageParam") },
        { "{\"model\":\"m\",\"max_tokens\":1,\"messages\":[{\"role\":1,\"content\":\"x\"}]}", Decode("json: cannot unmarshal number into Go struct field MessageParam.messages.role of type string") },

        // content (MessageContent.UnmarshalJSON sentinels).
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":null}]}", Decode("content cannot be null") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":5}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":{\"a\":1}}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[5]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":5}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":5}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"citations\":5}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"citations\":[{\"url\":5}]}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"source\":5}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"source\":{\"data\":5}}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":5}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":[{\"text\":5}]}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"is_error\":5}]}]}", Decode("invalid content type") },
        { "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"cache_control\":5}]}]}", Decode("invalid content type") },

        // system (SystemPrompt.UnmarshalJSON sentinel).
        { "{" + Base + "," + Messages + ",\"system\":5}", Decode("invalid system prompt format") },
        { "{" + Base + "," + Messages + ",\"system\":{}}", Decode("invalid system prompt format") },
        { "{" + Base + "," + Messages + ",\"system\":[{\"type\":5}]}", Decode("invalid system prompt format") },
        { "{" + Base + "," + Messages + ",\"system\":[{\"type\":\"text\",\"cache_control\":5}]}", Decode("invalid system prompt format") },

        // thinking / output_config.
        { "{" + Base + "," + Messages + ",\"thinking\":{\"type\":5}}", Decode("json: cannot unmarshal number into Go struct field Thinking.thinking.type of type string") },
        { "{" + Base + "," + Messages + ",\"thinking\":{\"budget_tokens\":\"x\"}}", Decode("json: cannot unmarshal string into Go struct field Thinking.thinking.budget_tokens of type int64") },
        { "{" + Base + "," + Messages + ",\"thinking\":{\"display\":5}}", Decode("json: cannot unmarshal number into Go struct field Thinking.thinking.display of type string") },
        { "{" + Base + "," + Messages + ",\"output_config\":{\"effort\":5}}", Decode("json: cannot unmarshal number into Go struct field OutputConfig.output_config.effort of type string") },

        // tools.
        { "{" + Base + "," + Messages + ",\"tools\":5}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.tools of type []anthropic.Tool") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"name\":5}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.name of type string") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"max_uses\":\"x\"}]}", Decode("json: cannot unmarshal string into Go struct field Tool.tools.max_uses of type int64") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"strict\":5}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.strict of type bool") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"allowed_domains\":[5]}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.allowed_domains of type string") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"blocked_domains\":5}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.blocked_domains of type []string") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"user_location\":5}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.user_location of type anthropic.WebSearchToolUserLocation") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"user_location\":{\"city\":5}}]}", Decode("json: cannot unmarshal number into Go struct field WebSearchToolUserLocation.tools.user_location.city of type string") },
        { "{" + Base + "," + Messages + ",\"tools\":[{\"cache_control\":5}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.cache_control of type anthropic.CacheControl") },

        // tool_choice.
        { "{" + Base + "," + Messages + ",\"tool_choice\":5}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.tool_choice of type anthropic.ToolChoice") },
        { "{" + Base + "," + Messages + ",\"tool_choice\":{\"type\":5}}", Decode("json: cannot unmarshal number into Go struct field ToolChoice.tool_choice.type of type string") },
        { "{" + Base + "," + Messages + ",\"tool_choice\":{\"disable_parallel_tool_use\":5}}", Decode("json: cannot unmarshal number into Go struct field ToolChoice.tool_choice.disable_parallel_tool_use of type bool") },
        { "{" + Base + "," + Messages + ",\"tool_choice\":{\"name\":5}}", Decode("json: cannot unmarshal number into Go struct field ToolChoice.tool_choice.name of type string") },

        // Precedence: the first saved type error wins, but a hard UnmarshalJSON
        // error aborts the decode and overrides it in either document order.
        {
            "{\"thinking\":{\"budget_tokens\":\"x\"},\"messages\":[{\"role\":\"user\",\"content\":null}],\"model\":\"m\",\"max_tokens\":1}",
            Decode("content cannot be null")
        },
        {
            "{\"messages\":[{\"role\":\"user\",\"content\":null}],\"thinking\":{\"budget_tokens\":\"x\"},\"model\":\"m\",\"max_tokens\":1}",
            Decode("content cannot be null")
        },
        { "{\"model\":5,\"messages\":[{\"role\":6,\"content\":\"x\"}]}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.model of type string") },
        { "{\"messages\":[{\"role\":6,\"content\":\"x\"}],\"model\":5}", Decode("json: cannot unmarshal number into Go struct field MessageParam.messages.role of type string") },

        // Go matches field names case-insensitively (to the tag name) and
        // decodes duplicate keys in input order.
        { "{\"MODEL\":5," + Messages + "}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.model of type string") },
        { "{\"model\":\"m\",\"model\":5," + Messages + "}", Decode("json: cannot unmarshal number into Go struct field MessageRequest.model of type string") },
    };

    public static TheoryData<string> GoAccepted => new()
    {
        // null roots leave the zero struct, then fail validation.
        "null",
        // null values are Go zero values everywhere.
        "{" + Base + "," + Messages + ",\"system\":null}",
        "{" + Base + "," + Messages + ",\"system\":[]}",
        "{" + Base + "," + Messages + ",\"system\":[{\"type\":\"text\",\"text\":\"x\"}]}",
        "{" + Base + "," + Messages + ",\"stop_sequences\":[null]}",
        "{" + Base + ",\"messages\":[null]}",
        "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[null]}]}",
        "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[]}]}",
        "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"tool_use\",\"input\":[1,2]}]}]}",
        "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":null}]}]}",
        "{" + Base + "," + Messages + ",\"tools\":[null]}",
        "{" + Base + "," + Messages + ",\"tools\":[{\"name\":\"t\",\"input_schema\":null}]}",
        "{" + Base + "," + Messages + ",\"tools\":[{\"name\":\"t\",\"input_schema\":[1]}]}",
        "{" + Base + "," + Messages + ",\"unknown\":{\"role\":5}}",
        "{" + Base + ",\"system\":[{\"type\":\"text\",\"text\":\"s\"}],\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"temperature\":0.5,\"top_k\":3}",
        // Field names fold case-insensitively inside content blocks too.
        "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"TYPE\":\"text\",\"TEXT\":\"x\"}]}]}",
    };

    [Theory]
    [MemberData(nameof(GoDecodeErrors))]
    public void ReportsGoDecodeErrors(string body, string expected)
        => Assert.Equal(expected, ErrorOf(body));

    [Theory]
    [MemberData(nameof(GoAccepted))]
    public void AcceptsDocumentsGoAccepts(string body)
    {
        try
        {
            _ = Transform(body);
        }
        catch (TransformerException ex)
        {
            // Decoding succeeded; the document may still fail the required-field
            // checks that run afterwards, exactly as in the reference.
            Assert.True(
                ex.Message is "invalid request: model is required"
                    or "invalid request: messages are required"
                    or "invalid request: max_tokens is required and must be positive",
                ex.Message);
        }
    }

    [Fact]
    public void CaseInsensitiveBlockFieldsDecodeLikeGo()
    {
        var request = Transform(
            "{" + Base + ",\"messages\":[{\"role\":\"user\",\"content\":[{\"TYPE\":\"text\",\"TEXT\":\"x\"}]}]}");

        var message = Assert.Single(request.Messages);
        // A single text block collapses to the bare-string form.
        Assert.Equal("x", message.Content!.Content);
    }

    [Fact]
    public void NullMaxTokensFallsIntoTheRequiredFieldCheck()
    {
        Assert.Equal(
            "invalid request: max_tokens is required and must be positive",
            ErrorOf("{\"model\":\"m\",\"max_tokens\":null," + Messages + "}"));
    }

    private static string Decode(string detail) => DecodePrefix + detail;

    // ------------------------------------------------------------------
    // Response conversion (convertToAnthropicResponse)
    // ------------------------------------------------------------------

    [Fact]
    public void ConvertsTextResponse()
    {
        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_1",
            Model = "claude-3",
            Choices =
            [
                new Choice
                {
                    Message = new Message { Role = "assistant", Content = MessageContent.FromText("hi") },
                    FinishReason = "stop",
                },
            ],
            Usage = new Usage { PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15 },
        });

        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;

        Assert.Equal("msg_1", root.GetProperty("id").GetString());
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("claude-3", root.GetProperty("model").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.False(root.TryGetProperty("stop_sequence", out _));

        var block = root.GetProperty("content")[0];
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("hi", block.GetProperty("text").GetString());

        var usage = root.GetProperty("usage");
        Assert.Equal(10, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(5, usage.GetProperty("output_tokens").GetInt64());
        Assert.Equal(0, usage.GetProperty("cache_creation_input_tokens").GetInt64());
        Assert.Equal(0, usage.GetProperty("cache_read_input_tokens").GetInt64());
        Assert.True(usage.TryGetProperty("cache_creation", out _));
        Assert.False(usage.TryGetProperty("cost", out _));
    }

    [Fact]
    public void ConvertsToolCallsAndDefaultsStopReasonToToolUse()
    {
        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_2",
            Choices =
            [
                new Choice
                {
                    Message = new Message
                    {
                        Content = MessageContent.FromText("calling"),
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "toolu_1",
                                Function = new FunctionCall { Name = "f", Arguments = "{\"a\":1}" },
                            },
                        ],
                    },
                },
            ],
        });

        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;

        Assert.Equal("tool_use", root.GetProperty("stop_reason").GetString());
        var content = root.GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("tool_use", content[1].GetProperty("type").GetString());
        Assert.Equal("toolu_1", content[1].GetProperty("id").GetString());
        Assert.Equal("f", content[1].GetProperty("name").GetString());
        Assert.Equal(1, content[1].GetProperty("input").GetProperty("a").GetInt32());
    }

    [Fact]
    public void ConvertsThinkingBlocksBeforeText()
    {
        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_3",
            Choices =
            [
                new Choice
                {
                    Message = new Message
                    {
                        ReasoningContent = "ponder",
                        ReasoningSignature = "sig",
                        Content = MessageContent.FromText("done"),
                    },
                    FinishReason = "length",
                },
            ],
        });

        using var body = JsonDocument.Parse(response.Body);
        var content = body.RootElement.GetProperty("content");

        Assert.Equal("thinking", content[0].GetProperty("type").GetString());
        Assert.Equal("ponder", content[0].GetProperty("thinking").GetString());
        Assert.Equal("sig", content[0].GetProperty("signature").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Equal("max_tokens", body.RootElement.GetProperty("stop_reason").GetString());
    }

    [Fact]
    public void OrdersBlocksByAnthropicBlockIndexMetadata()
    {
        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_4",
            Choices =
            [
                new Choice
                {
                    Message = new Message
                    {
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "t0",
                                Function = new FunctionCall { Name = "f", Arguments = "{}" },
                                TransformerMetadata = Metadata(("anthropic_block_index", 0)),
                            },
                        ],
                        InlineToolResults =
                        [
                            new InlineToolResult
                            {
                                ToolCallId = "t0",
                                TransformerMetadata = Metadata(
                                    ("anthropic_type", "web_search_tool_result"),
                                    ("anthropic_block_index", 1)),
                            },
                        ],
                    },
                },
            ],
        });

        using var body = JsonDocument.Parse(response.Body);
        var content = body.RootElement.GetProperty("content");
        Assert.Equal("tool_use", content[0].GetProperty("type").GetString());
        Assert.Equal("web_search_tool_result", content[1].GetProperty("type").GetString());
        Assert.Equal("t0", content[1].GetProperty("tool_use_id").GetString());
    }

    [Fact]
    public void ProviderResponseContentReplacesConvertedBlocks()
    {
        JsonElement raw;
        using (var document = JsonDocument.Parse("[{\"type\":\"text\",\"text\":\"provider\"}]"))
        {
            raw = document.RootElement.Clone();
        }

        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_5",
            Choices =
            [
                new Choice
                {
                    Message = new Message { Content = MessageContent.FromText("converted") },
                },
            ],
            TransformerMetadata = Metadata(("anthropic_response_content", raw)),
        });

        using var body = JsonDocument.Parse(response.Body);
        var content = body.RootElement.GetProperty("content");
        Assert.Equal("provider", Assert.Single(content.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public void AttachesCitationsToFirstTextBlockWithWebSearchType()
    {
        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_6",
            Choices =
            [
                new Choice
                {
                    Message = new Message
                    {
                        Content = MessageContent.FromText("answer"),
                        Annotations =
                        [
                            new Annotation
                            {
                                Type = "url_citation",
                                UrlCitation = new UrlCitation { Url = "https://x", Title = "X" },
                            },
                        ],
                    },
                },
            ],
            TransformerMetadata = Metadata(
                ("openai_responses_web_search_calls", JsonSerializer.SerializeToElement(WebSearchCallIds))),
        });

        using var body = JsonDocument.Parse(response.Body);
        var citation = body.RootElement.GetProperty("content")[0].GetProperty("citations")[0];
        Assert.Equal("web_search_result_location", citation.GetProperty("type").GetString());
        Assert.Equal("https://x", citation.GetProperty("url").GetString());
        Assert.Equal("X", citation.GetProperty("title").GetString());
    }

    [Fact]
    public void SubtractsCachedTokensFromAnthropicInputTokens()
    {
        var response = TransformResponse(new LlmResponse
        {
            Id = "msg_7",
            Choices = [new Choice { Message = new Message { Content = MessageContent.FromText("x") } }],
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 2,
                PromptTokensDetails = new PromptTokensDetails
                {
                    CachedTokens = 3,
                    WriteCachedTokens = 2,
                    WriteCached5MinTokens = 2,
                },
            },
        });

        using var body = JsonDocument.Parse(response.Body);
        var usage = body.RootElement.GetProperty("usage");
        Assert.Equal(5, usage.GetProperty("input_tokens").GetInt64());
        Assert.Equal(3, usage.GetProperty("cache_read_input_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("cache_creation_input_tokens").GetInt64());
        Assert.Equal(2, usage.GetProperty("cache_creation").GetProperty("ephemeral_5m_input_tokens").GetInt64());
    }

    [Fact]
    public void RejectsNullResponse()
        => Assert.Throws<InvalidOperationException>(() => Transformer.TransformResponse(null));

    // ------------------------------------------------------------------
    // Error mapping (inbound.go:153-221)
    // ------------------------------------------------------------------

    [Fact]
    public void MapsNullErrorToInternalServerError()
    {
        var response = Transformer.TransformError(null);

        Assert.Equal(500, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        Assert.Equal("error", root.GetProperty("type").GetString());
        Assert.Equal(string.Empty, root.GetProperty("request_id").GetString());
        Assert.Equal(string.Empty, root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("internal server error", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void MapsInvalidModelToUnprocessableEntity()
    {
        var response = Transformer.TransformError(TransformerErrors.InvalidModel("m"));

        Assert.Equal(422, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        Assert.Equal("invalid_model_error", root.GetProperty("type").GetString());
        Assert.Equal(string.Empty, root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("model not found: m", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void MapsResponseErrorWithItsDetail()
    {
        var response = Transformer.TransformError(new ResponseError
        {
            StatusCode = 429,
            Detail = new ErrorDetail
            {
                Type = "rate_limit_error",
                Message = "slow down",
                RequestId = "req-1",
            },
        });

        Assert.Equal(429, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        Assert.Equal("rate_limit_error", root.GetProperty("type").GetString());
        Assert.Equal("req-1", root.GetProperty("request_id").GetString());
        Assert.Equal("rate_limit_error", root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("slow down", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void PassesTransportErrorsThroughUntouched()
    {
        var transportBody = Encoding.UTF8.GetBytes("{\"provider\":\"raw\"}");
        var response = Transformer.TransformError(new HttpClientError(
            "POST", "http://upstream", 503, "503 Service Unavailable", transportBody));

        Assert.Equal(503, response.StatusCode);
        Assert.Equal(transportBody, response.Body);
    }

    [Fact]
    public void MapsInvalidRequestToBadRequest()
    {
        var response = Transformer.TransformError(TransformerErrors.InvalidRequest("bad"));

        Assert.Equal(400, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        Assert.Equal("invalid_request_error", root.GetProperty("type").GetString());
        Assert.Equal("invalid_request_error", root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("invalid request: bad", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void MapsUnknownErrorsToInternalServerError()
    {
        var response = Transformer.TransformError(new InvalidOperationException("boom"));

        Assert.Equal(500, response.StatusCode);
        using var body = JsonDocument.Parse(response.Body);
        var root = body.RootElement;
        Assert.Equal("internal_server_error", root.GetProperty("type").GetString());
        Assert.Equal("internal_server_error", root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("boom", root.GetProperty("error").GetProperty("message").GetString());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static HttpResponseData TransformResponse(LlmResponse response)
        => Transformer.TransformResponse(response);

    private static LlmRequest Transform(string body)
        => Transformer.TransformRequest(new HttpRequestData
        {
            Method = "POST",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        });

    private static string ErrorOf(string body)
    {
        var ex = Assert.Throws<TransformerException>(() => Transform(body));
        return ex.Message;
    }

    private static Dictionary<string, JsonElement> Metadata(params (string Key, object Value)[] values)
    {
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            metadata[key] = value switch
            {
                JsonElement element => element,
                _ => JsonSerializer.SerializeToElement(value),
            };
        }

        return metadata;
    }
}
