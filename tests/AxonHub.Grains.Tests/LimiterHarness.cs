using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Threading helpers shared by the limiter and RateLimitAware tests. The port's
/// <see cref="ChannelLimiter.Acquire"/> blocks its caller (difference C042), so
/// waiters are started on dedicated threads — the closest stand-in for the
/// reference's blocked goroutines — and queue state is polled instead of slept
/// on.
/// </summary>
internal static class LimiterHarness
{
    /// <summary>Runs a blocking acquire on a dedicated thread.</summary>
    public static Task RunBlocking(Action action)
        => Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Fires <paramref name="count"/> background acquires.</summary>
    public static Task[] LaunchAcquire(ChannelLimiter limiter, CancellationToken cancellationToken, int count)
    {
        ArgumentNullException.ThrowIfNull(limiter);

        var tasks = new Task[count];
        for (var index = 0; index < count; index++)
        {
            tasks[index] = RunBlocking(() => limiter.Acquire(cancellationToken));
        }

        return tasks;
    }

    /// <summary>Releases any slot granted while the waiters were being unblocked.</summary>
    public static async Task DrainGrantsAsync(ChannelLimiter limiter, Task[] tasks)
    {
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(tasks);

        foreach (var task in tasks)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(5));
                limiter.Release();
            }
            catch (Exception ex) when (ex is OperationCanceledException or ChannelQueueException)
            {
                // Expected: the waiter observed the cancellation or a queue error.
            }
        }
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or five seconds pass.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(5);

        while (TimeProvider.System.GetUtcNow() < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail(because);
    }
}
