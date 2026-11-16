using System.Collections.Concurrent;
using System.Text;
using AxonHub.Api.Http;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// api/chat.go:396-500: an idle stream is kept alive with the API surface's
/// heartbeat payload, and a delivered event restarts the interval.
/// </summary>
public sealed class SseKeepAliveTests
{
    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static async Task WaitForBodyAsync(MemoryStream body, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Encoding.UTF8.GetString(body.ToArray()).Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("timed out waiting for " + expected);
    }

    [Fact]
    public async Task OpenAiHeartbeatIsWrittenWhileTheStreamIsIdle()
    {
        var (context, body) = NewContext();
        using var stream = new BlockingStream();

        var writer = SseStreamWriter.WriteWithHeartbeatAsync(
            context,
            stream,
            expectedChoices: 1,
            CancellationToken.None,
            transformStreamError: null,
            new SseKeepAliveSettings(true, TimeSpan.FromMilliseconds(20)),
            SseHeartbeatFormat.OpenAi);

        await WaitForBodyAsync(body, ": keep-alive\n\n");

        stream.Add(TestChannels.Data("[DONE]"));
        stream.Complete();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));

        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains(": keep-alive\n\n", text, StringComparison.Ordinal);
        Assert.Contains("data:[DONE]\n\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("event:error", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicHeartbeatUsesPingEvent()
    {
        var (context, body) = NewContext();
        using var stream = new BlockingStream();

        var writer = SseStreamWriter.WriteWithHeartbeatAsync(
            context,
            stream,
            expectedChoices: 1,
            CancellationToken.None,
            transformStreamError: null,
            new SseKeepAliveSettings(true, TimeSpan.FromMilliseconds(20)),
            SseHeartbeatFormat.Anthropic);

        await WaitForBodyAsync(body, "event: ping\ndata: {\"type\":\"ping\"}\n\n");

        stream.Add(TestChannels.Data("[DONE]"));
        stream.Complete();
        await writer.WaitAsync(TimeSpan.FromSeconds(5));

        var text = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("event: ping\ndata: {\"type\":\"ping\"}\n\n", text, StringComparison.Ordinal);
        Assert.Contains("data:[DONE]\n\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventBeforeTheIntervalSuppressesTheFirstHeartbeat()
    {
        var (context, body) = NewContext();
        using var stream = new BlockingStream();
        stream.Add(TestChannels.Data("[DONE]"));
        stream.Complete();

        await SseStreamWriter.WriteWithHeartbeatAsync(
            context,
            stream,
            expectedChoices: 1,
            CancellationToken.None,
            transformStreamError: null,
            new SseKeepAliveSettings(true, TimeSpan.FromSeconds(1)),
            SseHeartbeatFormat.OpenAi);

        Assert.Equal("data:[DONE]\n\n", Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>
    /// A pull stream whose Next blocks until the test publishes an event or the
    /// end sentinel. This is the shape the heartbeat reader exists for.
    /// </summary>
    private sealed class BlockingStream : IStream<StreamEvent>
    {
        private readonly BlockingCollection<StreamEvent?> _items = new();

        public StreamEvent? Current { get; private set; }

        public Exception? Err => null;

        public bool Next()
        {
            var item = _items.Take();
            if (item is null)
            {
                return false;
            }

            Current = item;
            return true;
        }

        public void Add(StreamEvent streamEvent) => _items.Add(streamEvent);

        public void Complete() => _items.Add(null);

        public void Dispose() => _items.CompleteAdding();
    }
}
