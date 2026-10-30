using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Port of internal/server/orchestrator/channel_limiter_test.go: the per-channel
/// concurrency limiter's admission, queueing, timeout, cancellation and FIFO
/// fairness rules. Waiters run on dedicated threads because
/// <see cref="ChannelLimiter.Acquire"/> blocks its caller, mirroring the
/// reference's blocked goroutines (difference C042).
/// </summary>
public sealed class ChannelLimiterTests
{
    [Fact]
    public async Task NoQueueBlocksBeyondCapacity()
    {
        var limiter = new ChannelLimiter(2, 0, 0);

        // Fill capacity — these return immediately.
        limiter.Acquire(CancellationToken.None);
        limiter.Acquire(CancellationToken.None);

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(2, inFlight);
        Assert.Equal(0, waiting);

        using var cancel = new CancellationTokenSource();
        var third = LimiterHarness.RunBlocking(() => limiter.Acquire(cancel.Token));

        await LimiterHarness.WaitUntilAsync(
            () => limiter.Stats().Waiting == 1,
            "third acquire must queue, not be admitted");

        try
        {
            await Task.Delay(50);
            Assert.False(
                third.IsCompleted,
                "third Acquire returned while at capacity; MaxConcurrent must hard-block without a queue");

            // Free a slot -> the waiter is admitted (slot transferred, in-flight stays 2).
            limiter.Release();
            await third.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, limiter.Stats().InFlight);

            // Drain.
            limiter.Release();
            limiter.Release();

            (inFlight, waiting) = limiter.Stats();
            Assert.Equal(0, inFlight);
            Assert.Equal(0, waiting);
        }
        finally
        {
            cancel.Cancel();
        }
    }

    [Fact]
    public void HardModeAcquireUpToCapacity()
    {
        var limiter = new ChannelLimiter(3, 5, 0);

        for (var index = 0; index < 3; index++)
        {
            limiter.Acquire(CancellationToken.None);
        }

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(3, inFlight);
        Assert.Equal(0, waiting);
    }

    [Fact]
    public async Task HardModeQueueFull()
    {
        var limiter = new ChannelLimiter(1, 2, 0);

        // 1 in-flight + 2 queued = at the capacity ceiling.
        limiter.Acquire(CancellationToken.None);
        using var cancelWait = new CancellationTokenSource();

        var enqueued = LimiterHarness.LaunchAcquire(limiter, cancelWait.Token, 2);

        await LimiterHarness.WaitUntilAsync(
            () => limiter.Stats().Waiting == 2,
            "both waiters should enqueue");

        try
        {
            // The next Acquire must hit the queue-full path immediately.
            var error = Assert.Throws<ChannelQueueException>(
                () => limiter.Acquire(CancellationToken.None));
            Assert.Equal(ChannelQueueReason.QueueFull, error.Reason);
        }
        finally
        {
            cancelWait.Cancel();
            await LimiterHarness.DrainGrantsAsync(limiter, enqueued);
            limiter.Release();
        }
    }

    [Fact]
    public void HardModeQueueTimeout()
    {
        var limiter = new ChannelLimiter(1, 5, 50);

        limiter.Acquire(CancellationToken.None);

        var started = TimeProvider.System.GetTimestamp();
        var error = Assert.Throws<ChannelQueueException>(
            () => limiter.Acquire(CancellationToken.None));
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.Equal(ChannelQueueReason.QueueTimeout, error.Reason);
        Assert.True(
            elapsed >= TimeSpan.FromMilliseconds(45),
            $"queue wait returned after {elapsed.TotalMilliseconds}ms; the 50ms timeout did not elapse");

        // The slot must not have leaked.
        limiter.Release();

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);
    }

    [Fact]
    public async Task NoTimeoutHonoursCancellation()
    {
        var limiter = new ChannelLimiter(1, 5, 0);
        using var cancel = new CancellationTokenSource();

        limiter.Acquire(cancel.Token);

        var waiter = LimiterHarness.RunBlocking(() => limiter.Acquire(cancel.Token));

        await LimiterHarness.WaitUntilAsync(
            () => limiter.Stats().Waiting == 1,
            "second acquire must queue");

        cancel.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiter.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsNotType<ChannelQueueException>(error);

        limiter.Release();

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);
    }

    [Fact]
    public async Task HardModeDrainsInFifoOrder()
    {
        const int Waiters = 20;

        var limiter = new ChannelLimiter(1, Waiters, 0);
        limiter.Acquire(CancellationToken.None);

        var order = new List<int>(Waiters);
        var orderLock = new object();
        var tasks = new Task[Waiters];

        // Launch waiters one at a time, blocking until each is enqueued, so the
        // FIFO order is deterministic regardless of thread scheduling.
        for (var index = 0; index < Waiters; index++)
        {
            var waiterIndex = index;
            tasks[index] = LimiterHarness.RunBlocking(() =>
            {
                limiter.Acquire(CancellationToken.None);

                lock (orderLock)
                {
                    order.Add(waiterIndex);
                }

                limiter.Release();
            });

            await LimiterHarness.WaitUntilAsync(
                () => limiter.Stats().Waiting == waiterIndex + 1,
                $"waiter {waiterIndex} should enqueue");
        }

        // Release the initial slot — the FIFO chain drains in insertion order.
        limiter.Release();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Enumerable.Range(0, Waiters), order);
    }

    [Fact]
    public async Task NoSlotLeakOnTimeout()
    {
        const int Capacity = 2;
        const int QueueSize = 10;
        const int Waiters = 30;

        var limiter = new ChannelLimiter(Capacity, QueueSize, 30);

        // Saturate capacity so subsequent Acquire calls must queue.
        for (var index = 0; index < Capacity; index++)
        {
            limiter.Acquire(CancellationToken.None);
        }

        var timeouts = 0;
        var queueFulls = 0;
        var acquired = 0;
        var ctxCancels = 0;
        var other = 0;

        var tasks = new Task[Waiters];
        for (var index = 0; index < Waiters; index++)
        {
            tasks[index] = LimiterHarness.RunBlocking(() =>
            {
                try
                {
                    limiter.Acquire(CancellationToken.None);
                    Interlocked.Increment(ref acquired);
                    limiter.Release();
                }
                catch (ChannelQueueException ex) when (ex.Reason == ChannelQueueReason.QueueTimeout)
                {
                    Interlocked.Increment(ref timeouts);
                }
                catch (ChannelQueueException ex) when (ex.Reason == ChannelQueueReason.QueueFull)
                {
                    Interlocked.Increment(ref queueFulls);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref ctxCancels);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref other);
                }
            });
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        // Drain the originally-held slots.
        for (var index = 0; index < Capacity; index++)
        {
            limiter.Release();
        }

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);
        Assert.Equal(0, ctxCancels);
        Assert.Equal(0, other);
    }

    [Fact]
    public void ReleaseOnEmptyIsNoop()
    {
        var limiter = new ChannelLimiter(2, 2, 0);

        // Release without a prior Acquire must not throw or go negative.
        limiter.Release();
        limiter.Release();

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);
    }

    [Fact]
    public async Task HardModeReleaseTransfersToWaiter()
    {
        var limiter = new ChannelLimiter(1, 2, 0);

        limiter.Acquire(CancellationToken.None);

        // One waiter queued.
        var granted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = LimiterHarness.RunBlocking(() =>
        {
            limiter.Acquire(CancellationToken.None);
            granted.SetResult();
        });

        await LimiterHarness.WaitUntilAsync(
            () => limiter.Stats().Waiting == 1,
            "waiter should queue");

        limiter.Release();
        await granted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await waiter;

        // inFlight should still be 1 — the slot was transferred, not freed.
        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(1, inFlight);
        Assert.Equal(0, waiting);

        limiter.Release();
        Assert.Equal(0, limiter.Stats().InFlight);
    }

    [Fact]
    public void HardModeAlreadyCancelledToken()
    {
        var limiter = new ChannelLimiter(1, 5, 0);
        using var cancel = new CancellationTokenSource();

        // Saturate capacity so the next Acquire must queue.
        limiter.Acquire(cancel.Token);

        cancel.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => limiter.Acquire(cancel.Token));

        limiter.Release();

        var (inFlight, waiting) = limiter.Stats();
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);
     }
}
