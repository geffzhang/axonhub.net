using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// The request-scoped view of the deadline <see cref="RequestTimeoutMiddleware"/>
/// installed, equivalent to reading <c>ctx.Err()</c> on the Go request context.
/// <see cref="IsTimedOut"/> tells a server-side deadline apart from the parent
/// request aborting, which .NET represents with the same exception otherwise.
/// </summary>
public interface IRequestTimeoutFeature
{
    /// <summary>The token downstream middleware and handlers should observe.</summary>
    CancellationToken Token { get; }

    /// <summary>
    /// True when the deadline fired before the parent token was canceled
    /// (Go: <c>ctx.Err() == context.DeadlineExceeded</c>).
    /// </summary>
    bool IsTimedOut { get; }
}

/// <summary>
/// Port of middleware.WithTimeout's <c>context.WithTimeout</c> state machine
/// (internal/server/middleware/timeout.go:10-18): the first of the deadline and
/// the parent cancellation wins, exactly like Go's cancelCtx error recording.
/// </summary>
public sealed class RequestTimeoutFeature : IRequestTimeoutFeature, IDisposable
{
    private const int Active = 0;
    private const int TimedOut = 1;
    private const int ParentCanceled = 2;

    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenRegistration _parentRegistration;
    private readonly ITimer? _timer;
    private int _state;

    public RequestTimeoutFeature(
        TimeSpan timeout,
        CancellationToken parent,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        Token = _cts.Token;

        if (timeout <= TimeSpan.Zero)
        {
            // Go: WithTimeout with dur <= 0 cancels immediately with
            // DeadlineExceeded.
            Transition(TimedOut);
            return;
        }

        if (parent.CanBeCanceled)
        {
            if (parent.IsCancellationRequested)
            {
                Transition(ParentCanceled);
                return;
            }

            _parentRegistration = parent.Register(
                static state => ((RequestTimeoutFeature)state!).Transition(ParentCanceled),
                this);
        }

        _timer = timeProvider.CreateTimer(
            static state => ((RequestTimeoutFeature)state!).Transition(TimedOut),
            this,
            timeout,
            Timeout.InfiniteTimeSpan);
    }

    public CancellationToken Token { get; }

    public bool IsTimedOut => Volatile.Read(ref _state) == TimedOut;

    /// <summary>Port of the deferred cancel: the request token ends canceled
    /// when the handler returns, but a fired deadline keeps its state.</summary>
    public void Cancel() => Transition(ParentCanceled);

    private void Transition(int state)
    {
        if (Interlocked.CompareExchange(ref _state, state, Active) != Active)
        {
            // The first terminal cause wins; later timers/cancellations are
            // no-ops (Go records the first error on the cancelCtx).
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A queued timer callback raced the middleware's cleanup.
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _parentRegistration.Dispose();
        _cts.Dispose();
    }
}

/// <summary>
/// Port of middleware.WithTimeout (internal/server/middleware/timeout.go):
/// replaces the request context with a deadline context, runs the rest of the
/// chain, then cancels it. The middleware never writes the response itself; the
/// SSE writer and the persistence middleware classify the deadline.
/// </summary>
public sealed class RequestTimeoutMiddleware(
    RequestDelegate next,
    TimeSpan timeout,
    TimeProvider timeProvider)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var original = context.RequestAborted;
        using var feature = new RequestTimeoutFeature(timeout, original, timeProvider);

        context.Features.Set<IRequestTimeoutFeature>(feature);
        context.RequestAborted = feature.Token;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            // Go's deferred cancel runs when the gin handler chain returns.
            feature.Cancel();
            context.RequestAborted = original;
            context.Features.Set<IRequestTimeoutFeature>(null);
        }
    }
}
