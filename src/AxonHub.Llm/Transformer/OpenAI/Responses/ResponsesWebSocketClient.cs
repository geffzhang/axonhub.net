using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using AxonHub.Llm.Http;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// The WebSocket opening handshake gorilla/websocket performs for the reference
/// (<c>websocket.Dialer.DialContext</c>), implemented over a plain socket so the
/// port keeps the handshake response's status, headers and body — gorilla's
/// <c>DialContext</c> reports all three on a rejected upgrade, and the executor
/// forwards them as an <see cref="HttpClientError"/>.
/// </summary>
/// <remarks>
/// Differences from gorilla are registered in the porting ledger: proxy
/// environment variables and the inner HTTP client's TLS configuration cannot be
/// inherited (the port's <see cref="HttpExecutor"/> does not expose them), and
/// the default User-Agent is <c>axonhub/1.0</c> instead of net/http's
/// <c>Go-http-client/1.1</c> (production sets it before the transport anyway).
/// </remarks>
internal static class ResponsesWebSocketClient
{
    /// <summary>Default User-Agent, matching httpclient.BuildHttpRequest.</summary>
    public const string DefaultUserAgent = "axonhub/1.0";

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaxHandshakeHeaderBytes = 1 << 20;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Opens a WebSocket connection to <paramref name="wsUrl"/>. Handshake
    /// response headers are observed through <paramref name="request"/> before a
    /// rejected upgrade becomes an error, exactly as the reference does.
    /// </summary>
    public static WebSocket Connect(
        CancellationToken cancellationToken,
        string wsUrl,
        Dictionary<string, List<string>> headers,
        HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(request);

        var target = ParseTarget(wsUrl);

        using var timeout = new CancellationTokenSource(HandshakeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var tcp = new TcpClient();
        Stream? stream = null;
        var handshakeSucceeded = false;
        try
        {
            try
            {
                // ValueTask.AsTask: the executor is synchronous by design
                // (Go's blocking dial), and CA2012 forbids consuming an
                // uncompleted ValueTask with GetAwaiter().GetResult().
                tcp.ConnectAsync(target.Host, target.Port, linked.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("websocket dial failed: i/o timeout");
            }
            catch (OperationCanceledException ex)
            {
                throw new OperationCanceledException(ex.Message, ex, cancellationToken);
            }

            var networkStream = new NetworkStream(tcp.Client, ownsSocket: true);
            stream = networkStream;
            if (target.Secure)
            {
                var ssl = new SslStream(networkStream, leaveInnerStreamOpen: false);
                stream = ssl;
                try
                {
                    ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions { TargetHost = target.Host },
                        linked.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("websocket dial failed: i/o timeout");
                }
            }

            var key = GenerateKey();
            WriteHandshake(stream, target, headers, key);

            var response = ReadHandshakeResponse(stream, linked.Token);
            request.ObserveResponseHeaders(response.Headers);

            var expectedAccept = ComputeAccept(key);
            if (response.StatusCode != 101
                || !string.Equals(
                    HttpClientUtils.HeaderGet(response.Headers, "Upgrade"),
                    "websocket",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    HttpClientUtils.HeaderGet(response.Headers, "Connection"),
                    "upgrade",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    HttpClientUtils.HeaderGet(response.Headers, "Sec-WebSocket-Accept"),
                    expectedAccept,
                    StringComparison.Ordinal))
            {
                var body = response.StatusCode == 101
                    ? []
                    : ReadHandshakeBody(stream, response.Headers, linked.Token);

                throw new HttpClientError(
                    request.Method,
                    request.Url,
                    response.StatusCode,
                    response.StatusLine,
                    body)
                {
                    Headers = response.Headers,
                };
            }

            var webSocket = WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions
                {
                    IsServer = false,
                    KeepAliveInterval = TimeSpan.Zero,
                });

            handshakeSucceeded = true;
            return webSocket;
        }
        catch (HttpClientError)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(ex.Message, ex, cancellationToken);
        }
        catch (Exception ex) when (ex is SocketException or IOException or WebSocketException)
        {
            throw new InvalidOperationException("websocket dial failed: " + ex.Message, ex);
        }
        finally
        {
            if (!handshakeSucceeded)
            {
                stream?.Dispose();
                tcp.Dispose();
            }
        }
    }

    private static void WriteHandshake(
        Stream stream,
        WebSocketTarget target,
        Dictionary<string, List<string>> headers,
        string key)
    {
        var builder = new StringBuilder(256);
        builder.Append("GET ").Append(target.PathAndQuery).Append(" HTTP/1.1\r\n");
        builder.Append("Host: ").Append(target.Authority).Append("\r\n");

        var userAgent = HttpClientUtils.HeaderGet(headers, "User-Agent");
        if (userAgent.Length == 0)
        {
            userAgent = DefaultUserAgent;
        }

        AppendHeader(builder, "User-Agent", userAgent);
        AppendHeader(builder, "Upgrade", "websocket");
        AppendHeader(builder, "Connection", "Upgrade");
        AppendHeader(builder, "Sec-WebSocket-Key", key);
        AppendHeader(builder, "Sec-WebSocket-Version", "13");

        foreach (var key2 in headers.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            var canonical = HttpClientUtils.CanonicalHeaderKey(key2);
            if (string.Equals(canonical, "Host", StringComparison.Ordinal)
                || string.Equals(canonical, "User-Agent", StringComparison.Ordinal))
            {
                // Host and User-Agent are written above (net/http writes both
                // before the sorted header map).
                continue;
            }

            foreach (var value in headers[key2])
            {
                AppendHeader(builder, canonical, value);
            }
        }

        builder.Append("\r\n");

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static void AppendHeader(StringBuilder builder, string name, string value)
    {
        if (name.Contains('\r', StringComparison.Ordinal)
            || name.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("websocket dial failed: invalid header field");
        }

        builder.Append(name).Append(": ").Append(value).Append("\r\n");
    }

    private static HandshakeResponse ReadHandshakeResponse(Stream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new MemoryStream();
        var one = new byte[1];
        var matched = 0;

        while (headerBytes.Length < MaxHandshakeHeaderBytes)
        {
            var read = ReadWithCancellation(stream, one, cancellationToken);
            if (read <= 0)
            {
                throw new InvalidOperationException("websocket dial failed: unexpected EOF");
            }

            headerBytes.WriteByte(one[0]);
            matched = one[0] switch
            {
                (byte)'\r' when matched is 0 or 2 => matched + 1,
                (byte)'\n' when matched is 1 or 3 => matched + 1,
                _ => one[0] == (byte)'\r' ? 1 : 0,
            };

            if (matched == 4)
            {
                break;
            }
        }

        if (matched != 4)
        {
            throw new InvalidOperationException("websocket dial failed: response headers too large");
        }

        var raw = headerBytes.ToArray();
        var text = Encoding.Latin1.GetString(raw, 0, raw.Length - 4);
        var lines = text.Split("\r\n");
        var statusLine = lines[0];

        var firstSpace = statusLine.IndexOf(' ', StringComparison.Ordinal);
        if (firstSpace < 0)
        {
            throw new InvalidOperationException("websocket dial failed: malformed HTTP response");
        }

        var status = statusLine[(firstSpace + 1)..].TrimStart(' ');
        var codeText = status.Split(' ', 2)[0];
        if (!int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var statusCode))
        {
            throw new InvalidOperationException("websocket dial failed: malformed HTTP status");
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidOperationException("websocket dial failed: malformed HTTP header");
            }

            var name = HttpClientUtils.CanonicalHeaderKey(line[..colon].Trim(' ', '\t'));
            var value = line[(colon + 1)..].Trim(' ', '\t');
            HttpClientUtils.HeaderAdd(headers, name, value);
        }

        return new HandshakeResponse(statusCode, status, headers);
    }

    private static byte[] ReadHandshakeBody(
        Stream stream,
        Dictionary<string, List<string>> headers,
        CancellationToken cancellationToken)
    {
        // net/http gives 1xx responses an empty body, and gorilla only reads it
        // when the upgrade was rejected.
        var transferEncoding = HttpClientUtils.HeaderGet(headers, "Transfer-Encoding");
        if (transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return ReadChunkedBody(stream, cancellationToken);
        }

        var contentLength = HttpClientUtils.HeaderGet(headers, "Content-Length");
        if (int.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            var body = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = ReadWithCancellation(stream, body.AsMemory(offset), cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                offset += read;
            }

            return offset == length ? body : body[..offset];
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = ReadWithCancellation(stream, chunk, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static byte[] ReadChunkedBody(Stream stream, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var line = new MemoryStream();
        while (true)
        {
            line.SetLength(0);
            while (true)
            {
                var one = new byte[1];
                var read = ReadWithCancellation(stream, one, cancellationToken);
                if (read <= 0)
                {
                    return body.ToArray();
                }

                if (one[0] == (byte)'\n')
                {
                    break;
                }

                if (one[0] != (byte)'\r')
                {
                    line.WriteByte(one[0]);
                }
            }

            var sizeText = Encoding.ASCII.GetString(line.ToArray()).Split(';', 2)[0].Trim();
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
                || size < 0)
            {
                return body.ToArray();
            }

            if (size == 0)
            {
                return body.ToArray();
            }

            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = ReadWithCancellation(stream, chunk.AsMemory(offset), cancellationToken);
                if (read <= 0)
                {
                    return body.ToArray();
                }

                offset += read;
            }

            body.Write(chunk, 0, size);

            // Consume the CRLF after the chunk data.
            var cr = new byte[2];
            var consumed = 0;
            while (consumed < 2)
            {
                var read = ReadWithCancellation(stream, cr.AsMemory(consumed), cancellationToken);
                if (read <= 0)
                {
                    return body.ToArray();
                }

                consumed += read;
            }
        }
    }

    private static int ReadWithCancellation(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        // Stream.ReadAsync(Memory<byte>, CT) returns ValueTask<int>; CA2012
        // requires converting before a synchronous block on the result.
        => stream.ReadAsync(buffer, cancellationToken).AsTask().GetAwaiter().GetResult();

    private static string GenerateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));

    private static string ComputeAccept(string key)
    {
        var bytes = Encoding.ASCII.GetBytes(key + WebSocketGuid);
#pragma warning disable CA5350 // RFC 6455 mandates SHA-1 for the opening handshake key.
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }

    private static WebSocketTarget ParseTarget(string wsUrl)
    {
        var schemeEnd = wsUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            throw new InvalidOperationException("websocket dial failed: malformed url");
        }

        var scheme = wsUrl[..schemeEnd].ToLowerInvariant();
        var secure = scheme == "wss";
        var rest = wsUrl[(schemeEnd + 3)..];
        var fragment = rest.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            rest = rest[..fragment];
        }

        var boundary = rest.IndexOfAny(['/', '?']);
        var authority = boundary < 0 ? rest : rest[..boundary];
        var pathAndQuery = boundary < 0 ? "/" : rest[boundary..];
        if (pathAndQuery.StartsWith('?'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        var hostPort = authority[(authority.LastIndexOf('@') + 1)..];
        string host;
        var port = secure ? 443 : 80;

        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new InvalidOperationException("websocket dial failed: missing ']' in host");
            }

            host = hostPort[1..close];
            if (close + 1 < hostPort.Length && hostPort[close + 1] == ':')
            {
                port = ParsePort(hostPort[(close + 2)..], port);
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0 && hostPort.IndexOf(':', 0, colon) < 0)
            {
                host = hostPort[..colon];
                port = ParsePort(hostPort[(colon + 1)..], port);
            }
            else
            {
                host = hostPort;
            }
        }

        if (host.Length == 0)
        {
            throw new InvalidOperationException("websocket dial failed: missing host");
        }

        return new WebSocketTarget(secure, host, port, authority, pathAndQuery);
    }

    private static int ParsePort(string text, int fallback)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : fallback;

    private sealed record HandshakeResponse(
        int StatusCode,
        string StatusLine,
        Dictionary<string, List<string>> Headers);

    private sealed record WebSocketTarget(
        bool Secure,
        string Host,
        int Port,
        string Authority,
        string PathAndQuery);
}
