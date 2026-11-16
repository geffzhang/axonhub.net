namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of Go's <c>errors.Is</c> over the <c>Unwrap</c> chain. Go sentinel
/// errors are modelled as shared <see cref="Exception"/> instances; matching is
/// by reference, exactly like Go's <c>err == target</c> comparison.
/// </summary>
public static class GoErrors
{
    /// <summary>Port of <c>errors.Is(err, target)</c>.</summary>
    public static bool Is(Exception? err, Exception? target)
    {
        if (target is null)
        {
            return err is null;
        }

        for (var current = err; current is not null; current = Unwrap(current))
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of repeated <c>errors.Is</c> calls against several sentinels.</summary>
    public static bool IsAny(Exception? err, params Exception[] targets)
    {
        foreach (var target in targets)
        {
            if (Is(err, target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of <c>fmt.Errorf("&lt;prefix&gt;%w", err)</c>: the message is the
    /// prefix plus the wrapped error's message, and the chain keeps unwrapping.
    /// </summary>
    public static Exception Wrap(Exception inner, string prefix)
        => new WrappedException(prefix + inner.Message, inner);

    /// <summary>
    /// Port of <c>errors.As(err, &amp;target)</c>: the first exception of
    /// <typeparamref name="T"/> in the unwrap chain.
    /// </summary>
    public static T? As<T>(Exception? err)
        where T : Exception
    {
        for (var current = err; current is not null; current = Unwrap(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Go's <c>ResponseError.Unwrap</c> exposes <c>Cause</c>; other errors
    /// unwrap through <see cref="Exception.InnerException"/>.
    /// </summary>
    private static Exception? Unwrap(Exception error)
        => error is ResponseError { CauseException: { } cause }
            ? cause
            : error.InnerException;
}

/// <summary>Carrier for <see cref="GoErrors.Wrap"/>: a message prefix over an inner error.</summary>
public sealed class WrappedException : Exception
{
    public WrappedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Port of pipeline.UpstreamError (llm/pipeline/upstream_error.go): marks errors
/// that originate from the upstream provider path. The message is the wrapped
/// error's message; an empty wrap reports <c>"upstream error"</c>.
/// </summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(Exception? inner)
        : base(inner is null ? "upstream error" : inner.Message, inner)
    {
    }
}

public static class UpstreamErrors
{
    /// <summary>Port of <c>WrapUpstreamError</c>: idempotent.</summary>
    public static Exception? Wrap(Exception? err)
    {
        if (err is null)
        {
            return null;
        }

        return GoErrors.As<UpstreamException>(err) is not null ? err : new UpstreamException(err);
    }

    /// <summary>Port of <c>IsUpstreamError</c>.</summary>
    public static bool Is(Exception? err) => GoErrors.As<UpstreamException>(err) is not null;
}
