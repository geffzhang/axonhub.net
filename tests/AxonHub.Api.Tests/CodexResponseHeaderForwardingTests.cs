using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/api/chat_test.go's codex response-header tests:
/// writeForwardResponseHeaders forwards only X-Reasoning-Included, and only for
/// responses requests whose outbound advertises the codex contract and whose
/// request carries a codex session; primeCodexTurnStateHeader copies
/// X-Codex-Turn-State from the first event while replaying every event.
/// </summary>
public sealed class CodexResponseHeaderForwardingTests
{
    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static Dictionary<string, List<string>> CodexSessionHeaders()
        => new(StringComparer.Ordinal)
        {
            [CodexHeaders.TurnMetadataHeader] = ["""{"session_id":"session-1"}"""],
        };

    [Fact]
    public void PrimeCodexTurnStateHeaderCopiesTheFirstEventHeaderAndReplaysEvents()
    {
        var first = new StreamEvent
        {
            Type = "response.created",
            Data = """{"type":"response.created"}"""u8.ToArray(),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [CodexHeaders.TurnStateHeader] = ["ts-1"],
            },
        };
        var second = new StreamEvent
        {
            Type = "response.completed",
            Data = """{"type":"response.completed"}"""u8.ToArray(),
        };

        var context = NewContext();
        var stream = ChatCompletionEndpoint.PrimeCodexTurnStateHeader(
            context.Response.Headers,
            new SliceStream<StreamEvent>([first, second]));

        Assert.Equal("ts-1", context.Response.Headers[CodexHeaders.TurnStateHeader].ToString());

        var events = new List<StreamEvent>();
        while (stream.Next())
        {
            events.Add(stream.Current!);
        }

        Assert.Null(stream.Err);
        Assert.Equal(2, events.Count);
        Assert.Same(first, events[0]);
        Assert.Same(second, events[1]);
        stream.Dispose();
    }

    [Theory]
    [InlineData("/v1/chat/completions", false, "")]
    [InlineData("/v1/responses", false, "")]
    [InlineData("/v1/responses", true, "true")]
    public void WriteForwardResponseHeadersOnlyForCodexResponses(
        string path,
        bool withSession,
        string want)
    {
        var result = new ChatCompletionResult
        {
            Response = new HttpResponseData
            {
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    [ResponseHeaders.ReasoningIncludedHeader] = ["true"],
                },
            },
            CodexResponseHeadersSupported = true,
        };
        var request = new HttpRequestData
        {
            Path = path,
            Headers = withSession ? CodexSessionHeaders() : null,
        };

        var context = NewContext();
        ChatCompletionEndpoint.WriteForwardResponseHeaders(context, request, result);

        Assert.Equal(want, context.Response.Headers[ResponseHeaders.ReasoningIncludedHeader].ToString());
    }

    [Fact]
    public void WriteForwardResponseHeadersRejectsUnsupportedOutbound()
    {
        var result = new ChatCompletionResult
        {
            Response = new HttpResponseData
            {
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    [ResponseHeaders.ReasoningIncludedHeader] = ["true"],
                },
            },
            // CodexResponseHeadersSupported stays false: only the codex outbound
            // advertises the contract.
        };
        var request = new HttpRequestData
        {
            Path = "/v1/responses",
            Headers = CodexSessionHeaders(),
        };

        var context = NewContext();
        ChatCompletionEndpoint.WriteForwardResponseHeaders(context, request, result);

        Assert.Equal(
            string.Empty,
            context.Response.Headers[ResponseHeaders.ReasoningIncludedHeader].ToString());
    }

    [Fact]
    public void WriteForwardResponseHeadersReadsTheStreamProvider()
    {
        var result = new ChatCompletionResult
        {
            Stream = ResponseHeaders.WithResponseHeaders(
                new SliceStream<StreamEvent>([]),
                new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    [ResponseHeaders.ReasoningIncludedHeader] = ["true"],
                }),
            CodexResponseHeadersSupported = true,
        };
        var request = new HttpRequestData
        {
            Path = "/v1/responses",
            Headers = CodexSessionHeaders(),
        };

        var context = NewContext();
        ChatCompletionEndpoint.WriteForwardResponseHeaders(context, request, result);

        Assert.Equal(
            "true",
            context.Response.Headers[ResponseHeaders.ReasoningIncludedHeader].ToString());
    }

    [Fact]
    public void CopyCodexTurnStateHeaderIgnoresMissingOrEmptyValues()
    {
        var context = NewContext();

        ChatCompletionEndpoint.CopyCodexTurnStateHeader(context.Response.Headers, null);
        ChatCompletionEndpoint.CopyCodexTurnStateHeader(
            context.Response.Headers,
            new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                [CodexHeaders.TurnStateHeader] = [""],
            });

        Assert.Equal(string.Empty, context.Response.Headers[CodexHeaders.TurnStateHeader].ToString());
    }
}
