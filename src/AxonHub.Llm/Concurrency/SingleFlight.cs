namespace AxonHub.Llm.Concurrency;

/// <summary>
/// Port of golang.org/x/sync/singleflight.Group: concurrent callers of
/// <see cref="Do"/> with the same key share a single in-flight execution, and a
/// key is reusable as soon as that execution finishes.
/// </summary>
/// <remarks>
/// The reference runs the function on the first caller's goroutine; here the
/// first caller runs it inline as well, while the others block on the shared
/// completion. Exceptions are rethrown unchanged to every caller, matching the
/// reference's error return; a panic would propagate to all callers too.
/// </remarks>
public sealed class SingleFlight<T>
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TaskCompletionSource<T>> _calls = new(StringComparer.Ordinal);

    /// <summary>Runs <paramref name="fn"/> once per key for concurrent callers.</summary>
    public T Do(string key, Func<T> fn)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fn);

        TaskCompletionSource<T> call;
        var owner = false;

        lock (_sync)
        {
            if (!_calls.TryGetValue(key, out call!))
            {
                call = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                _calls[key] = call;
                owner = true;
            }
        }

        if (!owner)
        {
            // GetResult rethrows the shared failure of the owning call.
            return call.Task.GetAwaiter().GetResult();
        }

        try
        {
            var result = fn();

            // Remove before completing, like the reference: a caller arriving
            // after this point starts a new call instead of joining this one.
            lock (_sync)
            {
                _calls.Remove(key);
            }

            call.SetResult(result);

            return result;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _calls.Remove(key);
            }

            call.SetException(ex);
            throw;
        }
    }
}
