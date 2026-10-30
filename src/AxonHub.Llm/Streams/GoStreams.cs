using AxonHub.Llm.Http;

namespace AxonHub.Llm.Streams;

/// <summary>
/// Port of streams.Stream (llm/streams): pull-based, single-reader, errors are
/// data (<see cref="Err"/>) rather than exceptions.
/// </summary>
public interface IStream<out T> : IDisposable
    where T : class
{
    /// <summary>Advances to the next item. False at end of stream or on error.</summary>
    bool Next();

    /// <summary>The current item; null before the first successful <see cref="Next"/>.</summary>
    T? Current { get; }

    /// <summary>Terminal error, or null for a clean end.</summary>
    Exception? Err { get; }
}

/// <summary>Port of streams.MapErr + streams.NoNil.</summary>
public sealed class MapStream<TIn, TOut> : IStream<TOut>
    where TIn : class
    where TOut : class
{
    private readonly IStream<TIn> _source;
    private readonly Func<TIn, TOut?> _map;
    private Exception? _err;

    public MapStream(IStream<TIn> source, Func<TIn, TOut?> map)
    {
        _source = source;
        _map = map;
    }

    public TOut? Current { get; private set; }

    public Exception? Err => _err ?? _source.Err;

    public bool Next()
    {
        while (true)
        {
            if (_err is not null)
            {
                return false;
            }

            if (!_source.Next())
            {
                return false;
            }

            if (_source.Current is not { } item)
            {
                continue;
            }

            TOut? mapped;
            try
            {
                mapped = _map(item);
            }
            catch (Exception ex)
            {
                // MapErr turns a mapping failure into the stream's terminal error.
                _err = ex;
                return false;
            }

            if (mapped is null)
            {
                continue;
            }

            Current = mapped;
            return true;
        }
    }

    public void Dispose() => _source.Dispose();
}

/// <summary>Port of streams.SliceStream: a fixed sequence of items.</summary>
public sealed class SliceStream<T>(IEnumerable<T> items) : IStream<T>
    where T : class
{
    private readonly List<T> _items = [.. items];
    private int _index;

    public T? Current { get; private set; }

    public Exception? Err => null;

    public bool Next()
    {
        if (_index >= _items.Count)
        {
            return false;
        }

        Current = _items[_index];
        _index++;
        return true;
    }

    public void Dispose()
    {
        // Nothing to release.
    }
}

/// <summary>
/// Port of streams.PrependStream: replays buffered items, then continues with
/// the source stream. Errors and disposal belong to the source.
/// </summary>
public sealed class PrependStream<T>(IStream<T> stream, params T[] prependItems) : IStream<T>
    where T : class
{
    private int _prependIndex;

    public T? Current { get; private set; }

    public Exception? Err => stream.Err;

    public bool Next()
    {
        if (_prependIndex < prependItems.Length)
        {
            Current = prependItems[_prependIndex];
            _prependIndex++;
            return true;
        }

        if (stream.Next())
        {
            Current = stream.Current;
            return true;
        }

        return false;
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>Port of streams.Filter: yields only items matching the predicate.</summary>
public sealed class FilterStream<T>(IStream<T> stream, Func<T, bool> predicate) : IStream<T>
    where T : class
{
    public T? Current { get; private set; }

    public Exception? Err => stream.Err;

    public bool Next()
    {
        while (stream.Next())
        {
            if (stream.Current is { } item && predicate(item))
            {
                Current = item;
                return true;
            }
        }

        return false;
    }

    public void Dispose() => stream.Dispose();
}

public static class GoStreams
{
    /// <summary>Port of <c>streams.All</c>: drain the stream, surfacing its terminal error.</summary>
    public static List<T> All<T>(IStream<T> stream, out Exception? error)
        where T : class
    {
        var result = new List<T>();
        while (stream.Next())
        {
            if (stream.Current is { } item)
            {
                result.Add(item);
            }
        }

        error = stream.Err;
        return result;
    }

    /// <summary>Port of <c>streams.NoNil</c>.</summary>
    public static IStream<T> NoNil<T>(IStream<T> stream)
        where T : class
        => new FilterStream<T>(stream, static item => item is not null);
}

/// <summary>Port of the SSE-backed decoder in llm/httpclient/decoder.go.</summary>
public sealed class SseEventStream : IStream<StreamEvent>
{
    private readonly SseDecoder _decoder;

    public SseEventStream(string text, int maxEventBytes = 0)
    {
        _decoder = new SseDecoder(text, maxEventBytes);
    }

    public StreamEvent? Current => _decoder.Current;

    public Exception? Err => _decoder.Err;

    public bool Next() => _decoder.Next();

    public void Dispose() => _decoder.Dispose();
}
