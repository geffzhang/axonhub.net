using System.Text;
using System.Text.Json.Nodes;
using AxonHub.Api.AnthropicUsage;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of anthropic_input_usage_test.go and the stream-learning half of
/// anthropic_input_calibration_test.go.
/// </summary>
public sealed class AnthropicInputUsageStreamTests
{
    [Fact]
    public void EstimatesOnlyWhenTheInitialUsageIsEmpty()
    {
        var initials = new[]
        {
            """{"input_tokens":0,"output_tokens":0}""",
            """{"input_tokens":42,"output_tokens":0}""",
            """{"input_tokens":0,"cache_read_input_tokens":100,"output_tokens":0}""",
            """{"cache_creation_input_tokens":100}""",
        };

        foreach (var initial in initials)
        {
            var start = new StreamEvent
            {
                Type = "message_start",
                Data = Encoding.UTF8.GetBytes(
                    """{"type":"message_start","message":{"usage":"""
                    + initial
                    + "}}"),
            };
            var original = (byte[])start.Data.Clone();
            var final = new StreamEvent
            {
                Type = "message_delta",
                Data = Encoding.UTF8.GetBytes(
                    """{"type":"message_delta","usage":{"input_tokens":12,"output_tokens":34}}"""),
            };
            var source = new SliceStream<StreamEvent>([start, final]);
            using var wrapped = AnthropicInputUsageStream.Wrap(
                source,
                MessagesRequest("""{"messages":[{"role":"user","content":"Hello test request"}]}"""),
                null);

            Assert.True(wrapped.Next());
            var first = wrapped.Current!;
            Assert.Same(first, wrapped.Current);
            if (initial == """{"input_tokens":0,"output_tokens":0}""")
            {
                Assert.True(
                    JsonNode.Parse(first.Data)!["message"]!["usage"]!["input_tokens"]!
                        .GetValue<long>() > 0);
            }
            else
            {
                Assert.Equal(original, first.Data);
            }

            Assert.Equal(original, start.Data);

            Assert.True(wrapped.Next());
            Assert.Same(final, wrapped.Current);
            Assert.False(wrapped.Next());
            Assert.Null(wrapped.Err);
        }
    }

    [Fact]
    public void SkipsOtherProtocols()
    {
        var source = new SliceStream<StreamEvent>([]);
        var wrapped = AnthropicInputUsageStream.Wrap(
            source,
            new HttpRequestData { Path = "/v1/responses" },
            null);
        Assert.Same(source, wrapped);
    }

    [Theory]
    [InlineData(true, true, false, false, false, 1)]
    [InlineData(false, true, false, false, false, 0)]
    [InlineData(true, false, false, false, false, 0)]
    [InlineData(true, true, false, false, true, 0)]
    [InlineData(true, true, true, false, false, 0)]
    [InlineData(true, true, false, true, false, 0)]
    public void LearnsOnlyFromCleanlyCompletedStreams(
        bool stop,
        bool terminal,
        bool cancel,
        bool closeEarly,
        bool upstreamFailure,
        int wantSamples)
    {
        var cache = new InputUsageCache();
        using var cancellation = new CancellationTokenSource();
        var route = new ResponseRoute(3, "model", "openai/responses");
        var calibration = AnthropicInputUsageCalibration.Create(
            cancellation.Token,
            cache,
            route,
            DateTimeOffset.UtcNow,
            projectId: 1,
            apiKeyId: 2);
        Assert.NotNull(calibration);

        var events = new List<StreamEvent>
        {
            new()
            {
                Data = Encoding.UTF8.GetBytes(
                    """{"type":"message_start","message":{"model":"model","usage":{"input_tokens":0}}}"""),
            },
        };
        var final = terminal
            ? """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"input_tokens":30,"cache_read_input_tokens":70}}"""
            : """{"type":"message_delta","usage":{"input_tokens":30,"cache_read_input_tokens":70}}""";
        events.Add(new StreamEvent { Data = Encoding.UTF8.GetBytes(final) });
        if (stop)
        {
            events.Add(new StreamEvent
            {
                Data = Encoding.UTF8.GetBytes("""{"type":"message_stop"}"""),
            });
        }

        IStream<StreamEvent> source = upstreamFailure
            ? new ErrorStream(new SliceStream<StreamEvent>(events), new InvalidOperationException("failed"))
            : new SliceStream<StreamEvent>(events);
        using var wrapped = AnthropicInputUsageStream.Wrap(
            source,
            MessagesRequest(CalibrationBody("first")),
            calibration);

        Assert.True(wrapped.Next());
        Assert.True(
            JsonNode.Parse(wrapped.Current!.Data)!["message"]!["usage"]!["input_tokens"]!
                .GetValue<long>() > 0);
        if (cancel)
        {
            cancellation.Cancel();
        }

        if (closeEarly)
        {
            wrapped.Dispose();
        }
        else
        {
            Assert.True(wrapped.Next());
            Assert.Equal(final, Encoding.UTF8.GetString(wrapped.Current!.Data));
            Assert.Empty(cache.Samples);
            while (wrapped.Next())
            {
            }
        }

        Assert.Equal(wantSamples, cache.Samples.Count);
        if (wantSamples == 1)
        {
            Assert.Equal(100, cache.Samples[0].Actual);
        }
    }

    private static HttpRequestData MessagesRequest(string body) => new()
    {
        Path = "/v1/messages",
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static string CalibrationBody(params string[] messages)
    {
        var items = new JsonArray(messages
            .Select(static message => (JsonNode)new JsonObject
            {
                ["role"] = "user",
                ["content"] = message,
            })
            .ToArray());
        return new JsonObject
        {
            ["model"] = "alias",
            ["messages"] = items,
        }.ToJsonString();
    }

    private sealed class ErrorStream(IStream<StreamEvent> inner, Exception error)
        : IStream<StreamEvent>
    {
        public StreamEvent? Current => inner.Current;

        public Exception? Err => error;

        public bool Next() => inner.Next();

        public void Dispose() => inner.Dispose();
    }
}
