namespace AxonHub.Llm.Http;

/// <summary>Port of the sentinel errors in llm/httpclient/decoder.go.</summary>
public static class StreamLimits
{
    /// <summary>Go: <c>httpclient.ErrStreamEventTooLarge</c>.</summary>
    public static readonly Exception EventTooLarge = new StreamEventTooLargeException();

    /// <summary>Go: <c>defaultMaxSSEEventBytes</c>.</summary>
    public const int DefaultMaxSseEventBytes = 32 * 1024 * 1024;
}

/// <summary>Carrier type for <see cref="StreamLimits.EventTooLarge"/>.</summary>
public sealed class StreamEventTooLargeException : Exception
{
    public StreamEventTooLargeException()
        : base("stream event exceeds byte limit")
    {
    }

    /// <summary>
    /// Go wraps the sentinel with <c>fmt.Errorf("%w: limit %d bytes", ...)</c>;
    /// the wrapper carries the same message and keeps the sentinel reachable for
    /// <c>errors.Is</c> / <see cref="GoErrors"/>.
    /// </summary>
    public StreamEventTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
