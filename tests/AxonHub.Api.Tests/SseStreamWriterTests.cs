using System.Text;
using AxonHub.Api.Http;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Byte-level tests for the SSE writer. The reference uses gin-contrib/sse, so
/// the exact framing (no space after <c>data:</c>, CR escaped as <c>\r</c>,
/// blank line between events) is part of the client contract.
/// </summary>
public sealed class SseStreamWriterTests
{
    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    [Fact]
    public async Task WritesHeadersAndGinCompatibleFraming()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
        [
            TestChannels.Data("hello\nworld"),
            TestChannels.Data("[DONE]"),
        ]);

        await SseStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None);

        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("keep-alive", context.Response.Headers["Connection"].ToString());
        Assert.Equal(
            "data:hello\ndata:world\n\ndata:[DONE]\n\n",
            Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task WritesEventFieldForTypedEventsAndEscapesCarriageReturns()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
        [
            new StreamEvent { Type = "error", Data = Encoding.UTF8.GetBytes("a\rb") },
            TestChannels.Data("[DONE]"),
        ]);

        await SseStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None);

        Assert.Equal(
            "event:error\ndata:a\\rb\n\ndata:[DONE]\n\n",
            Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task SuppressesTrailingErrorAfterDoneEvent()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
            [TestChannels.Data("[DONE]")],
            terminalError: new IOException("connection reset"));

        await SseStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None);

        Assert.Equal("data:[DONE]\n\n", Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task SuppressesTrailingErrorAfterFinishReason()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
            [TestChannels.Data("{\"choices\":[{\"index\":0,\"finish_reason\":\"stop\"}]}")],
            terminalError: new IOException("connection reset"));

        await SseStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None);

        Assert.DoesNotContain("event:error", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesErrorEventWhenStreamFailsWithoutTerminal()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
            [],
            terminalError: new IOException("connection reset"));

        await SseStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None);

        Assert.Equal(
            "event:error\ndata:{\"error\":{\"code\":\"\",\"message\":\"connection reset\",\"type\":\"server_error\"},\"request_id\":\"\"}\n\n",
            Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task WritesIncompleteStreamErrorWhenStreamEndsWithoutTerminal()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>([]);

        await SseStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None);

        Assert.Equal(
            "event:error\ndata:{\"error\":{\"code\":\"\",\"message\":\"stream ended without terminal event\",\"type\":\"server_error\"},\"request_id\":\"\"}\n\n",
            Encoding.UTF8.GetString(body.ToArray()));
    }
}
