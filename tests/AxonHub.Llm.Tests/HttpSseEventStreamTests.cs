using System.Text;
using AxonHub.Llm.Http;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/httpclient/decoder_test.go coverage added by live cbdf9a6d
/// (#2692): some upstreams end an SSE body with <c>data: [DONE]</c> and no
/// trailing newline, and the reference now synthesizes the missing newline at
/// EOF instead of dropping the final field.
/// </summary>
public sealed class HttpSseEventStreamTests
{
    private static readonly string TestDataRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "httpclient");

    [Theory]
    [InlineData("synthetic_upstream_unterminated_final_line.raw")]
    [InlineData("synthetic_upstream_terminated_final_line.raw")]
    public void TerminalEventAtUnterminatedEOF(string fixture)
    {
        var data = File.ReadAllBytes(Path.Combine(TestDataRoot, fixture));
        var (events, err) = Decode(data);

        Assert.Null(err);
        Assert.NotEmpty(events);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(events[^1].Data));
    }

    [Fact]
    public void UnterminatedFixtureDiffersOnlyByItsLineTermination()
    {
        var unterminated = File.ReadAllBytes(
            Path.Combine(TestDataRoot, "synthetic_upstream_unterminated_final_line.raw"));
        var terminated = File.ReadAllBytes(
            Path.Combine(TestDataRoot, "synthetic_upstream_terminated_final_line.raw"));

        Assert.NotEqual((byte)'\n', unterminated[^1]);
        Assert.Equal("\n\n", Encoding.UTF8.GetString(terminated[^2..]));
        Assert.Equal(unterminated, terminated[..^2]);
    }

    [Fact]
    public void UnterminatedDoneOnlyYieldsDone()
    {
        var (events, err) = Decode(Encoding.UTF8.GetBytes("data: [DONE]"));

        Assert.Null(err);
        var single = Assert.Single(events);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(single.Data));
    }

    [Fact]
    public void TruncatedMidJsonDoesNotFabricateTerminal()
    {
        var body = Encoding.UTF8.GetBytes(
            """data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"con""");
        var (events, err) = Decode(body);

        Assert.Null(err);
        Assert.DoesNotContain(
            events,
            static e => Encoding.UTF8.GetString(e.Data) == "[DONE]");
        // The newline-terminated partial field may surface as a truncated data
        // event; it must never be rewritten into a terminal [DONE].
        Assert.Contains(
            events,
            static e => Encoding.UTF8.GetString(e.Data)
                .StartsWith("""{"id":"chatcmpl-1""", StringComparison.Ordinal));
    }

    [Fact]
    public void SynthesizedNewlineCountsTowardTheEventSizeCap()
    {
        // "data: [DONE]" is 12 bytes; the synthesized newline makes it 13.
        using var stream = new HttpSseEventStream(
            new MemoryStream(Encoding.UTF8.GetBytes("data: [DONE]")),
            maxEventBytes: 12);

        Assert.False(stream.Next());
        Assert.IsType<StreamEventTooLargeException>(stream.Err);
    }

    [Fact]
    public void TerminatedEventAtTheCapIsAccepted()
    {
        // "data: [DONE]\n\n" is 14 bytes and needs no synthesized newline.
        var (events, err) = Decode(Encoding.UTF8.GetBytes("data: [DONE]\n\n"), maxEventBytes: 14);

        Assert.Null(err);
        var single = Assert.Single(events);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(single.Data));
    }

    [Fact]
    public void LoneCarriageReturnNeedsNoSynthesizedNewline()
    {
        // CR terminates an SSE line, so the 13-byte body fits a 13-byte cap.
        var (events, err) = Decode(Encoding.UTF8.GetBytes("data: [DONE]\r"), maxEventBytes: 13);

        Assert.Null(err);
        var single = Assert.Single(events);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(single.Data));
    }

    [Fact]
    public void EmptyBodyDoesNotSynthesizeAnEvent()
    {
        var (events, err) = Decode([], maxEventBytes: 1);

        Assert.Null(err);
        Assert.Empty(events);
    }

    [Fact]
    public void StringDecoderMatchesUnterminatedRecoveryAndCapAccounting()
    {
        using var decoder = new SseDecoder("data: [DONE]");
        Assert.True(decoder.Next());
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(decoder.Current!.Data));
        Assert.False(decoder.Next());
        Assert.Null(decoder.Err);

        using var oversized = new SseDecoder("data: [DONE]", maxEventBytes: 12);
        Assert.False(oversized.Next());
        Assert.IsType<StreamEventTooLargeException>(oversized.Err);

        using var terminated = new SseDecoder("data: [DONE]\n\n", maxEventBytes: 14);
        Assert.True(terminated.Next());
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(terminated.Current!.Data));

        using var empty = new SseDecoder(string.Empty, maxEventBytes: 1);
        Assert.False(empty.Next());
        Assert.Null(empty.Err);
    }

    private static (List<StreamEvent> Events, Exception? Err) Decode(
        byte[] body,
        int maxEventBytes = 0)
    {
        using var stream = new HttpSseEventStream(new MemoryStream(body), maxEventBytes: maxEventBytes);
        var events = new List<StreamEvent>();
        while (stream.Next())
        {
            events.Add(stream.Current!);
        }

        return (events, stream.Err);
    }
}
