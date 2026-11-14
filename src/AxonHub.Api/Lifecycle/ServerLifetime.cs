namespace AxonHub.Api.Lifecycle;

/// <summary>
/// Port of internal/server/lifecycle.Manager: cancellation that is independent
/// from the HTTP connection lifetime. The server attaches the manager to every
/// request context and cancels it before HTTP shutdown starts, so an
/// acknowledged stream still stops its execution when the host goes away.
/// </summary>
public sealed class ServerLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _source = new();
    private Exception? _cause;
    private bool _shutdown;

    /// <summary>The cancellation token derived from the lifetime (Go: Context()).</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>
    /// Go: <c>context.Cause(manager.Context())</c>; null until <see cref="Shutdown"/>.
    /// </summary>
    public Exception? Cause
    {
        get
        {
            lock (_sync)
            {
                return _cause;
            }
        }
    }

    /// <summary>
    /// Port of Manager.Shutdown. The first call wins; a null cause becomes the
    /// reference's <c>errors.New("server shutdown")</c>.
    /// </summary>
    public void Shutdown(Exception? cause = null)
    {
        lock (_sync)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _cause = cause ?? new InvalidOperationException("server shutdown");
        }

        _source.Cancel();
    }

    public void Dispose() => _source.Dispose();
}
