using System.Net;
using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the transport behaviour ported from llm/httpclient/client.go: request
/// building (default headers, auth, body bytes), error status translation with
/// the 1 MB body cap, and SSE stream negotiation.
/// </summary>
public sealed class HttpExecutorTests
{
    [Fact]
    public void DoSendsBuiltRequestAndReturnsResponse()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        });

        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "https://upstream.example/v1/chat/completions?x=1",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"model":"gpt-4o"}"""),
            Auth = new AuthConfig { Type = AuthTypes.Bearer, ApiKey = "sk-test" },
        };

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var response = executor.Do(CancellationToken.None, request);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"ok":true}""", Encoding.UTF8.GetString(response.Body!));
        Assert.Equal("POST", handler.LastMethod);
        Assert.Equal("https://upstream.example/v1/chat/completions?x=1", handler.LastUrl);
        Assert.Equal("""{"model":"gpt-4o"}""", handler.LastBody);
        Assert.Equal("axonhub/1.0", handler.LastHeader("User-Agent"));
        Assert.Equal("application/json", handler.LastHeader("Accept"));
        Assert.Equal("Bearer sk-test", handler.LastHeader("Authorization"));
        Assert.Equal("application/json", handler.LastHeader("Content-Type"));
        Assert.NotNull(response.RawResponse);
        Assert.Equal(HttpStatusCode.OK, response.RawResponse!.StatusCode);
    }

    [Fact]
    public void DoThrowsHttpClientErrorForErrorStatus()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"error":"boom"}""", Encoding.UTF8, "application/json"),
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var error = Assert.Throws<HttpClientError>(() =>
            executor.Do(CancellationToken.None, new HttpRequestData { Method = "POST", Url = "https://u.example/v1" }));

        Assert.Equal(503, error.StatusCode);
        Assert.Equal("""{"error":"boom"}""", Encoding.UTF8.GetString(error.Body));
        Assert.StartsWith("503", error.Status, StringComparison.Ordinal);
        Assert.NotNull(error.Headers);
        Assert.True(error.Headers!.ContainsKey("Content-Type"));
    }

    [Fact]
    public void DoCapsErrorBodyAtOneMegabyte()
    {
        var oversized = new string('x', HttpExecutor.MaxErrorBodySize + 100);
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(oversized),
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var error = Assert.Throws<HttpClientError>(() =>
            executor.Do(CancellationToken.None, new HttpRequestData { Method = "GET", Url = "https://u.example/v1" }));

        Assert.Equal(HttpExecutor.MaxErrorBodySize, error.Body.Length);
    }

    [Fact]
    public void DoObservesResponseHeaders()
    {
        IReadOnlyDictionary<string, List<string>>? observed = null;
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
            response.Headers.Add("X-Request-Id", "req-1");
            return response;
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = "https://u.example/v1",
            OnResponseHeaders = headers => observed = headers,
        };

        var response = executor.Do(CancellationToken.None, request);

        Assert.NotNull(observed);
        Assert.Equal("req-1", observed!["X-Request-Id"][0]);
        Assert.Equal("req-1", response.Headers["X-Request-Id"][0]);
    }

    [Fact]
    public void DoStreamNegotiatesSseAndYieldsEvents()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"a\":1}\n\ndata: [DONE]\n\n",
                Encoding.UTF8,
                "text/event-stream"),
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "https://u.example/v1/chat/completions",
            Body = Encoding.UTF8.GetBytes("{}"),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
        };

        using var stream = executor.DoStream(CancellationToken.None, request);

        Assert.Equal("text/event-stream", handler.LastHeader("Accept"));
        Assert.Equal("no-cache", handler.LastHeader("Cache-Control"));
        Assert.Equal("keep-alive", handler.LastHeader("Connection"));

        var events = new List<string>();
        while (stream.Next())
        {
            events.Add(Encoding.UTF8.GetString(stream.Current!.Data));
        }

        Assert.Null(stream.Err);
        Assert.Equal(["{\"a\":1}", "[DONE]"], events);
    }

    [Fact]
    public void DoStreamForwardsReasoningIncludedHeaderMetadata()
    {
        var handler = new ScriptedHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: x\n\n", Encoding.UTF8, "text/event-stream"),
            };
            response.Headers.Add(ResponseHeaders.ReasoningIncludedHeader, "true");
            return response;
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        using var stream = executor.DoStream(
            CancellationToken.None,
            new HttpRequestData { Method = "POST", Url = "https://u.example/v1" });

        Assert.True(stream.Next());

        var headers = ResponseHeaders.GetResponseHeaders(stream);
        Assert.NotNull(headers);
        Assert.Equal("true", headers![ResponseHeaders.ReasoningIncludedHeader][0]);
    }

    [Fact]
    public void DoStreamThrowsHttpClientErrorForErrorStatus()
    {
        var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":"rate limited"}""", Encoding.UTF8, "application/json"),
        });

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);

        var error = Assert.Throws<HttpClientError>(() =>
            executor.DoStream(CancellationToken.None, new HttpRequestData { Method = "POST", Url = "https://u.example/v1" }));

        Assert.Equal(429, error.StatusCode);
        Assert.Equal("""{"error":"rate limited"}""", Encoding.UTF8.GetString(error.Body));
        Assert.NotNull(error.Headers);
    }

    [Fact]
    public void StreamFramingMatchesBufferedDecoder()
    {
        // The buffered decoder (SseEventStream) is the corpus-locked reference
        // for framing; the incremental reader must produce the same events.
        string[] inputs =
        [
            "data: one\n\ndata: two\n\n",
            "event: e1\r\ndata: a\r\ndata: b\r\n\r\n",
            "data: cr\ronly\r\r",
            ": comment\ndata: x\n\n",
            "id: 7\ndata: x\n\ndata: y\n\n",
            "data\ndata: \n\n",
            "﻿data: bom\n\n",
            "data: no-trailing-blank",
            "\n\ndata: after-blanks\n\n",
            "data: 你好\n\n",
        ];

        foreach (var input in inputs)
        {
            using var buffered = new SseEventStream(input);
            var expected = Drain(buffered);

            using var streamed = new HttpSseEventStream(
                new MemoryStream(Encoding.UTF8.GetBytes(input)));
            var actual = Drain(streamed);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void StreamRejectsOversizedEventsLikeBufferedDecoder()
    {
        const string Input = "data: 0123456789\n\n";

        using var buffered = new SseEventStream(Input, maxEventBytes: 10);
        Assert.False(buffered.Next());
        Assert.NotNull(buffered.Err);

        using var streamed = new HttpSseEventStream(
            new MemoryStream(Encoding.UTF8.GetBytes(Input)),
            maxEventBytes: 10);
        Assert.False(streamed.Next());
        Assert.NotNull(streamed.Err);
    }

    private static List<string> Drain(IStream<StreamEvent> stream)
    {
        var events = new List<string>();
        while (stream.Next())
        {
            events.Add(Format(stream.Current!));
        }

        Assert.Null(stream.Err);
        return events;
    }

    private static string Format(StreamEvent e)
        => e.LastEventId + "|" + e.Type + "|" + Encoding.UTF8.GetString(e.Data);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public string? LastMethod { get; private set; }

        public string? LastUrl { get; private set; }

        public string? LastBody { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string LastHeader(string name)
        {
            if (LastRequest is null)
            {
                return string.Empty;
            }

            if (LastRequest.Headers.TryGetValues(name, out var values))
            {
                return values.First();
            }

            if (LastRequest.Content is not null
                && LastRequest.Content.Headers.TryGetValues(name, out var contentValues))
            {
                return contentValues.First();
            }

            return string.Empty;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastMethod = request.Method.Method;
            LastUrl = request.RequestUri?.OriginalString;
            LastBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();

            return Task.FromResult(responder(request, cancellationToken));
        }
    }
}
