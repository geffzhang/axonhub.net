using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks Go's field-level decode errors (difference ledger C003). Every expected
/// string below was captured from the reference implementation with a Go probe,
/// including the saved-error precedence (a failing UnmarshalJSON aborts the
/// decode and overrides an earlier type error).
/// </summary>
public sealed class OpenAiRequestTypeCheckTests
{
    private const string DecodePrefix = "invalid request: failed to decode openai request: ";

    private static readonly OpenAiInboundTransformer Transformer = new();

    public static TheoryData<string, string> GoDecodeErrors => new()
    {
        // Root fields and their Go types.
        { "{\"model\":123,\"messages\":[]}", Decode("json: cannot unmarshal number into Go struct field Request.model of type string") },
        { "{\"model\":\"m\",\"messages\":\"nope\"}", Decode("json: cannot unmarshal string into Go struct field Request.messages of type []openai.Message") },
        { "{\"model\":\"m\",\"messages\":{}}", Decode("json: cannot unmarshal object into Go struct field Request.messages of type []openai.Message") },
        { "{\"model\":\"m\",\"messages\":[5]}", Decode("json: cannot unmarshal number into Go struct field Request.messages of type openai.Message") },
        { "{\"model\":\"m\",\"messages\":[\"x\"]}", Decode("json: cannot unmarshal string into Go struct field Request.messages of type openai.Message") },
        { "{\"model\":\"m\",\"messages\":[],\"temperature\":\"hot\"}", Decode("json: cannot unmarshal string into Go struct field Request.temperature of type float64") },
        { "{\"model\":\"m\",\"messages\":[],\"temperature\":1e999}", Decode("json: cannot unmarshal number 1e999 into Go struct field Request.temperature of type float64") },
        { "{\"model\":\"m\",\"messages\":[],\"stream\":\"yes\"}", Decode("json: cannot unmarshal string into Go struct field Request.stream of type bool") },
        { "{\"model\":\"m\",\"messages\":[],\"reasoning_effort\":5}", Decode("json: cannot unmarshal number into Go struct field Request.reasoning_effort of type string") },
        { "{\"model\":\"m\",\"messages\":[],\"modalities\":\"text\"}", Decode("json: cannot unmarshal string into Go struct field Request.modalities of type []string") },
        { "{\"model\":\"m\",\"messages\":[],\"modalities\":[5]}", Decode("json: cannot unmarshal number into Go struct field Request.modalities of type string") },
        { "{\"model\":\"m\",\"messages\":[],\"stream_options\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field Request.stream_options of type openai.StreamOptions") },
        { "{\"model\":\"m\",\"messages\":[],\"stream_options\":{\"include_usage\":\"x\"}}", Decode("json: cannot unmarshal string into Go struct field StreamOptions.stream_options.include_usage of type bool") },
        { "{\"model\":\"m\",\"messages\":[],\"thinking\":{\"type\":5}}", Decode("json: cannot unmarshal number into Go struct field Thinking.thinking.type of type string") },
        { "{\"model\":\"m\",\"messages\":[],\"response_format\":\"x\"}", Decode("json: cannot unmarshal string into Go struct field Request.response_format of type openai.ResponseFormat") },
        { "{\"model\":\"m\",\"messages\":[],\"response_format\":{\"type\":5}}", Decode("json: cannot unmarshal number into Go struct field ResponseFormat.response_format.type of type string") },

        // Number conversion failures quote the literal, like Go's convertNumber.
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":\"many\"}", Decode("json: cannot unmarshal string into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":1.5}", Decode("json: cannot unmarshal number 1.5 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":1.0}", Decode("json: cannot unmarshal number 1.0 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":1e2}", Decode("json: cannot unmarshal number 1e2 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":1E2}", Decode("json: cannot unmarshal number 1E2 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":1e100}", Decode("json: cannot unmarshal number 1e100 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":9223372036854775808}", Decode("json: cannot unmarshal number 9223372036854775808 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":-9223372036854775809}", Decode("json: cannot unmarshal number -9223372036854775809 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":-0.0}", Decode("json: cannot unmarshal number -0.0 into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":true}", Decode("json: cannot unmarshal bool into Go struct field Request.max_tokens of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"max_tokens\":[]}", Decode("json: cannot unmarshal array into Go struct field Request.max_tokens of type int64") },

        // Maps report the map field and the value type; keys never join the path.
        { "{\"model\":\"m\",\"messages\":[],\"logit_bias\":[]}", Decode("json: cannot unmarshal array into Go struct field Request.logit_bias of type map[string]int64") },
        { "{\"model\":\"m\",\"messages\":[],\"logit_bias\":{\"1\":\"x\"}}", Decode("json: cannot unmarshal string into Go struct field Request.logit_bias of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"logit_bias\":{\"a\":true}}", Decode("json: cannot unmarshal bool into Go struct field Request.logit_bias of type int64") },
        { "{\"model\":\"m\",\"messages\":[],\"metadata\":{\"a\":1}}", Decode("json: cannot unmarshal number into Go struct field Request.metadata of type string") },
        { "{\"model\":\"m\",\"messages\":[],\"metadata\":{\"a\":{}}}", Decode("json: cannot unmarshal object into Go struct field Request.metadata of type string") },

        // Nested structs report the innermost struct name and the tag path.
        { "{\"model\":\"m\",\"messages\":[{\"role\":1,\"content\":\"hi\"}]}", Decode("json: cannot unmarshal number into Go struct field Message.messages.role of type string") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\",\"name\":5}]}", Decode("json: cannot unmarshal number into Go struct field Message.messages.name of type string") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"content\":\"hi\",\"audio\":{\"expires_at\":\"x\"}}]}", Decode("json: cannot unmarshal string into Go struct field OutputAudio.messages.audio.expires_at of type int64") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"content\":\"hi\",\"annotations\":[{\"start_index\":\"x\"}]}]}", Decode("json: cannot unmarshal string into Go struct field Annotation.messages.annotations.start_index of type int64") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"content\":\"hi\",\"annotations\":[{\"url_citation\":{\"url\":5}}]}]}", Decode("json: cannot unmarshal number into Go struct field URLCitation.messages.annotations.url_citation.url of type string") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"tool_calls\":[{\"id\":\"1\",\"type\":\"function\",\"function\":\"x\"}]}]}", Decode("json: cannot unmarshal string into Go struct field ToolCall.messages.tool_calls.function of type openai.FunctionCall") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"content\":\"hi\",\"tool_calls\":[{\"id\":\"1\",\"type\":\"function\",\"function\":{\"name\":\"f\",\"arguments\":\"{}\"},\"index\":\"x\"}]}]}", Decode("json: cannot unmarshal string into Go struct field ToolCall.messages.tool_calls.index of type int") },
        { "{\"model\":\"m\",\"messages\":[],\"tools\":{}}", Decode("json: cannot unmarshal object into Go struct field Request.tools of type []openai.Tool") },
        { "{\"model\":\"m\",\"messages\":[],\"tools\":[{\"type\":5}]}", Decode("json: cannot unmarshal number into Go struct field Tool.tools.type of type string") },
        { "{\"model\":\"m\",\"messages\":[],\"tools\":[{\"type\":\"function\",\"function\":\"x\"}]}", Decode("json: cannot unmarshal string into Go struct field Tool.tools.function of type openai.Function") },
        { "{\"model\":\"m\",\"messages\":[],\"tools\":[{\"function\":{\"name\":5}}]}", Decode("json: cannot unmarshal number into Go struct field Function.tools.function.name of type string") },

        // Custom UnmarshalJSON sentinels.
        { "{\"model\":\"m\",\"messages\":[],\"stop\":5}", Decode("invalid stop type") },
        { "{\"model\":\"m\",\"messages\":[],\"stop\":true}", Decode("invalid stop type") },
        { "{\"model\":\"m\",\"messages\":[],\"stop\":{\"a\":1}}", Decode("invalid stop type") },
        { "{\"model\":\"m\",\"messages\":[],\"stop\":[5]}", Decode("invalid stop type") },
        { "{\"model\":\"m\",\"messages\":[],\"stop\":[{}]}", Decode("invalid stop type") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":1}]}", Decode("invalid content type") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":{\"a\":1}}]}", Decode("invalid content type") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[5]}]}", Decode("invalid content type") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":5}]}]}", Decode("invalid content type") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":\"x\"}]}]}", Decode("invalid content type") },
        { "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\",\"image_url\":{\"url\":5}}]}]}", Decode("invalid content type") },
        { "{\"model\":\"m\",\"messages\":[],\"tool_choice\":5}", Decode("invalid tool choice type") },
        { "{\"model\":\"m\",\"messages\":[],\"tool_choice\":[]}", Decode("invalid tool choice type") },
        { "{\"model\":\"m\",\"messages\":[],\"tool_choice\":true}", Decode("invalid tool choice type") },
        { "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":5}}", Decode("invalid tool choice type") },
        { "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"function\",\"function\":5}}", Decode("invalid tool choice type") },

        // First type error in document order wins; a hard UnmarshalJSON error
        // anywhere overrides it.
        { "{\"model\":123,\"messages\":\"x\",\"stream\":\"y\"}", Decode("json: cannot unmarshal number into Go struct field Request.model of type string") },
        { "{\"temperature\":\"x\",\"model\":123,\"messages\":[]}", Decode("json: cannot unmarshal string into Go struct field Request.temperature of type float64") },
        { "{\"messages\":[{\"role\":1,\"content\":\"hi\"}],\"temperature\":\"x\",\"model\":\"m\"}", Decode("json: cannot unmarshal number into Go struct field Message.messages.role of type string") },
        { "{\"model\":123,\"messages\":[],\"stop\":5}", Decode("invalid stop type") },
        { "{\"model\":123,\"messages\":[{\"role\":\"user\",\"content\":{\"a\":1}}]}", Decode("invalid content type") },
        { "{\"messages\":[{\"role\":\"user\",\"content\":{\"a\":1}}],\"model\":123}", Decode("invalid content type") },

        // Go matches field names case-insensitively but reports the tag name,
        // and decodes duplicate keys in input order.
        { "{\"MODEL\":123,\"messages\":[]}", Decode("json: cannot unmarshal number into Go struct field Request.model of type string") },
        { "{\"model\":\"a\",\"model\":123,\"messages\":[]}", Decode("json: cannot unmarshal number into Go struct field Request.model of type string") },
    };

    public static TheoryData<string> GoAccepted => new()
    {
        // null values are Go zero values everywhere, including map values.
        "{\"model\":\"m\",\"messages\":[],\"seed\":null}",
        "{\"model\":\"m\",\"messages\":[],\"logit_bias\":{\"a\":null}}",
        "{\"model\":\"m\",\"messages\":[],\"metadata\":{\"a\":null}}",
        "{\"model\":\"m\",\"messages\":[],\"modalities\":[null]}",
        "{\"model\":\"m\",\"messages\":[],\"tools\":null}",
        "{\"model\":\"m\",\"messages\":[],\"tools\":[]}",
        "{\"model\":\"m\",\"messages\":[],\"tools\":[null]}",
        "{\"model\":\"m\",\"messages\":[],\"tools\":[{\"function\":null}]}",
        "{\"model\":\"m\",\"messages\":[],\"stop\":\"END\"}",
        "{\"model\":\"m\",\"messages\":[],\"stop\":[]}",
        "{\"model\":\"m\",\"messages\":[],\"stop\":[null]}",
        "{\"model\":\"m\",\"messages\":[null]}",
        "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[]}]}",
        "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":null}]}",
        "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":[null]}]}",
        "{\"model\":\"m\",\"messages\":[{\"role\":\"assistant\",\"content\":\"hi\",\"tool_calls\":[null],\"annotations\":[null]}]}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":null}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{}}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"allowed_tools\":{}}}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"allowed_tools\",\"allowed_tools\":{}}}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"allowed_tools\",\"allowed_tools\":5}}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"allowed_tools\",\"allowed_tools\":{\"tools\":\"x\"}}}",
        "{\"model\":\"m\",\"messages\":[],\"tool_choice\":{\"type\":\"allowed_tools\",\"allowed_tools\":{\"mode\":\"auto\",\"tools\":[{\"type\":\"function\",\"function\":5}]}}}",
        "{\"model\":\"m\",\"messages\":[],\"response_format\":null}",
        "{\"model\":\"m\",\"messages\":[],\"response_format\":{\"json_schema\":5}}",
        "{\"model\":\"m\",\"messages\":[],\"max_tokens\":9223372036854775807}",
        "{\"model\":\"m\",\"messages\":[],\"max_tokens\":-9223372036854775808}",
        "{\"model\":\"m\",\"messages\":[],\"max_tokens\":-0}",
        "{\"model\":\"m\",\"messages\":[],\"temperature\":1e-999}",
        "{\"model\":\"m\",\"messages\":[],\"tools\":[{\"type\":\"function\",\"function\":{\"name\":\"f\",\"parameters\":\"x\"}}]}",
        "{\"model\":\"m\",\"messages\":[],\"unknown\":{\"role\":5}}",
        "{\"model\":\"m\",\"messages\":null}",
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
            // Decoding succeeded; the request may still fail the required-field
            // checks that run afterwards, exactly as in the reference.
            Assert.True(
                ex.Message is "invalid request: messages are required"
                    or "invalid request: model is required",
                ex.Message);
        }
    }

    [Fact]
    public void NullCollectionElementsDecodeAsGoZeroValues()
    {
        var request = Transform(
            "{\"model\":\"m\","
            + "\"messages\":[null,{\"role\":\"user\",\"content\":[null],"
            + "\"tool_calls\":[null],\"annotations\":[null]}],"
            + "\"tools\":[null],"
            + "\"stop\":[null,\"END\"],"
            + "\"logit_bias\":{\"a\":null},"
            + "\"metadata\":{\"b\":null}}");

        Assert.Equal(2, request.Messages.Count);
        Assert.Null(request.Messages[0].Role);

        var part = Assert.Single(request.Messages[1].Content!.MultipleContent!);
        Assert.Equal(string.Empty, part.Type);
        Assert.Null(part.Text);

        Assert.Single(request.Messages[1].ToolCalls!);
        Assert.Single(request.Messages[1].Annotations!);
        Assert.Single(request.Tools!);
        Assert.Equal(["", "END"], request.Stop!.Multiple);
        Assert.Equal(0, request.LogitBias!["a"]);
        Assert.Equal(string.Empty, request.Metadata!["b"]);
    }

    [Fact]
    public void AllowedToolsEnvelopeFallsBackToNamedChoiceLikeGo()
    {
        const string messages = "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]";

        var fallback = Transform(
            "{\"model\":\"m\"," + messages + ","
            + "\"tool_choice\":{\"type\":\"allowed_tools\",\"allowed_tools\":{\"tools\":\"x\"}}}");

        // Go falls back to the named choice, whose Type is the literal tag.
        Assert.Equal("allowed_tools", fallback.ToolChoice!.NamedToolChoice!.Type);
        Assert.Null(fallback.ToolChoice.Tools);

        var envelope = Transform(
            "{\"model\":\"m\"," + messages + ","
            + "\"tool_choice\":{\"type\":\"allowed_tools\",\"allowed_tools\":{}}}");

        // The envelope keeps both representations, as in Go.
        Assert.Equal("allowed_tools", envelope.ToolChoice!.NamedToolChoice!.Type);
        Assert.NotNull(envelope.ToolChoice.Tools);
        Assert.Empty(envelope.ToolChoice.Tools);
    }

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

    private static string Decode(string detail) => DecodePrefix + detail;
}
