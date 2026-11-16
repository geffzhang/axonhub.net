using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Minimal WebSocket server for the Responses WebSocket transport tests.
/// </summary>
/// <remarks>
/// The reference tests use <c>httptest.NewServer</c> plus gorilla's
/// <c>Upgrader</c>. The port's dialer performs the opening handshake itself
/// (gorilla's <c>Dialer.DialContext</c> equivalent), so this helper speaks just
/// enough HTTP/1.1 to complete the upgrade on a loopback
/// <see cref="TcpListener"/>, then wraps the socket with
/// <see cref="WebSocket.CreateFromStream"/> in server mode. Handlers run on
/// dedicated threads, exactly as each <c>httptest</c> request gets its own
/// goroutine; assertion failures are recorded and rethrown by
/// <see cref="AssertNoHandlerErrors"/>.
/// </remarks>
internal sealed class ResponsesWebSocketTestServer : IDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private const int MaxHandshakeHeaderBytes = 64 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _acceptThread;
    private readonly List<Thread> _handlerThreads = [];
    private readonly List<TestWebSocketConnection> _connections = [];
    private readonly List<Exception> _handlerErrors = [];
    private readonly object _mu = new();
    private int _upgrades;
    private bool _disposed;
    private bool _stopping;

    public ResponsesWebSocketTestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "responses-ws-test-accept",
        };
        _acceptThread.Start();
    }

    /// <summary>Handles one upgraded connection; the 1-based upgrade index is on the connection.</summary>
    public Action<TestWebSocketConnection> Handler { get; set; } = static _ => { };

    /// <summary>Extra headers the server adds to the 101 response.</summary>
    public Dictionary<string, string> ExtraHandshakeHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; }

    /// <summary>The HTTP base URL; the executor rewrites http to ws.</summary>
    public string Url => "http://127.0.0.1:" + Port;

    public int Upgrades => Volatile.Read(ref _upgrades);

    /// <summary>Rethrows the first handler assertion failure, if any.</summary>
    public void AssertNoHandlerErrors()
    {
        lock (_mu)
        {
            if (_handlerErrors.Count > 0)
            {
                ExceptionDispatchInfo.Capture(_handlerErrors[0]).Throw();
            }
        }
    }

    public void Dispose()
    {
        List<Thread> threads;
        List<TestWebSocketConnection> connections;
        lock (_mu)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping = true;
        }

        _cts.Cancel();
        _listener.Stop();
        _acceptThread.Join(TimeSpan.FromSeconds(10));

        lock (_mu)
        {
            threads = [.. _handlerThreads];
            connections = [.. _connections];
        }

        // Unblock handlers that are still waiting on a peer frame.
        foreach (var connection in connections)
        {
            connection.Abort();
        }

        foreach (var thread in threads)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var thread = new Thread(() => HandleClient(client))
            {
                IsBackground = true,
                Name = "responses-ws-test-handler",
            };
            lock (_mu)
            {
                _handlerThreads.Add(thread);
            }

            thread.Start();
        }
    }

    private void HandleClient(TcpClient client)
    {
        TestWebSocketConnection? connection = null;
        try
        {
            var stream = client.GetStream();
            var headers = ReadHandshakeRequest(stream);

            if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0)
            {
                WriteHttpError(stream, "400 Bad Request", "missing Sec-WebSocket-Key");
                return;
            }

            var response = new StringBuilder(256);
            response.Append("HTTP/1.1 101 Switching Protocols\r\n");
            response.Append("Upgrade: websocket\r\n");
            response.Append("Connection: Upgrade\r\n");
            response.Append("Sec-WebSocket-Accept: ").Append(ComputeAccept(key)).Append("\r\n");
            foreach (var (name, value) in ExtraHandshakeHeaders)
            {
                response.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            response.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(response.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();

            var webSocket = WebSocket.CreateFromStream(
                stream,
                new WebSocketCreationOptions
                {
                    IsServer = true,
                    KeepAliveInterval = TimeSpan.Zero,
                });

            var upgrade = Interlocked.Increment(ref _upgrades);
            connection = new TestWebSocketConnection(webSocket, client, upgrade, headers);
            lock (_mu)
            {
                _connections.Add(connection);
            }

            Handler(connection);
        }
        catch (Exception ex)
        {
            lock (_mu)
            {
                if (!_stopping)
                {
                    _handlerErrors.Add(ex);
                }
            }
        }
        finally
        {
            connection?.Abort();
            client.Dispose();
        }
    }

    private static Dictionary<string, string> ReadHandshakeRequest(Stream stream)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        var matched = 0;
        while (buffer.Length < MaxHandshakeHeaderBytes)
        {
            var read = stream.Read(one, 0, 1);
            if (read <= 0)
            {
                throw new InvalidOperationException("unexpected EOF during websocket handshake");
            }

            buffer.WriteByte(one[0]);
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
            throw new InvalidOperationException("websocket handshake headers too large");
        }

        var text = Encoding.Latin1.GetString(buffer.GetBuffer(), 0, (int)buffer.Length - 4);
        var lines = text.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var existing)
                ? existing + ", " + value
                : value;
        }

        return headers;
    }

    private static void WriteHttpError(Stream stream, string status, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 " + status + "\r\n"
            + "Content-Type: text/plain\r\n"
            + "Content-Length: " + body.Length + "\r\n"
            + "Connection: close\r\n"
            + "\r\n");
        stream.Write(head, 0, head.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static string ComputeAccept(string key)
    {
        var bytes = Encoding.ASCII.GetBytes(key + WebSocketGuid);
#pragma warning disable CA5350 // RFC 6455 mandates SHA-1 for the opening handshake key.
        var hash = SHA1.HashData(bytes);
#pragma warning restore CA5350
        return Convert.ToBase64String(hash);
    }
}

/// <summary>
/// One upgraded connection as seen by a test handler: gorilla's
/// <c>ReadJSON</c>/<c>WriteJSON</c>/<c>WriteMessage</c> surface and the
/// handshake request headers.
/// </summary>
internal sealed class TestWebSocketConnection : IDisposable
{
    private const int ReceiveBufferSize = 16 * 1024;

    private readonly WebSocket _webSocket;
    private readonly TcpClient _client;
    private readonly byte[] _buffer = new byte[ReceiveBufferSize];
    private int _disposed;

    public TestWebSocketConnection(
        WebSocket webSocket,
        TcpClient client,
        int upgradeIndex,
        IReadOnlyDictionary<string, string> headers)
    {
        _webSocket = webSocket;
        _client = client;
        UpgradeIndex = upgradeIndex;
        Headers = headers;
    }

    /// <summary>1-based upgrade counter, like the handler's atomic counter in the reference.</summary>
    public int UpgradeIndex { get; }

    /// <summary>The handshake request headers, case-insensitive (Go's <c>r.Header</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public void WriteJson(string json) => WriteRaw(json);

    public void WriteRaw(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public void WriteClose(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? description = "")
        => _webSocket.CloseOutputAsync(
            status,
            description ?? string.Empty,
            CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Reads one text message, rejecting close frames like gorilla's framing.</summary>
    public string ReadRawMessage()
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = _webSocket.ReceiveAsync(
                new ArraySegment<byte>(_buffer),
                CancellationToken.None).GetAwaiter().GetResult();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("websocket closed while reading a message");
            }

            message.Write(_buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public JsonElement ReadJson()
    {
        using var document = JsonDocument.Parse(ReadRawMessage());
        return document.RootElement.Clone();
    }

    /// <summary>Asserts that the client drops the connection, then returns.</summary>
    public void ExpectClosed()
    {
        try
        {
            _ = ReadRawMessage();
        }
        catch (Exception ex) when (ex is WebSocketException or IOException
            or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException("expected the peer to close the websocket");
    }

    /// <summary>Blocks until the client closes the connection (Go's <c>&lt;-r.Context().Done()</c>).</summary>
    public void WaitUntilClosed()
    {
        try
        {
            while (true)
            {
                var result = _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(_buffer),
                    CancellationToken.None).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException
            or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Aborted by server teardown or closed by the peer.
        }
    }

    public void Abort()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _webSocket.Dispose();
        }
        catch (Exception)
        {
            // Abort never throws.
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // Abort never throws.
        }
    }

    public void Dispose()
    {
        Abort();
        GC.SuppressFinalize(this);
    }
}
