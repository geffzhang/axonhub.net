namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of <c>withNonStreamTimeout</c> / <c>isNonStreamTimeout</c>
/// (llm/pipeline/pipeline.go:455-465). Go attaches
/// <c>ErrNonStreamResponseTimeout</c> as the context cause; the flag here plays
/// the same role and stays readable after the scope is disposed, because the
/// caller checks it only after cancelling.
/// </summary>
public sealed class NonStreamTimeoutScope : IDisposable
{
    private readonly CancellationTokenSource? _cts;
    private readonly Timer? _timer;
    private int _timedOut;
    private bool _disposed;

    private NonStreamTimeoutScope(CancellationToken token)
    {
        Token = token;
    }

    private NonStreamTimeoutScope(CancellationTokenSource cts, Timer timer, CancellationToken token)
    {
        _cts = cts;
        _timer = timer;
        Token = token;
    }

    /// <summary>Token handed to the executor.</summary>
    public CancellationToken Token { get; }

    /// <summary>True when the deadline fired (Go: the context cause was the timeout).</summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) == 1;

    public static NonStreamTimeoutScope Create(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return new NonStreamTimeoutScope(cancellationToken);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        NonStreamTimeoutScope? scope = null;
        var timer = new Timer(
            _ =>
            {
                if (scope is null)
                {
                    return;
                }

                Interlocked.Exchange(ref scope._timedOut, 1);

                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The exchange finished first; nothing left to cancel.
                }
            },
            null,
            timeout,
            Timeout.InfiniteTimeSpan);

        scope = new NonStreamTimeoutScope(cts, timer, cts.Token);
        return scope;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _cts?.Dispose();
    }
}
